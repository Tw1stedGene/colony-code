import { mock } from 'claude-code/testing'
import type { On } from 'claude-code'
import type { MockClock } from 'claude-code/testing'
import { hmacSha256Hex } from '../hooks/protocol'

export const HOME = '/home/tester'
export const BRIDGE_FILE = `${HOME}/.colony-code/bridge.json`
export const SID = 'abcdef1234567890'
export const LEAD = 'lead-abcdef12'
export const TOKEN = '0123456789abcdef0123456789abcdef'

export type Sent = { type: string; [k: string]: unknown }
export type Inbox = { id: string; type: string; [k: string]: unknown }

export type World = {
  isRunning: boolean
  /** The key this server proves with: TOKEN for the game, anything else for an impostor. */
  proofKey: string
  challenges: number
  inboxPaths: string[]
  events: Sent[]
  requests: { method: string; path: string; token: string | undefined }[]
  inbox: Inbox[]
  decisions: Map<string, { status: string; option?: string; text?: string }>
  status: (string | undefined)[]
  prompts: string[]
  aborted: string[]
  /** Fake git beneath $.process.run: answers each argv (argv[0] 'git'); unset, process.run rejects. */
  git?: (args: string[], cwd: string) => { exitCode: number; stdout?: string; stderr?: string }
  gitCalls: { args: string[]; cwd: string }[]
  /** Answers a decision the game was never told about, from its decision.open (e.g. a question). */
  autoAnswer?: (decision: { id: string; kind?: string; options?: string[]; question?: string }) =>
    | { option: string; text?: string }
    | undefined
}

/** The engine beneath the plugin, with a fake game behind $.http.fetch. */
export function world(on: On, clock: MockClock, isRunning: boolean): World {
  const w: World = {
    isRunning,
    proofKey: TOKEN,
    challenges: 0,
    inboxPaths: [],
    events: [],
    requests: [],
    inbox: [],
    decisions: new Map(),
    status: [],
    prompts: [],
    aborted: [],
    gitCalls: [],
  }
  mock.env(on, { HOME })
  on('session.start', ($, e) => ({ cwd: e.cwd }))
  on('session.end', ($, e) => ({ sessionId: e.sessionId }))
  on('session.id', () => ({ value: SID }))
  on('session.model', () => ({ value: 'claude-test' }))
  on('session.repo', () => ({ value: { root: '/work/todo-app', remote: null, internal: false, name: null } }))
  on('turn.start', ($, e) => ({ turnId: e.turnId }))
  on('turn.complete', ($, e) => ({ text: e.answer }))
  on('turn.abort', ($, e) => {
    w.aborted.push(e.turnId)
    return { value: undefined }
  })
  on('ui.status', ($, e) => {
    w.status.push(e.text)
    return { value: undefined }
  })
  on('prompt.submit', ($, e) => {
    w.prompts.push(e.text)
    return { text: e.text }
  })
  on('process.run', ($, e) => {
    if (w.git === undefined || e.argv[0] !== 'git') throw new Error('ENOENT: no such program')
    // drop the `-c key=value` pairs the plugin puts in front
    const args = [...e.argv.slice(1)]
    while (args[0] === '-c') args.splice(0, 2)
    const cwd = e.init?.cwd ?? ''
    w.gitCalls.push({ args, cwd })
    const r = w.git(args, cwd)
    return {
      value: { exitCode: r.exitCode, stdout: r.stdout ?? '', stderr: r.stderr ?? '', isStdoutTruncated: false, isStderrTruncated: false },
    }
  })
  on('fs.read', ($, e) => {
    if (e.path === BRIDGE_FILE && w.isRunning) {
      return { value: JSON.stringify({ port: 7878, token: TOKEN, pid: 1, protocol: 0, game: '1.6', mod: '0.1.0' }) }
    }
    if (e.path === '/work/todo-app/.git/HEAD') return { value: 'ref: refs/heads/main\n' }
    throw new Error(`ENOENT: ${e.path}`)
  })
  on('http.fetch', async ($, e) => {
    if (!w.isRunning) throw new Error('ECONNREFUSED')
    const url = new URL(e.url)
    const method = e.init?.method ?? 'GET'
    const token = e.init?.headers?.['X-Colony-Token']
    const challenge = e.init?.headers?.['X-Colony-Challenge']
    const reply = (body: unknown, status = 200) => ({
      value: {
        status,
        ok: status >= 200 && status < 300,
        headers: { 'content-type': 'application/json' },
        text: JSON.stringify(body),
      },
    })
    if (url.pathname === '/health' && token === undefined && challenge !== undefined) {
      // the game proves it holds the token without the plugin sending it
      w.challenges += 1
      return reply({ ok: true, protocol: 0, proof: await hmacSha256Hex(w.proofKey, challenge) })
    }
    w.requests.push({ method, path: url.pathname, token })
    if (token !== TOKEN) return reply({ ok: false }, 401)
    if (url.pathname === '/events' && method === 'POST') {
      const batch = JSON.parse(e.init?.body ?? '[]') as Sent[]
      w.events.push(...batch)
      return reply({ ok: true, accepted: batch.length })
    }
    if (url.pathname === '/inbox') {
      w.inboxPaths.push(url.pathname + url.search)
      const session = url.searchParams.get('session')
      const mine = w.inbox.filter(i => session === null || i.sessionId === undefined || i.sessionId === session)
      w.inbox = w.inbox.filter(i => !mine.includes(i))
      return reply(mine)
    }
    const m = /^\/decision\/(.+)$/.exec(url.pathname)
    if (m?.[1] !== undefined) {
      // a long poll, as the game holds it: answered at once, or after `wait`
      const id = decodeURIComponent(m[1])
      if (!w.decisions.has(id) && w.autoAnswer !== undefined) {
        const opened = w.events
          .filter(ev => ev.type === 'decision.open')
          .map(ev => ev.decision as { id: string; kind?: string; options?: string[]; question?: string })
          .find(d => d.id === id)
        const got = opened === undefined ? undefined : w.autoAnswer(opened)
        if (got !== undefined) w.decisions.set(id, { status: 'answered', ...got })
      }
      if (!w.decisions.has(id)) await clock.sleep(Number(url.searchParams.get('wait') ?? '25') * 1000)
      if (!w.isRunning) throw new Error('ECONNREFUSED')
      return reply(w.decisions.get(id) ?? { status: 'open' })
    }
    return reply({ ok: false }, 404)
  })
  return w
}

export const ofType = (w: World, type: string) => w.events.filter(ev => ev.type === type)
export const upserts = (w: World, id: string) =>
  ofType(w, 'agent.upsert')
    .map(ev => ev.agent as { id: string; role: string; state: string; station: string; activity: string; label?: string })
    .filter(a => a.id === id)

