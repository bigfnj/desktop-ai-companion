#requires -Version 7
<#
.SYNOPSIS
Validate a generic register_agent request; -Execute starts one bounded hidden lease worker.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Start-AgentBrokerCollaborationLease.ps1 -RequestFile artifacts/collaboration/register.json -Execute
.DESCRIPTION
Temporary development support. Renews only the same generic inbox, every 20 seconds,
for at most 15 minutes. This does
not read an inbox, send input, wake a native session, or restart itself. Health receipts
contain fixed codes and counts only. The default validates without network or writes.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RequestFile, [ValidateRange(20,900)][int]$DurationSeconds=900,
    [switch]$Execute, [switch]$Worker, [string]$LeaseDirectory, [string]$ExpectedRequestSha256)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$helper=Join-Path $PSScriptRoot 'Invoke-AgentBrokerCollaborationMcp.ps1'
$helperHash='AFDCBB671CA0F0135F9984D0643B12BFED63CBD7C36C0855CE5085C69785CA80'
function Import-LeaseGuards {
    $partPath=[IO.Path]::GetPathRoot($helper)
    foreach($part in $helper.Substring($partPath.Length).Split('\')) {
        $partPath=Join-Path $partPath $part
        if (([IO.File]::GetAttributes($partPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'helper_path_refused' }
    }
    if ((Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash -cne $helperHash) { throw 'helper_changed' }
    $tokens=$null; $errors=$null; $ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw 'helper_parse_failed' }
    foreach($name in @('Refuse-Mcp','Convert-StrictMcpJson','Get-McpField','Confirm-GenericMcpAddress','Confirm-CollaborationRequest','Assert-McpRequestPath','Confirm-McpRequestComponents','Read-McpBytes')) {
        $definitions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
        if ($definitions.Count -ne 1) { throw 'helper_shape_changed' }
        $parameters=(@($definitions[0].Parameters|ForEach-Object {$_.Extent.Text}) -join ',')
        $body=$definitions[0].Body.Extent.Text
        Set-Item -LiteralPath ('Function:script:'+$name) -Value ([scriptblock]::Create(('param('+$parameters+")`n"+$body.Substring(1,$body.Length-2))))
    }
}
function Confirm-LeaseRegistration($Request) {
    $validated=Confirm-CollaborationRequest $Request
    if ($validated.Method -cne 'tools/call' -or $validated.Tool -cne 'register_agent') { throw 'registration_only' }
    $parameters=Get-McpField $Request 'params'; $args=Get-McpField $parameters 'arguments'
    foreach($key in $parameters.Keys) { if ($key -cnotin @('name','arguments')) { throw 'registration_shape' } }
    foreach($key in $args.Keys) { if ($key -cnotin @('clientId','incarnation','label','isPrivate')) { throw 'registration_shape' } }
    foreach($name in @('clientId','incarnation')) {
        $value=Get-McpField $args $name; $parsed=[guid]::Empty
        if ($value -isnot [string] -or -not [guid]::TryParseExact($value,'D',[ref]$parsed) -or $parsed -eq [guid]::Empty -or $value -cne $parsed.ToString('D')) { throw 'registration_identity' }
    }
    $label=Get-McpField $args 'label'; $private=Get-McpField $args 'isPrivate'
    if ($label -isnot [string] -or $label.Length -lt 1 -or $label.Length -gt 160 -or $label -match '[\x00-\x1f]' -or $private -isnot [bool]) { throw 'registration_shape' }
    return $args
}
function Read-LeaseRequest([string]$Path) {
    $full=Assert-McpRequestPath $Path $repo; Confirm-McpRequestComponents $full
    $stream=[IO.File]::Open($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { return ,(Read-McpBytes $stream 131072 ([Threading.CancellationToken]::None)) } finally { $stream.Dispose() }
}
function Confirm-LeaseReply([string]$Text,$Registration,[string]$ExpectedBrokerId) {
    if ($Text.Length -gt 524288) { return @{Code='reply_refused';BrokerId=$null} }
    try {
        $reply=Convert-StrictMcpJson $Text
        if ((Get-McpField $reply 'Status') -cne 'mcp_result') { return @{Code='renewal_failed';BrokerId=$null} }
        $result=Get-McpField $reply 'Result'
        if ($result.Contains('isError') -and $result['isError'] -ne $false) { throw 'tool_error' }
        $sessions=[Collections.Generic.List[object]]::new()
        if ($result.Contains('structuredContent')) { $sessions.Add($result['structuredContent']) }
        if ($result.Contains('content')) {
            $content=@($result['content']); if ($content.Count -ne 1 -or (Get-McpField $content[0] 'type') -cne 'text') { throw 'reply_shape' }
            $sessions.Add((Convert-StrictMcpJson (Get-McpField $content[0] 'text')))
        }
        if ($sessions.Count -eq 0) { throw 'reply_shape' }; $broker=$null
        foreach($session in $sessions) {
            $address=Get-McpField $session 'address'; Confirm-GenericMcpAddress $address
            $current=Get-McpField $address 'brokerId'; $parsed=[guid]::Parse($current)
            if ($current -cne $parsed.ToString('D') -or (Get-McpField $address 'sessionId') -cne ('mcp:'+$Registration.clientId) -or
                (Get-McpField $address 'incarnation') -cne $Registration.incarnation -or (Get-McpField $session 'adapter') -cne 'mcp' -or
                (Get-McpField $session 'isPrivate') -cne $Registration.isPrivate -or (Get-McpField $session 'label') -cne $Registration.label) { throw 'reply_identity' }
            if ($null -ne $broker -and $current -cne $broker) { throw 'reply_disagreement' }; $broker=$current
        }
        if ($ExpectedBrokerId -and $ExpectedBrokerId -cne $broker) { return @{Code='broker_changed';BrokerId=$null} }
        return @{Code='renewed';BrokerId=$broker}
    } catch { return @{Code='reply_refused';BrokerId=$null} }
}
function Invoke-LeaseCycle([int]$Seconds,[scriptblock]$Renew,[scriptblock]$Clock,[scriptblock]$Sleep,[scriptblock]$Record) {
    if ($Seconds -lt 20 -or $Seconds -gt 900) { throw 'duration_refused' }
    $attempts=0; $successes=0; $failures=0; $broker=$null; $start=& $Clock; $next=0.0
    while ($attempts -lt 45) {
        $elapsed=(& $Clock)-$start
        if ($elapsed -lt $next) { & $Sleep ([int][Math]::Ceiling(($next-$elapsed)*1000)); continue }
        if ($elapsed+15 -gt $Seconds) { break }
        $attempts++; $renewal=@{Code='renewal_failed';BrokerId=$null}
        try { $renewal=& $Renew $broker } catch { }
        $code='renewal_failed'; if ($renewal.Code -cin @('renewed','renewal_failed','reply_refused','broker_changed')) { $code=$renewal.Code }
        if ($code -ceq 'renewed') { $successes++; $broker=$renewal.BrokerId } else { $failures++ }
        & $Record ([ordered]@{Attempt=$attempts;Successes=$successes;Failures=$failures;Code=$code;ElapsedSeconds=[int]((& $Clock)-$start)})
        if ($code -ceq 'broker_changed') { break }; $next+=20
    }
    return [ordered]@{Status='completed';Attempts=$attempts;Successes=$successes;Failures=$failures}
}
function Write-LeaseReceipt([string]$Path,$Value) {
    $full=Assert-McpRequestPath $Path $repo; Confirm-McpRequestComponents ([IO.Path]::GetDirectoryName($full))
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Compress -Depth 8))
    if ($bytes.Length -gt 16384) { throw 'receipt_limit' }
    $file=[IO.File]::Open($full,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $file.Write($bytes,0,$bytes.Length) } finally { $file.Dispose() }
}
function Confirm-LeaseHostFacts([string]$Profile,[string]$UserName,[string]$UserSid,
    [string]$Edition,[string]$ProcessPath,[string]$PowerShellHome) {
    if ($UserSid -in @('S-1-5-18','S-1-5-19','S-1-5-20') -or $UserSid.StartsWith('S-1-5-80-', [StringComparison]::Ordinal)) { throw 'normal_user_required' }
    if ([string]::IsNullOrWhiteSpace($UserSid) -or -not [IO.Path]::IsPathFullyQualified($Profile) -or $Profile.StartsWith('\\')) { throw 'normal_user_required' }
    $profilePath=[IO.Path]::GetFullPath($Profile).TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($UserName) -or $UserName -ine [IO.Path]::GetFileName($profilePath) -or $Edition -cne 'Core') { throw 'normal_user_required' }
    if (-not [IO.Path]::IsPathFullyQualified($PowerShellHome) -or -not [IO.Path]::IsPathFullyQualified($ProcessPath)) { throw 'powershell_host_required' }
    $caller=[IO.Path]::GetFullPath((Join-Path $PowerShellHome 'pwsh.exe'))
    if (-not [string]::Equals([IO.Path]::GetFullPath($ProcessPath),$caller,[StringComparison]::OrdinalIgnoreCase)) { throw 'powershell_host_required' }
    return $caller
}
function Confirm-LeaseHost {
    # A checkout can live on any drive. Validate the Windows caller independently
    # of the checkout's parent directory, retaining the normal-user and pwsh rules.
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $caller=Confirm-LeaseHostFacts ([Environment]::GetFolderPath('UserProfile')) ([Environment]::UserName) `
            $identity.User.Value $PSVersionTable.PSEdition ([Environment]::ProcessPath) $PSHOME
    } finally { $identity.Dispose() }
    if (-not [IO.File]::Exists($caller)) { throw 'powershell_host_required' }
    return $caller
}
$snapshotLock=$null; $helperLock=$null; $workerReceiptStarted=$false; $stage='helper_validation_failed'
try {
    Import-LeaseGuards
    $stage='request_read_failed'; $bytes=Read-LeaseRequest $RequestFile
    $stage='registration_validation_failed'; $registration=Confirm-LeaseRegistration (Convert-StrictMcpJson ([Text.UTF8Encoding]::new($false,$true).GetString($bytes)))
    if (-not $Execute) { if ($Worker) { throw 'worker_requires_execute' }; [ordered]@{Status='validated_only';RenewalSeconds=20;DurationSeconds=$DurationSeconds;ProcessStarted=$false;HttpRequestSent=$false}|ConvertTo-Json -Compress; return }
    $stage='lease_execution_failed'; $caller=Confirm-LeaseHost
    if (-not $Worker) { $LeaseDirectory=Join-Path $repo ('artifacts\agentbroker-lease-'+[guid]::NewGuid().ToString('N')) }
    $snapshot=Assert-McpRequestPath (Join-Path $LeaseDirectory 'registration.json') $repo
    if ([IO.Path]::GetDirectoryName($LeaseDirectory) -ine (Join-Path $repo 'artifacts') -or [IO.Path]::GetFileName($LeaseDirectory) -cnotmatch '^agentbroker-lease-[a-f0-9]{32}$') { throw 'lease_directory_refused' }
    if (-not $Worker) {
        Confirm-McpRequestComponents ([IO.Path]::GetDirectoryName($LeaseDirectory))
        $null=New-Item -ItemType Directory -Path $LeaseDirectory -ErrorAction Stop
        Write-LeaseReceipt $snapshot (Convert-StrictMcpJson ([Text.UTF8Encoding]::new($false,$true).GetString($bytes)))
        $hash=(Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash
        $arguments=@('-NoLogo','-NoProfile','-NonInteractive','-File',$PSCommandPath,'-RequestFile',$snapshot,'-DurationSeconds',"$DurationSeconds",'-Execute','-Worker','-LeaseDirectory',$LeaseDirectory,'-ExpectedRequestSha256',$hash)
        foreach($argument in $arguments) { if ($argument -match '["\r\n]' -or $argument.EndsWith('\')) { throw 'argument_refused' } }
        $child=Start-Process -FilePath $caller -ArgumentList (($arguments|ForEach-Object {'"'+$_+'"'}) -join ' ') -WindowStyle Hidden -PassThru
        try { $receipt=[ordered]@{Status='started';Pid=$child.Id;CreationFileTime=$child.StartTime.ToUniversalTime().ToFileTimeUtc();DurationSeconds=$DurationSeconds;LeaseDirectory=$LeaseDirectory}; Write-LeaseReceipt (Join-Path $LeaseDirectory 'launch.json') $receipt; $receipt|ConvertTo-Json -Compress } finally { $child.Dispose() }
        return
    }
    if ([IO.Path]::GetFullPath($RequestFile) -ine $snapshot -or $ExpectedRequestSha256 -cnotmatch '^[A-F0-9]{64}$') { throw 'snapshot_refused' }
    $snapshotLock=[IO.File]::Open($snapshot,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    if ((Get-FileHash -InputStream $snapshotLock -Algorithm SHA256).Hash -cne $ExpectedRequestSha256) { throw 'snapshot_changed' }
    $helperLock=[IO.File]::Open($helper,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    if ((Get-FileHash -InputStream $helperLock -Algorithm SHA256).Hash -cne $helperHash) { throw 'helper_changed' }
    Write-LeaseReceipt (Join-Path $LeaseDirectory 'worker-started.json') ([ordered]@{Pid=$PID;CreationFileTime=[Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc();DurationSeconds=$DurationSeconds})
    $workerReceiptStarted=$true
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $summary=Invoke-LeaseCycle $DurationSeconds {
        param($broker)
        $text=@(& $helper -RequestFile $snapshot -Execute 2>$null)-join "`n"
        Confirm-LeaseReply $text $registration $broker
    } {$timer.Elapsed.TotalSeconds} {param($milliseconds) Start-Sleep -Milliseconds $milliseconds} {
        param($health) Write-LeaseReceipt (Join-Path $LeaseDirectory ('health-{0:D3}.json' -f $health.Attempt)) $health
    }
    Write-LeaseReceipt (Join-Path $LeaseDirectory 'completed.json') $summary
} catch {
    if ($workerReceiptStarted) { try { Write-LeaseReceipt (Join-Path $LeaseDirectory 'failed.json') ([ordered]@{Status='failed';Code='lease_failed'}) } catch { } }
    [ordered]@{Status='refused';Code=$stage;ProcessOrNetworkStateMayBePartial=[bool]$Execute}|ConvertTo-Json -Compress; exit 1
}
finally { if ($null -ne $snapshotLock) {$snapshotLock.Dispose()}; if ($null -ne $helperLock) {$helperLock.Dispose()} }
