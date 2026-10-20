import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { startBridge, runNode, TOOLS } from './helpers.mjs';
import { BridgeClient, runScenario, serveInbox, DEFAULT_SCENARIO } from '../replay.mjs';

const STATES = new Set(['idle', 'thinking', 'reading', 'editing', 'running', 'testing', 'waiting_user', 'blocked', 'done', 'error']);
const STATIONS = new Set(['desk', 'terminal', 'lounge', 'user']);
const KINDS = new Set(['text', 'tool', 'result', 'error', 'diff']);

/** The player in the fake game: questions get `pick`, permissions get `perm(decision)`. */
function player(bridge, perm, pick = 'List\nCalendar', delay = 20) {
  bridge.on('event', (ev) => {
    if (ev.type !== 'decision.open') return;
    const d = ev.decision;
    setTimeout(() => {
      if (d.kind === 'question') bridge.answer(d.id, pick);
      else perm(d);
    }, delay);
  });
}

function checkProtocolShape(events) {
  for (const ev of events) {
    assert.equal(typeof ev.type, 'string');
    assert.ok(Number.isInteger(ev.ts), `${ev.type} has integer ts`);
    assert.equal(typeof ev.sessionId, 'string');
    assert.ok(!JSON.stringify(ev).includes('null'), 'no null fields');
    if (ev.type === 'agent.upsert') {
      const a = ev.agent;
      assert.ok(['lead', 'worker'].includes(a.role));
      assert.ok(STATES.has(a.state), `state ${a.state}`);
      assert.ok(STATIONS.has(a.station), `station ${a.station}`);
      assert.equal(typeof a.activity, 'string');
      assert.ok(a.activity.length <= 48, `activity "${a.activity}" <= 48 chars`);
      if (a.role === 'lead') assert.equal(a.id, 'lead-' + ev.sessionId.slice(0, 8));
    }
    if (ev.type === 'agent.log') {
      for (const e of ev.entries) { assert.ok(KINDS.has(e.kind)); assert.ok(Number.isInteger(e.ts)); assert.equal(typeof e.text, 'string'); }
    }
    if (ev.type === 'task.snapshot') {
      assert.ok(Array.isArray(ev.tasks));
      const ids = new Set();
      for (const t of ev.tasks) {
        assert.equal(typeof t.id, 'string');
        assert.ok(!ids.has(t.id), `unique task id ${t.id}`);
        ids.add(t.id);
        assert.equal(typeof t.subject, 'string');
        assert.ok(['todo', 'doing', 'done'].includes(t.status), `task status ${t.status}`);
        if (t.blockedBy !== undefined) assert.ok(Array.isArray(t.blockedBy) && t.blockedBy.length > 0);
        assert.ok(['goal', 'agent', 'game', 'claude'].includes(t.source), `task source ${t.source}`);
        if (t.parent !== undefined) assert.ok(ev.tasks.some((p) => p.id === t.parent), `parent ${t.parent} is on the board`);
        if (t.result !== undefined) { assert.equal(t.status, 'done'); assert.ok(['failed', 'interrupted'].includes(t.result)); }
      }
    }
    if (ev.type === 'decision.open') {
      const d = ev.decision;
      if (d.kind === 'question') {
        assert.match(d.id, /^ask-.+-\d+$/);
        assert.equal(typeof d.multiSelect, 'boolean');
        assert.equal(d.allowText, true);
        assert.ok(d.options.length >= 2);
      } else {
        assert.match(d.id, /^perm-/);
        if (d.options.includes('Always allow')) {
          assert.deepEqual(d.options, ['Allow once', 'Always allow', 'Deny']);
          assert.equal(Object.keys(d.context).at(-1), 'Always allow covers');
        } else {
          assert.deepEqual(d.options, ['Allow once', 'Deny']);
        }
      }
    }
    if (ev.type === 'usage.update') {
      for (const k of ['inputTokens', 'outputTokens', 'turns']) assert.ok(Number.isInteger(ev[k]), `usage ${k}`);
    }
    if (ev.type === 'worktree.list') {
      assert.equal(typeof ev.base, 'string');
      assert.equal(typeof ev.dirty, 'boolean');
      for (const w of ev.worktrees) for (const k of ['id', 'path', 'branch']) assert.equal(typeof w[k], 'string');
    }
    if (ev.type === 'diff.result') {
      assert.equal(typeof ev.requestId, 'string');
      for (const f of ev.files) {
        assert.ok(['added', 'modified', 'deleted', 'renamed'].includes(f.status));
        for (const h of f.hunks) for (const l of h.lines) {
          assert.ok(['add', 'del', 'ctx'].includes(l.kind));
          if (l.kind !== 'add') assert.ok(Number.isInteger(l.oldNo));
          if (l.kind !== 'del') assert.ok(Number.isInteger(l.newNo));
        }
      }
    }
    for (const s of JSON.stringify(ev).match(/\{\{\w+\}\}/g) || []) assert.fail(`unfilled template ${s} in ${ev.type}`);
  }
}

describe('replay against fake-bridge', () => {
  test('CLI runs basic.json end to end; decision round trip (allow)', async () => {
    const { bridge, home } = await startBridge();
    try {
      player(bridge, (d) => bridge.allow(d.id), 'List\nCalendar', 50);
      const r = await runNode([path.join(TOOLS, 'replay.mjs'), '--speed', '200', '--decision-timeout', '20'], { env: { COLONY_HOME: home } });
      assert.equal(r.code, 0, r.stderr + r.stdout);

      const types = bridge.events.map((e) => e.type);
      for (const t of ['session.start', 'agent.upsert', 'agent.log', 'agent.say', 'decision.open', 'goal.upsert', 'usage.update', 'worktree.list', 'diff.result', 'session.end']) {
        assert.ok(types.includes(t), `received ${t}`);
      }
      assert.equal(types[0], 'session.start');
      assert.equal(types.at(-1), 'session.end');
      checkProtocolShape(bridge.events);

      const sid = bridge.events[0].sessionId;
      const leadId = 'lead-' + sid.slice(0, 8);
      assert.ok(bridge.agents.has(leadId));
      const workers = [...bridge.agents.values()].filter((a) => a.role === 'worker');
      assert.equal(workers.length, 1);
      assert.ok(bridge.events.some((e) => e.type === 'agent.upsert' && e.agent.role === 'worker' && e.agent.state === 'testing'));
      assert.ok(bridge.logs.get(leadId).some((e) => e.kind === 'diff' && /^\+/m.test(e.text) && /^-/m.test(e.text)));

      assert.equal(bridge.events[0].label, 'todo-app');
      const decisions = [...bridge.decisions.values()];
      assert.equal(decisions.length, 2);
      assert.equal(decisions[0].status, 'answered');
      assert.equal(decisions[0].option, 'Allow once');
      assert.equal(decisions[1].decision.kind, 'question');
      assert.equal(decisions[1].decision.multiSelect, true);
      assert.ok(bridge.logs.get(leadId).some((e) => e.text === 'Which views should show due dates? → List, Calendar'));
      assert.ok(!bridge.logs.get(leadId).some((e) => /^Saved:/.test(e.text)), 'the "always" branch did not run');
      assert.deepEqual(bridge.usage.get(sid), { inputTokens: 31877, outputTokens: 5102, cacheReadTokens: 214900, turns: 2, agentId: undefined, ts: bridge.usage.get(sid).ts });
      assert.deepEqual(bridge.worktrees.get(sid).worktrees.map((w) => w.id), ['agent-a3f9c2e1b7d40001', 'todo-app-docs']);
      assert.equal(bridge.diffs.get('replay-diff-1').files.length, 3);
      // The allowed branch ran, after the decision was opened.
      const openIdx = types.indexOf('decision.open');
      const installIdx = bridge.events.findIndex((e) => e.type === 'agent.log' && e.entries.some((x) => x.text === 'Bash: npm install date-fns'));
      assert.ok(installIdx > openIdx, 'continued after the answer');
      assert.ok(!bridge.events.some((e) => e.type === 'agent.log' && e.entries.some((x) => x.kind === 'error')));

      const goal = [...bridge.goals.values()][0];
      assert.equal(goal.status, 'done');
      assert.equal(goal.progress, 1);

      // The board, as the plugin derives it: Claude's 4 tasks, the goal card (doing from the start),
      // the subagent card under it, and the task the player added from the game; all done at the end.
      const snaps = bridge.events.filter((e) => e.type === 'task.snapshot');
      assert.ok(snaps.length >= 6, 'several task snapshots');
      assert.deepEqual(snaps[0].tasks.map((t) => `${t.source}:${t.status}`), ['claude:todo', 'claude:todo', 'claude:todo', 'claude:todo', 'goal:doing']);
      assert.ok(snaps.some((e) => e.tasks.some((t) => t.status === 'doing')));
      const final = bridge.tasks.get(sid);
      assert.deepEqual(final.map((t) => `${t.id}:${t.source}:${t.status}`), [
        '1:claude:done', '2:claude:done', '3:claude:done', '4:claude:done',
        'goal-1:goal:done', `agent-${workers[0].id}:agent:done`, 'game-replay-1:game:done',
      ]);
      assert.equal(final.find((t) => t.source === 'agent').parent, 'goal-1');
      assert.ok(snaps.some((e) => e.tasks.some((t) => t.id === 'game-replay-1' && t.status === 'todo')), 'game card waits in To do first');
      assert.equal(final.find((t) => t.id === '3').agentId, workers[0].id);
      assert.equal(final.find((t) => t.id === '1').agentId, leadId);
      // session.end puts everyone in the lounge
      for (const a of bridge.agents.values()) { assert.equal(a.state, 'idle'); assert.equal(a.station, 'lounge'); }
    } finally {
      await bridge.stop();
    }
  });

  test('deny with reason takes the denied branch', async () => {
    const { bridge } = await startBridge();
    try {
      player(bridge, (d) => bridge.deny(d.id, 'no new deps'));
      const scenario = JSON.parse(fs.readFileSync(DEFAULT_SCENARIO, 'utf8'));
      const client = new BridgeClient({ port: bridge.port, token: bridge.token });
      const stats = await runScenario({ client, scenario, speed: 500, decisionTimeoutS: 10, log: () => {} });
      assert.equal(stats.decisions[0].option, 'Deny');
      const errors = bridge.events.filter((e) => e.type === 'agent.log').flatMap((e) => e.entries).filter((e) => e.kind === 'error');
      assert.equal(errors.length, 1);
      assert.match(errors[0].text, /no new deps/);
      assert.ok(!bridge.events.some((e) => e.type === 'agent.log' && e.entries.some((x) => x.text === 'Bash: npm install date-fns')));
      assert.equal([...bridge.goals.values()][0].status, 'done');
    } finally {
      await bridge.stop();
    }
  });

  test('no answer: times out, posts decision.closed and continues', async () => {
    const { bridge } = await startBridge();
    try {
      const scenario = JSON.parse(fs.readFileSync(DEFAULT_SCENARIO, 'utf8'));
      const client = new BridgeClient({ port: bridge.port, token: bridge.token });
      const stats = await runScenario({ client, scenario, speed: 500, decisionTimeoutS: 1, log: () => {} });
      assert.equal(stats.decisions[0].status, 'timeout');
      assert.equal(stats.decisions[1].status, 'timeout');
      const closed = bridge.events.filter((e) => e.type === 'decision.closed');
      assert.equal(closed.length, 2, 'the permission and the question are closed');
      assert.equal(closed[0].option, undefined); // the real terminal answer is unknown
      assert.ok([...bridge.decisions.values()].every((d) => d.status === 'closed'));
      assert.equal([...bridge.goals.values()][0].status, 'done');
    } finally {
      await bridge.stop();
    }
  });

  test('loop: second run reuses the session, skips "once" steps, new decision and goal ids', async () => {
    const { bridge } = await startBridge();
    try {
      player(bridge, (d) => bridge.allow(d.id), 'Export', 0);
      const scenario = JSON.parse(fs.readFileSync(DEFAULT_SCENARIO, 'utf8'));
      const client = new BridgeClient({ port: bridge.port, token: bridge.token });
      const ctrl = new AbortController();
      bridge.on('event', (ev) => { if (bridge.decisions.size === 4 && ev.type === 'goal.upsert' && ev.goal.status === 'done') ctrl.abort(); });
      await assert.rejects(() => runScenario({ client, scenario, speed: 1000, loop: true, decisionTimeoutS: 5, signal: ctrl.signal, log: () => {} }));
      assert.equal(bridge.events.filter((e) => e.type === 'session.start').length, 1);
      assert.equal(bridge.decisions.size, 4);
      assert.equal(bridge.goals.size, 2);
      assert.equal(new Set(bridge.events.map((e) => e.sessionId)).size, 1);
      assert.equal(bridge.events.at(-1).type, 'session.end', 'final step sent on interrupt');
    } finally {
      await bridge.stop();
    }
  });

  test('Always allow takes the allowed and the always branches', async () => {
    const { bridge } = await startBridge();
    try {
      player(bridge, (d) => bridge.answer(d.id, 'Always allow'));
      const scenario = JSON.parse(fs.readFileSync(DEFAULT_SCENARIO, 'utf8'));
      const client = new BridgeClient({ port: bridge.port, token: bridge.token });
      const stats = await runScenario({ client, scenario, speed: 500, decisionTimeoutS: 10, log: () => {} });
      assert.equal(stats.decisions[0].option, 'Always allow');
      const texts = bridge.events.filter((e) => e.type === 'agent.log').flatMap((e) => e.entries).map((e) => e.text);
      assert.ok(texts.includes('Bash: npm install date-fns'));
      assert.ok(texts.some((t) => /^Saved: Bash\(npm install:\*\)/.test(t)));
      checkProtocolShape(bridge.events);
    } finally {
      await bridge.stop();
    }
  });

  test('a free-text question answer reaches {{answerOption}}', async () => {
    const { bridge } = await startBridge();
    try {
      bridge.on('event', (ev) => {
        if (ev.type !== 'decision.open') return;
        setTimeout(() => (ev.decision.kind === 'question' ? bridge.answer(ev.decision.id, '', 'only the widget') : bridge.allow(ev.decision.id)), 10);
      });
      const scenario = JSON.parse(fs.readFileSync(DEFAULT_SCENARIO, 'utf8'));
      const client = new BridgeClient({ port: bridge.port, token: bridge.token });
      const stats = await runScenario({ client, scenario, speed: 500, decisionTimeoutS: 10, log: () => {} });
      assert.equal(stats.decisions[1].text, 'only the widget');
      const r = await client.decision(stats.decisions[1].id, 0);
      assert.deepEqual(r, { status: 'answered', option: '', text: 'only the widget' });
    } finally {
      await bridge.stop();
    }
  });

  test('serveInbox answers diff and merge requests with the scenario\'s respond events', async () => {
    const { bridge } = await startBridge();
    const ctrl = new AbortController();
    try {
      const scenario = JSON.parse(fs.readFileSync(DEFAULT_SCENARIO, 'utf8'));
      const client = new BridgeClient({ port: bridge.port, token: bridge.token });
      const sessionId = 'cafebabe-0000-4000-8000-000000000001';
      await client.postEvents([{ type: 'session.start', ts: Date.now(), sessionId, cwd: '/x', label: 'x' }]);
      const served = serveInbox({ client, scenario, sessionId, signal: ctrl.signal, log: () => {}, waitS: 1 });
      const diffItem = bridge.requestDiff('todo-app-docs', sessionId);
      const mainDiff = bridge.requestDiff(undefined, sessionId);
      const mergeItem = bridge.requestMerge('todo-app-docs', 'reject', undefined, sessionId);
      const other = bridge.requestDiff('x', 'some-other-session');
      const { waitFor } = await import('./helpers.mjs');
      await waitFor(() => bridge.mergeResults.length === 1 && bridge.diffs.size === 2);
      assert.equal(bridge.diffs.get(diffItem.requestId).worktree, 'todo-app-docs');
      assert.equal(bridge.diffs.get(mainDiff.requestId).worktree, undefined);
      assert.deepEqual(
        { ok: bridge.mergeResults[0].ok, action: bridge.mergeResults[0].action, requestId: bridge.mergeResults[0].requestId },
        { ok: true, action: 'reject', requestId: mergeItem.requestId },
      );
      assert.ok(bridge.inbox.includes(other), 'another session\'s request stays queued');
      checkProtocolShape(bridge.events);
      ctrl.abort();
      await served;
    } finally {
      ctrl.abort();
      await bridge.stop();
    }
  });

  test('CLI fails cleanly with a wrong token (401)', async () => {
    const { bridge } = await startBridge();
    try {
      const r = await runNode([path.join(TOOLS, 'replay.mjs'), '--port', String(bridge.port), '--token', 'f'.repeat(32)]);
      assert.equal(r.code, 1);
      assert.match(r.stderr, /401/);
      assert.equal(bridge.events.length, 0);
    } finally {
      await bridge.stop();
    }
  });

  test('CLI fails cleanly when no bridge.json exists', async () => {
    const { tempHome } = await import('./helpers.mjs');
    const r = await runNode([path.join(TOOLS, 'replay.mjs')], { env: { COLONY_HOME: tempHome() } });
    assert.equal(r.code, 1);
    assert.match(r.stderr, /bridge\.json/);
  });
});
