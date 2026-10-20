import { describe, expect, test } from 'claude-code/testing'

import {
  activityFor,
  branchFromHead,
  describePermission,
  diffFor,
  hmacSha256Hex,
  leadIdFor,
  parseBridge,
  parseDecision,
  parseInbox,
  parseProof,
  randomHex,
  stateForTool,
} from '../hooks/protocol'

describe('protocol helpers', () => {
  test('state follows the protocol mapping', () => {
    expect(stateForTool('Read', {})).toBe('reading')
    expect(stateForTool('Grep', {})).toBe('reading')
    expect(stateForTool('WebSearch', {})).toBe('reading')
    expect(stateForTool('MultiEdit', {})).toBe('editing')
    expect(stateForTool('NotebookEdit', {})).toBe('editing')
    expect(stateForTool('Bash', { command: 'cargo test --all' })).toBe('testing')
    expect(stateForTool('Bash', { command: 'npm run check' })).toBe('testing')
    expect(stateForTool('Bash', { command: 'ls -la' })).toBe('running')
    expect(stateForTool('TodoWrite', {})).toBe('thinking')
  })

  test('activity is one line of at most 48 chars', () => {
    const long = activityFor('Bash', { command: `echo ${'x'.repeat(100)}\nsecond line` }, '/w')
    expect(long.length).toBe(48)
    expect(long.includes('\n')).toBe(false)
    expect(activityFor('Read', { file_path: '/w/src/a.ts' }, '/w')).toBe('reading src/a.ts')
  })

  test('lead id and bridge file', () => {
    expect(leadIdFor('0123456789')).toBe('lead-01234567')
    expect(parseBridge('{"port":7879,"token":"ab","protocol":0}')).toEqual({ port: 7879, token: 'ab' })
    expect(parseBridge('{"port":7879,"token":"ab","protocol":1}')).toBeUndefined()
    expect(parseBridge('not json')).toBeUndefined()
  })

  test('inbox and decision answers', () => {
    expect(parseInbox('[{"id":"a","type":"goal.submit","text":"x"},{"type":"bad"}]')).toEqual([
      { id: 'a', type: 'goal.submit', text: 'x' },
    ])
    expect(parseInbox('{}')).toEqual([])
    expect(parseDecision('{"status":"answered","option":"Deny","text":"no"}')).toEqual({
      status: 'answered',
      option: 'Deny',
      text: 'no',
    })
    expect(parseDecision('{"status":"open"}')).toEqual({ status: 'open' })
    expect(parseDecision('{"status":"weird"}')).toBeUndefined()
  })

  test('diffs, questions and branches', () => {
    expect(diffFor('Write', { file_path: '/w/n.txt', content: 'a\nb' }, '/w')).toBe('--- a/n.txt\n+++ b/n.txt\n@@\n+a\n+b')
    expect(diffFor('Read', { file_path: '/w/n.txt' }, '/w')).toBeUndefined()
    expect(describePermission('Bash', { command: 'npm install left-pad' }, '/w')).toBe('Run: npm install left-pad')
    expect(branchFromHead('ref: refs/heads/feature/x\n')).toBe('feature/x')
    expect(branchFromHead('3f2a9c0')).toBeUndefined()
  })

  test('server proof is RFC 4231 HMAC-SHA256, matching the mod', async () => {
    // RFC 4231 test case 2; tests/bridge checks the C# side against the same vector
    expect(await hmacSha256Hex('Jefe', 'what do ya want for nothing?')).toBe(
      '5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843',
    )
    // a key longer than the 64-byte block is hashed first (value from Node's crypto.createHmac)
    expect(await hmacSha256Hex('k'.repeat(100), 'x')).toBe('8c1858bff6cf9cb73708b4b54f43bca6e1b8b4c3380760dc009001b265b64c6f')
    expect(randomHex(16)).toMatch(/^[0-9a-f]{32}$/)
    expect(randomHex(16)).not.toBe(randomHex(16))
    expect(parseProof('{"ok":true,"proof":"ab"}')).toBe('ab')
    expect(parseProof('{"ok":true}')).toBeUndefined()
    expect(parseProof('nope')).toBeUndefined()
  })
})
