#requires -Version 7
<#
.SYNOPSIS
Build and run synthetic owner-discovery deadline checks without a live client.
.EXAMPLE
pwsh -NoProfile -File tests/Invoke-DesktopDiscoveryTimeoutChecks.ps1
.EXAMPLE
pwsh -NoProfile -File tests/Invoke-DesktopDiscoveryTimeoutChecks.ps1 -Case default-delay
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'New', 'Existing', 'default-delay', 'method-defaults', 'explicit-deadline', 'outer-cancellation', 'missing-owner')]
    [string]$Case = 'All',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'tests\AgentBroker.Desktop.Tests\AgentBroker.Desktop.Tests.csproj'
$executable = Join-Path $repo 'tests\AgentBroker.Desktop.Tests\bin\Release\net10.0-windows\AgentBroker.Desktop.Tests.exe'
$scratch = Join-Path $repo ('artifacts\discovery-timeouts\' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$config = Join-Path $scratch 'NuGet.Config'
$cachedPackages = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
$fallback = [Security.SecurityElement]::Escape($cachedPackages)
[IO.File]::WriteAllText($config, ('<configuration><packageSources><clear/></packageSources><fallbackPackageFolders><clear/>' +
    '<add key="cached-public-runtime-packs" value="' + $fallback + '"/></fallbackPackageFolders></configuration>'), [Text.UTF8Encoding]::new($false))
$overrides = @{
    DOTNET_CLI_HOME = (Join-Path $scratch 'dotnet-home')
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    MSBUILDDISABLENODEREUSE = '1'
    NUGET_PACKAGES = (Join-Path $scratch 'packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $scratch 'http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $scratch 'plugin-cache')
    NUGET_SCRATCH = (Join-Path $scratch 'nuget-scratch')
}
$previous = @{}
try {
    foreach ($name in $overrides.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $overrides[$name], 'Process')
    }
    if (-not $SkipBuild) {
        & dotnet build $project -c Release --disable-build-servers --nologo -v:minimal '-p:RestoreLockedMode=true' `
            ('-p:RestoreConfigFile=' + $config) '-p:UseSharedCompilation=false' '-nodeReuse:false'
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic desktop timeout checks failed to build.' }
    }
    [string[]]$testArguments = @(switch ($Case) {
        'All' { @() }
        'New' { @('--discovery-timeouts-only') }
        'Existing' { @('--skip-discovery-timeouts') }
        default { @('--discovery-timeout-case', $Case) }
    })
    Write-Output ('SyntheticCheckCase=' + $Case + ';ArgumentCount=' + $testArguments.Count)
    & $executable @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic desktop timeout checks failed.' }
}
finally {
    foreach ($name in $previous.Keys) {
        if ($null -eq $previous[$name]) { Remove-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    }
}
