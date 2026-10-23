#requires -Version 5.1
<#
.SYNOPSIS
Read-only, development diagnostic. Default: bounded metadata-only candidate plan.
.DESCRIPTION
Supply the explicitly resolved normal user's APPDATA\Code\logs root. There is no default.
The root must match this normal user's ApplicationData location; projected/offline roots
are not substituted. Only session\windowN\exthost\openai.chatgpt\Codex.log is supported.
No recursive traversal, rotated logs, other channels, remote paths or output_logging paths.
Content is read ONLY with -Summarize and exact -SelectedLogPath relative selectors from
the metadata plan. Summaries contain fixed counters, never raw lines/errors/IDs/paths.
At most 3 sessions, 16 windows/files, 64 MiB/file, 512 KiB tail/file and 4096 lines/file.
Partial boundary lines are discarded. Missing debug entries do not prove connectivity.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Get-CodexTransportSummary.ps1 -LogRoot '<resolved-normal-user-Code-log-root>'
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Get-CodexTransportSummary.ps1 -LogRoot '<same-root>' -Summarize -SelectedLogPath '<exact-relative-selector>'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LogRoot,
    [switch]$Summarize,
    [string[]]$SelectedLogPath = @(),
    [ValidateRange(1, 3)][int]$SessionLimit = 3
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$contentMayHaveBeenRead = $false

function Stop-Transport([string]$Code) {
    $errorValue = New-Object IO.IOException('Transport diagnostic refused.')
    $errorValue.Data['TransportCode'] = $Code
    throw $errorValue
}
function Get-TransportSelector([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 160) { Stop-Transport 'selector_refused' }
    $relative = $Value.Replace('/', '\')
    $match = [regex]::Match($relative, '^(?<session>[0-9]{8}T[0-9]{6})\\(?<window>window[1-9][0-9]{0,5})\\exthost\\openai\.chatgpt\\Codex\.log$')
    $stamp = [datetime]::MinValue
    if (-not $match.Success -or -not [datetime]::TryParseExact($match.Groups['session'].Value, 'yyyyMMddTHHmmss',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$stamp)) { Stop-Transport 'selector_refused' }
    return [pscustomobject]@{ Relative = $relative; Session = $match.Groups['session'].Value; Window = $match.Groups['window'].Value }
}
function Assert-TransportScope([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $scope = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (($full -ne $scope -and -not $full.StartsWith($scope + '\', [StringComparison]::OrdinalIgnoreCase)) -or
        $full.StartsWith('\\') -or $full.Substring(3).Contains(':') -or $full -match '[\x00-\x1f]') { Stop-Transport 'path_refused' }
    # Textual protection precedes all component metadata. Only fixed ordinary Code paths follow.
    foreach ($part in $full.Substring([IO.Path]::GetPathRoot($full).Length).Split('\')) {
        if ($part -match '^(\.env(\..*)?|\.npmrc|privatekey.*|\.aws|\.ssh|\.kube|\.mozilla)$' -or
            $part -match '\.(env|pem|key|p12|pfx|keystore)$') { Stop-Transport 'path_refused' }
    }
    return $full
}
function Test-TransportOrdinary([string]$Path, [string]$Root, [bool]$File) {
    $full = Assert-TransportScope $Path $Root
    $current = [IO.Path]::GetPathRoot($full)
    $parts = $full.Substring($current.Length).Split('\')
    for ($index = 0; $index -lt $parts.Length; $index++) {
        $current = Join-Path $current $parts[$index]
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { return $false }
        catch [IO.DirectoryNotFoundException] { return $false }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-Transport 'reparse_refused' }
        $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
        if ($index -lt $parts.Length - 1 -and -not $isDirectory) { Stop-Transport 'path_refused' }
        if ($index -eq $parts.Length - 1 -and $isDirectory -eq $File) { Stop-Transport 'path_refused' }
    }
    return $true
}
function New-TransportCategoryCounts {
    return [ordered]@{ ClientInitializeFailed = 0; ClientRouterStartFailed = 0; ClientParseFailed = 0; ClientConnectError = 0
        RouterAlreadyStarted = 0; RouterStarting = 0; RouterOtherActive = 0; RouterListening = 0; RouterServerError = 0; RouterSecureFailed = 0 }
}
function New-TransportOsCounts {
    # Conservative diagnostic vocabulary, not a vendor guarantee or raw-error export.
    return [ordered]@{ EACCES = 0; EPERM = 0; EADDRINUSE = 0; ENOENT = 0; ECONNREFUSED = 0; ECONNRESET = 0; EPIPE = 0; ETIMEDOUT = 0; EINVAL = 0; ENOTCONN = 0; EBUSY = 0 }
}
function New-TransportLevelCounts {
    return [ordered]@{ trace = 0; debug = 0; info = 0; warning = 0; warn = 0; error = 0; critical = 0 }
}
function Convert-TransportText([string]$Text) {
    if ($Text.Length -gt 524288) { Stop-Transport 'text_limit_refused' }
    $counts = New-TransportCategoryCounts
    $codes = New-TransportOsCounts
    $levels = New-TransportLevelCounts
    $messages = @(
        @('IpcClient', 'Initialize failed', 'ClientInitializeFailed'), @('IpcClient', 'Unable to start router if needed', 'ClientRouterStartFailed'),
        @('IpcClient', 'Failed to parse message', 'ClientParseFailed'), @('IpcClient', 'Connect error', 'ClientConnectError'),
        @('IpcRouterManager', 'Router already started.', 'RouterAlreadyStarted'), @('IpcRouterManager', 'Starting router...', 'RouterStarting'),
        @('IpcRouterManager', 'Another router is active.', 'RouterOtherActive'), @('IpcRouterManager', 'Listening on', 'RouterListening'),
        @('IpcRouterManager', 'Server error', 'RouterServerError'), @('IpcRouterManager', 'Unable to secure IPC socket', 'RouterSecureFailed')
    )
    $header = New-Object Text.RegularExpressions.Regex('^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3} \[(?:trace|debug|info|warning|warn|error|critical)\] \[(?<owner>IpcClient|IpcRouterManager)\] (?<message>.*)$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant, [timespan]::FromMilliseconds(100))
    $generalHeader = New-Object Text.RegularExpressions.Regex('^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3} \[(?<level>trace|debug|info|warning|warn|error|critical)\](?:\s|$)',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant, [timespan]::FromMilliseconds(100))
    $lines = [regex]::Split($Text.TrimEnd([char[]]@([char]13, [char]10)), '\r?\n')
    $start = [Math]::Max(0, $lines.Length - 4096)
    $recognized = 0
    $structured = 0; $tagged = 0; $parsed = 0
    for ($index = $start; $index -lt $lines.Length; $index++) {
        $general = $generalHeader.Match($lines[$index])
        if ($general.Success) { $structured++; $levels[$general.Groups['level'].Value]++ }
        if ($lines[$index].Contains('[IpcClient]') -or $lines[$index].Contains('[IpcRouterManager]')) { $tagged++ }
        $match = $header.Match($lines[$index])
        if (-not $match.Success) { continue }
        $parsed++
        $body = $match.Groups['message'].Value
        $category = $null
        foreach ($known in $messages) {
            if ($match.Groups['owner'].Value -ceq $known[0] -and $body.StartsWith($known[1], [StringComparison]::Ordinal) -and
                ($body.Length -eq $known[1].Length -or [char]::IsWhiteSpace($body[$known[1].Length]))) { $category = $known[2]; break }
        }
        if ($null -eq $category) { continue }
        $counts[$category]++; $recognized++
        foreach ($code in @($codes.Keys)) {
            # Only explicit code fields on recognized transport records; never return another field.
            $pattern = '(?:^|[\s,{])(?:"code"\s*:\s*"|code\s*[:=]\s*["'']?)' + [regex]::Escape($code) + '(?=["''\s,}]|$)'
            if ([regex]::IsMatch($body, $pattern)) { $codes[$code]++ }
        }
    }
    return [pscustomobject]@{ CategoryCounts = $counts; OsErrorCounts = $codes; RecognizedRecordCount = $recognized
        StructuredHeaderCount = $structured; TaggedTransportLineCount = $tagged; ParsedTransportHeaderCount = $parsed
        LogLevelCounts = $levels; LineCapReached = ($start -gt 0) }
}
function Get-TransportChildren([string]$Directory, [string]$Pattern) {
    $names = New-Object 'Collections.Generic.List[string]'
    $seen = 0
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Directory)) {
        if (++$seen -gt 128) { Stop-Transport 'directory_entry_limit_refused' }
        $name = [IO.Path]::GetFileName($entry)
        # Nonmatching siblings are ignored by name; never descended, opened or attributed.
        if ([regex]::IsMatch($name, $Pattern)) { $names.Add($name) }
    }
    return $names.ToArray()
}
function Convert-TransportTailBytes([byte[]]$Buffer, [int]$Count, [bool]$SkipFirstLine, [bool]$ShortRead) {
    if ($Buffer.Length -gt 524288 -or $Count -lt 0 -or $Count -gt $Buffer.Length) { Stop-Transport 'text_limit_refused' }
    $begin = 0
    if ($SkipFirstLine) { while ($begin -lt $Count -and $Buffer[$begin] -ne 10) { $begin++ }; if ($begin -lt $Count) { $begin++ } }
    $end = $Count
    while ($end -gt $begin -and $Buffer[$end - 1] -ne 10) { $end-- }
    $decoder = New-Object Text.UTF8Encoding($false, $true)
    $text = $decoder.GetString($Buffer, $begin, $end - $begin)
    $summary = Convert-TransportText $text
    return [pscustomobject]@{ Summary = $summary; Incomplete = ($SkipFirstLine -or $end -ne $Count -or $ShortRead -or $summary.LineCapReached) }
}
function Read-TransportTail([string]$Path, [string]$Root) {
    if (-not (Test-TransportOrdinary $Path $Root $true)) { Stop-Transport 'selected_file_missing' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $length = $stream.Length
        if ($length -gt 67108864) { Stop-Transport 'file_size_limit_refused' }
        $offset = [Math]::Max(0, $length - 524288)
        $null = $stream.Seek($offset, [IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] ([int]($length - $offset))
        $count = 0
        while ($count -lt $buffer.Length) { $read = $stream.Read($buffer, $count, $buffer.Length - $count); if ($read -eq 0) { break }; $count += $read }
    } finally { $stream.Dispose() }
    return Convert-TransportTailBytes $buffer $count ($offset -gt 0) ($count -ne $buffer.Length)
}
function Get-SelectedTransportSummary([string[]]$Files, [string]$Root, [ref]$ContentMayHaveBeenRead) {
    $counts = New-TransportCategoryCounts; $codes = New-TransportOsCounts; $recognized = 0; $incomplete = $false
    $levels = New-TransportLevelCounts; $structured = 0; $tagged = 0; $parsed = 0
    foreach ($file in $Files) {
        # Set before the opener. A later open/decode failure must not claim no content access.
        $ContentMayHaveBeenRead.Value = $true
        $tail = Read-TransportTail $file $Root
        foreach ($name in @($counts.Keys)) { $counts[$name] += $tail.Summary.CategoryCounts[$name] }
        foreach ($name in @($codes.Keys)) { $codes[$name] += $tail.Summary.OsErrorCounts[$name] }
        foreach ($name in @($levels.Keys)) { $levels[$name] += $tail.Summary.LogLevelCounts[$name] }
        $structured += $tail.Summary.StructuredHeaderCount; $tagged += $tail.Summary.TaggedTransportLineCount; $parsed += $tail.Summary.ParsedTransportHeaderCount
        $recognized += $tail.Summary.RecognizedRecordCount; $incomplete = $incomplete -or $tail.Incomplete
    }
    return [ordered]@{ Status = 'summarized'; ContentRead = $true; FileCount = $Files.Count; CategoryCounts = $counts; OsErrorCounts = $codes; RecognizedRecordCount = $recognized
        StructuredHeaderCount = $structured; TaggedTransportLineCount = $tagged; ParsedTransportHeaderCount = $parsed; LogLevelCounts = $levels
        Incomplete = $incomplete; ConnectivityProven = $false }
}

try {
    $root = [IO.Path]::GetFullPath($LogRoot).TrimEnd('\')
    $expectedRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) 'Code\logs'
    if (-not [IO.Path]::IsPathRooted($LogRoot) -or -not [string]::Equals($root, $LogRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($root, $expectedRoot, [StringComparison]::OrdinalIgnoreCase)) { Stop-Transport 'normal_user_root_required' }
    if (-not (Test-TransportOrdinary $root $root $false)) { Stop-Transport 'log_root_missing' }
    if (-not $Summarize) {
        if ($SelectedLogPath.Count -ne 0) { Stop-Transport 'explicit_summarize_required' }
        $selectors = New-Object 'Collections.Generic.List[string]'
        $windows = 0; $sessions = 0; $limited = $false
        $sessionNames = @(Get-TransportChildren $root '^[0-9]{8}T[0-9]{6}$' | Sort-Object -Descending)
        if ($sessionNames.Count -gt $SessionLimit) { $limited = $true }
        foreach ($session in @($sessionNames | Select-Object -First $SessionLimit)) {
            $null = Get-TransportSelector ($session + '\window1\exthost\openai.chatgpt\Codex.log')
            $sessionPath = Join-Path $root $session
            if (-not (Test-TransportOrdinary $sessionPath $root $false)) { continue }; $sessions++
            foreach ($window in @(Get-TransportChildren $sessionPath '^window[1-9][0-9]{0,5}$' | Sort-Object)) {
                if ($windows -ge 16) { $limited = $true; break }; $windows++
                $relative = $session + '\' + $window + '\exthost\openai.chatgpt\Codex.log'
                if (Test-TransportOrdinary (Join-Path $root $relative) $root $true) { $selectors.Add($relative) }
            }
        }
        [ordered]@{ Status = 'metadata_only'; ContentRead = $false; RelativeSelectors = @($selectors.ToArray()); SessionsConsidered = $sessions; WindowsConsidered = $windows; PlanLimited = $limited } | ConvertTo-Json -Depth 5
    } else {
        if ($SelectedLogPath.Count -lt 1 -or $SelectedLogPath.Count -gt 16) { Stop-Transport 'selection_limit_refused' }
        $selected = New-Object 'Collections.Generic.List[string]'
        $sessionSet = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        $unique = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($value in $SelectedLogPath) {
            $selector = Get-TransportSelector $value
            if (-not $unique.Add($selector.Relative)) { Stop-Transport 'duplicate_selection_refused' }
            $null = $sessionSet.Add($selector.Session)
            if ($sessionSet.Count -gt 3) { Stop-Transport 'selection_limit_refused' }
            $file = Join-Path $root $selector.Relative
            if (-not (Test-TransportOrdinary $file $root $true)) { Stop-Transport 'selected_file_missing' }
            if ((Get-Item -LiteralPath $file -Force).Length -gt 67108864) { Stop-Transport 'file_size_limit_refused' }
            $selected.Add($file)
        }
        Get-SelectedTransportSummary $selected.ToArray() $root ([ref]$contentMayHaveBeenRead) | ConvertTo-Json -Depth 5
    }
} catch {
    $code = 'read_refused'
    $allowed = @('selector_refused', 'path_refused', 'reparse_refused', 'text_limit_refused', 'directory_entry_limit_refused', 'selected_file_missing',
        'file_size_limit_refused', 'normal_user_root_required', 'log_root_missing', 'explicit_summarize_required', 'selection_limit_refused', 'duplicate_selection_refused')
    if ($_.Exception.Data.Contains('TransportCode') -and [string]$_.Exception.Data['TransportCode'] -cin $allowed) { $code = [string]$_.Exception.Data['TransportCode'] }
    [ordered]@{ Status = 'refused'; Code = $code; ContentReadMayHaveOccurred = $contentMayHaveBeenRead; ConnectivityProven = $false } | ConvertTo-Json -Compress
    exit 1
}
