// Reviewed one-shot user-scope Claude Code registration or exact-only HTTP connection check.
// Run only after reviewing this script, checking host projection, and approving the exact action.
// node tools/agentbroker/Connect-ClaudeMcpEntry.mjs --apply <claude.exe> <worker.exe> <data-directory> <project-directory> [<Git/bin/bash.exe>]
// --apply-http instead explicitly asks Claude to register http://127.0.0.1:7432/mcp under the same name. An allowlist
// miss ("not allowed by enterprise policy") gets one more add with http://localhost:7432/mcp, which a managed policy can
// admit where it refuses 127.0.0.1 and the worker also answers (N-bus-04); no other refusal is retried.
// --verify-http instead runs only `mcp get desktop-ai-companion-agentbroker`, after exact HTTP preflight. Either canonical
// spelling of the endpoint is the exact entry.
// It prints fixed connection fields only. No arbitrary server name, list, login, model or native inbox command.
// Pure path-policy checks, no public file reads or CLI launch: node tools/agentbroker/Connect-ClaudeMcpEntry.mjs --self-test
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, lstatSync, readFileSync } from 'node:fs';
import { isAbsolute, join, parse, resolve, sep } from 'node:path';

const [mode, cliArgument, workerArgument, dataArgument, projectArgument, bashArgument, ...extra] = process.argv.slice(2);
const expectedSha = 'EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23';
const serverName = 'desktop-ai-companion-agentbroker';
const loopbackEndpoint = 'http://127.0.0.1:7432/mcp';
const localhostEndpoint = 'http://localhost:7432/mcp';
const useHttp = mode === '--apply-http';
const verifyHttp = mode === '--verify-http';
function report(status, additional = {}) { console.log(JSON.stringify({ status, ...additional })); }
function strictLoopbackEndpoint(value) {
  if (value !== loopbackEndpoint && value !== localhostEndpoint) throw new Error('invalid_loopback_endpoint');
  return value;
}
function argumentsFor(http, worker, data, endpoint = loopbackEndpoint) {
  return http
    ? ['mcp', 'add', '--transport', 'http', '--scope', 'user', serverName, strictLoopbackEndpoint(endpoint)]
    : ['mcp', 'add', '--transport', 'stdio', '--scope', 'user', serverName, '--', worker, '--mcp-stdio', '--data', data];
}
// One localhost add after an HTTP allowlist miss on the literal URL; an explicit deny, exclusive or plugin-only control
// and every non-policy failure are final.
function retriesWithLocalhost(http, endpoint, result) {
  return http && endpoint === loopbackEndpoint && result.exitCode !== 0 && !result.timedOut && !result.oversized &&
    policyReason(result) === 'allowlist_miss';
}
function verificationArguments() { return ['mcp', 'get', serverName]; }
function probeEntryMatches(entry) {
  return entry?.status === 'ok' && entry.userEntryFound === true && entry.userType === 'http' &&
    entry.userUrlMatches === true && entry.userHttpProbeSafe === true && entry.localEntryFound === false;
}
function noProjectMcpOverride(project, metadata = lstatSync) {
  for (let current = project; ; current = join(current, '..')) {
    try { metadata(join(current, '.mcp.json')); }
    catch (error) {
      if (error.code === 'ENOENT' || error.code === 'ENOTDIR') {
        if (current === parse(current).root) return;
        continue;
      }
      throw new Error('project_override_refused');
    }
    throw new Error('project_override_refused');
  }
}
function connectionFields(stdout) {
  // Reviewed public 2.1.292 mcpGetHandler prints these labels. Unknown/new formats fail
  // closed. Never return Issue, Headers, OAuth, paths or any other vendor output.
  const lines = stdout.replace(/\r/g, '').split('\n');
  const headings = lines.flatMap((line, index) => line === serverName + ':' ? [index] : []);
  const fields = { reportedNameMatches: headings.length === 1, reportedScopeIsUser: false,
    reportedTypeIsHttp: false, reportedUrlMatches: false, connectionState: 'unknown' };
  if (headings.length !== 1) return fields;
  const block = lines.slice(headings[0] + 1);
  const one = label => {
    const prefix = '  ' + label + ': ', matches = block.filter(line => line.startsWith(prefix));
    return matches.length === 1 ? matches[0].slice(prefix.length) : null;
  };
  fields.reportedScopeIsUser = one('Scope') === 'User config (available in all your projects)';
  fields.reportedTypeIsHttp = one('Type') === 'http';
  fields.reportedUrlMatches = [loopbackEndpoint, localhostEndpoint].includes(one('URL'));
  const status = one('Status');
  // The pinned public figures table uses U+221A on Windows and U+2714 elsewhere.
  if (/^[✓✔√] Connected$/.test(status ?? '')) fields.connectionState = 'connected';
  else if (status === '! Connected · tools fetch failed') fields.connectionState = 'tools_unavailable';
  else if (status === '! Needs authentication') fields.connectionState = 'needs_authentication';
  else if (/^[✗✘×] Failed to connect$/.test(status ?? '')) fields.connectionState = 'failed';
  else if (/^[✗✘×] Connection error$/.test(status ?? '')) fields.connectionState = 'connection_error';
  else if (status === '⏸ Pending approval (run `claude` to approve)') fields.connectionState = 'pending_approval';
  else if (/^[✗✘×] Rejected \(see disabledMcpjsonServers in settings\)$/.test(status ?? '')) fields.connectionState = 'rejected';
  else if (status === '⊘ Disabled for this project (re-enable via /mcp)') fields.connectionState = 'disabled';
  else if (status === '- Not configured') fields.connectionState = 'not_configured';
  return fields;
}
function connectionConfirmed(result, entry) {
  const fields = connectionFields(result.stdout);
  return result.exitCode === 0 && !result.timedOut && !result.oversized && probeEntryMatches(entry) &&
    fields.reportedNameMatches && fields.reportedScopeIsUser && fields.reportedTypeIsHttp &&
    fields.reportedUrlMatches && fields.connectionState === 'connected';
}
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
  const argv = [join(import.meta.dirname, 'Inspect-ClaudeMcpEntry.mjs'), worker, data, project];
  // The inspector counts the localhost spelling of this literal URL as the same exact entry.
  if (useHttp || verifyHttp) argv.push('--http-url', strictLoopbackEndpoint(loopbackEndpoint));
  const result = spawnSync(process.execPath, argv,
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
// Only fixed local error codes are reported; anything else (a runtime or vendor error, whose message could carry a path
// or CLI output) reads diagnostic_unavailable. Every code this file throws must be listed here: tests/Test-AgentBroker.ps1
// compares the set with the file's Error literals, and the self-test checks both mappings (N-broker-d-01, where the
// self-test's own invalid_verification was missing and read diagnostic_unavailable).
const reportableErrors = new Set(['invalid_path', 'invalid_loopback_endpoint', 'protected_path_refused', 'public_tool_location_refused',
  'linked_path', 'invalid_public_file', 'invalid_arguments', 'public_cli_changed', 'invalid_directory', 'invalid_retry',
  'invalid_verification', 'legacy_configuration_present', 'entry_inspection_unavailable', 'existing_entry_refused',
  'probe_entry_refused', 'project_override_refused', 'cli_start_failed']);
function reportableReason(error) {
  return typeof error?.message === 'string' && reportableErrors.has(error.message) ? error.message : 'diagnostic_unavailable';
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
    checks++;
    if (strictLoopbackEndpoint(loopbackEndpoint) !== loopbackEndpoint || strictLoopbackEndpoint(localhostEndpoint) !== localhostEndpoint)
      throw new Error('invalid_loopback_endpoint');
    checks++;
    for (const url of ['https://127.0.0.1:7432/mcp', 'http://LOCALHOST:7432/mcp', 'http://localhost:7433/mcp', 'http://127.1:7432/mcp',
      'http://127.0.0.1:7432/mcp?token=synthetic', 'http://127.0.0.1:7432/mcp#fragment', 'http://localhost.evil.invalid:7432/mcp',
      'http://user@127.0.0.1:7432/mcp', 'http://127.0.0.1:7433/mcp', 'http://127.0.0.1.evil.invalid:7432/mcp']) {
      let refused = false;
      try { strictLoopbackEndpoint(url); } catch (error) { refused = error.message === 'invalid_loopback_endpoint'; }
      if (!refused) throw new Error('invalid_loopback_endpoint');
      checks++;
    }
    if (JSON.stringify(argumentsFor(true, 'synthetic-worker', 'synthetic-data')) !== JSON.stringify([
      'mcp', 'add', '--transport', 'http', '--scope', 'user', serverName, loopbackEndpoint])) throw new Error('invalid_arguments');
    checks++;
    if (JSON.stringify(argumentsFor(false, 'synthetic worker', 'synthetic data')) !== JSON.stringify([
      'mcp', 'add', '--transport', 'stdio', '--scope', 'user', serverName, '--', 'synthetic worker', '--mcp-stdio', '--data', 'synthetic data']))
      throw new Error('invalid_arguments');
    checks++;
    if (JSON.stringify(argumentsFor(true, 'synthetic-worker', 'synthetic-data', localhostEndpoint)) !== JSON.stringify([
      'mcp', 'add', '--transport', 'http', '--scope', 'user', serverName, localhostEndpoint])) throw new Error('invalid_arguments');
    checks++;
    // The retry rule: an HTTP allowlist miss on the literal URL only, never a deny, a stdio add, a retry or another failure.
    const miss = { exitCode: 1, stdout: '', stderr: 'Cannot add MCP server: not allowed by enterprise policy', timedOut: false, oversized: false };
    if (!retriesWithLocalhost(true, loopbackEndpoint, miss)) throw new Error('invalid_retry');
    checks++;
    for (const [http, endpoint, changed] of [
      [true, loopbackEndpoint, { ...miss, stderr: 'Cannot add MCP server: server is explicitly blocked by enterprise policy' }],
      [true, loopbackEndpoint, { ...miss, stderr: 'EPERM: configuration write failed' }],
      [true, loopbackEndpoint, { ...miss, exitCode: 0 }], [true, loopbackEndpoint, { ...miss, timedOut: true }],
      [false, loopbackEndpoint, miss], [true, localhostEndpoint, miss]]) {
      if (retriesWithLocalhost(http, endpoint, changed)) throw new Error('invalid_retry');
      checks++;
    }
    const entry = { status: 'ok', userEntryFound: true, userType: 'http', userUrlMatches: true,
      userHttpProbeSafe: true, localEntryFound: false };
    const privateMarker = 'SYNTHETIC_CONNECTION_PRIVATE_VALUE';
    const output = `${serverName}:\n  Scope: User config (available in all your projects)\n  Status: ✓ Connected\n  Type: http\n  URL: ${loopbackEndpoint}\n  Issue: ${privateMarker}\n  Headers:\n    Authorization: ${privateMarker}\n`;
    const result = { exitCode: 0, stdout: output, stderr: privateMarker, timedOut: false, oversized: false };
    if (JSON.stringify(verificationArguments()) !== JSON.stringify(['mcp', 'get', serverName]) ||
      !connectionConfirmed(result, entry) || JSON.stringify(connectionFields(output)).includes(privateMarker)) throw new Error('invalid_verification');
    checks++;
    for (const status of ['√ Connected', '✔ Connected']) {
      if (!connectionConfirmed({ ...result, stdout: output.replace('✓ Connected', status) }, entry)) throw new Error('invalid_verification');
      checks++;
    }
    if (!connectionConfirmed({ ...result, stdout: output.replace(loopbackEndpoint, localhostEndpoint) }, entry)) throw new Error('invalid_verification');
    checks++;
    for (const status of ['! Connected · tools fetch failed', '! Needs authentication', '✘ Failed to connect',
      '✘ Connection error', '⏸ Pending approval (run `claude` to approve)', '✘ Rejected (see disabledMcpjsonServers in settings)',
      '⊘ Disabled for this project (re-enable via /mcp)', '- Not configured', 'new vendor state']) {
      const changed = { ...result, stdout: output.replace('✓ Connected', status) };
      if (connectionConfirmed(changed, entry)) throw new Error('invalid_verification');
      checks++;
    }
    for (const changed of [ { ...result, exitCode: 1 }, { ...result, timedOut: true }, { ...result, oversized: true },
      { ...result, stdout: output.replace(serverName + ':', 'another-server:') },
      { ...result, stdout: output.replace('Type: http', 'Type: stdio') },
      { ...result, stdout: output.replace(loopbackEndpoint, 'http://localhost:7433/mcp') },
      { ...result, stdout: output.replace('User config (available in all your projects)', 'Project config (shared via .mcp.json)') },
      { ...result, stdout: output + '  Status: ✓ Connected\n' },
      { ...result, stdout: output + '  URL: http://example.invalid/mcp\n' } ]) {
      if (connectionConfirmed(changed, entry)) throw new Error('invalid_verification');
      checks++;
    }
    for (const changed of [null, { ...entry, userHttpProbeSafe: false }, { ...entry, localEntryFound: true },
      { ...entry, userUrlMatches: false }, { ...entry, userType: 'stdio' }]) {
      if (connectionConfirmed(result, changed)) throw new Error('invalid_verification');
      checks++;
    }
    let visited = 0;
    noProjectMcpOverride('C:\\synthetic\\project', () => { visited++; const error = new Error(); error.code = 'ENOENT'; throw error; });
    if (visited !== 3) throw new Error('invalid_verification');
    checks++;
    for (const metadata of [() => ({}), () => { const error = new Error(); error.code = 'EACCES'; throw error; }]) {
      let refused = false;
      try { noProjectMcpOverride('C:\\synthetic\\project', metadata); }
      catch (error) { refused = error.message === 'project_override_refused'; }
      if (!refused) throw new Error('invalid_verification');
      checks++;
    }
    // N-broker-d-01: a listed code (this self-test's own failure code) is reported as itself; an unlisted message, and a
    // thrown value that is not an Error, read diagnostic_unavailable.
    if (reportableReason(new Error('invalid_verification')) !== 'invalid_verification') throw new Error('invalid_verification');
    checks++;
    // Built without an Error literal, so tests/Test-AgentBroker.ps1's source check does not take it for a thrown code.
    for (const unlisted of [Object.assign(new Error(), { message: 'SYNTHETIC_UNLISTED_DETAIL' }), new Error(), 'invalid_verification', null]) {
      if (reportableReason(unlisted) !== 'diagnostic_unavailable') throw new Error('invalid_verification');
      checks++;
    }
    report('self_test_passed',{ checks, publicFilesOpened: false, cliLaunched: false });
    process.exit(0);
  }
  if (process.platform !== 'win32' || !['--apply', '--apply-http', '--verify-http'].includes(mode) || extra.length) throw new Error('invalid_arguments');
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
  if (verifyHttp) {
    if (!probeEntryMatches(before)) throw new Error('probe_entry_refused');
    // The named get command merges scopes. Refuse any project MCP file in the cwd
    // ancestry by metadata, rather than reading it or risking a project override.
    noProjectMcpOverride(project);
  } else if (before.status === 'ok' && (before.userEntryFound !== false || before.localEntryFound !== false))
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
  const runCli = argv => new Promise((complete, reject) => {
    const child = spawn(cli, argv, { cwd: project, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    child.stdout.setEncoding('utf8'); child.stderr.setEncoding('utf8');
    let stdout = '', stderr = '', capturedBytes = 0, oversized = false, timedOut = false;
    const collect = kind => chunk => {
      if (oversized || timedOut) return;
      if (capturedBytes + Buffer.byteLength(chunk) > 128 * 1024) {
        oversized = true; child.kill(); return;
      }
      capturedBytes += Buffer.byteLength(chunk);
      if (kind === 'stdout') stdout += chunk; else stderr += chunk;
    };
    child.stdout.on('data', collect('stdout')); child.stderr.on('data', collect('stderr'));
    const timer = setTimeout(() => { timedOut = true; child.kill(); }, 20000);
    child.once('error', () => { clearTimeout(timer); reject(new Error('cli_start_failed')); });
    child.once('close', exitCode => { clearTimeout(timer); complete({ exitCode, stdout, stderr, oversized, timedOut }); });
  });
  let result = await runCli(verifyHttp ? verificationArguments() : argumentsFor(useHttp, worker, data));
  // Claude's add handler refuses an allowlist miss before it writes anything, so the one localhost add cannot replace an
  // entry; an entry that exists by then makes it fail "already exists", which is final.
  const retriedWithLocalhost = !verifyHttp && retriesWithLocalhost(useHttp, loopbackEndpoint, result);
  if (retriedWithLocalhost) result = await runCli(argumentsFor(true, worker, data, localhostEndpoint));
  // A failed/timed-out CLI can still have written the entry before failing. Inspect only
  // the selected config entry afterward, and never retry or overwrite beyond the one localhost add.
  let after;
  try { after = inspect(worker, data, project); } catch { after = { status: 'unavailable' }; }
  if (verifyHttp) {
    const fields = connectionFields(result.stdout), confirmed = connectionConfirmed(result, after);
    report('completed', { publicVersion: '2.1.292', transport: 'http', operation: 'verify_connection', exitCode: result.exitCode,
      reason: result.timedOut ? 'timed_out' : result.oversized ? 'cli_output_limit' : result.exitCode !== 0
        ? classification(result) : confirmed ? 'connection_confirmed' : 'connection_not_confirmed',
      managedPolicyReason: policyReason(result), registrationConfirmed: probeEntryMatches(after),
      ...fields, connectionConfirmed: confirmed });
    if (!confirmed) process.exitCode = 1;
  } else {
    const registrationConfirmed = after.status === 'ok' && after.userEntryFound === true &&
      (useHttp ? after.userType === 'http' && after.userUrlMatches === true
        : after.userType === 'stdio' && after.userCommandMatches === true && after.userArgumentsMatch === true);
    report('completed', { publicVersion: '2.1.292', transport: useHttp ? 'http' : 'stdio', exitCode: result.exitCode,
      reason: result.timedOut ? 'timed_out' : result.oversized ? 'cli_output_limit' : classification(result),
      managedPolicyReason: policyReason(result), retriedWithLocalhost, registrationConfirmed });
    if (!registrationConfirmed) process.exitCode = 1;
  }
} catch (error) {
  // Only fixed local error codes are permitted; never print vendor stdout/stderr or config.
  report('refused', { reason: reportableReason(error) });
  process.exitCode = 1;
}
