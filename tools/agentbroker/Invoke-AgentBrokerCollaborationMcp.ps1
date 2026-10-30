#requires -Version 7
<#
.SYNOPSIS
Validate one ordinary workspace JSON MCP request. Only -Execute sends it to loopback.
.DESCRIPTION
Fixed stateless endpoint/protocol; no credentials, proxy, redirects, cookies or native IPC.
Allowed tools are the small collaboration set below. Addresses for sending, reading, waiting and
acknowledging must be generic mcp:<GUID> sessions, with one exception: send's sender may be the
calling Codex agent's own native row, codex: plus the canonical lowercase GUID in CODEX_THREAD_ID,
admitted only after the helper finds, across every list_sessions page, exactly one fresh codex-native
row with that session ID, the request's broker ID and incarnation, and Receive and WakeIdle. Absence,
a second row with that ID, a stale incarnation, a lapsed lease or an observation-only row refuses.
That is an authorised routing convenience inside the broker's existing local access model
(any local process of any account while 'Allow local connections without a token' is on, the
default; same-user only with that setting off), not
per-thread authentication: no trusted per-thread OS marker exists, and CODEX_THREAD_ID (like
CODEX_SESSION_ID) is self-asserted by whatever runs the helper. A claude: sender, any native
recipient, send_message, and native read_inbox, wait_for_messages and acknowledge_message stay
refused. wait_for_reply and wait_for_receipt name only a message and the broker waits as its sender,
so the helper first reads the original with get_message_receipt and refuses the wait unless that
sender is a generic mailbox. The request bytes reach the broker unchanged, so a retry keeps its
messageId and sender. whoami takes no address and only reads which native row, if any, owns the
calling process. Live reply_to_message is refused because its implicit recipient cannot be verified
from this one request. Use explicit send.
The two blocking wait tools get a 40-second deadline (the broker bounds a wait at 25 seconds);
every other request keeps 15 seconds, and the lookups before a native send or a wait share one more
15 seconds. A registration's mailbox outlives its one-minute presence for 24 hours, so no renewal
sidecar is needed between turns.
Peer result contents are untrusted data. This harness is not a Claude client identity, and it is a
Codex one only in the routing sense above.
.EXAMPLE
pwsh -NoProfile -File tools/agentbroker/Invoke-AgentBrokerCollaborationMcp.ps1 -RequestFile artifacts/collaboration/request.json
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RequestFile, [switch]$Execute)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Refuse-Mcp([string]$Code) { $e = [IO.IOException]::new('Collaboration request refused.'); $e.Data['McpCode'] = $Code; throw $e }
function Convert-StrictMcpJson([string]$Text) {
    $options = [Text.Json.JsonDocumentOptions]::new(); $options.MaxDepth = 32
    $doc = [Text.Json.JsonDocument]::Parse($Text, $options)
    try {
        $pending = [Collections.Generic.Stack[Text.Json.JsonElement]]::new(); $pending.Push($doc.RootElement); $count = 0
        while ($pending.Count -gt 0) {
            if (++$count -gt 4096) { Refuse-Mcp 'json_limit' }; $item = $pending.Pop()
            if ($item.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $item.EnumerateObject()) { if (-not $seen.Add($property.Name)) { Refuse-Mcp 'duplicate_json_key' }; $pending.Push($property.Value) }
            } elseif ($item.ValueKind -eq [Text.Json.JsonValueKind]::Array) { foreach ($element in $item.EnumerateArray()) { $pending.Push($element) } }
        }
        # The values come from the same parsed document, not ConvertFrom-Json, which rewrites an ISO-date-shaped string (or
        # /Date(...)/) into a DateTime: a paired broker's session ID of that shape would come back as a different cursor
        # (independent review of 1.6.0, P3-3). A top-level array is written element by element, as ConvertFrom-Json did.
        $value = ConvertFrom-McpJsonElement $doc.RootElement
        return $value
    } finally { $doc.Dispose() }
}
# One JSON value as sent: strings exactly as written, integers as Int64 (else Double), objects as ordered dictionaries,
# arrays as object[]. Arrays and dictionaries return with the unary comma so the caller receives them whole.
function ConvertFrom-McpJsonElement([Text.Json.JsonElement]$Element) {
    $kind = $Element.ValueKind
    if ($kind -eq [Text.Json.JsonValueKind]::Object) {
        $map = [ordered]@{}
        foreach ($property in $Element.EnumerateObject()) { $map[$property.Name] = ConvertFrom-McpJsonElement $property.Value }
        return ,$map
    }
    if ($kind -eq [Text.Json.JsonValueKind]::Array) {
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in $Element.EnumerateArray()) { $items.Add((ConvertFrom-McpJsonElement $item)) }
        return ,$items.ToArray()
    }
    if ($kind -eq [Text.Json.JsonValueKind]::String) { return $Element.GetString() }
    if ($kind -eq [Text.Json.JsonValueKind]::Number) { $whole = 0L; if ($Element.TryGetInt64([ref]$whole)) { return $whole }; return $Element.GetDouble() }
    if ($kind -eq [Text.Json.JsonValueKind]::True) { return $true }
    if ($kind -eq [Text.Json.JsonValueKind]::False) { return $false }
    return $null
}
function Get-McpField($Value, [string]$Name) {
    if ($Value -isnot [Collections.IDictionary] -or @($Value.Keys) -cnotcontains $Name) { Refuse-Mcp 'request_shape' }
    return $Value[$Name]
}
function Confirm-GenericMcpAddress($Address) {
    $broker = Get-McpField $Address 'brokerId'; $session = Get-McpField $Address 'sessionId'; $incarnation = Get-McpField $Address 'incarnation'
    $parsed = [guid]::Empty
    foreach ($value in @($broker, $incarnation)) { if (-not [guid]::TryParseExact([string]$value, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty) { Refuse-Mcp 'generic_address_required' } }
    if ($session -isnot [string] -or -not $session.StartsWith('mcp:', [StringComparison]::Ordinal) -or
        -not [guid]::TryParseExact($session.Substring(4), 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty -or
        $session -cne ('mcp:' + $parsed.ToString('D'))) { Refuse-Mcp 'generic_address_required' }
}
# The calling Codex agent's own native session ID: codex: plus the canonical lowercase GUID in CODEX_THREAD_ID, the
# form the broker's Codex adapter names its rows in. $null for a missing, empty or zero value, or one that is not
# already canonical (uppercase, braces, no hyphens, spaces): a self-asserted value is never repaired.
function Get-OwnCodexSessionId([string]$ThreadId) {
    $parsed = [guid]::Empty
    if ([string]::IsNullOrEmpty($ThreadId) -or -not [guid]::TryParseExact($ThreadId, 'D', [ref]$parsed) -or
        $parsed -eq [guid]::Empty -or $ThreadId -cne $parsed.ToString('D')) { return $null }
    return 'codex:' + $parsed.ToString('D')
}
function Test-CodexSessionAddress($Address) {
    return $Address -is [Collections.IDictionary] -and @($Address.Keys) -ccontains 'sessionId' -and
        $Address['sessionId'] -is [string] -and $Address['sessionId'].StartsWith('codex:', [StringComparison]::Ordinal)
}
# A send's sender may be the caller's own codex: row (N-agentbroker-helper-native-sender-01). This checks only the
# address; Confirm-NativeSenderRow then finds the row itself before anything is sent.
function Confirm-OwnCodexSender($Address, [string]$CodexThreadId) {
    $broker = Get-McpField $Address 'brokerId'; $session = Get-McpField $Address 'sessionId'; $incarnation = Get-McpField $Address 'incarnation'
    $parsed = [guid]::Empty
    foreach ($value in @($broker, $incarnation)) { if ($value -isnot [string] -or -not [guid]::TryParseExact($value, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty) { Refuse-Mcp 'native_sender_refused' } }
    $own = Get-OwnCodexSessionId $CodexThreadId
    if ($null -eq $own -or $session -cne $own) { Refuse-Mcp 'native_sender_refused' }
}
function Confirm-CollaborationRequest($Request, [string]$CodexThreadId = '') {
    if ((Get-McpField $Request 'jsonrpc') -cne '2.0') { Refuse-Mcp 'request_shape' }
    foreach ($name in $Request.Keys) { if ($name -cnotin @('jsonrpc', 'id', 'method', 'params')) { Refuse-Mcp 'request_shape' } }
    $method = Get-McpField $Request 'method'
    if ($method -cnotin @('initialize', 'notifications/initialized', 'tools/list', 'tools/call')) { Refuse-Mcp 'method_not_allowed' }
    if ($method -ceq 'notifications/initialized') { if (@($Request.Keys) -ccontains 'id') { Refuse-Mcp 'request_shape' } }
    else { $id = Get-McpField $Request 'id'; if ($id -isnot [string] -or $id -cnotmatch '^[A-Za-z0-9._:-]{1,64}$') { Refuse-Mcp 'request_shape' } }
    $tool = $null; $nativeSender = $null; $originalMessageId = $null
    if ($method -ceq 'initialize') {
        $params = Get-McpField $Request 'params'; $client = Get-McpField $params 'clientInfo'
        if ((Get-McpField $params 'protocolVersion') -cne '2025-11-25' -or
            (Get-McpField $client 'name') -cne 'agentbroker-collaboration-harness') { Refuse-Mcp 'harness_identity_required' }
    }
    if ($method -ceq 'tools/call') {
        $params = Get-McpField $Request 'params'; $tool = Get-McpField $params 'name'; $args = Get-McpField $params 'arguments'
        if ($tool -cnotin @('whoami', 'register_agent', 'list_sessions', 'read_inbox', 'send', 'send_message', 'wait_for_messages', 'wait_for_reply', 'wait_for_receipt', 'reply_to_message', 'acknowledge_message', 'get_message_receipt')) { Refuse-Mcp 'tool_not_allowed' }
        if ($args -isnot [Collections.IDictionary]) { Refuse-Mcp 'request_shape' }
        if ($tool -ceq 'send_message') { $message = Get-McpField $args 'message'; Confirm-GenericMcpAddress (Get-McpField $message 'sender'); Confirm-GenericMcpAddress (Get-McpField $message 'recipient') }
        if ($tool -ceq 'send') {
            $sender = Get-McpField $args 'sender'; Confirm-GenericMcpAddress (Get-McpField $args 'recipient')
            if (Test-CodexSessionAddress $sender) { Confirm-OwnCodexSender $sender $CodexThreadId; $nativeSender = $sender }
            else { Confirm-GenericMcpAddress $sender }
        }
        if ($tool -cin @('read_inbox', 'wait_for_messages')) { Confirm-GenericMcpAddress (Get-McpField $args 'session') }
        if ($tool -ceq 'acknowledge_message') { Confirm-GenericMcpAddress (Get-McpField $args 'recipient') }
        # The broker infers these waits' session from the original message's sender, past every address guard above.
        if ($tool -cin @('wait_for_reply', 'wait_for_receipt')) {
            $id = Get-McpField $args 'messageId'; $parsed = [guid]::Empty
            if ($id -isnot [string] -or -not [guid]::TryParseExact($id, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty) { Refuse-Mcp 'request_shape' }
            $originalMessageId = $parsed.ToString('D')
        }
    }
    return [pscustomobject]@{ Method = $method; Tool = $tool; ExecuteAllowed = ($tool -cne 'reply_to_message')
        NativeSender = $nativeSender; OriginalMessageId = $originalMessageId }
}
# A tool's value, from structured content or the one JSON text block, written to the pipeline (an array's items one
# by one), so a caller collects it with @(...). A tool error refuses; nothing of its text is kept.
function Get-McpToolValue($Result) {
    if ($Result -isnot [Collections.IDictionary]) { Refuse-Mcp 'lookup_shape' }
    if (@($Result.Keys) -ccontains 'isError' -and $Result['isError'] -eq $true) { Refuse-Mcp 'lookup_failed' }
    if (@($Result.Keys) -ccontains 'structuredContent') {
        $structured = $Result['structuredContent']
        if ($structured -is [Collections.IDictionary] -and @($structured.Keys) -ccontains 'result') { return $structured['result'] }
        return $structured
    }
    $content = @(Get-McpField $Result 'content')
    if ($content.Count -eq 0) { return }
    if ($content.Count -ne 1 -or (Get-McpField $content[0] 'type') -cne 'text' -or (Get-McpField $content[0] 'text') -isnot [string]) { Refuse-Mcp 'lookup_shape' }
    return Convert-StrictMcpJson $content[0]['text']
}
# Capabilities arrive as the enum's names ("Receive, WakeIdle, Reply, NativeIdleGuard") or as its number; an unknown
# name refuses rather than being skipped.
function Test-ReceiveAndWakeIdle($Capabilities) {
    if ($Capabilities -is [long] -or $Capabilities -is [int]) { return $Capabilities -ge 0 -and ($Capabilities -band 3) -eq 3 }
    if ($Capabilities -isnot [string]) { return $false }
    $names = @($Capabilities.Split(',') | ForEach-Object { $_.Trim() })
    if (@($names | Where-Object { $_ -cnotin @('None', 'Receive', 'WakeIdle', 'Reply', 'NativeIdleGuard') }).Count -gt 0) { return $false }
    return ($names -ccontains 'Receive') -and ($names -ccontains 'WakeIdle')
}
# The lease as JSON gives it: a string with an explicit offset, or the DateTime ConvertFrom-Json makes of one. A time
# with no offset (DateTimeKind Unspecified) cannot be compared and refuses.
function Test-FreshLease($Lease, [DateTime]$UtcNow) {
    if ($Lease -is [DateTime]) { return $Lease.Kind -ne [DateTimeKind]::Unspecified -and $Lease.ToUniversalTime() -gt $UtcNow }
    if ($Lease -is [DateTimeOffset]) { return $Lease.UtcDateTime -gt $UtcNow }
    $parsed = [DateTimeOffset]::MinValue
    if ($Lease -isnot [string] -or $Lease -cnotmatch '(Z|[+-][0-9]{2}:[0-9]{2})$' -or
        -not [DateTimeOffset]::TryParse($Lease, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsed)) { return $false }
    return $parsed.UtcDateTime -gt $UtcNow
}
# Before a native send: exactly one fresh codex-native row with the sender's session ID across EVERY list_sessions
# page (from any broker, so a paired broker advertising the same ID makes it ambiguous), naming the request's broker ID
# and incarnation, able to Receive and WakeIdle. The broker itself still refuses a sender from any other broker
# (sender_mismatch) and one that is no longer addressable (sender_unavailable) when the send arrives. $Invoke runs one
# tool call and writes its value; tests pass a fake.
function Confirm-NativeSenderRow($Sender, [scriptblock]$Invoke, [DateTime]$UtcNow) {
    $session = [string]$Sender['sessionId']; $broker = [guid]::Parse([string]$Sender['brokerId']); $incarnation = [guid]::Parse([string]$Sender['incarnation'])
    $matched = [Collections.Generic.List[object]]::new(); $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal); $cursor = $null
    for ($page = 1; ; $page++) {
        # 64 pages of 16 rows (1,024) covers this broker's own rows (at most 256) and three full paired brokers (256 each);
        # a larger federated directory refuses native_sender_directory_limit, fail closed. In practice the 15-second
        # lookup budget ends a scan first when a paired broker is slow: every page re-fetches each paired broker's
        # directory under its own 5-second budget (BrokerRuntime.DirectoryAsync), so one slow peer allows about three
        # pages before the scan refuses (independent review of 1.6.0, P3-3).
        if ($page -gt 64) { Refuse-Mcp 'native_sender_directory_limit' }
        $arguments = [ordered]@{ limit = 16 }; if ($null -ne $cursor) { $arguments['afterAddress'] = $cursor }
        $rows = @(& $Invoke 'list_sessions' $arguments)
        foreach ($row in $rows) {
            $address = Get-McpField $row 'address'
            $key = [string](Get-McpField $address 'brokerId') + '|' + [string](Get-McpField $address 'sessionId') + '|' + [string](Get-McpField $address 'incarnation')
            if (-not $seen.Add($key)) { Refuse-Mcp 'native_sender_directory_inconsistent' }
            if ((Get-McpField $address 'sessionId') -ceq $session) { $matched.Add($row) }
        }
        if ($rows.Count -lt 16) { break }
        $last = Get-McpField $rows[-1] 'address'
        $cursor = [ordered]@{ brokerId = $last['brokerId']; sessionId = $last['sessionId']; incarnation = $last['incarnation'] }
    }
    if ($matched.Count -eq 0) { Refuse-Mcp 'native_sender_not_found' }
    if ($matched.Count -gt 1) { Refuse-Mcp 'native_sender_ambiguous' }
    $row = $matched[0]; $address = $row['address']; $parsed = [guid]::Empty
    if (-not [guid]::TryParse([string]$address['brokerId'], [ref]$parsed) -or $parsed -ne $broker) { Refuse-Mcp 'native_sender_other_broker' }
    if (-not [guid]::TryParse([string]$address['incarnation'], [ref]$parsed) -or $parsed -ne $incarnation) { Refuse-Mcp 'native_sender_stale_incarnation' }
    if ((Get-McpField $row 'adapter') -cne 'codex-native') { Refuse-Mcp 'native_sender_not_codex' }
    if (-not (Test-ReceiveAndWakeIdle (Get-McpField $row 'capabilities'))) { Refuse-Mcp 'native_sender_observation_only' }
    if (-not (Test-FreshLease (Get-McpField $row 'leaseUntil') $UtcNow)) { Refuse-Mcp 'native_sender_lease_lapsed' }
}
# Before wait_for_reply or wait_for_receipt: the original message, read with get_message_receipt, must have been sent
# by a generic mailbox. A native sender (or no such message) refuses, so a wait never reaches a native session's
# BeginWait through the message ID.
function Confirm-GenericOriginalSender([string]$MessageId, [scriptblock]$Invoke) {
    $receipts = @(& $Invoke 'get_message_receipt' ([ordered]@{ messageId = $MessageId }))
    if ($receipts.Count -ne 1 -or $receipts[0] -isnot [Collections.IDictionary]) { Refuse-Mcp 'original_message_unavailable' }
    $message = Get-McpField $receipts[0] 'message'; $parsed = [guid]::Empty
    if (-not [guid]::TryParse([string](Get-McpField $message 'id'), [ref]$parsed) -or $parsed.ToString('D') -cne $MessageId) { Refuse-Mcp 'original_message_unavailable' }
    try { Confirm-GenericMcpAddress (Get-McpField $message 'sender') } catch { Refuse-Mcp 'native_wait_refused' }
}
# A wait may block up to the broker's 25-second bound, so its request needs more than the usual 15.
function Get-McpRequestSeconds([string]$Tool) { if ($Tool -cin @('wait_for_messages', 'wait_for_reply', 'wait_for_receipt')) { return 40 }; return 15 }
function Assert-McpRequestPath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path); $scope = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not $full.StartsWith($scope + '\', [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith('\\') -or
        $full.Substring(3).Contains(':') -or $full -match '[\x00-\x1f]' -or [IO.Path]::GetExtension($full) -cne '.json') { Refuse-Mcp 'request_path_refused' }
    $parts = $full.Substring([IO.Path]::GetPathRoot($full).Length).Split('\')
    foreach ($part in $parts) { if ($part -match '^(\.env(\..*)?|\.npmrc|privatekey.*|\.aws|\.ssh|\.kube|\.mozilla)$' -or $part -match '\.(env|pem|key|p12|pfx|keystore)$') { Refuse-Mcp 'request_path_refused' } }
    if ($full.Replace('\', '/') -match '(?i)/\.git/credentials(/|$)') { Refuse-Mcp 'request_path_refused' }
    return $full
}
function Confirm-McpRequestComponents([string]$Full) {
    $current = [IO.Path]::GetPathRoot($Full)
    foreach ($part in $Full.Substring($current.Length).Split('\')) {
        $current = Join-Path $current $part; $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Refuse-Mcp 'request_path_refused' }
    }
}
function Read-McpBytes([IO.Stream]$Stream, [int]$Limit, [Threading.CancellationToken]$Token) {
    $memory = [IO.MemoryStream]::new(); $buffer = [byte[]]::new(8192)
    try {
        while ($true) { $read = $Stream.ReadAsync($buffer, 0, [Math]::Min($buffer.Length, $Limit + 1 - [int]$memory.Length), $Token).GetAwaiter().GetResult(); if ($read -eq 0) { break }; $memory.Write($buffer, 0, $read); if ($memory.Length -gt $Limit) { Refuse-Mcp 'body_limit' } }
        return ,$memory.ToArray()
    } finally { $memory.Dispose() }
}
function Convert-McpReply([byte[]]$Bytes, [string]$MediaType, [string]$ExpectedId) {
    if ($Bytes.Length -gt 524288) { Refuse-Mcp 'body_limit' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes); $messages = [Collections.Generic.List[object]]::new()
    if ($MediaType -ceq 'application/json') { $messages.Add((Convert-StrictMcpJson $text)) }
    elseif ($MediaType -ceq 'text/event-stream') {
        $frames = [regex]::Split($text, '\r?\n\r?\n'); if ($frames.Count -gt 32) { Refuse-Mcp 'sse_limit' }
        foreach ($frame in $frames) { $data = @([regex]::Matches($frame, '(?m)^data: ?(.*)\r?$') | ForEach-Object { $_.Groups[1].Value.TrimEnd("`r") }); if ($data.Count -gt 0) { $messages.Add((Convert-StrictMcpJson ($data -join "`n"))) } }
    } else { Refuse-Mcp 'response_type_refused' }
    $matches = @($messages | Where-Object { $_ -is [Collections.IDictionary] -and @($_.Keys) -ccontains 'id' -and $_['id'] -ceq $ExpectedId })
    if ($matches.Count -ne 1 -or (Get-McpField $matches[0] 'jsonrpc') -cne '2.0') { Refuse-Mcp 'response_shape' }
    if (@($matches[0].Keys) -ccontains 'error') { return [ordered]@{ Status = 'mcp_error'; ErrorCode = [int](Get-McpField $matches[0]['error'] 'code') } }
    return [ordered]@{ Status = 'mcp_result'; Result = (Get-McpField $matches[0] 'result') }
}
# One tool call the helper makes itself, before the request it was given: the same fixed loopback endpoint, client and
# response limits, under the lookups' shared deadline. Writes the tool's value; an HTTP, RPC or tool error refuses.
function Invoke-McpLookup([Net.Http.HttpClient]$Client, [string]$Tool, $Arguments, [string]$Id, [Threading.CancellationToken]$Token) {
    $call = [ordered]@{ jsonrpc = '2.0'; id = $Id; method = 'tools/call'; params = [ordered]@{ name = $Tool; arguments = $Arguments } }
    $body = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $call -Depth 8 -Compress))
    $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, 'http://127.0.0.1:7432/mcp'); $response = $null
    try {
        $message.Headers.Add('MCP-Protocol-Version', '2025-11-25'); $message.Headers.Accept.ParseAdd('application/json'); $message.Headers.Accept.ParseAdd('text/event-stream')
        $message.Content = [Net.Http.ByteArrayContent]::new($body); $message.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/json')
        $response = $Client.SendAsync($message, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $Token).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode -or $response.Content.Headers.ContentLength -gt 524288) { Refuse-Mcp 'lookup_unavailable' }
        $stream = $response.Content.ReadAsStream(); try { $reply = Read-McpBytes $stream 524288 $Token } finally { $stream.Dispose() }
        $decoded = Convert-McpReply $reply $response.Content.Headers.ContentType.MediaType $Id
        if ($decoded.Status -cne 'mcp_result') { Refuse-Mcp 'lookup_failed' }
        Get-McpToolValue $decoded.Result
    } finally { if ($null -ne $response) { $response.Dispose() }; $message.Dispose() }
}
# Execute's order: the lookups a validated request needs, then the request itself, and nothing is sent when a lookup
# refuses. $LookupCall makes one tool call and writes its value; $SendRequest posts the request file's own bytes.
function Invoke-CheckedRequest($Checked, [scriptblock]$LookupCall, [scriptblock]$SendRequest) {
    if ($null -ne $Checked.NativeSender) { Confirm-NativeSenderRow $Checked.NativeSender $LookupCall ([DateTime]::UtcNow) }
    if ($null -ne $Checked.OriginalMessageId) { Confirm-GenericOriginalSender $Checked.OriginalMessageId $LookupCall }
    & $SendRequest
}
try {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')); $path = Assert-McpRequestPath $RequestFile $repo; Confirm-McpRequestComponents $path
    $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $bytes = Read-McpBytes $file 131072 ([Threading.CancellationToken]::None) } finally { $file.Dispose() }
    $request = Convert-StrictMcpJson ([Text.UTF8Encoding]::new($false, $true).GetString($bytes)); $validated = Confirm-CollaborationRequest $request $env:CODEX_THREAD_ID
    $lookupBefore = 'none'; if ($null -ne $validated.NativeSender) { $lookupBefore = 'list_sessions' } elseif ($null -ne $validated.OriginalMessageId) { $lookupBefore = 'get_message_receipt' }
    if (-not $Execute) { [ordered]@{ Status = 'validated_only'; Method = $validated.Method; Tool = $validated.Tool; ExecuteAllowed = $validated.ExecuteAllowed; LookupBeforeRequest = $lookupBefore; HttpRequestSent = $false } | ConvertTo-Json -Compress; return }
    if (-not $validated.ExecuteAllowed) { Refuse-Mcp 'implicit_reply_recipient_refused' }
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false; $handler.AllowAutoRedirect = $false; $handler.UseCookies = $false; $handler.UseDefaultCredentials = $false
    $client = [Net.Http.HttpClient]::new($handler); $lookupDeadline = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds(15)); $script:lookupSerial = 0
    try {
        $lookupCall = { param([string]$LookupTool, $LookupArguments) $script:lookupSerial++; Invoke-McpLookup $client $LookupTool $LookupArguments ('collab-lookup-' + $script:lookupSerial) $lookupDeadline.Token }
        # The request file's own bytes, unchanged, so a retry keeps its messageId and sender.
        $sendRequest = {
            $deadline = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds((Get-McpRequestSeconds $validated.Tool))); $response = $null; $message = $null
            try {
                $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, 'http://127.0.0.1:7432/mcp'); $message.Headers.Add('MCP-Protocol-Version', '2025-11-25'); $message.Headers.Accept.ParseAdd('application/json'); $message.Headers.Accept.ParseAdd('text/event-stream')
                $message.Content = [Net.Http.ByteArrayContent]::new($bytes); $message.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/json')
                $response = $client.SendAsync($message, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
                if (-not $response.IsSuccessStatusCode) { [ordered]@{ Status = 'http_error'; HttpStatus = [int]$response.StatusCode } | ConvertTo-Json -Compress; return }
                if ([int]$response.StatusCode -in @(202, 204) -and $validated.Method -ceq 'notifications/initialized') { [ordered]@{ Status = 'notification_accepted' } | ConvertTo-Json -Compress; return }
                if ($response.Content.Headers.ContentLength -gt 524288) { Refuse-Mcp 'body_limit' }
                $stream = $response.Content.ReadAsStream(); try { $reply = Read-McpBytes $stream 524288 $deadline.Token } finally { $stream.Dispose() }
                Convert-McpReply $reply $response.Content.Headers.ContentType.MediaType ([string]$request['id']) | ConvertTo-Json -Depth 32
            } finally { if ($null -ne $response) { $response.Dispose() }; if ($null -ne $message) { $message.Dispose() }; $deadline.Dispose() }
        }
        Invoke-CheckedRequest $validated $lookupCall $sendRequest
    } finally { $lookupDeadline.Dispose(); $client.Dispose() }
} catch {
    $code = 'request_refused'; if ($_.Exception.Data.Contains('McpCode')) { $code = [string]$_.Exception.Data['McpCode'] }
    [ordered]@{ Status = 'refused'; Code = $code } | ConvertTo-Json -Compress; exit 1
}
