#!/usr/bin/env node
// Colony Code replay: posts a scripted scenario to a running bridge (the game or tools/fake-bridge.mjs)
// with realistic timing, so the RimWorld mod can be driven without spending Claude usage.
// Zero dependencies, Node 24+. Follows the bridge protocol (v0).
//
// Usage:
//   node tools/replay.mjs [scenario.json] [--speed X] [--loop] [--port N --token HEX] [--home DIR]
//                         [--session ID] [--decision-timeout S] [--inbox] [--quiet]
//
// Scenario format (see tools/scenarios/basic.json):
//   { "name", "vars": { k: "string with {{templates}}" }, "steps": [ step ] }
//   step: { "label"?, "delay"? (ms before the step), "events"?: [event],
//           "await"?: "<decision id>"   wait (polling GET /decision) until the player answers,
//           "if"?: "allowed" | "always" | "denied"   run only after the last awaited decision had that
//                                       outcome ("allowed" covers "Allow once" and "Always allow";
//                                       "always" only "Always allow"; "denied" anything else),
//           "once"?: true               only on the first loop iteration,
//           "final"?: true              only once, when replay finishes or is interrupted }
//   "respond"?: { "<inbox type>": [event] }   with --inbox, answer each inbox item of that type with
//              these events (diff.request -> diff.result, merge.request -> merge.result, ...).
//              Extra templates there: {{requestId}}, {{worktree}}, {{action}}, {{text}}, {{itemId}}.
//   Templates: {{session}}, {{lead}}, {{run}}, {{answerOption}}, {{answerText}} and every key of "vars".
//   {{answerOption}} of a multiSelect question is the picked labels joined with ", ".
//   Missing "ts" and "sessionId" are filled in; log entries get "ts" too.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const DEFAULT_SCENARIO = path.join(HERE, 'scenarios', 'basic.json');

const useColor = process.stdout.isTTY && !process.env.NO_COLOR;
const c = (code) => (s) => (useColor ? `\x1b[${code}m${s}\x1b[0m` : String(s));
const col = { dim: c('2'), bold: c('1'), red: c('31'), green: c('32'), yellow: c('33'), cyan: c('36'), gray: c('90') };

export function readBridgeInfo(home) {
  const dir = home || process.env.COLONY_HOME || path.join(os.homedir(), '.colony-code');
  const file = path.join(dir, 'bridge.json');
  let info;
  try { info = JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) {
    throw new Error(`cannot read ${file} (${e.code || e.message}). Is the game or fake-bridge running? Or pass --port and --token.`);
  }
  return info;
}

export class BridgeClient {
  constructor({ port, token, host = '127.0.0.1' }) {
    this.base = `http://${host}:${port}`;
    this.token = token;
  }

  async request(method, p, body, { signal } = {}) {
    const res = await fetch(this.base + p, {
      method,
      headers: { 'X-Colony-Token': this.token, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal,
    });
    const text = await res.text();
    let json;
    try { json = text ? JSON.parse(text) : undefined; } catch { json = undefined; }
    if (!res.ok) {
      const err = new Error(`${method} ${p} -> ${res.status}${json && json.error ? ' ' + json.error : ''}`);
      err.status = res.status;
      throw err;
    }
    return json;
  }

  health(opts) { return this.request('GET', '/health', undefined, opts); }
  postEvents(events, opts) { return this.request('POST', '/events', events, opts); }
  inbox(waitS = 25, opts = {}) {
    const q = opts.session ? `&session=${encodeURIComponent(opts.session)}` : '';
    return this.request('GET', `/inbox?wait=${waitS}${q}`, undefined, opts);
  }
  decision(id, waitS = 25, opts) { return this.request('GET', `/decision/${encodeURIComponent(id)}?wait=${waitS}`, undefined, opts); }
}

// ---- templating ----------------------------------------------------------------

function fillTemplate(str, vars) {
  return str.replace(/\{\{(\w+)\}\}/g, (m, k) => (Object.prototype.hasOwnProperty.call(vars, k) ? String(vars[k]) : m));
}

function deepFill(v, vars) {
  if (typeof v === 'string') return fillTemplate(v, vars);
  if (Array.isArray(v)) return v.map((x) => deepFill(x, vars));
  if (v && typeof v === 'object') {
    const o = {};
    for (const [k, x] of Object.entries(v)) o[k] = deepFill(x, vars);
    return o;
  }
  return v;
}

function resolveVars(scenarioVars, builtins) {
  const vars = { ...builtins };
  const raw = scenarioVars || {};
  // Scenario vars may reference builtins and each other; a few passes settle the chains.
  for (const [k, v] of Object.entries(raw)) vars[k] = v;
  for (let pass = 0; pass < 5; pass++) {
    for (const k of Object.keys(raw)) if (typeof vars[k] === 'string') vars[k] = fillTemplate(vars[k], vars);
  }
  return vars;
}

function stampEvent(ev, sessionId) {
  const now = Date.now();
  const out = { ...ev };
  if (out.ts === undefined) out.ts = now;
  if (out.sessionId === undefined) out.sessionId = sessionId;
  if (out.type === 'agent.log' && Array.isArray(out.entries)) {
    out.entries = out.entries.map((e) => (e.ts === undefined ? { ...e, ts: now } : e));
  }
  return out;
}

// ---- runner ----------------------------------------------------------------------

function sleep(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(signal.reason || new Error('aborted'));
    const t = setTimeout(() => { signal?.removeEventListener('abort', onAbort); resolve(); }, ms);
    const onAbort = () => { clearTimeout(t); reject(signal.reason || new Error('aborted')); };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

/**
 * Wait for the player to answer decision `id`. Polls GET /decision/<id>?wait=25 until answered or timeout.
 * Returns { status: 'answered', option, text? } or { status: 'timeout' | 'unknown' }.
 */
export async function awaitDecision(client, id, { timeoutS = 300, pollWaitS = 25, signal } = {}) {
  const deadline = timeoutS > 0 ? Date.now() + timeoutS * 1000 : Infinity;
  for (;;) {
    const left = (deadline - Date.now()) / 1000;
    if (left <= 0) return { status: 'timeout' };
    const wait = Math.max(0, Math.min(pollWaitS, Math.ceil(left)));
    let r;
    try {
      r = await client.decision(id, wait, { signal });
    } catch (e) {
      if (signal?.aborted) throw e;
      // Bridge hiccup or game restarting: back off briefly and retry until the deadline.
      await sleep(1000, signal);
      continue;
    }
    if (r && r.status === 'answered') return r;
    if (r && r.status === 'unknown') return { status: 'unknown' };
  }
}

/**
 * Run a scenario against a bridge.
 * opts: { client, scenario, speed=1, loop=false, sessionId, decisionTimeoutS=300, signal, log=console.log }
 * Returns { runs, posted, decisions: [{ id, option, text?, status }] }.
 */
export async function runScenario(opts) {
  const { client, scenario, speed = 1, loop = false, decisionTimeoutS = 300, signal } = opts;
  const log = opts.log || console.log;
  const sessionId = opts.sessionId || crypto.randomUUID();
  const lead = 'lead-' + sessionId.slice(0, 8);
  const steps = scenario.steps || [];
  const stats = { runs: 0, posted: 0, decisions: [], sessionId };
  const scale = (ms) => Math.max(0, Math.round((ms || 0) / (speed > 0 ? speed : 1)));

  const post = async (events, vars) => {
    if (!events || !events.length) return;
    const filled = deepFill(events, vars).map((e) => stampEvent(e, sessionId));
    const r = await client.postEvents(filled, { signal });
    stats.posted += filled.length;
    return r;
  };

  let finalVars = resolveVars(scenario.vars, { session: sessionId, lead, run: 1, answerOption: '', answerText: '' });
  try {
    for (let run = 1; ; run++) {
      stats.runs = run;
      let last = null; // last awaited decision outcome
      let vars = resolveVars(scenario.vars, { session: sessionId, lead, run, answerOption: '', answerText: '' });
      finalVars = vars;
      if (loop) log(col.bold(`-- run ${run} --`));
      for (const step of steps) {
        if (step.final) continue;
        if (step.once && run > 1) continue;
        if (step.if) {
          const always = !!last && last.option === 'Always allow';
          const allowed = !!last && (last.option === 'Allow once' || always);
          if (step.if === 'allowed' && !allowed) continue;
          if (step.if === 'always' && !always) continue;
          if (step.if === 'denied' && allowed) continue;
        }
        if (step.delay) await sleep(scale(step.delay), signal);
        if (step.label) log(col.cyan('> ') + step.label);
        if (step.events) await post(step.events, vars);
        if (step.await) {
          const id = fillTemplate(step.await, vars);
          log(col.yellow(`  waiting for an answer to ${id} (answer in game, or "allow ${id}" in fake-bridge)`));
          const r = await awaitDecision(client, id, { timeoutS: decisionTimeoutS, signal });
          if (r.status === 'answered') {
            last = { id, option: r.option, text: r.text, status: 'answered' };
            log(col.green(`  answered: ${String(r.option).split('\n').join(', ')}${r.text ? ' (' + r.text + ')' : ''}`));
          } else if (id.startsWith('ask-')) {
            // A question with no answer from the game: the plugin would show the terminal dialog.
            last = { id, option: '', text: 'answered in the terminal', status: r.status };
            log(col.yellow(`  ${r.status === 'unknown' ? 'bridge does not know ' + id : 'no answer in time'}; the question goes to the "terminal"`));
            await post([{ type: 'decision.closed', id }], vars);
          } else {
            // Like the plugin: no answer from the game -> the terminal prompt would appear. Simulate the
            // player allowing it there and tell the game the question is closed.
            // The scenario continues down the "allowed" branch, but the real terminal answer is not
            // known to the plugin, so decision.closed carries no option (as the plugin sends it).
            last = { id, option: 'Allow once', status: r.status };
            log(col.yellow(`  ${r.status === 'unknown' ? 'bridge does not know ' + id : 'no answer in time'}; assuming "Allow once" in the "terminal"`));
            await post([{ type: 'decision.closed', id }], vars);
          }
          stats.decisions.push(last);
          const picked = String(last.option || '').split('\n').filter(Boolean).join(', ');
          vars = { ...vars, answerOption: picked, answerText: last.text || 'no reason given' };
          finalVars = vars;
        }
      }
      if (!loop) break;
      await sleep(scale(3000), signal);
    }
  } finally {
    const finals = steps.filter((s) => s.final);
    if (finals.length) {
      for (const step of finals) {
        if (step.label) log(col.cyan('> ') + step.label);
        try {
          const filled = deepFill(step.events || [], finalVars).map((e) => stampEvent(e, sessionId));
          if (filled.length) { await client.postEvents(filled); stats.posted += filled.length; }
        } catch (e) { log(col.red(`  could not post final events: ${e.message}`)); }
      }
    }
  }
  return stats;
}

/**
 * Long-polls the inbox as session `sessionId` and answers items with the scenario's "respond"
 * events (templated with the item's fields). Runs until `signal` aborts. Returns the items seen.
 */
export async function serveInbox({ client, scenario, sessionId, signal, log = console.log, waitS = 25 }) {
  const respond = (scenario && scenario.respond) || {};
  const seen = [];
  const lead = 'lead-' + sessionId.slice(0, 8);
  while (!signal?.aborted) {
    let items;
    try {
      items = await client.inbox(waitS, { signal, session: sessionId });
    } catch (e) {
      if (signal?.aborted) break;
      await sleep(1000, signal).catch(() => {});
      continue;
    }
    for (const it of items || []) {
      seen.push(it);
      log(col.green(`inbox <- ${it.type} ${JSON.stringify(it)}`));
      const events = respond[it.type];
      if (!Array.isArray(events) || !events.length) continue;
      const vars = resolveVars(scenario.vars, {
        session: sessionId, lead, run: 1, answerOption: '', answerText: '',
        requestId: it.requestId || it.id, worktree: it.worktree || '', action: it.action || '', text: it.text || '', itemId: it.id,
      });
      const filled = deepFill(events, vars).map((e) => stampEvent(e, sessionId));
      // a diff.request without a worktree gets a diff.result without one
      for (const e of filled) if (e.type === 'diff.result' && !it.worktree) delete e.worktree;
      try {
        await client.postEvents(filled, { signal });
        log(col.cyan(`  answered ${it.type} with ${filled.map((e) => e.type).join(', ')}`));
      } catch (e) {
        if (signal?.aborted) break;
        log(col.red(`  could not answer ${it.type}: ${e.message}`));
      }
    }
  }
  return seen;
}

// ---- CLI ---------------------------------------------------------------------------

function parseArgs(argv) {
  const o = { speed: 1, loop: false, decisionTimeoutS: 300 };
  const take = (a, i) => (a.includes('=') ? [a.slice(a.indexOf('=') + 1), i] : [argv[i + 1], i + 1]);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const key = a.split('=')[0];
    let v;
    switch (key) {
      case '--speed': [v, i] = take(a, i); o.speed = Number(v); break;
      case '--loop': o.loop = true; break;
      case '--port': [v, i] = take(a, i); o.port = Number(v); break;
      case '--token': [v, i] = take(a, i); o.token = v; break;
      case '--home': [v, i] = take(a, i); o.home = v; break;
      case '--session': [v, i] = take(a, i); o.sessionId = v; break;
      case '--decision-timeout': [v, i] = take(a, i); o.decisionTimeoutS = Number(v); break;
      case '--inbox': o.inbox = true; break;
      case '--quiet': case '-q': o.quiet = true; break;
      case '--help': case '-h': o.help = true; break;
      default:
        if (a.startsWith('-')) { console.error(`unknown argument: ${a}`); o.help = true; o.bad = true; }
        else o.scenario = a;
    }
  }
  return o;
}

const HELP = `Colony Code replay

Usage: node tools/replay.mjs [scenario.json] [options]

  scenario.json          default: tools/scenarios/basic.json
  --speed X              time multiplier (2 = twice as fast, 0.5 = half speed). Default 1
  --loop                 repeat until Ctrl-C (same session, new goal and decision ids per run)
  --port N --token HEX   bridge address; default read from $COLONY_HOME or ~/.colony-code/bridge.json
  --home DIR             folder that holds bridge.json
  --session ID           Claude Code session id to pretend to be (default random UUID)
  --decision-timeout S   give up waiting for a decision after S seconds and "answer in the terminal"
                         (default 300, 0 = wait forever)
  --inbox                also long-poll GET /inbox as this session, print what the game sends
                         (goals, messages, task.create, diff/merge requests) and answer diff and
                         merge requests with the scenario's "respond" events
  --quiet                print nothing but errors`;

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) { console.log(HELP); process.exit(args.bad ? 2 : 0); }
  const log = args.quiet ? () => {} : (s) => console.log(s);

  let port = args.port;
  let token = args.token;
  if (!port || !token) {
    try {
      const info = readBridgeInfo(args.home);
      port = port || info.port;
      token = token || info.token;
    } catch (e) { console.error(col.red(e.message)); process.exit(1); }
  }
  const scenarioPath = path.resolve(args.scenario || DEFAULT_SCENARIO);
  let scenario;
  try { scenario = JSON.parse(fs.readFileSync(scenarioPath, 'utf8')); } catch (e) {
    console.error(col.red(`cannot load scenario ${scenarioPath}: ${e.message}`)); process.exit(1);
  }

  const client = new BridgeClient({ port, token });
  try {
    const h = await client.health();
    log(`${col.bold('replay')} ${scenario.name || path.basename(scenarioPath)} -> 127.0.0.1:${port} ${col.dim(`(${h.game}, mod ${h.mod}, protocol ${h.protocol})`)}`);
    if (h.protocol !== 0) console.error(col.yellow(`warning: bridge speaks protocol ${h.protocol}, replay was written for 0`));
  } catch (e) {
    console.error(col.red(`bridge not reachable at 127.0.0.1:${port}: ${e.message}`));
    process.exit(1);
  }

  const ctrl = new AbortController();
  let interrupted = false;
  process.on('SIGINT', () => {
    if (interrupted) process.exit(130);
    interrupted = true;
    log(col.yellow('\ninterrupted, sending final events (Ctrl-C again to quit now)'));
    ctrl.abort(new Error('interrupted'));
  });

  const sessionId = args.sessionId || crypto.randomUUID();
  if (args.inbox) {
    serveInbox({ client, scenario, sessionId, signal: ctrl.signal, log }).catch(() => {});
  }

  try {
    const stats = await runScenario({ client, scenario, speed: args.speed, loop: args.loop, sessionId, decisionTimeoutS: args.decisionTimeoutS, signal: ctrl.signal, log });
    log(col.green(`done: ${stats.posted} events in ${stats.runs} run(s), session ${stats.sessionId}`));
    ctrl.abort();
    process.exit(0);
  } catch (e) {
    if (interrupted) { log(col.dim('stopped')); process.exit(130); }
    console.error(col.red(`replay failed: ${e.message}`));
    process.exit(1);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
