// Task board cards the plugin derives itself: goal-<n> per user
// prompt, agent-<id> per subagent, game-<inbox id> per task.create; Claude's own task tools still merge.
import { describe, expect, mock, test } from 'claude-code/testing'
import { LEAD, ofType, world } from './world'
import type { World } from './world'
import { DONE_CARDS_MAX, TaskBook, cardSubject, stripPluginPrefix } from '../hooks/tasks'

type Card = {
  id: string
  subject: string
  status: string
  source?: string
  parent?: string
  result?: string
  description?: string
  owner?: string
  agentId?: string
}
const board = (w: World) => (ofType(w, 'task.snapshot').at(-1)?.tasks as Card[] | undefined) ?? []
const card = (w: World, id: string) => board(w).find(c => c.id === id)
const START = { cwd: '/work/todo-app', surface: 'terminal', isInteractive: true } as const
const typed = (text: string, turnId?: string) => ({
  text,
  wait: false,
  origin: { kind: 'composer' as const },
  ...(turnId !== undefined ? { turnId } : {}),
})
const done = (turnId: string, reason: 'answer' | 'aborted' | 'error' | 'refusal' = 'answer', agentId?: string) => {
  const base = { answer: 'ok', durationMs: 1, isAborted: reason === 'aborted', turnId, ...(agentId !== undefined ? { agentId } : {}) }
  return reason === 'refusal'
    ? { ...base, reason, refusal: { category: null, explanation: null } }
    : { ...base, reason }
}

describe('goal cards', () => {
  test('a prompt becomes goal-1: todo, doing when its turn starts, done when the main turn completes', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    await $.session.start(START)
    await clock.advance(1_000)

    await $.prompt.submit(typed('Add due dates to todos\nand show overdue items in red'))
    await clock.advance(300)
    expect(card(w, 'goal-1')).toEqual({
      id: 'goal-1',
      subject: 'Add due dates to todos',
      status: 'todo',
      source: 'goal',
      description: 'Add due dates to todos\nand show overdue items in red',
    })

    await $.turn.start({ text: 'Add due dates to todos\nand show overdue items in red', turnId: 't1' })
    await clock.advance(300)
    expect(card(w, 'goal-1')).toMatchObject({ status: 'doing', agentId: LEAD })

    // a subagent's turn ending does not end the goal
    await $.turn.complete(done('t-sub', 'answer', 'agent-x'))
    await clock.advance(300)
    expect(card(w, 'goal-1')?.status).toBe('doing')

    await $.turn.complete(done('t1'))
    await clock.advance(300)
    expect(card(w, 'goal-1')).toMatchObject({ status: 'done' })
    expect(card(w, 'goal-1')?.result).toBeUndefined()

    // the next prompt is goal-2; an interrupted turn ends done + interrupted, a failed one + failed
    await $.prompt.submit(typed('Second thing'))
    await $.turn.start({ text: 'Second thing', turnId: 't2' })
    await $.turn.complete(done('t2', 'aborted'))
    await $.prompt.submit(typed('Third thing'))
    await $.turn.start({ text: 'Third thing', turnId: 't3' })
    await $.turn.complete(done('t3', 'error'))
    await clock.advance(300)
    expect(card(w, 'goal-2')).toMatchObject({ subject: 'Second thing', status: 'done', result: 'interrupted' })
    expect(card(w, 'goal-3')).toMatchObject({ subject: 'Third thing', status: 'done', result: 'failed' })
  })

  test('a prompt typed during a turn waits in todo until its own turn starts', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    await $.session.start(START)
    await clock.advance(1_000)

    await $.prompt.submit(typed('First'))
    await $.turn.start({ text: 'First', turnId: 't1' })
    await $.prompt.submit(typed('Queued one', 't1'))
    await clock.advance(300)
    expect(board(w).map(c => `${c.id}:${c.status}`)).toEqual(['goal-1:doing', 'goal-2:todo'])

    await $.turn.complete(done('t1'))
    await clock.advance(300)
    expect(board(w).map(c => `${c.id}:${c.status}`)).toEqual(['goal-1:done', 'goal-2:todo'])

    await $.turn.start({ text: 'Queued one', turnId: 't2' })
    await clock.advance(300)
    expect(card(w, 'goal-2')?.status).toBe('doing')
    await $.turn.complete(done('t2'))

    // queued behind a turn that is then interrupted: Claude Code hands it back, so its card goes
    await $.prompt.submit(typed('Fourth'))
    await $.turn.start({ text: 'Fourth', turnId: 't3' })
    await $.prompt.submit(typed('Never mind', 't3'))
    await $.turn.complete(done('t3', 'aborted'))
    await clock.advance(300)
    expect(board(w).map(c => c.id)).toEqual(['goal-1', 'goal-2', 'goal-3'])
  })

  test('notifications, meta prompts and game messages get no card; slash commands are named by the command', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    await $.session.start(START)
    await clock.advance(1_000)

    await $.prompt.submit({ text: '<task-notification>done</task-notification>', wait: false, origin: { kind: 'task-notification' } })
    await $.turn.start({ text: '<task-notification>done</task-notification>', turnId: 't1' })
    await $.turn.complete(done('t1'))
    await $.prompt.submit({ text: '[RimWorld message for all] lunch soon', wait: false, origin: { kind: 'plugin', name: 'colony-code' } })
    await $.turn.start({ text: '[RimWorld message for all] lunch soon', turnId: 't2' })
    await $.turn.complete(done('t2'))
    await clock.advance(300)
    expect(board(w)).toEqual([])

    await $.prompt.submit({ text: '/review main', wait: false, origin: { kind: 'composer' }, command: { name: 'review', args: 'main' } })
    await $.turn.start({ text: 'Review the diff of the current branch against its base...', turnId: 't3' })
    await clock.advance(300)
    expect(board(w)).toEqual([{ id: 'goal-1', subject: '/review main', status: 'doing', source: 'goal', agentId: LEAD }])
  })
})

describe('subagent cards', () => {
  test('agent.spawn → agent-<agentId> doing, linked to the running goal; its turn.complete → done', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    let n = 0
    on('agent.spawn', () => ({ model: 'claude-test', agentId: `agent-${++n}` }))
    const spawn = (description: string, parentAgentId?: string) =>
      $.agent.spawn({
        tool_use_id: `tu${n + 1}`,
        prompt: `${description}: the long task text`,
        description,
        subagentType: 'Explore',
        provider: { plugin: 'engine', tier: 'core' },
        parentModel: 'claude-test',
        background: false,
        fork: false,
        ...(parentAgentId !== undefined ? { parentAgentId } : {}),
      })

    await $.session.start(START)
    await clock.advance(1_000)
    await $.prompt.submit(typed('Fix the flaky test'))
    await $.turn.start({ text: 'Fix the flaky test', turnId: 't1' })
    await spawn('Find the flaky test')
    await spawn('Read the CI logs', 'agent-1') // nested: same goal
    await clock.advance(300)
    expect(card(w, 'agent-agent-1')).toEqual({
      id: 'agent-agent-1',
      subject: 'Find the flaky test',
      status: 'doing',
      source: 'agent',
      parent: 'goal-1',
      owner: 'Explore',
      agentId: 'agent-1',
      description: 'Find the flaky test: the long task text',
    })
    expect(card(w, 'agent-agent-2')).toMatchObject({ parent: 'goal-1', status: 'doing' })

    await $.turn.complete(done('ts1', 'answer', 'agent-1'))
    await $.turn.complete(done('ts2', 'error', 'agent-2'))
    await clock.advance(300)
    expect(card(w, 'agent-agent-1')).toMatchObject({ status: 'done' })
    expect(card(w, 'agent-agent-1')?.result).toBeUndefined()
    expect(card(w, 'agent-agent-2')).toMatchObject({ status: 'done', result: 'failed' })
    expect(card(w, 'goal-1')?.status).toBe('doing')

    // woken again (a message), it is back in progress
    await $.turn.start({ text: 'one more thing', turnId: 'ts3', agentId: 'agent-1' })
    await clock.advance(300)
    expect(card(w, 'agent-agent-1')?.status).toBe('doing')
  })
})

describe('game task cards', () => {
  test('task.create shows game-<id> in todo at once; its turn moves it, with no duplicate goal card', async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    on('agent.spawn', () => ({ model: 'claude-test', agentId: 'agent-g' }))
    await $.session.start(START)
    await clock.advance(1_000)

    w.inbox.push({ id: 'tc1', type: 'task.create', ts: 1, text: 'Write a changelog entry' })
    await clock.advance(1_500)
    expect(w.prompts).toHaveLength(1)
    expect(board(w)).toEqual([{ id: 'game-tc1', subject: 'Write a changelog entry', status: 'todo', source: 'game' }])

    const prompt = w.prompts[0] ?? ''
    await $.turn.start({ text: prompt, turnId: 't1' })
    await $.agent.spawn({
      tool_use_id: 'tu1',
      prompt: 'Collect the merged PRs',
      description: 'Collect merged PRs',
      subagentType: 'general-purpose',
      provider: { plugin: 'engine', tier: 'core' },
      parentModel: 'claude-test',
      background: false,
      fork: false,
    })
    await clock.advance(300)
    expect(board(w).map(c => `${c.id}:${c.status}`)).toEqual(['game-tc1:doing', 'agent-agent-g:doing'])
    expect(card(w, 'agent-agent-g')?.parent).toBe('game-tc1')

    await $.turn.complete(done('t1'))
    await clock.advance(300)
    expect(card(w, 'game-tc1')).toMatchObject({ status: 'done', source: 'game', agentId: LEAD })
    expect(board(w).some(c => c.source === 'goal')).toBe(false)
  })
})

describe('mixed and pure', () => {
  test("Claude's own task tools still merge (source claude, ids unchanged); TaskList keeps the derived cards", async ($, on) => {
    const clock = mock.clock(on, { now: 1_000 })
    const w = world(on, clock, true)
    on('tool.call', ($, e) => {
      if (e.tool === 'TaskCreate') return { result: { task: { id: '1', subject: e.subject } } }
      if (e.tool === 'TaskList') return { result: { tasks: [{ id: '1', subject: 'Write the parser', status: 'in_progress', blockedBy: [] }] } }
      return { result: 'ok' }
    })
    await $.session.start(START)
    await clock.advance(1_000)
    await $.prompt.submit(typed('Build a parser'))
    await $.turn.start({ text: 'Build a parser', turnId: 't1' })
    await $.tool.call({ tool: 'TaskCreate', subject: 'Write the parser', description: 'x' })
    await $.tool.call({ tool: 'TaskList' })
    await clock.advance(300)
    expect(board(w).map(c => `${c.id}:${c.source}:${c.status}`)).toEqual(['1:claude:doing', 'goal-1:goal:doing'])
  })

  test('prefixes are stripped; subjects are one line of at most 80 chars; done cards are capped', () => {
    expect(
      stripPluginPrefix('[RimWorld task] Work on this when appropriate (add it to your task list if you have one): Ship it'),
    ).toBe('Ship it')
    expect(stripPluginPrefix('[RimWorld review] Changes requested on feat/x: rename it')).toBe('Changes requested on feat/x: rename it')
    expect(stripPluginPrefix('[RimWorld message for Lead] hello')).toBe('hello')
    expect(cardSubject('\n\n  first line  \nsecond')).toBe('first line')
    expect(cardSubject('x'.repeat(200))?.length).toBe(80)
    expect(cardSubject('   ')).toBeUndefined()

    const book = new TaskBook()
    for (let i = 1; i <= DONE_CARDS_MAX + 5; i++) {
      book.promptSubmitted('s', { text: `goal ${i}` })
      book.turnStarted('s', `t${i}`, `goal ${i}`, undefined, 'lead')
      book.turnCompleted('s', `t${i}`, undefined, 'answer')
    }
    book.promptSubmitted('s', { text: 'still waiting' })
    const list = book.list('s')
    expect(list.filter(c => c.status === 'done')).toHaveLength(DONE_CARDS_MAX)
    expect(list[0]?.id).toBe('goal-6')
    expect(list.at(-1)).toMatchObject({ id: `goal-${DONE_CARDS_MAX + 6}`, status: 'todo' })

    // review prompts are goal cards with the prefix stripped
    book.promptSubmitted('s', { text: '[RimWorld review] Changes requested on feat/x: rename it', origin: 'plugin' })
    expect(book.list('s').at(-1)?.subject).toBe('Changes requested on feat/x: rename it')
  })
})
