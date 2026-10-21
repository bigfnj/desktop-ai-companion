// Reviewed one-shot user-scope Claude Code registration. Never runs mcp get/list or inference.
// Run only after reviewing this script, checking host projection, and approving the exact write.
// node tools/agentbroker/Connect-ClaudeMcpEntry.mjs --apply <claude.exe> <worker.exe> <data-directory> <project-directory> [<Git/bin/bash.exe>]
// Pure path-policy checks, no public file reads or CLI launch: node tools/agentbroker/Connect-ClaudeMcpEntry.mjs --self-test
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, lstatSync, readFileSync } from 'node:fs';
import { isAbsolute, join, parse, resolve, sep } from 'node:path';

const [mode, cliArgument, workerArgument, dataArgument, projectArgument, bashArgument, ...extra] = process.argv.slice(2);
const expectedSha = 'EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23';
const serverName = 'desktop-ai-companion-agentbroker';
function report(status, additional = {}) { console.log(JSON.stringify({ status, ...additional })); }
function absolute(path) {
  if (!path || !isAbsolute(path) || path.startsWith('\\\\') || path.indexOf(':', 2) >= 0) throw new Error('invalid_path');
  const full = resolve(path), parts = full.slice(parse(full).root.length).split(sep).filter(Boolean);
  if (parts.some(part => part !== part.replace(/[ .]+$/, '') ||
      /^(?:\.env(?:\..*)?|\.npmrc|privatekey.*|\.aws|\.ssh|\.kube|\.pypirc|\.cargo|\.docker|\.git|\.git-credentials|\.dockercfg|\.pip|\.config|\.mozilla|id_.*|credentials(?:\.toml)?)$/i.test(part) ||
      /\.(?:env|pem|key|p12|pfx|keystore)$/i.test(part))) throw new Error('protected_path_refused');
  const normalized = '/' + parts.join('/').toLowerCase();
  if (/\/(?:google\/chrome|microsoft\/edge)\/user data(?:\/|$)/.test(normalized) ||
      /\/appdata(?:\/.*)?\/mozilla\/firefox(?:\/|$)/.test(normalized) ||
      /\/library\/(?:keychains|application support\/(?:google\/chrome|firefox|microsoft edge))(?:\/|$)/.test(normalized))
    throw new Error('protected_path_refused');
  return full;
}
function unlinked(path) {
  let current = parse(path).root;
  for (const part of path.slice(current.length).split(sep).filter(Boolean)) {
    current = join(current, part);
    if (lstatSync(current).isSymbolicLink()) throw new Error('linked_path');
  }
  return path;
}
function file(path, leaf, expected) {
  const full = absolute(path);
  if (expected && full.toLowerCase() !== absolute(expected).toLowerCase()) throw new Error('public_tool_location_refused');
  unlinked(full);
  const info = lstatSync(full);
  if (!info.isFile() || info.nlink !== 1 || full.split(sep).at(-1).toLowerCase() !== leaf) throw new Error('invalid_public_file');
  return full;
}
function inspect(worker, data, project) {
  const result = spawnSync(process.execPath, [join(import.meta.dirname, 'Inspect-ClaudeMcpEntry.mjs'), worker, data, project],
    { windowsHide: true, encoding: 'utf8', timeout: 25000, maxBuffer: 16 * 1024 });
  if (result.error || result.status !== 0) throw new Error('entry_inspection_unavailable');
  try { return JSON.parse(result.stdout.trim()); } catch { throw new Error('entry_inspection_unavailable'); }
}
function classification(result) {
  const text = result.stdout + '\n' + result.stderr;
  if (result.exitCode === 0) return 'cli_success';
  if (/enterprise policy|enterprise MCP configuration|managed settings|managed MCP/i.test(text)) return 'managed_policy_refusal';
  if (/already exists/i.test(text)) return 'entry_exists';
  if (/EACCES|EPERM|access (?:is )?denied|permission denied|failed to write|config lock|lock acquisition/i.test(text)) return 'configuration_write_failed';
  if (/unknown option|unexpected argument|Usage:|required option|command is required/i.test(text)) return 'cli_argument_rejected';
  if (/requires a shell tool|(?:Git Bash|git-bash|PowerShell).*(?:not found|disabled|requires)/i.test(text)) return 'shell_dependency_missing';
  return 'other_cli_failure';
}
function policyReason(result) {
  const text = result.stdout + '\n' + result.stderr;
  if (/enterprise MCP configuration is active and has exclusive control/i.test(text)) return 'exclusive_control';
  if (/managed settings allow only MCP servers that plugins provide/i.test(text)) return 'plugin_only';
  if (/explicitly blocked by enterprise policy/i.test(text)) return 'explicit_deny';
  if (/not allowed by enterprise policy/i.test(text)) return 'allowlist_miss';
  return 'unspecified';
}

try {
  if (mode === '--self-test' && process.argv.length === 3) {
    let checks = 0;
    for (const path of ['C:\\synthetic\\.aws\\claude.exe', 'C:\\synthetic\\fixture.env\\claude.exe',
      'C:\\synthetic\\privatekey-fixture\\claude.exe', 'C:\\synthetic\\AppData\\Local\\Mozilla\\Firefox\\claude.exe',
      'C:\\synthetic\\AppData\\Local\\Google\\Chrome\\User Data\\claude.exe']) {
      let refused = false;
      try { absolute(path); } catch (error) { refused = error.message === 'protected_path_refused'; }
      if (!refused) throw new Error('invalid_path');
      checks++;
    }
    let locationRefused = false;
    try { file('C:\\synthetic\\different\\claude.exe', 'claude.exe', 'C:\\synthetic\\home\\.local\\bin\\claude.exe'); }
    catch (error) { locationRefused = error.message === 'public_tool_location_refused'; }
    if (!locationRefused) throw new Error('invalid_path');
    report('self_test_passed', { checks: checks + 1, publicFilesOpened: false, cliLaunched: false });
    process.exit(0);
  }
  if (process.platform !== 'win32' || mode !== '--apply' || extra.length) throw new Error('invalid_arguments');
  const home = absolute(process.env.USERPROFILE), local = absolute(process.env.LOCALAPPDATA);
  const cli = file(cliArgument, 'claude.exe', join(home, '.local', 'bin', 'claude.exe'));
  if (lstatSync(cli).size > 512 * 1024 * 1024 || createHash('sha256').update(readFileSync(cli)).digest('hex').toUpperCase() !== expectedSha)
    throw new Error('public_cli_changed');
  const worker = file(workerArgument, 'agentbroker.worker.exe');
  const data = unlinked(absolute(dataArgument)), project = unlinked(absolute(projectArgument));
  if (!lstatSync(data).isDirectory() || !lstatSync(project).isDirectory()) throw new Error('invalid_directory');
  const bash = bashArgument ? file(bashArgument, 'bash.exe', join(local, 'Programs', 'Git', 'bin', 'bash.exe')) : undefined;
  // This helper specifically targets the ordinary user config inspected below. A legacy
  // alternate global file would invalidate that absence check, so refuse it by metadata.
  if (existsSync(join(home, '.claude', '.config.json'))) throw new Error('legacy_configuration_present');
  const before = inspect(worker, data, project);
  if (before.status !== 'not_found' && before.status !== 'ok') throw new Error('entry_inspection_unavailable');
  if (before.status === 'ok' && (before.userEntryFound !== false || before.localEntryFound !== false))
    throw new Error('existing_entry_refused');

  // Retain ordinary Windows profile/tool paths. No inherited vendor host, cloud, proxy,
  // API, OAuth, or alternate config-root variables are delivered to this CLI process.
  // Managed policy remains active; no policy controls are disabled or overridden.
  const env = {};
  for (const name of ['SystemRoot', 'WINDIR', 'ComSpec', 'PATH', 'PATHEXT', 'ProgramData', 'ALLUSERSPROFILE',
    'USERPROFILE', 'HOMEDRIVE', 'HOMEPATH', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP']) {
    const actual = Object.keys(process.env).find(key => key.toLowerCase() === name.toLowerCase());
    if (actual && process.env[actual]) env[name] = process.env[actual];
  }
  Object.assign(env, { HOME: home, CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
    DISABLE_TELEMETRY: '1', DISABLE_ERROR_REPORTING: '1', DISABLE_AUTOUPDATER: '1', NO_COLOR: '1' });
  if (bash) env.CLAUDE_CODE_GIT_BASH_PATH = bash;
  const argv = ['mcp', 'add', '--transport', 'stdio', '--scope', 'user', serverName,
    '--', worker, '--mcp-stdio', '--data', data];
  const result = await new Promise((complete, reject) => {
    const child = spawn(cli, argv, { cwd: project, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '', stderr = '', oversized = false, timedOut = false;
    const collect = kind => bytes => {
      if (Buffer.byteLength(stdout) + Buffer.byteLength(stderr) + bytes.length > 128 * 1024) {
        oversized = true; child.kill(); return;
      }
      if (kind === 'stdout') stdout += bytes.toString('utf8'); else stderr += bytes.toString('utf8');
    };
    child.stdout.on('data', collect('stdout')); child.stderr.on('data', collect('stderr'));
    const timer = setTimeout(() => { timedOut = true; child.kill(); }, 20000);
    child.once('error', () => { clearTimeout(timer); reject(new Error('cli_start_failed')); });
    child.once('close', exitCode => { clearTimeout(timer); complete({ exitCode, stdout, stderr, oversized, timedOut }); });
  });
  // A failed/timed-out CLI can still have written the entry before failing. Inspect only
  // the selected config entry afterward, and never retry or overwrite automatically.
  let after;
  try { after = inspect(worker, data, project); } catch { after = { status: 'unavailable' }; }
  const registrationConfirmed = after.status === 'ok' && after.userEntryFound === true &&
    after.userType === 'stdio' && after.userCommandMatches === true && after.userArgumentsMatch === true;
  report('completed', { publicVersion: '2.1.292', exitCode: result.exitCode,
    reason: result.timedOut ? 'timed_out' : result.oversized ? 'cli_output_limit' : classification(result),
    managedPolicyReason: policyReason(result), registrationConfirmed });
  if (!registrationConfirmed) process.exitCode = 1;
} catch (error) {
  // Only fixed local error codes are permitted; never print vendor stdout/stderr or config.
  const allowed = new Set(['invalid_path', 'protected_path_refused', 'public_tool_location_refused', 'linked_path', 'invalid_public_file', 'invalid_arguments', 'public_cli_changed',
    'invalid_directory', 'legacy_configuration_present', 'entry_inspection_unavailable', 'existing_entry_refused', 'cli_start_failed']);
  report('refused', { reason: allowed.has(error.message) ? error.message : 'diagnostic_unavailable' });
  process.exitCode = 1;
}
