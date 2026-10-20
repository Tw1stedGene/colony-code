// Protocol v0.2 (milestone 2): the fake bridge's new events and commands, and the plugin's git
// review code (plugin/hooks/review.ts, loaded through Node's TypeScript type stripping) run against
// a real temporary git repository.
import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { startBridge, req } from './helpers.mjs';
import { runCommand, formatEvent, formatDiff } from '../fake-bridge.mjs';
import { listWorktrees, diffFor, review } from '../../plugin/hooks/review.ts';

const SID = 'aaaabbbb-1111-4222-8333-444455556666';
const OTHER = 'ccccdddd-1111-4222-8333-444455556666';

describe('fake-bridge: v0.2 events', () => {
  let ctx;
  before(async () => { ctx = await startBridge(); });
  after(async () => { await ctx.bridge.stop(); });
  const post = (events) => req(ctx.base, ctx.token, 'POST', '/events', { body: events.map((e) => ({ ts: Date.now(), sessionId: SID, ...e })) });

  test('tracks labels, usage, worktrees, diffs and merge results; prints each', async () => {
    const r = await post([
      { type: 'session.start', cwd: '/w/todo-app', repo: 'todo-app', label: 'todo-app' },
      { type: 'usage.update', inputTokens: 1200, outputTokens: 300, cacheReadTokens: 9000, turns: 1 },
      { type: 'usage.update', inputTokens: 2500, outputTokens: 700, cacheReadTokens: 12000, turns: 2 },
      { type: 'worktree.list', base: 'main', dirty: true, worktrees: [{ id: 'agent-1', path: '/w/.claude/worktrees/agent-1', branch: 'claude/agent-1', ahead: 2, files: 3, additions: 40, deletions: 5, agentId: 'agent-1' }] },
      { type: 'diff.result', requestId: 'req-1', worktree: 'agent-1', files: [{ path: 'src/a.ts', status: 'modified', hunks: [{ header: '@@ -1,2 +1,2 @@', lines: [{ kind: 'del', text: 'old', oldNo: 1 }, { kind: 'add', text: 'new', newNo: 1 }, { kind: 'ctx', text: 'same', oldNo: 2, newNo: 2 }] }] }] },
      { type: 'merge.result', requestId: 'req-2', worktree: 'agent-1', action: 'merge', ok: false, message: 'The main checkout has uncommitted changes.' },
    ]);
    assert.equal(r.json.accepted, 6);
    const b = ctx.bridge;
    assert.equal(b.sessions.get(SID).label, 'todo-app');
    assert.equal(b.usage.get(SID).inputTokens, 2500);
    assert.equal(b.usage.get(SID).turns, 2);
    assert.equal(b.worktrees.get(SID).dirty, true);
    assert.equal(b.diffs.get('req-1').files[0].path, 'src/a.ts');
    assert.equal(b.mergeResults[0].ok, false);

    const printed = b.events.slice(-6).map(formatEvent).join('\n');
    assert.match(printed, /\[todo-app\]/);
    assert.match(printed, /usage\.update.*in 2,500.*out 700.*turns 2/);
    assert.match(printed, /worktree\.list.*base=main.*dirty/);
    assert.match(printed, /agent-1 .*claude\/agent-1/);
    assert.match(printed, /diff\.result.*req-1.*1 file\(s\), 3 line\(s\)/);
    assert.match(printed, /merge\.result.*refused.*uncommitted/);
    const diffText = formatDiff(b.diffs.get('req-1'));
    assert.match(diffText, /-old/);
    assert.match(diffText, /\+new/);
  });

  test('question decisions print their answer hints; Always allow is offered when present', () => {
    const q = formatEvent({ type: 'decision.open', ts: 1, sessionId: SID, decision: { id: 'ask-t1-0', agentId: 'lead-aaaabbbb', kind: 'question', question: 'Which?', options: ['A', 'B'], multiSelect: true, allowText: true, context: 'H\n- A: a' } });
    assert.match(q, /multiSelect allowText/);
    assert.match(q, /answer ask-t1-0 <n>\[,<n>\.\.\.\]/);
    assert.match(q, /text ask-t1-0/);
    const p = formatEvent({ type: 'decision.open', ts: 1, sessionId: SID, decision: { id: 'perm-9', agentId: 'x', kind: 'permission', question: 'Run: ls', options: ['Allow once', 'Always allow', 'Deny'] } });
    assert.match(p, /always perm-9/);
  });

  test('commands: answer (multiSelect), text, always, task, diff, merge, reject, changes, usage, worktrees, showdiff', async () => {
    const b = ctx.bridge;
    const out = [];
    const o = (s) => out.push(s);
    await post([
      { type: 'decision.open', decision: { id: 'ask-t2-0', agentId: 'lead-aaaabbbb', kind: 'question', question: 'Views?', options: ['List', 'Calendar', 'Export'], multiSelect: true, allowText: true } },
      { type: 'decision.open', decision: { id: 'ask-t2-1', agentId: 'lead-aaaabbbb', kind: 'question', question: 'Name?', options: ['a', 'b'], multiSelect: false, allowText: true } },
      { type: 'decision.open', decision: { id: 'perm-a1', agentId: 'lead-aaaabbbb', kind: 'permission', question: 'Run: npm i', options: ['Allow once', 'Always allow', 'Deny'] } },
      { type: 'decision.open', decision: { id: 'perm-a2', agentId: 'lead-aaaabbbb', kind: 'permission', question: 'Run: ls', options: ['Allow once', 'Deny'] } },
    ]);
    runCommand(b, 'answer ask-t2-0 1,Export -- and the widget', o);
    let r = await req(ctx.base, ctx.token, 'GET', '/decision/ask-t2-0?wait=0');
    assert.deepEqual(r.json, { status: 'answered', option: 'List\nExport', text: 'and the widget' });
    runCommand(b, 'text ask-t2-1 call it Bob', o);
    r = await req(ctx.base, ctx.token, 'GET', '/decision/ask-t2-1?wait=0');
    assert.deepEqual(r.json, { status: 'answered', option: '', text: 'call it Bob' });
    runCommand(b, 'always perm-a2', o);
    assert.equal(b.decisions.get('perm-a2').status, 'open', 'not offered there');
    runCommand(b, 'always perm-a1', o);
    assert.equal(b.decisions.get('perm-a1').option, 'Always allow');

    await post([{ type: 'session.start', cwd: '/w/other', label: 'other', sessionId: OTHER }].map((e) => ({ ...e })));
    runCommand(b, 'task s=aaaab Write the changelog', o);
    runCommand(b, 'diff', o);
    runCommand(b, 'diff s=cccc agent-1', o);
    runCommand(b, 'merge s=aaaab agent-1', o);
    runCommand(b, 'reject agent-1', o);
    runCommand(b, 'changes agent-1 please add tests', o);
    runCommand(b, 'task s=zzzz nope', o);
    assert.match(out.at(-1), /no single session/);
    r = await req(ctx.base, ctx.token, 'GET', '/inbox?wait=0');
    const items = r.json;
    assert.deepEqual(items.map((i) => i.type), ['task.create', 'diff.request', 'diff.request', 'merge.request', 'merge.request', 'merge.request']);
    assert.deepEqual([items[0].sessionId, items[0].text], [SID, 'Write the changelog']);
    assert.equal(items[1].worktree, undefined);
    assert.equal(items[1].sessionId, undefined);
    assert.deepEqual([items[2].sessionId, items[2].worktree], [OTHER, 'agent-1']);
    assert.equal(typeof items[2].requestId, 'string');
    assert.deepEqual([items[3].action, items[3].sessionId], ['merge', SID]);
    assert.equal(items[4].action, 'reject');
    assert.deepEqual([items[5].action, items[5].text], ['request_changes', 'please add tests']);
    assert.equal(new Set(items.filter((i) => i.requestId).map((i) => i.requestId)).size, 5, 'request ids are unique');

    out.length = 0;
    runCommand(b, 'usage', o);
    runCommand(b, 'worktrees', o);
    runCommand(b, 'showdiff', o);
    runCommand(b, 'reviews', o);
    runCommand(b, 'state', o);
    const text = out.join('\n');
    assert.match(text, /\[todo-app\].*in 2,500/);
    assert.match(text, /base=main/);
    assert.match(text, /diff req-1/);
    assert.match(text, /req-2.*merge.*refused/);
    assert.match(text, /\[todo-app\] Claude|\[todo-app\]/);
  });
});

// ---- the plugin's git review code against a real repository ------------------------------

/** review.ts's Runner over Node's execFile (argv, no shell), like $.process.run. */
const run = (argv, { cwd, env, timeoutMs }) =>
  new Promise((resolve, reject) => {
    execFile(argv[0], argv.slice(1), { cwd, env: { ...process.env, ...env }, timeout: timeoutMs, maxBuffer: 16 << 20 }, (err, stdout, stderr) => {
      if (err && typeof err.code !== 'number') return reject(err);
      resolve({ exitCode: err ? err.code : 0, stdout, stderr });
    });
  });

const sh = (cwd, ...args) =>
  new Promise((resolve, reject) => {
    execFile('git', args, { cwd, env: { ...process.env, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_SYSTEM: '/dev/null' } }, (err, stdout, stderr) =>
      err ? reject(new Error(`git ${args.join(' ')}: ${stderr}`)) : resolve(stdout));
  });

describe('plugin review.ts against a real git repository', () => {
  let tmp;
  let root;
  const write = (p, text) => { fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, text); };

  before(async () => {
    tmp = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'colony-review-')));
    root = path.join(tmp, 'todo-app');
    fs.mkdirSync(root);
    await sh(root, 'init', '-q', '-b', 'main');
    await sh(root, 'config', 'user.email', 'player@example.com');
    await sh(root, 'config', 'user.name', 'Player');
    await sh(root, 'config', 'commit.gpgsign', 'false');
    write(path.join(root, 'src/todo.ts'), 'export type Todo = {\n  id: string\n  title: string\n}\n');
    write(path.join(root, 'README.md'), '# todo\n');
    await sh(root, 'add', '.');
    await sh(root, 'commit', '-q', '-m', 'init');
    // a worker's worktree with one commit and one uncommitted change
    await sh(root, 'worktree', 'add', '-q', '-b', 'claude/agent-a3f9', path.join(root, '.claude/worktrees/agent-a3f9'));
    const wt = path.join(root, '.claude/worktrees/agent-a3f9');
    write(path.join(wt, 'src/todo.ts'), 'export type Todo = {\n  id: string\n  title: string\n  dueDate?: string\n}\n');
    write(path.join(wt, 'src/due.ts'), 'export const due = 1\n');
    await sh(wt, 'add', '.');
    await sh(wt, 'commit', '-q', '-m', 'due dates');
    write(path.join(wt, 'README.md'), '# todo\n\nNow with due dates.\n');
    // a second worktree that will conflict with main
    await sh(root, 'worktree', 'add', '-q', '-b', 'docs-pass', path.join(tmp, 'wt-docs'));
    write(path.join(tmp, 'wt-docs/README.md'), '# TODO APP\n');
    await sh(path.join(tmp, 'wt-docs'), 'commit', '-q', '-am', 'docs');
    write(path.join(root, 'README.md'), '# todo (main)\n');
    await sh(root, 'commit', '-q', '-am', 'main readme');
  });
  after(() => { fs.rmSync(tmp, { recursive: true, force: true }); });

  test('lists worktrees with ahead counts and shortstat; maps agent ids', async () => {
    const list = await listWorktrees(run, path.join(root, 'src'), ['a3f9']);
    assert.equal(list.root, root);
    assert.equal(list.base, 'main');
    assert.equal(list.dirty, false);
    assert.deepEqual(list.worktrees.map((w) => [w.id, w.branch, w.ahead, w.files, w.additions, w.deletions, w.agentId]), [
      ['agent-a3f9', 'claude/agent-a3f9', 1, 2, 2, 0, 'a3f9'],
      ['wt-docs', 'docs-pass', 1, 1, 1, 1, undefined],
    ]);
  });

  test('diffs: the checkout vs HEAD, and a worktree from its merge base incl. uncommitted', async () => {
    write(path.join(root, 'src/todo.ts'), 'export type Todo = {\n  id: string\n  title: string\n  done: boolean\n}\n');
    const list = await listWorktrees(run, root);
    assert.equal(list.dirty, true);
    const main = await diffFor(run, list);
    assert.deepEqual(main.files.map((f) => `${f.status}:${f.path}`), ['modified:src/todo.ts']);
    assert.deepEqual(main.files[0].hunks[0].lines.filter((l) => l.kind === 'add'), [{ kind: 'add', text: '  done: boolean', newNo: 4 }]);
    const wt = await diffFor(run, list, 'agent-a3f9');
    assert.deepEqual(wt.files.map((f) => `${f.status}:${f.path}`).sort(), ['added:src/due.ts', 'modified:README.md', 'modified:src/todo.ts']);
    const none = await diffFor(run, list, 'nope');
    assert.equal(none.error, 'no worktree named nope');

    // dirty: merge refused, nothing changed
    const refused = await review(run, list, 'agent-a3f9', 'merge');
    assert.equal(refused.ok, false);
    assert.match(refused.message, /uncommitted changes/);
    await sh(root, 'checkout', '-q', '--', 'src/todo.ts');
  });

  test('a conflicting merge is aborted and lists the files; a clean one makes a merge commit as the user', async () => {
    let list = await listWorktrees(run, root);
    const head = (await sh(root, 'rev-parse', 'HEAD')).trim();
    const conflict = await review(run, list, 'wt-docs', 'merge');
    assert.equal(conflict.ok, false);
    assert.equal(conflict.message, 'Merging docs-pass conflicts in: README.md. The merge was aborted; nothing changed.');
    assert.equal((await sh(root, 'rev-parse', 'HEAD')).trim(), head);
    assert.equal((await sh(root, 'status', '--porcelain', '--untracked-files=no')).trim(), '');

    const merged = await review(run, list, 'agent-a3f9', 'merge');
    assert.deepEqual(merged, { ok: true, message: 'Merged claude/agent-a3f9 into main.', branch: 'claude/agent-a3f9' });
    const parents = (await sh(root, 'log', '-1', '--format=%P %an')).trim().split(' ');
    assert.equal(parents.length, 3, 'a --no-ff merge commit with two parents');
    assert.equal(parents[2], 'Player');
    list = await listWorktrees(run, root);
    assert.equal(list.worktrees.find((w) => w.id === 'agent-a3f9').ahead, 0);
  });

  test('reject removes the worktree and deletes its branch; request_changes runs no git', async () => {
    let list = await listWorktrees(run, root);
    const changes = await review(run, list, 'wt-docs', 'request_changes');
    assert.deepEqual(changes, { ok: true, message: 'Changes requested on docs-pass.', branch: 'docs-pass' });
    const rejected = await review(run, list, 'wt-docs', 'reject');
    assert.equal(rejected.ok, true);
    assert.equal(fs.existsSync(path.join(tmp, 'wt-docs')), false);
    assert.equal((await sh(root, 'branch', '--list', 'docs-pass')).trim(), '');
    list = await listWorktrees(run, root);
    assert.deepEqual(list.worktrees.map((w) => w.id), ['agent-a3f9']);
    assert.equal((await review(run, list, 'wt-docs', 'reject')).ok, false);
  });

  test('outside a repository: undefined', async () => {
    assert.equal(await listWorktrees(run, tmp), undefined);
  });
});
