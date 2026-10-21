// Task details (task.snapshot resultText / changes / timestamps): what a card's turn answered and
// which files it changed, attributed to the right card, within the caps, and never with a home path.
import { describe, expect, mock, test } from 'claude-code/testing'
import { HOME, ofType, world } from './world'
import type { World } from './world'
import { editPatch } from '../hooks/protocol'
import {
  CHANGE_FILES_MAX,
  CHANGE_LINES_MAX,
  RESULT_MAX,
  RESULT_OLD_MAX,
  RICH_DONE_MAX,
  SNAPSHOT_DIFF_BUDGET,
  TaskBook,
} from '../hooks/tasks'
import type { CardChange } from '../hooks/tasks'

type Card = {
  id: string
  status: string
  parent?: string
  resultText?: string
  changes?: CardChange[]
  changesOmitted?: number
  createdAt?: number
  startedAt?: number
  doneAt?: number
}
const board = (w: World) => (ofType(w, 'task.snapshot').at(-1)?.tasks as Card[] | undefined) ?? []
const card = (w: World, id: string) => board(w).find(c => c.id === id)
const START = { cwd: '/work/todo-app', surface: 'terminal', isInteractive: true } as const
const typed = (text: string) => ({ text, wait: false, origin: { kind: 'composer' as const } })
const complete = (turnId: string, answer: string, agentId?: string) => ({
  answer,
  durationMs: 1,
  isAborted: false,
  reason: 'answer' as const,
  turnId,
  ...(agentId !== undefined ? { agentId } : {}),
})
const editResult = (oldStart: number, lines: string[]) => ({
  result: {
    filePath: 'x',
    oldString: '',
    newString: '',
    originalFile: null,
    structuredPatch: [
      {
        oldStart,
        oldLines: lines.filter(l => !l.startsWith('+')).length,
        newStart: oldStart,
        newLines: lines.filter(l => !l.startsWith('-')).length,
        lines,
      },
    ],
    userModified: false,
    replaceAll: false,
  },
  text: 'ok',
})

describe('task details: results and changes', () => {
  test('goal and subagent cards carry their final answer and the files their turns changed', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    on('agent.spawn', () => ({ model: 'claude-test', agentId: 'agent-1' }))
    on('tool.call', ($, e) => {
      if (e.tool === 'Edit' && e.file_path.endsWith('model.ts')) return editResult(3, [' a', '-b', '+B', ' c'])
      if (e.tool === 'Edit') return editResult(10, ['-old', '+new', '+more'])
      return { result: 'ok', text: 'ok' }
    })
    await $.session.start(START)
    await clock.advance(1_000)

    await $.prompt.submit(typed('Add due dates'))
    await $.turn.start({ text: 'Add due dates', turnId: 't1' })
    await $.tool.call({ tool: 'Edit', file_path: '/work/todo-app/src/model.ts', old_string: 'b', new_string: 'B' })
    await $.tool.call({ tool: 'Edit', file_path: '/work/todo-app/src/model.ts', old_string: 'x', new_string: 'y' })
    await $.agent.spawn({
      tool_use_id: 'tu1',
      prompt: 'Fix the list test',
      description: 'Fix the list test',
      subagentType: 'general-purpose',
      provider: { plugin: 'engine', tier: 'core' },
      parentModel: 'claude-test',
      background: false,
      fork: false,
    })
    // a subagent's call: the engine sets agentId on tool.call (not part of $.tool.call's own input type)
    const subEdit = { tool: 'Edit' as const, file_path: '/work/todo-app/test/list.test.ts', old_string: 'old', new_string: 'new' }
    await $.tool.call(Object.assign(subEdit, { agentId: 'agent-1' }))
    await $.tool.call({ tool: 'Read', file_path: '/work/todo-app/src/ui.ts' })
    await $.turn.complete(complete('ts1', 'Updated the **snapshot** in `test/list.test.ts`.', 'agent-1'))
    await clock.advance(300)

    const sub = card(w, 'agent-agent-1')
    expect(sub).toMatchObject({ status: 'done', parent: 'goal-1', resultText: 'Updated the **snapshot** in `test/list.test.ts`.' })
    expect(sub?.changes).toEqual([
      { path: 'test/list.test.ts', additions: 2, deletions: 1, diff: '@@ -10,1 +10,2 @@\n-old\n+new\n+more' },
    ])
    expect(card(w, 'goal-1')?.resultText).toBeUndefined() // still working

    const answer = `Time now: **Wed Oct 21 2026, 07:04 EEST**.\n\n- one\n- two\n${'x'.repeat(RESULT_MAX)}`
    await $.turn.complete(complete('t1', answer))
    await clock.advance(300)
    const goal = card(w, 'goal-1')
    expect(goal?.status).toBe('done')
    expect(goal?.resultText?.startsWith('Time now: **Wed Oct 21 2026, 07:04 EEST**.\n\n- one\n- two\n')).toBe(true)
    expect(goal?.resultText?.length).toBe(RESULT_MAX)
    expect(goal?.resultText?.endsWith('…')).toBe(true)
    // the main loop's two edits of one file are merged (hunks in call order, counts summed);
    // the subagent's edit is listed too, marked via its card
    expect(goal?.changes).toEqual([
      { path: 'src/model.ts', additions: 2, deletions: 2, diff: '@@ -3,3 +3,3 @@\n a\n-b\n+B\n c\n@@ -3,3 +3,3 @@\n a\n-b\n+B\n c' },
      { path: 'test/list.test.ts', additions: 2, deletions: 1, diff: '@@ -10,1 +10,2 @@\n-old\n+new\n+more', via: ['agent-agent-1'] },
    ])
    expect(typeof goal?.createdAt).toBe('number')
    expect(typeof goal?.startedAt).toBe('number')
    expect(typeof goal?.doneAt).toBe('number')
  })

  test('errored or denied edits change nothing; edits outside a turn are not attributed', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    on('tool.call', ($, e) =>
      e.tool === 'Edit' && e.old_string === 'boom' ? { result: 'no match', text: 'String not found', isError: true } : { result: 'ok', text: 'ok' },
    )
    await $.session.start(START)
    await clock.advance(1_000)
    await $.tool.call({ tool: 'Edit', file_path: '/work/todo-app/a.ts', old_string: 'q', new_string: 'r' }) // no turn yet
    await $.prompt.submit(typed('Go'))
    await $.turn.start({ text: 'Go', turnId: 't1' })
    await $.tool.call({ tool: 'Edit', file_path: '/work/todo-app/a.ts', old_string: 'boom', new_string: 'x' })
    await $.tool.call({ tool: 'Write', file_path: '/work/todo-app/b.ts', content: 'one\ntwo' })
    await $.turn.complete(complete('t1', 'Done.'))
    await clock.advance(300)
    // without structuredPatch the input is used: Write's content as added lines
    expect(card(w, 'goal-1')?.changes).toEqual([{ path: 'b.ts', additions: 2, deletions: 0, diff: '@@\n+one\n+two' }])
  })

  test('privacy: paths outside the project and home paths in answers and diffs never reach the game', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    on('tool.call', () => ({ result: 'ok', text: 'ok' }))
    await $.session.start(START)
    await clock.advance(1_000)
    await $.prompt.submit(typed('Take notes'))
    await $.turn.start({ text: 'Take notes', turnId: 't1' })
    await $.tool.call({ tool: 'Write', file_path: `${HOME}/notes/todo.md`, content: `see ${HOME}/secret and /Users/alice/x` })
    await $.turn.complete(complete('t1', `Wrote ${HOME}/notes/todo.md (also /Users/alice/y).`))
    await clock.advance(300)
    const goal = card(w, 'goal-1')
    expect(goal?.changes?.[0]?.path).toBe('~/notes/todo.md')
    expect(goal?.resultText).toBe('Wrote ~/notes/todo.md (also ~/y).')
    const sent = JSON.stringify(ofType(w, 'task.snapshot'))
    expect(sent.includes(HOME)).toBe(false)
    expect(sent.includes('/Users/alice')).toBe(false)
    expect(sent.includes('tester')).toBe(false)
  })
})

describe('task details: caps (pure)', () => {
  const patch = (path: string, n = 1) => ({
    path,
    lines: ['@@', ...Array.from({ length: n }, (_, i) => `+line ${i}`)],
    additions: n,
    deletions: 0,
  })

  test('files per card, lines per file, older done cards, and the snapshot diff budget', () => {
    const book = new TaskBook(() => 42)
    book.promptSubmitted('s', { text: 'big change' })
    book.turnStarted('s', 't0', 'big change', undefined, 'lead')
    for (let i = 0; i < CHANGE_FILES_MAX + 5; i++) book.fileChanged('s', undefined, patch(`f${i}.ts`))
    book.fileChanged('s', undefined, patch('f0.ts', CHANGE_LINES_MAX + 50))
    book.turnCompleted('s', 't0', undefined, 'answer', 'ok')
    let first = book.list('s').find(c => c.id === 'goal-1')
    expect(first?.changes).toHaveLength(CHANGE_FILES_MAX)
    expect(first?.changesOmitted).toBe(5)
    const f0 = first?.changes?.[0]
    expect(f0).toMatchObject({ path: 'f0.ts', additions: CHANGE_LINES_MAX + 51, truncated: true })
    expect(f0?.diff?.split('\n')).toHaveLength(CHANGE_LINES_MAX)
    expect(first).toMatchObject({ createdAt: 42, startedAt: 42, doneAt: 42 })

    // RICH_DONE_MAX newer done cards: goal-1 keeps a short result and loses its changes
    for (let i = 2; i <= RICH_DONE_MAX + 1; i++) {
      book.promptSubmitted('s', { text: `goal ${i}` })
      book.turnStarted('s', `t${i}`, `goal ${i}`, undefined, 'lead')
      book.fileChanged('s', undefined, patch(`g${i}.ts`))
      book.turnCompleted('s', `t${i}`, undefined, 'answer', 'y'.repeat(RESULT_MAX + 10))
    }
    const list = book.list('s')
    first = list.find(c => c.id === 'goal-1')
    expect(first?.resultText).toBe('ok')
    expect(first?.changes).toBeUndefined()
    const second = list.find(c => c.id === 'goal-2')
    expect(second?.resultText?.length).toBe(RESULT_MAX)
    expect(second?.changes).toHaveLength(1)

    book.promptSubmitted('s', { text: 'one more' })
    book.turnStarted('s', 'tz', 'one more', undefined, 'lead')
    book.turnCompleted('s', 'tz', undefined, 'answer', 'z')
    expect(book.list('s').find(c => c.id === 'goal-2')?.resultText?.length).toBe(RESULT_OLD_MAX)

    // the whole snapshot's diff text stays within the budget; what does not fit keeps path + counts
    const big = new TaskBook()
    for (let g = 1; g <= 15; g++) {
      big.promptSubmitted('b', { text: `g${g}` })
      big.turnStarted('b', `t${g}`, `g${g}`, undefined, 'lead')
      for (let f = 0; f < CHANGE_FILES_MAX; f++) {
        big.fileChanged('b', undefined, { path: `p${f}.ts`, lines: ['@@', `+${'w'.repeat(5_000)}`, `+${'w'.repeat(5_000)}`], additions: 2, deletions: 0 })
      }
      big.turnCompleted('b', `t${g}`, undefined, 'answer', 'done')
    }
    const changes = big.list('b').flatMap(c => c.changes ?? [])
    const total = changes.reduce((n, c) => n + (c.diff?.length ?? 0), 0)
    expect(total).toBeLessThanOrEqual(SNAPSHOT_DIFF_BUDGET)
    expect(changes.some(c => c.diff === undefined && c.truncated === true && c.additions === 2)).toBe(true)
  })

  test('a subagent edit seen before its spawn answered is kept for its card', () => {
    const book = new TaskBook()
    book.promptSubmitted('s', { text: 'go' })
    book.turnStarted('s', 't1', 'go', undefined, 'lead')
    book.fileChanged('s', 'agent-9', patch('early.ts', 2))
    book.turnCompleted('s', 'ta', 'agent-9', 'answer', 'early report')
    book.agentSpawned('s', { agentId: 'agent-9', toolUseId: 'tu9', description: 'Early bird' })
    const list = book.list('s')
    expect(list.find(c => c.id === 'agent-agent-9')).toMatchObject({
      status: 'done',
      resultText: 'early report',
      changes: [{ path: 'early.ts', additions: 2, deletions: 0, diff: '@@\n+line 0\n+line 1' }],
    })
    expect(list.find(c => c.id === 'goal-1')?.changes?.[0]?.via).toEqual(['agent-agent-9'])
  })

  test('editPatch: structuredPatch, fallbacks per tool, staged edits and paths', () => {
    expect(editPatch('Read', { file_path: '/a' }, undefined, '/w')).toBeUndefined()
    expect(editPatch('Edit', { file_path: '/w/a.ts', old_string: 'x', new_string: 'y' }, { staged: true }, '/w')).toBeUndefined()
    expect(editPatch('Edit', { file_path: '/w/a.ts', old_string: 'x', new_string: 'y' }, undefined, '/w')).toEqual({
      path: 'a.ts',
      lines: ['@@', '-x', '+y'],
      additions: 1,
      deletions: 1,
    })
    const json = JSON.stringify({ structuredPatch: [{ oldStart: 1, oldLines: 1, newStart: 1, newLines: 1, lines: ['-a', '+b'] }] })
    expect(editPatch('MultiEdit', { file_path: '/w/m.ts', edits: [] }, json, '/w')?.lines).toEqual(['@@ -1,1 +1,1 @@', '-a', '+b'])
    expect(editPatch('Write', { file_path: '/w/n.ts', content: 'a\nb' }, { type: 'create', structuredPatch: [] }, '/w')?.lines).toEqual([
      '@@ -0,0 +1,2 @@',
      '+a',
      '+b',
    ])
    expect(
      editPatch('NotebookEdit', { notebook_path: '/w/n.ipynb', cell_id: 'c1', new_source: 'print(2)' }, { old_source: 'print(1)' }, '/w'),
    ).toEqual({ path: 'n.ipynb', lines: ['@@ cell c1 @@', '-print(1)', '+print(2)'], additions: 1, deletions: 1 })
    expect(editPatch('Edit', { file_path: '/Users/bob/elsewhere/z.ts', old_string: 'a', new_string: 'b' }, undefined, '/w')?.path).toBe(
      '~/elsewhere/z.ts',
    )
  })
})
