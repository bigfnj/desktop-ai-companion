// Focused read-only inspection of the public openai/codex source. No local profiles are read.
// node tools/agentbroker/inspect-codex-source.mjs --directory codex-rs/app-server/src --pattern processor
// node tools/agentbroker/inspect-codex-source.mjs --file codex-rs/core/src/codex.rs --pattern inject_items
const args = process.argv.slice(2);
const value = (key, fallback = null) => args.includes(key) ? args[args.indexOf(key) + 1] : fallback;
if (args.includes('--help')) {
  console.log('Read focused public openai/codex source: --ref rust-v0.161.0 --directory <path> OR --file <path> --pattern <regex> [--context 600 --limit 12]');
  process.exit(0);
}
const ref = value('--ref', 'rust-v0.161.0');
const directory = value('--directory');
const file = value('--file');
const pattern = value('--pattern');
const path = directory ?? file;
if (!/^rust-v\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$/.test(ref) || !path || !pattern || Boolean(directory) === Boolean(file)
  || !/^codex-rs\/[A-Za-z0-9_./-]+$/.test(path) || path.split('/').some(x => x === '..' || x === '.')
  || file && !/\.(?:rs|ts|json|md)$/.test(file)) throw new Error('Explicit public source path/ref and a focused pattern are required.');
const regex = new RegExp(pattern, 'g');
const context = Math.min(2000, Math.max(0, Number(value('--context', '600'))));
const limit = Math.min(40, Math.max(1, Number(value('--limit', '12'))));
if (!Number.isSafeInteger(context) || !Number.isSafeInteger(limit)) throw new Error('Invalid output bounds.');
const url = directory
  ? `https://api.github.com/repos/openai/codex/contents/${path}?ref=${ref}`
  : `https://raw.githubusercontent.com/openai/codex/${ref}/${path}`;
const response = await fetch(url, { signal: AbortSignal.timeout(20000), redirect: 'error', headers: { 'User-Agent': 'AgentBroker-public-source-inspector' } });
if (!response.ok) throw new Error(`Public source returned HTTP ${response.status}`);
const raw = await response.text();
if (Buffer.byteLength(raw) > 8 * 1024 * 1024) throw new Error('Public source size limit exceeded.');
if (directory) {
  const entries = JSON.parse(raw);
  if (!Array.isArray(entries)) throw new Error('Unexpected directory response.');
  for (const entry of entries.filter(x => typeof x.name === 'string' && new RegExp(pattern).test(x.name)).slice(0, limit))
    console.log(JSON.stringify({ name: entry.name, path: entry.path, type: entry.type, size: entry.size }));
} else {
  let emitted = 0;
  for (const match of raw.matchAll(regex)) {
    if (++emitted > limit) break;
    console.log(JSON.stringify({ path, ref, offset: match.index,
      excerpt: raw.slice(Math.max(0, match.index - context), match.index + match[0].length + context) }));
  }
}
