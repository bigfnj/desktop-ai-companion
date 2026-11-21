// Test-only entry: patch shared IO in this child, then import the actual fixture with owned argv.
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const [mode, root] = process.argv.slice(2);
const labels = { '--atomic': 'READY_NOT_VISIBLE_UNTIL_CLOSED', '--transient': 'READY_TRANSIENT_EPERM_RECOVERS', '--persistent': 'READY_PERSISTENT_CONSUMER_DIAGNOSTIC' };
const label = labels[mode];
const ready = path.join(root, 'ready.json');
const pending = ready + '.pending';
const fixture = fileURLToPath(new URL('./agentbroker-collaboration-process-fixture.mjs', import.meta.url));
const helper = fileURLToPath(new URL('./agentbroker-fixture-ready.mjs', import.meta.url));
const sha = name => createHash('sha256').update(fs.readFileSync(name)).digest('hex');
console.log('READY_HELPER_SHA256=' + sha(helper));
console.log('READY_FIXTURE_SHA256=' + sha(fixture));
let server;
let finished = false;
let visibleDuringWrite = false;
let writeClosed = false;
let attempts = 0;
let syntheticRefusals = 0;
const write = fs.writeFileSync.bind(fs);
const rename = fs.renameSync.bind(fs);
const create = http.createServer.bind(http);
const deadline = setTimeout(() => finish(label, 1), 5000);
function finish(reason, code) {
  if (finished) return;
  finished = true;
  clearTimeout(deadline);
  fs.writeFileSync = write; fs.renameSync = rename; http.createServer = create;
  if (code === 0) console.log('PASS: ' + label);
  else console.error('FAIL: ' + reason);
  if (server?.listening) server.close(() => process.exit(code));
  else process.exit(code);
}
process.once('uncaughtException', () => finish('READY_UNEXPECTED_EXECUTION', 1));
process.once('unhandledRejection', () => finish('READY_UNEXPECTED_EXECUTION', 1));
function published() {
  setImmediate(() => {
    try {
      const text = fs.readFileSync(ready, 'utf8');
      const state = JSON.parse(text);
      const valid = !visibleDuringWrite && writeClosed && attempts >= 1 && !fs.existsSync(pending) &&
        state.pid === process.pid && state.port === server.address().port && state.port > 0 && state.port !== 7432;
      if (!valid || mode === '--persistent' || (mode === '--transient' && syntheticRefusals !== 2)) return finish(label, 1);
      finish('', 0);
    } catch { finish('READY_UNEXPECTED_EXECUTION', 1); }
  });
}
http.createServer = (...args) => { server = create(...args); return server; };
fs.writeFileSync = (name, bytes, ...options) => {
  if (name !== ready && name !== pending) return write(name, bytes, ...options);
  // Observe publication while the actual file handle is open, then synchronously close before return.
  const handle = fs.openSync(name, 'w');
  try { fs.writeSync(handle, bytes); visibleDuringWrite ||= fs.existsSync(ready); }
  finally { fs.closeSync(handle); writeClosed = true; }
  if (name === ready) published(); // Direct-write bypass must reach the atomic assertion too.
};
fs.renameSync = (from, to) => {
  if (from !== pending || to !== ready) return rename(from, to);
  attempts++;
  if (mode === '--persistent' || (mode === '--transient' && syntheticRefusals < 2)) {
    syntheticRefusals++;
    throw Object.assign(new Error('synthetic'), { code: 'EPERM' });
  }
  rename(from, to); // Use real IO and the helper's real timers; genuine transient faults may retry too.
  published();
};
if (!label) finish('READY_UNEXPECTED_EXECUTION', 1);
process.argv = [process.execPath, fixture, ready, path.join(root, 'requests.jsonl'), 'success-empty'];
try { await import(new URL('./agentbroker-collaboration-process-fixture.mjs', import.meta.url)); }
catch { finish('READY_UNEXPECTED_EXECUTION', 1); }
