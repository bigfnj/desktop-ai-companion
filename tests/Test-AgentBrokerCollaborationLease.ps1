#requires -Version 7
# Source-only imports and synthetic delegates. No entrypoint, processes, network or sleeps.
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
foreach($relative in @('tools\agentbroker\Invoke-AgentBrokerCollaborationMcp.ps1','tools\agentbroker\Start-AgentBrokerCollaborationLease.ps1')) {
    $tokens=$null; $errors=$null; $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $relative),[ref]$tokens,[ref]$errors)
    if($errors.Count) {throw 'helper_parse_failed'}
    foreach($definition in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)) { . ([scriptblock]::Create($definition.Extent.Text)) }
}
$checks=0
function Check([bool]$Value,[string]$Label) {if(-not $Value){throw $Label};$script:checks++}
function Reject([scriptblock]$Action,[string]$Label) {$failed=$false;try{$null=& $Action}catch{$failed=$true};Check $failed $Label}
$helper=Join-Path $repo 'tools\agentbroker\Invoke-AgentBrokerCollaborationMcp.ps1'
$helperHash='AFDCBB671CA0F0135F9984D0643B12BFED63CBD7C36C0855CE5085C69785CA80'
Import-LeaseGuards
Check ((Get-McpField @{value='actual imported parameter binding'} 'value') -ceq 'actual imported parameter binding') 'actual guard importer retains external function parameter lists'
$hostArguments=@('C:\Users\SyntheticUser','SyntheticUser','S-1-5-21-111-222-333-1001','Core','C:\Program Files\PowerShell\7\pwsh.exe','C:\Program Files\PowerShell\7')
Check ((Confirm-LeaseHostFacts @hostArguments) -ceq $hostArguments[4]) 'normal user host is independent of repository location'
$folded=@($hostArguments); $folded[0]='C:\Users\syntheticuser\'; $folded[4]='c:\program files\powershell\7\PWSH.EXE'
Check ((Confirm-LeaseHostFacts @folded) -ceq $hostArguments[4]) 'Windows identity and executable comparisons allow equivalent path casing'
foreach($serviceSid in @('S-1-5-18','S-1-5-19','S-1-5-20','S-1-5-80-123-456')) {
    $badHost=@($hostArguments); $badHost[2]=$serviceSid
    Reject {Confirm-LeaseHostFacts @badHost} 'service token cannot start a development lease'
}
$badHost=@($hostArguments); $badHost[1]='AnotherUser'
Reject {Confirm-LeaseHostFacts @badHost} 'profile and account name must match'
$badHost=@($hostArguments); $badHost[2]=''
Reject {Confirm-LeaseHostFacts @badHost} 'missing Windows identity is refused'
$badHost=@($hostArguments); $badHost[3]='Desktop'
Reject {Confirm-LeaseHostFacts @badHost} 'legacy PowerShell host cannot execute the lease'
$badHost=@($hostArguments); $badHost[4]='C:\Synthetic\other.exe'
Reject {Confirm-LeaseHostFacts @badHost} 'different process image cannot become the lease host'
$badHost=@($hostArguments); $badHost[0]='relative\SyntheticUser'
Reject {Confirm-LeaseHostFacts @badHost} 'relative profile is refused before access'
$badHost=@($hostArguments); $badHost[0]='\\server\share\SyntheticUser'
Reject {Confirm-LeaseHostFacts @badHost} 'remote profile is refused before access'
function New-Request {
    Convert-StrictMcpJson '{"jsonrpc":"2.0","id":"register-test","method":"tools/call","params":{"name":"register_agent","arguments":{"clientId":"11111111-1111-4111-8111-111111111111","incarnation":"22222222-2222-4222-8222-222222222222","label":"Synthetic test agent","isPrivate":true}}}'
}
$request=New-Request; $registration=Confirm-LeaseRegistration $request
Check ($registration.clientId -ceq $request.params.arguments.clientId) 'exact register request accepted'
foreach($method in @('initialize','tools/list','notifications/initialized')) {$bad=New-Request;$bad.method=$method;Reject {Confirm-LeaseRegistration $bad} 'metadata methods cannot become heartbeat'}
foreach($tool in @('send_message','reply_to_message','read_inbox','acknowledge_message','native_submit')) {$bad=New-Request;$bad.params.name=$tool;Reject {Confirm-LeaseRegistration $bad} 'non-registration tool refused'}
foreach($name in @('clientId','incarnation')) {
    foreach($value in @('00000000-0000-0000-0000-000000000000','bad','{11111111-1111-4111-8111-111111111111}','ABCDEF11-1111-4111-8111-111111111111')) {
        $bad=New-Request;$bad.params.arguments[$name]=$value;Reject {Confirm-LeaseRegistration $bad} 'empty/malformed/noncanonical identity refused'
    }
}
$bad=New-Request;$bad.params.arguments.message=@{};Reject {Confirm-LeaseRegistration $bad} 'extra registration arguments refused'
$bad=New-Request;$bad.params.arbitrary=@{};Reject {Confirm-LeaseRegistration $bad} 'extra tool parameters refused'
$bad=New-Request;$bad.params.arguments.isPrivate='true';Reject {Confirm-LeaseRegistration $bad} 'nonboolean privacy refused'
$bad=New-Request;$bad.params.arguments.label="line`ninput";Reject {Confirm-LeaseRegistration $bad} 'control characters refused'
function New-Session {return @{address=@{brokerId='33333333-3333-4333-8333-333333333333';sessionId='mcp:11111111-1111-4111-8111-111111111111';incarnation=$registration.incarnation};adapter='mcp';label=$registration.label;isPrivate=$true}}
function Encode-Reply($Session) {return (@{Status='mcp_result';Result=@{content=@(@{type='text';text=($Session|ConvertTo-Json -Depth 8 -Compress)})}}|ConvertTo-Json -Depth 12 -Compress)}
$session=New-Session; $broker=$session.address.brokerId
Check ((Confirm-LeaseReply (Encode-Reply $session) $registration $null).Code -ceq 'renewed') 'text session response accepted'
$structured=@{Status='mcp_result';Result=@{structuredContent=$session}}|ConvertTo-Json -Depth 12 -Compress
Check ((Confirm-LeaseReply $structured $registration $broker).Code -ceq 'renewed') 'structured session response accepted'
$both=@{Status='mcp_result';Result=@{structuredContent=$session;content=@(@{type='text';text=($session|ConvertTo-Json -Depth 8 -Compress)})}}
Check ((Confirm-LeaseReply ($both|ConvertTo-Json -Depth 12 -Compress) $registration $broker).Code -ceq 'renewed') 'matching dual response accepted'
$other=New-Session;$other.address.brokerId='44444444-4444-4444-8444-444444444444';$both.Result.structuredContent=$other
Check ((Confirm-LeaseReply ($both|ConvertTo-Json -Depth 12 -Compress) $registration $broker).Code -ceq 'reply_refused') 'disagreeing dual response refused'
foreach($field in @('sessionId','incarnation','brokerId')) {$bad=New-Session;$bad.address[$field]='codex:wrong';Check ((Confirm-LeaseReply (Encode-Reply $bad) $registration $broker).Code -ceq 'reply_refused') 'native or wrong address refused'}
$bad=New-Session;$bad.adapter='codex-native';Check ((Confirm-LeaseReply (Encode-Reply $bad) $registration $broker).Code -ceq 'reply_refused') 'native adapter response refused'
$bad=New-Session;$bad.isPrivate=$false;Check ((Confirm-LeaseReply (Encode-Reply $bad) $registration $broker).Code -ceq 'reply_refused') 'privacy mismatch refused'
$bad=New-Session;$bad.label='Other';Check ((Confirm-LeaseReply (Encode-Reply $bad) $registration $broker).Code -ceq 'reply_refused') 'label mismatch refused'
$errorReply=@{Status='mcp_result';Result=@{isError=$true;content=@(@{type='text';text=($session|ConvertTo-Json -Depth 8 -Compress)})}}|ConvertTo-Json -Depth 12 -Compress
Check ((Confirm-LeaseReply $errorReply $registration $broker).Code -ceq 'reply_refused') 'isError blocks an otherwise valid response'
Check ((Confirm-LeaseReply (Encode-Reply $other) $registration $broker).Code -ceq 'broker_changed') 'broker identity locks after first success'
Check ((Confirm-LeaseReply '{"Status":"http_error","Raw":"MUST_NOT_ESCAPE"}' $registration $broker).Code -ceq 'renewal_failed') 'untrusted failure text reduced to fixed code'
Check ((Confirm-LeaseReply ('x'*524289) $registration $broker).Code -ceq 'reply_refused') 'reply length bounded'
Check ((Confirm-LeaseReply '{}' $registration $broker).Code -ceq 'reply_refused') 'malformed response refused'
$script:fakeSeconds=0.0; $script:calls=[Collections.Generic.List[double]]::new();$script:records=[Collections.Generic.List[object]]::new()
$summary=Invoke-LeaseCycle 900 {param($expected) $script:calls.Add($script:fakeSeconds);if($script:calls.Count -gt 1){Check ($expected -ceq $broker) 'broker lock carried across renewals'};@{Code='renewed';BrokerId=$broker}} {$script:fakeSeconds} {param($milliseconds) $script:fakeSeconds+=$milliseconds/1000.0} {param($record) $script:records.Add($record)}
Check ($summary.Attempts -eq 45 -and $summary.Successes -eq 45 -and $summary.Failures -eq 0) '900-second limit yields at most 45 calls'
Check ($script:calls[0] -eq 0 -and $script:calls[1] -eq 20 -and $script:calls[44] -eq 880) 'renewal cadence exactly20seconds'
Check ($script:records.Count -eq 45 -and @($script:records[0].Keys).Count -eq 5) 'finite health records contain only counts/code/elapsed'
$script:fakeSeconds=0.0
$summary=Invoke-LeaseCycle 20 {throw 'UNTRUSTED_RAW_FAILURE'} {$script:fakeSeconds} {param($milliseconds) $script:fakeSeconds+=$milliseconds/1000.0} {param($record) Check ($record.Code -ceq 'renewal_failed') 'exception reduced to fixed failure'}
Check ($summary.Attempts -eq 1 -and $summary.Failures -eq 1) 'single failed attempt remains bounded'
$script:fakeSeconds=0.0
$summary=Invoke-LeaseCycle 900 {@{Code='broker_changed';BrokerId=$null}} {$script:fakeSeconds} {throw 'unexpected_sleep'} {param($record)}
Check ($summary.Attempts -eq 1 -and $summary.Failures -eq 1) 'broker change stops without restart'
foreach($duration in @(0,19,901,3600)){Reject {Invoke-LeaseCycle $duration {} {} {} {}} 'invalid duration refused'}
$symbolicRoot='C:\SyntheticWorkspace'
Check ((Assert-McpRequestPath 'C:\SyntheticWorkspace\artifacts\registration.json' $symbolicRoot) -ceq 'C:\SyntheticWorkspace\artifacts\registration.json') 'reused guard accepts ordinary workspace JSON'
foreach($symbolicPath in @('C:\elsewhere\registration.json','C:\SyntheticWorkspace\.env.json','C:\SyntheticWorkspace\privatekey-fixture\registration.json','\\server\share\registration.json')) {
    Reject {Assert-McpRequestPath $symbolicPath $symbolicRoot} 'reused path guard rejects protected/outside/remote symbolic strings before access'
}
Write-Output ('PASS: '+$checks+' synthetic collaboration lease checks; no processes, HTTP, client configuration, protected paths or actual sleeps.')
