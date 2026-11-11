import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { publishFixtureReady } from './agentbroker-fixture-ready.mjs';

const [mode, root] = process.argv.slice(2);
const value = { pid: process.pid, port: 12345 };
const ready = path.join(root, 'ready.json');
const helper = fileURLToPath(new URL('./agentbroker-fixture-ready.mjs', import.meta.url));
console.log('READY_HELPER_SHA256=' + createHash('sha256').update(fs.readFileSync(helper)).digest('hex'));
const refused = () => Object.assign(new Error('SYNTHETIC_DO_NOT_EXPORT'), { code: 'EPERM' });

if (mode === '--persistent') {
  let elapsed = 0;
  publishFixtureReady(ready, value, {
    io: { writeFileSync: fs.writeFileSync, renameSync: () => { throw refused(); } },
    now: () => elapsed,
    later: (callback, delay) => { elapsed += delay; callback(); },
  });
  console.error('FAIL: READY_PERSISTENT_CONSUMER_DIAGNOSTIC');
  process.exit(1);
}

let current = 'READY_NOT_VISIBLE_UNTIL_CLOSED';
try {
  if (mode !== '--baseline') throw new Error(current);
  let visibleWhileOpen = false;
  let failed = false;
  publishFixtureReady(ready, value, {
    io: {
      writeFileSync: (name, bytes) => {
        const handle = fs.openSync(name, 'w');
        try {
          fs.writeSync(handle, bytes);
          visibleWhileOpen = fs.existsSync(ready);
        } finally { fs.closeSync(handle); }
      },
      renameSync: fs.renameSync,
    },
    fail: () => { failed = true; },
  });
  if (visibleWhileOpen || failed || !fs.existsSync(ready) ||
      fs.readFileSync(ready, 'utf8') !== JSON.stringify(value) || fs.existsSync(ready + '.pending'))
    throw new Error(current);
  console.log('PASS: ' + current);
  fs.unlinkSync(ready);

  current = 'READY_TRANSIENT_EPERM_RECOVERS';
  let attempts = 0;
  let elapsed = 0;
  failed = false;
  publishFixtureReady(ready, value, {
    io: {
      writeFileSync: fs.writeFileSync,
      renameSync: (from, to) => { if (++attempts < 3) throw refused(); fs.renameSync(from, to); },
    },
    now: () => elapsed,
    later: (callback, delay) => { elapsed += delay; callback(); },
    fail: () => { failed = true; },
  });
  if (failed || attempts !== 3 || elapsed !== 50 || !fs.existsSync(ready) ||
      fs.readFileSync(ready, 'utf8') !== JSON.stringify(value) || fs.existsSync(ready + '.pending'))
    throw new Error(current);
  console.log('PASS: ' + current);
  console.log('READY_NODE_SELFTEST=PASS checks=2');
} catch {
  // Only a fixed assertion label crosses this child boundary, never an OS path or error stack.
  console.error('FAIL: ' + current);
  process.exitCode = 1;
}
