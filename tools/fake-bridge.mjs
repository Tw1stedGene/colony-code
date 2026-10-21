#!/usr/bin/env node
// Colony Code fake bridge: a stand-in for the RimWorld mod's HTTP bridge.
// Implements the bridge protocol (v0) so the Claude Code plugin can be developed without the game.
// Zero dependencies, Node 24+.
//
// Usage:
//   node tools/fake-bridge.mjs [--port N] [--token HEX] [--home DIR] [--force] [--quiet]
//
// Interactive commands (stdin):
//   goal <text>                 queue a goal.submit inbox item
//   say @agent <text>           queue a user.message (agent id or "all")
//   interrupt <agentId>         queue an agent.interrupt
//   allow [decisionId]          answer a decision with "Allow once"
//   deny [decisionId] [reason]  answer a decision with "Deny"
//   answer <decisionId> <n|option>[,<n|option>...] [-- text]   answer with any option(s); several
//                               (comma-separated) for a multiSelect question, joined with newlines
//   text <decisionId> <free text>   answer a question with free text only (the "Other" answer)
//   always [decisionId]         answer a permission with "Always allow"
//   task [s=<session>] <text>   queue a task.create
//   diff [s=<session>] [worktree]                 queue a diff.request (no worktree: the checkout)
//   merge|reject [s=<session>] <worktree>         queue a merge.request
//   changes [s=<session>] <worktree> <text>       queue a merge.request with action request_changes
//   showdiff [requestId]        print a received diff.result (default: the last one)
//   usage | worktrees | reviews show usage per session, worktrees per session, merge results
//   state                       print the agents table and the task board
//   tasks                       print the task board (To do / In progress / Done; ✓ = has a result)
//   decisions                   list decisions
//   goals                       list goals
//   inbox                       show undelivered inbox items
//   help, quit

import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import readline from 'node:readline';
import { EventEmitter } from 'node:events';
import { fileURLToPath } from 'node:url';

export const PROTOCOL = 0;
export const DEFAULT_PORT = 7878;
export const MAX_PORT = 7887;
const MAX_WAIT_S = 30;
const DEFAULT_WAIT_S = 25;
const MAX_BODY = 4 * 1024 * 1024; // protocol: a body over 4 MB -> 413
const LOG_KEEP = 500;

export const STATES = ['idle', 'thinking', 'reading', 'editing', 'running', 'testing', 'waiting_user', 'blocked', 'done', 'error'];
export const STATIONS = ['desk', 'terminal', 'lounge', 'user'];
export const TASK_STATUSES = ['todo', 'doing', 'done'];

/** Directory holding bridge.json: $COLONY_HOME or ~/.colony-code. */
export function colonyHome(override) {
  return override || process.env.COLONY_HOME || path.join(os.homedir(), '.colony-code');
}

export function leadIdFor(sessionId) {
  return 'lead-' + String(sessionId).slice(0, 8);
}

function pidAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}

function parseWait(searchParams) {
  const raw = searchParams.get('wait');
  if (raw === null || raw === '') return DEFAULT_WAIT_S;
  const n = Number(raw);
  if (!Number.isFinite(n)) return DEFAULT_WAIT_S;
  return Math.max(0, Math.min(MAX_WAIT_S, n));
}

/**
 * Create a fake bridge. Call `start()` to listen and write bridge.json, `stop()` to close and remove it.
 * Emits: 'listening' {port}, 'event' (event), 'request' {method, path, status}, 'inbox.delivered' (items),
 * 'decision.answered' (decision).
 */
export class FakeBridge extends EventEmitter {
  constructor(opts = {}) {
    super();
    this.home = colonyHome(opts.home);
    this.requestedPort = opts.port; // undefined = 7878..7887 fallback, 0 = random, N = strict
    this.token = opts.token || crypto.randomBytes(16).toString('hex');
    this.game = opts.game || `fake-bridge (node ${process.version})`;
    this.modVersion = opts.modVersion || '0.1.0';
    this.force = !!opts.force;
    this.writeFile = opts.writeFile !== false;
    this.port = null;
    this.server = null;

    this.events = [];          // every received event, in order
    this.agents = new Map();   // id -> agent
    this.logs = new Map();     // agentId -> entries (last 500)
    this.goals = new Map();    // id -> goal
    this.tasks = new Map();    // sessionId -> [task] (replaced whole by task.snapshot)
    this.sessions = new Map(); // sessionId -> {cwd, repo, branch, model, ended}
    this.decisions = new Map();// id -> {decision, sessionId, status, option?, text?, openedAt}
    this.usage = new Map();    // sessionId -> usage.update (cumulative, last one wins)
    this.worktrees = new Map();// sessionId -> worktree.list (last one wins)
    this.diffs = new Map();    // requestId -> diff.result
    this.mergeResults = [];    // merge.result events, in order
    this.lastDiffId = null;
    this.inbox = [];           // undelivered inbox items
    this.inboxWaiters = [];    // {res, timer}
    this.decisionWaiters = new Map(); // id -> Set<{res, timer}>
    this.sockets = new Set();
    this._seq = 0;
  }

  get bridgeFile() { return path.join(this.home, 'bridge.json'); }

  async start() {
    if (this.writeFile) this._checkExistingFile();
    this.server = http.createServer((req, res) => this._handle(req, res));
    this.server.on('connection', (s) => { this.sockets.add(s); s.on('close', () => this.sockets.delete(s)); });
    const candidates = this.requestedPort !== undefined && this.requestedPort !== null
      ? [this.requestedPort]
      : Array.from({ length: MAX_PORT - DEFAULT_PORT + 1 }, (_, i) => DEFAULT_PORT + i);
    let lastErr;
    for (const p of candidates) {
      try {
        await new Promise((resolve, reject) => {
          const onErr = (e) => { this.server.off('listening', onOk); reject(e); };
          const onOk = () => { this.server.off('error', onErr); resolve(); };
          this.server.once('error', onErr);
          this.server.once('listening', onOk);
          this.server.listen(p, '127.0.0.1');
        });
        this.port = this.server.address().port;
        lastErr = null;
        break;
      } catch (e) {
        lastErr = e;
        if (e.code !== 'EADDRINUSE') break;
      }
    }
    if (lastErr) throw lastErr;
    if (this.writeFile) this._writeBridgeFile();
    this.emit('listening', { port: this.port });
    return this;
  }

  async stop() {
    for (const w of this.inboxWaiters) { clearTimeout(w.timer); this._send(w.res, 200, []); }
    this.inboxWaiters = [];
    for (const set of this.decisionWaiters.values()) {
      for (const w of set) { clearTimeout(w.timer); this._send(w.res, 200, { status: 'open' }); }
    }
    this.decisionWaiters.clear();
    if (this.writeFile) this._removeBridgeFile();
    if (this.server) {
      await new Promise((resolve) => {
        this.server.close(() => resolve());
        for (const s of this.sockets) s.destroy();
      });
      this.server = null;
    }
  }

  // ---- bridge.json ---------------------------------------------------------

  _checkExistingFile() {
    let existing;
    try { existing = JSON.parse(fs.readFileSync(this.bridgeFile, 'utf8')); } catch { return; }
    if (existing && existing.pid !== process.pid && pidAlive(existing.pid) && !this.force) {
      const err = new Error(`${this.bridgeFile} belongs to a running process (pid ${existing.pid}, ${existing.game || 'unknown'}). ` +
        'Is RimWorld running? Use --force to overwrite, or set COLONY_HOME to another folder.');
      err.code = 'BRIDGE_IN_USE';
      throw err;
    }
  }

  _writeBridgeFile() {
    fs.mkdirSync(this.home, { recursive: true, mode: 0o700 });
    const info = { port: this.port, token: this.token, pid: process.pid, protocol: PROTOCOL, game: this.game, mod: this.modVersion };
    const tmp = this.bridgeFile + '.' + process.pid + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(info, null, 2) + '\n', { mode: 0o600 });
    fs.renameSync(tmp, this.bridgeFile);
  }

  _removeBridgeFile() {
    try {
      const cur = JSON.parse(fs.readFileSync(this.bridgeFile, 'utf8'));
      if (cur.token === this.token) fs.unlinkSync(this.bridgeFile);
    } catch { /* already gone */ }
  }

  // ---- HTTP ----------------------------------------------------------------

  _send(res, status, body) {
    if (res.writableEnded || res.destroyed) return;
    const data = JSON.stringify(body);
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(data), 'Cache-Control': 'no-store' });
    res.end(data);
  }

  _handle(req, res) {
    const url = new URL(req.url, 'http://127.0.0.1');
    const p = url.pathname;
    res.on('finish', () => this.emit('request', { method: req.method, path: p, status: res.statusCode }));

    if (req.headers.origin !== undefined) return this._send(res, 403, { ok: false, error: 'origin not allowed' });
    const tok = req.headers['x-colony-token'];
    const challenge = req.headers['x-colony-challenge'];
    const challengeOk = typeof challenge === 'string' && /^[0-9a-fA-F]{16,128}$/.test(challenge);
    // The one tokenless request: GET /health with a challenge (the client checks our proof first).
    const tokenless = tok === undefined && challengeOk && p === '/health' && req.method === 'GET';
    if (!tokenless && (typeof tok !== 'string' || !safeEqual(tok, this.token))) return this._send(res, 401, { ok: false, error: 'bad or missing token' });

    if (p === '/health') {
      if (req.method !== 'GET') return this._send(res, 405, { ok: false, error: 'method not allowed' });
      const out = { ok: true, protocol: PROTOCOL, game: this.game, mod: this.modVersion };
      if (challengeOk) out.proof = proofFor(this.token, challenge);
      return this._send(res, 200, out);
    }
    if (p === '/events') {
      if (req.method !== 'POST') return this._send(res, 405, { ok: false, error: 'method not allowed' });
      return this._readBody(req, res, (text) => {
        let body;
        try { body = JSON.parse(text); } catch { return this._send(res, 400, { ok: false, error: 'invalid JSON' }); }
        if (!Array.isArray(body)) return this._send(res, 400, { ok: false, error: 'body must be a JSON array of events' });
        let accepted = 0;
        for (const ev of body) {
          if (ev && typeof ev === 'object' && typeof ev.type === 'string') { this._applyEvent(ev); accepted++; }
        }
        this._send(res, 200, { ok: true, accepted });
      });
    }
    if (p === '/inbox') {
      if (req.method !== 'GET') return this._send(res, 405, { ok: false, error: 'method not allowed' });
      return this._inboxPoll(res, parseWait(url.searchParams), url.searchParams.get('session') || null);
    }
    const m = /^\/decision\/([^/]+)$/.exec(p);
    if (m) {
      if (req.method !== 'GET') return this._send(res, 405, { ok: false, error: 'method not allowed' });
      let id;
      try { id = decodeURIComponent(m[1]); } catch { id = m[1]; }
      return this._decisionPoll(res, id, parseWait(url.searchParams));
    }
    return this._send(res, 404, { ok: false, error: 'not found' });
  }

  _readBody(req, res, cb) {
    const chunks = [];
    let size = 0;
    let aborted = false;
    req.on('data', (c) => {
      if (aborted) return;
      size += c.length;
      if (size > MAX_BODY) { aborted = true; this._send(res, 413, { ok: false, error: 'body too large' }); req.resume(); return; }
      chunks.push(c);
    });
    req.on('end', () => { if (!aborted) cb(Buffer.concat(chunks).toString('utf8')); });
  }

  // ---- inbox ---------------------------------------------------------------

  /** Items for `session`: addressed to it or to no session. A null session takes every item. */
  _inboxFor(session) {
    return this.inbox.filter((i) => session === null || i.sessionId === undefined || i.sessionId === session);
  }

  _inboxPoll(res, waitS, session = null) {
    if (this._inboxFor(session).length > 0 || waitS === 0) return this._deliverInbox(res, session);
    const w = { res, timer: null, session };
    w.timer = setTimeout(() => { this._dropInboxWaiter(w); this._send(res, 200, []); }, waitS * 1000);
    res.on('close', () => { clearTimeout(w.timer); this._dropInboxWaiter(w); });
    this.inboxWaiters.push(w);
  }

  _dropInboxWaiter(w) {
    const i = this.inboxWaiters.indexOf(w);
    if (i >= 0) this.inboxWaiters.splice(i, 1);
  }

  _deliverInbox(res, session = null) {
    const items = this._inboxFor(session);
    this.inbox = this.inbox.filter((i) => !items.includes(i));
    this._send(res, 200, items);
    if (items.length) this.emit('inbox.delivered', items);
  }

  /** Queue an inbox item. `type` + payload; id and ts are filled in. Returns the item. */
  enqueue(type, payload = {}) {
    const item = { id: `inbox-${Date.now().toString(36)}-${++this._seq}`, type, ts: Date.now(), ...payload };
    this.inbox.push(item);
    for (const w of [...this.inboxWaiters]) {
      if (w.res.destroyed || w.res.writableEnded) { clearTimeout(w.timer); this._dropInboxWaiter(w); continue; }
      if (this._inboxFor(w.session).length === 0) continue;
      clearTimeout(w.timer);
      this._dropInboxWaiter(w);
      this._deliverInbox(w.res, w.session);
      break;
    }
    return item;
  }

  submitGoal(text, sessionId) { return this.enqueue('goal.submit', sessionId ? { text, sessionId } : { text }); }
  createTask(text, sessionId) { return this.enqueue('task.create', sessionId ? { text, sessionId } : { text }); }
  /** diff.request: no worktree = the checkout's uncommitted changes. Returns the item (requestId inside). */
  requestDiff(worktree, sessionId) {
    const p = { requestId: `diff-${Date.now().toString(36)}-${this._seq + 1}` };
    if (worktree) p.worktree = worktree;
    if (sessionId) p.sessionId = sessionId;
    return this.enqueue('diff.request', p);
  }
  /** merge.request: action merge | request_changes | reject. */
  requestMerge(worktree, action, text, sessionId) {
    const p = { requestId: `merge-${Date.now().toString(36)}-${this._seq + 1}`, worktree, action };
    if (text) p.text = text;
    if (sessionId) p.sessionId = sessionId;
    return this.enqueue('merge.request', p);
  }
  /** A known session id from a unique prefix, or null. */
  findSession(prefix) {
    if (!prefix) return null;
    const hits = [...this.sessions.keys()].filter((id) => id.startsWith(prefix));
    return hits.length === 1 ? hits[0] : null;
  }
  sendMessage(to, text) { return this.enqueue('user.message', { to, text }); }
  interrupt(agentId) { return this.enqueue('agent.interrupt', { agentId }); }

  // ---- decisions -----------------------------------------------------------

  _decisionView(d) {
    // protocol: after decision.closed the id is forgotten, as in the mod -> "unknown"
    if (!d || d.status === 'closed') return { status: 'unknown' };
    if (d.status === 'answered') {
      const out = { status: 'answered', option: d.option };
      if (d.text !== undefined && d.text !== '') out.text = d.text;
      return out;
    }
    return { status: 'open' };
  }

  _decisionPoll(res, id, waitS) {
    const d = this.decisions.get(id);
    const view = this._decisionView(d);
    if (view.status !== 'open' || waitS === 0) return this._send(res, 200, view);
    const w = { res, timer: null };
    let set = this.decisionWaiters.get(id);
    if (!set) { set = new Set(); this.decisionWaiters.set(id, set); }
    set.add(w);
    w.timer = setTimeout(() => { set.delete(w); this._send(res, 200, this._decisionView(this.decisions.get(id))); }, waitS * 1000);
    res.on('close', () => { clearTimeout(w.timer); set.delete(w); });
  }

  _wakeDecision(id) {
    const set = this.decisionWaiters.get(id);
    if (!set) return;
    const view = this._decisionView(this.decisions.get(id));
    if (view.status === 'open') return;
    for (const w of set) { clearTimeout(w.timer); this._send(w.res, 200, view); }
    this.decisionWaiters.delete(id);
  }

  /** Every session's tasks, by session then list order. */
  allTasks() {
    return [...this.tasks.values()].flat();
  }

  /** Resolve a decision id or unique prefix; with no id, the single open decision. */
  findDecision(idOrPrefix) {
    if (!idOrPrefix) {
      const open = [...this.decisions.values()].filter((d) => d.status === 'open');
      return open.length === 1 ? open[0] : null;
    }
    if (this.decisions.has(idOrPrefix)) return this.decisions.get(idOrPrefix);
    const hits = [...this.decisions.values()].filter((d) => d.decision.id.startsWith(idOrPrefix));
    return hits.length === 1 ? hits[0] : null;
  }

  /** Answer a decision as the player would in game. Returns the decision record. */
  answer(id, option, text) {
    const d = this.decisions.get(id);
    if (!d) throw new Error(`unknown decision ${id}`);
    if (d.status !== 'open') throw new Error(`decision ${id} is already ${d.status}`);
    d.status = 'answered';
    d.option = option;
    if (text) d.text = text;
    d.answeredAt = Date.now();
    this.emit('decision.answered', d);
    this._wakeDecision(id);
    return d;
  }

  allow(id) { return this.answer(id, 'Allow once'); }
  deny(id, reason) { return this.answer(id, 'Deny', reason); }

  // ---- event handling --------------------------------------------------------

  _ensureAgent(id, init) {
    let a = this.agents.get(id);
    if (!a) { a = { id, role: 'worker', state: 'idle', activity: '', station: 'lounge', ...init, createdAt: Date.now() }; this.agents.set(id, a); }
    return a;
  }

  _applyEvent(ev) {
    this.events.push(ev);
    const sid = ev.sessionId;
    switch (ev.type) {
      case 'session.start': {
        this.sessions.set(sid, { cwd: ev.cwd, repo: ev.repo, label: ev.label, branch: ev.branch, model: ev.model, ended: false, startedAt: ev.ts });
        const lead = this._ensureAgent(leadIdFor(sid), { role: 'lead', sessionId: sid });
        lead.updatedAt = ev.ts;
        break;
      }
      case 'session.end': {
        const s = this.sessions.get(sid);
        if (s) s.ended = true;
        for (const a of this.agents.values()) {
          if (a.sessionId === sid) { a.state = 'idle'; a.station = 'lounge'; a.updatedAt = ev.ts; }
        }
        break;
      }
      case 'agent.upsert': {
        const ag = ev.agent;
        if (!ag || typeof ag.id !== 'string') break;
        const a = this._ensureAgent(ag.id, { sessionId: sid });
        Object.assign(a, ag, { sessionId: a.sessionId || sid, updatedAt: ev.ts });
        break;
      }
      case 'agent.log': {
        if (typeof ev.agentId !== 'string' || !Array.isArray(ev.entries)) break;
        const arr = this.logs.get(ev.agentId) || [];
        arr.push(...ev.entries);
        if (arr.length > LOG_KEEP) arr.splice(0, arr.length - LOG_KEEP);
        this.logs.set(ev.agentId, arr);
        break;
      }
      case 'agent.say':
        break;
      case 'decision.open': {
        const dec = ev.decision;
        if (!dec || typeof dec.id !== 'string') break;
        const prev = this.decisions.get(dec.id);
        if (prev && prev.status !== 'open') break; // keep the recorded answer
        this.decisions.set(dec.id, { decision: dec, sessionId: sid, status: 'open', openedAt: ev.ts });
        break;
      }
      case 'decision.closed': {
        const d = this.decisions.get(ev.id);
        if (d && d.status === 'open') {
          d.status = 'closed';
          if (ev.option !== undefined) d.option = ev.option;
          this._wakeDecision(ev.id);
        }
        break;
      }
      case 'goal.upsert': {
        const g = ev.goal;
        if (!g || typeof g.id !== 'string') break;
        this.goals.set(g.id, { ...(this.goals.get(g.id) || {}), ...g, sessionId: sid, updatedAt: ev.ts });
        break;
      }
      case 'task.snapshot': {
        // The session's whole list: it replaces what we had.
        if (typeof sid !== 'string' || !Array.isArray(ev.tasks)) break;
        const seen = new Set();
        const list = [];
        for (const t of ev.tasks) {
          if (!t || typeof t !== 'object' || typeof t.id !== 'string' || seen.has(t.id)) continue;
          seen.add(t.id);
          list.push({ ...t, status: TASK_STATUSES.includes(t.status) ? t.status : 'todo', sessionId: sid });
        }
        this.tasks.set(sid, list);
        break;
      }
      case 'usage.update': {
        if (typeof sid !== 'string') break;
        this.usage.set(sid, { inputTokens: ev.inputTokens || 0, outputTokens: ev.outputTokens || 0, cacheReadTokens: ev.cacheReadTokens || 0, turns: ev.turns || 0, agentId: ev.agentId, ts: ev.ts });
        break;
      }
      case 'worktree.list': {
        if (typeof sid !== 'string' || !Array.isArray(ev.worktrees)) break;
        this.worktrees.set(sid, { base: ev.base, dirty: !!ev.dirty, worktrees: ev.worktrees, ts: ev.ts });
        break;
      }
      case 'diff.result': {
        if (typeof ev.requestId !== 'string') break;
        this.diffs.set(ev.requestId, ev);
        this.lastDiffId = ev.requestId;
        break;
      }
      case 'merge.result': {
        if (typeof ev.requestId !== 'string') break;
        this.mergeResults.push(ev);
        break;
      }
      default:
        break; // unknown types are ignored, per protocol
    }
    this.emit('event', ev);
  }
}

/** Hex HMAC-SHA256(key = token, message = challenge): the server's proof that it holds the token. */
export function proofFor(token, challenge) {
  return crypto.createHmac('sha256', token).update(challenge, 'utf8').digest('hex');
}

function safeEqual(a, b) {
  const ba = Buffer.from(a);
  const bb = Buffer.from(b);
  return ba.length === bb.length && crypto.timingSafeEqual(ba, bb);
}

// ---- pretty printing ----------------------------------------------------------

const useColor = process.stdout.isTTY && !process.env.NO_COLOR;
const c = (code) => (s) => (useColor ? `\x1b[${code}m${s}\x1b[0m` : String(s));
export const col = { dim: c('2'), bold: c('1'), red: c('31'), green: c('32'), yellow: c('33'), blue: c('34'), magenta: c('35'), cyan: c('36'), gray: c('90') };

function hhmmss(ts) {
  const d = new Date(typeof ts === 'number' ? ts : Date.now());
  return d.toTimeString().slice(0, 8);
}

function indent(text, prefix = '      ') {
  return String(text).split('\n').map((l) => prefix + l).join('\n');
}

function fmtLogEntry(e) {
  const kind = e.kind || 'text';
  if (kind === 'diff') {
    return String(e.text).split('\n').map((l) => {
      const line = '      ' + l;
      if (l.startsWith('+') && !l.startsWith('+++')) return col.green(line);
      if (l.startsWith('-') && !l.startsWith('---')) return col.red(line);
      if (l.startsWith('@@')) return col.cyan(line);
      return col.dim(line);
    }).join('\n');
  }
  const tag = { tool: col.blue('tool  '), result: col.gray('result'), error: col.red('error '), text: col.dim('text  ') }[kind] || col.dim(kind.padEnd(6));
  const lines = String(e.text).split('\n');
  return `    ${tag} ${lines[0]}` + (lines.length > 1 ? '\n' + indent(lines.slice(1).join('\n'), '           ') : '');
}

export function formatEvent(ev) {
  const t = col.gray(hhmmss(ev.ts));
  const sid = ev.sessionId ? col.gray(`[${String(ev.sessionId).slice(0, 8)}]`) : '';
  const head = (label, rest = '') => `${t} ${sid} ${label} ${rest}`.replace(/\s+$/, '');
  switch (ev.type) {
    case 'session.start':
      return head(col.green(col.bold('session.start')), [ev.label && col.bold(`[${ev.label}]`), ev.repo && `repo=${ev.repo}`, ev.branch && `branch=${ev.branch}`, ev.model && `model=${ev.model}`, ev.cwd && col.dim(ev.cwd)].filter(Boolean).join(' '));
    case 'session.end':
      return head(col.yellow(col.bold('session.end')));
    case 'agent.upsert': {
      const a = ev.agent || {};
      return head(col.cyan('agent.upsert'), `${col.bold(a.id)} ${col.dim(a.role || '')}${a.label ? ' "' + a.label + '"' : ''} ${stateColor(a.state)} @${a.station || '?'} ${col.dim(a.activity || '')}`);
    }
    case 'agent.log':
      return head(col.blue('agent.log'), col.bold(ev.agentId)) + '\n' + (ev.entries || []).map(fmtLogEntry).join('\n');
    case 'agent.say':
      return head(col.magenta('agent.say'), `${col.bold(ev.agentId)}: "${ev.text}"`);
    case 'decision.open': {
      const d = ev.decision || {};
      const flags = [d.multiSelect && 'multiSelect', d.allowText && 'allowText'].filter(Boolean).join(' ');
      let s = head(col.yellow(col.bold('decision.open')), `${col.bold(d.id)} ${col.dim(d.kind || '')} by ${d.agentId}${flags ? ' ' + col.dim(flags) : ''}`) +
        `\n    ${col.bold('?')} ${d.question}\n    options: ${(d.options || []).map((o, i) => `[${i + 1}] ${o}`).join('  ')}`;
      if (d.context !== undefined) s += '\n' + col.dim(indent(typeof d.context === 'string' ? d.context : JSON.stringify(d.context), '    ctx '));
      if (d.kind === 'question') {
        s += '\n    ' + col.yellow(`-> answer ${d.id} <n>${d.multiSelect ? '[,<n>...]' : ''} [-- text]   |   text ${d.id} <free text>`);
      } else {
        const always = (d.options || []).includes('Always allow') ? `   |   always ${d.id}` : '';
        s += '\n    ' + col.yellow(`-> allow ${d.id}${always}   |   deny ${d.id} [reason]`);
      }
      return s;
    }
    case 'decision.closed':
      return head(col.yellow('decision.closed'), `${col.bold(ev.id)}${ev.option !== undefined ? ' option=' + ev.option : ''}`);
    case 'task.snapshot': {
      const list = Array.isArray(ev.tasks) ? ev.tasks : [];
      const n = (st) => list.filter((t) => t && t.status === st).length;
      return head(col.blue(col.bold('task.snapshot')), `${list.length} task(s): to do ${n('todo')}, in progress ${n('doing')}, done ${n('done')}`) +
        (list.length ? '\n' + list.map((t) => `    ${taskMark(t.status)} ${col.bold('#' + t.id)} ${col.dim('[' + taskSource(t) + ']')} ${t.subject}${t.result ? col.red(' (' + t.result + ')') : ''}${t.parent ? col.dim(' for #' + t.parent) : ''}${t.agentId ? col.dim(' by ' + t.agentId) : ''}${t.blockedBy && t.blockedBy.length ? col.red(' blocked by ' + t.blockedBy.map((b) => '#' + b).join(',')) : ''}${taskDetails(t)}`).join('\n') : '');
    }
    case 'goal.upsert': {
      const g = ev.goal || {};
      const st = g.status === 'done' ? col.green(g.status) : g.status === 'failed' ? col.red(g.status) : col.yellow(g.status);
      const prog = typeof g.progress === 'number' ? ` ${Math.round(g.progress * 100)}%` : '';
      return head(col.magenta(col.bold('goal.upsert')), `${col.bold(g.id)} ${st}${prog} "${g.text}"${g.summary ? '\n    summary: ' + g.summary : ''}`);
    }
    case 'usage.update':
      return head(col.magenta('usage.update'), `in ${fmtNum(ev.inputTokens)}  out ${fmtNum(ev.outputTokens)}  cache read ${fmtNum(ev.cacheReadTokens || 0)}  turns ${ev.turns ?? 0}${ev.agentId ? col.dim(' agent ' + ev.agentId) : ''}`);
    case 'worktree.list': {
      const list = Array.isArray(ev.worktrees) ? ev.worktrees : [];
      return head(col.blue(col.bold('worktree.list')), `base=${ev.base}${ev.dirty ? col.red(' dirty') : col.dim(' clean')} ${list.length} worktree(s)`) +
        (list.length ? '\n' + list.map(fmtWorktree).join('\n') : '');
    }
    case 'diff.result': {
      const files = Array.isArray(ev.files) ? ev.files : [];
      const lines = files.reduce((n, f) => n + (f.hunks || []).reduce((m, h) => m + (h.lines || []).length, 0), 0);
      return head(col.blue(col.bold('diff.result')), `${col.bold(ev.requestId)}${ev.worktree ? ' worktree=' + ev.worktree : ' (checkout)'} ${files.length} file(s), ${lines} line(s)${ev.truncated ? col.yellow(' truncated') : ''}${ev.error ? col.red(' error: ' + ev.error) : ''}`) +
        (files.length ? '\n' + files.map((f) => `    ${col.dim(pad(f.status, 8))} ${f.path}`).join('\n') + '\n    ' + col.yellow(`-> showdiff ${ev.requestId}`) : '');
    }
    case 'merge.result':
      return head(col.magenta(col.bold('merge.result')), `${col.bold(ev.requestId)} ${ev.action} ${ev.worktree} ${ev.ok ? col.green('ok') : col.red('refused')}: ${ev.message}`);
    default:
      return head(col.gray(ev.type), col.dim(JSON.stringify(ev)));
  }
}

function fmtNum(n) {
  return typeof n === 'number' ? n.toLocaleString('en-US') : '?';
}

function fmtWorktree(w) {
  return `    ${col.bold(pad(w.id, 22))} ${pad(w.branch, 24)} ${col.cyan('+' + (w.ahead ?? 0) + ' ahead')}  ${w.files ?? 0} file(s) ${col.green('+' + (w.additions ?? 0))} ${col.red('-' + (w.deletions ?? 0))}${w.agentId ? col.dim(' agent ' + w.agentId) : ''}`;
}

/** A diff.result as colored unified text with line numbers. */
export function formatDiff(ev) {
  if (!ev) return col.dim('  (no diff received yet)');
  const out = [col.bold(`diff ${ev.requestId}${ev.worktree ? ' (' + ev.worktree + ')' : ' (checkout)'}`)];
  if (ev.error) out.push(col.red('  error: ' + ev.error));
  for (const f of ev.files || []) {
    out.push(col.bold(`  ${f.status} ${f.path}`));
    for (const h of f.hunks || []) {
      out.push(col.cyan('  ' + h.header));
      for (const l of h.lines || []) {
        const nums = `${pad(l.oldNo ?? '', 5)}${pad(l.newNo ?? '', 5)}`;
        if (l.kind === 'add') out.push(col.green(`  ${nums}+${l.text}`));
        else if (l.kind === 'del') out.push(col.red(`  ${nums}-${l.text}`));
        else out.push(col.dim(`  ${nums} `) + l.text);
      }
    }
  }
  if (ev.truncated) out.push(col.yellow('  … truncated'));
  return out.join('\n');
}

export function formatUsage(bridge) {
  if (!bridge.usage.size) return col.dim('  (no usage yet)');
  return [...bridge.usage.entries()].map(([sid, u]) => {
    const label = bridge.sessions.get(sid)?.label;
    return `  ${pad(label ? `[${label}]` : sid.slice(0, 8), 20)} in ${fmtNum(u.inputTokens)}  out ${fmtNum(u.outputTokens)}  cache read ${fmtNum(u.cacheReadTokens)}  turns ${u.turns}`;
  }).join('\n');
}

export function formatWorktrees(bridge) {
  if (!bridge.worktrees.size) return col.dim('  (no worktree.list yet)');
  return [...bridge.worktrees.entries()].map(([sid, l]) => {
    const label = bridge.sessions.get(sid)?.label;
    const headLine = `  ${col.bold(label ? `[${label}]` : sid.slice(0, 8))} base=${l.base}${l.dirty ? col.red(' dirty') : col.dim(' clean')}`;
    return [headLine, ...(l.worktrees.length ? l.worktrees.map(fmtWorktree) : [col.dim('    (no other worktrees)')])].join('\n');
  }).join('\n');
}

/** A card's source as the protocol names it ("claude" when missing). */
export function taskSource(t) {
  return ['goal', 'agent', 'game', 'claude'].includes(t && t.source) ? t.source : 'claude';
}

/** Task details (v0.3.1): one dim line each for the result and the changed files, '' when none. */
export function taskDetails(t) {
  let s = '';
  if (typeof t.resultText === 'string' && t.resultText !== '') {
    s += '\n        ' + col.green('✓ ') + col.dim(oneLine(t.resultText, 100));
  }
  const changes = Array.isArray(t.changes) ? t.changes : [];
  if (changes.length) {
    const files = changes.map((c) => `${c.path} ${col.green('+' + (c.additions ?? 0))} ${col.red('-' + (c.deletions ?? 0))}${c.via ? col.dim(' via ' + c.via.join(',')) : ''}${c.truncated ? col.dim(' (cut)') : ''}`);
    const more = t.changesOmitted ? col.dim(` +${t.changesOmitted} more`) : '';
    s += '\n        ' + col.dim('changes: ') + files.join(', ') + more;
  }
  return s;
}

function oneLine(text, max) {
  const flat = String(text).replace(/\s+/g, ' ').trim();
  return flat.length > max ? flat.slice(0, max - 1) + '…' : flat;
}

function taskMark(status) {
  if (status === 'done') return col.green('[x]');
  if (status === 'doing') return col.cyan('[>]');
  return col.dim('[ ]');
}

function stateColor(s) {
  if (!s) return col.dim('?');
  if (s === 'error' || s === 'blocked') return col.red(s);
  if (s === 'waiting_user') return col.yellow(s);
  if (s === 'done') return col.green(s);
  if (s === 'idle') return col.dim(s);
  return col.cyan(s);
}

function pad(s, n) {
  s = String(s ?? '');
  return s.length > n ? s.slice(0, n - 1) + '…' : s.padEnd(n);
}

export function formatAgents(bridge) {
  const rows = [...bridge.agents.values()];
  if (!rows.length) return col.dim('  (no agents yet)');
  const hdr = `  ${pad('ID', 22)} ${pad('ROLE', 6)} ${pad('LABEL', 16)} ${pad('STATE', 13)} ${pad('STATION', 8)} ${pad('SESSION', 9)} ${pad('LOG', 4)} ACTIVITY`;
  const many = bridge.sessions.size > 1;
  const labelOf = (a) => {
    const sl = many && a.sessionId ? bridge.sessions.get(a.sessionId)?.label : undefined;
    return sl ? `[${sl}] ${a.label || ''}` : a.label;
  };
  const lines = rows.map((a) => `  ${pad(a.id, 22)} ${pad(a.role, 6)} ${pad(labelOf(a), 16)} ${stateColor(a.state)}${' '.repeat(Math.max(1, 14 - String(a.state || '?').length))}${pad(a.station, 8)} ${pad(String(a.sessionId || '').slice(0, 8), 9)} ${pad((bridge.logs.get(a.id) || []).length, 4)} ${a.activity || ''}`);
  return [col.bold(hdr), ...lines].join('\n');
}

/** The task board as three text columns: To do / In progress / Done (done newest last in the list, shown first). */
export function formatTasks(bridge, width = 110) {
  const tasks = bridge.allTasks();
  const cols = TASK_STATUSES.map((st) => tasks.filter((t) => t.status === st));
  cols[2] = cols[2].slice().reverse();
  const headers = [`To do (${cols[0].length})`, `In progress (${cols[1].length})`, `Done (${cols[2].length})`];
  if (!tasks.length) return col.dim('  (no tasks yet)');
  const w = Math.max(20, Math.floor((width - 2) / 3) - 2);
  const cell = (t) => {
    if (!t) return '';
    // derived cards are tagged; Claude's own tasks (source claude) stay as they were
    const src = taskSource(t);
    let s = `${t.parent ? '↳ ' : ''}#${t.id} ${src === 'claude' ? '' : `[${src}] `}${t.subject}`;
    if (t.result) s += ` (${t.result})`;
    // ✓: Claude's answer for it is in (resultText); the files it changed are counted
    if (typeof t.resultText === 'string' && t.resultText !== '') s += ' ✓';
    if (Array.isArray(t.changes) && t.changes.length) s += ` (${t.changes.length + (t.changesOmitted || 0)} file${t.changes.length + (t.changesOmitted || 0) === 1 ? '' : 's'})`;
    if (t.blockedBy && t.blockedBy.length) s += ' (blocked)';
    const who = t.owner || t.agentId;
    if (who) s += ` @${who}`;
    return s;
  };
  const rows = Math.max(...cols.map((c) => c.length));
  const lines = ['  ' + headers.map((h) => col.bold(pad(h, w))).join('  '), '  ' + headers.map(() => '-'.repeat(w)).join('  ')];
  for (let i = 0; i < rows; i++) lines.push('  ' + cols.map((c) => pad(cell(c[i]), w)).join('  ').replace(/\s+$/, ''));
  return lines.join('\n');
}

function formatDecisions(bridge) {
  const rows = [...bridge.decisions.values()];
  if (!rows.length) return col.dim('  (no decisions)');
  return rows.map((d) => {
    const st = d.status === 'open' ? col.yellow('open') : d.status === 'answered' ? col.green('answered') : col.gray(d.status);
    return `  ${pad(d.decision.id, 24)} ${st}${d.option !== undefined ? ' -> ' + d.option : ''}${d.text ? ' (' + d.text + ')' : ''}  ${col.dim(d.decision.question || '')}`;
  }).join('\n');
}

function formatGoals(bridge) {
  const rows = [...bridge.goals.values()];
  if (!rows.length) return col.dim('  (no goals)');
  return rows.map((g) => `  ${pad(g.id, 20)} ${pad(g.status, 7)} ${typeof g.progress === 'number' ? pad(Math.round(g.progress * 100) + '%', 5) : pad('', 5)} ${g.text}`).join('\n');
}

// ---- CLI ---------------------------------------------------------------------

function parseArgs(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    if (a === '--port' || a === '-p') o.port = Number(next());
    else if (a.startsWith('--port=')) o.port = Number(a.slice(7));
    else if (a === '--token') o.token = next();
    else if (a.startsWith('--token=')) o.token = a.slice(8);
    else if (a === '--home') o.home = next();
    else if (a.startsWith('--home=')) o.home = a.slice(7);
    else if (a === '--force') o.force = true;
    else if (a === '--quiet' || a === '-q') o.quiet = true;
    else if (a === '--help' || a === '-h') o.help = true;
    else { console.error(`unknown argument: ${a}`); o.help = true; o.bad = true; }
  }
  return o;
}

const HELP = `Colony Code fake bridge (protocol v${PROTOCOL})

Usage: node tools/fake-bridge.mjs [--port N] [--token HEX] [--home DIR] [--force] [--quiet]

  --port N     listen on exactly this port (0 = random). Default: first free of ${DEFAULT_PORT}..${MAX_PORT}
  --token HEX  use this token instead of a random one
  --home DIR   where bridge.json goes (default $COLONY_HOME or ~/.colony-code)
  --force      overwrite a bridge.json that belongs to a running process (e.g. the game)
  --quiet      do not print events (still prints command output)

Commands:
  goal <text>                    queue goal.submit
  say @<agent|all> <text>        queue user.message
  interrupt <agentId>            queue agent.interrupt
  allow [decisionId]             answer "Allow once" (id optional when exactly one is open; prefixes ok)
  deny [decisionId] [reason]     answer "Deny" with an optional reason
  always [decisionId]            answer "Always allow" (permissions that offer it)
  answer <decisionId> <n|option>[,<n|option>...] [-- text]
                                 answer with any option; comma-separate several for a multiSelect
                                 question (sent joined with newlines); "-- text" adds free text
  text <decisionId> <free text>  answer a question with free text only
  task [s=<session>] <text>      queue task.create (s= takes a session id prefix)
  diff [s=<session>] [worktree]  queue diff.request (no worktree: the checkout's uncommitted changes)
  merge [s=<session>] <worktree>            queue merge.request action merge
  reject [s=<session>] <worktree>           queue merge.request action reject
  changes [s=<session>] <worktree> <text>   queue merge.request action request_changes
  showdiff [requestId]           print a received diff.result (default: the last one)
  usage                          token usage per session
  worktrees                      the last worktree.list per session
  reviews                        merge results
  state                          agents table and task board
  tasks                          task board (To do / In progress / Done; ✓ = has a result, (n files) changed)
  decisions | goals | inbox | help | quit`;

export function runCommand(bridge, line, out = console.log) {
  const trimmed = line.trim();
  if (!trimmed) return true;
  const sp = trimmed.indexOf(' ');
  const cmd = (sp < 0 ? trimmed : trimmed.slice(0, sp)).toLowerCase();
  const rest = sp < 0 ? '' : trimmed.slice(sp + 1).trim();
  try {
    switch (cmd) {
      case 'goal': {
        if (!rest) { out('usage: goal <text>'); break; }
        const it = bridge.submitGoal(rest);
        out(col.green(`queued goal.submit ${it.id}: "${rest}"`));
        break;
      }
      case 'say': case 'msg': {
        const m = /^@?(\S+)\s+([\s\S]+)$/.exec(rest);
        if (!m) { out('usage: say @agent <text>   (agent id or "all")'); break; }
        const it = bridge.sendMessage(m[1], m[2]);
        out(col.green(`queued user.message ${it.id} to ${m[1]}`));
        break;
      }
      case 'interrupt': {
        if (!rest) { out('usage: interrupt <agentId>'); break; }
        const it = bridge.interrupt(rest);
        out(col.green(`queued agent.interrupt ${it.id} for ${rest}`));
        break;
      }
      case 'allow': case 'deny': {
        const parts = rest ? rest.split(/\s+/) : [];
        const d = bridge.findDecision(parts[0]);
        const reasonParts = parts.slice(1);
        if (!d) {
          if (!parts[0]) { out(col.red('which decision? (none or several open; give an id)')); out(formatDecisions(bridge)); break; }
          out(col.red(`no single decision matches "${parts[0]}"`)); break;
        }
        if (cmd === 'allow') bridge.allow(d.decision.id);
        else bridge.deny(d.decision.id, reasonParts.join(' ') || undefined);
        out(col.green(`answered ${d.decision.id}: ${d.option}${d.text ? ' (' + d.text + ')' : ''}`));
        break;
      }
      case 'answer': {
        const [head, text] = rest.split(/\s+--\s+/, 2);
        const parts = (head || '').split(/\s+/);
        const d = bridge.findDecision(parts[0]);
        if (!d || parts.length < 2) { out('usage: answer <decisionId> <n|option>[,<n|option>...] [-- text]'); break; }
        const sel = parts.slice(1).join(' ');
        const opts = d.decision.options || [];
        const pick = (one) => {
          const n = Number(one);
          return Number.isInteger(n) && n >= 1 && n <= opts.length ? opts[n - 1] : one;
        };
        const picks = d.decision.multiSelect ? sel.split(',').map((x) => x.trim()).filter(Boolean).map(pick) : [pick(sel)];
        const option = picks.join('\n');
        bridge.answer(d.decision.id, option, text);
        out(col.green(`answered ${d.decision.id}: ${picks.join(', ')}${text ? ' (' + text + ')' : ''}`));
        break;
      }
      case 'text': {
        const m = /^(\S+)\s+([\s\S]+)$/.exec(rest);
        const d = m && bridge.findDecision(m[1]);
        if (!d) { out('usage: text <decisionId> <free text>'); break; }
        bridge.answer(d.decision.id, '', m[2]);
        out(col.green(`answered ${d.decision.id} with text: ${m[2]}`));
        break;
      }
      case 'always': {
        const d = bridge.findDecision(rest.split(/\s+/)[0] || undefined);
        if (!d) { out(col.red('which decision? (none or several open; give an id)')); break; }
        if (!(d.decision.options || []).includes('Always allow')) { out(col.red(`${d.decision.id} does not offer "Always allow"`)); break; }
        bridge.answer(d.decision.id, 'Always allow');
        out(col.green(`answered ${d.decision.id}: Always allow`));
        break;
      }
      case 'task': case 'diff': case 'merge': case 'reject': case 'changes': {
        let words = rest ? rest.split(/\s+/) : [];
        let sessionId;
        if (words[0] && words[0].startsWith('s=')) {
          sessionId = bridge.findSession(words[0].slice(2));
          if (!sessionId) { out(col.red(`no single session matches "${words[0].slice(2)}"`)); break; }
          words = words.slice(1);
        }
        if (cmd === 'task') {
          if (!words.length) { out('usage: task [s=<session>] <text>'); break; }
          const it = bridge.createTask(words.join(' '), sessionId);
          out(col.green(`queued task.create ${it.id}: "${it.text}"`));
        } else if (cmd === 'diff') {
          const it = bridge.requestDiff(words[0], sessionId);
          out(col.green(`queued diff.request ${it.requestId}${it.worktree ? ' for ' + it.worktree : ' for the checkout'}`));
        } else {
          if (!words[0] || (cmd === 'changes' && words.length < 2)) { out(`usage: ${cmd} [s=<session>] <worktree>${cmd === 'changes' ? ' <text>' : ''}`); break; }
          const action = cmd === 'changes' ? 'request_changes' : cmd;
          const it = bridge.requestMerge(words[0], action, cmd === 'changes' ? words.slice(1).join(' ') : undefined, sessionId);
          out(col.green(`queued merge.request ${it.requestId}: ${action} ${words[0]}`));
        }
        break;
      }
      case 'showdiff': out(formatDiff(bridge.diffs.get(rest || bridge.lastDiffId))); break;
      case 'usage': out(formatUsage(bridge)); break;
      case 'worktrees': out(formatWorktrees(bridge)); break;
      case 'reviews': out(bridge.mergeResults.length ? bridge.mergeResults.map((r) => `  ${pad(r.requestId, 24)} ${pad(r.action, 16)} ${pad(r.worktree, 18)} ${r.ok ? col.green('ok') : col.red('refused')} ${r.message}`).join('\n') : col.dim('  (no merge results)')); break;
      case 'state':
        out(formatAgents(bridge)); out(col.bold('Tasks')); out(formatTasks(bridge));
        if (bridge.usage.size) { out(col.bold('Usage')); out(formatUsage(bridge)); }
        if (bridge.worktrees.size) { out(col.bold('Worktrees')); out(formatWorktrees(bridge)); }
        break;
      case 'agents': out(formatAgents(bridge)); break;
      case 'tasks': out(formatTasks(bridge)); break;
      case 'decisions': out(formatDecisions(bridge)); break;
      case 'goals': out(formatGoals(bridge)); break;
      case 'inbox': out(bridge.inbox.length ? bridge.inbox.map((i) => '  ' + JSON.stringify(i)).join('\n') : col.dim('  (inbox empty)')); break;
      case 'help': case '?': out(HELP); break;
      case 'quit': case 'exit': return false;
      default: out(col.red(`unknown command "${cmd}". Type help.`));
    }
  } catch (e) {
    out(col.red(e.message));
  }
  return true;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) { console.log(HELP); process.exit(args.bad ? 2 : 0); }
  const bridge = new FakeBridge({ port: Number.isFinite(args.port) ? args.port : undefined, token: args.token, home: args.home, force: args.force });

  const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: process.stdin.isTTY });
  const print = (s) => {
    if (process.stdout.isTTY) { readline.clearLine(process.stdout, 0); readline.cursorTo(process.stdout, 0); }
    console.log(s);
    if (process.stdin.isTTY) rl.prompt(true);
  };

  try {
    await bridge.start();
  } catch (e) {
    console.error(col.red(`fake-bridge: ${e.message}`));
    process.exit(1);
  }

  let stopping = false;
  const shutdown = async (code = 0) => {
    if (stopping) return;
    stopping = true;
    rl.close();
    await bridge.stop();
    process.exit(code);
  };
  process.stdout.on('error', (e) => { if (e.code === 'EPIPE') shutdown(0); });
  process.on('SIGINT', () => shutdown(0));
  process.on('SIGTERM', () => shutdown(0));
  process.on('exit', () => { try { bridge._removeBridgeFile(); } catch {} });

  console.log(`${col.bold('Colony Code fake bridge')} listening on ${col.cyan(`http://127.0.0.1:${bridge.port}`)}`);
  console.log(col.dim(`bridge.json: ${bridge.bridgeFile}`));
  console.log(col.dim(`token: ${bridge.token}`));
  console.log(col.dim('Type "help" for commands.'));
  // Machine-readable line for scripts and tests.
  console.log(`READY ${JSON.stringify({ port: bridge.port, token: bridge.token, file: bridge.bridgeFile })}`);

  if (!args.quiet) bridge.on('event', (ev) => print(formatEvent(ev)));
  bridge.on('request', (r) => {
    if (r.status === 401 || r.status === 403 || r.status >= 404) print(col.red(`${r.method} ${r.path} -> ${r.status}`));
  });
  bridge.on('inbox.delivered', (items) => { if (!args.quiet) print(col.gray(`inbox: delivered ${items.map((i) => i.id + ' (' + i.type + ')').join(', ')}`)); });

  rl.setPrompt(col.dim('bridge> '));
  if (process.stdin.isTTY) rl.prompt();
  rl.on('line', (line) => {
    const keep = runCommand(bridge, line, print);
    if (!keep) return shutdown(0);
    if (process.stdin.isTTY) rl.prompt();
  });
  // Keep running when stdin closes (e.g. started with </dev/null); stop with SIGINT/SIGTERM.
  rl.on('close', () => { if (!stopping && process.stdin.isTTY) shutdown(0); });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
