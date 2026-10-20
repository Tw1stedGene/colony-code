// Colony Code task board: tracks Claude Code's own task list per session, as the plugin sees it
// through tool calls (TaskCreate / TaskUpdate / TaskList / TaskGet / TodoWrite) and the classic
// TaskCreated / TaskCompleted hooks, plus cards the plugin derives itself: one per user prompt
// (goal-<n>), per subagent (agent-<id>) and per task the game asked for (game-<inbox id>).
// Pure: no `$`, so it is easy to test.
// The game gets the whole list as `task.snapshot`.

export type BoardStatus = 'todo' | 'doing' | 'done'

/** Where a card comes from: a user prompt, a subagent, the game's task.create, Claude's task tools. */
export type CardSource = 'goal' | 'agent' | 'game' | 'claude'

/** How a done goal/agent card's turn ended, when it did not end with an answer. */
export type CardResult = 'failed' | 'interrupted'

export type BoardTask = {
  id: string
  subject: string
  status: BoardStatus
  /** Set on derived cards; Claude's own tasks are sent as `claude`. */
  source?: CardSource
  /** A subagent card's goal card (`goal-<n>` or `game-<id>`). */
  parent?: string
  /** A done card whose turn failed (error, refusal) or was interrupted. */
  result?: CardResult
  description?: string
  activeForm?: string
  owner?: string
  /** The agent that last moved the task (lead id or worker agentId). */
  agentId?: string
  blockedBy?: string[]
}

export const TASK_TOOLS = new Set(['TaskCreate', 'TaskUpdate', 'TaskList', 'TaskGet', 'TodoWrite'])

const SUBJECT_MAX = 200
const DESCRIPTION_MAX = 1000
const ACTIVE_FORM_MAX = 80
const OWNER_MAX = 60
const TODO_PREFIX = 'todo-'

type Rec = Record<string, unknown>

const isRec = (v: unknown): v is Rec => v !== null && typeof v === 'object' && !Array.isArray(v)

/** An object as is, or a JSON string that parses to an object; else undefined. */
export function asRecord(v: unknown): Rec | undefined {
  if (isRec(v)) return v
  if (typeof v !== 'string') return undefined
  const t = v.trim()
  if (!t.startsWith('{')) return undefined
  try {
    const parsed = JSON.parse(t) as unknown
    return isRec(parsed) ? parsed : undefined
  } catch {
    return undefined
  }
}

/** pending → todo, in_progress → doing, completed → done, deleted → 'deleted'. */
export function mapStatus(s: unknown): BoardStatus | 'deleted' | undefined {
  switch (s) {
    case 'pending':
      return 'todo'
    case 'in_progress':
      return 'doing'
    case 'completed':
      return 'done'
    case 'deleted':
      return 'deleted'
    default:
      return undefined
  }
}

const idOf = (v: unknown): string | undefined =>
  typeof v === 'string' && v !== '' ? v : typeof v === 'number' && Number.isFinite(v) ? String(v) : undefined

function clip(v: unknown, max: number): string | undefined {
  if (typeof v !== 'string') return undefined
  const t = v.trim()
  if (t === '') return undefined
  return t.length <= max ? t : `${t.slice(0, max - 1)}…`
}

function ids(v: unknown): string[] {
  if (!Array.isArray(v)) return []
  const out: string[] = []
  for (const x of v) {
    const id = idOf(x)
    if (id !== undefined && !out.includes(id)) out.push(id)
  }
  return out
}

/** A copy without empty optional fields (protocol: optional fields are omitted, never null). */
function clean(t: BoardTask): BoardTask {
  const out: BoardTask = { id: t.id, subject: t.subject, status: t.status, source: t.source ?? 'claude' }
  if (t.parent !== undefined) out.parent = t.parent
  if (t.result !== undefined && t.status === 'done') out.result = t.result
  if (t.description !== undefined) out.description = t.description
  if (t.activeForm !== undefined) out.activeForm = t.activeForm
  if (t.owner !== undefined) out.owner = t.owner
  if (t.agentId !== undefined) out.agentId = t.agentId
  if (t.blockedBy !== undefined && t.blockedBy.length > 0) out.blockedBy = [...t.blockedBy]
  return out
}

/** The tool's result as a record: `result` (object or JSON string), else the model-facing `text`. */
export function resultRecord(result: unknown, text: unknown): Rec | undefined {
  return asRecord(result) ?? asRecord(text)
}

// ---- derived cards: goals, subagents, game tasks -------------------------------------------------

/** Prompts the plugin itself submits (register.ts route / review); the card shows the text after them. */
export const GAME_TASK_PREFIX =
  '[RimWorld task] Work on this when appropriate (add it to your task list if you have one): '
export const REVIEW_PREFIX = '[RimWorld review] '
export const MESSAGE_PREFIX = '[RimWorld message for '

export const CARD_SUBJECT_MAX = 80
/** Done goal/agent/game cards kept per session (newest by completion). */
export const DONE_CARDS_MAX = 100
const PENDING_MAX = 20
const FINISHED_AGENTS_MAX = 50

/** Origins whose prompts are the person's own work requests (PromptOrigin.kind); others get no card. */
const CARD_ORIGINS = new Set(['composer', 'bridge', 'sdk', 'plugin', 'slack-ping'])

/** Strips the plugin's own prompt prefixes, so a card shows the real text. */
export function stripPluginPrefix(text: string): string {
  const t = text.trim()
  if (t.startsWith(GAME_TASK_PREFIX.trim())) return t.slice(GAME_TASK_PREFIX.trim().length).trim()
  if (t.startsWith(REVIEW_PREFIX)) return t.slice(REVIEW_PREFIX.length).trim()
  if (t.startsWith(MESSAGE_PREFIX)) {
    const end = t.indexOf('] ')
    if (end > 0) return t.slice(end + 2).trim()
  }
  return t
}

/** A card's subject: the first non-blank line of the prompt (prefixes stripped), clipped to ~80. */
export function cardSubject(text: string): string | undefined {
  const body = stripPluginPrefix(text)
  const line = body.split(/\r?\n/).find(l => l.trim() !== '')
  return line === undefined ? undefined : clip(line, CARD_SUBJECT_MAX)
}

/** The card's description: the whole prompt (prefixes stripped) when it says more than the subject. */
function cardDescription(text: string, subject: string): string | undefined {
  const body = stripPluginPrefix(text)
  return body === subject ? undefined : clip(body, DESCRIPTION_MAX)
}

/** Why a turn ended (TurnCompleteReason) → a done card's `result`. */
export function resultFor(reason: unknown): CardResult | undefined {
  if (reason === 'aborted') return 'interrupted'
  if (reason === 'error' || reason === 'refusal') return 'failed'
  return undefined
}

/**
 * A submitted prompt waiting for its turn. `card`: it has a todo card (`cardId`); `skip`: it gets
 * none (a message, a notification); `command`: a slash command, carded when a turn starts for it.
 */
type Pending = {
  kind: 'card' | 'skip' | 'command'
  /** The prompt text as submitted (trimmed): a main turn whose text contains it is its turn. */
  text: string
  cardId?: string
  /** `/name args` for a command. */
  label?: string
  /** The main turn that was running when it was submitted (queued behind it). */
  during?: string
  /** Registered by the plugin before its own `$.prompt.submit`: the hook's sighting is not a new prompt. */
  echo?: boolean
}

type Derived = {
  cards: Map<string, BoardTask>
  /** card id → completion order, for the done cap. */
  doneAt: Map<string, number>
  pending: Pending[]
  goals: number
  /** The running main turn and the goal cards it works on. */
  main?: { turnId: string; cards: string[] }
  /** The goal subagents link to when no main turn runs. */
  lastGoal?: string
  /** agentId → card id. */
  agentCards: Map<string, string>
  /** Agents whose turn ended before their spawn was seen (agentId → result or ''). */
  finished: Map<string, CardResult | ''>
}

export type PromptSeen = {
  text: string
  command?: { name: string; args?: string }
  isMeta?: boolean
  /** PromptOrigin.kind; undefined counts as the person's own. */
  origin?: string
  /** The main turn running when it was submitted. */
  turnId?: string
}

export type SpawnSeen = {
  agentId?: string
  toolUseId: string
  description?: string
  prompt?: string
  subagentType?: string
  parentAgentId?: string
}

/** Every session's task list, in creation order. */
export class TaskBook {
  private readonly lists = new Map<string, Map<string, BoardTask>>()
  private readonly derived = new Map<string, Derived>()
  private doneCounter = 0

  /** True once the session has a list (even an empty one): then it is worth (re)sending. */
  has(sessionId: string): boolean {
    return this.lists.has(sessionId) || (this.derived.get(sessionId)?.cards.size ?? 0) > 0
  }

  /** The session's tasks for `task.snapshot`: Claude's own tasks, then the derived cards. */
  list(sessionId: string): BoardTask[] {
    const m = this.lists.get(sessionId)
    const own = m === undefined ? [] : [...m.values()].map(clean)
    const d = this.derived.get(sessionId)
    return d === undefined ? own : [...own, ...[...d.cards.values()].map(clean)]
  }

  clear(sessionId: string): void {
    this.lists.delete(sessionId)
    this.derived.delete(sessionId)
  }

  private dbook(sessionId: string): Derived {
    let d = this.derived.get(sessionId)
    if (d === undefined) {
      d = { cards: new Map(), doneAt: new Map(), pending: [], goals: 0, agentCards: new Map(), finished: new Map() }
      this.derived.set(sessionId, d)
    }
    return d
  }

  /** Runs `fn` on the session's derived cards; true when the snapshot changed. */
  private changeCards(sessionId: string, fn: (d: Derived) => void): boolean {
    const d = this.dbook(sessionId)
    const before = JSON.stringify([...d.cards.values()].map(clean))
    fn(d)
    this.capDone(d)
    return JSON.stringify([...d.cards.values()].map(clean)) !== before
  }

  private setStatus(d: Derived, card: BoardTask, status: BoardStatus, result?: CardResult): void {
    card.status = status
    if (status === 'done') {
      if (!d.doneAt.has(card.id)) d.doneAt.set(card.id, ++this.doneCounter)
      if (result !== undefined) card.result = result
      else delete card.result
    } else {
      d.doneAt.delete(card.id)
      delete card.result
    }
  }

  /** Keeps the newest DONE_CARDS_MAX done cards. */
  private capDone(d: Derived): void {
    if (d.doneAt.size <= DONE_CARDS_MAX) return
    const order = [...d.doneAt.entries()].sort((a, b) => a[1] - b[1])
    for (const [id] of order.slice(0, order.length - DONE_CARDS_MAX)) {
      d.doneAt.delete(id)
      d.cards.delete(id)
      for (const [agent, cardId] of d.agentCards) if (cardId === id) d.agentCards.delete(agent)
    }
  }

  private newGoal(d: Derived, text: string, subject?: string): BoardTask | undefined {
    const s = subject ?? cardSubject(text)
    if (s === undefined) return undefined
    d.goals += 1
    const card: BoardTask = { id: `goal-${d.goals}`, subject: s, status: 'todo', source: 'goal' }
    const description = subject === undefined ? cardDescription(text, s) : undefined
    if (description !== undefined) card.description = description
    d.cards.set(card.id, card)
    return card
  }

  private addPending(d: Derived, p: Pending): void {
    d.pending.push(p)
    // never-matched records (a /model command, a dropped notification) must not pile up
    while (d.pending.length > PENDING_MAX) {
      const gone = d.pending.shift()
      if (gone?.cardId !== undefined && d.cards.get(gone.cardId)?.status === 'todo' && gone.cardId.startsWith('goal-')) {
        d.cards.delete(gone.cardId)
      }
    }
  }

  /**
   * `prompt.submit`, before it enters. A main-session prompt that asks for work gets a `goal-<n>`
   * card in `todo` (its turn moves it to `doing`); messages from the game, notifications and meta
   * prompts get none. Slash commands get a card once a turn starts for them.
   */
  promptSubmitted(sessionId: string, p: PromptSeen): boolean {
    const text = p.text.trim()
    if (text === '' && p.command === undefined) return false
    return this.changeCards(sessionId, d => {
      const echo = d.pending.find(x => x.echo === true && x.text === text)
      if (echo !== undefined) {
        echo.echo = false // the plugin's own prompt (a game task): already on the board
        if (p.turnId !== undefined) echo.during = p.turnId
        return
      }
      const base: Pending = { kind: 'skip', text }
      if (p.turnId !== undefined) base.during = p.turnId
      if (p.isMeta === true || (p.origin !== undefined && !CARD_ORIGINS.has(p.origin)) || text.startsWith(MESSAGE_PREFIX)) {
        this.addPending(d, base)
        return
      }
      if (p.command !== undefined) {
        const args = (p.command.args ?? '').trim()
        // only the latest command waits for a turn: a /model or /clear never gets one
        d.pending = d.pending.filter(x => x.kind !== 'command')
        this.addPending(d, { ...base, kind: 'command', label: `/${p.command.name}${args === '' ? '' : ` ${args}`}` })
        return
      }
      // a prompt sent while idle starts at once: an older command never got a turn
      if (p.turnId === undefined) d.pending = d.pending.filter(x => x.kind !== 'command')
      const card = this.newGoal(d, text)
      if (card === undefined) return
      this.addPending(d, { ...base, kind: 'card', cardId: card.id })
    })
  }

  /** The prompt did not enter (a hook dropped it): its todo card goes. */
  promptDropped(sessionId: string, text: string): boolean {
    const t = text.trim()
    return this.changeCards(sessionId, d => {
      for (let i = d.pending.length - 1; i >= 0; i--) {
        const p = d.pending[i]
        if (p === undefined || p.text !== t || p.echo === true) continue
        d.pending.splice(i, 1)
        if (p.cardId !== undefined && d.cards.get(p.cardId)?.status === 'todo') d.cards.delete(p.cardId)
        return
      }
    })
  }

  /**
   * The game's `task.create`: a `game-<inbox id>` card in `todo` right away. `promptText` is what the
   * plugin submits for it; the turn that starts with it works on this card (no goal card).
   */
  gameTask(sessionId: string, inboxId: string, text: string, promptText: string): boolean {
    const subject = cardSubject(text)
    if (subject === undefined) return false
    return this.changeCards(sessionId, d => {
      const id = `game-${inboxId}`
      if (d.cards.has(id)) return
      const card: BoardTask = { id, subject, status: 'todo', source: 'game' }
      const description = cardDescription(text, subject)
      if (description !== undefined) card.description = description
      d.cards.set(id, card)
      this.addPending(d, { kind: 'card', text: promptText.trim(), cardId: id, echo: true })
    })
  }

  /** Claude Code did not take a game task's prompt: its card goes again. */
  gameTaskFailed(sessionId: string, inboxId: string): boolean {
    return this.changeCards(sessionId, d => {
      const id = `game-${inboxId}`
      d.pending = d.pending.filter(x => x.cardId !== id)
      if (d.cards.get(id)?.status === 'todo') d.cards.delete(id)
    })
  }

  /**
   * `turn.start`. On the main loop: the waiting prompts whose text the turn's text contains are its
   * work (their cards go `doing`); else the latest slash command, else a new goal card from the
   * turn's own text. A subagent's turn moves its card back to `doing`.
   */
  turnStarted(sessionId: string, turnId: string, text: string, agentId: string | undefined, leadId: string): boolean {
    return this.changeCards(sessionId, d => {
      if (agentId !== undefined) {
        const card = d.cards.get(d.agentCards.get(agentId) ?? '')
        if (card !== undefined && card.status !== 'doing') this.setStatus(d, card, 'doing')
        return
      }
      const turnText = text.trim()
      const bound: string[] = []
      const matched = turnText === '' ? [] : d.pending.filter(p => p.kind !== 'command' && p.text !== '' && turnText.includes(p.text))
      for (const p of matched) if (p.cardId !== undefined) bound.push(p.cardId)
      d.pending = d.pending.filter(p => !matched.includes(p))
      if (matched.length === 0) {
        const command = d.pending.find(p => p.kind === 'command')
        if (command !== undefined) {
          d.pending = d.pending.filter(p => p !== command)
          const card = this.newGoal(d, command.text, clip(command.label, CARD_SUBJECT_MAX))
          if (card !== undefined) bound.push(card.id)
        } else if (turnText !== '' && !turnText.startsWith('<')) {
          // no prompt.submit seen for it (a reload, a host that raises none); `<...>` is a system notice
          const card = this.newGoal(d, turnText)
          if (card !== undefined) bound.push(card.id)
        }
      }
      d.main = { turnId, cards: bound }
      for (const id of bound) {
        const card = d.cards.get(id)
        if (card === undefined) continue
        this.setStatus(d, card, 'doing')
        card.agentId = leadId
      }
      if (bound[0] !== undefined) d.lastGoal = bound[0]
    })
  }

  /**
   * `turn.complete`. The main loop's: its goal cards go `done` (`result` when it failed or was
   * interrupted); an interrupted turn also drops the prompts queued behind it (Claude Code hands
   * them back to the input box; resubmitted, they get new cards). A subagent's: its card goes `done`.
   */
  turnCompleted(sessionId: string, turnId: string, agentId: string | undefined, reason: unknown): boolean {
    const result = resultFor(reason)
    return this.changeCards(sessionId, d => {
      if (agentId !== undefined) {
        const card = d.cards.get(d.agentCards.get(agentId) ?? '')
        if (card !== undefined) this.setStatus(d, card, 'done', result)
        else {
          d.finished.set(agentId, result ?? '')
          while (d.finished.size > FINISHED_AGENTS_MAX) {
            const first = d.finished.keys().next().value
            if (first === undefined) break
            d.finished.delete(first)
          }
        }
        return
      }
      for (const id of d.main?.cards ?? []) {
        const card = d.cards.get(id)
        if (card !== undefined) this.setStatus(d, card, 'done', result)
      }
      d.main = undefined
      if (reason === 'aborted') {
        for (const p of d.pending.filter(x => x.during === turnId && x.cardId?.startsWith('goal-') === true)) {
          if (p.cardId !== undefined && d.cards.get(p.cardId)?.status === 'todo') d.cards.delete(p.cardId)
        }
        d.pending = d.pending.filter(x => !(x.during === turnId && x.cardId?.startsWith('goal-') === true))
      }
    })
  }

  /**
   * `agent.spawn` once it answered: an `agent-<agentId>` card in `doing` (owner the subagent type),
   * linked to the goal it works for (`parent`): the running main turn's goal, a nested agent's
   * parent's goal, else the last goal.
   */
  agentSpawned(sessionId: string, s: SpawnSeen): boolean {
    const key = s.agentId ?? (s.toolUseId !== '' ? s.toolUseId : undefined)
    if (key === undefined) return false
    return this.changeCards(sessionId, d => {
      let id = `agent-${key}`
      if (s.agentId === undefined) for (let n = 2; d.cards.has(id); n++) id = `agent-${key}-${n}`
      const subject =
        clip(s.description, CARD_SUBJECT_MAX) ?? cardSubject(s.prompt ?? '') ?? clip(s.subagentType, CARD_SUBJECT_MAX) ?? 'Subagent'
      const card: BoardTask = d.cards.get(id) ?? { id, subject, status: 'doing', source: 'agent' }
      card.subject = subject
      const owner = clip(s.subagentType, OWNER_MAX)
      if (owner !== undefined) card.owner = owner
      if (s.agentId !== undefined) card.agentId = s.agentId
      const prompt = clip(s.prompt, DESCRIPTION_MAX)
      if (prompt !== undefined && prompt !== subject) card.description = prompt
      const parentCard = s.parentAgentId === undefined ? undefined : d.cards.get(d.agentCards.get(s.parentAgentId) ?? '')
      const parent = parentCard?.parent ?? d.main?.cards[0] ?? d.lastGoal
      if (parent !== undefined) card.parent = parent
      d.cards.set(id, card)
      if (s.agentId !== undefined) d.agentCards.set(s.agentId, id)
      const ended = s.agentId === undefined ? undefined : d.finished.get(s.agentId)
      if (ended !== undefined) {
        d.finished.delete(s.agentId ?? '')
        this.setStatus(d, card, 'done', ended === '' ? undefined : ended)
      } else {
        this.setStatus(d, card, 'doing')
      }
    })
  }

  private book(sessionId: string): Map<string, BoardTask> {
    let m = this.lists.get(sessionId)
    if (m === undefined) {
      m = new Map()
      this.lists.set(sessionId, m)
    }
    return m
  }

  /** Runs `fn` on the session's list; true when the snapshot changed. */
  private change(sessionId: string, fn: (m: Map<string, BoardTask>) => Map<string, BoardTask> | void): boolean {
    const existed = this.lists.has(sessionId)
    const before = existed ? JSON.stringify(this.list(sessionId)) : ''
    const m = this.book(sessionId)
    const replaced = fn(m)
    if (replaced !== undefined) this.lists.set(sessionId, replaced)
    return !existed || JSON.stringify(this.list(sessionId)) !== before
  }

  /**
   * A finished task tool call. `input` is the tool's input, `result` / `text` what it answered.
   * Returns true when the session's list changed.
   */
  applyTool(sessionId: string, tool: string, input: Rec, result: unknown, text: unknown, agentId: string): boolean {
    const res = resultRecord(result, text)
    switch (tool) {
      case 'TaskCreate':
        return this.onCreate(sessionId, input, res, agentId)
      case 'TaskUpdate':
        return this.onUpdate(sessionId, input, res, agentId)
      case 'TaskList':
        return res === undefined ? false : this.onList(sessionId, res)
      case 'TaskGet':
        return res === undefined ? false : this.onGet(sessionId, input, res)
      case 'TodoWrite':
        return this.onTodoWrite(sessionId, input, res, agentId)
      default:
        return false
    }
  }

  private onCreate(sessionId: string, input: Rec, res: Rec | undefined, agentId: string): boolean {
    const task = res === undefined ? undefined : isRec(res.task) ? res.task : res
    const id = idOf(task?.id)
    if (id === undefined) return false // the classic TaskCreated hook may still bring it
    return this.change(sessionId, m => {
      const prev = m.get(id)
      const t: BoardTask = prev ?? { id, subject: '', status: 'todo' }
      t.subject = clip(task?.subject, SUBJECT_MAX) ?? clip(input.subject, SUBJECT_MAX) ?? t.subject ?? `#${id}`
      if (t.subject === '') t.subject = `#${id}`
      const description = clip(input.description, DESCRIPTION_MAX)
      if (description !== undefined) t.description = description
      const activeForm = clip(input.activeForm, ACTIVE_FORM_MAX)
      if (activeForm !== undefined) t.activeForm = activeForm
      t.agentId = agentId
      m.set(id, t)
    })
  }

  private onUpdate(sessionId: string, input: Rec, res: Rec | undefined, agentId: string): boolean {
    if (res !== undefined && res.success === false) return false
    const id = idOf(input.taskId) ?? idOf(res?.taskId)
    if (id === undefined) return false
    const change = isRec(res?.statusChange) ? res.statusChange : undefined
    const status = mapStatus(change?.to) ?? mapStatus(input.status)
    return this.change(sessionId, m => {
      if (status === 'deleted') {
        m.delete(id)
        return
      }
      const prev = m.get(id)
      const t: BoardTask = prev ?? { id, subject: `#${id}`, status: 'todo' }
      const subject = clip(input.subject, SUBJECT_MAX)
      if (subject !== undefined) t.subject = subject
      const description = clip(input.description, DESCRIPTION_MAX)
      if (description !== undefined) t.description = description
      const activeForm = clip(input.activeForm, ACTIVE_FORM_MAX)
      if (activeForm !== undefined) t.activeForm = activeForm
      const owner = clip(input.owner, OWNER_MAX)
      if (owner !== undefined) t.owner = owner
      const blockers = ids(input.addBlockedBy)
      if (blockers.length > 0) t.blockedBy = [...new Set([...(t.blockedBy ?? []), ...blockers])]
      if (status !== undefined && status !== t.status) {
        t.status = status
        t.agentId = agentId
      } else if (prev === undefined) {
        t.agentId = agentId
      }
      m.set(id, t)
    })
  }

  /** TaskList is authoritative for the Task* tasks: unknown ones go, new ones come, order follows it. */
  private onList(sessionId: string, res: Rec): boolean {
    if (!Array.isArray(res.tasks)) return false
    const listed = res.tasks
    return this.change(sessionId, m => {
      const next = new Map<string, BoardTask>()
      for (const one of listed) {
        if (!isRec(one)) continue
        const id = idOf(one.id)
        if (id === undefined) continue
        const prev = m.get(id)
        const status = mapStatus(one.status)
        if (status === 'deleted') continue
        const t: BoardTask = prev ?? { id, subject: `#${id}`, status: 'todo' }
        t.subject = clip(one.subject, SUBJECT_MAX) ?? t.subject
        if (status !== undefined) t.status = status
        const owner = clip(one.owner, OWNER_MAX)
        if (owner !== undefined) t.owner = owner
        else delete t.owner
        t.blockedBy = ids(one.blockedBy)
        next.set(id, t)
      }
      for (const [id, t] of m) if (id.startsWith(TODO_PREFIX)) next.set(id, t)
      return next
    })
  }

  private onGet(sessionId: string, input: Rec, res: Rec): boolean {
    if (!('task' in res)) return false
    const task = res.task
    if (task === null) {
      const gone = idOf(input.taskId)
      if (gone === undefined || !this.lists.get(sessionId)?.has(gone)) return false
      return this.change(sessionId, m => {
        m.delete(gone)
      })
    }
    if (!isRec(task)) return false
    const id = idOf(task.id) ?? idOf(input.taskId)
    if (id === undefined) return false
    return this.change(sessionId, m => {
      const t: BoardTask = m.get(id) ?? { id, subject: `#${id}`, status: 'todo' }
      t.subject = clip(task.subject, SUBJECT_MAX) ?? t.subject
      const description = clip(task.description, DESCRIPTION_MAX)
      if (description !== undefined) t.description = description
      const status = mapStatus(task.status)
      if (status === 'deleted') {
        m.delete(id)
        return
      }
      if (status !== undefined) t.status = status
      if (Array.isArray(task.blockedBy)) t.blockedBy = ids(task.blockedBy)
      m.set(id, t)
    })
  }

  /** TodoWrite sends the whole todo list: it replaces the session's `todo-<n>` tasks. */
  private onTodoWrite(sessionId: string, input: Rec, res: Rec | undefined, agentId: string): boolean {
    const todos = Array.isArray(res?.newTodos) ? res.newTodos : Array.isArray(input.todos) ? input.todos : undefined
    if (todos === undefined) return false
    return this.change(sessionId, m => {
      const next = new Map<string, BoardTask>()
      for (const [id, t] of m) if (!id.startsWith(TODO_PREFIX)) next.set(id, t)
      let n = 0
      for (const one of todos) {
        if (!isRec(one)) continue
        const subject = clip(one.content, SUBJECT_MAX)
        if (subject === undefined) continue
        n += 1
        const id = `${TODO_PREFIX}${n}`
        const status = mapStatus(one.status)
        const prev = m.get(id)
        const t: BoardTask = {
          id,
          subject,
          status: status === undefined || status === 'deleted' ? 'todo' : status,
        }
        const activeForm = clip(one.activeForm, ACTIVE_FORM_MAX)
        if (activeForm !== undefined) t.activeForm = activeForm
        const moved = prev === undefined || prev.status !== t.status || prev.subject !== t.subject
        const who = moved ? agentId : prev.agentId
        if (who !== undefined) t.agentId = who
        next.set(id, t)
      }
      return next
    })
  }

  /** classic TaskCreated: a backup for a TaskCreate whose result the plugin could not read. */
  applyCreated(sessionId: string, id: unknown, subject: unknown, description: unknown, agentId: string): boolean {
    const tid = idOf(id)
    if (tid === undefined) return false
    return this.change(sessionId, m => {
      const prev = m.get(tid)
      if (prev !== undefined) {
        if (prev.description === undefined) {
          const d = clip(description, DESCRIPTION_MAX)
          if (d !== undefined) prev.description = d
        }
        return
      }
      const t: BoardTask = { id: tid, subject: clip(subject, SUBJECT_MAX) ?? `#${tid}`, status: 'todo', agentId }
      const d = clip(description, DESCRIPTION_MAX)
      if (d !== undefined) t.description = d
      m.set(tid, t)
    })
  }

  /** classic TaskCompleted: the task is done. */
  applyCompleted(sessionId: string, id: unknown, subject: unknown, agentId: string): boolean {
    const tid = idOf(id)
    if (tid === undefined) return false
    return this.change(sessionId, m => {
      const t: BoardTask = m.get(tid) ?? { id: tid, subject: clip(subject, SUBJECT_MAX) ?? `#${tid}`, status: 'todo' }
      if (t.status !== 'done') {
        t.status = 'done'
        t.agentId = agentId
      }
      m.set(tid, t)
    })
  }
}
