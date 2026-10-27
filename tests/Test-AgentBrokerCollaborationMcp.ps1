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
        Reject { Confirm-CollaborationRequest $direct } 'send refuses a native or malformed sender or recipient'
    }
    $direct.params.arguments[$role] = $address
}
$waitRequest = @{ jsonrpc = '2.0'; id = 'wait-1'; method = 'tools/call'; params = @{ name = 'wait_for_messages'; arguments = @{ session = $address; timeoutSeconds = 20 } } }
Check ((Confirm-CollaborationRequest $waitRequest).ExecuteAllowed) 'wait_for_messages on a generic session accepted'
foreach ($native in @('claude:22222222-2222-4222-8222-222222222222', 'mcp:bad')) {
    $waitRequest.params.arguments.session = @{ brokerId = $address.brokerId; sessionId = $native; incarnation = $address.incarnation }
    Reject { Confirm-CollaborationRequest $waitRequest } 'wait_for_messages refuses a native or malformed session'
}
$replyWait = @{ jsonrpc = '2.0'; id = 'wait-2'; method = 'tools/call'; params = @{ name = 'wait_for_reply'; arguments = @{ messageId = '44444444-4444-4444-8444-444444444444' } } }
Check ((Confirm-CollaborationRequest $replyWait).ExecuteAllowed) 'wait_for_reply by message ID accepted'
Check ((Get-McpRequestSeconds 'wait_for_messages') -eq 40 -and (Get-McpRequestSeconds 'wait_for_reply') -eq 40) 'the two wait tools get a 40-second request deadline'
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
