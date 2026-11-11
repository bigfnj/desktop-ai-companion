import http from 'node:http';
import fs from 'node:fs';
import { publishFixtureReady } from './agentbroker-fixture-ready.mjs';

const [readyPath, requestsPath, scenario] = process.argv.slice(2);
const allowed = new Set(['success-empty', 'success-single', 'success-rich', 'success-string', 'tool-error',
  'rpc-error', 'http-error', 'transport-truncated', 'preflight-json', 'preflight-port', 'preflight-native', 'lookup-wait']);
if (!readyPath || !requestsPath || !allowed.has(scenario)) throw new Error('Invalid owned fixture arguments.');
const lifetime = setTimeout(() => process.exit(2), 45_000);
const server = http.createServer((request, response) => {
  const chunks = [];
  request.on('data', chunk => chunks.push(chunk));
  request.on('end', () => {
    const raw = Buffer.concat(chunks).toString('utf8');
    const message = JSON.parse(raw);
    fs.appendFileSync(requestsPath, JSON.stringify({ method: request.method, url: request.url, raw, tool: message.params?.name }) + '\n');
    if (request.method !== 'POST' || request.url !== '/mcp') throw new Error('Unexpected fixture request.');
    if (scenario === 'http-error') { response.writeHead(503, { 'Content-Type': 'text/plain' }); response.end('DO_NOT_EXPORT_HTTP_BODY'); return; }
    if (scenario === 'transport-truncated') {
      response.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': 1024 });
      response.write('{', () => response.socket?.destroy());
      return;
    }
    if (scenario === 'rpc-error') {
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({ jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'DO_NOT_EXPORT_RPC_BODY' } })); return;
    }
    let value = [];
    if (scenario === 'success-single') value = ['one'];
    if (scenario === 'success-string') value = '';
    if (scenario === 'success-rich') value = { text: 'line one\nD:\\synthetic\\path\n猫 🐑', empty: '', items: [] };
    if (scenario === 'lookup-wait' && message.params?.name === 'get_message_receipt')
      value = { message: { id: message.params.arguments.messageId, sender: {
        brokerId: '11111111-1111-4111-8111-111111111111', sessionId: 'mcp:22222222-2222-4222-8222-222222222222',
        incarnation: '33333333-3333-4333-8333-333333333333' } } };
    const result = scenario === 'tool-error'
      ? { isError: true, content: [{ type: 'text', text: 'session_unavailable' }] }
      : { content: [{ type: 'text', text: JSON.stringify(value) }] };
    const body = JSON.stringify({ jsonrpc: '2.0', id: message.id, result });
    if (scenario === 'success-rich') {
      response.writeHead(200, { 'Content-Type': 'text/event-stream' }); response.end('event: message\ndata: ' + body + '\n\n');
    } else { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(body); }
  });
});
server.listen(0, '127.0.0.1', () => {
  publishFixtureReady(readyPath, { pid: process.pid, port: server.address().port });
});
server.on('close', () => clearTimeout(lifetime));
