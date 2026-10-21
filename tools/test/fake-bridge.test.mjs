import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { startBridge, req, tempHome, waitFor, TOOLS } from './helpers.mjs';
import crypto from 'node:crypto';
import { FakeBridge, runCommand, proofFor, formatEvent, formatTasks } from '../fake-bridge.mjs';

describe('fake-bridge: discovery and auth', () => {
  let ctx;
  before(async () => { ctx = await startBridge(); });
  after(async () => { await ctx.bridge.stop(); });

  test('writes bridge.json with port, 32-hex token, pid, protocol 0', () => {
    const info = JSON.parse(fs.readFileSync(path.join(ctx.home, 'bridge.json'), 'utf8'));
    assert.equal(info.port, ctx.bridge.port);
    assert.match(info.token, /^[0-9a-f]{32}$/);
    assert.equal(info.pid, process.pid);
    assert.equal(info.protocol, 0);
    assert.equal(typeof info.game, 'string');
    assert.equal(typeof info.mod, 'string');
  });

  test('401 without token or with a wrong token', async () => {
    assert.equal((await req(ctx.base, null, 'GET', '/health')).status, 401);
    assert.equal((await req(ctx.base, 'deadbeef', 'GET', '/health')).status, 401);
    assert.equal((await req(ctx.base, 'x'.repeat(32), 'POST', '/events', { body: [] })).status, 401);
  });

  test('403 for any Origin header, even with the right token', async () => {
    const r = await req(ctx.base, ctx.token, 'GET', '/health', { headers: { Origin: 'https://evil.example' } });
    assert.equal(r.status, 403);
    const r2 = await req(ctx.base, ctx.token, 'POST', '/events', { body: [], headers: { Origin: 'null' } });
    assert.equal(r2.status, 403);
  });

  test('GET /health', async () => {
    const r = await req(ctx.base, ctx.token, 'GET', '/health');
    assert.equal(r.status, 200);
    assert.match(r.headers.get('content-type'), /^application\/json/);
    assert.equal(r.json.ok, true);
    assert.equal(r.json.protocol, 0);
    assert.equal(typeof r.json.game, 'string');
    assert.equal(typeof r.json.mod, 'string');
  });

  test('404 for unknown paths, 405 for wrong methods', async () => {
    assert.equal((await req(ctx.base, ctx.token, 'GET', '/nope')).status, 404);
    assert.equal((await req(ctx.base, ctx.token, 'POST', '/health', { body: {} })).status, 405);
    assert.equal((await req(ctx.base, ctx.token, 'GET', '/events')).status, 405);
    assert.equal((await req(ctx.base, ctx.token, 'POST', '/inbox', { body: {} })).status, 405);
    assert.equal((await req(ctx.base, ctx.token, 'DELETE', '/decision/x')).status, 405);
  });

  test('POST /events rejects non-arrays and bad JSON with 400', async () => {
    assert.equal((await req(ctx.base, ctx.token, 'POST', '/events', { body: { type: 'agent.say' } })).status, 400);
    assert.equal((await req(ctx.base, ctx.token, 'POST', '/events', { rawBody: '[{' })).status, 400);
  });
});

describe('fake-bridge: events and state', () => {
  let ctx;
  before(async () => { ctx = await startBridge(); });
  after(async () => { await ctx.bridge.stop(); });

  test('accepts events, ignores unknown types, tracks agents', async () => {
    const sid = '1234abcd-0000-4000-8000-000000000000';
    const ts = Date.now();
    const r = await req(ctx.base, ctx.token, 'POST', '/events', { body: [
      { type: 'session.start', ts, sessionId: sid, cwd: '/tmp/x', repo: 'x' },
      { type: 'agent.upsert', ts, sessionId: sid, agent: { id: 'w1', role: 'worker', state: 'testing', activity: 'running npm test', station: 'terminal' } },
      { type: 'agent.log', ts, sessionId: sid, agentId: 'w1', entries: [{ ts, kind: 'tool', text: 'Bash: npm test' }] },
      { type: 'something.new', ts, sessionId: sid, extra: 1 },
    ] });
    assert.equal(r.status, 200);
    assert.deepEqual(r.json, { ok: true, accepted: 4 });
    const lead = ctx.bridge.agents.get('lead-1234abcd');
    assert.ok(lead, 'session.start creates the lead');
    assert.equal(lead.role, 'lead');
    assert.equal(ctx.bridge.agents.get('w1').state, 'testing');
    assert.equal(ctx.bridge.logs.get('w1').length, 1);

    await req(ctx.base, ctx.token, 'POST', '/events', { body: [{ type: 'session.end', ts, sessionId: sid }] });
    for (const id of ['lead-1234abcd', 'w1']) {
      assert.equal(ctx.bridge.agents.get(id).state, 'idle');
      assert.equal(ctx.bridge.agents.get(id).station, 'lounge');
    }
  });

  test('keeps only the last 500 log entries per agent', async () => {
    const ts = Date.now();
    const entries = Array.from({ length: 620 }, (_, i) => ({ ts, kind: 'text', text: `line ${i}` }));
    await req(ctx.base, ctx.token, 'POST', '/events', { body: [{ type: 'agent.log', ts, sessionId: 's', agentId: 'big', entries }] });
    const log = ctx.bridge.logs.get('big');
    assert.equal(log.length, 500);
    assert.equal(log[499].text, 'line 619');
  });
});

describe('fake-bridge: inbox', () => {
  let ctx;
  before(async () => { ctx = await startBridge(); });
  after(async () => { await ctx.bridge.stop(); });

  test('wait=0 on empty inbox returns [] immediately', async () => {
    const r = await req(ctx.base, ctx.token, 'GET', '/inbox?wait=0');
    assert.equal(r.status, 200);
    assert.deepEqual(r.json, []);
  });

  test('each item is delivered exactly once', async () => {
    const item = ctx.bridge.submitGoal('add a README');
    const r1 = await req(ctx.base, ctx.token, 'GET', '/inbox?wait=0');
    assert.equal(r1.json.length, 1);
    assert.deepEqual(r1.json[0], item);
    assert.equal(r1.json[0].type, 'goal.submit');
    assert.equal(r1.json[0].text, 'add a README');
    assert.equal(typeof r1.json[0].id, 'string');
    assert.equal(typeof r1.json[0].ts, 'number');
    const r2 = await req(ctx.base, ctx.token, 'GET', '/inbox?wait=0');
    assert.deepEqual(r2.json, []);
  });

  test('long-poll is held open and answered as soon as an item arrives', async () => {
    const t0 = Date.now();
    const pending = req(ctx.base, ctx.token, 'GET', '/inbox?wait=10');
    await new Promise((r) => setTimeout(r, 150));
    ctx.bridge.sendMessage('all', 'lunch time');
    const r = await pending;
    assert.ok(Date.now() - t0 < 3000);
    assert.equal(r.json.length, 1);
    assert.deepEqual({ type: r.json[0].type, to: r.json[0].to, text: r.json[0].text }, { type: 'user.message', to: 'all', text: 'lunch time' });
  });

  test('long-poll times out with []', async () => {
    const t0 = Date.now();
    const r = await req(ctx.base, ctx.token, 'GET', '/inbox?wait=1');
    assert.deepEqual(r.json, []);
    assert.ok(Date.now() - t0 >= 900);
  });

  test('two concurrent pollers: the item goes to only one of them', async () => {
    const a = req(ctx.base, ctx.token, 'GET', '/inbox?wait=2');
    const b = req(ctx.base, ctx.token, 'GET', '/inbox?wait=2');
    await new Promise((r) => setTimeout(r, 150));
    ctx.bridge.interrupt('lead-12345678');
    const [ra, rb] = await Promise.all([a, b]);
    const all = [...ra.json, ...rb.json];
    assert.equal(all.length, 1);
    assert.equal(all[0].type, 'agent.interrupt');
    assert.equal(all[0].agentId, 'lead-12345678');
  });
});

describe('fake-bridge: decisions', () => {
  let ctx;
  before(async () => { ctx = await startBridge(); });
  after(async () => { await ctx.bridge.stop(); });

  const open = (id) => req(ctx.base, ctx.token, 'POST', '/events', { body: [{
    type: 'decision.open', ts: Date.now(), sessionId: 's1',
    decision: { id, agentId: 'lead-s1', kind: 'permission', question: 'Run: npm install left-pad', options: ['Allow once', 'Deny'], context: { tool: 'Bash', input: { command: 'npm install left-pad' } } },
  }] });

  test('unknown id', async () => {
    const r = await req(ctx.base, ctx.token, 'GET', '/decision/perm-never?wait=5');
    assert.deepEqual(r.json, { status: 'unknown' });
  });

  test('open after wait expires', async () => {
    await open('perm-1');
    const r = await req(ctx.base, ctx.token, 'GET', '/decision/perm-1?wait=1');
    assert.deepEqual(r.json, { status: 'open' });
  });

  test('long-poll wakes on answer (allow)', async () => {
    await open('perm-2');
    const pending = req(ctx.base, ctx.token, 'GET', '/decision/perm-2?wait=10');
    await new Promise((r) => setTimeout(r, 100));
    ctx.bridge.allow('perm-2');
    const r = await pending;
    assert.deepEqual(r.json, { status: 'answered', option: 'Allow once' });
    // Later polls keep returning the recorded answer.
    const again = await req(ctx.base, ctx.token, 'GET', '/decision/perm-2?wait=0');
    assert.deepEqual(again.json, { status: 'answered', option: 'Allow once' });
  });

  test('deny with reason carries text', async () => {
    await open('perm-3');
    ctx.bridge.deny('perm-3', 'use the stdlib');
    const r = await req(ctx.base, ctx.token, 'GET', '/decision/perm-3?wait=5');
    assert.deepEqual(r.json, { status: 'answered', option: 'Deny', text: 'use the stdlib' });
  });

  test('decision.closed (answered in the terminal) closes it', async () => {
    await open('perm-4');
    await req(ctx.base, ctx.token, 'POST', '/events', { body: [{ type: 'decision.closed', ts: Date.now(), sessionId: 's1', id: 'perm-4', option: 'Deny' }] });
    assert.equal(ctx.bridge.decisions.get('perm-4').status, 'closed');
    assert.throws(() => ctx.bridge.allow('perm-4'));    // as in the mod: a closed decision is forgotten
    const r = await req(ctx.base, ctx.token, 'GET', '/decision/perm-4?wait=0');
    assert.deepEqual(r.json, { status: 'unknown' });
  });

  test('stdin commands allow/deny/goal/say/state', async () => {
    await open('perm-cli-a');
    const out = [];
    const o = (s) => out.push(s);
    runCommand(ctx.bridge, 'deny perm-cli-a not today', o);
    assert.equal(ctx.bridge.decisions.get('perm-cli-a').option, 'Deny');
    assert.equal(ctx.bridge.decisions.get('perm-cli-a').text, 'not today');
    await open('perm-cli-b');
    // perm-1 is still open too, so a bare "allow" is ambiguous and must not answer anything
    runCommand(ctx.bridge, 'allow', o);
    assert.equal(ctx.bridge.decisions.get('perm-cli-b').status, 'open');
    runCommand(ctx.bridge, 'allow perm-cli-b', o);
    assert.equal(ctx.bridge.decisions.get('perm-cli-b').option, 'Allow once');
    runCommand(ctx.bridge, 'goal write docs', o);
    runCommand(ctx.bridge, 'say @lead-s1 please hurry', o);
    const r = await req(ctx.base, ctx.token, 'GET', '/inbox?wait=0');
    assert.deepEqual(r.json.map((i) => i.type), ['goal.submit', 'user.message']);
    assert.equal(r.json[1].to, 'lead-s1');
    assert.equal(r.json[1].text, 'please hurry');
    await req(ctx.base, ctx.token, 'POST', '/events', { body: [{ type: 'session.start', ts: Date.now(), sessionId: 'feedface99', cwd: '/x' }] });
    out.length = 0;
    runCommand(ctx.bridge, 'state', o);
    assert.match(out.join('\n'), /lead-feedface/);
  });
});

describe('fake-bridge: lifecycle', () => {
  test('removes bridge.json on stop', async () => {
    const { bridge, home } = await startBridge();
    const f = path.join(home, 'bridge.json');
    assert.ok(fs.existsSync(f));
    await bridge.stop();
    assert.ok(!fs.existsSync(f));
  });

  test('refuses to overwrite a bridge.json owned by another live process unless forced', async () => {
    const home = tempHome();
    fs.writeFileSync(path.join(home, 'bridge.json'), JSON.stringify({ port: 7878, token: 'a'.repeat(32), pid: process.ppid, protocol: 0, game: 'RimWorld', mod: '0.1.0' }));
    const b = new FakeBridge({ port: 0, home });
    await assert.rejects(() => b.start(), /running process/);
    const forced = new FakeBridge({ port: 0, home, force: true });
    await forced.start();
    await forced.stop();
  });

  test('CLI: READY line, stdin commands, SIGTERM cleanup', async () => {
    const home = tempHome();
    const child = spawn(process.execPath, [path.join(TOOLS, 'fake-bridge.mjs'), '--port', '0'], {
      env: { ...process.env, COLONY_HOME: home, NO_COLOR: '1' }, stdio: ['pipe', 'pipe', 'pipe'],
    });
    let stdout = '';
    child.stdout.on('data', (d) => { stdout += d; });
    try {
      const ready = await waitFor(() => /READY (\{.*\})/.exec(stdout)?.[1]);
      const info = JSON.parse(ready);
      const base = `http://127.0.0.1:${info.port}`;
      const file = JSON.parse(fs.readFileSync(path.join(home, 'bridge.json'), 'utf8'));
      assert.equal(file.token, info.token);

      await req(base, info.token, 'POST', '/events', { body: [
        { type: 'session.start', ts: Date.now(), sessionId: 'abcdef1234', cwd: '/x' },
        { type: 'decision.open', ts: Date.now(), sessionId: 'abcdef1234', decision: { id: 'perm-cli', agentId: 'lead-abcdef12', kind: 'permission', question: 'Run: ls', options: ['Allow once', 'Deny'] } },
      ] });
      await waitFor(() => stdout.includes('perm-cli'));
      child.stdin.write('goal hello world\n');
      child.stdin.write('allow perm-cli\n');
      child.stdin.write('state\n');
      const inbox = await req(base, info.token, 'GET', '/inbox?wait=5');
      assert.equal(inbox.json[0].type, 'goal.submit');
      assert.equal(inbox.json[0].text, 'hello world');
      const d = await req(base, info.token, 'GET', '/decision/perm-cli?wait=5');
      assert.deepEqual(d.json, { status: 'answered', option: 'Allow once' });
      await waitFor(() => stdout.includes('lead-abcdef12'));
    } finally {
      const exited = new Promise((r) => child.on('exit', r));
      child.kill('SIGTERM');
      await exited;
    }
    assert.ok(!fs.existsSync(path.join(home, 'bridge.json')), 'bridge.json removed on exit');
  });
});

describe('fake-bridge: server proof and session inbox', () => {
  let ctx;
  before(async () => { ctx = await startBridge(); });
  after(async () => { await ctx.bridge.stop(); });

  test('tokenless /health with a challenge returns HMAC-SHA256(token, challenge)', async () => {
    const challenge = '00112233445566778899aabbccddeeff';
    const r = await req(ctx.base, null, 'GET', '/health', { headers: { 'X-Colony-Challenge': challenge } });
    assert.equal(r.status, 200);
    assert.equal(r.json.proof, crypto.createHmac('sha256', ctx.token).update(challenge).digest('hex'));
    assert.equal(proofFor('Jefe', 'what do ya want for nothing?'), '5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843');
    assert.equal((await req(ctx.base, null, 'GET', '/health', { headers: { 'X-Colony-Challenge': 'nothex' } })).status, 401);
    assert.equal((await req(ctx.base, null, 'GET', '/inbox?wait=0', { headers: { 'X-Colony-Challenge': challenge } })).status, 401);
    assert.equal((await req(ctx.base, null, 'GET', '/health', { headers: { 'X-Colony-Challenge': challenge, Origin: 'http://x' } })).status, 403);
  });

  test('?session= takes only its own and unaddressed items; others stay queued', async () => {
    ctx.bridge.enqueue('user.message', { to: 'a', text: 'for A', sessionId: 'A' });
    ctx.bridge.enqueue('user.message', { to: 'b', text: 'for B', sessionId: 'B' });
    ctx.bridge.enqueue('goal.submit', { text: 'anyone' });
    const b = await req(ctx.base, ctx.token, 'GET', '/inbox?wait=0&session=B');
    assert.deepEqual(b.json.map((i) => i.text), ['for B', 'anyone']);
    const pending = req(ctx.base, ctx.token, 'GET', '/inbox?wait=5&session=C');
    await new Promise((r) => setTimeout(r, 100));
    ctx.bridge.enqueue('user.message', { text: 'for D', sessionId: 'D' });
    ctx.bridge.enqueue('user.message', { text: 'for C', sessionId: 'C' });
    const c = await pending;
    assert.deepEqual(c.json.map((i) => i.text), ['for C']);
    const rest = await req(ctx.base, ctx.token, 'GET', '/inbox?wait=0');
    assert.deepEqual(rest.json.map((i) => i.text), ['for A', 'for D']);
  });
});

describe('fake-bridge: task board', () => {
  let ctx;
  before(async () => { ctx = await startBridge(); });
  after(async () => { await ctx.bridge.stop(); });

  const snapshot = (sessionId, tasks) => ({ type: 'task.snapshot', ts: Date.now(), sessionId, tasks });

  test('task.snapshot round trip: each snapshot replaces that session\'s list', async () => {
    const first = [
      { id: '1', subject: 'Add dueDate', status: 'doing', activeForm: 'Adding dueDate', agentId: 'lead-aaaaaaaa' },
      { id: '2', subject: 'Render overdue', status: 'todo', blockedBy: ['1'] },
      { id: '3', subject: 'Old idea', status: 'todo' },
    ];
    let r = await req(ctx.base, ctx.token, 'POST', '/events', { body: [snapshot('aaaaaaaa-1', first), snapshot('bbbbbbbb-2', [{ id: '9', subject: 'Other session', status: 'done' }])] });
    assert.equal(r.status, 200);
    assert.equal(r.json.accepted, 2);
    assert.deepEqual(ctx.bridge.tasks.get('aaaaaaaa-1').map((t) => ({ ...t, sessionId: undefined })), first.map((t) => ({ ...t, sessionId: undefined })));

    // #1 done, #2 in progress, #3 deleted (absent), bad entries ignored, unknown status -> todo
    const second = [
      { id: '1', subject: 'Add dueDate', status: 'done', agentId: 'lead-aaaaaaaa' },
      { id: '2', subject: 'Render overdue', status: 'doing', owner: 'test-runner' },
      { id: '2', subject: 'duplicate', status: 'done' },
      { subject: 'no id', status: 'todo' },
      { id: '4', subject: 'Weird status', status: 'paused' },
    ];
    r = await req(ctx.base, ctx.token, 'POST', '/events', { body: [snapshot('aaaaaaaa-1', second)] });
    assert.equal(r.status, 200);
    const list = ctx.bridge.tasks.get('aaaaaaaa-1');
    assert.deepEqual(list.map((t) => `${t.id}:${t.status}`), ['1:done', '2:doing', '4:todo']);
    assert.equal(list[1].owner, 'test-runner');
    assert.equal(ctx.bridge.tasks.get('bbbbbbbb-2').length, 1, 'other session untouched');
    assert.deepEqual(ctx.bridge.allTasks().map((t) => t.id), ['1', '2', '4', '9']);

    // an empty snapshot clears the session's list
    await req(ctx.base, ctx.token, 'POST', '/events', { body: [snapshot('bbbbbbbb-2', [])] });
    assert.deepEqual(ctx.bridge.tasks.get('bbbbbbbb-2'), []);
  });

  test('state and tasks commands show the three columns; events print', () => {
    const lines = [];
    runCommand(ctx.bridge, 'state', (s) => lines.push(s));
    const text = lines.join('\n');
    assert.match(text, /To do \(1\)/);
    assert.match(text, /In progress \(1\)/);
    assert.match(text, /Done \(1\)/);
    assert.match(text, /#2 Render overdue @test-runner/);
    const only = [];
    runCommand(ctx.bridge, 'tasks', (s) => only.push(s));
    assert.match(only.join('\n'), /Weird status/);
    const printed = formatEvent(snapshot('aaaaaaaa-1', [{ id: '5', subject: 'Blocked one', status: 'todo', blockedBy: ['1'] }]));
    assert.match(printed, /task\.snapshot/);
    assert.match(printed, /#5/);
    assert.match(printed, /Blocked one/);
    assert.match(printed, /blocked by #1/);
  });

  test('derived cards print their source, parent goal and result', () => {
    const cards = [
      { id: 'goal-1', subject: 'Fix the flaky test', status: 'done', source: 'goal', result: 'interrupted' },
      { id: 'agent-a1', subject: 'Find the flake', status: 'doing', source: 'agent', parent: 'goal-1', owner: 'Explore' },
      { id: 'game-tc1', subject: 'Write a changelog', status: 'todo', source: 'game' },
    ];
    const printed = formatEvent(snapshot('cccccccc-3', cards));
    assert.match(printed, /\[goal\]/);
    assert.match(printed, /\[agent\]/);
    assert.match(printed, /\[game\]/);
    assert.match(printed, /for #goal-1/);
    assert.match(printed, /\(interrupted\)/);
    ctx.bridge._applyEvent(snapshot("cccccccc-3", cards));
    const board = formatTasks(ctx.bridge, 240);
    assert.match(board, /↳ #agent-a1 \[agent\] Find the flake @Explore/);
    assert.match(board, /#game-tc1 \[game\] Write a changelog/);
    assert.match(board, /#goal-1 \[goal\] Fix the flaky test \(interrupted\)/);
  });

  test('task details: the result and changed files print; the board marks cards with a result', () => {
    const cards = [
      {
        id: 'goal-2', subject: 'Add due dates', status: 'done', source: 'goal',
        resultText: 'Done. **3 files changed**,\n12/12 tests pass.',
        changes: [
          { path: 'src/a.ts', additions: 3, deletions: 1, diff: '@@ -1,1 +1,3 @@\n-a\n+b\n+c\n+d' },
          { path: 'test/b.ts', additions: 1, deletions: 1, via: ['agent-a2'], truncated: true },
        ],
        changesOmitted: 2,
      },
    ];
    const printed = formatEvent(snapshot('dddddddd-4', cards));
    assert.match(printed, /✓ Done\. \*\*3 files changed\*\*, 12\/12 tests pass\./);
    assert.match(printed, /changes: src\/a\.ts \+3 -1, test\/b\.ts \+1 -1 via agent-a2 \(cut\) \+2 more/);
    ctx.bridge._applyEvent(snapshot('dddddddd-4', cards));
    assert.match(formatTasks(ctx.bridge, 300), /#goal-2 \[goal\] Add due dates ✓ \(4 files\)/);
  });
});
