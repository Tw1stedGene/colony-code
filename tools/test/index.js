// Entry point so `node --test tools/test` works: Node resolves the folder through package.json "main".
// `node --test tools/test/*.test.mjs` runs the same files, each in its own process.
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const dir = fileURLToPath(new URL('.', import.meta.url));
for (const f of fs.readdirSync(dir).filter((n) => n.endsWith('.test.mjs')).sort()) {
  await import(new URL(f, import.meta.url));
}
