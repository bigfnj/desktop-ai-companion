import path from 'node:path';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { publishFixtureReady } from './agentbroker-fixture-ready.mjs';

const [mode, root] = process.argv.slice(2);
const ready = path.join(root, 'ready.json');
const value = { pid: process.pid, port: 12345 };
const helper = fileURLToPath(new URL('./agentbroker-fixture-ready.mjs', import.meta.url));
console.log('READY_HELPER_SHA256=' + createHash('sha256').update(fs.readFileSync(helper)).digest('hex'));
const names = new Set(['READY_NOT_VISIBLE_UNTIL_CLOSED', 'READY_TRANSIENT_EPERM_RECOVERS']);
function need(condition, label) { if (!condition) throw new Error('READINESS_ASSERT:' + label); }
try {
  if (mode !== '--baseline') throw new Error('unexpected mode');
  // Unit IO is entirely in memory. The real fixture check owns actual close/rename behavior.
  const files = new Map();
  let visibleDuringWrite = false;
  let failed = false;
  publishFixtureReady(ready, value, {
    io: {
      writeFileSync: (name, bytes) => { files.set(name, bytes); visibleDuringWrite = files.has(ready); },
      renameSync: (from, to) => { need(files.has(from), 'READY_NOT_VISIBLE_UNTIL_CLOSED'); files.set(to, files.get(from)); files.delete(from); },
    },
    fail: () => { failed = true; },
  });
  need(!visibleDuringWrite && !failed && files.get(ready) === JSON.stringify(value) && !files.has(ready + '.pending'), 'READY_NOT_VISIBLE_UNTIL_CLOSED');
  console.log('PASS: READY_NOT_VISIBLE_UNTIL_CLOSED');
  files.clear();
  let attempts = 0;
  let elapsed = 0;
  failed = false;
  publishFixtureReady(ready, value, {
    io: {
      writeFileSync: (name, bytes) => { files.set(name, bytes); },
      renameSync: (from, to) => {
        if (++attempts < 3) throw Object.assign(new Error('synthetic'), { code: 'EPERM' });
        need(files.has(from), 'READY_TRANSIENT_EPERM_RECOVERS'); files.set(to, files.get(from)); files.delete(from);
      },
    },
    now: () => elapsed,
    later: (callback, delay) => { elapsed += delay; callback(); },
    fail: () => { failed = true; },
  });
  need(!failed && attempts === 3 && elapsed === 50 && files.get(ready) === JSON.stringify(value) && !files.has(ready + '.pending'), 'READY_TRANSIENT_EPERM_RECOVERS');
  console.log('PASS: READY_TRANSIENT_EPERM_RECOVERS');
  console.log('READY_NODE_SELFTEST=PASS checks=2');
} catch (error) {
  const label = error?.message?.startsWith('READINESS_ASSERT:') ? error.message.slice('READINESS_ASSERT:'.length) : '';
  console.error('FAIL: ' + (names.has(label) ? label : 'READY_UNEXPECTED_EXECUTION'));
  process.exitCode = 1;
}
