#requires -Version 7
<#
.SYNOPSIS
    Compare the production status client with a bounded buffered read if it fails.
.DESCRIPTION
    Opens only the current user's AgentBroker management pipe and sends "status".
    Both paths validate the server's user, protected pipe ACL and optional exact PID
    before sending. No settings, native credentials, MCP input or agent messages are
    read or changed. Returned names, addresses, IDs and payloads are never printed.
    CompileOnly compiles the three current source files without opening the pipe.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-ControlStatus.ps1 -CompileOnly
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-ControlStatus.ps1 -WorkerProcessId 51736
#>
[CmdletBinding()]
param(
    [ValidateRange(2, 2147483647)][int]$WorkerProcessId,
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This diagnostic requires Windows.' }

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sources = @(
    (Join-Path $repo 'tools\AgentBroker.Worker\ControlProtocol.cs'),
    (Join-Path $repo 'tools\AgentBroker.Worker\ControlPipeSecurity.cs'),
    (Join-Path $repo 'modules\AgentBroker\BrokerControlClient.cs')
)

# Add-Type accepts one source string. Change only file-scoped namespace syntax to
# equivalent blocks, retaining each source's usings, types and method bodies.
$parts = [Collections.Generic.List[string]]::new()
$parts.Add(@'
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
'@)
foreach ($path in $sources) {
    $source = [IO.File]::ReadAllText($path)
    $match = [regex]::Match($source, '(?m)^namespace ([A-Za-z0-9_.]+);\s*$')
    if (-not $match.Success -or [regex]::Matches($source, '(?m)^namespace ').Count -ne 1) {
        throw 'A diagnostic source no longer has the expected file-scoped namespace.'
    }
    $preamble = [regex]::Replace($source.Substring(0, $match.Index), '(?m)^using ', 'using global::')
    $parts.Add('namespace ' + $match.Groups[1].Value + " {`n" +
        $preamble + $source.Substring($match.Index + $match.Length) + "`n}")
}
$parts.Add(@'
namespace DesktopAICompanion.AgentBroker {
using System.Diagnostics;
using System.Text.Json;
using global::AgentBroker.Protocol;

public sealed class ControlStatusDiagnosticResult {
    public string Path { get; set; } = "";
    public bool Success { get; set; }
    public string Code { get; set; } = "failed";
    public long ElapsedMs { get; set; }
    // Production SendAsync does not expose bytes read; its success value is the
    // re-encoded frame length. Buffered reads measure the received frame directly.
    public int FrameBytes { get; set; } = -1;
    public bool FrameBytesMeasured { get; set; }
    public int AdapterCount { get; set; }
    public int SessionCount { get; set; }
    public int PairedPeerCount { get; set; }
    public int DiscoveredPeerCount { get; set; }
}

public static class ControlStatusDiagnostic {
    private static string SafeCode(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value ?? "", "^[a-zA-Z0-9_.-]{1,80}$")
            ? value! : "unclassified_failure";

    private static string FailureCode(Exception error) => error switch {
        OperationCanceledException => "timeout",
        UnauthorizedAccessException => "security_refused",
        JsonException => "invalid_response_json",
        IOException => SafeCode(error.Message),
        _ => error.GetType().Name
    };

    private static void Summarize(ControlStatusDiagnosticResult result, ControlResponse response) {
        result.Success = response.Success && response.Snapshot is not null;
        result.Code = SafeCode(response.Code);
        if (response.Success && response.Snapshot is null) result.Code = "status_snapshot_missing";
        if (response.Snapshot is not { } snapshot) return;
        result.SessionCount = snapshot.Sessions.Length;
        result.PairedPeerCount = snapshot.Paired.Length;
        result.DiscoveredPeerCount = snapshot.Discovered.Length;
        result.AdapterCount = snapshot.Sessions.Select(session => session.Adapter)
            .Concat(snapshot.AdapterErrors.Keys).Distinct(StringComparer.Ordinal).Count();
    }

    private static async Task<ControlStatusDiagnosticResult> ProductionAsync(string data, int? pid) {
        var result = new ControlStatusDiagnosticResult { Path = "production" };
        var elapsed = Stopwatch.StartNew();
        try {
            var response = await new BrokerControlClient().SendAsync(data, new ControlRequest("status"),
                CancellationToken.None, pid).ConfigureAwait(false);
            result.FrameBytes = JsonSerializer.SerializeToUtf8Bytes(response, ControlProtocol.Json).Length;
            Summarize(result, response);
        } catch (Exception error) { result.Code = FailureCode(error); }
        finally { result.ElapsedMs = elapsed.ElapsedMilliseconds; }
        return result;
    }

    private static async Task<ControlStatusDiagnosticResult> BufferedAsync(string data, int? pid) {
        var result = new ControlStatusDiagnosticResult { Path = "buffered", FrameBytes = 0, FrameBytesMeasured = true };
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var body = new MemoryStream();
        try {
            await using var pipe = ControlPipeSecurity.CreateClient(ControlProtocol.PipeName(data));
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            ControlPipeSecurity.ValidateConnectedServer(pipe, pid);
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new ControlRequest("status"), ControlProtocol.Json);
            await pipe.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            byte[] buffer = new byte[8192];
            try {
                while (true) {
                    int read = await pipe.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (read == 0) throw new IOException("control_eof");
                    int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                    int count = newline < 0 ? read : newline;
                    if (body.Length + count > ControlProtocol.MaximumFrameBytes)
                        throw new IOException("control_frame_too_large");
                    body.Write(buffer, 0, count);
                    result.FrameBytes = checked((int)body.Length);
                    if (newline < 0) continue;
                    if (newline != read - 1) throw new IOException("control_extra_frame");
                    if (body.Length == 0) throw new IOException("control_invalid_response");
                    var response = JsonSerializer.Deserialize<ControlResponse>(body.ToArray(), ControlProtocol.Json)
                        ?? throw new IOException("control_invalid_response");
                    Summarize(result, response);
                    break;
                }
            } finally { Array.Clear(buffer); }
        } catch (Exception error) { result.Code = FailureCode(error); }
        finally { result.ElapsedMs = elapsed.ElapsedMilliseconds; }
        return result;
    }

    public static async Task<ControlStatusDiagnosticResult[]> RunAsync(string data, int? pid) {
        var production = await ProductionAsync(data, pid).ConfigureAwait(false);
        if (production.Success) return new[] { production };
        return new[] { production, await BufferedAsync(data, pid).ConfigureAwait(false) };
    }
}
}
'@)
Add-Type -TypeDefinition ([string]::Join("`n", $parts)) -CompilerOptions '/nullable:enable'
Write-Host "PowerShellVersion=$($PSVersionTable.PSVersion)"
if ($CompileOnly) {
    Write-Host 'SourceCompiled=True'
    Write-Host 'PipeOpened=False'
    return
}

$data = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DesktopAICompanion\modules\agentbroker'))
$requiredPid = if ($PSBoundParameters.ContainsKey('WorkerProcessId')) { [Nullable[int]]$WorkerProcessId } else { $null }
$results = [DesktopAICompanion.AgentBroker.ControlStatusDiagnostic]::RunAsync($data, $requiredPid).GetAwaiter().GetResult()
foreach ($result in $results) {
    Write-Host ("Path={0} Success={1} Code={2} ElapsedMs={3} FrameBytes={4} FrameBytesMeasured={5} AdapterCount={6} SessionCount={7} PairedPeerCount={8} DiscoveredPeerCount={9}" -f
        $result.Path, $result.Success, $result.Code, $result.ElapsedMs, $result.FrameBytes, $result.FrameBytesMeasured,
        $result.AdapterCount, $result.SessionCount, $result.PairedPeerCount, $result.DiscoveredPeerCount)
}
if (-not $results[0].Success) { exit 1 }
