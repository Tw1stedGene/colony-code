// Pure parts of the v0.2 plugin: diff / porcelain / shortstat parsers, the worktree listing over a
// fake Runner, permission-suggestion summaries and question answers.
import { describe, expect, test } from 'claude-code/testing'
import {
  listWorktrees,
  parseShortstat,
  parseUnifiedDiff,
  parseWorktreePorcelain,
  review,
  unquotePath,
  worktreeId,
} from '../hooks/review'
import type { Runner } from '../hooks/review'
import { questionAnswer, questionContext, sessionLabel, summarizeSuggestions } from '../hooks/protocol'

describe('review parsers', () => {
  test('worktree porcelain: branches, detached, bare, prunable', () => {
    const text = [
      'worktree /r',
      'HEAD aaa',
      'branch refs/heads/main',
      '',
      'worktree /r/.claude/worktrees/agent-1',
      'HEAD bbb',
      'branch refs/heads/claude/agent-1',
      'locked',
      '',
      'worktree /tmp/d',
      'HEAD ccc',
      'detached',
      '',
      'worktree /gone',
      'HEAD ddd',
      'branch refs/heads/old',
      'prunable gitdir file points to non-existent location',
      '',
    ].join('\n')
    const e = parseWorktreePorcelain(text)
    expect(e.map(x => x.path)).toEqual(['/r', '/r/.claude/worktrees/agent-1', '/tmp/d', '/gone'])
    expect(e[1]).toMatchObject({ branch: 'claude/agent-1', locked: true, detached: false })
    expect(e[2]).toMatchObject({ detached: true })
    expect(e[2]?.branch).toBeUndefined()
    expect(e[3]?.prunable).toBe(true)
    expect(worktreeId('/r/.claude/worktrees/agent-1/')).toBe('agent-1')
  })

  test('shortstat in all its forms', () => {
    expect(parseShortstat(' 3 files changed, 10 insertions(+), 2 deletions(-)\n')).toEqual({ files: 3, additions: 10, deletions: 2 })
    expect(parseShortstat(' 1 file changed, 1 insertion(+)\n')).toEqual({ files: 1, additions: 1, deletions: 0 })
    expect(parseShortstat(' 1 file changed, 4 deletions(-)\n')).toEqual({ files: 1, additions: 0, deletions: 4 })
    expect(parseShortstat('')).toEqual({ files: 0, additions: 0, deletions: 0 })
  })

  test('quoted paths', () => {
    expect(unquotePath('"a\\tb.txt"')).toBe('a\tb.txt')
    expect(unquotePath('"caf\\303\\251.md"')).toBe('café.md')
    expect(unquotePath('plain name.txt')).toBe('plain name.txt')
  })

  test('diff: statuses, renames, binary, line numbers, "-- " lines inside hunks', () => {
    const text = [
      'diff --git a/a.txt b/a.txt',
      'index 1..2 100644',
      '--- a/a.txt',
      '+++ b/a.txt',
      '@@ -10,3 +10,3 @@',
      ' keep',
      '--- not a header',
      '+++ not a header either',
      ' tail',
      '\\ No newline at end of file',
      'diff --git a/gone.txt b/gone.txt',
      'deleted file mode 100644',
      '--- a/gone.txt',
      '+++ /dev/null',
      '@@ -1 +0,0 @@',
      '-bye',
      'diff --git a/old name.txt b/new name.txt',
      'similarity index 100%',
      'rename from old name.txt',
      'rename to new name.txt',
      'diff --git a/img.png b/img.png',
      'new file mode 100644',
      'Binary files /dev/null and b/img.png differ',
      '',
    ].join('\n')
    const d = parseUnifiedDiff(text)
    expect(d.truncated).toBeUndefined()
    expect(d.files.map(f => `${f.status}:${f.path}`)).toEqual([
      'modified:a.txt',
      'deleted:gone.txt',
      'renamed:new name.txt',
      'added:img.png',
    ])
    expect(d.files[0]?.hunks[0]?.lines).toEqual([
      { kind: 'ctx', text: 'keep', oldNo: 10, newNo: 10 },
      { kind: 'del', text: '-- not a header', oldNo: 11 },
      { kind: 'add', text: '++ not a header either', newNo: 11 },
      { kind: 'ctx', text: 'tail', oldNo: 12, newNo: 12 },
    ])
    expect(d.files[1]?.hunks[0]?.lines).toEqual([{ kind: 'del', text: 'bye', oldNo: 1 }])
    expect(d.files[2]?.hunks).toEqual([])
  })

  test('diff caps: files and lines', () => {
    const many: string[] = []
    for (let i = 0; i < 5; i++) {
      many.push(`diff --git a/f${i} b/f${i}`, `--- a/f${i}`, `+++ b/f${i}`, '@@ -1,0 +1,3 @@', '+1', '+2', '+3')
    }
    const byFiles = parseUnifiedDiff(many.join('\n'), 2, 100)
    expect(byFiles.files).toHaveLength(2)
    expect(byFiles.truncated).toBe(true)
    const byLines = parseUnifiedDiff(many.join('\n'), 100, 7)
    expect(byLines.truncated).toBe(true)
    expect(byLines.files.flatMap(f => f.hunks.flatMap(h => h.lines))).toHaveLength(7)
    expect(byLines.files).toHaveLength(5) // every file is still named
  })
})

describe('worktree listing over a fake runner', () => {
  test('skips the checkout itself, detached and bare entries; maps agent ids', async () => {
    const calls: string[][] = []
    const run: Runner = async argv => {
      const args = [...argv.slice(1)]
      while (args[0] === '-c') args.splice(0, 2)
      calls.push(args)
      const a = args.join(' ')
      const ok = (stdout: string) => ({ exitCode: 0, stdout, stderr: '' })
      if (a === 'rev-parse --show-toplevel') return ok('/r\n')
      if (a === 'symbolic-ref --quiet --short HEAD') return ok('main\n')
      if (a.startsWith('status')) return ok('')
      if (a === 'worktree list --porcelain') {
        return ok('worktree /r\nbranch refs/heads/main\n\nworktree /w/agent-a3f9c2\nbranch refs/heads/wt\n\nworktree /w/d\ndetached\n\nworktree /bare\nbare\n')
      }
      if (a.startsWith('rev-list')) return ok('4\n')
      if (a.startsWith('diff --shortstat')) return ok(' 2 files changed, 1 insertion(+), 1 deletion(-)\n')
      return { exitCode: 1, stdout: '', stderr: 'no' }
    }
    const list = await listWorktrees(run, '/r/src', ['a3f9c2'])
    expect(list).toEqual({
      root: '/r',
      base: 'main',
      dirty: false,
      worktrees: [{ id: 'agent-a3f9c2', path: '/w/agent-a3f9c2', branch: 'wt', ahead: 4, files: 2, additions: 1, deletions: 1, agentId: 'a3f9c2' }],
    })
    expect(calls.some(c => c[0] === 'push' || c[0] === 'fetch' || c[0] === 'remote')).toBe(false)
  })

  test('worktrees sharing a folder name get unique ids; a failing git status refuses a merge', async () => {
    let statusFails = false
    const run: Runner = async argv => {
      const args = [...argv.slice(1)]
      while (args[0] === '-c') args.splice(0, 2)
      const a = args.join(' ')
      const ok = (stdout: string) => ({ exitCode: 0, stdout, stderr: '' })
      if (a === 'rev-parse --show-toplevel') return ok('/r\n')
      if (a === 'symbolic-ref --quiet --short HEAD') return ok('main\n')
      if (a.startsWith('status')) return statusFails ? { exitCode: 128, stdout: '', stderr: 'fatal: index.lock exists' } : ok('')
      if (a === 'worktree list --porcelain') {
        return ok('worktree /r\nbranch refs/heads/main\n\nworktree /a/fix\nbranch refs/heads/fix-a\n\nworktree /b/fix\nbranch refs/heads/fix-b\n')
      }
      if (a.startsWith('rev-list')) return ok('1\n')
      if (a.startsWith('diff --shortstat')) return ok('')
      if (a.startsWith('merge')) throw new Error('must not merge')
      return { exitCode: 1, stdout: '', stderr: 'no' }
    }
    const list = await listWorktrees(run, '/r')
    expect(list?.worktrees.map(w => `${w.id}:${w.branch}`)).toEqual(['fix:fix-a', 'fix-2:fix-b'])
    statusFails = true
    const merged = await review(run, list!, 'fix-2', 'merge')
    expect(merged.ok).toBe(false)
    expect(merged.message).toBe('Could not check for uncommitted changes: fatal: index.lock exists')
  })

  test('not a repository: undefined', async () => {
    const run: Runner = async () => ({ exitCode: 128, stdout: '', stderr: 'fatal: not a git repository' })
    expect(await listWorktrees(run, '/tmp')).toBeUndefined()
    const broken: Runner = async () => {
      throw new Error('ENOENT')
    }
    expect(await listWorktrees(broken, '/tmp')).toBeUndefined()
  })
})

describe('v0.2 protocol helpers', () => {
  test('permission suggestions read as one human line', () => {
    expect(
      summarizeSuggestions([
        { type: 'addRules', rules: [{ toolName: 'Bash', ruleContent: 'npm test:*' }, { toolName: 'Read' }], behavior: 'allow', destination: 'session' },
        { type: 'addDirectories', directories: ['/work/lib'], destination: 'userSettings' },
        { type: 'setMode', mode: 'acceptEdits', destination: 'session' },
      ]),
    ).toBe(
      'allow Bash(npm test:*), Read for this session only; access to /work/lib for all your projects (user settings); switch to acceptEdits mode for this session only',
    )
    expect(summarizeSuggestions(undefined)).toBe('')
    expect(summarizeSuggestions([{ type: 'weird' }, null])).toBe('')
  })

  test('question answers: multiSelect newlines become commas, free text appended', () => {
    expect(questionAnswer('date-fns', undefined)).toBe('date-fns')
    expect(questionAnswer('List\nCalendar', undefined)).toBe('List, Calendar')
    expect(questionAnswer('', 'something else')).toBe('something else')
    expect(questionAnswer('dayjs', 'but pinned')).toBe('dayjs, but pinned')
    expect(questionAnswer('', '  ')).toBeUndefined()
    expect(questionContext({ question: 'q?', header: 'H', options: [{ label: 'A', description: 'a' }, { label: 'B' }] })).toBe('H\n- A: a\n- B')
  })

  test('session label: repo folder, else the cwd', () => {
    expect(sessionLabel('todo-app', '/x/y')).toBe('todo-app')
    expect(sessionLabel(undefined, '/x/y/')).toBe('y')
  })
})
