// Colony Code: reports this Claude Code session to the RimWorld mod's bridge
// (protocol v0) and relays the game's inbox back as prompts.
//
// Rules this module keeps:
// - never throw out of a hook, never block Claude Code when the game is absent;
// - event posts are buffered and flushed by a timer (fire-and-forget);
// - one inbox long-poll at a time.
//
// Module state lives at the top level: a hot reload starts it over, and
// session.start fires again then.

import type { EngineInterface, HttpResponse, Register, Timer, ToolCallInput, ToolCallResult } from 'claude-code'

import {
  ACTIVITY_MAX,
  ALWAYS_ALLOW,
  ALWAYS_COVERS_KEY,
  CHALLENGE_HEADER,
  DECISION_OPTIONS,
  DECISION_OPTIONS_ALWAYS,
  activityFor,
  argsOf,
  branchFromHead,
  describePermission,
  diffFor,
  folderName,
  hmacSha256Hex,
  leadIdFor,
  parseBridge,
  parseDecision,
  parseInbox,
  parseProof,
  questionAnswer,
  questionContext,
  randomHex,
  resultSnippet,
  isPersonalFolder,
  privatePath,
  scrubHome,
  sessionLabel,
  summarizeSuggestions,
  stateForTool,
  stationForState,
  toolSummary,
  truncate,
} from './protocol'
import type { Agent, AgentState, Bridge, InboxItem, LogEntry } from './protocol'
import { GAME_TASK_PREFIX, TASK_TOOLS, TaskBook } from './tasks'
import { diffFor as gitDiff, listWorktrees, review } from './review'
import type { ReviewAction, Runner, WorktreeList } from './review'

const TICK_MS = 250
const HOUSEKEEPING_EVERY = 4 // ticks: once a second
const DISCOVER_DISCONNECTED = 2 // housekeeping rounds: every 2 s while not connected
const DISCOVER_CONNECTED = 5 // every 5 s while connected (the game may restart)
const RETRY_AFTER_FAIL = 3 // housekeeping rounds to wait after a failed request
const POST_TIMEOUT_MS = 2_000
const INBOX_WAIT_S = 25
const DECISION_WAIT_S = 25
const DECISION_TOTAL_MS = 5 * 60_000
const IDLE_AFTER_MS = 10_000
const FLUSH_AT = 20
const BUFFER_MAX = 500
const BATCH_MAX = 100

export const STATUS_ON = 'RimWorld ● connected'
export const STATUS_OFF = '○ RimWorld not running'

type Ev = { type: string; ts: number; sessionId: string; [k: string]: unknown }
type Engine = EngineInterface

// ---- module state --------------------------------------------------------------

let sessionId = ''
let cwd = ''
let repo: string | undefined
let branch: string | undefined
let model: string | undefined
let label = ''
let homeDir: string | undefined
/** The player's home folder (HOME / USERPROFILE): never shown in the game, see scrubHome. */
let userHome: string | undefined
/** The folder holding bridge.json: $COLONY_HOME, else $HOME/.colony-code. */
let colonyDir: string | undefined

let bridge: Bridge | undefined
let bridgeKey = '' // `${port}:${token}` of the bridge file last read
let helloFor = '' // `${bridgeKey}|${sessionId}` the session.start went to
let verifiedFor = '' // bridgeKey whose server proved it holds the token
let connected = false

let ticker: Timer | undefined
let tick = 0
let rounds = 0
let lastDiscover = -1000
let waitRounds = 0
let housekeeping = false
let polling = false
let flushing: Promise<void> | undefined
let buffer: Ev[] = []
let statusShown: string | undefined

const agents = new Map<string, Agent>()
const labels = new Map<string, string>()
const idleTimers = new Map<string, Timer>()
let mainTurnId: string | undefined
let activeGoal: { id: string; text: string } | undefined
let permCounter = 0
let pendingClosed: { id: string; agentId: string }[] = []

// Claude Code's task list per session (task.snapshot). Kept across resetAll: a session.start for a
// session the module already knows resends its last list. `tasksDirty` coalesces changes into one
// snapshot per timer tick (250 ms), posted with the next event batch.
const tasks = new TaskBook()
let tasksDirty = false

// usage.update: cumulative token counts of this session's turns (main loop and subagents);
// `turns` counts main-loop turns that carried usage.
type Usage = { inputTokens: number; outputTokens: number; cacheReadTokens: number; turns: number }
let usage: Usage = { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, turns: 0 }

// Review (worktree.list, diff.request, merge.request): git jobs run one at a time on this chain.
// `worktreesDirty` asks the timer for a fresh worktree.list (after session.start and each turn).
let reviewChain: Promise<void> = Promise.resolve()
let worktreesDirty = false
let listingQueued = false

const leadId = (): string => leadIdFor(sessionId)
const now = (): number => Date.now()

function resetAll(): void {
  sessionId = ''
  cwd = ''
  repo = undefined
  branch = undefined
  model = undefined
  label = ''
  bridge = undefined
  bridgeKey = ''
  helloFor = ''
  verifiedFor = ''
  connected = false
  tick = 0
  rounds = 0
  lastDiscover = -1000
  waitRounds = 0
  housekeeping = false
  polling = false
  flushing = undefined
  buffer = []
  statusShown = undefined
  agents.clear()
  labels.clear()
  for (const t of idleTimers.values()) t.cancel()
  idleTimers.clear()
  mainTurnId = undefined
  activeGoal = undefined
  pendingClosed = []
  tasksDirty = false
  usage = { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, turns: 0 }
  worktreesDirty = false
  listingQueued = false
}

// ---- small utilities -------------------------------------------------------------

function status($: Engine, text: string): void {
  if (statusShown === text) return
  statusShown = text
  try {
    $.ui.status(text)
  } catch {
    // the status line is decoration
  }
}

/** Resolves `p`, or undefined after `ms` or once `signal` aborts; never rejects. */
function withTimeout<T>($: Engine, p: Promise<T>, ms: number, signal?: AbortSignal): Promise<T | undefined> {
  return new Promise<T | undefined>(resolve => {
    let done = false
    let timer: Timer | undefined
    if (signal !== undefined) {
      if (signal.aborted) {
        resolve(undefined)
        return
      }
      signal.addEventListener(
        'abort',
        () => {
          if (done) return
          done = true
          timer?.cancel()
          resolve(undefined)
        },
        { once: true },
      )
    }
    try {
      timer = $.clock.after(ms, () => {
        if (!done) {
          done = true
          resolve(undefined)
        }
      })
    } catch {
      timer = undefined
    }
    p.then(
      v => {
        if (done) return
        done = true
        timer?.cancel()
        resolve(v)
      },
      () => {
        if (done) return
        done = true
        timer?.cancel()
        resolve(undefined)
      },
    )
  })
}

async function request(
  $: Engine,
  method: 'GET' | 'POST',
  path: string,
  body: string | undefined,
  timeoutMs: number,
  signal?: AbortSignal,
): Promise<HttpResponse | undefined> {
  const b = bridge
  // the token only ever goes to a server that proved it holds it (verify)
  if (b === undefined || verifiedFor !== bridgeKey || bridgeKey === '') return undefined
  const headers: Record<string, string> = { 'X-Colony-Token': b.token }
  const init: { method: string; headers: Record<string, string>; body?: string } = { method, headers }
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    init.body = body
  }
  try {
    return await withTimeout($, $.http.fetch(`http://127.0.0.1:${b.port}${path}`, init), timeoutMs, signal)
  } catch {
    return undefined
  }
}

/** Forget the bridge; the next round reads the file again. */
function forgetBridge(): void {
  connected = false
  helloFor = ''
  verifiedFor = ''
  bridgeKey = ''
  bridge = undefined
  buffer = []
}

/** A request failed: drop the connection; rediscover after a short wait. */
function lost($: Engine): void {
  forgetBridge()
  waitRounds = RETRY_AFTER_FAIL
  status($, STATUS_OFF)
}

// ---- outgoing events ---------------------------------------------------------------

function enqueue(type: string, payload: Record<string, unknown>): void {
  if (sessionId === '') return
  buffer.push({ type, ts: now(), sessionId, ...payload })
  if (buffer.length > BUFFER_MAX) buffer.splice(0, buffer.length - BUFFER_MAX)
}

/** Posts everything buffered; one flush at a time. Never rejects. */
function flush($: Engine): Promise<void> {
  if (flushing !== undefined) {
    // let the running flush finish, then post what came in meanwhile
    return flushing.then(() => (buffer.length > 0 ? flush($) : undefined))
  }
  const run = async (): Promise<void> => {
    while (buffer.length > 0) {
      if (!connected) {
        buffer = []
        return
      }
      const batch = buffer.splice(0, BATCH_MAX)
      const res = await request($, 'POST', '/events', scrubHome(JSON.stringify(batch), userHome), POST_TIMEOUT_MS)
      if (res === undefined) {
        lost($)
        return
      }
      if (res.status === 401) {
        forgetBridge() // the token changed: read the file again
        return
      }
    }
  }
  flushing = run()
    .catch(() => undefined)
    .finally(() => {
      flushing = undefined
    })
  return flushing
}

/** Fire-and-forget flush once the buffer is long (else the timer flushes it). */
function flushIfLong($: Engine): void {
  if (buffer.length > FLUSH_AT) void flush($)
}

function upsert(id: string, patch: Partial<Agent>): void {
  const prev = agents.get(id)
  const role: Agent['role'] = id === leadId() ? 'lead' : 'worker'
  const label = patch.label ?? prev?.label ?? labels.get(id)
  const state: AgentState = patch.state ?? prev?.state ?? 'idle'
  const agent: Agent = {
    id,
    role,
    state,
    activity: truncate(patch.activity ?? prev?.activity ?? state, ACTIVITY_MAX),
    station: patch.station ?? prev?.station ?? stationForState(state),
  }
  if (label !== undefined) agent.label = label
  agents.set(id, agent)
  enqueue('agent.upsert', { agent })
}

function log(agentId: string, entries: LogEntry[]): void {
  if (entries.length > 0) enqueue('agent.log', { agentId, entries })
}

function cancelIdle(id: string): void {
  idleTimers.get(id)?.cancel()
  idleTimers.delete(id)
}

function scheduleIdle($: Engine, id: string): void {
  cancelIdle(id)
  try {
    const timer = $.clock.after(IDLE_AFTER_MS, () => {
      idleTimers.delete(id)
      upsert(id, { state: 'idle', activity: 'idle', station: 'lounge' })
    })
    idleTimers.set(id, timer)
  } catch {
    // no timer: the agent stays done
  }
}

function closePending(agentId: string | undefined): void {
  const keep: typeof pendingClosed = []
  for (const p of pendingClosed) {
    if (agentId === undefined || p.agentId === agentId) enqueue('decision.closed', { id: p.id })
    else keep.push(p)
  }
  pendingClosed = keep
}

/** The session's whole task list, or undefined when it never had one. */
function taskSnapshot(): Ev | undefined {
  if (sessionId === '' || !tasks.has(sessionId)) return undefined
  return { type: 'task.snapshot', ts: now(), sessionId, tasks: tasks.list(sessionId) }
}

function markTasks(changed: boolean): void {
  if (changed && sessionId !== '') tasksDirty = true
}

function usagePayload(): Record<string, unknown> {
  return {
    inputTokens: usage.inputTokens,
    outputTokens: usage.outputTokens,
    cacheReadTokens: usage.cacheReadTokens,
    turns: usage.turns,
  }
}

// ---- review: worktrees, diffs, merges (git through $.process.run, never a shell) -----------

/** `$.process.run` as review.ts's Runner. */
function runnerFor($: Engine): Runner {
  return (argv, opts) => $.process.run(argv, { cwd: opts.cwd, timeoutMs: opts.timeoutMs, env: opts.env })
}

/** Runs `job` after every review job queued before it; never rejects. */
function queueReview(job: () => Promise<void>): void {
  reviewChain = reviewChain.then(job).catch(() => undefined)
}

const workerIds = (): string[] => [...agents.keys()].filter(id => id !== leadId())

/** The current worktrees, or undefined outside a git repository (or without git). */
async function currentWorktrees($: Engine): Promise<WorktreeList | undefined> {
  if (cwd === '') return undefined
  try {
    return await listWorktrees(runnerFor($), cwd, workerIds())
  } catch {
    return undefined
  }
}

async function sendWorktrees($: Engine, list?: WorktreeList): Promise<void> {
  const l = list ?? (await currentWorktrees($))
  if (l === undefined || sessionId === '') return
  enqueue('worktree.list', { base: l.base, dirty: l.dirty, worktrees: l.worktrees })
}

/** Queues one fresh worktree.list unless one is queued already. */
function refreshWorktrees($: Engine): void {
  if (listingQueued) return
  listingQueued = true
  queueReview(async () => {
    listingQueued = false
    await sendWorktrees($)
  })
}

function handleDiffRequest($: Engine, item: InboxItem): void {
  const requestId = item.requestId ?? item.id
  const worktree = item.worktree !== undefined && item.worktree !== '' ? item.worktree : undefined
  queueReview(async () => {
    const payload: Record<string, unknown> = { requestId }
    if (worktree !== undefined) payload.worktree = worktree
    const list = await currentWorktrees($)
    if (list === undefined) {
      payload.files = []
      payload.error = 'Not a git repository (or git could not run).'
    } else {
      const diff = await gitDiff(runnerFor($), list, worktree)
      payload.files = diff.files
      if (diff.truncated === true) payload.truncated = true
      if (diff.error !== undefined) payload.error = diff.error
    }
    enqueue('diff.result', payload)
    await flush($)
  })
}

const REVIEW_ACTIONS = new Set<string>(['merge', 'request_changes', 'reject'])

function handleMergeRequest($: Engine, item: InboxItem): void {
  const requestId = item.requestId ?? item.id
  const worktree = item.worktree ?? ''
  const action = item.action ?? ''
  queueReview(async () => {
    const result = (ok: boolean, message: string): void => {
      enqueue('merge.result', { requestId, worktree, action, ok, message })
    }
    if (!REVIEW_ACTIONS.has(action) || worktree === '') {
      result(false, worktree === '' ? 'No worktree named.' : `Unknown action ${action}.`)
      await flush($)
      return
    }
    const list = await currentWorktrees($)
    if (list === undefined) {
      result(false, 'Not a git repository (or git could not run).')
      await flush($)
      return
    }
    const outcome = await review(runnerFor($), list, worktree, action as ReviewAction)
    if (action === 'request_changes' && outcome.ok) {
      const branch = outcome.branch ?? worktree
      const text = (item.text ?? '').trim()
      if (text === '') {
        result(false, 'No change request text.')
        await flush($)
        return
      }
      try {
        await $.prompt.submit({ text: `[RimWorld review] Changes requested on ${branch}: ${text}` })
        result(true, `Sent to Claude: changes requested on ${branch}.`)
      } catch {
        result(false, 'Claude Code did not take the prompt.')
      }
    } else {
      result(outcome.ok, outcome.message)
    }
    await sendWorktrees($)
    await flush($)
  })
}

// ---- connection ----------------------------------------------------------------------

async function discover($: Engine): Promise<void> {
  if (homeDir === undefined) {
    // COLONY_HOME overrides ~/.colony-code (same as tools/fake-bridge.mjs), so tests can isolate
    const override = (await $.env.get('COLONY_HOME'))?.trim()
    if (override !== undefined && override !== '') {
      homeDir = override.replace(/\/+$/, '')
      colonyDir = homeDir
    } else {
      homeDir = (await $.env.get('HOME')) ?? (await $.env.get('USERPROFILE'))
      if (homeDir !== undefined) colonyDir = `${homeDir}/.colony-code`
    }
  }
  if (homeDir === undefined || colonyDir === undefined) return
  let text = ''
  try {
    const read = await $.fs.read(`${colonyDir}/bridge.json`)
    text = typeof read === 'string' ? read : ''
  } catch {
    // no file: the game is not running
    forgetBridge()
    status($, STATUS_OFF)
    return
  }
  const found = parseBridge(text)
  if (found === undefined) {
    forgetBridge()
    status($, STATUS_OFF)
    return
  }
  const key = `${found.port}:${found.token}`
  if (key !== bridgeKey) {
    bridge = found
    bridgeKey = key
    connected = false
    helloFor = ''
  }
}

/**
 * Asks the server to prove it holds the token from bridge.json, without sending the token:
 * GET /health with a random challenge; the answer's `proof` must be HMAC-SHA256(token, challenge).
 * A stale bridge.json (the game crashed) may point at a port some other process now holds; that
 * process never gets the token, and its answers are never turned into prompts or permissions.
 */
async function verify($: Engine): Promise<boolean> {
  const b = bridge
  const key = bridgeKey
  if (b === undefined || key === '') return false
  const challenge = randomHex(16)
  let res: HttpResponse | undefined
  try {
    res = await withTimeout(
      $,
      $.http.fetch(`http://127.0.0.1:${b.port}/health`, { method: 'GET', headers: { [CHALLENGE_HEADER]: challenge } }),
      POST_TIMEOUT_MS,
    )
  } catch {
    res = undefined
  }
  if (bridgeKey !== key) return false // the file changed meanwhile
  if (res === undefined) {
    lost($)
    return false
  }
  const proof = res.ok ? parseProof(res.text) : undefined
  const expected = await hmacSha256Hex(b.token, challenge)
  if (proof === undefined || proof !== expected) {
    // not the game that wrote bridge.json: stay away until the file changes or the retry
    forgetBridge()
    waitRounds = RETRY_AFTER_FAIL
    status($, STATUS_OFF)
    return false
  }
  verifiedFor = key
  return true
}

/** session.start plus a snapshot of every known agent, in one post. */
async function hello($: Engine): Promise<void> {
  if (sessionId === '') {
    try {
      sessionId = await $.session.id()
    } catch {
      return
    }
  }
  const lead = leadId()
  if (!agents.has(lead)) {
    agents.set(lead, {
      id: lead,
      role: 'lead',
      label: label === '' ? 'Claude' : `Claude (${label})`,
      state: 'idle',
      activity: 'idle',
      station: 'lounge',
    })
  }
  const start: Ev = { type: 'session.start', ts: now(), sessionId, cwd: privatePath(cwd, userHome) }
  if (repo !== undefined) start.repo = repo
  if (label !== '') start.label = label
  if (branch !== undefined) start.branch = branch
  if (model !== undefined) start.model = model
  const events: Ev[] = [start]
  for (const agent of agents.values()) {
    events.push({ type: 'agent.upsert', ts: now(), sessionId, agent })
  }
  const snapshot = taskSnapshot()
  if (snapshot !== undefined) {
    events.push(snapshot)
    tasksDirty = false
  }
  if (usage.turns > 0 || usage.outputTokens > 0) {
    events.push({ type: 'usage.update', ts: now(), sessionId, ...usagePayload() })
  }
  const key = `${bridgeKey}|${sessionId}`
  const res = await request($, 'POST', '/events', scrubHome(JSON.stringify(events), userHome), POST_TIMEOUT_MS)
  if (res === undefined) {
    lost($)
    return
  }
  if (!res.ok) {
    forgetBridge()
    waitRounds = RETRY_AFTER_FAIL
    return
  }
  helloFor = key
  connected = true
  worktreesDirty = true // worktree.list follows session.start (protocol v0.2)
  status($, STATUS_ON)
}

function route($: Engine, item: InboxItem): void {
  if (item.sessionId !== undefined && item.sessionId !== sessionId) return
  const text = item.text ?? ''
  if (item.type === 'goal.submit' && text.trim() !== '') {
    const goal = { id: item.id, text }
    enqueue('goal.upsert', { goal: { id: goal.id, text: goal.text, status: 'active' } })
    $.prompt.submit({ text }).then(
      () => {
        activeGoal = goal
      },
      () => undefined,
    )
  } else if (item.type === 'user.message' && text.trim() !== '') {
    const to = item.to ?? 'all'
    $.prompt.submit({ text: `[RimWorld message for ${to}] ${text}` }).catch(() => undefined)
  } else if (item.type === 'agent.interrupt') {
    if (item.agentId === leadId() && mainTurnId !== undefined) {
      $.turn.abort({ turnId: mainTurnId }).catch(() => undefined)
    }
  } else if (item.type === 'task.create' && text.trim() !== '') {
    // its card shows at once (game-<id>, todo); the turn that starts with this prompt works on it
    const prompt = `${GAME_TASK_PREFIX}${text}`
    markTasks(tasks.gameTask(sessionId, item.id, text, prompt))
    $.prompt.submit({ text: prompt }).catch(() => {
      markTasks(tasks.gameTaskFailed(sessionId, item.id))
    })
  } else if (item.type === 'diff.request') {
    handleDiffRequest($, item)
  } else if (item.type === 'merge.request') {
    handleMergeRequest($, item)
  }
  // unknown types are ignored, per protocol
}

/** False for a `-p` run or the SDK (session.start isInteractive): such sessions never poll the inbox. */
let interactive = true

async function pollInbox($: Engine): Promise<void> {
  if (polling) return
  polling = true
  try {
    // ?session= keeps items addressed to another Claude Code session queued for that session
    const path = `/inbox?wait=${INBOX_WAIT_S}&session=${encodeURIComponent(sessionId)}`
    const res = await request($, 'GET', path, undefined, (INBOX_WAIT_S + 10) * 1000)
    if (res === undefined) {
      lost($)
      return
    }
    if (res.status === 401) {
      forgetBridge()
      return
    }
    if (!res.ok) return
    for (const item of parseInbox(res.text)) route($, item)
  } catch {
    // never surface
  } finally {
    polling = false
  }
}

async function housekeep($: Engine): Promise<void> {
  if (housekeeping) return
  housekeeping = true
  try {
    rounds += 1
    if (waitRounds > 0) {
      waitRounds -= 1
      return
    }
    const every = connected ? DISCOVER_CONNECTED : DISCOVER_DISCONNECTED
    if (bridge === undefined || rounds - lastDiscover >= every) {
      lastDiscover = rounds
      await discover($)
    }
    if (bridge === undefined) return
    if (verifiedFor !== bridgeKey && !(await verify($))) return
    if (sessionId === '' || helloFor !== `${bridgeKey}|${sessionId}`) await hello($)
    // headless (-p / SDK) sessions only report: they never take inbox items, which would start
    // turns or run git merges in a scripted run, and an open long-poll would hold up its exit
    if (connected && !polling && interactive) void pollInbox($)
  } catch {
    // never surface
  } finally {
    housekeeping = false
  }
}

function startTimer($: Engine): void {
  ticker?.cancel()
  ticker = $.clock.every(TICK_MS, () => {
    tick += 1
    if (tasksDirty) {
      tasksDirty = false
      const snapshot = taskSnapshot()
      if (snapshot !== undefined && connected) {
        buffer.push(snapshot)
        if (buffer.length > BUFFER_MAX) buffer.splice(0, buffer.length - BUFFER_MAX)
      }
    }
    if (worktreesDirty && connected) {
      worktreesDirty = false
      refreshWorktrees($)
    }
    if (buffer.length > 0) void flush($)
    if (tick % HOUSEKEEPING_EVERY === 0) void housekeep($)
  })
}

async function readSessionFacts($: Engine, startCwd: string): Promise<void> {
  cwd = startCwd
  try {
    sessionId = await $.session.id()
  } catch {
    sessionId = ''
  }
  if (userHome === undefined) {
    try {
      userHome = (await $.env.get('HOME')) ?? (await $.env.get('USERPROFILE'))
    } catch {
      userHome = undefined
    }
  }
  let root = startCwd
  let isRepo = false
  try {
    const r = await $.session.repo()
    if (r !== null) {
      root = r.root
      isRepo = true
    }
  } catch {
    // not a repository
  }
  // Only a repository's own folder names the session, and never the home folder (account name).
  repo = isRepo && !isPersonalFolder(root, userHome) ? folderName(root) : undefined
  label = sessionLabel(repo, startCwd, userHome)
  try {
    const head = await $.fs.read(`${root}/.git/HEAD`)
    branch = typeof head === 'string' ? branchFromHead(head) : undefined
  } catch {
    branch = undefined
  }
  try {
    model = await $.session.model()
  } catch {
    model = undefined
  }
}

async function clockNow($: Engine): Promise<number> {
  try {
    return await $.clock.now()
  } catch {
    return now()
  }
}

/**
 * Long-polls the game for decision `id` until answered, unknown, unreachable, aborted, or 5 minutes
 * after `startedAt` (default: now). Undefined unless the player answered.
 */
async function askGame(
  $: Engine,
  id: string,
  signal: AbortSignal,
  startedAt?: number,
): Promise<{ option: string; text?: string } | undefined> {
  const started = startedAt ?? (await clockNow($))
  let elapsed = (await clockNow($)) - started
  // a misbehaving server that answers "open" at once must not spin forever
  let attempts = 2 * Math.ceil(DECISION_TOTAL_MS / (DECISION_WAIT_S * 1000))
  while (connected && elapsed < DECISION_TOTAL_MS && attempts > 0 && !signal.aborted) {
    attempts -= 1
    const left = Math.ceil((DECISION_TOTAL_MS - elapsed) / 1000)
    const wait = Math.max(1, Math.min(DECISION_WAIT_S, left))
    const path = `/decision/${encodeURIComponent(id)}?wait=${wait}`
    // raced against the hook's abort signal, so Esc returns at once instead of after the poll
    const res = await request($, 'GET', path, undefined, (wait + 10) * 1000, signal)
    if (signal.aborted) return undefined
    if (res === undefined || !res.ok) return undefined
    const view = parseDecision(res.text)
    if (view === undefined || view.status === 'unknown') return undefined
    if (view.status === 'answered') {
      return view.text === undefined ? { option: view.option } : { option: view.option, text: view.text }
    }
    try {
      elapsed = (await $.clock.now()) - started
    } catch {
      elapsed = now() - started
    }
  }
  return undefined
}

type AskInput = Extract<ToolCallInput, { tool: 'AskUserQuestion' }>

/**
 * AskUserQuestion in the game (protocol v0.2 "Questions"): one `question` decision per question,
 * `ask-<tool_use_id>-<i>`. Resolves the tool's own result once the player answered them all, or
 * undefined (after decision.closed for each id) so the caller runs the normal terminal dialog.
 */
async function askQuestions($: Engine, e: AskInput, signal: AbortSignal): Promise<ToolCallResult | undefined> {
  const questions = e.questions
  if (!Array.isArray(questions) || questions.length === 0) return undefined
  const agentId = e.agentId ?? leadId()
  const ids = questions.map((_, i) => `ask-${e.tool_use_id}-${i}`)
  const before = agents.get(agentId)
  const first = questions[0]?.question ?? 'question'
  cancelIdle(agentId)
  upsert(agentId, { state: 'waiting_user', activity: `waiting: ${first}`, station: 'user' })
  const restore = (): void => {
    upsert(
      agentId,
      before === undefined || before.state === 'waiting_user'
        ? { state: 'thinking', activity: 'thinking', station: 'desk' }
        : { state: before.state, activity: before.activity, station: before.station },
    )
  }
  try {
    return await askQuestionsIn($, e, signal, ids, agentId, first, restore)
  } catch {
    // any throw: close every id that may already be open, restore the agent, use the terminal dialog
    restore()
    for (const id of ids) enqueue('decision.closed', { id })
    return undefined
  }
}

async function askQuestionsIn(
  $: Engine,
  e: AskInput,
  signal: AbortSignal,
  ids: string[],
  agentId: string,
  first: string,
  restore: () => void,
): Promise<ToolCallResult | undefined> {
  const questions = e.questions
  questions.forEach((q, i) => {
    enqueue('decision.open', {
      decision: {
        id: ids[i],
        agentId,
        kind: 'question',
        question: q.question,
        options: (q.options ?? []).map(o => o.label),
        context: questionContext(q),
        multiSelect: q.multiSelect === true,
        allowText: true,
      },
    })
  })
  await flush($)

  const started = await clockNow($)
  const answers: Record<string, string> = {}
  let complete = true
  for (let i = 0; i < questions.length; i++) {
    const q = questions[i]
    const id = ids[i]
    if (q === undefined || id === undefined || !connected || signal.aborted) {
      complete = false
      break
    }
    const got = await askGame($, id, signal, started)
    if (got === undefined) {
      complete = false
      break
    }
    const answer = questionAnswer(got.option, got.text)
    if (answer !== undefined) answers[q.question] = answer
  }

  restore()
  if (!complete) {
    // timeout, unreachable or interrupted: the terminal dialog takes over
    for (const id of ids) enqueue('decision.closed', { id })
    return undefined
  }
  const ts = now()
  const summary = Object.entries(answers)
    .map(([q, a]) => `${q} → ${a}`)
    .join('\n')
  log(agentId, [
    { ts, kind: 'tool', text: truncate(`AskUserQuestion: ${first}`, 200) },
    { ts, kind: 'result', text: resultSnippet(summary === '' ? 'answered in RimWorld (no answer given)' : summary, 8, 600) },
  ])
  return { result: { questions, answers } }
}

// ---- hooks -------------------------------------------------------------------------

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    try {
      resetAll()
      interactive = e.isInteractive !== false
      await readSessionFacts($, e.cwd)
      startTimer($)
      status($, STATUS_OFF)
      void housekeep($) // first connection attempt right away
    } catch {
      // the game is decoration: never stop the session
    }
    return next(e)
  })

  // Goal cards (task board): every prompt that asks for work gets one, todo until its turn starts.
  on('prompt.submit', async ($, e, next) => {
    let seen = false
    try {
      if (sessionId !== '') {
        const origin = (e.origin as { kind?: unknown } | undefined)?.kind
        markTasks(
          tasks.promptSubmitted(sessionId, {
            text: e.text,
            ...(e.command !== undefined ? { command: { name: e.command.name, args: e.command.args } } : {}),
            ...(e.isMeta === true ? { isMeta: true } : {}),
            ...(typeof origin === 'string' ? { origin } : {}),
            ...(e.turnId !== undefined ? { turnId: e.turnId } : {}),
          }),
        )
        seen = true
      }
    } catch {
      // ignore
    }
    const entered = await next(e)
    try {
      if (seen && entered.drop !== undefined) markTasks(tasks.promptDropped(sessionId, e.text))
    } catch {
      // ignore
    }
    return entered
  }).catch(($, e, next) => next(e))

  on('turn.start', ($, e, next) => {
    try {
      const id = e.agentId ?? leadId()
      if (e.agentId === undefined) mainTurnId = e.turnId
      cancelIdle(id)
      upsert(id, { state: 'thinking', activity: 'thinking', station: 'desk' })
      if (sessionId !== '') markTasks(tasks.turnStarted(sessionId, e.turnId, e.text, e.agentId, leadId()))
    } catch {
      // ignore
    }
    return next(e)
  })

  on('tool.call', async ($, e, next) => {
    if (e.tool === 'AskUserQuestion' && connected && sessionId !== '') {
      // a throw here goes to .catch, which runs the normal dialog
      const answered = await askQuestions($, e, next.signal)
      if (answered !== undefined) return answered
    }
    let id = ''
    let args: Record<string, unknown> = {}
    try {
      id = e.agentId ?? leadId()
      args = argsOf(e)
      const state = stateForTool(e.tool, args)
      cancelIdle(id)
      upsert(id, { state, activity: activityFor(e.tool, args, cwd), station: stationForState(state) })
      flushIfLong($)
    } catch {
      // ignore
    }
    const ran = await next(e)
    try {
      if (id !== '') {
        const ts = now()
        const entries: LogEntry[] = [{ ts, kind: 'tool', text: toolSummary(e.tool, args, cwd) }]
        if (ran.deny !== undefined) {
          entries.push({ ts, kind: 'error', text: truncate(`denied: ${ran.deny}`, 300) })
        } else if (ran.isError === true) {
          entries.push({ ts, kind: 'error', text: resultSnippet(ran.text ?? 'error', 3, 300) })
        } else {
          const diff = diffFor(e.tool, args, cwd)
          if (diff !== undefined) entries.push({ ts, kind: 'diff', text: diff })
          else if (e.tool === 'Bash' && ran.text !== undefined && ran.text.trim() !== '') {
            entries.push({ ts, kind: 'result', text: resultSnippet(ran.text) })
          }
        }
        log(id, entries)
        closePending(id)
        flushIfLong($)
        if (TASK_TOOLS.has(e.tool) && ran.deny === undefined && ran.isError !== true && sessionId !== '') {
          markTasks(tasks.applyTool(sessionId, e.tool, args, ran.result, ran.text, id))
        }
      }
    } catch {
      // ignore
    }
    return ran
  }).catch(($, e, next) => next(e))

  // Backups for the task board when a TaskCreate result could not be read, or a task is completed
  // by a teammate the plugin does not see through tool.call.
  on('classic.TaskCreated', ($, e, next) => {
    try {
      if (sessionId !== '') {
        markTasks(tasks.applyCreated(sessionId, e.task_id, e.task_subject, e.task_description, e.agent_id ?? leadId()))
      }
    } catch {
      // ignore
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('classic.TaskCompleted', ($, e, next) => {
    try {
      if (sessionId !== '') markTasks(tasks.applyCompleted(sessionId, e.task_id, e.task_subject, e.agent_id ?? leadId()))
    } catch {
      // ignore
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('agent.spawn', async ($, e, next) => {
    const spawned = await next(e)
    try {
      if (spawned.agentId !== undefined) {
        const label = truncate(`${e.subagentType}: ${e.description}`, ACTIVITY_MAX)
        labels.set(spawned.agentId, label)
        upsert(spawned.agentId, {
          label,
          state: 'thinking',
          activity: e.description === '' ? 'starting' : e.description,
          station: 'desk',
        })
      }
      if (sessionId !== '' && spawned.deny === undefined) {
        markTasks(
          tasks.agentSpawned(sessionId, {
            ...(spawned.agentId !== undefined ? { agentId: spawned.agentId } : {}),
            toolUseId: e.tool_use_id,
            description: e.description,
            prompt: e.prompt,
            subagentType: e.subagentType,
            ...(e.parentAgentId !== undefined ? { parentAgentId: e.parentAgentId } : {}),
          }),
        )
      }
    } catch {
      // ignore
    }
    return spawned
  }).catch(($, e, next) => next(e))

  on('turn.complete', ($, e, next) => {
    try {
      const id = e.agentId ?? leadId()
      const failed = e.reason === 'error' || e.reason === 'refusal'
      upsert(
        id,
        failed
          ? { state: 'error', activity: e.reason === 'error' ? 'turn failed' : 'refused', station: 'desk' }
          : { state: 'done', activity: e.isAborted ? 'interrupted' : 'done', station: 'desk' },
      )
      if (e.answer.trim() !== '') log(id, [{ ts: now(), kind: 'text', text: truncate(e.answer, 600) }])
      scheduleIdle($, id)
      if (sessionId !== '') markTasks(tasks.turnCompleted(sessionId, e.turnId, e.agentId, e.reason))
      if (e.usage !== undefined) {
        const u = e.usage
        usage.inputTokens += (u.input_tokens || 0) + (u.cache_creation_input_tokens || 0)
        usage.outputTokens += u.output_tokens || 0
        usage.cacheReadTokens += u.cache_read_input_tokens || 0
        if (e.agentId === undefined) usage.turns += 1
        enqueue('usage.update', usagePayload())
      }
      if (e.agentId === undefined) {
        worktreesDirty = true // a fresh worktree.list after each turn
        mainTurnId = undefined
        closePending(undefined)
        if (activeGoal !== undefined) {
          const goal = activeGoal
          activeGoal = undefined
          const ok = e.reason === 'answer'
          const payload: Record<string, unknown> = { id: goal.id, text: goal.text, status: ok ? 'done' : 'failed' }
          if (ok) payload.progress = 1
          if (e.answer.trim() !== '') payload.summary = truncate(e.answer, 300)
          enqueue('goal.upsert', { goal: payload })
        }
      }
    } catch {
      // ignore
    }
    return next(e)
  })

  on('classic.PermissionRequest', async ($, e, next) => {
    if (!connected || bridge === undefined || sessionId === '') return next(e)
    const agentId = e.agent_id ?? leadId()
    if (permCounter === 0) permCounter = now()
    permCounter += 1
    const id = `perm-${permCounter}`
    const before = agents.get(agentId)
    const question = describePermission(e.tool_name, e.tool_input, cwd)
    // "Always allow" only when Claude Code suggested rules; they are what it then adds
    const suggestions =
      Array.isArray(e.permission_suggestions) && e.permission_suggestions.length > 0 ? e.permission_suggestions : undefined
    const context: Record<string, unknown> = { tool: e.tool_name, input: e.tool_input }
    if (suggestions !== undefined) {
      // the game prints context keys in order: this one is the last line
      context[ALWAYS_COVERS_KEY] = summarizeSuggestions(suggestions) || 'the rules Claude Code suggested'
    }
    upsert(agentId, { state: 'waiting_user', activity: `waiting: ${question}`, station: 'user' })
    enqueue('decision.open', {
      decision: {
        id,
        agentId,
        kind: 'permission',
        question,
        options: suggestions === undefined ? [...DECISION_OPTIONS] : [...DECISION_OPTIONS_ALWAYS],
        context,
      },
    })
    await flush($)

    const answer = connected ? await askGame($, id, next.signal) : undefined

    upsert(
      agentId,
      before === undefined
        ? { state: 'thinking', activity: 'thinking', station: 'desk' }
        : { state: before.state, activity: before.activity, station: before.station },
    )
    if (answer?.option === ALWAYS_ALLOW && suggestions !== undefined) {
      return { decision: { behavior: 'allow', updatedPermissions: suggestions } }
    }
    if (answer?.option === 'Allow once' || answer?.option === ALWAYS_ALLOW) {
      return { decision: { behavior: 'allow' } }
    }
    if (answer?.option === 'Deny') {
      return { decision: { behavior: 'deny', message: answer.text ?? 'Denied from RimWorld.' } }
    }
    // timeout, unreachable, interrupted or an unknown option: the normal prompt shows
    if (next.signal.aborted) enqueue('decision.closed', { id })
    else pendingClosed.push({ id, agentId })
    return next(e)
  }).catch(($, e, next) => next(e))

  on('session.end', async ($, e, next) => {
    try {
      if (connected && sessionId !== '') {
        const snapshot = tasksDirty ? taskSnapshot() : undefined
        tasksDirty = false
        if (snapshot !== undefined) buffer.push(snapshot)
        enqueue('session.end', {})
        await withTimeout($, flush($), 1_500)
      }
      if (e.reason === 'clear') {
        // the process goes on under a new session id: announced on the next round
        if (sessionId !== '') tasks.clear(sessionId)
        tasksDirty = false
        sessionId = ''
        helloFor = ''
        agents.clear()
        labels.clear()
        pendingClosed = []
        activeGoal = undefined
        mainTurnId = undefined
        usage = { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, turns: 0 }
      } else {
        ticker?.cancel()
        ticker = undefined
      }
    } catch {
      // ignore
    }
    return next(e)
  })
}
