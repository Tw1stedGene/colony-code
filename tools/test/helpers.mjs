import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { FakeBridge } from '../fake-bridge.mjs';

export const TOOLS = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export function tempHome() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'colony-code-test-'));
}

export async function startBridge(opts = {}) {
  const home = opts.home || tempHome();
  const bridge = new FakeBridge({ port: 0, home, ...opts });
  await bridge.start();
  return { bridge, home, base: `http://127.0.0.1:${bridge.port}`, token: bridge.token };
}

export async function req(base, token, method, p, { body, headers = {}, rawBody } = {}) {
  const h = { ...headers };
  if (token) h['X-Colony-Token'] = token;
  const res = await fetch(base + p, { method, headers: h, body: rawBody !== undefined ? rawBody : body !== undefined ? JSON.stringify(body) : undefined });
  const text = await res.text();
  let json;
  try { json = JSON.parse(text); } catch { json = undefined; }
  return { status: res.status, json, headers: res.headers };
}

/** Run a node script, resolve with { code, stdout, stderr }. */
export function runNode(args, { env = {}, timeoutMs = 30000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, args, { env: { ...process.env, NO_COLOR: '1', ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (d) => { stdout += d; });
    child.stderr.on('data', (d) => { stderr += d; });
    const t = setTimeout(() => { child.kill('SIGKILL'); reject(new Error(`timeout running ${args.join(' ')}\n${stdout}\n${stderr}`)); }, timeoutMs);
    child.on('exit', (code) => { clearTimeout(t); resolve({ code, stdout, stderr }); });
  });
}

export function waitFor(pred, { timeoutMs = 5000, intervalMs = 20 } = {}) {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const tick = () => {
      let v;
      try { v = pred(); } catch { v = false; }
      if (v) return resolve(v);
      if (Date.now() - start > timeoutMs) return reject(new Error('waitFor timed out'));
      setTimeout(tick, intervalMs);
    };
    tick();
  });
}
