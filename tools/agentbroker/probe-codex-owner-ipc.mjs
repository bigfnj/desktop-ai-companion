// Connection-only probe of the installed Codex desktop's owner-routing IPC.
// It does not start a router or agent and cannot send turns, queue items, or resumes.
// Usage: node tools/agentbroker/probe-codex-owner-ipc.mjs [--thread <exact-known-UUID>]
// Usage: node tools/agentbroker/probe-codex-owner-ipc.mjs --thread-env CODEX_THREAD_ID
import net from 'node:net';
import { randomUUID } from 'node:crypto';

const endpoint = String.raw`\\.\pipe\codex-ipc`;
const args = process.argv.slice(2);
const fromArgument = args[0] === '--thread';
const fromEnvironment = args[0] === '--thread-env' && args[1] === 'CODEX_THREAD_ID';
if (args.length && (args.length !== 2 || (!fromArgument && !fromEnvironment))) {
  throw new Error('Unsupported arguments.');
}
// Read this one explicitly allowed non-secret value only, never enumerate the environment.
const threadId = fromEnvironment ? process.env.CODEX_THREAD_ID
  : fromArgument ? args[1] : '00000000-0000-4000-8000-000000000000';
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
if (!uuid.test(threadId ?? '')) throw new Error('No valid exact thread UUID was supplied.');

const summary = {
  probe: 'codex-owner-ipc',
  connected: false,
  initialized: false,
  syntheticTarget: !fromArgument && !fromEnvironment,
  discoveryAttempted: false,
  ownerFound: false,
  supportsUntrustedAppInput: false,
  ignoredBroadcasts: 0,
  rejectedIncomingRequests: 0,
  errorCode: null,
};
const maxFrameBytes = 64 * 1024;
let socket;
let buffer = Buffer.alloc(0);
let clientId = 'initializing-client';
let finished = false;
const initializeId = randomUUID();
const discoveryId = randomUUID();
let timer;

function finish(errorCode = null) {
  if (finished) return;
  finished = true;
  clearTimeout(timer);
  summary.errorCode = errorCode;
  socket?.destroy();
  // No native IDs, request params, conversation metadata, or message bodies are emitted.
  console.log(JSON.stringify(summary));
  process.exitCode = errorCode ? 1 : 0;
}
function write(message) {
  const body = Buffer.from(JSON.stringify(message), 'utf8');
  const frame = Buffer.alloc(4 + body.length);
  frame.writeUInt32LE(body.length, 0);
  body.copy(frame, 4);
  socket.write(frame);
}
function request(method, params, requestId, version) {
  if (method !== 'initialize' && method !== 'thread-owner-discovery') {
    throw new Error('Mutating and unknown methods are prohibited by this probe.');
  }
  write({ type: 'request', requestId, sourceClientId: clientId, version, method, params, timeoutMs: 2000 });
}
function handle(message) {
  if (message?.type === 'broadcast') {
    summary.ignoredBroadcasts++;
    return;
  }
  if (message?.type === 'client-discovery-request') {
    summary.rejectedIncomingRequests++;
    write({ type: 'client-discovery-response', requestId: message.requestId, response: { canHandle: false } });
    return;
  }
  if (message?.type !== 'response') return;
  if (message.requestId === initializeId) {
    if (message.resultType !== 'success' || message.method !== 'initialize'
        || !uuid.test(message.result?.clientId ?? '')) return finish('initialization-rejected');
    clientId = message.result.clientId;
    summary.initialized = true;
    summary.discoveryAttempted = true;
    request('thread-owner-discovery', { hostId: 'local', conversationId: threadId }, discoveryId, 1);
  } else if (message.requestId === discoveryId) {
    summary.ownerFound = message.resultType === 'success'
      && uuid.test(message.handledByClientId ?? '');
    summary.supportsUntrustedAppInput = summary.ownerFound
      && message.result?.supportsUntrustedAppInput === true;
    if (message.resultType === 'error' && message.error !== 'no-client-found') {
      return finish('owner-discovery-rejected');
    }
    finish();
  }
}
if (process.platform !== 'win32') {
  finish('native-windows-required');
} else {
  timer = setTimeout(() => finish('probe-timeout'), 5000);
  socket = net.createConnection(endpoint);
  socket.once('connect', () => {
    summary.connected = true;
    request('initialize', { clientType: 'agentbroker-probe' }, initializeId, 0);
  });
  socket.on('data', chunk => {
    if (finished) return;
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const size = buffer.readUInt32LE(0);
      if (!size || size > maxFrameBytes) return finish('unexpected-frame-size');
      if (buffer.length < 4 + size) return;
      const body = buffer.subarray(4, 4 + size);
      buffer = buffer.subarray(4 + size);
      try { handle(JSON.parse(body.toString('utf8'))); }
      catch { return finish('invalid-protocol-frame'); }
      if (finished) return;
    }
    if (buffer.length > maxFrameBytes + 4) finish('unexpected-frame-size');
  });
  socket.once('error', error => {
    const known = new Set(['EACCES', 'ENOENT', 'EPIPE', 'ECONNREFUSED', 'ECONNRESET', 'EBUSY']);
    finish(known.has(error.code) ? error.code : 'connection-error');
  });
  socket.once('close', () => { if (!finished) finish('connection-closed'); });
}
