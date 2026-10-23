#requires -Version 5.1
# In-memory tests only. Import function ASTs, never execute the helper's live entry point.
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\agentbroker\Get-CodexTransportSummary.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Test-CodexTransportSummary.ps1: helper parse failed.' }
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$checks = 0
function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw ('Test-CodexTransportSummary.ps1: ' + $Description) }
    $script:checks++
}
function Refuses([scriptblock]$Action, [string]$Description) {
    $failed = $false
    try { $null = & $Action } catch { $failed = $true }
    Check $failed $Description
}
$selector = Get-TransportSelector '20261022T144223\window2\exthost\openai.chatgpt\Codex.log'
Check ($selector.Session -ceq '20261022T144223' -and $selector.Window -ceq 'window2') 'exact local session/window/channel selector'
foreach ($bad in @('20261322T144223\window2\exthost\openai.chatgpt\Codex.log', '20261022T144223\window0\exthost\openai.chatgpt\Codex.log',
    '20261022T144223\window2\exthost\output_logging_20261022T144223\1-Codex.log',
    '20261022T144223\window2\exthost\openai.chatgpt\Codex.1.log', '20261022T144223\window2\exthost\other.extension\Codex.log',
    '..\20261022T144223\window2\exthost\openai.chatgpt\Codex.log', '20261022T144223\window2\exthost\openai.chatgpt\.env',
    '20261022T144223\window2\exthost\privatekey-fixture\Codex.log', '\\remote\share\Codex.log')) {
    # Protected-looking names are strings only. No matching file/directory is created or accessed.
    Refuses { Get-TransportSelector $bad } 'unknown/protected/remote selector rejected lexically'
}
$root = 'C:\Users\Synthetic\AppData\Roaming\Code\logs'
Refuses { Assert-TransportScope ($root + '\.env') $root } 'protected symbolic path refused without filesystem access'
Refuses { Assert-TransportScope ($root + '\privatekey-placeholder\Codex.log') $root } 'protected symbolic directory refused without access'
Refuses { Assert-TransportScope 'C:\outside\Codex.log' $root } 'out-of-root symbolic path refused'
$records = @(
    '[IpcClient] Initialize failed', '[IpcClient] Unable to start router if needed', '[IpcClient] Failed to parse message', '[IpcClient] Connect error',
    '[IpcRouterManager] Router already started.', '[IpcRouterManager] Starting router...', '[IpcRouterManager] Another router is active.',
    '[IpcRouterManager] Listening on', '[IpcRouterManager] Server error', '[IpcRouterManager] Unable to secure IPC socket'
)
$sensitiveMarker = 'SYNTHETIC_NOT_FOR_OUTPUT'
$text = (($records | ForEach-Object { '2026-10-22 14:42:23.123 [warning] ' + $_ + ' code=EACCES id=11111111-1111-4111-8111-111111111111 path=C:\Synthetic\file token=' + $sensitiveMarker }) -join "`n") + "`n"
$summary = Convert-TransportText $text
Check ($summary.RecognizedRecordCount -eq 10) 'all and only ten source-confirmed categories recognized'
Check ($summary.StructuredHeaderCount -eq 10) 'general structured headers counted independently of messages'
Check ($summary.TaggedTransportLineCount -eq 10 -and $summary.ParsedTransportHeaderCount -eq 10) 'literal transport tags and parsed headers counted separately'
Check ($summary.LogLevelCounts.warning -eq 10 -and $summary.LogLevelCounts.debug -eq 0) 'only finite observed log levels counted'
foreach ($name in $summary.CategoryCounts.Keys) { Check ($summary.CategoryCounts[$name] -eq 1) ('fixed category counted: ' + $name) }
Check ($summary.OsErrorCounts.EACCES -eq 10) 'allowlisted explicit OS code counted'
Check ($summary.OsErrorCounts.EPERM -eq 0) 'unobserved code stays zero'
$json = $summary | ConvertTo-Json -Depth 5 -Compress
Check ($json -notmatch 'SYNTHETIC_NOT_FOR_OUTPUT|11111111|Synthetic|token|path=|\[IpcClient\]') 'no raw values, identities, paths or log lines exported'
$noise = @(
    '2026-10-22 14:42:23.123 [warning] [OtherComponent] Initialize failed code=EACCES',
    'prefix [IpcClient] Initialize failed code=EACCES',
    '2026-10-22 14:42:23.123 [warning] [IpcClient] Initialize failed_suffix code=EACCES',
    '2026-10-22 14:42:23.123 [warning] [IpcClient] Arbitrary error code=EACCES',
    '2026-10-22 14:42:23.123 [warning] [IpcRouterManager] Unknown path token=EACCES'
) -join "`n"
$ignored = Convert-TransportText $noise
Check ($ignored.RecognizedRecordCount -eq 0 -and $ignored.OsErrorCounts.EACCES -eq 0) 'spoofed/unrelated/unknown messages never contribute codes'
Check ($ignored.StructuredHeaderCount -eq 4 -and $ignored.TaggedTransportLineCount -eq 4 -and $ignored.ParsedTransportHeaderCount -eq 3) 'format counters distinguish unrelated, unrecognized and malformed transport headers'
$differentFormat = Convert-TransportText '2026-10-22T14:42:23.123Z [debug] [IpcClient] Connect error code=EPIPE id=SYNTHETIC_NOT_FOR_OUTPUT'
Check ($differentFormat.TaggedTransportLineCount -eq 1 -and $differentFormat.StructuredHeaderCount -eq 0 -and $differentFormat.ParsedTransportHeaderCount -eq 0) 'literal tag identifies a timestamp format mismatch without raw export'
Check ($differentFormat.RecognizedRecordCount -eq 0 -and $differentFormat.OsErrorCounts.EPIPE -eq 0) 'unsupported format does not contribute transport or OS-code claims'
$debug = Convert-TransportText '2026-10-22 14:42:23.123 [debug] [UnrelatedComponent] unrelated payload=SYNTHETIC_NOT_FOR_OUTPUT'
Check ($debug.StructuredHeaderCount -eq 1 -and $debug.LogLevelCounts.debug -eq 1 -and $debug.TaggedTransportLineCount -eq 0 -and $debug.RecognizedRecordCount -eq 0) 'observed debug level can be counted without attributing unrelated content to transport'
$codes = Convert-TransportText '2026-10-22 14:42:23.123 [debug] [IpcClient] Connect error {"code":"ECONNREFUSED","message":"SYNTHETIC_NOT_FOR_OUTPUT","other":"EPERM"} code=EUNRECOGNIZED'
Check ($codes.OsErrorCounts.ECONNREFUSED -eq 1 -and $codes.OsErrorCounts.EPERM -eq 0) 'only explicit allowlisted OS-code field counted'
Check (($codes | ConvertTo-Json -Depth 5 -Compress) -notmatch 'EUNRECOGNIZED|SYNTHETIC_NOT_FOR_OUTPUT') 'unknown error value never becomes output'
$utf8 = New-Object Text.UTF8Encoding($false)
$bytes = $utf8.GetBytes($text)
$tail = Convert-TransportTailBytes $bytes $bytes.Length $false $false
Check ($tail.Summary.RecognizedRecordCount -eq 10 -and -not $tail.Incomplete) 'complete bounded tail stays complete'
$boundaryText = 'incomplete first record' + "`n" + $text + '2026-10-22 14:42:23.123 [warning] [IpcClient] Initialize failed'
$boundary = $utf8.GetBytes($boundaryText)
$cropped = Convert-TransportTailBytes $boundary $boundary.Length $true $false
Check ($cropped.Summary.RecognizedRecordCount -eq 10 -and $cropped.Incomplete) 'both partial boundary records discarded'
$short = Convert-TransportTailBytes $bytes $bytes.Length $false $true
Check $short.Incomplete 'short read stays explicitly incomplete'
$one = '2026-10-22 14:42:23.123 [debug] [IpcClient] Connect error'
$capped = Convert-TransportText (($one + "`n") * 4100)
Check ($capped.LineCapReached -and $capped.RecognizedRecordCount -eq 4096) 'line processing cap retains only bounded tail records'
Refuses { Convert-TransportText ('x' * 524289) } 'text byte/character budget cannot be exceeded'
Refuses { Convert-TransportTailBytes (New-Object byte[] 524289) 0 $false $false } 'tail-byte budget cannot be exceeded'
Refuses { Convert-TransportTailBytes ([byte[]]@(255, 10)) 2 $false $false } 'invalid complete UTF-8 record refused without raw output'
# Exercise the actual production content-loop and refusal report with synthetic in-memory reads.
# No filesystem opener is called: only this lexical test scope replaces the reader function.
& {
    $script:fakeReads = 0
    function Read-TransportTail([string]$Path, [string]$Root) {
        $script:fakeReads++
        if ($script:fakeReads -gt 1) { Stop-Transport 'text_limit_refused' }
        return [pscustomobject]@{ Summary = (Convert-TransportText '2026-10-22 14:42:23.123 [debug] [IpcClient] Connect error'); Incomplete = $false }
    }
    $contentMayHaveBeenRead = $false
    $caught = $false
    $outerTry = $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] } | Select-Object -Last 1
    $reportCode = [scriptblock]::Create($outerTry.CatchClauses[0].Body.Extent.Text.TrimStart('{').TrimEnd('}').Replace('exit 1', ''))
    $report = $null
    try { $null = Get-SelectedTransportSummary @('first-synthetic.log', 'second-synthetic.log') 'C:\Synthetic' ([ref]$contentMayHaveBeenRead) }
    catch { $caught = $true; $report = (& $reportCode | ConvertFrom-Json) }
    Check ($caught -and $script:fakeReads -eq 2 -and $contentMayHaveBeenRead) 'failure after an earlier read preserves content-may-have-occurred truth'
    # Run the actual top-level catch body. It must emit only a fixed refusal and truthful access flag.
    Check ($report.Status -ceq 'refused' -and $report.ContentReadMayHaveOccurred -eq $true -and -not $report.ConnectivityProven) 'actual catch report cannot claim success or unread content after partial failure'
    Check (($report | ConvertTo-Json -Compress) -notmatch 'Synthetic|first|second|code=') 'refusal report has no synthetic raw details'
}
Write-Output ('PASS: ' + $checks + ' in-memory transport-summary checks; no user logs, IPC, GUI or configuration accessed.')
