// Read-only selected-entry diagnostic. Does not launch Claude, MCP, or a worker.
// Usage: node tools/agentbroker/Inspect-ClaudeMcpEntry.mjs <expected-worker.exe> <expected-data-directory> [<current-project-directory>]
// Append --http-url http://127.0.0.1:7432/mcp to additionally compare the selected HTTP endpoint.
// Prints fixed fields only. No configuration values, paths, other server names, or CLI output.
import { spawnSync } from 'node:child_process';
import { isAbsolute, join, resolve, relative, sep, parse } from 'node:path';
import { mkdirSync, mkdtempSync, lstatSync, readdirSync, unlinkSync, rmdirSync, writeFileSync } from 'node:fs';

const supplied = process.argv.slice(2);
let expectedHttpEndpoint = '';
const httpFlag = supplied.indexOf('--http-url');
if (httpFlag >= 0) {
  if (httpFlag !== supplied.length - 2 || supplied[httpFlag + 1] !== 'http://127.0.0.1:7432/mcp') {
    console.log(JSON.stringify({ status: 'invalid_arguments' })); process.exit(2);
  }
  expectedHttpEndpoint = supplied[httpFlag + 1]; supplied.splice(httpFlag, 2);
}
const compileOnly = supplied.length === 1 && supplied[0] === '--compile-check';
const selfTest = supplied.length === 1 && supplied[0] === '--self-test';
let [worker, data, project, ...extra] = supplied;
if (compileOnly || selfTest) { worker = 'C:\\synthetic worker\\AgentBroker.Worker.exe'; data = 'C:\\synthetic data'; project = 'C:\\synthetic project'; extra = []; }
if (process.platform !== 'win32' || extra.length || !worker || !data ||
    !isAbsolute(worker) || !isAbsolute(data) || project !== undefined && !isAbsolute(project)) {
  console.log(JSON.stringify({ status: 'invalid_arguments' }));
  process.exit(2);
}
if (!process.env.USERPROFILE || !isAbsolute(process.env.USERPROFILE) || !process.env.SystemRoot) {
  console.log(JSON.stringify({ status: 'profile_unavailable' }));
  process.exit(2);
}

// Windows PowerShell is used only as a host for framework JSON parsing and Win32
// file metadata. The opened file is pinned without write/delete sharing. OPEN_REPARSE_POINT
// prevents a leaf reparse point being followed before its attributes are checked.
// The fixed script never writes configuration or invokes any vendor executable.
const source = String.raw`
$ErrorActionPreference = 'Stop'
try {
Add-Type -AssemblyName System.Web.Extensions
Add-Type -ReferencedAssemblies 'System.Web.Extensions' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
public static class SelectedClaudeMcpEntry {
    const string Name = "desktop-ai-companion-agentbroker";
    const int MaxBytes = 4 * 1024 * 1024;
    [StructLayout(LayoutKind.Sequential)] struct FileIdentity {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity identity);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    static string Full(string path) {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\") || full.IndexOf(':', 2) >= 0) throw new InvalidDataException();
        return full;
    }
    static void UnlinkedParents(string path) {
        for (string current = Path.GetDirectoryName(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }
    static Dictionary<string, object> Object(object value) { return value as Dictionary<string, object>; }
    static object Property(Dictionary<string, object> value, string key) {
        object found; return value != null && value.TryGetValue(key, out found) ? found : null;
    }
    static Dictionary<string, object> Entry(Dictionary<string, object> scope) {
        return Object(Property(Object(Property(scope, "mcpServers")), Name));
    }
    static string Type(Dictionary<string, object> entry) {
        if (entry == null) return "missing";
        string type = Property(entry, "type") as string;
        if (type == "streamable-http") return "http";
        if (type == "stdio" || type == "http" || type == "sse") return type;
        // Claude also accepts stdio entries that omit the optional type discriminator.
        return type == null && Property(entry, "command") is string ? "stdio" : "other";
    }
    static bool CommandMatches(Dictionary<string, object> entry, string expected) {
        string command = Property(entry, "command") as string;
        return command != null && String.Equals(command, expected, StringComparison.OrdinalIgnoreCase);
    }
    static bool ArgsMatch(Dictionary<string, object> entry, string data) {
        var args = Property(entry, "args") as object[];
        return args != null && args.Length == 3 && (args[0] as string) == "--mcp-stdio" &&
            (args[1] as string) == "--data" && (args[2] as string) == data;
    }
    static bool UrlMatches(Dictionary<string, object> entry, string expected) {
        return !String.IsNullOrEmpty(expected) && Type(entry) == "http" && (Property(entry, "url") as string) == expected;
    }
    static bool HttpProbeSafe(Dictionary<string, object> entry, string expected) {
        // A health probe must not invoke headers helpers or consume configured auth/env.
        // Accept only the two-field definition emitted by the ordinary HTTP add command.
        return UrlMatches(entry, expected) && entry.Count == 2 && entry.ContainsKey("type") && entry.ContainsKey("url");
    }
    public static string Inspect(string path, string worker, string data, string project, string endpoint) {
        var result = new Dictionary<string, object>();
        result["status"] = "unavailable";
        try {
            path = Full(path); worker = Full(worker); data = Full(data);
            if (!String.IsNullOrEmpty(project)) project = Full(project);
            UnlinkedParents(path);
            using (var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero)) {
                if (handle.IsInvalid) {
                    int error = Marshal.GetLastWin32Error();
                    result["status"] = error == 2 || error == 3 ? "not_found" : "unreadable";
                    return new JavaScriptSerializer().Serialize(result);
                }
                FileIdentity identity;
                if (!GetFileInformationByHandle(handle, out identity) || GetFileType(handle) != 1 ||
                    identity.Links != 1 || (identity.Attributes & (0x10u | 0x400u)) != 0) {
                    result["status"] = "unsafe_file";
                    return new JavaScriptSerializer().Serialize(result);
                }
                var finalName = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, finalName, (uint)finalName.Capacity, 0);
                string actual = finalName.ToString();
                if (actual.StartsWith(@"\\?\")) actual = actual.Substring(4);
                if (length == 0 || length >= finalName.Capacity || !String.Equals(actual, path, StringComparison.OrdinalIgnoreCase)) {
                    result["status"] = "unsafe_file";
                    return new JavaScriptSerializer().Serialize(result);
                }
                UnlinkedParents(path);
                using (var file = new FileStream(handle, FileAccess.Read)) {
                    if (file.Length > MaxBytes) {
                        result["status"] = "too_large";
                        return new JavaScriptSerializer().Serialize(result);
                    }
                    string json;
                    using (var reader = new StreamReader(file, new UTF8Encoding(false, true), true, 4096, true)) {
                        var text = new StringBuilder();
                        var buffer = new char[4096];
                        int read;
                        while ((read = reader.Read(buffer, 0, buffer.Length)) != 0) {
                            if (text.Length + read > MaxBytes) {
                                result["status"] = "too_large";
                                return new JavaScriptSerializer().Serialize(result);
                            }
                            text.Append(buffer, 0, read);
                        }
                        json = text.ToString();
                    }
                    var serializer = new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 128 };
                    var config = Object(serializer.DeserializeObject(json));
                    if (config == null) throw new FormatException();
                    var user = Entry(config);
                    Dictionary<string, object> local = null;
                    bool matchingProjectFound = false;
                    if (!String.IsNullOrEmpty(project)) {
                        var projects = Object(Property(config, "projects"));
                        // Exact candidate lookups only, never enumerate unrelated project/server entries.
                        string trimmed = project.TrimEnd('\\', '/');
                        foreach (string candidate in new[] { project, trimmed, trimmed.Replace('\\', '/'), trimmed.Replace('/', '\\') }) {
                            var scope = Object(Property(projects, candidate));
                            if (scope != null) { matchingProjectFound = true; local = Entry(scope); break; }
                        }
                    }
                    result["status"] = "ok";
                    result["userEntryFound"] = user != null;
                    result["userType"] = Type(user);
                    result["userCommandMatches"] = CommandMatches(user, worker);
                    result["userArgumentsMatch"] = ArgsMatch(user, data);
                    result["userUrlMatches"] = UrlMatches(user, endpoint);
                    result["userHttpProbeSafe"] = HttpProbeSafe(user, endpoint);
                    result["matchingProjectInspected"] = !String.IsNullOrEmpty(project);
                    result["matchingProjectFound"] = matchingProjectFound;
                    result["localEntryFound"] = local != null;
                    result["localType"] = Type(local);
                    result["localCommandMatches"] = CommandMatches(local, worker);
                    result["localArgumentsMatch"] = ArgsMatch(local, data);
                    result["localUrlMatches"] = UrlMatches(local, endpoint);
                    result["localHttpProbeSafe"] = HttpProbeSafe(local, endpoint);
                }
            }
        } catch (InvalidDataException) { result.Clear(); result["status"] = "unsafe_file"; }
          catch (DecoderFallbackException) { result.Clear(); result["status"] = "invalid_json"; }
          catch (ArgumentException) { result.Clear(); result["status"] = "invalid_json"; }
          catch (FormatException) { result.Clear(); result["status"] = "invalid_json"; }
          catch (InvalidOperationException) { result.Clear(); result["status"] = "invalid_json"; }
          catch (Exception) { result.Clear(); result["status"] = "unreadable"; }
        return new JavaScriptSerializer().Serialize(result);
    }
}
'@
if ($env:AGENTBROKER_DIAGNOSTIC_COMPILE_ONLY -eq '1') { '{"status":"compile_passed"}' }
else { [SelectedClaudeMcpEntry]::Inspect($env:AGENTBROKER_DIAGNOSTIC_CONFIG, $env:AGENTBROKER_DIAGNOSTIC_WORKER,
    $env:AGENTBROKER_DIAGNOSTIC_DATA, $env:AGENTBROKER_DIAGNOSTIC_PROJECT, $env:AGENTBROKER_DIAGNOSTIC_HTTP) }
} catch { '{"status":"diagnostic_unavailable"}' }
`;

function unlinked(path) {
  let current = parse(path).root;
  for (const part of path.slice(current.length).split(sep).filter(Boolean)) {
    current = join(current, part);
    if (lstatSync(current).isSymbolicLink()) throw new Error('Linked diagnostic temporary parent.');
  }
}
const artifacts = resolve(import.meta.dirname, '..', '..', 'artifacts');
let temporary;
try {
  try { mkdirSync(artifacts); } catch (e) { if (e.code !== 'EEXIST') throw e; }
  unlinked(artifacts);
  temporary = mkdtempSync(join(artifacts, 'claude-entry-diagnostic-'));
} catch {
  console.log(JSON.stringify({ status: 'diagnostic_unavailable' }));
  process.exit(1);
}
const syntheticConfig = join(temporary, '.claude.json');
try {
  if (selfTest) writeFileSync(syntheticConfig, JSON.stringify({
    unrelated: { token: 'SYNTHETIC_DO_NOT_PRINT' },
    mcpServers: { 'desktop-ai-companion-agentbroker': expectedHttpEndpoint
      ? { type: 'http', url: expectedHttpEndpoint }
      : { type: 'stdio', command: worker, args: ['--mcp-stdio', '--data', data] } },
    projects: { [project.replaceAll('\\', '/')]: { mcpServers: { 'desktop-ai-companion-agentbroker': expectedHttpEndpoint
      ? { type: 'http', url: expectedHttpEndpoint, headersHelper: 'SYNTHETIC_NEVER_EXECUTE' }
      : { command: 'synthetic-other-command', args: [] } } } }
  }), { flag: 'wx' });
} catch {
  console.log(JSON.stringify({ status: 'diagnostic_unavailable' }));
  process.exit(1);
}
const powershell = join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const result = spawnSync(powershell, ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(source, 'utf16le').toString('base64')], {
  windowsHide: true, timeout: 20000, maxBuffer: 16 * 1024, encoding: 'utf8',
  env: { ...process.env,
    TEMP: temporary, TMP: temporary,
    AGENTBROKER_DIAGNOSTIC_COMPILE_ONLY: compileOnly ? '1' : '0',
    AGENTBROKER_DIAGNOSTIC_CONFIG: selfTest ? syntheticConfig : join(resolve(process.env.USERPROFILE), '.claude.json'),
    AGENTBROKER_DIAGNOSTIC_WORKER: resolve(worker), AGENTBROKER_DIAGNOSTIC_DATA: resolve(data),
    AGENTBROKER_DIAGNOSTIC_PROJECT: project ? resolve(project) : '', AGENTBROKER_DIAGNOSTIC_HTTP: expectedHttpEndpoint }
});

try {
  const parsed = JSON.parse(result.stdout.trim());
  const allowed = new Set(['status', 'userEntryFound', 'userType', 'userCommandMatches', 'userArgumentsMatch',
    'userUrlMatches', 'userHttpProbeSafe', 'matchingProjectInspected', 'matchingProjectFound', 'localEntryFound', 'localType',
    'localCommandMatches', 'localArgumentsMatch', 'localUrlMatches', 'localHttpProbeSafe']);
  const fixedStrings = new Set(['ok', 'unavailable', 'not_found', 'unreadable', 'unsafe_file', 'too_large',
    'invalid_json', 'diagnostic_unavailable', 'compile_passed', 'missing', 'stdio', 'http', 'sse', 'other']);
  if (result.error || result.status !== 0 || !parsed || Array.isArray(parsed) ||
      Object.entries(parsed).some(([key, value]) => !allowed.has(key) || typeof value !== 'boolean' && !fixedStrings.has(value)))
    throw new Error('Invalid fixed diagnostic response.');
  if (selfTest) {
    const selectedMatches = expectedHttpEndpoint
      ? parsed.userType === 'http' && parsed.userUrlMatches && parsed.userHttpProbeSafe && !parsed.userCommandMatches && !parsed.userArgumentsMatch
      : parsed.userType === 'stdio' && parsed.userCommandMatches && parsed.userArgumentsMatch && !parsed.userUrlMatches && !parsed.userHttpProbeSafe;
    const localProbeRefused = expectedHttpEndpoint
      ? parsed.localType === 'http' && parsed.localUrlMatches && !parsed.localHttpProbeSafe
      : !parsed.localCommandMatches && !parsed.localArgumentsMatch && !parsed.localUrlMatches && !parsed.localHttpProbeSafe;
    if (parsed.status !== 'ok' || !parsed.userEntryFound || !selectedMatches ||
        !parsed.matchingProjectFound || !parsed.localEntryFound || !localProbeRefused)
      throw new Error('Synthetic selected-entry assertions failed.');
    console.log(JSON.stringify({ status: 'self_test_passed', checks: 11 }));
  } else console.log(JSON.stringify(parsed));
} catch {
  // Never forward PowerShell error text; it could contain a path or a JSON fragment.
  console.log(JSON.stringify({ status: 'diagnostic_unavailable' }));
  process.exitCode = 1;
} finally {
  // Only this unique, repo-contained compiler fixture is removable. Check the entire
  // metadata plan before deleting anything, and retain unexpected protected/link output.
  const files = [], directories = [];
  function plan(directory) {
    const tail = relative(artifacts, directory);
    if (!tail.startsWith('claude-entry-diagnostic-') || tail.includes('..' + sep)) throw new Error('Unsafe cleanup root.');
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (/^(?:\.env(?:\..*)?|\.npmrc|privatekey.*|\.aws|\.ssh|\.kube|\.pypirc|\.cargo|\.docker|\.git|\.git-credentials|\.dockercfg|\.pip|\.config|\.mozilla|id_.*|credentials(?:\.toml)?)$/i.test(entry.name) ||
          /\.(?:env|pem|key|p12|pfx|keystore)$/i.test(entry.name)) throw new Error('Unexpected protected compiler output.');
      const child = join(directory, entry.name), info = lstatSync(child);
      if (info.isSymbolicLink() || !info.isDirectory() && info.nlink !== 1) throw new Error('Unexpected linked compiler output.');
      if (info.isDirectory()) plan(child);
      else if (info.isFile()) files.push(child);
      else throw new Error('Unexpected compiler output type.');
    }
    directories.push(directory);
  }
  try { unlinked(temporary); plan(temporary); for (const file of files) unlinkSync(file); for (const directory of directories) rmdirSync(directory); }
  catch { console.log(JSON.stringify({ status: 'temporary_cleanup_refused' })); process.exitCode = 1; }
}
