// Read-only inspection of explicitly selected public application bundle members.
// Usage: node tools/agentbroker/inspect-public-asar.mjs <app.asar> --list-prefix .vite/build/
// Usage: node tools/agentbroker/inspect-public-asar.mjs <app.asar> --member .vite/build/main.js --pattern app-server
import fs from 'node:fs';
import path from 'node:path';

const [archive, ...args] = process.argv.slice(2);
function option(name, fallback) {
  const at = args.indexOf(name);
  return at < 0 ? fallback : args[at + 1];
}
function fail(message) {
  throw new Error(message);
}
function safeMember(value) {
  const normalized = String(value ?? '').replaceAll('\\', '/');
  if (!normalized || normalized.startsWith('/') || normalized.includes(':')
      || normalized.split('/').some(part => part === '..' || part === '.')) {
    fail('An exact relative archive member is required.');
  }
  const forbidden = /^(?:.*\.env|\.env(?:\..*)?|\.npmrc|.*\.(?:pem|key|p12|pfx|keystore)|privatekey.*|credentials(?:\.toml)?|auth\.json)$/i;
  if (normalized.split('/').some(part => forbidden.test(part))) {
    fail('Protected member paths are not supported.');
  }
  return normalized;
}
if (!archive || !path.isAbsolute(archive) || !archive.endsWith('.asar')) {
  fail('Pass an absolute path to a public application .asar archive.');
}
const fd = fs.openSync(archive, 'r');
try {
  const prefix = Buffer.alloc(16);
  if (fs.readSync(fd, prefix, 0, 16, 0) !== 16) fail('Truncated archive prefix.');
  const headerSize = prefix.readUInt32LE(4);
  const jsonSize = prefix.readUInt32LE(12);
  if (jsonSize < 2 || jsonSize > 64 * 1024 * 1024 || headerSize < jsonSize + 8) {
    fail('Invalid or oversized ASAR header.');
  }
  const raw = Buffer.alloc(jsonSize);
  if (fs.readSync(fd, raw, 0, jsonSize, 16) !== jsonSize) fail('Truncated ASAR header.');
  const header = JSON.parse(raw.toString('utf8'));
  const listPrefix = option('--list-prefix', null);
  if (listPrefix !== null) {
    const requested = listPrefix.replaceAll('\\', '/');
    if (requested && requested !== '/') safeMember(requested.replace(/\/$/, ''));
    function list(files, base = '') {
      for (const [name, entry] of Object.entries(files ?? {})) {
        const member = `${base}${name}`;
        if (entry.files) {
          if (member.startsWith(requested) || requested.startsWith(`${member}/`)) {
            list(entry.files, `${member}/`);
          }
        } else if (member.startsWith(requested)) {
          // Names, sizes, and link/unpacked flags only; no member bytes are read.
          try { safeMember(member); } catch { continue; }
          console.log(JSON.stringify({ member, size: entry.size, link: Boolean(entry.link), unpacked: Boolean(entry.unpacked) }));
        }
      }
    }
    list(header.files);
  } else {
    const member = safeMember(option('--member', ''));
    if (!/\.(?:[cm]?js|json)$/i.test(member)) fail('Only explicit public JS/JSON members are supported.');
    let entry = { files: header.files };
    for (const segment of member.split('/')) {
      if (entry.link || entry.unpacked) fail('Linked or unpacked archive members are not supported.');
      entry = entry.files?.[segment];
      if (!entry) fail('Archive member does not exist.');
    }
    if (entry.files || entry.link || entry.unpacked) fail('Only packed regular files are supported.');
    const size = Number(entry.size);
    const offset = Number(entry.offset);
    if (!Number.isSafeInteger(size) || size < 0 || size > 32 * 1024 * 1024
        || !Number.isSafeInteger(offset) || offset < 0) fail('Invalid or oversized member.');
    const bytes = Buffer.alloc(size);
    if (fs.readSync(fd, bytes, 0, size, 8 + headerSize + offset) !== size) fail('Truncated member.');
    const source = bytes.toString('utf8');
    const at = option('--offset', null);
    if (at !== null) {
      const offset = Number(at);
      const length = Math.min(6000, Math.max(1, Number(option('--length', '2000'))));
      if (!Number.isSafeInteger(offset) || offset < 0 || offset >= source.length
          || !Number.isSafeInteger(length)) fail('Invalid focused source range.');
      console.log(JSON.stringify({ member, offset, excerpt: source.slice(offset, offset + length) }));
      process.exitCode = 0;
    } else {
    const pattern = option('--pattern', null);
    if (pattern === null) fail('Provide a focused --pattern; whole-member output is not supported.');
    const expression = new RegExp(pattern, 'g');
    const context = Math.min(2000, Math.max(0, Number(option('--context', '180'))));
    const limit = Math.min(100, Math.max(1, Number(option('--limit', '20'))));
    let count = 0;
    for (const match of source.matchAll(expression)) {
      if (++count > limit) break;
      const start = Math.max(0, match.index - context);
      const end = Math.min(source.length, match.index + match[0].length + context);
      console.log(JSON.stringify({ member, offset: match.index, excerpt: source.slice(start, end) }));
    }
    }
  }
} finally {
  fs.closeSync(fd);
}
