// Protocol v0.2 (milestone 2) behaviours: session label, AskUserQuestion in the game, "Always allow",
// usage.update, task.create, and the review flow (worktree.list, diff.result, merge.result).
import { describe, expect, mock, test } from 'claude-code/testing'
import { LEAD, ofType, upserts, world } from './world'
import type { World } from './world'

const START = { cwd: '/work/todo-app', surface: 'terminal', isInteractive: true } as const
const ROOT = '/work/todo-app'
const WT_AGENT = '/work/todo-app/.claude/worktrees/agent-a3f9'
const WT_DOCS = '/work/wt-docs'

const DIFF = [
  'diff --git a/src/todo.ts b/src/todo.ts',
  'index 1111111..2222222 100644',
  '--- a/src/todo.ts',
  '+++ b/src/todo.ts',
  '@@ -1,3 +1,4 @@ export type Todo = {',
  '   id: string',
  '-  title: string',
  '+  title: string',
  '+  dueDate?: Date',
  '   done: boolean',
  'diff --git a/src/new.ts b/src/new.ts',
  'new file mode 100644',
  'index 0000000..3333333',
  '--- /dev/null',
  '+++ b/src/new.ts',
  '@@ -0,0 +1 @@',
  '+export const x = 1',
  '',
].join('\n')

type Repo = { dirty: boolean; conflict: string[]; merged: string[]; removed: string[]; deleted: string[]; aborted: number }

/** A fake repository behind the world's git: main checkout plus two linked worktrees. */
function repo(w: World, init: Partial<Repo> = {}): Repo {
  const r: Repo = { dirty: false, conflict: [], merged: [], removed: [], deleted: [], aborted: 0, ...init }
  const ok = (stdout = '') => ({ exitCode: 0, stdout })
  w.git = args => {
    const a = args.join(' ')
    if (a === 'rev-parse --show-toplevel') return ok(`${ROOT}\n`)
    if (a === 'symbolic-ref --quiet --short HEAD') return ok('main\n')
    if (a === 'status --porcelain --untracked-files=no') return ok(r.dirty ? ' M src/todo.ts\n' : '')
    if (a === 'worktree list --porcelain') {
      const entries = [
        `worktree ${ROOT}\nHEAD aaaa\nbranch refs/heads/main\n`,
        `worktree ${WT_AGENT}\nHEAD bbbb\nbranch refs/heads/claude/agent-a3f9\n`,
        `worktree ${WT_DOCS}\nHEAD cccc\nbranch refs/heads/docs-pass\n`,
        `worktree /work/detached\nHEAD dddd\ndetached\n`,
      ].filter(e => !r.removed.some(p => e.startsWith(`worktree ${p}\n`)))
      return ok(entries.join('\n'))
    }
    if (a.startsWith('rev-list --count main..')) return ok(a.endsWith('docs-pass') ? '1\n' : '3\n')
    if (a.startsWith('diff --shortstat main...')) {
      return ok(a.endsWith('docs-pass') ? ' 1 file changed, 4 insertions(+)\n' : ' 2 files changed, 12 insertions(+), 3 deletions(-)\n')
    }
    if (a.startsWith('merge-base main ')) return ok('base123\n')
    if (a === 'diff --no-color --no-ext-diff -M HEAD' || a === 'diff --no-color --no-ext-diff -M base123') return ok(DIFF)
    if (a.startsWith('merge --no-ff --no-edit ')) {
      if (r.conflict.length > 0) return { exitCode: 1, stdout: 'CONFLICT (content): Merge conflict in src/todo.ts\n' }
      r.merged.push(args[args.length - 1] ?? '')
      return ok("Merge made by the 'ort' strategy.\n")
    }
    if (a === 'diff --name-only --diff-filter=U') return ok(r.conflict.join('\n'))
    if (a === 'rev-parse -q --verify MERGE_HEAD') return r.conflict.length > 0 ? ok('eeee\n') : { exitCode: 1 }
    if (a === 'merge --abort') {
      r.aborted += 1
      return ok()
    }
    if (a.startsWith('worktree remove --force ')) {
      r.removed.push(args[args.length - 1] ?? '')
      return ok()
    }
    if (a.startsWith('branch -D ')) {
      r.deleted.push(args[args.length - 1] ?? '')
      return ok(`Deleted branch ${args[args.length - 1]}\n`)
    }
    return { exitCode: 128, stderr: `fake git: unexpected ${a}` }
  }
  return r
}

const noRemotes = (w: World) => {
  for (const c of w.gitCalls) {
    expect(['push', 'fetch', 'pull', 'remote', 'clone'].includes(c.args[0] ?? '')).toBe(false)
  }
}

describe('session label and worktrees', () => {
  test('session.start carries the repo folder as label; worktree.list follows', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    repo(w)

    await $.session.start(START)
    await clock.advance(2_000)

    expect(ofType(w, 'session.start')[0]).toMatchObject({ label: 'todo-app', repo: 'todo-app' })
    const list = ofType(w, 'worktree.list').at(-1)
    expect(list).toMatchObject({ base: 'main', dirty: false })
    expect(list?.worktrees).toEqual([
      { id: 'agent-a3f9', path: WT_AGENT, branch: 'claude/agent-a3f9', ahead: 3, files: 2, additions: 12, deletions: 3 },
      { id: 'wt-docs', path: WT_DOCS, branch: 'docs-pass', ahead: 1, files: 1, additions: 4, deletions: 0 },
    ])
    // git ran with the session's checkout as cwd, by argv
    expect(w.gitCalls.every(c => c.cwd === ROOT)).toBe(true)
    noRemotes(w)
  })

  test('outside a git repository (or without git) no worktree.list is sent', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true) // no w.git: process.run rejects

    await $.session.start(START)
    await clock.advance(2_000)

    expect(ofType(w, 'session.start')).toHaveLength(1)
    expect(ofType(w, 'worktree.list')).toHaveLength(0)
  })

  test('a fresh worktree.list after each main turn', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    const r = repo(w)

    await $.session.start(START)
    await clock.advance(2_000)
    expect(ofType(w, 'worktree.list')).toHaveLength(1)
    r.dirty = true
    await $.turn.start({ text: 'go', turnId: 't1' })
    await $.turn.complete({ answer: 'ok', turnId: 't1', reason: 'answer', isAborted: false, durationMs: 5 })
    await clock.advance(1_000)
    expect(ofType(w, 'worktree.list')).toHaveLength(2)
    expect(ofType(w, 'worktree.list').at(-1)).toMatchObject({ dirty: true })
  })
})

describe('AskUserQuestion in the game', () => {
  const QUESTIONS = [
    {
      question: 'Which date library?',
      header: 'Library',
      multiSelect: false,
      options: [
        { label: 'date-fns', description: 'Small, tree-shakable' },
        { label: 'dayjs', description: 'Tiny, moment-like API' },
      ],
    },
    {
      question: 'Which views show due dates?',
      header: 'Views',
      multiSelect: true,
      options: [
        { label: 'List', description: 'The main list' },
        { label: 'Calendar', description: 'Month view' },
        { label: 'Export', description: 'CSV export' },
      ],
    },
  ]

  test('every question answered in the game: the tool result, without the terminal dialog', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    let dialogs = 0
    on('tool.call', () => {
      dialogs += 1
      return { result: { questions: [], answers: {} } }
    })
    w.autoAnswer = d =>
      d.question === 'Which date library?' ? { option: 'date-fns' } : { option: 'List\nCalendar', text: 'and the widget' }

    await $.session.start(START)
    await clock.advance(1_000)
    const asked = $.tool.call({ tool: 'AskUserQuestion', questions: QUESTIONS })
    await clock.advance(1_000)
    const ran = await asked

    expect(dialogs).toBe(0)
    expect(ran.result).toEqual({
      questions: QUESTIONS,
      answers: { 'Which date library?': 'date-fns', 'Which views show due dates?': 'List, Calendar, and the widget' },
    })
    const opened = ofType(w, 'decision.open').map(ev => ev.decision as Record<string, unknown>)
    expect(opened).toHaveLength(2)
    expect(String(opened[0]?.id)).toMatch(/^ask-.+-0$/)
    expect(String(opened[1]?.id)).toMatch(/^ask-.+-1$/)
    expect(opened[0]).toMatchObject({
      kind: 'question',
      agentId: LEAD,
      question: 'Which date library?',
      options: ['date-fns', 'dayjs'],
      context: 'Library\n- date-fns: Small, tree-shakable\n- dayjs: Tiny, moment-like API',
      multiSelect: false,
      allowText: true,
    })
    expect(opened[1]).toMatchObject({ multiSelect: true, options: ['List', 'Calendar', 'Export'] })
    expect(ofType(w, 'decision.closed')).toHaveLength(0)
    expect(upserts(w, LEAD).some(a => a.state === 'waiting_user' && a.station === 'user')).toBe(true)
    expect(upserts(w, LEAD).at(-1)?.state).not.toBe('waiting_user')
  })

  test('no answer in 5 minutes: the terminal dialog runs and every id is closed', { timeoutMs: 60_000 }, async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    on('tool.call', () => ({ result: { questions: QUESTIONS, answers: { 'Which date library?': 'dayjs' } } }))

    await $.session.start(START)
    await clock.advance(1_000)
    const asked = $.tool.call({ tool: 'AskUserQuestion', questions: QUESTIONS })
    await clock.advance(5 * 60_000 + 30_000)
    const ran = await asked
    await clock.advance(500)

    expect(ran.result).toEqual({ questions: QUESTIONS, answers: { 'Which date library?': 'dayjs' } })
    const openIds = ofType(w, 'decision.open').map(ev => (ev.decision as { id: string }).id)
    const closedIds = ofType(w, 'decision.closed').map(ev => ev.id)
    expect(closedIds).toEqual(openIds)
  })

  test('the game going away mid-question falls back at once', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    let dialogs = 0
    on('tool.call', () => {
      dialogs += 1
      return { result: { questions: QUESTIONS, answers: {} } }
    })

    await $.session.start(START)
    await clock.advance(1_000)
    const asked = $.tool.call({ tool: 'AskUserQuestion', questions: QUESTIONS })
    await clock.settle()
    w.isRunning = false
    await clock.advance(26_000)
    await asked
    expect(dialogs).toBe(1)
  })

  test('without the game the question goes straight to the terminal', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, false)
    let dialogs = 0
    on('tool.call', () => {
      dialogs += 1
      return { result: { questions: QUESTIONS, answers: {} } }
    })
    await $.session.start(START)
    await clock.advance(1_000)
    await $.tool.call({ tool: 'AskUserQuestion', questions: QUESTIONS })
    expect(dialogs).toBe(1)
    expect(w.requests).toHaveLength(0)
  })
})

describe('permission: Always allow', () => {
  const SUGGESTIONS = [
    {
      type: 'addRules' as const,
      rules: [{ toolName: 'Bash', ruleContent: 'npm install:*' }],
      behavior: 'allow' as const,
      destination: 'localSettings' as const,
    },
  ]

  test('offered with permission_suggestions; answering it adds exactly those rules', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    on('classic.PermissionRequest', () => ({}))

    await $.session.start(START)
    await clock.advance(1_000)
    const asked = $.classic.PermissionRequest({
      tool_name: 'Bash',
      tool_input: { command: 'npm install left-pad' },
      permission_suggestions: SUGGESTIONS,
    })
    await clock.settle()
    const open = ofType(w, 'decision.open')[0]?.decision as { id: string; options: string[]; context: Record<string, unknown> }
    expect(open.options).toEqual(['Allow once', 'Always allow', 'Deny'])
    expect(Object.keys(open.context)).toEqual(['tool', 'input', 'Always allow covers'])
    expect(open.context['Always allow covers']).toBe('allow Bash(npm install:*) for this project (your local settings)')

    w.decisions.set(open.id, { status: 'answered', option: 'Always allow' })
    await clock.advance(26_000)
    expect((await asked).decision).toEqual({ behavior: 'allow', updatedPermissions: SUGGESTIONS })
  })

  test('not offered without suggestions', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    on('classic.PermissionRequest', () => ({}))

    await $.session.start(START)
    await clock.advance(1_000)
    const asked = $.classic.PermissionRequest({ tool_name: 'Bash', tool_input: { command: 'ls' }, permission_suggestions: [] })
    await clock.settle()
    const open = ofType(w, 'decision.open')[0]?.decision as { id: string; options: string[]; context: Record<string, unknown> }
    expect(open.options).toEqual(['Allow once', 'Deny'])
    expect(Object.keys(open.context)).toEqual(['tool', 'input'])
    w.decisions.set(open.id, { status: 'answered', option: 'Allow once' })
    await clock.advance(26_000)
    expect((await asked).decision).toEqual({ behavior: 'allow' })
  })
})

describe('usage and tasks from the game', () => {
  test('usage.update is cumulative per session, after each turn that carried usage', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)

    await $.session.start(START)
    await clock.advance(1_000)
    const u = (input: number, output: number, read: number, created: number) => ({
      input_tokens: input,
      output_tokens: output,
      cache_read_input_tokens: read,
      cache_creation_input_tokens: created,
      model: 'claude-test',
    })
    await $.turn.complete({ answer: 'a', turnId: 't1', reason: 'answer', isAborted: false, durationMs: 1, usage: u(100, 50, 1000, 10) })
    await $.turn.complete({ answer: '', turnId: 't2', reason: 'aborted', isAborted: true, durationMs: 1 })
    await $.turn.complete({ answer: 'w', turnId: 't3', agentId: 'agent-7', reason: 'answer', isAborted: false, durationMs: 1, usage: u(5, 5, 0, 0) })
    await $.turn.complete({ answer: 'b', turnId: 't4', reason: 'answer', isAborted: false, durationMs: 1, usage: u(200, 25, 500, 0) })
    await clock.advance(500)

    const updates = ofType(w, 'usage.update')
    expect(updates).toHaveLength(3)
    expect(updates.at(-1)).toMatchObject({ inputTokens: 315, outputTokens: 80, cacheReadTokens: 1500, turns: 2 })
    expect(updates[0]).toMatchObject({ inputTokens: 110, outputTokens: 50, cacheReadTokens: 1000, turns: 1 })
    expect(updates.every(ev => !JSON.stringify(ev).toLowerCase().includes('cost'))).toBe(true)
  })

  test('task.create becomes a prompt asking Claude to add the task', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)

    await $.session.start(START)
    await clock.advance(1_000)
    w.inbox.push(
      { id: 'tc1', type: 'task.create', ts: 1, text: 'Write a changelog entry' },
      { id: 'tc2', type: 'task.create', ts: 2, sessionId: 'someone-else', text: 'not mine' },
    )
    await clock.advance(2_000)
    expect(w.prompts).toEqual([
      '[RimWorld task] Work on this when appropriate (add it to your task list if you have one): Write a changelog entry',
    ])
  })
})

describe('review: diffs and merges', () => {
  test('diff.request for the checkout and for a worktree answer with parsed diff.result', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    repo(w)

    await $.session.start(START)
    await clock.advance(1_000)
    w.inbox.push(
      { id: 'd1', type: 'diff.request', ts: 1, requestId: 'req-main' },
      { id: 'd2', type: 'diff.request', ts: 2, requestId: 'req-wt', worktree: 'agent-a3f9' },
      { id: 'd3', type: 'diff.request', ts: 3, requestId: 'req-none', worktree: 'nope' },
    )
    await clock.advance(3_000)

    const results = ofType(w, 'diff.result')
    expect(results.map(r => r.requestId)).toEqual(['req-main', 'req-wt', 'req-none'])
    const main = results[0] as unknown as { worktree?: string; files: { path: string; status: string; hunks: { header: string; lines: unknown[] }[] }[] }
    expect(main.worktree).toBeUndefined()
    expect(main.files.map(f => `${f.status}:${f.path}`)).toEqual(['modified:src/todo.ts', 'added:src/new.ts'])
    expect(main.files[0]?.hunks[0]?.header).toBe('@@ -1,3 +1,4 @@ export type Todo = {')
    expect(main.files[0]?.hunks[0]?.lines).toEqual([
      { kind: 'ctx', text: '  id: string', oldNo: 1, newNo: 1 },
      { kind: 'del', text: '  title: string', oldNo: 2 },
      { kind: 'add', text: '  title: string', newNo: 2 },
      { kind: 'add', text: '  dueDate?: Date', newNo: 3 },
      { kind: 'ctx', text: '  done: boolean', oldNo: 3, newNo: 4 },
    ])
    expect(results[1]).toMatchObject({ worktree: 'agent-a3f9' })
    // the worktree's diff runs in the worktree, from the merge base (committed + uncommitted)
    expect(w.gitCalls.some(c => c.cwd === WT_AGENT && c.args.join(' ') === 'diff --no-color --no-ext-diff -M base123')).toBe(true)
    expect(results[2]).toMatchObject({ files: [], error: 'no worktree named nope' })
    noRemotes(w)
  })

  test('merge: a --no-ff merge into the checkout, then merge.result and a fresh worktree.list', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    const r = repo(w)

    await $.session.start(START)
    await clock.advance(2_000)
    const before = ofType(w, 'worktree.list').length
    w.inbox.push({ id: 'm1', type: 'merge.request', ts: 1, requestId: 'r1', worktree: 'agent-a3f9', action: 'merge' })
    await clock.advance(3_000)

    expect(r.merged).toEqual(['claude/agent-a3f9'])
    expect(ofType(w, 'merge.result')).toEqual([
      expect.objectContaining({ requestId: 'r1', worktree: 'agent-a3f9', action: 'merge', ok: true, message: 'Merged claude/agent-a3f9 into main.' }),
    ])
    const types = w.events.map(ev => ev.type)
    expect(types.lastIndexOf('worktree.list')).toBeGreaterThan(types.indexOf('merge.result'))
    expect(ofType(w, 'worktree.list').length).toBeGreaterThan(before)
    noRemotes(w)
  })

  test('merge is refused while the checkout is dirty; a conflict is aborted and listed', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    const r = repo(w, { dirty: true })

    await $.session.start(START)
    await clock.advance(1_000)
    w.inbox.push({ id: 'm1', type: 'merge.request', ts: 1, requestId: 'dirty', worktree: 'wt-docs', action: 'merge' })
    await clock.advance(3_000)
    r.dirty = false
    r.conflict = ['src/todo.ts', 'README.md']
    w.inbox.push({ id: 'm2', type: 'merge.request', ts: 2, requestId: 'conflict', worktree: 'wt-docs', action: 'merge' })
    await clock.advance(3_000)

    const results = ofType(w, 'merge.result')
    expect(results[0]).toMatchObject({ requestId: 'dirty', ok: false })
    expect(String(results[0]?.message)).toMatch(/uncommitted changes/)
    expect(results[1]).toMatchObject({ requestId: 'conflict', ok: false })
    expect(String(results[1]?.message)).toBe('Merging docs-pass conflicts in: src/todo.ts, README.md. The merge was aborted; nothing changed.')
    expect(r.aborted).toBe(1)
    expect(r.merged).toEqual([])
  })

  test('reject removes the worktree and deletes its branch; request_changes prompts Claude', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    const r = repo(w)

    await $.session.start(START)
    await clock.advance(1_000)
    w.inbox.push(
      { id: 'x1', type: 'merge.request', ts: 1, requestId: 'rc', worktree: 'agent-a3f9', action: 'request_changes', text: 'add tests for overdue' },
      { id: 'x2', type: 'merge.request', ts: 2, requestId: 'rj', worktree: 'wt-docs', action: 'reject' },
    )
    await clock.advance(4_000)

    expect(w.prompts).toEqual(['[RimWorld review] Changes requested on claude/agent-a3f9: add tests for overdue'])
    expect(r.removed).toEqual([WT_DOCS])
    expect(r.deleted).toEqual(['docs-pass'])
    const results = ofType(w, 'merge.result')
    expect(results.map(x => `${x.requestId}:${x.ok}`)).toEqual(['rc:true', 'rj:true'])
    const last = ofType(w, 'worktree.list').at(-1)?.worktrees as { id: string }[]
    expect(last.map(x => x.id)).toEqual(['agent-a3f9'])
    noRemotes(w)
  })
})

describe('integration fixes (milestone 2)', () => {
  test('a "text" question without options does not crash: it is asked with no options', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    let dialogs = 0
    on('tool.call', () => {
      dialogs += 1
      return { result: { questions: [], answers: {} } }
    })
    w.autoAnswer = () => ({ option: '', text: 'Friday' })
    const questions = [{ question: 'When is it due?', header: 'Due', kind: 'text', multiSelect: false }] as never

    await $.session.start(START)
    await clock.advance(1_000)
    const asked = $.tool.call({ tool: 'AskUserQuestion', questions })
    await clock.advance(1_000)
    const ran = await asked

    expect(dialogs).toBe(0)
    expect(ran.result).toEqual({ questions, answers: { 'When is it due?': 'Friday' } })
    expect((ofType(w, 'decision.open')[0]?.decision as { options: string[] }).options).toEqual([])
  })

  test('request_changes with blank text is refused and prompts nothing', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    repo(w)

    await $.session.start(START)
    await clock.advance(1_000)
    w.inbox.push({ id: 'b1', type: 'merge.request', ts: 1, requestId: 'blank', worktree: 'agent-a3f9', action: 'request_changes', text: '  ' })
    await clock.advance(3_000)

    expect(w.prompts).toEqual([])
    expect(ofType(w, 'merge.result')).toEqual([
      expect.objectContaining({ requestId: 'blank', ok: false, message: 'No change request text.' }),
    ])
  })

  test('a headless (-p) session reports events but never polls the inbox', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    repo(w)

    await $.session.start({ cwd: ROOT, surface: null, isInteractive: false })
    w.inbox.push({ id: 'g1', type: 'goal.submit', ts: 1, text: 'do not run me' })
    await clock.advance(5_000)

    expect(ofType(w, 'session.start')).toHaveLength(1)
    expect(w.inboxPaths).toEqual([])
    expect(w.prompts).toEqual([])
  })
})
