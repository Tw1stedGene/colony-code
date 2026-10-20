// Colony Code review: worktrees, diffs and merges for the game's review UI.
//
// Self-contained on purpose: no imports and no `$`. Git runs through a `Runner` the caller hands
// in: the plugin passes `$.process.run`, the tools/test suite passes Node's execFile against a
// real temporary repository. Rules kept here:
// - git always runs by argv, never through a shell;
// - nothing here pushes, fetches or touches a remote;
// - branch names and paths only ever come from git's own output, never from the game: the game
//   names a worktree by its folder name and that is looked up in `git worktree list`.

export type RunResult = { exitCode: number; stdout: string; stderr: string; isStdoutTruncated?: boolean }

/** Runs `argv` (argv[0] is the executable) in `cwd`; rejects when it cannot start. */
export type Runner = (
  argv: readonly string[],
  opts: { cwd: string; timeoutMs: number; env: Record<string, string> },
) => Promise<RunResult>

export type WorktreeInfo = {
  id: string
  path: string
  branch: string
  ahead: number
  files: number
  additions: number
  deletions: number
  agentId?: string
}

export type WorktreeList = {
  /** The session checkout's top folder (not sent to the game). */
  root: string
  base: string
  dirty: boolean
  worktrees: WorktreeInfo[]
}

export type DiffLine = { kind: 'add' | 'del' | 'ctx'; text: string; oldNo?: number; newNo?: number }
export type DiffHunk = { header: string; lines: DiffLine[] }
export type DiffFile = { path: string; status: 'added' | 'modified' | 'deleted' | 'renamed'; hunks: DiffHunk[] }
export type DiffPayload = { files: DiffFile[]; truncated?: boolean; error?: string }

export type ReviewAction = 'merge' | 'request_changes' | 'reject'
export type ReviewOutcome = { ok: boolean; message: string; branch?: string }

export const MAX_FILES = 200
export const MAX_LINES = 5000
export const MAX_LINE_CHARS = 400
export const MAX_WORKTREES = 30

const GIT_ENV: Record<string, string> = {
  GIT_TERMINAL_PROMPT: '0',
  GIT_MERGE_AUTOEDIT: 'no',
  GIT_EDITOR: 'true',
  GIT_PAGER: 'cat',
  LC_ALL: 'C',
}
const QUICK_MS = 15_000
const SLOW_MS = 120_000

// ---- parsers (pure) -------------------------------------------------------------------

export type PorcelainEntry = {
  path: string
  head?: string
  /** Short branch name (refs/heads/ stripped); absent when detached or bare. */
  branch?: string
  bare: boolean
  detached: boolean
  locked: boolean
  prunable: boolean
}

/** `git worktree list --porcelain` (newline-separated records, blank line between them). */
export function parseWorktreePorcelain(text: string): PorcelainEntry[] {
  const out: PorcelainEntry[] = []
  let cur: PorcelainEntry | undefined
  for (const raw of text.split('\n')) {
    const line = raw.replace(/\r$/, '')
    if (line === '') {
      if (cur !== undefined) out.push(cur)
      cur = undefined
      continue
    }
    const sp = line.indexOf(' ')
    const key = sp < 0 ? line : line.slice(0, sp)
    const value = sp < 0 ? '' : line.slice(sp + 1)
    if (key === 'worktree') {
      if (cur !== undefined) out.push(cur)
      cur = { path: value, bare: false, detached: false, locked: false, prunable: false }
      continue
    }
    if (cur === undefined) continue
    if (key === 'HEAD') cur.head = value
    else if (key === 'branch') cur.branch = value.replace(/^refs\/heads\//, '')
    else if (key === 'bare') cur.bare = true
    else if (key === 'detached') cur.detached = true
    else if (key === 'locked') cur.locked = true
    else if (key === 'prunable') cur.prunable = true
  }
  if (cur !== undefined) out.push(cur)
  return out
}

/** ` 3 files changed, 10 insertions(+), 2 deletions(-)`; empty text is all zeros. */
export function parseShortstat(text: string): { files: number; additions: number; deletions: number } {
  const num = (re: RegExp): number => {
    const m = re.exec(text)
    return m?.[1] === undefined ? 0 : Number(m[1])
  }
  return {
    files: num(/(\d+) files? changed/),
    additions: num(/(\d+) insertions?\(\+\)/),
    deletions: num(/(\d+) deletions?\(-\)/),
  }
}

/** Undoes git's C-style quoting of a path ("a\tb" → a<TAB>b, octal escapes as UTF-8 bytes). */
export function unquotePath(p: string): string {
  if (!(p.length >= 2 && p.startsWith('"') && p.endsWith('"'))) return p
  const body = p.slice(1, -1)
  const bytes: number[] = []
  const pushText = (s: string): void => {
    for (const b of new TextEncoder().encode(s)) bytes.push(b)
  }
  for (let i = 0; i < body.length; i++) {
    const ch = body[i] ?? ''
    if (ch !== '\\') {
      pushText(ch)
      continue
    }
    const nx = body[i + 1] ?? ''
    if (/[0-7]/.test(nx)) {
      const oct = body.slice(i + 1, i + 4)
      bytes.push(parseInt(oct, 8) & 0xff)
      i += 3
      continue
    }
    const map: Record<string, string> = { n: '\n', t: '\t', r: '\r', '"': '"', '\\': '\\', a: '\x07', b: '\b', f: '\f', v: '\v' }
    pushText(map[nx] ?? nx)
    i += 1
  }
  return new TextDecoder().decode(new Uint8Array(bytes))
}

/** The path after a/ or b/ in a ---/+++ line, or undefined for /dev/null. */
function sidePath(rest: string): string | undefined {
  const t = unquotePath(rest.replace(/\t.*$/, '').trim())
  if (t === '/dev/null') return undefined
  return t.replace(/^[ab]\//, '')
}

/** The path from `diff --git a/X b/X` when both sides are the same (binary files, mode changes). */
function pathFromGitLine(rest: string): string | undefined {
  if (rest.startsWith('"')) {
    const m = /^"((?:[^"\\]|\\.)*)" "((?:[^"\\]|\\.)*)"$/.exec(rest)
    if (m?.[2] !== undefined) return unquotePath(`"${m[2]}"`).replace(/^b\//, '')
    return undefined
  }
  if (!rest.startsWith('a/')) return undefined
  if ((rest.length - 1) % 2 !== 0) return undefined
  const half = (rest.length - 1) / 2
  const a = rest.slice(0, half)
  const b = rest.slice(half + 1)
  return a.slice(2) === b.slice(2) && b.startsWith('b/') ? b.slice(2) : undefined
}

const clipLine = (s: string): string => (s.length <= MAX_LINE_CHARS ? s : `${s.slice(0, MAX_LINE_CHARS - 1)}…`)

/**
 * A unified diff (`git diff` output) as files → hunks → lines with line numbers. At most
 * `maxFiles` files and `maxLines` lines in all; past either cap `truncated` is true.
 * Hunk bodies are read by the counts in their `@@ -a,b +c,d @@` header, so a removed line that
 * itself starts with `-- ` is never taken for a file header.
 */
export function parseUnifiedDiff(text: string, maxFiles = MAX_FILES, maxLines = MAX_LINES): DiffPayload {
  const files: DiffFile[] = []
  let truncated = false
  let lineCount = 0
  let file: DiffFile | undefined
  let hunk: DiffHunk | undefined
  let oldNo = 0
  let newNo = 0
  let oldLeft = 0
  let newLeft = 0

  const lines = text.split('\n')
  if (lines[lines.length - 1] === '') lines.pop()
  for (const raw of lines) {
    const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw
    if (oldLeft > 0 || newLeft > 0) {
      if (line.startsWith('\\')) continue // "\ No newline at end of file"
      const sign = line[0] ?? ' '
      const kind: DiffLine['kind'] = sign === '+' ? 'add' : sign === '-' ? 'del' : 'ctx'
      const keep = file !== undefined && hunk !== undefined
      if (keep && lineCount >= maxLines) truncated = true
      const record = keep && lineCount < maxLines
      const body = clipLine(line.slice(1))
      if (kind === 'add') {
        if (record) hunk?.lines.push({ kind, text: body, newNo })
        newNo += 1
        newLeft -= 1
      } else if (kind === 'del') {
        if (record) hunk?.lines.push({ kind, text: body, oldNo })
        oldNo += 1
        oldLeft -= 1
      } else {
        if (record) hunk?.lines.push({ kind, text: body, oldNo, newNo })
        oldNo += 1
        newNo += 1
        oldLeft -= 1
        newLeft -= 1
      }
      if (record) lineCount += 1
      continue
    }
    if (line.startsWith('\\')) continue
    if (line.startsWith('diff --git ')) {
      hunk = undefined
      if (files.length >= maxFiles) {
        truncated = true
        file = undefined
        continue
      }
      file = { path: pathFromGitLine(line.slice('diff --git '.length)) ?? '', status: 'modified', hunks: [] }
      files.push(file)
      continue
    }
    if (line.startsWith('@@')) {
      const m = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(line)
      if (m === null) continue
      oldNo = Number(m[1])
      oldLeft = m[2] === undefined ? 1 : Number(m[2])
      newNo = Number(m[3])
      newLeft = m[4] === undefined ? 1 : Number(m[4])
      if (file !== undefined) {
        hunk = { header: clipLine(line), lines: [] }
        file.hunks.push(hunk)
      }
      continue
    }
    if (file === undefined) continue
    if (line.startsWith('new file mode')) file.status = 'added'
    else if (line.startsWith('deleted file mode')) file.status = 'deleted'
    else if (line.startsWith('rename from ')) file.status = 'renamed'
    else if (line.startsWith('rename to ')) file.path = unquotePath(line.slice('rename to '.length))
    else if (line.startsWith('copy to ')) {
      file.status = 'added'
      file.path = unquotePath(line.slice('copy to '.length))
    } else if (line.startsWith('--- ')) {
      const p = sidePath(line.slice(4))
      if (p === undefined) file.status = 'added'
      else if (file.path === '') file.path = p
    } else if (line.startsWith('+++ ')) {
      const p = sidePath(line.slice(4))
      if (p === undefined) file.status = 'deleted'
      else file.path = p
    }
  }
  return truncated ? { files, truncated: true } : { files }
}

/** `git diff` output as the diff.result payload (same as parseUnifiedDiff). */
export const parseGitDiff = parseUnifiedDiff

/** The folder name of a path: a worktree's id. */
export function worktreeId(path: string): string {
  const parts = path.split(/[\\/]+/).filter(p => p !== '')
  return parts[parts.length - 1] ?? path
}

const samePath = (a: string, b: string): boolean => a.replace(/[\\/]+$/, '') === b.replace(/[\\/]+$/, '')

// ---- git -------------------------------------------------------------------------------

async function git(run: Runner, cwd: string, args: readonly string[], timeoutMs = QUICK_MS): Promise<RunResult | undefined> {
  try {
    return await run(['git', '-c', 'core.quotepath=false', '-c', 'color.ui=never', ...args], {
      cwd,
      timeoutMs,
      env: GIT_ENV,
    })
  } catch {
    return undefined
  }
}

const firstLine = (s: string): string => s.split('\n').map(l => l.trim()).find(l => l !== '') ?? ''

/**
 * The worktrees of the repository `cwd` is in, besides the session's own checkout, with how far
 * each branch is ahead of the checkout's branch. Undefined when `cwd` is not in a git repository
 * or git cannot run.
 */
export async function listWorktrees(run: Runner, cwd: string, agentIds: readonly string[] = []): Promise<WorktreeList | undefined> {
  const top = await git(run, cwd, ['rev-parse', '--show-toplevel'])
  if (top === undefined || top.exitCode !== 0) return undefined
  const root = top.stdout.trim()
  if (root === '') return undefined
  const head = await git(run, root, ['symbolic-ref', '--quiet', '--short', 'HEAD'])
  const base = head !== undefined && head.exitCode === 0 && head.stdout.trim() !== '' ? head.stdout.trim() : 'HEAD'
  const status = await git(run, root, ['status', '--porcelain', '--untracked-files=no'])
  const dirty = status !== undefined && status.exitCode === 0 && status.stdout.trim() !== ''
  const listed = await git(run, root, ['worktree', 'list', '--porcelain'])
  const entries = listed !== undefined && listed.exitCode === 0 ? parseWorktreePorcelain(listed.stdout) : []
  const worktrees: WorktreeInfo[] = []
  for (const e of entries) {
    if (worktrees.length >= MAX_WORKTREES) break
    if (e.bare || e.prunable || e.branch === undefined || samePath(e.path, root) || e.branch === base) continue
    const branch = e.branch
    const count = await git(run, root, ['rev-list', '--count', `${base}..${branch}`])
    const stat = await git(run, root, ['diff', '--shortstat', `${base}...${branch}`])
    const s = parseShortstat(stat !== undefined && stat.exitCode === 0 ? stat.stdout : '')
    // folder names can repeat (../a/fix and ../b/fix): later ones get -2, -3, ... so an id
    // always names exactly one worktree (reject removes it with --force)
    const folder = worktreeId(e.path)
    let id = folder
    for (let n = 2; worktrees.some(w => w.id === id); n++) id = `${folder}-${n}`
    const info: WorktreeInfo = {
      id,
      path: e.path,
      branch,
      ahead: count !== undefined && count.exitCode === 0 ? Number(count.stdout.trim()) || 0 : 0,
      files: s.files,
      additions: s.additions,
      deletions: s.deletions,
    }
    const agent = agentIds.find(a => a !== '' && (id.includes(a) || branch.includes(a)))
    if (agent !== undefined) info.agentId = agent
    worktrees.push(info)
  }
  return { root, base, dirty, worktrees }
}

/**
 * diff.request: with no worktree, the checkout's uncommitted tracked changes vs HEAD; with one,
 * everything its branch and working tree changed since it left `base` (the merge base), which is
 * `<base>...<branch>` plus the worktree's uncommitted changes.
 */
export async function diffFor(run: Runner, list: WorktreeList, worktree?: string): Promise<DiffPayload> {
  const flags = ['diff', '--no-color', '--no-ext-diff', '-M']
  let res: RunResult | undefined
  if (worktree === undefined || worktree === '') {
    res = await git(run, list.root, [...flags, 'HEAD'], SLOW_MS)
  } else {
    const wt = list.worktrees.find(w => w.id === worktree)
    if (wt === undefined) return { files: [], error: `no worktree named ${worktree}` }
    const mb = await git(run, list.root, ['merge-base', list.base, wt.branch])
    if (mb === undefined || mb.exitCode !== 0 || mb.stdout.trim() === '') {
      return { files: [], error: `${wt.branch} shares no history with ${list.base}` }
    }
    res = await git(run, wt.path, [...flags, mb.stdout.trim()], SLOW_MS)
  }
  if (res === undefined) return { files: [], error: 'git could not run' }
  if (res.exitCode !== 0) return { files: [], error: firstLine(res.stderr) || `git diff exited ${res.exitCode}` }
  const payload = parseGitDiff(res.stdout)
  if (res.isStdoutTruncated === true) payload.truncated = true
  return payload
}

/**
 * merge.request. `merge` merges the worktree's branch into the checkout's branch (a merge commit,
 * by the user's own git identity), refused when the checkout has uncommitted tracked changes; a
 * conflict is aborted and reported. `reject` removes the worktree and deletes its branch.
 * `request_changes` runs no git: the caller submits the prompt.
 */
export async function review(
  run: Runner,
  list: WorktreeList,
  worktree: string,
  action: ReviewAction,
): Promise<ReviewOutcome> {
  const matches = list.worktrees.filter(w => w.id === worktree)
  if (matches.length > 1) return { ok: false, message: `More than one worktree is named ${worktree}; refusing.` }
  const wt = matches[0]
  if (wt === undefined) return { ok: false, message: `No worktree named ${worktree}.` }
  const branch = wt.branch
  if (action === 'request_changes') return { ok: true, message: `Changes requested on ${branch}.`, branch }

  if (action === 'merge') {
    if (list.base === 'HEAD') return { ok: false, message: 'The main checkout is not on a branch; nothing to merge into.', branch }
    // dirty is checked again now, not taken from the last listing
    const status = await git(run, list.root, ['status', '--porcelain', '--untracked-files=no'])
    if (status === undefined) return { ok: false, message: 'git could not run.', branch }
    if (status.exitCode !== 0) {
      return { ok: false, message: `Could not check for uncommitted changes: ${firstLine(status.stderr) || `git status exited ${status.exitCode}`}`, branch }
    }
    if (status.stdout.trim() !== '') {
      return { ok: false, message: `The main checkout has uncommitted changes. Commit or stash them, then merge ${branch} again.`, branch }
    }
    const merged = await git(run, list.root, ['merge', '--no-ff', '--no-edit', branch], SLOW_MS)
    if (merged === undefined) return { ok: false, message: 'git merge could not run.', branch }
    if (merged.exitCode === 0) {
      return { ok: true, message: `Merged ${branch} into ${list.base}.`, branch }
    }
    const conflicts = await git(run, list.root, ['diff', '--name-only', '--diff-filter=U'])
    const files = (conflicts?.stdout ?? '').split('\n').map(l => l.trim()).filter(l => l !== '')
    const merging = await git(run, list.root, ['rev-parse', '-q', '--verify', 'MERGE_HEAD'])
    if (merging !== undefined && merging.exitCode === 0) await git(run, list.root, ['merge', '--abort'])
    if (files.length > 0) {
      const shown = files.slice(0, 20).join(', ') + (files.length > 20 ? ` and ${files.length - 20} more` : '')
      return { ok: false, message: `Merging ${branch} conflicts in: ${shown}. The merge was aborted; nothing changed.`, branch }
    }
    const why = firstLine(merged.stderr) || firstLine(merged.stdout) || `git merge exited ${merged.exitCode}`
    return { ok: false, message: `Could not merge ${branch}: ${why}`, branch }
  }

  // reject
  if (samePath(wt.path, list.root) || branch === list.base) {
    return { ok: false, message: 'Refusing to remove the main checkout or its branch.', branch }
  }
  const removed = await git(run, list.root, ['worktree', 'remove', '--force', wt.path], SLOW_MS)
  if (removed === undefined || removed.exitCode !== 0) {
    return { ok: false, message: `Could not remove ${wt.path}: ${firstLine(removed?.stderr ?? '') || 'git could not run'}`, branch }
  }
  const deleted = await git(run, list.root, ['branch', '-D', branch])
  if (deleted === undefined || deleted.exitCode !== 0) {
    return { ok: false, message: `Removed ${wt.id}, but could not delete ${branch}: ${firstLine(deleted?.stderr ?? '') || 'git could not run'}`, branch }
  }
  return { ok: true, message: `Rejected ${branch}: removed ${wt.id} and deleted the branch.`, branch }
}
