#requires -Version 7
# Pure in-memory validation/decoder tests. Never execute the helper entry point or HTTP.
$ErrorActionPreference = 'Stop'
$path = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\agentbroker\Invoke-AgentBrokerCollaborationMcp.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Test-AgentBrokerCollaborationMcp.ps1: helper parse failed.' }
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) { . ([scriptblock]::Create($definition.Extent.Text)) }
$checks = 0
function Check([bool]$Value, [string]$Label) { if (-not $Value) { throw ('Test-AgentBrokerCollaborationMcp.ps1: ' + $Label) }; $script:checks++ }
function Reject([scriptblock]$Action, [string]$Label) { $failed = $false; try { $null = & $Action } catch { $failed = $true }; Check $failed $Label }
# A call that must not refuse; a refusal fails the named check with its code instead of ending the run unlabelled.
function Accept([scriptblock]$Action, [string]$Label) {
    $actual = 'accepted'; try { $null = & $Action } catch { $actual = 'unrecognized'; if ($_.Exception.Data.Contains('McpCode')) { $actual = [string]$_.Exception.Data['McpCode'] } }
    Check ($actual -ceq 'accepted') ($Label + ' (' + $actual + ')')
}
# The helper's own refusal code, so a check cannot pass on an unrelated failure.
function RejectCode([scriptblock]$Action, [string]$Code, [string]$Label) {
    $actual = 'no refusal'; try { $null = & $Action } catch { $actual = 'unrecognized'; if ($_.Exception.Data.Contains('McpCode')) { $actual = [string]$_.Exception.Data['McpCode'] } }
    Check ($actual -ceq $Code) ($Label + ' (' + $actual + ')')
}
$init = Convert-StrictMcpJson '{"jsonrpc":"2.0","id":"collab-init","method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"agentbroker-collaboration-harness","version":"1"}}}'
Check ((Confirm-CollaborationRequest $init).ExecuteAllowed) 'fixed harness initialize accepted'
foreach ($bad in @('claude', 'codex', 'other-client')) { $init.params.clientInfo.name = $bad; Reject { Confirm-CollaborationRequest $init } 'client identity impersonation refused' }
$init.params.clientInfo.name = 'agentbroker-collaboration-harness'
foreach ($method in @('tools/list', 'notifications/initialized')) {
    $request = @{ jsonrpc = '2.0'; method = $method }; if ($method -ceq 'tools/list') { $request.id = 'list-1' }
    Check ((Confirm-CollaborationRequest $request).ExecuteAllowed) 'allowlisted metadata method accepted'
}
$address = @{ brokerId = '11111111-1111-4111-8111-111111111111'; sessionId = 'mcp:22222222-2222-4222-8222-222222222222'; incarnation = '33333333-3333-4333-8333-333333333333' }
$send = @{ jsonrpc = '2.0'; id = 'send-1'; method = 'tools/call'; params = @{ name = 'send_message'; arguments = @{ message = @{ sender = $address; recipient = $address } } } }
Check ((Confirm-CollaborationRequest $send).ExecuteAllowed) 'explicit generic sender and recipient accepted'
foreach ($native in @('claude:22222222-2222-4222-8222-222222222222', 'codex:22222222-2222-4222-8222-222222222222', 'mcp:00000000-0000-0000-0000-000000000000', 'mcp:bad')) {
    $send.params.arguments.message.recipient = @{ brokerId = $address.brokerId; sessionId = $native; incarnation = $address.incarnation }
    Reject { Confirm-CollaborationRequest $send } 'native or malformed recipient cannot request wake'
}
$send.params.arguments.message.recipient = $address
$direct = @{ jsonrpc = '2.0'; id = 'send-2'; method = 'tools/call'; params = @{ name = 'send'; arguments = @{ sender = $address; recipient = $address; messageId = '44444444-4444-4444-8444-444444444444'; text = 'synthetic' } } }
Check ((Confirm-CollaborationRequest $direct).ExecuteAllowed) 'send with explicit generic sender and recipient accepted'
foreach ($role in @('sender', 'recipient')) {
    foreach ($native in @('claude:22222222-2222-4222-8222-222222222222', 'codex:22222222-2222-4222-8222-222222222222', 'mcp:00000000-0000-0000-0000-000000000000', 'mcp:bad')) {
        $direct.params.arguments[$role] = @{ brokerId = $address.brokerId; sessionId = $native; incarnation = $address.incarnation }
        Reject { Confirm-CollaborationRequest $direct } 'send refuses a native or malformed sender or recipient without CODEX_THREAD_ID'
    }
    $direct.params.arguments[$role] = $address
}
# ---- N-agentbroker-helper-native-sender-01 (AgentBroker 1.6.0): send's sender may be the caller's own codex: row ----
$thread = '5a5b5c5d-5555-4555-8555-55555555555e'
$own = @{ brokerId = $address.brokerId; sessionId = 'codex:' + $thread; incarnation = '66666666-6666-4666-8666-666666666666' }
$nativeSend = @{ jsonrpc = '2.0'; id = 'send-3'; method = 'tools/call'; params = @{ name = 'send'; arguments = @{ sender = $own; recipient = $address; messageId = '77777777-7777-4777-8777-777777777777'; text = 'synthetic' } } }
$nativeValidated = Confirm-CollaborationRequest $nativeSend $thread
Check ($nativeValidated.ExecuteAllowed -and $null -ne $nativeValidated.NativeSender -and $nativeValidated.NativeSender.sessionId -ceq ('codex:' + $thread) -and
    $null -eq $nativeValidated.OriginalMessageId) 'NATIVE_SENDER_OWN: send as codex:<CODEX_THREAD_ID> passes validation and is marked for the row check'
Check ($null -eq (Confirm-CollaborationRequest $direct $thread).NativeSender) 'NATIVE_SENDER_OWN: WITNESS a generic sender with CODEX_THREAD_ID set needs no row check'
# Parenthesized: in an array literal the comma binds tighter than +.
foreach ($threadValue in @('88888888-8888-4888-8888-888888888888', $null, '', '00000000-0000-0000-0000-000000000000', ($thread.ToUpperInvariant()),
        ('{' + $thread + '}'), ($thread.Replace('-', '')), (' ' + $thread), ($thread + ' '))) {
    RejectCode { Confirm-CollaborationRequest $nativeSend $threadValue } 'native_sender_refused' 'NATIVE_SENDER_ENV: another thread, or a missing, empty, zero or non-canonical CODEX_THREAD_ID, refuses'
}
foreach ($sender in @(
        @{ brokerId = $address.brokerId; sessionId = 'codex:' + $thread.ToUpperInvariant(); incarnation = $own.incarnation },
        @{ brokerId = $address.brokerId; sessionId = 'codex: ' + $thread; incarnation = $own.incarnation },
        @{ brokerId = 'bad'; sessionId = $own.sessionId; incarnation = $own.incarnation },
        @{ brokerId = $address.brokerId; sessionId = $own.sessionId; incarnation = '00000000-0000-0000-0000-000000000000' },
        @{ sessionId = $own.sessionId; incarnation = $own.incarnation })) {
    $nativeSend.params.arguments.sender = $sender
    Reject { Confirm-CollaborationRequest $nativeSend $thread } 'NATIVE_SENDER_SHAPE: a non-canonical own session ID, or a malformed or missing broker ID or incarnation, refuses'
}
$nativeSend.params.arguments.sender = @{ brokerId = $address.brokerId; sessionId = 'codex:00000000-0000-0000-0000-000000000000'; incarnation = $own.incarnation }
RejectCode { Confirm-CollaborationRequest $nativeSend '00000000-0000-0000-0000-000000000000' } 'native_sender_refused' 'NATIVE_SENDER_ENV: the zero thread refuses even when the sender names it'
$nativeSend.params.arguments.sender = @{ brokerId = $address.brokerId; sessionId = 'claude:' + $thread; incarnation = $own.incarnation }
RejectCode { Confirm-CollaborationRequest $nativeSend $thread } 'generic_address_required' 'NATIVE_SENDER_CLAUDE: a claude: sender is never admitted'
$nativeSend.params.arguments.sender = $own
foreach ($recipient in @($own, @{ brokerId = $address.brokerId; sessionId = 'claude:' + $thread; incarnation = $own.incarnation })) {
    $nativeSend.params.arguments.recipient = $recipient
    RejectCode { Confirm-CollaborationRequest $nativeSend $thread } 'generic_address_required' 'NATIVE_RECIPIENT: a native recipient stays refused, the own row included'
}
$nativeSend.params.arguments.recipient = $address
$rawNative = @{ jsonrpc = '2.0'; id = 'send-4'; method = 'tools/call'; params = @{ name = 'send_message'; arguments = @{ message = @{ sender = $own; recipient = $address } } } }
RejectCode { Confirm-CollaborationRequest $rawNative $thread } 'generic_address_required' 'NATIVE_RAW: send_message stays generic-only for the own native row'
foreach ($tool in @('read_inbox', 'wait_for_messages', 'acknowledge_message')) {
    $request = @{ jsonrpc = '2.0'; id = 'tool-2'; method = 'tools/call'; params = @{ name = $tool; arguments = @{ session = $own; recipient = $own } } }
    RejectCode { Confirm-CollaborationRequest $request $thread } 'generic_address_required' 'NATIVE_INBOX: read_inbox, wait_for_messages and acknowledge_message on the own native row stay refused'
}
# The row check. A fake directory serves list_sessions pages of 16 and records each cursor.
$leaseAhead = [DateTimeOffset]::UtcNow.AddSeconds(30).ToString('o'); $leasePast = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('o')
function New-Row([string]$Session, [string]$Incarnation = $own.incarnation, $Capabilities = 'Receive, WakeIdle, Reply, NativeIdleGuard', $Lease = $leaseAhead,
    [string]$Broker = $address.brokerId, [string]$Adapter = 'codex-native') {
    [ordered]@{ address = [ordered]@{ brokerId = $Broker; sessionId = $Session; incarnation = $Incarnation }; label = 'synthetic'; adapter = $Adapter
        capabilities = $Capabilities; activity = 'Idle'; leaseUntil = $Lease; isPrivate = $false }
}
function New-Filler([int]$Count) { @(for ($i = 0; $i -lt $Count; $i++) { New-Row ('mcp:' + [guid]::new($i + 1, 0, 0, [byte[]]::new(8)).ToString('D')) -Capabilities 'Receive, Reply' -Adapter 'mcp' }) }
function New-Directory([object[]]$Rows) {
    $state = @{ Rows = @($Rows); Cursors = [Collections.Generic.List[object]]::new(); Tools = [Collections.Generic.List[string]]::new() }
    $invoke = {
        param([string]$Tool, $Arguments)
        $state.Tools.Add($Tool); $start = 16 * $state.Cursors.Count
        $state.Cursors.Add($(if ($Arguments.Contains('afterAddress')) { $Arguments['afterAddress'] } else { $null }))
        if ($Arguments['limit'] -ne 16) { throw 'fake directory: unexpected limit' }
        for ($i = $start; $i -lt [Math]::Min($start + 16, $state.Rows.Count); $i++) { $state.Rows[$i] }
    }.GetNewClosure()
    return @{ State = $state; Invoke = $invoke }
}
$directory = New-Directory (@(New-Filler 20) + @(New-Row $own.sessionId))
Accept { Confirm-NativeSenderRow $own $directory.Invoke ([DateTime]::UtcNow) } 'NATIVE_SENDER_PAGES: the own row on the second page is accepted'
Check ($directory.State.Cursors.Count -eq 2 -and $null -eq $directory.State.Cursors[0] -and
    $directory.State.Cursors[1].sessionId -ceq $directory.State.Rows[15].address.sessionId -and
    @($directory.State.Tools | Where-Object { $_ -cne 'list_sessions' }).Count -eq 0) 'NATIVE_SENDER_PAGES: the own row on the second list_sessions page is found by following the last address'
$directory = New-Directory (@(New-Filler 16))
RejectCode { Confirm-NativeSenderRow $own $directory.Invoke ([DateTime]::UtcNow) } 'native_sender_not_found' 'NATIVE_SENDER_ABSENT: no row with the own session ID refuses'
Check ($directory.State.Cursors.Count -eq 2) 'NATIVE_SENDER_PAGES: a full page is always followed by one more request'
foreach ($case in @(
        @{ Rows = @(New-Row $own.sessionId; New-Row $own.sessionId -Broker '99999999-9999-4999-8999-999999999999'); Code = 'native_sender_ambiguous'; Label = 'NATIVE_SENDER_AMBIGUOUS: a paired broker advertising the same session ID makes it ambiguous' },
        @{ Rows = @(New-Row $own.sessionId; New-Row $own.sessionId -Incarnation '12121212-1212-4212-8212-121212121212'); Code = 'native_sender_ambiguous'; Label = 'NATIVE_SENDER_AMBIGUOUS: two incarnations of the own session refuse' },
        @{ Rows = @(New-Row $own.sessionId -Incarnation '12121212-1212-4212-8212-121212121212'); Code = 'native_sender_stale_incarnation'; Label = 'NATIVE_SENDER_STALE: the request names an incarnation the row no longer has' },
        @{ Rows = @(New-Row $own.sessionId -Broker '99999999-9999-4999-8999-999999999999'); Code = 'native_sender_other_broker'; Label = 'NATIVE_SENDER_BROKER: the only matching row is on another broker' },
        @{ Rows = @(New-Row $own.sessionId -Capabilities 'None'); Code = 'native_sender_observation_only'; Label = 'NATIVE_SENDER_OBSERVATION: an observation-only row (no capabilities) refuses' },
        @{ Rows = @(New-Row $own.sessionId -Capabilities 0); Code = 'native_sender_observation_only'; Label = 'NATIVE_SENDER_OBSERVATION: an observation-only row (numeric 0) refuses' },
        @{ Rows = @(New-Row $own.sessionId -Capabilities 'Receive, Reply'); Code = 'native_sender_observation_only'; Label = 'NATIVE_SENDER_OBSERVATION: Receive without WakeIdle refuses' },
        @{ Rows = @(New-Row $own.sessionId -Capabilities 'Receive, WakeIdle, Teleport'); Code = 'native_sender_observation_only'; Label = 'NATIVE_SENDER_OBSERVATION: an unknown capability name refuses' },
        @{ Rows = @(New-Row $own.sessionId -Lease $leasePast); Code = 'native_sender_lease_lapsed'; Label = 'NATIVE_SENDER_LEASE: a row whose lease lapsed refuses' },
        @{ Rows = @(New-Row $own.sessionId -Lease '2099-01-01T00:00:00'); Code = 'native_sender_lease_lapsed'; Label = 'NATIVE_SENDER_LEASE: a lease with no offset refuses' },
        @{ Rows = @(New-Row $own.sessionId -Adapter 'mcp'); Code = 'native_sender_not_codex'; Label = 'NATIVE_SENDER_ADAPTER: a codex: row from another adapter refuses' },
        @{ Rows = @(New-Row $own.sessionId; New-Row $own.sessionId); Code = 'native_sender_directory_inconsistent'; Label = 'NATIVE_SENDER_DIRECTORY: a repeated address refuses' })) {
    $directory = New-Directory $case.Rows
    RejectCode { Confirm-NativeSenderRow $own $directory.Invoke ([DateTime]::UtcNow) } $case.Code $case.Label
}
$endless = { param([string]$Tool, $Arguments) for ($i = 0; $i -lt 16; $i++) { New-Row ('mcp:' + [guid]::NewGuid().ToString('D')) -Adapter 'mcp' } }
RejectCode { Confirm-NativeSenderRow $own $endless ([DateTime]::UtcNow) } 'native_sender_directory_limit' 'NATIVE_SENDER_DIRECTORY: an endless directory stops at the page bound'
foreach ($capabilities in @(15, 'WakeIdle, Receive')) {
    $directory = New-Directory @(New-Row $own.sessionId -Capabilities $capabilities)
    Accept { Confirm-NativeSenderRow $own $directory.Invoke ([DateTime]::UtcNow) } 'NATIVE_SENDER_OWN: WITNESS numeric or reordered capabilities with Receive and WakeIdle pass'
}
# The broker's JSON text, decoded the way Execute decodes it (ConvertFrom-Json turns leaseUntil into a DateTime).
$pageText = ConvertTo-Json -InputObject @(New-Row $own.sessionId) -Depth 8 -Compress
$decodedRows = @(Get-McpToolValue @{ content = @(@{ type = 'text'; text = $pageText }) })
Check ($decodedRows.Count -eq 1 -and $decodedRows[0].leaseUntil -is [DateTime]) 'NATIVE_SENDER_DECODE: a one-row page decodes to one row with a DateTime lease'
Accept { Confirm-NativeSenderRow $own { param([string]$Tool, $Arguments) $decodedRows } ([DateTime]::UtcNow) } 'NATIVE_SENDER_DECODE: a row decoded from the broker JSON text passes the row check'
$structuredRows = @(Get-McpToolValue @{ structuredContent = @{ result = @((New-Row $own.sessionId), (New-Row 'mcp:00000000-0000-4000-8000-000000000001')) } })
Check ($structuredRows.Count -eq 2) 'NATIVE_SENDER_DECODE: a structured result array decodes to its rows'
Check (@(Get-McpToolValue @{ content = @() }).Count -eq 0) 'NATIVE_SENDER_DECODE: an empty content list decodes to nothing'
RejectCode { Get-McpToolValue @{ isError = $true; content = @(@{ type = 'text'; text = 'DO_NOT_EXPORT' }) } } 'lookup_failed' 'NATIVE_SENDER_DECODE: a tool error refuses'
# wait_for_reply and wait_for_receipt: the original's sender must be a generic mailbox.
$messageId = '44444444-4444-4444-8444-444444444444'
$receiptOf = { param($Sender, [string]$Id = $messageId) [ordered]@{ message = [ordered]@{ id = $Id; sender = $Sender; recipient = $address }; status = 'Accepted' } }
RejectCode { Confirm-GenericOriginalSender $messageId { param([string]$Tool, $Arguments) & $receiptOf $own } } 'native_wait_refused' 'NATIVE_WAIT: a wait on a message a native session sent is refused'
RejectCode { Confirm-GenericOriginalSender $messageId { param([string]$Tool, $Arguments) & $receiptOf @{ brokerId = $address.brokerId; sessionId = 'claude:' + $thread; incarnation = $own.incarnation } } } 'native_wait_refused' 'NATIVE_WAIT: a wait on a message a claude: session sent is refused'
RejectCode { Confirm-GenericOriginalSender $messageId { param([string]$Tool, $Arguments) } } 'original_message_unavailable' 'NATIVE_WAIT: no receipt for the message refuses'
RejectCode { Confirm-GenericOriginalSender $messageId { param([string]$Tool, $Arguments) & $receiptOf $address '45454545-4545-4545-8545-454545454545' } } 'original_message_unavailable' 'NATIVE_WAIT: a receipt for another message refuses'
$asked = [Collections.Generic.List[string]]::new()
Accept { Confirm-GenericOriginalSender $messageId { param([string]$Tool, $Arguments) $asked.Add($Tool + ':' + $Arguments['messageId']); & $receiptOf $address } } 'NATIVE_WAIT: WITNESS a generic original sender is accepted'
Check ($asked.Count -eq 1 -and $asked[0] -ceq ('get_message_receipt:' + $messageId)) 'NATIVE_WAIT: WITNESS a generic original sender passes after one get_message_receipt for that message'
foreach ($waitTool in @('wait_for_reply', 'wait_for_receipt')) {
    $wait = @{ jsonrpc = '2.0'; id = 'wait-4'; method = 'tools/call'; params = @{ name = $waitTool; arguments = @{ messageId = $messageId.ToUpperInvariant() } } }
    Check ((Confirm-CollaborationRequest $wait $thread).OriginalMessageId -ceq $messageId) 'NATIVE_WAIT: both message-ID waits carry the canonical original message ID to the lookup'
    foreach ($bad in @('not-a-guid', '00000000-0000-0000-0000-000000000000', 7)) {
        $wait.params.arguments.messageId = $bad
        RejectCode { Confirm-CollaborationRequest $wait $thread } 'request_shape' 'NATIVE_WAIT: a missing, malformed or zero message ID refuses before any lookup'
    }
}
# Execute's order (Invoke-CheckedRequest): the lookups first, and nothing is posted when one refuses.
$events = [Collections.Generic.List[string]]::new()
$post = { $events.Add('post') }
$ownDirectory = New-Directory @(New-Row $own.sessionId)
Accept { Invoke-CheckedRequest $nativeValidated { param([string]$Tool, $Arguments) $events.Add($Tool); & $ownDirectory.Invoke $Tool $Arguments } $post } 'NATIVE_EXECUTE: a native send with its own row is accepted'
Check (($events -join ',') -ceq 'list_sessions,post') 'NATIVE_EXECUTE: a native send reads the directory, then posts the request once'
$events.Clear(); $emptyDirectory = New-Directory @()
RejectCode { Invoke-CheckedRequest $nativeValidated { param([string]$Tool, $Arguments) $events.Add($Tool); & $emptyDirectory.Invoke $Tool $Arguments } $post } 'native_sender_not_found' 'NATIVE_EXECUTE: a native send with no own row refuses'
Check (($events -join ',') -ceq 'list_sessions') 'NATIVE_EXECUTE: a refused native sender posts nothing'
$waitValidated = Confirm-CollaborationRequest @{ jsonrpc = '2.0'; id = 'wait-5'; method = 'tools/call'; params = @{ name = 'wait_for_reply'; arguments = @{ messageId = $messageId } } } $thread
$events.Clear()
RejectCode { Invoke-CheckedRequest $waitValidated { param([string]$Tool, $Arguments) $events.Add($Tool); & $receiptOf $own } $post } 'native_wait_refused' 'NATIVE_EXECUTE: wait_for_reply on a native-sent message refuses'
Check (($events -join ',') -ceq 'get_message_receipt') 'NATIVE_EXECUTE: a refused wait posts nothing'
$events.Clear()
Accept { Invoke-CheckedRequest $waitValidated { param([string]$Tool, $Arguments) $events.Add($Tool); & $receiptOf $address } $post } 'NATIVE_EXECUTE: WITNESS a wait on a generic-sent message is accepted'
Check (($events -join ',') -ceq 'get_message_receipt,post') 'NATIVE_EXECUTE: WITNESS a wait on a generic-sent message posts after its lookup'
$events.Clear()
Accept { Invoke-CheckedRequest (Confirm-CollaborationRequest $direct $thread) { param([string]$Tool, $Arguments) $events.Add($Tool) } $post } 'NATIVE_EXECUTE: WITNESS a generic send is accepted'
Check (($events -join ',') -ceq 'post') 'NATIVE_EXECUTE: WITNESS a generic send posts with no lookup'
$waitRequest = @{ jsonrpc = '2.0'; id = 'wait-1'; method = 'tools/call'; params = @{ name = 'wait_for_messages'; arguments = @{ session = $address; timeoutSeconds = 20 } } }
Check ((Confirm-CollaborationRequest $waitRequest).ExecuteAllowed) 'wait_for_messages on a generic session accepted'
foreach ($native in @('claude:22222222-2222-4222-8222-222222222222', 'mcp:bad')) {
    $waitRequest.params.arguments.session = @{ brokerId = $address.brokerId; sessionId = $native; incarnation = $address.incarnation }
    Reject { Confirm-CollaborationRequest $waitRequest } 'wait_for_messages refuses a native or malformed session'
}
$replyWait = @{ jsonrpc = '2.0'; id = 'wait-2'; method = 'tools/call'; params = @{ name = 'wait_for_reply'; arguments = @{ messageId = '44444444-4444-4444-8444-444444444444' } } }
Check ((Confirm-CollaborationRequest $replyWait).ExecuteAllowed) 'wait_for_reply by message ID accepted'
$receiptWait = @{ jsonrpc = '2.0'; id = 'wait-3'; method = 'tools/call'; params = @{ name = 'wait_for_receipt'; arguments = @{ messageId = '44444444-4444-4444-8444-444444444444' } } }
Check ((Confirm-CollaborationRequest $receiptWait).ExecuteAllowed) 'wait_for_receipt by message ID accepted'
$whoami = @{ jsonrpc = '2.0'; id = 'who-1'; method = 'tools/call'; params = @{ name = 'whoami'; arguments = @{} } }
Check ((Confirm-CollaborationRequest $whoami).ExecuteAllowed -and (Get-McpRequestSeconds 'whoami') -eq 15) 'whoami accepted with no arguments and the 15-second deadline'
Check ((Get-McpRequestSeconds 'wait_for_messages') -eq 40 -and (Get-McpRequestSeconds 'wait_for_reply') -eq 40 -and
    (Get-McpRequestSeconds 'wait_for_receipt') -eq 40) 'the three wait tools get a 40-second request deadline'
Check ((Get-McpRequestSeconds 'read_inbox') -eq 15 -and (Get-McpRequestSeconds 'send') -eq 15 -and (Get-McpRequestSeconds '') -eq 15) 'every other request keeps the 15-second deadline'
foreach ($tool in @('register_agent', 'list_sessions', 'read_inbox', 'wait_for_messages', 'acknowledge_message', 'get_message_receipt', 'reply_to_message')) {
    $request = @{ jsonrpc = '2.0'; id = 'tool-1'; method = 'tools/call'; params = @{ name = $tool; arguments = @{ session = $address; recipient = $address } } }
    $validated = Confirm-CollaborationRequest $request
    Check ($validated.ExecuteAllowed -eq ($tool -cne 'reply_to_message')) 'implicit reply recipient is refused live; other generic tools allowed'
}
$send.params.name = 'native_submit'; Reject { Confirm-CollaborationRequest $send } 'nonallowlisted tool refused'; $send.params.name = 'send_message'
$send.method = 'arbitrary/rpc'; Reject { Confirm-CollaborationRequest $send } 'nonallowlisted RPC refused'; $send.method = 'tools/call'
Reject { Convert-StrictMcpJson '{"recipient":{},"Recipient":{}}' } 'case-alias duplicate keys refused'
Reject { Convert-StrictMcpJson '{"id":"one","id":"two"}' } 'identical duplicate keys refused'
$root = 'C:\SyntheticWorkspace'
Check ((Assert-McpRequestPath 'C:\SyntheticWorkspace\artifacts\request.json' $root) -ceq 'C:\SyntheticWorkspace\artifacts\request.json') 'ordinary workspace JSON path accepted lexically'
foreach ($symbolic in @('C:\elsewhere\request.json', 'C:\SyntheticWorkspace\.env.json', 'C:\SyntheticWorkspace\privatekey-fixture\request.json', 'C:\SyntheticWorkspace\.git\credentials\request.json', '\\server\share\request.json')) {
    # Strings only, no protected-looking path is created or accessed.
    Reject { Assert-McpRequestPath $symbolic $root } 'outside/protected/remote symbolic path refused before access'
}
$utf8 = [Text.UTF8Encoding]::new($false)
$reply = '{"jsonrpc":"2.0","id":"tool-1","result":{"content":[{"type":"text","text":"synthetic peer collaboration data"}]}}'
$decoded = Convert-McpReply ($utf8.GetBytes($reply)) 'application/json' 'tool-1'
Check ($decoded.Status -ceq 'mcp_result' -and $decoded.Result.content[0].text -ceq 'synthetic peer collaboration data') 'valid bounded peer result preserved as data'
$sse = "event: message`ndata: " + $reply + "`n`n"
Check ((Convert-McpReply ($utf8.GetBytes($sse)) 'text/event-stream' 'tool-1').Status -ceq 'mcp_result') 'SSE result decoded without raw frames'
Reject { Convert-McpReply ($utf8.GetBytes($reply)) 'application/json' 'wrong-id' } 'wrong response ID refused'
Reject { Convert-McpReply ($utf8.GetBytes($reply)) 'text/plain' 'tool-1' } 'unknown response type refused'
Reject { Convert-McpReply ([byte[]]::new(524289)) 'application/json' 'tool-1' } 'response byte cap enforced'
$errorReply = '{"jsonrpc":"2.0","id":"tool-1","error":{"code":-32601,"message":"DO_NOT_EXPORT_RAW_ERROR_OR_PATH"}}'
$errorResult = Convert-McpReply ($utf8.GetBytes($errorReply)) 'application/json' 'tool-1'
Check ($errorResult.ErrorCode -eq -32601 -and ($errorResult | ConvertTo-Json -Compress) -notmatch 'DO_NOT_EXPORT') 'RPC error code only, no raw error body'
$memory = [IO.MemoryStream]::new([byte[]]::new(9))
try { Reject { Read-McpBytes $memory 8 ([Threading.CancellationToken]::None) } 'bounded stream reader refuses overrun' } finally { $memory.Dispose() }
Write-Output ('PASS: ' + $checks + ' in-memory collaboration MCP checks; no HTTP, client files, GUI, IPC, credentials or settings accessed.')
