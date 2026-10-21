#requires -Version 7
<#
.SYNOPSIS
    Read-only installed AgentBroker HTTP health and optional passive worker resource observation.
.DESCRIPTION
    Sends no agent input or MCP tools/call. Opens no settings, identity, inbox, native credential,
    or client configuration file. A bounded resource sample is an observation, not proof of no leaks.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-LocalAgentBrokerHealth.ps1
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-LocalAgentBrokerHealth.ps1 -WorkerProcessId <exact PID> -SampleSeconds 30
#>
[CmdletBinding()]
param(
    [ValidateRange(0, 2147483647)][int]$WorkerProcessId = 0,
    [ValidateRange(0, 60)][int]$SampleSeconds = 0,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-PublicInfo([string]$Json) {
    $document = $null
    try {
        $document = [Text.Json.JsonDocument]::Parse($Json, [Text.Json.JsonDocumentOptions]@{ MaxDepth = 16 })
        $root = $document.RootElement
        $brokerId = [guid]::Empty
        $endpoint = $null
        $address = $null
        $name = $root.GetProperty('name').GetString()
        $seen = [DateTimeOffset]::MinValue
        return [guid]::TryParse($root.GetProperty('brokerId').GetString(), [ref]$brokerId) -and
            $brokerId -ne [guid]::Empty -and $name.Length -ge 1 -and $name.Length -le 80 -and
            -not [string]::IsNullOrWhiteSpace($name) -and $name.ToCharArray().Where({ [char]::IsControl($_) }).Count -eq 0 -and
            $root.GetProperty('certificateSha256').GetString() -match '^[a-fA-F0-9]{64}$' -and
            [uri]::TryCreate($root.GetProperty('endpoint').GetString(), [UriKind]::Absolute, [ref]$endpoint) -and
            $endpoint.Scheme -eq 'https' -and [Net.IPAddress]::TryParse($endpoint.Host, [ref]$address) -and
            $address.Equals([Net.IPAddress]::Loopback) -and $endpoint.Port -eq 7433 -and $endpoint.AbsolutePath -eq '/' -and
            $endpoint.UserInfo.Length -eq 0 -and $endpoint.Query.Length -eq 0 -and $endpoint.Fragment.Length -eq 0 -and
            [DateTimeOffset]::TryParse($root.GetProperty('seenAt').GetString(), [ref]$seen)
    }
    catch { return $false }
    finally { if ($document) { $document.Dispose() } }
}

if ($SelfTest) {
    $sample = @{ brokerId = [guid]::NewGuid().ToString(); name = 'Synthetic broker';
        endpoint = 'https://127.0.0.1:7433/'; certificateSha256 = ('A' * 64); seenAt = [DateTimeOffset]::UtcNow.ToString('O') }
    if (-not (Test-PublicInfo ($sample | ConvertTo-Json -Compress))) { throw 'Valid public metadata was refused.' }
    $checks = 1
    foreach ($case in @(@('brokerId', [guid]::Empty.ToString()), @('certificateSha256', 'invalid'),
                        @('endpoint', 'http://127.0.0.1:7433/'), @('endpoint', 'https://example.invalid:7433/'),
                        @('endpoint', 'https://user@127.0.0.1:7433/'), @('endpoint', 'https://127.0.0.1:7433/?query'),
                        @('endpoint', 'https://127.0.0.1:7433/unexpected'), @('endpoint', 'https://127.0.0.1:7433/#fragment'),
                        @('endpoint', 'https://127.0.0.1:7432/'), @('endpoint', 'https://127.0.0.2:7433/'),
                        @('name', "unsafe`nname"), @('seenAt', 'invalid'))) {
        $edited = $sample.Clone(); $edited[$case[0]] = $case[1]
        if (Test-PublicInfo ($edited | ConvertTo-Json -Compress)) { throw 'Invalid public metadata was accepted.' }
        $checks++
    }
    foreach ($invalid in @('{}', '{')) {
        if (Test-PublicInfo $invalid) { throw 'Malformed public metadata was accepted.' }
        $checks++
    }
    Write-Host "PublicMetadataChecks=$checks"
    Write-Host 'HttpRequestsSent=False'
    Write-Host 'ProcessesInspected=False'
    return
}

if (-not $IsWindows) { throw 'Installed worker verification supports Windows only.' }
if ($SampleSeconds -gt 0 -and $WorkerProcessId -le 1) { throw 'A passive sample requires the exact installed worker PID.' }
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false; $handler.AllowAutoRedirect = $false; $handler.UseCookies = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
$worker = $null

function Invoke-HealthGet([string]$Path, [string]$Header = '', [string]$Value = '', [bool]$ReadBody = $false) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, ('http://127.0.0.1:7432' + $Path))
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
    $response = $null
    try {
        if ($Header) { [void]$request.Headers.TryAddWithoutValidation($Header, $Value) }
        $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
        $json = ''
        if ($ReadBody -and [int]$response.StatusCode -eq 200) {
            $bodyStream = $response.Content.ReadAsStreamAsync($deadline.Token).GetAwaiter().GetResult()
            $body = [IO.MemoryStream]::new()
            try {
                $buffer = [byte[]]::new(4096)
                while (($read = $bodyStream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                    if ($body.Length + $read -gt 32768) { throw 'Public health metadata exceeded its bound.' }
                    $body.Write($buffer, 0, $read)
                }
                $json = [Text.UTF8Encoding]::new($false, $true).GetString($body.ToArray())
            } finally { $bodyStream.Dispose(); $body.Dispose() }
        }
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $json }
    }
    finally {
        if ($response) { $response.Dispose() }
        $deadline.Dispose(); $request.Dispose()
    }
}

function Get-ResourceSample([Diagnostics.Process]$Process, [long]$Created) {
    $Process.Refresh()
    if ($Process.HasExited -or $Process.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $Created) { throw 'Worker process identity changed.' }
    return [pscustomobject]@{ CpuMs = $Process.TotalProcessorTime.TotalMilliseconds; Handles = $Process.HandleCount;
        PrivateBytes = $Process.PrivateMemorySize64; WorkingSetBytes = $Process.WorkingSet64; Threads = $Process.Threads.Count }
}

try {
    if ($WorkerProcessId -gt 0) {
        $worker = [Diagnostics.Process]::GetProcessById($WorkerProcessId)
        $retainedHandle = $worker.SafeHandle
        $expectedPath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Desktop AI Companion\modules\agentbroker\worker\AgentBroker.Worker.exe'))
        $actualPath = [IO.Path]::GetFullPath($worker.MainModule.FileName)
        if (-not $actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'The PID does not match the installed public worker path.' }
        $created = $worker.StartTime.ToUniversalTime().ToFileTimeUtc()
        Write-Host 'WorkerPathVerified=True'
    }
    $info = Invoke-HealthGet '/broker/info' '' '' $true
    $infoValid = $info.Status -eq 200 -and (Test-PublicInfo $info.Body)
    $federation = (Invoke-HealthGet '/federation/directory').Status -eq 403
    $origin = (Invoke-HealthGet '/broker/info' 'Origin' 'https://untrusted.invalid').Status -eq 403
    $hostAlias = (Invoke-HealthGet '/broker/info' 'Host' 'localhost').Status -eq 403
    Write-Host "PublicInfoValid=$infoValid"
    Write-Host "UnpairedDirectoryRefused=$federation"
    Write-Host "BrowserOriginRefused=$origin"
    Write-Host "HostAliasRefused=$hostAlias"
    if (-not ($infoValid -and $federation -and $origin -and $hostAlias)) { throw 'A read-only HTTP health check failed.' }
    if ($worker) {
        $before = Get-ResourceSample $worker $created
        $timer = [Diagnostics.Stopwatch]::StartNew()
        if ($SampleSeconds -gt 0) { Start-Sleep -Seconds $SampleSeconds }
        $after = Get-ResourceSample $worker $created
        Write-Host 'WorkerCreationTimeStable=True'
        Write-Host "SampleMilliseconds=$($timer.ElapsedMilliseconds)"
        Write-Host "CpuMillisecondsDelta=$([math]::Round($after.CpuMs - $before.CpuMs, 3))"
        Write-Host "HandleDelta=$($after.Handles - $before.Handles)"
        Write-Host "PrivateBytesDelta=$($after.PrivateBytes - $before.PrivateBytes)"
        Write-Host "WorkingSetBytesDelta=$($after.WorkingSetBytes - $before.WorkingSetBytes)"
        Write-Host "ThreadDelta=$($after.Threads - $before.Threads)"
        Write-Host 'ResourceObservationOnly=True'
        Write-Host 'LeakFreedomProven=False'
    }
    Write-Host 'ReadOnlyHealthPassed=True'
}
catch {
    Write-Host 'ReadOnlyHealthPassed=False'
    throw 'The bounded read-only health audit failed. No process was stopped and no configuration was changed.'
}
finally {
    if ($worker) { $worker.Dispose() }
    $client.Dispose()
}
