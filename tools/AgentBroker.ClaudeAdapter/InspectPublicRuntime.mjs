// Static public executable inspection only. Never opens config, session, or credential files.
import { readFileSync, realpathSync, statSync } from 'node:fs';
import { extname, resolve } from 'node:path';

const args = process.argv.slice(2);
if (args.length === 0 || args[0] === '--help') {
  console.log('node tools/AgentBroker.ClaudeAdapter/InspectPublicRuntime.mjs <public-claude.exe> [--after-byte <offset>] <literal-needle> [literal-needle ...]');
  console.log('Reads only the explicit public executable. Prints at most 3 bounded contexts per needle, at most 8 needles. Use @offset:length for an exact window (length <=12000).');
  process.exit(0);
}
const [input, ...searchArgs] = args;
let after = 0;
if (searchArgs[0] === '--after-byte') {
  if (!/^(0|[1-9][0-9]*)$/.test(searchArgs[1] ?? '')) throw new Error('Invalid start offset.');
  after = Number(searchArgs[1]);
  searchArgs.splice(0, 2);
}
const needles = searchArgs;
if (needles.length < 1 || needles.length > 8 || needles.some(x => x.length < 3 || x.length > 160))
  throw new Error('Supply 1-8 literal needles, each 3-160 characters.');
const path = realpathSync(resolve(input));
if (extname(path).toLowerCase() !== '.exe' || !/(?:^|[\\/])claude\.exe$/i.test(path))
  throw new Error('Only an explicitly named public claude.exe may be inspected.');
const size = statSync(path).size;
if (size > 512 * 1024 * 1024) throw new Error('Public executable exceeds inspection bound.');
const source = readFileSync(path).toString('latin1');
if (!Number.isSafeInteger(after) || after >= source.length) throw new Error('Invalid start offset.');
for (const needle of needles) {
  const window = /^@(\d+):(\d+)$/.exec(needle);
  if (window) {
    const start = Number(window[1]), length = Number(window[2]);
    if (length > 12000 || start >= source.length) throw new Error('Invalid source window.');
    console.log(JSON.stringify({ offset: start, source: source.slice(start, start + length) }));
    continue;
  }
  let offset = after;
  let count = 0;
  while (count < 3) {
    const found = source.indexOf(needle, offset);
    if (found < 0) break;
    console.log(JSON.stringify({ needle, offset: found,
      source: source.slice(Math.max(0, found - 250), Math.min(source.length, found + 2900)) }));
    offset = found + needle.length;
    count++;
  }
  if (!count) console.log(JSON.stringify({ needle, matches: 0 }));
}
