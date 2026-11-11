#requires -Version 7
<#
.SYNOPSIS
Run the actual collaboration helper child process against an owned ephemeral loopback fixture.
#>
[CmdletBinding()]
param(
    [ValidateSet('all','success-empty','success-single','success-rich','success-string','tool-error','rpc-error','http-error','transport-truncated','preflight-json','preflight-port','preflight-native','lookup-wait')]
    [string]$Case='all',
    [string]$HelperPath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\agentbroker\Invoke-AgentBrokerCollaborationMcp.ps1')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$checks=0
function Check([bool]$value,[string]$label) { if (-not $value) { throw ('Test-AgentBrokerCollaborationMcpProcess.ps1: '+$label) }; $script:checks++; Write-Output ('PASS: '+$label) }
function Start-Child([string]$File,[string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false,$true)
    $start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false,$true)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    if (-not $process.Start()) { throw 'Owned child process did not start.' }
    return $process
}
function Startup-Failure([Diagnostics.Process]$Process,[string]$CaseName) {
    $nativeExit='running'; $diagnostic='not-read-running'
    if ($Process.HasExited) {
        $nativeExit=[string]$Process.ExitCode; $diagnostic='read-deadline'
        try {
            $buffer=[char[]]::new(256)
            $read=$Process.StandardError.ReadBlockAsync($buffer,0,$buffer.Length)
            if ($read.Wait(1000)) {
                $sample=[string]::new($buffer,0,$read.GetAwaiter().GetResult())
                if ($sample -cmatch '\AFIXTURE_READY_PUBLISH_FAILED (EPERM|EACCES|EBUSY|OTHER)(?:\r?\n|\z)') {
                    $diagnostic='FIXTURE_READY_PUBLISH_FAILED '+$Matches[1]
                } elseif ($sample.Length -eq 0) { $diagnostic='empty' }
                else { $diagnostic='unclassified' }
            }
        } catch { $diagnostic='read-failed' }
    }
    return ('Fixture failed to start: '+$CaseName+'; exit='+$nativeExit+'; stderr='+$diagnostic)
}
$pwsh=@(Get-Command pwsh.exe -CommandType Application -ErrorAction Stop)[0].Source
$node=@(Get-Command node.exe -CommandType Application -ErrorAction Stop)[0].Source
$fixture=Join-Path $PSScriptRoot 'agentbroker-collaboration-process-fixture.mjs'
$HelperPath=[IO.Path]::GetFullPath($HelperPath)
$helperRoot=[IO.Path]::GetFullPath((Join-Path (Split-Path $HelperPath -Parent) '..\..'))
$ownedRoot=[IO.Path]::GetFullPath((Join-Path $helperRoot ('artifacts\collaboration-process\'+[Guid]::NewGuid().ToString('N'))))
if (-not $ownedRoot.StartsWith($helperRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Process fixture escaped helper workspace.' }
[IO.Directory]::CreateDirectory($ownedRoot) | Out-Null
$encoding=[Text.UTF8Encoding]::new($false)
$cases=@('success-empty','success-single','success-rich','success-string','tool-error','rpc-error','http-error','transport-truncated','preflight-json','preflight-port','preflight-native','lookup-wait')
if ($Case -ne 'all') { $cases=@($Case) }
try {
    foreach ($name in $cases) {
        $server=$null; $child=$null
        $ready=Join-Path $ownedRoot ($name+'-ready.json'); $requests=Join-Path $ownedRoot ($name+'-requests.jsonl')
        $requestPath=Join-Path $ownedRoot ($name+'-request.json')
        try {
            $server=Start-Child $node @($fixture,$ready,$requests,$name)
            $until=[DateTime]::UtcNow.AddSeconds(10)
            while (-not [IO.File]::Exists($ready) -and -not $server.HasExited -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 25 }
            if (-not [IO.File]::Exists($ready)) { throw (Startup-Failure $server $name) }
            $state=[IO.File]::ReadAllText($ready) | ConvertFrom-Json -AsHashtable
            Check ($state.pid -eq $server.Id -and $state.port -gt 0 -and $state.port -le 65535 -and $state.port -ne 7432) ('PROCESS_OWNED_ENDPOINT '+$name+': child binds its own ephemeral loopback port')
            $arguments=@{limit=16}; $tool='list_sessions'
            if ($name -eq 'lookup-wait') { $tool='wait_for_reply'; $arguments=@{messageId='44444444-4444-4444-8444-444444444444';timeoutSeconds=0} }
            if ($name -eq 'preflight-native') { $tool='read_inbox'; $arguments=@{session=@{brokerId='11111111-1111-4111-8111-111111111111';sessionId='codex:22222222-2222-4222-8222-222222222222';incarnation='33333333-3333-4333-8333-333333333333'}} }
            $request=@{jsonrpc='2.0';id='process-fixture';method='tools/call';params=@{name=$tool;arguments=$arguments}}
            $json=ConvertTo-Json -InputObject $request -Depth 10 -Compress
            if ($name -eq 'preflight-json') { $json='{' }
            [IO.File]::WriteAllText($requestPath,$json,$encoding)
            $port=[int]$state.port; if ($name -eq 'preflight-port') { $port=0 }
            $child=Start-Child $pwsh @('-NoProfile','-File',$HelperPath,'-RequestFile',$requestPath,'-LoopbackPort',[string]$port,'-Execute')
            $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
            if (-not $child.WaitForExit(25000)) { throw ('Helper child exceeded its owned deadline: '+$name) }
            $text=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
            $lines=@($text -split '\r?\n' | Where-Object { $_.Length -gt 0 })
            Check ($lines.Count -eq 1 -and [string]::IsNullOrWhiteSpace($errors)) ('PROCESS_ONE_JSON '+$name+': stdout contains exactly one result and stderr is empty')
            $result=$lines[0] | ConvertFrom-Json -AsHashtable -NoEnumerate
            Check ($result -is [Collections.IDictionary]) ('PROCESS_OBJECT '+$name+': result is one JSON object')
            $expectedExit=switch ($name) { 'tool-error' {10} 'rpc-error' {11} 'http-error' {12} 'transport-truncated' {69} {$_ -like 'preflight-*'} {2} default {0} }
            Check ($child.ExitCode -eq $expectedExit) ('PROCESS_EXIT '+$name+': main exits '+$expectedExit+' (actual '+$child.ExitCode+')')
            $received=@(); if ([IO.File]::Exists($requests)) { $received=@([IO.File]::ReadAllLines($requests) | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable }) }
            $expectedPosts=1; if ($name -like 'preflight-*') {$expectedPosts=0}; if ($name -eq 'lookup-wait') {$expectedPosts=2}
            Check ($received.Count -eq $expectedPosts) ('PROCESS_NO_RETRY '+$name+': exact owned request count is '+$expectedPosts)
            if ($expectedPosts -gt 0) { Check ($received[-1].raw -ceq $json) ('PROCESS_REQUEST_BYTES '+$name+': final request body preserves the source JSON') }
            switch ($name) {
                'success-empty' { Check ($result.Status -ceq 'ok' -and $result.Result -is [array] -and $result.Result.Count -eq 0) 'PROCESS_EMPTY_ARRAY: [] survives actual main output' }
                'success-single' { Check ($result.Status -ceq 'ok' -and $result.Result -is [array] -and $result.Result.Count -eq 1 -and $result.Result[0] -ceq 'one') 'PROCESS_SINGLE_ARRAY: [x] stays an array through main' }
                'success-string' { Check ($result.Status -ceq 'ok' -and $result.Result -is [string] -and $result.Result -ceq '') 'PROCESS_EMPTY_STRING: empty string survives actual main output' }
                'success-rich' {
                    $richMatches=$result.Status -ceq 'ok' -and $result.Result.text -ceq "line one`nD:\synthetic\path`n猫 🐑" -and $result.Result.empty -ceq '' -and $result.Result.items.Count -eq 0
                    if (-not $richMatches) { Write-Output ('PROCESS_RICH_OBSERVATION '+(ConvertTo-Json -InputObject $result -Depth 10 -Compress)) }
                    Check $richMatches 'PROCESS_SSE_ROUNDTRIP: SSE preserves real newlines, literal backslashes, Unicode and empty fields'
                }
                'tool-error' { Check ($result.Status -ceq 'tool_error' -and $result.DispatchState -ceq 'confirmed' -and $result.Result -ceq 'session_unavailable') 'PROCESS_TOOL_ERROR: MCP isError reaches the main exit contract' }
                'rpc-error' { Check ($result.Status -ceq 'mcp_error' -and $result.ErrorCode -eq -32601 -and $text -notmatch 'DO_NOT_EXPORT') 'PROCESS_RPC_ERROR: only the RPC code is exported' }
                'http-error' { Check ($result.Status -ceq 'http_error' -and $result.HttpStatus -eq 503 -and $result.DispatchState -ceq 'unknown' -and $text -notmatch 'DO_NOT_EXPORT') 'PROCESS_HTTP_ERROR: HTTP failure keeps uncertainty and omits raw body' }
                'transport-truncated' { Check ($result.Status -ceq 'transport_error' -and $result.DispatchState -ceq 'unknown') 'PROCESS_TRANSPORT_UNKNOWN: truncated postdispatch response never claims safe non-dispatch' }
                'lookup-wait' { Check ($result.Status -ceq 'ok' -and $result.Result.Count -eq 0 -and $received[0].tool -ceq 'get_message_receipt' -and $received[1].tool -ceq 'wait_for_reply') 'PROCESS_LOOKUP_PORT: guard lookup and main request share the selected owned endpoint' }
                default { Check ($result.Status -ceq 'refused' -and $result.DispatchState -ceq 'not_dispatched') ('PROCESS_PREFLIGHT '+$name+': invalid input performs no HTTP') }
            }
        }
        finally {
            foreach ($process in @($child,$server)) {
                if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }; if (-not $process.HasExited) { throw 'Owned child survived cleanup.' }; $process.Dispose() }
            }
        }
    }
}
finally {
    if (-not $ownedRoot.StartsWith($helperRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refused fixture cleanup outside owned workspace.' }
    if (Test-Path -LiteralPath $ownedRoot) { Remove-Item -LiteralPath $ownedRoot -Recurse -Force }
    if (Test-Path -LiteralPath $ownedRoot) { throw 'Owned process fixture files survived cleanup.' }
}
Write-Output ('PASS: '+$checks+' process collaboration MCP checks; owned server, helper children and fixture files cleaned.')
