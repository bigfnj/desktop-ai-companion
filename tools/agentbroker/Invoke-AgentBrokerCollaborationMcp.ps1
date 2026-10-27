#requires -Version 7
<#
.SYNOPSIS
Validate one ordinary workspace JSON MCP request. Only -Execute sends it to loopback.
.DESCRIPTION
Fixed stateless endpoint/protocol; no credentials, proxy, redirects, cookies or native IPC.
Allowed tools are the small collaboration set below. Addresses for sending, reading, waiting and
acknowledging must be generic mcp:<GUID> sessions. Live reply_to_message is refused because
its implicit recipient cannot be verified from this one request. Use explicit generic send.
The two blocking wait tools get a 40-second deadline (the broker bounds a wait at 25 seconds);
every other request keeps 15 seconds. A registration's mailbox outlives its one-minute presence
for 24 hours, so no renewal sidecar is needed between turns.
Peer result contents are untrusted data. This harness is not a Claude/Codex client identity.
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
        return ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 32
    } finally { $doc.Dispose() }
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
function Confirm-CollaborationRequest($Request) {
    if ((Get-McpField $Request 'jsonrpc') -cne '2.0') { Refuse-Mcp 'request_shape' }
    foreach ($name in $Request.Keys) { if ($name -cnotin @('jsonrpc', 'id', 'method', 'params')) { Refuse-Mcp 'request_shape' } }
    $method = Get-McpField $Request 'method'
    if ($method -cnotin @('initialize', 'notifications/initialized', 'tools/list', 'tools/call')) { Refuse-Mcp 'method_not_allowed' }
    if ($method -ceq 'notifications/initialized') { if (@($Request.Keys) -ccontains 'id') { Refuse-Mcp 'request_shape' } }
    else { $id = Get-McpField $Request 'id'; if ($id -isnot [string] -or $id -cnotmatch '^[A-Za-z0-9._:-]{1,64}$') { Refuse-Mcp 'request_shape' } }
    $tool = $null
    if ($method -ceq 'initialize') {
        $params = Get-McpField $Request 'params'; $client = Get-McpField $params 'clientInfo'
        if ((Get-McpField $params 'protocolVersion') -cne '2025-11-25' -or
            (Get-McpField $client 'name') -cne 'agentbroker-collaboration-harness') { Refuse-Mcp 'harness_identity_required' }
    }
    if ($method -ceq 'tools/call') {
        $params = Get-McpField $Request 'params'; $tool = Get-McpField $params 'name'; $args = Get-McpField $params 'arguments'
        if ($tool -cnotin @('register_agent', 'list_sessions', 'read_inbox', 'send', 'send_message', 'wait_for_messages', 'wait_for_reply', 'reply_to_message', 'acknowledge_message', 'get_message_receipt')) { Refuse-Mcp 'tool_not_allowed' }
        if ($args -isnot [Collections.IDictionary]) { Refuse-Mcp 'request_shape' }
        if ($tool -ceq 'send_message') { $message = Get-McpField $args 'message'; Confirm-GenericMcpAddress (Get-McpField $message 'sender'); Confirm-GenericMcpAddress (Get-McpField $message 'recipient') }
        if ($tool -ceq 'send') { Confirm-GenericMcpAddress (Get-McpField $args 'sender'); Confirm-GenericMcpAddress (Get-McpField $args 'recipient') }
        if ($tool -cin @('read_inbox', 'wait_for_messages')) { Confirm-GenericMcpAddress (Get-McpField $args 'session') }
        if ($tool -ceq 'acknowledge_message') { Confirm-GenericMcpAddress (Get-McpField $args 'recipient') }
    }
    return [pscustomobject]@{ Method = $method; Tool = $tool; ExecuteAllowed = ($tool -cne 'reply_to_message') }
}
# A wait may block up to the broker's 25-second bound, so its request needs more than the usual 15.
function Get-McpRequestSeconds([string]$Tool) { if ($Tool -cin @('wait_for_messages', 'wait_for_reply')) { return 40 }; return 15 }
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
try {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')); $path = Assert-McpRequestPath $RequestFile $repo; Confirm-McpRequestComponents $path
    $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $bytes = Read-McpBytes $file 131072 ([Threading.CancellationToken]::None) } finally { $file.Dispose() }
    $request = Convert-StrictMcpJson ([Text.UTF8Encoding]::new($false, $true).GetString($bytes)); $validated = Confirm-CollaborationRequest $request
    if (-not $Execute) { [ordered]@{ Status = 'validated_only'; Method = $validated.Method; Tool = $validated.Tool; ExecuteAllowed = $validated.ExecuteAllowed; HttpRequestSent = $false } | ConvertTo-Json -Compress; return }
    if (-not $validated.ExecuteAllowed) { Refuse-Mcp 'implicit_reply_recipient_refused' }
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false; $handler.AllowAutoRedirect = $false; $handler.UseCookies = $false; $handler.UseDefaultCredentials = $false
    $client = [Net.Http.HttpClient]::new($handler); $deadline = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds((Get-McpRequestSeconds $validated.Tool))); $response = $null; $message = $null
    try {
        $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, 'http://127.0.0.1:7432/mcp'); $message.Headers.Add('MCP-Protocol-Version', '2025-11-25'); $message.Headers.Accept.ParseAdd('application/json'); $message.Headers.Accept.ParseAdd('text/event-stream')
        $message.Content = [Net.Http.ByteArrayContent]::new($bytes); $message.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/json')
        $response = $client.SendAsync($message, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { [ordered]@{ Status = 'http_error'; HttpStatus = [int]$response.StatusCode } | ConvertTo-Json -Compress; return }
        if ([int]$response.StatusCode -in @(202, 204) -and $validated.Method -ceq 'notifications/initialized') { [ordered]@{ Status = 'notification_accepted' } | ConvertTo-Json -Compress; return }
        if ($response.Content.Headers.ContentLength -gt 524288) { Refuse-Mcp 'body_limit' }
        $stream = $response.Content.ReadAsStream(); try { $reply = Read-McpBytes $stream 524288 $deadline.Token } finally { $stream.Dispose() }
        Convert-McpReply $reply $response.Content.Headers.ContentType.MediaType ([string]$request['id']) | ConvertTo-Json -Depth 32
    } finally { if ($null -ne $response) { $response.Dispose() }; if ($null -ne $message) { $message.Dispose() }; $deadline.Dispose(); $client.Dispose() }
} catch {
    $code = 'request_refused'; if ($_.Exception.Data.Contains('McpCode')) { $code = [string]$_.Exception.Data['McpCode'] }
    [ordered]@{ Status = 'refused'; Code = $code } | ConvertTo-Json -Compress; exit 1
}
