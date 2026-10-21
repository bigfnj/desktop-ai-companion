// Diagnose the installed public CLI parser without opening the user's MCP configuration.
// Usage: node tools/agentbroker/Probe-IsolatedClaudeMcpSetup.mjs <claude.exe> [<Git/bin/bash.exe>]
// Synthetic path/cleanup checks, no public CLI reads or launch: node tools/agentbroker/Probe-IsolatedClaudeMcpSetup.mjs --self-test
// Source-reviewed profile: public Windows Claude Code 2.1.292 only. No inference, native inbox,
// mcp get/list health check, client restart, or inherited credentials. Managed policy stays active.
import { createHash, randomUUID } from 'node:crypto';
import { readFileSync, lstatSync, mkdirSync, readdirSync, realpathSync, unlinkSync, rmdirSync, renameSync, writeFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { isAbsolute, join, parse, relative, resolve, sep } from 'node:path';

async function main() {
const expectedSha = 'EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23';
const [publicCliArgument, publicBashArgument, ...extra] = process.argv.slice(2);
const selfTest = publicCliArgument === '--self-test' && !publicBashArgument && !extra.length;
if (!publicCliArgument || extra.length || process.platform !== 'win32')
  throw new Error('Usage on Windows: node tools/agentbroker/Probe-IsolatedClaudeMcpSetup.mjs <claude.exe> [<Git/bin/bash.exe>]');

function safeAbsolute(path) {
  if (!path || !isAbsolute(path) || path.startsWith('\\\\') || path.indexOf(':', 2) >= 0) throw new Error('invalid_path');
  const full = resolve(path), parts = full.slice(parse(full).root.length).split(sep).filter(Boolean);
  if (parts.some(part => part !== part.replace(/[ .]+$/, '') || protectedLeaf(part))) throw new Error('protected_path_refused');
  const normalized = '/' + parts.join('/').toLowerCase();
  if (/\/(?:google\/chrome|microsoft\/edge)\/user data(?:\/|$)/.test(normalized) ||
      /\/appdata(?:\/.*)?\/mozilla\/firefox(?:\/|$)/.test(normalized) ||
      /\/library\/(?:keychains|application support\/(?:google\/chrome|firefox|microsoft edge))(?:\/|$)/.test(normalized))
    throw new Error('protected_path_refused');
  return full;
}

function unlinkedPath(path) {
  const absolute = safeAbsolute(path);
  let current = parse(absolute).root;
  for (const part of absolute.slice(current.length).split(sep).filter(Boolean)) {
    current = join(current, part);
    const info = lstatSync(current);
    if (info.isSymbolicLink()) throw new Error('Refusing a linked path component.');
  }
  return absolute;
}

function publicExecutable(path, leaf, expected) {
  if (safeAbsolute(path).toLowerCase() !== safeAbsolute(expected).toLowerCase()) throw new Error('public_tool_location_refused');
  const absolute = unlinkedPath(path);
  const info = lstatSync(absolute);
  if (!info.isFile() || info.nlink !== 1 || absolute.split(sep).at(-1).toLowerCase() !== leaf)
    throw new Error('Expected a regular, single-link, explicitly named public executable.');
  return absolute;
}

const publicCli = selfTest ? undefined : publicExecutable(publicCliArgument, 'claude.exe',
  join(safeAbsolute(process.env.USERPROFILE), '.local', 'bin', 'claude.exe'));
if (!selfTest && (lstatSync(publicCli).size > 512 * 1024 * 1024 ||
    createHash('sha256').update(readFileSync(publicCli)).digest('hex').toUpperCase() !== expectedSha))
  throw new Error('Public CLI changed. Review its isolation and subcommand lifecycle before running this probe.');
const publicBash = publicBashArgument ? publicExecutable(publicBashArgument, 'bash.exe',
  join(safeAbsolute(process.env.LOCALAPPDATA), 'Programs', 'Git', 'bin', 'bash.exe')) : undefined;
const repo = realpathSync(resolve(import.meta.dirname, '..', '..'));
const artifacts = join(repo, 'artifacts');
try { mkdirSync(artifacts); } catch (e) { if (e.code !== 'EEXIST') throw e; }
unlinkedPath(artifacts);
const root = join(artifacts, 'isolated-claude-mcp-' + randomUUID());
mkdirSync(root);

function inside(path) {
  const tail = relative(root, path);
  if (!tail || tail.startsWith('..' + sep) || tail === '..' || resolve(path) === root || parse(tail).root)
    throw new Error('Probe path escaped its unique fixture.');
  return path;
}

function protectedLeaf(name) {
  // Conservative supersets also cover home credential stores if a future vendor build
  // unexpectedly creates one below the synthetic home. Inspect names only, then refuse.
  return /^(?:\.env(?:\..*)?|\.npmrc|privatekey.*|\.aws|\.ssh|\.kube|\.pypirc|\.cargo|\.docker|\.git-credentials|\.dockercfg|\.git|\.pip|\.config|\.mozilla|id_.*|credentials(?:\.toml)?)$/i.test(name) ||
    /\.(?:env|pem|key|p12|pfx|keystore)$/i.test(name);
}

// Enumerate names first, never read or remove a protected unexpected output.
function fixtureFiles(directory, cleanup) {
  unlinkedPath(directory);
  const directoryInfo = lstatSync(directory);
  if (!directoryInfo.isDirectory()) throw new Error('Unexpected fixture root type.');
  const files = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (protectedLeaf(entry.name)) throw new Error('Unexpected protected output; retained the fixture for separately approved handling.');
    const path = inside(join(directory, entry.name));
    const info = lstatSync(path);
    if (info.isSymbolicLink() || info.nlink > 1 && !info.isDirectory())
      throw new Error('Unexpected linked output; retained the fixture.');
    if (info.isDirectory()) files.push(...fixtureFiles(path, cleanup));
    else if (info.isFile()) {
      files.push(path);
      cleanup?.files.push({ path, dev: info.dev, ino: info.ino });
    }
    else throw new Error('Unexpected output type; retained the fixture.');
  }
  cleanup?.directories.push({ path: directory, dev: directoryInfo.dev, ino: directoryInfo.ino });
  return files;
}

function removeFixture(cleanup) {
  // Never walk a second tree during removal. Unknown later files stay untouched and
  // make the nonrecursive rmdir fail; every planned object must still be the same file.
  for (const entry of cleanup.files) {
    const path = unlinkedPath(inside(entry.path)), info = lstatSync(path);
    if (protectedLeaf(parse(path).base) || !info.isFile() || info.nlink !== 1 || info.dev !== entry.dev || info.ino !== entry.ino)
      throw new Error('Fixture identity changed; retained remaining output.');
    unlinkSync(path);
  }
  for (const entry of cleanup.directories) {
    const path = entry.path === root ? root : inside(entry.path);
    unlinkedPath(path);
    const info = lstatSync(path);
    if (!info.isDirectory() || info.dev !== entry.dev || info.ino !== entry.ino)
      throw new Error('Fixture directory changed; retained remaining output.');
    rmdirSync(path);
  }
}

function classify(exitCode, stdout, stderr) {
  const text = stdout + '\n' + stderr;
  if (exitCode === 0) return 'added';
  if (/enterprise policy|enterprise MCP configuration|managed settings|organization.*MCP/i.test(text)) return 'managed_policy_refusal';
  if (/git.bash|requires a shell tool|PowerShell.*(?:not found|disabled)/i.test(text)) return 'shell_dependency_missing';
  if (/already exists/i.test(text)) return 'entry_exists';
  if (/unknown option|unexpected argument|Usage:|command is required|server name is required/i.test(text)) return 'cli_argument_rejected';
  if (/EACCES|EPERM|access.*denied|permission.*denied|Failed to write|config.*lock/i.test(text)) return 'configuration_write_failed';
  if (/not recognized|ENOENT|not found/i.test(text)) return 'dependency_not_found';
  return 'other_cli_failure';
}

async function probe(label, bashPath) {
  const fixture = inside(join(root, label));
  mkdirSync(fixture);
  const folders = Object.fromEntries(['home', 'config', 'secure-storage', 'roaming', 'local', 'temp', 'project']
    .map(name => [name, inside(join(fixture, name))]));
  for (const path of Object.values(folders)) mkdirSync(path);
  const env = {};
  // Retain Windows executable loading and the caller's public tool search path only.
  // No Anthropic/cloud/proxy/host credential environment is inherited.
  for (const name of ['SystemRoot', 'WINDIR', 'ComSpec', 'PATH', 'PATHEXT', 'ProgramData', 'ALLUSERSPROFILE']) {
    const actual = Object.keys(process.env).find(key => key.toLowerCase() === name.toLowerCase());
    if (actual && process.env[actual]) env[name] = process.env[actual];
  }
  Object.assign(env, {
    HOME: folders.home, USERPROFILE: folders.home,
    HOMEDRIVE: parse(folders.home).root.slice(0, 2), HOMEPATH: folders.home.slice(2),
    APPDATA: folders.roaming, LOCALAPPDATA: folders.local,
    CLAUDE_CONFIG_DIR: folders.config, CLAUDE_SECURESTORAGE_CONFIG_DIR: folders['secure-storage'],
    XDG_CONFIG_HOME: folders.config, TMP: folders.temp, TEMP: folders.temp,
    CLAUDE_CODE_TMPDIR: folders.temp, CLAUDE_TMPDIR: folders.temp,
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1', DISABLE_TELEMETRY: '1', DISABLE_ERROR_REPORTING: '1',
    DISABLE_AUTOUPDATER: '1', NO_COLOR: '1', CI: '1'
  });
  if (bashPath) env.CLAUDE_CODE_GIT_BASH_PATH = bashPath;
  const serverName = 'agentbroker-isolated-setup-probe';
  const command = inside(join(fixture, 'synthetic worker directory', 'AgentBroker.Worker.exe'));
  const childArguments = ['--mcp-stdio', '--data', inside(join(fixture, 'synthetic data directory'))];
  const argv = ['--project-config-root', folders.project, '--setting-sources', 'user',
    'mcp', 'add', '--transport', 'stdio', '--scope', 'user', serverName, '--', command, ...childArguments];
  const result = await new Promise((complete, reject) => {
    const child = spawn(publicCli, argv, { cwd: folders.project, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '', stderr = '', oversized = false;
    const collect = key => data => {
      if (Buffer.byteLength(stdout) + Buffer.byteLength(stderr) + data.length > 128 * 1024) {
        oversized = true; child.kill(); return;
      }
      if (key === 'stdout') stdout += data.toString('utf8'); else stderr += data.toString('utf8');
    };
    child.stdout.on('data', collect('stdout')); child.stderr.on('data', collect('stderr'));
    const timer = setTimeout(() => child.kill(), 20000);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('close', (exitCode, signal) => { clearTimeout(timer); complete({ exitCode, signal, stdout, stderr, oversized }); });
  });
  // All output remains synthetic. No raw vendor diagnostics/config contents are printed.
  const files = fixtureFiles(fixture);
  const configPath = join(folders.config, '.claude.json');
  let exactEntry = false;
  if (files.includes(configPath)) {
    const entry = JSON.parse(readFileSync(configPath, 'utf8')).mcpServers?.[serverName];
    exactEntry = entry?.type === 'stdio' && entry.command === command &&
      Array.isArray(entry.args) && JSON.stringify(entry.args) === JSON.stringify(childArguments);
  }
  return { label, exitCode: result.exitCode, signal: result.signal, oversized: result.oversized,
    classification: classify(result.exitCode, result.stdout, result.stderr), exactEntry,
    fileCount: files.length, nativeSessionDirectoryCreated: files.some(path => relative(folders.config, path).split(sep)[0] === 'sessions') };
}

if (selfTest) {
  let checks = 0;
  const assert = value => { if (!value) throw new Error('Synthetic policy check failed.'); checks++; };
  try {
    for (const path of ['C:\\synthetic\\.aws\\claude.exe', 'C:\\synthetic\\fixture.env\\claude.exe',
      'C:\\synthetic\\privatekey-fixture\\claude.exe', 'C:\\synthetic\\AppData\\Local\\Mozilla\\Firefox\\claude.exe',
      'C:\\synthetic\\AppData\\Local\\Google\\Chrome\\User Data\\claude.exe']) {
      let refused = false;
      try { safeAbsolute(path); } catch (error) { refused = error.message === 'protected_path_refused'; }
      assert(refused); // strings only; no protected fixture is created or opened
    }
    let locationRefused = false;
    try { publicExecutable('C:\\synthetic\\different\\claude.exe', 'claude.exe', 'C:\\synthetic\\home\\.local\\bin\\claude.exe'); }
    catch (error) { locationRefused = error.message === 'public_tool_location_refused'; }
    assert(locationRefused); // location refusal precedes even metadata access
    const late = inside(join(root, 'late-output'));
    mkdirSync(late);
    writeFileSync(inside(join(late, 'planned.txt')), 'owned synthetic fixture', { flag: 'wx' });
    const latePlan = { files: [], directories: [] };
    fixtureFiles(late, latePlan);
    const unexpected = inside(join(late, 'unexpected.txt'));
    writeFileSync(unexpected, 'new synthetic output', { flag: 'wx' });
    let retained = false;
    try { removeFixture(latePlan); } catch { retained = lstatSync(unexpected).isFile(); }
    assert(retained);
    const changed = inside(join(root, 'changed-output'));
    mkdirSync(changed);
    const first = inside(join(changed, 'first.txt')), second = inside(join(changed, 'second.txt'));
    writeFileSync(first, 'original synthetic fixture', { flag: 'wx' });
    writeFileSync(second, 'replacement synthetic fixture', { flag: 'wx' });
    const changedPlan = { files: [], directories: [] };
    fixtureFiles(changed, changedPlan);
    unlinkSync(first); renameSync(second, first);
    let changedRefused = false;
    try { removeFixture(changedPlan); } catch { changedRefused = lstatSync(first).isFile(); }
    assert(changedRefused);
  } finally {
    const cleanup = { files: [], directories: [] };
    fixtureFiles(root, cleanup); removeFixture(cleanup);
  }
  console.log(JSON.stringify({ status: 'self_test_passed', checks, fixtureRemoved: true, publicFilesOpened: false, cliLaunched: false }));
  return;
}

try {
  const results = [await probe('inherited-public-path', undefined)];
  if (publicBash) results.push(await probe('explicit-public-git-bash', publicBash));
  const cleanup = { files: [], directories: [] };
  fixtureFiles(root, cleanup); // Whole-tree metadata preflight before any removal.
  removeFixture(cleanup);
  console.log(JSON.stringify({ publicVersion: '2.1.292', profilesIsolated: true, fixtureRemoved: true, results }));
} catch (error) {
  // This contains only a fixed classifier/exception type, never vendor output or a config value.
  const errorType = ['Error', 'TypeError', 'SyntaxError', 'RangeError'].includes(error.name) ? error.name : 'Error';
  console.log(JSON.stringify({ profilesIsolated: true, fixtureRetained: true, errorType, reason: 'probe_failed' }));
  process.exitCode = 1;
}
}

main().catch(() => {
  // Startup failures can include caller paths in native filesystem errors too.
  console.log(JSON.stringify({ status: 'refused', reason: 'diagnostic_unavailable' }));
  process.exitCode = 1;
});
