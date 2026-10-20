// Pure helpers for the Colony Code bridge protocol v0.
// No `$` here: everything is a plain function so it is easy to test.

export type AgentState =
  | 'idle'
  | 'thinking'
  | 'reading'
  | 'editing'
  | 'running'
  | 'testing'
  | 'waiting_user'
  | 'blocked'
  | 'done'
  | 'error'

export type Station = 'desk' | 'terminal' | 'lounge' | 'user'

export type LogKind = 'text' | 'tool' | 'result' | 'error' | 'diff'

export type Agent = {
  id: string
  role: 'lead' | 'worker'
  label?: string
  state: AgentState
  activity: string
  station: Station
}

export type LogEntry = { ts: number; kind: LogKind; text: string }

export type Bridge = { port: number; token: string }

export type InboxItem = {
  id: string
  type: string
  ts?: number
  sessionId?: string
  text?: string
  to?: string
  agentId?: string
  /** diff.request / merge.request: echoed in diff.result / merge.result. */
  requestId?: string
  /** diff.request / merge.request: the worktree's id (its folder name). */
  worktree?: string
  /** merge.request: merge | request_changes | reject. */
  action?: string
}

export type DecisionAnswer =
  | { status: 'open' }
  | { status: 'unknown' }
  | { status: 'answered'; option: string; text?: string }

export const ACTIVITY_MAX = 48
export const DECISION_OPTIONS = ['Allow once', 'Deny'] as const
/** Permission options when the request carried permission_suggestions (protocol v0.2). */
export const DECISION_OPTIONS_ALWAYS = ['Allow once', 'Always allow', 'Deny'] as const
export const ALWAYS_ALLOW = 'Always allow'
/** The context key whose line the game shows last: `Always allow covers: <summary>`. */
export const ALWAYS_COVERS_KEY = 'Always allow covers'

const READ_TOOLS = new Set(['Read', 'Grep', 'Glob', 'LS', 'WebFetch', 'WebSearch'])
const EDIT_TOOLS = new Set(['Edit', 'Write', 'MultiEdit', 'NotebookEdit'])
const TEST_COMMAND = /test|jest|vitest|pytest|cargo test|go test|npm run check/

export const leadIdFor = (sessionId: string): string => `lead-${sessionId.slice(0, 8)}`

export function truncate(text: string, max: number): string {
  const flat = text.replace(/\s+/g, ' ').trim()
  return flat.length <= max ? flat : `${flat.slice(0, Math.max(0, max - 1))}…`
}

/** Shortens a path for an activity line: relative to cwd, cut from the left. */
export function shortPath(path: string, cwd: string, max = 36): string {
  let p = path
  if (cwd !== '' && p.startsWith(cwd)) {
    p = p.slice(cwd.length).replace(/^[\\/]+/, '')
  }
  if (p === '') p = '.'
  return p.length <= max ? p : `…${p.slice(p.length - (max - 1))}`
}

const str = (v: unknown): string => (typeof v === 'string' ? v : '')

/** The tool's arguments as a loose record (e is a union per tool). */
export function argsOf(e: object): Record<string, unknown> {
  return e as Record<string, unknown>
}

export function stateForTool(tool: string, args: Record<string, unknown>): AgentState {
  if (READ_TOOLS.has(tool)) return 'reading'
  if (EDIT_TOOLS.has(tool)) return 'editing'
  if (tool === 'Bash') return TEST_COMMAND.test(str(args.command)) ? 'testing' : 'running'
  return 'thinking'
}

export function stationForState(state: AgentState): Station {
  switch (state) {
    case 'running':
    case 'testing':
      return 'terminal'
    case 'waiting_user':
      return 'user'
    case 'idle':
      return 'lounge'
    default:
      return 'desk'
  }
}

/** The thing a tool works on, for activity lines and log summaries. */
export function toolTarget(tool: string, args: Record<string, unknown>, cwd: string): string {
  const path = str(args.file_path) || str(args.notebook_path) || str(args.path)
  switch (tool) {
    case 'Read':
    case 'Edit':
    case 'Write':
    case 'MultiEdit':
    case 'NotebookEdit':
    case 'LS':
      return path === '' ? '' : shortPath(path, cwd)
    case 'Grep':
    case 'Glob':
      return str(args.pattern)
    case 'WebFetch':
      return str(args.url)
    case 'WebSearch':
      return str(args.query)
    case 'Bash':
      return str(args.command)
    case 'Agent':
    case 'Task':
      return str(args.description)
    default:
      return path === '' ? '' : shortPath(path, cwd)
  }
}

const VERB: Record<AgentState, string> = {
  idle: 'idle',
  thinking: 'thinking',
  reading: 'reading',
  editing: 'editing',
  running: 'running',
  testing: 'testing',
  waiting_user: 'waiting for you',
  blocked: 'blocked',
  done: 'done',
  error: 'error',
}

export function activityFor(tool: string, args: Record<string, unknown>, cwd: string): string {
  const state = stateForTool(tool, args)
  const target = toolTarget(tool, args, cwd)
  let verb = VERB[state]
  if (tool === 'Grep' || tool === 'Glob' || tool === 'WebSearch') verb = 'searching'
  if (tool === 'Agent' || tool === 'Task') verb = 'delegating'
  if (state === 'thinking' && tool !== 'Agent' && tool !== 'Task') {
    return truncate(target === '' ? `using ${tool}` : `${tool} ${target}`, ACTIVITY_MAX)
  }
  return truncate(target === '' ? `${verb} (${tool})` : `${verb} ${target}`, ACTIVITY_MAX)
}

/** One line for the `tool` log entry. */
export function toolSummary(tool: string, args: Record<string, unknown>, cwd: string): string {
  const target = toolTarget(tool, args, cwd)
  return truncate(target === '' ? tool : `${tool}: ${target}`, 200)
}

const MAX_DIFF_LINES = 80
const MAX_DIFF_CHARS = 6000

function prefixed(text: string, sign: '+' | '-'): string[] {
  if (text === '') return []
  return text.split('\n').map(line => `${sign}${line}`)
}

/** A unified-style diff text for Edit / MultiEdit / Write, or undefined. */
export function diffFor(tool: string, args: Record<string, unknown>, cwd: string): string | undefined {
  const path = str(args.file_path)
  if (path === '') return undefined
  const rel = shortPath(path, cwd, 200)
  const lines: string[] = [`--- a/${rel}`, `+++ b/${rel}`]
  if (tool === 'Edit') {
    lines.push('@@', ...prefixed(str(args.old_string), '-'), ...prefixed(str(args.new_string), '+'))
  } else if (tool === 'MultiEdit' && Array.isArray(args.edits)) {
    for (const one of args.edits as unknown[]) {
      if (one === null || typeof one !== 'object') continue
      const edit = one as Record<string, unknown>
      lines.push('@@', ...prefixed(str(edit.old_string), '-'), ...prefixed(str(edit.new_string), '+'))
    }
  } else if (tool === 'Write') {
    lines.push('@@', ...prefixed(str(args.content), '+'))
  } else {
    return undefined
  }
  const cut = lines.slice(0, MAX_DIFF_LINES)
  if (lines.length > MAX_DIFF_LINES) cut.push(` … ${lines.length - MAX_DIFF_LINES} more lines`)
  const text = cut.join('\n')
  return text.length > MAX_DIFF_CHARS ? `${text.slice(0, MAX_DIFF_CHARS)}\n … cut` : text
}

/** First few non-empty lines of a tool's output. */
export function resultSnippet(text: string, maxLines = 4, maxChars = 400): string {
  const lines = text
    .split('\n')
    .map(l => l.trimEnd())
    .filter(l => l.trim() !== '')
    .slice(0, maxLines)
  const joined = lines.join('\n')
  return joined.length <= maxChars ? joined : `${joined.slice(0, maxChars - 1)}…`
}

/** The question shown in the game for a permission request. */
export function describePermission(tool: string, input: unknown, cwd: string): string {
  const args = input !== null && typeof input === 'object' ? (input as Record<string, unknown>) : {}
  switch (tool) {
    case 'Bash':
      return truncate(`Run: ${str(args.command)}`, 200)
    case 'Edit':
    case 'MultiEdit':
      return truncate(`Edit ${shortPath(str(args.file_path), cwd, 160)}`, 200)
    case 'Write':
      return truncate(`Write ${shortPath(str(args.file_path), cwd, 160)}`, 200)
    case 'WebFetch':
      return truncate(`Fetch ${str(args.url)}`, 200)
    default: {
      const target = toolTarget(tool, args, cwd)
      return truncate(target === '' ? `Use ${tool}` : `Use ${tool}: ${target}`, 200)
    }
  }
}

export function parseBridge(text: string): Bridge | undefined {
  let raw: unknown
  try {
    raw = JSON.parse(text)
  } catch {
    return undefined
  }
  if (raw === null || typeof raw !== 'object') return undefined
  const o = raw as Record<string, unknown>
  const port = o.port
  const token = o.token
  if (typeof port !== 'number' || !Number.isInteger(port) || port <= 0 || port > 65535) return undefined
  if (typeof token !== 'string' || token === '') return undefined
  if (o.protocol !== undefined && o.protocol !== 0) return undefined
  return { port, token }
}

export function parseInbox(text: string): InboxItem[] {
  let raw: unknown
  try {
    raw = JSON.parse(text)
  } catch {
    return []
  }
  if (!Array.isArray(raw)) return []
  const items: InboxItem[] = []
  for (const one of raw) {
    if (one === null || typeof one !== 'object') continue
    const o = one as Record<string, unknown>
    if (typeof o.id !== 'string' || typeof o.type !== 'string') continue
    const item: InboxItem = { id: o.id, type: o.type }
    if (typeof o.ts === 'number') item.ts = o.ts
    if (typeof o.sessionId === 'string') item.sessionId = o.sessionId
    if (typeof o.text === 'string') item.text = o.text
    if (typeof o.to === 'string') item.to = o.to
    if (typeof o.agentId === 'string') item.agentId = o.agentId
    if (typeof o.requestId === 'string') item.requestId = o.requestId
    if (typeof o.worktree === 'string') item.worktree = o.worktree
    if (typeof o.action === 'string') item.action = o.action
    items.push(item)
  }
  return items
}

export function parseDecision(text: string): DecisionAnswer | undefined {
  let raw: unknown
  try {
    raw = JSON.parse(text)
  } catch {
    return undefined
  }
  if (raw === null || typeof raw !== 'object') return undefined
  const o = raw as Record<string, unknown>
  if (o.status === 'open') return { status: 'open' }
  if (o.status === 'unknown') return { status: 'unknown' }
  if (o.status === 'answered' && typeof o.option === 'string') {
    return typeof o.text === 'string' && o.text !== ''
      ? { status: 'answered', option: o.option, text: o.text }
      : { status: 'answered', option: o.option }
  }
  return undefined
}

/** `repo` for session.start: the last folder name of a path. */
export function folderName(path: string): string {
  const parts = path.split(/[\\/]+/).filter(p => p !== '')
  return parts[parts.length - 1] ?? path
}

/** The branch from a `.git/HEAD` file's text, or undefined when detached. */
export function branchFromHead(text: string): string | undefined {
  const m = /^ref:\s*refs\/heads\/(.+)$/m.exec(text.trim())
  return m?.[1]?.trim()
}

// ---- server proof -------------------------------

export const CHALLENGE_HEADER = 'X-Colony-Challenge'

const toHex = (bytes: Uint8Array): string => Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('')

/** `n` random bytes as lowercase hex. */
export function randomHex(n = 16): string {
  return toHex(crypto.getRandomValues(new Uint8Array(n)))
}

async function sha256(data: Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest('SHA-256', data))
}

/**
 * Lowercase hex HMAC-SHA256(key, message), both UTF-8 (RFC 2104). Built on digest()
 * because the hooks environment's crypto.subtle offers no importKey/sign.
 */
export async function hmacSha256Hex(key: string, message: string): Promise<string> {
  const enc = new TextEncoder()
  const BLOCK = 64
  let k = enc.encode(key)
  if (k.length > BLOCK) k = await sha256(k)
  const ipad = new Uint8Array(BLOCK)
  const opad = new Uint8Array(BLOCK)
  for (let i = 0; i < BLOCK; i++) {
    const b = k[i] ?? 0
    ipad[i] = b ^ 0x36
    opad[i] = b ^ 0x5c
  }
  const msg = enc.encode(message)
  const inner = new Uint8Array(BLOCK + msg.length)
  inner.set(ipad)
  inner.set(msg, BLOCK)
  const innerHash = await sha256(inner)
  const outer = new Uint8Array(BLOCK + innerHash.length)
  outer.set(opad)
  outer.set(innerHash, BLOCK)
  return toHex(await sha256(outer))
}

/** The `proof` field of a /health answer, or undefined. */
export function parseProof(text: string): string | undefined {
  try {
    const raw = JSON.parse(text) as unknown
    if (raw === null || typeof raw !== 'object') return undefined
    const proof = (raw as Record<string, unknown>).proof
    return typeof proof === 'string' ? proof : undefined
  } catch {
    return undefined
  }
}

// ---- permission suggestions ("Always allow", protocol v0.2) ------------------------------

const DESTINATION: Record<string, string> = {
  session: 'this session only',
  localSettings: 'this project (your local settings)',
  projectSettings: 'this project (shared project settings)',
  userSettings: 'all your projects (user settings)',
  cliArg: 'this run',
}

function ruleText(rule: unknown): string | undefined {
  if (rule === null || typeof rule !== 'object') return undefined
  const r = rule as Record<string, unknown>
  const tool = str(r.toolName)
  if (tool === '') return undefined
  const content = str(r.ruleContent)
  return content === '' ? tool : `${tool}(${content})`
}

/**
 * One line a person can read for a request's permission_suggestions (PermissionUpdate[]), e.g.
 * `allow Bash(npm install:*) for this project (your local settings)`. Empty when none is readable.
 */
export function summarizeSuggestions(suggestions: unknown): string {
  if (!Array.isArray(suggestions)) return ''
  const parts: string[] = []
  for (const one of suggestions) {
    if (one === null || typeof one !== 'object') continue
    const u = one as Record<string, unknown>
    const where = DESTINATION[str(u.destination)] ?? str(u.destination)
    const tail = where === '' ? '' : ` for ${where}`
    switch (u.type) {
      case 'addRules':
      case 'replaceRules': {
        const rules = Array.isArray(u.rules) ? u.rules.map(ruleText).filter((r): r is string => r !== undefined) : []
        if (rules.length === 0) break
        const verb = str(u.behavior) === '' ? 'allow' : str(u.behavior)
        parts.push(`${verb} ${rules.join(', ')}${tail}`)
        break
      }
      case 'removeRules': {
        const rules = Array.isArray(u.rules) ? u.rules.map(ruleText).filter((r): r is string => r !== undefined) : []
        if (rules.length > 0) parts.push(`remove ${str(u.behavior) || 'allow'} rules ${rules.join(', ')}${tail}`)
        break
      }
      case 'setMode':
        if (str(u.mode) !== '') parts.push(`switch to ${str(u.mode)} mode${tail}`)
        break
      case 'addDirectories': {
        const dirs = Array.isArray(u.directories) ? u.directories.filter((d): d is string => typeof d === 'string') : []
        if (dirs.length > 0) parts.push(`access to ${dirs.join(', ')}${tail}`)
        break
      }
      case 'removeDirectories': {
        const dirs = Array.isArray(u.directories) ? u.directories.filter((d): d is string => typeof d === 'string') : []
        if (dirs.length > 0) parts.push(`no more access to ${dirs.join(', ')}${tail}`)
        break
      }
      default:
        break
    }
  }
  return truncate(parts.join('; '), 400)
}

// ---- questions (AskUserQuestion, protocol v0.2) ----------------------------------------

export type AskQuestion = {
  question: string
  header?: string
  options?: readonly { label: string; description?: string }[]
  multiSelect?: boolean
}

/** decision.open `context` for one question: the header plus each option's description. */
export function questionContext(q: AskQuestion): string {
  const lines: string[] = []
  if (str(q.header) !== '') lines.push(str(q.header))
  for (const o of q.options ?? []) {
    const d = str(o.description)
    lines.push(d === '' ? `- ${o.label}` : `- ${o.label}: ${d}`)
  }
  return lines.join('\n')
}

/**
 * The AskUserQuestion answer string for a game answer: the picked labels (the game joins a
 * multiSelect answer with newlines; the tool result wants them comma-separated), then the free
 * text. Undefined when the player left it empty.
 */
export function questionAnswer(option: string, text: string | undefined): string | undefined {
  const parts = option
    .split('\n')
    .map(l => l.trim())
    .filter(l => l !== '')
  const t = (text ?? '').trim()
  if (t !== '') parts.push(t)
  return parts.length === 0 ? undefined : parts.join(', ')
}

// ---- privacy: never show the player's account name in the game ------------------------

const HOME_PATTERNS = [/^\/Users\/[^/]+/, /^\/home\/[^/]+/, /^[A-Za-z]:[\\/]Users[\\/][^\\/]+/i]

const trimSlashes = (p: string): string => p.replace(/[\\/]+$/, '')

/**
 * True for folders whose name is the account name or tells nothing: the home folder itself
 * (`/Users/<name>`, `/home/<name>`, `C:\Users\<name>`, or `userHome`), its parents and the root.
 */
export function isPersonalFolder(path: string, userHome?: string): boolean {
  const p = trimSlashes(path.trim())
  if (p === '' || p === '~' || /^[A-Za-z]:$/.test(p)) return true
  if (userHome !== undefined && userHome !== '') {
    const h = trimSlashes(userHome)
    if (p === h || h.startsWith(`${p}/`) || h.startsWith(`${p}\\`)) return true
  }
  if (/^(\/Users|\/home|[A-Za-z]:[\\/]Users)$/i.test(p)) return true
  return HOME_PATTERNS.some(re => (re.exec(p)?.[0] ?? '') === p)
}

/** `path` with the home folder prefix shown as `~`, so it never carries the account name. */
export function privatePath(path: string, userHome?: string): string {
  if (userHome !== undefined && userHome !== '') {
    const h = trimSlashes(userHome)
    if (path === h) return '~'
    if (path.startsWith(`${h}/`) || path.startsWith(`${h}\\`)) return `~${path.slice(h.length)}`
  }
  for (const re of HOME_PATTERNS) {
    const m = re.exec(path)
    if (m !== null) return `~${path.slice(m[0].length)}`
  }
  return path
}

/**
 * The serialized event batch with every home-folder path replaced by `~`: logs, diffs, activity
 * lines, permission questions and worktree paths never carry the account name to the game.
 */
export function scrubHome(json: string, userHome?: string): string {
  let out = json
  if (userHome !== undefined && userHome.length > 1) {
    const h = trimSlashes(userHome)
    // In JSON a backslash is escaped, so match both spellings of a Windows home.
    for (const v of new Set([h, JSON.stringify(h).slice(1, -1)])) out = out.split(v).join('~')
  }
  return out
    .replace(/\/Users\/[^/"\\\s]+/g, '~')
    .replace(/\/home\/[^/"\\\s]+/g, '~')
    .replace(/[A-Za-z]:(\\\\|\/)Users(\\\\|\/)[^\\/"\s]+/gi, '~')
}

/**
 * `label` for session.start: the repository's folder name, else the cwd's last segment, but never
 * a folder that is or names the player's home folder (that is the account name). '' when none.
 */
export function sessionLabel(repo: string | undefined, cwd: string, userHome?: string): string {
  const r = (repo ?? '').trim()
  if (r !== '') return r
  if (isPersonalFolder(cwd, userHome)) return ''
  return folderName(cwd)
}
