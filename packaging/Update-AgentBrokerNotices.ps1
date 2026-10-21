#requires -Version 7
<#
.SYNOPSIS
    Refresh checked-in redistribution notices from the exact published runtime and package sources.
.EXAMPLE
    pwsh -NoProfile -File packaging/Update-AgentBrokerNotices.ps1
#>
[CmdletBinding()]
param([switch]$NormalizeOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$destination = Join-Path $PSScriptRoot 'AgentBrokerNotices'
function Normalize-NoticeFiles {
    foreach ($name in @('THIRD-PARTY-NOTICES.txt', 'versions.json', 'mcp-sdk-license.txt',
                        'microsoft-extensions-ai-license.txt', 'dotnet-license.txt',
                        'dotnet-third-party-notices.txt', 'aspnetcore-license.txt',
                        'aspnetcore-third-party-notices.txt')) {
        $path = Join-Path $destination $name
        $text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))
        [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n").Replace("`r", "`n"), [Text.UTF8Encoding]::new($false))
    }
}
if ($NormalizeOnly) { Normalize-NoticeFiles; Write-Host 'AgentBroker notices normalized to UTF-8 without BOM and LF line endings.'; exit 0 }
$configPath = Join-Path $repoRoot 'build\DesktopAICompanionPortable\bin\Release\x64\modules\agentbroker\worker\AgentBroker.Worker.runtimeconfig.json'
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$frameworks = @($config.runtimeOptions.includedFrameworks)
$netVersion = [string](@($frameworks | Where-Object { $_.name -ceq 'Microsoft.NETCore.App' })[0].version)
$aspVersion = [string](@($frameworks | Where-Object { $_.name -ceq 'Microsoft.AspNetCore.App' })[0].version)
$lock = Get-Content -LiteralPath (Join-Path $repoRoot 'tools\AgentBroker.Worker\packages.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$dependencies = $lock.dependencies.'net10.0-windows7.0'
$mcpVersion = [string]$dependencies.'ModelContextProtocol.AspNetCore'.resolved
$aiVersion = [string]$dependencies.'Microsoft.Extensions.AI.Abstractions'.resolved
if ($mcpVersion -cne '2.2.0' -or $aiVersion -cne '10.8.3') {
    throw 'Review the upstream license source commits before refreshing notices for new package versions.'
}
$packages = $env:NUGET_PACKAGES
if ([string]::IsNullOrWhiteSpace($packages)) { $packages = Join-Path $env:USERPROFILE '.nuget\packages' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($entry in @(
    @{ Package = 'microsoft.netcore.app.runtime.win-x64'; Version = $netVersion; Prefix = 'dotnet' },
    @{ Package = 'microsoft.aspnetcore.app.runtime.win-x64'; Version = $aspVersion; Prefix = 'aspnetcore' }
)) {
    $source = Join-Path (Join-Path $packages $entry.Package) $entry.Version
    foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $destination ($entry.Prefix + '-' + $name.ToLowerInvariant()))
    }
}
$mcpCommit = '6fa3825973949a9c4f0cd8af344e15a8db09dc35'
& curl.exe --fail --location --silent --show-error --output (Join-Path $destination 'mcp-sdk-license.txt') `
    ("https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/$mcpCommit/LICENSE")
if ($LASTEXITCODE -ne 0) { throw 'Downloading the exact MCP license source failed.' }
$manifest = [ordered]@{
    dotnetRuntime = $netVersion
    aspnetRuntime = $aspVersion
    modelContextProtocol = $mcpVersion
    extensionsAI = $aiVersion
    mcpSourceCommit = $mcpCommit
    extensionsSourceCommit = 'ccb356f31db9d894807c4fd0c97c2f41553d1524'
}
$manifestJson = $manifest | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $destination 'versions.json'), ($manifestJson + "`n"), [Text.UTF8Encoding]::new($false))
$text = @"
AgentBroker worker third-party software notices

Microsoft.NETCore.App $netVersion, Microsoft.AspNetCore.App $aspVersion
Copyright (c) .NET Foundation and Contributors.
Their original MIT license and complete third-party notices are included separately:
dotnet-license.txt, dotnet-third-party-notices.txt,
aspnetcore-license.txt, aspnetcore-third-party-notices.txt.
These files are copied from the exact restored Microsoft runtime packages.

ModelContextProtocol.AspNetCore, ModelContextProtocol, ModelContextProtocol.Core $mcpVersion
Copyright (c) 2024-2026 Model Context Protocol a Series of LF Projects, LLC.
The package metadata declares Apache-2.0. The upstream source's full licensing
transition notice, Apache-2.0 text and retained MIT notice are in mcp-sdk-license.txt.
Source: https://github.com/modelcontextprotocol/csharp-sdk/tree/$mcpCommit
The SDK assemblies are redistributed unchanged. No upstream NOTICE file is present
at this source commit.

Microsoft.Extensions.AI.Abstractions $aiVersion
Copyright (c) Microsoft Corporation. All rights reserved.
The package metadata declares MIT. See microsoft-extensions-ai-license.txt.
Source: https://github.com/dotnet/extensions/tree/ccb356f31db9d894807c4fd0c97c2f41553d1524

Codex and Claude clients are installed separately. Their executables and proprietary
source are not redistributed in the AgentBroker payload.
"@
[IO.File]::WriteAllText((Join-Path $destination 'THIRD-PARTY-NOTICES.txt'), $text, [Text.UTF8Encoding]::new($false))
& curl.exe --fail --location --silent --show-error --output (Join-Path $destination 'microsoft-extensions-ai-license.txt') `
    'https://raw.githubusercontent.com/dotnet/extensions/ccb356f31db9d894807c4fd0c97c2f41553d1524/LICENSE'
if ($LASTEXITCODE -ne 0) { throw 'Downloading the exact Extensions AI license source failed.' }
Normalize-NoticeFiles
Write-Host "AgentBroker notices refreshed for .NET $netVersion, ASP.NET $aspVersion, MCP $mcpVersion and Extensions AI $aiVersion."
