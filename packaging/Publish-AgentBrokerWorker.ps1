#requires -Version 7
<#
.SYNOPSIS
    Publish the AgentBroker worker as a self-contained Windows x64 module payload.
.DESCRIPTION
    Called by AgentBroker.csproj after Build. It needs the pinned .NET SDK only on the build
    machine; the installed module needs no ASP.NET runtime. It never installs, starts, or stops
    the worker. A complete validated publish replaces only the module's worker directory.
.EXAMPLE
    pwsh -NoProfile -File packaging/Publish-AgentBrokerWorker.ps1 -Configuration Release
#>
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'StagingPathSafety.ps1')
$buildRoot = Join-Path $repoRoot 'build'
$stagingRoot = Join-Path $buildRoot 'AgentBrokerWorker'
$moduleOutput = Join-Path $buildRoot "DesktopAICompanionPortable\bin\$Configuration\x64\modules\agentbroker"
$destination = Join-Path $moduleOutput 'worker'
$nonce = [guid]::NewGuid().ToString('N')
$staging = Join-Path $stagingRoot ("publish-$nonce")
$backup = Join-Path $moduleOutput ("worker.previous-$nonce")
$project = Join-Path $repoRoot 'tools\AgentBroker.Worker\AgentBroker.Worker.csproj'

foreach ($path in @($staging, $destination, $backup)) {
    if (-not (Test-DesktopAICompanionPathWithin -Path $path -Root $buildRoot)) {
        throw 'The worker publish target must stay inside the repository build directory.'
    }
    [void](Assert-DesktopAICompanionPathChainSafe -Path $path -TrustedRoot $repoRoot)
}
[void](Assert-DesktopAICompanionPathChainSafe -Path $project -TrustedRoot $repoRoot)
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$previousMoved = $false
$installed = $false
try {
    & dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
        --artifacts-path (Join-Path $stagingRoot 'compile') `
        --nologo -v:minimal -o $staging '-p:RestoreLockedMode=true' `
        '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' `
        '-p:DebugType=None' '-p:DebugSymbols=false' ("-p:PathMap=$repoRoot=/_/")
    if ($LASTEXITCODE -ne 0) { throw "AgentBroker worker publish failed (exit $LASTEXITCODE)." }

    $required = @('AgentBroker.Worker.exe', 'AgentBroker.Worker.dll',
                  'AgentBroker.Worker.deps.json', 'AgentBroker.Worker.runtimeconfig.json',
                  'AgentBroker.Core.dll', 'AgentBroker.CodexDesktopAdapter.dll',
                  'AgentBroker.ClaudeAdapter.dll', 'coreclr.dll', 'hostfxr.dll',
                  'Microsoft.AspNetCore.Server.Kestrel.Core.dll')
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $name) -PathType Leaf)) {
            throw "The self-contained worker publish is missing $name."
        }
    }
    $config = Get-Content -LiteralPath (Join-Path $staging 'AgentBroker.Worker.runtimeconfig.json') -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $runtime = $config.runtimeOptions
    $included = @($runtime.includedFrameworks | ForEach-Object { $_.name })
    if ($runtime.PSObject.Properties['framework'] -or $runtime.PSObject.Properties['frameworks'] -or
        $included -notcontains 'Microsoft.NETCore.App' -or $included -notcontains 'Microsoft.AspNetCore.App') {
        throw 'The worker publish still requires a machine-installed shared framework.'
    }

    $noticeSource = Join-Path $PSScriptRoot 'AgentBrokerNotices'
    $noticeVersions = Get-Content -LiteralPath (Join-Path $noticeSource 'versions.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $frameworks = @($runtime.includedFrameworks)
    $netVersion = [string](@($frameworks | Where-Object { $_.name -ceq 'Microsoft.NETCore.App' })[0].version)
    $aspVersion = [string](@($frameworks | Where-Object { $_.name -ceq 'Microsoft.AspNetCore.App' })[0].version)
    $packageLock = Get-Content -LiteralPath (Join-Path (Split-Path $project -Parent) 'packages.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $dependencies = $packageLock.dependencies.'net10.0-windows7.0'
    if ($noticeVersions.dotnetRuntime -cne $netVersion -or $noticeVersions.aspnetRuntime -cne $aspVersion -or
        $noticeVersions.modelContextProtocol -cne $dependencies.'ModelContextProtocol.AspNetCore'.resolved -or
        $noticeVersions.extensionsAI -cne $dependencies.'Microsoft.Extensions.AI.Abstractions'.resolved) {
        throw 'Redistribution notices do not match the published dependencies. Run packaging/Update-AgentBrokerNotices.ps1 after reviewing upstream licenses.'
    }
    $noticeOutput = Join-Path $staging 'licenses'
    New-Item -ItemType Directory -Path $noticeOutput | Out-Null
    foreach ($name in @('THIRD-PARTY-NOTICES.txt', 'versions.json', 'mcp-sdk-license.txt',
                        'microsoft-extensions-ai-license.txt', 'dotnet-license.txt',
                        'dotnet-third-party-notices.txt', 'aspnetcore-license.txt',
                        'aspnetcore-third-party-notices.txt')) {
        $source = Join-Path $noticeSource $name
        [void](Assert-DesktopAICompanionPathChainSafe -Path $source -TrustedRoot $repoRoot)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
            throw "A required redistribution notice is missing or empty: $name"
        }
        $noticeBytes = [IO.File]::ReadAllBytes($source)
        if ($noticeBytes -contains [byte]13 -or ($noticeBytes.Length -ge 3 -and
            $noticeBytes[0] -eq 0xEF -and $noticeBytes[1] -eq 0xBB -and $noticeBytes[2] -eq 0xBF)) {
            throw 'Redistribution notices must use committed UTF-8 without BOM and LF bytes. Run packaging/Update-AgentBrokerNotices.ps1 -NormalizeOnly.'
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $noticeOutput $name)
    }

    # Check paths before moving or packaging any output. The publish project copies no user data.
    $entries = @(Get-ChildItem -LiteralPath $staging -Recurse -Force)
    foreach ($entry in $entries) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The worker publish contains a reparse point.'
        }
        if (-not $entry.PSIsContainer -and
            ($entry.Name -match '(?i)(?:\.env$|^\.env\.|^\.npmrc$|\.(?:pem|key|p12|pfx|keystore)$|^privatekey)')) {
            throw 'The worker publish contains an unexpected protected credential filename.'
        }
    }
    $files = @($entries | Where-Object { -not $_.PSIsContainer })
    $bytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
    if ($files.Count -lt $required.Count -or $bytes -gt 300MB) {
        throw 'The worker publish is empty or exceeds the supported 300 MiB expanded payload.'
    }

    New-Item -ItemType Directory -Path $moduleOutput -Force | Out-Null
    foreach ($path in @($staging, $destination, $backup)) {
        [void](Assert-DesktopAICompanionPathChainSafe -Path $path -TrustedRoot $repoRoot)
    }
    if (Test-Path -LiteralPath $destination) {
        Move-Item -LiteralPath $destination -Destination $backup
        $previousMoved = $true
    }
    try {
        Move-Item -LiteralPath $staging -Destination $destination
        $installed = $true
    }
    catch {
        if ($previousMoved) {
            Move-Item -LiteralPath $backup -Destination $destination
            $previousMoved = $false
        }
        throw
    }
    Write-Host ("AgentBroker worker payload: {0} files, {1:N0} bytes, self-contained win-x64." -f $files.Count, $bytes)
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-DesktopAICompanionSafeDirectory -Path $staging -AllowedRoot $stagingRoot -TrustedRoot $repoRoot
    }
    if ($installed -and $previousMoved) {
        Remove-DesktopAICompanionSafeDirectory -Path $backup -AllowedRoot $moduleOutput -TrustedRoot $repoRoot
    }
}
