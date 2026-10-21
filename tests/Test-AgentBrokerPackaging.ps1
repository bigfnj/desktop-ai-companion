#requires -Version 5
<#
.SYNOPSIS
    Verify separately published module projects remain in the shared freshness/publish watch set.
.EXAMPLE
    pwsh -NoProfile -File tests/Test-AgentBrokerPackaging.ps1
#>
[CmdletBinding()]
param([string]$RepoRoot = '', [switch]$CheckPayload)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path $PSScriptRoot -Parent }
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
. (Join-Path $RepoRoot 'packaging\ModuleWatchSet.ps1')
. (Join-Path $RepoRoot 'packaging\StagingPathSafety.ps1')
$checks = 0
function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "AgentBroker packaging assertion failed: $Description" }
    $script:checks++
    Write-Host ("PASS: " + $Description)
}
function Write-Fixture([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory((Split-Path $Path -Parent))
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}
# Fixture projects must stay outside the checkout. C# language services discover even short-lived
# *.csproj files under artifacts, retain their diagnostics, and may recreate obj after cleanup.
# Read the user's configured temp directly because Invoke-IsolatedGate deliberately replaces this
# process's TEMP with an in-checkout path. The fallback is the standard per-user local temp folder.
$scratchBase = [Environment]::GetEnvironmentVariable('TEMP', 'User')
if ([string]::IsNullOrWhiteSpace($scratchBase)) { $scratchBase = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp' }
$scratchBase = [IO.Path]::GetFullPath($scratchBase)
if (Test-DesktopAICompanionPathWithin -Path $scratchBase -Root $RepoRoot -AllowRoot) {
    $scratchBase = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp'))
}
if (Test-DesktopAICompanionPathWithin -Path $scratchBase -Root $RepoRoot -AllowRoot) {
    throw 'Packaging fixture temporary storage must be outside the checkout.'
}
$scratch = Join-Path $scratchBase ('desktop-ai-agentbroker-packaging-' + [guid]::NewGuid().ToString('N'))
[void](Assert-DesktopAICompanionPathChainSafe -Path $scratch -TrustedRoot $scratchBase)
$fixture = Join-Path $scratch 'repo'
$module = Join-Path $fixture 'modules\Broker'
try {
    Write-Fixture (Join-Path $fixture 'global.json') '{"sdk":{"version":"10.0.302"}}'
    Write-Fixture (Join-Path $fixture 'modules\Directory.Build.props') '<Project />'
    Write-Fixture (Join-Path $fixture 'src\Directory.Build.props') '<Project />'
    Write-Fixture (Join-Path $fixture 'packaging\publish.ps1') '# publisher'
    Write-Fixture (Join-Path $fixture 'src\Atomic.cs') '// shared source'
    $sdkProject = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    Write-Fixture (Join-Path $fixture 'src\Host\Host.csproj') $sdkProject
    Write-Fixture (Join-Path $fixture 'src\ModuleKit\Kit.csproj') $sdkProject
    Write-Fixture (Join-Path $fixture 'src\Core\Core.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="..\Atomic.cs" /></ItemGroup><Target Name="Generate"><ItemGroup><Compile Include="$(IntermediateOutputPath)Shared.g.cs" /></ItemGroup></Target></Project>'
    Write-Fixture (Join-Path $fixture 'tools\Native\Native.csproj') $sdkProject
    Write-Fixture (Join-Path $fixture 'tools\Worker\Worker.csproj') @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>
  <ProjectReference Include="..\Native\Native.csproj" />
  <ProjectReference Include="..\..\src\Core\Core.csproj" />
</ItemGroup></Project>
'@
    $projectText = @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>
  <ProjectReference Include="..\..\src\Host\Host.csproj" Private="false" />
  <ProjectReference Include="..\..\src\ModuleKit\Kit.csproj" />
  <ModulePayloadProject Include="..\..\tools\Worker\Worker.csproj" Private="false" />
  <ModulePayloadInput Include="..\..\packaging\publish.ps1" />
</ItemGroup></Project>
'@
    $moduleProject = Join-Path $module 'Broker.csproj'
    Write-Fixture $moduleProject $projectText
    $watch = Get-ModuleWatchSet -RepoRoot $fixture -ModuleDirectory $module
    Assert-Check ($watch.Degraded.Count -eq 0) 'the explicit payload graph resolves without degradation'
    foreach ($expected in @('global.json', 'modules/Directory.Build.props', 'src/Directory.Build.props',
                            'src/ModuleKit', 'src/Core', 'src/Atomic.cs', 'tools/Worker',
                            'tools/Native', 'packaging/publish.ps1')) {
        Assert-Check ($watch.External -contains $expected) ("the payload watches " + $expected)
    }
    Assert-Check ($watch.External -notcontains 'src/Host') 'host-owned Private=false references stay outside the payload'
    $groups = Get-ModuleWatchPathspecs -RepoRoot $fixture -ModuleDirectory (Get-Item -LiteralPath $module)
    Assert-Check ($groups.Pathspecs -contains ':(exclude)tools/Worker/**/*.md') 'payload project directory watches exclude documentation'
    Assert-Check ($groups.Pathspecs -contains 'packaging/publish.ps1') 'the publisher itself participates in both guard and freshness pathspecs'

    Write-Fixture $moduleProject ($projectText.Replace('packaging\publish.ps1', 'packaging\missing.ps1'))
    $missing = Get-ModuleWatchSet -RepoRoot $fixture -ModuleDirectory $module
    Assert-Check (@($missing.Degraded | Where-Object { $_ -like 'payload input is missing:*' }).Count -eq 1) 'a missing declared publisher fails closed'
    Write-Fixture $moduleProject ($projectText.Replace('Worker\Worker.csproj', 'Worker\Missing.csproj'))
    $missingProject = Get-ModuleWatchSet -RepoRoot $fixture -ModuleDirectory $module
    Assert-Check (@($missingProject.Degraded | Where-Object { $_ -like 'referenced project is missing:*' }).Count -eq 1) 'a missing payload project fails closed'
    Write-Fixture $moduleProject ($projectText.Replace('packaging\publish.ps1', '$(Unresolved)\publish.ps1'))
    $unresolved = Get-ModuleWatchSet -RepoRoot $fixture -ModuleDirectory $module
    Assert-Check (@($unresolved.Degraded | Where-Object { $_ -like 'unresolved MSBuild property*' }).Count -eq 1) 'an unresolved payload input fails closed'

    # This sibling used to pass a bare StartsWith(repo) boundary. Its invalid XML proves it was
    # rejected before project parsing, rather than followed and merely omitted from the final list.
    Write-Fixture (Join-Path $scratch 'repo-sibling\Outside.fixtureproj') 'not project XML'
    Write-Fixture $moduleProject '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ModulePayloadProject Include="..\..\..\repo-sibling\Outside.fixtureproj" /></ItemGroup></Project>'
    $escape = Get-ModuleWatchSet -RepoRoot $fixture -ModuleDirectory $module
    Assert-Check (@($escape.Degraded | Where-Object { $_ -like 'outside the repository*' }).Count -eq 1) 'a repository-name-prefix sibling is refused'
    Assert-Check (@($escape.Degraded | Where-Object { $_ -like 'could not parse*' }).Count -eq 0) 'an outside project is never opened or parsed'
    $outsideRefused = $false
    try { [void](Get-ModuleWatchSet -RepoRoot $fixture -ModuleDirectory (Join-Path $scratch 'repo-sibling')) }
    catch { $outsideRefused = $_.Exception.Message -ceq 'The module source directory must be inside the repository.' }
    Assert-Check $outsideRefused 'an outside module directory is refused before enumeration'

    $real = Get-ModuleWatchSet -RepoRoot $RepoRoot -ModuleDirectory (Join-Path $RepoRoot 'modules\AgentBroker')
    Assert-Check ($real.Degraded.Count -eq 0) ('the production AgentBroker payload graph is complete: ' + ($real.Degraded -join '; '))
    foreach ($expected in @('tools/AgentBroker.Worker', 'tools/AgentBroker.ClaudeAdapter',
                            'tools/AgentBroker.CodexDesktopAdapter', 'src/AgentBroker.Core',
                            'src/DesktopAICompanion.ModuleKit/AtomicFile.cs',
                            'packaging/Publish-AgentBrokerWorker.ps1', 'packaging/StagingPathSafety.ps1',
                            'packaging/AgentBrokerNotices')) {
        Assert-Check ($real.External -contains $expected) ("the production graph watches " + $expected)
    }
    if ($CheckPayload) {
        $output = Join-Path $RepoRoot 'build\DesktopAICompanionPortable\bin\Release\x64\modules\agentbroker'
        $worker = Join-Path $output 'worker'
        foreach ($name in @('AgentBroker.Worker.exe', 'AgentBroker.Worker.dll', 'coreclr.dll',
                            'hostfxr.dll', 'Microsoft.AspNetCore.Server.Kestrel.Core.dll')) {
            Assert-Check (Test-Path -LiteralPath (Join-Path $worker $name) -PathType Leaf) ("the self-contained payload includes " + $name)
        }
        foreach ($name in @('THIRD-PARTY-NOTICES.txt', 'versions.json', 'mcp-sdk-license.txt',
                            'microsoft-extensions-ai-license.txt', 'dotnet-license.txt',
                            'dotnet-third-party-notices.txt', 'aspnetcore-license.txt',
                            'aspnetcore-third-party-notices.txt')) {
            $source = Join-Path $RepoRoot ('packaging\AgentBrokerNotices\' + $name)
            $published = Join-Path $worker ('licenses\' + $name)
            Assert-Check ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ceq
                          (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash) ("the payload retains the original " + $name)
        }
        foreach ($relative in @('AgentBroker.dll', 'DesktopAICompanion.ModuleKit.dll',
                                'worker\AgentBroker.Worker.dll', 'worker\AgentBroker.Core.dll',
                                'worker\AgentBroker.CodexDesktopAdapter.dll', 'worker\AgentBroker.ClaudeAdapter.dll')) {
            $bytes = [IO.File]::ReadAllBytes((Join-Path $output $relative))
            $ascii = [Text.Encoding]::ASCII.GetString($bytes)
            $unicode = [Text.Encoding]::Unicode.GetString($bytes)
            $profile = [Environment]::GetFolderPath('UserProfile')
            Assert-Check (($ascii.IndexOf($RepoRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) -and
                          ($unicode.IndexOf($RepoRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) -and
                          ($ascii.IndexOf($profile, [StringComparison]::OrdinalIgnoreCase) -lt 0) -and
                          ($unicode.IndexOf($profile, [StringComparison]::OrdinalIgnoreCase) -lt 0)) ("published binary omits local build paths: " + $relative)
        }

        # --help returns before broker creation, so this proves the shipped apphost starts without
        # binding ports, creating credentials, or touching any client. It uses no installed SDK.
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path $worker 'AgentBroker.Worker.exe'
        $start.Arguments = '--help'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.StandardOutputEncoding = [Text.Encoding]::UTF8
        $start.StandardErrorEncoding = [Text.Encoding]::UTF8
        $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $scratch 'no-installed-runtime'
        $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $start
        try {
            [void]$process.Start()
            if (-not $process.WaitForExit(10000)) {
                $process.Kill()
                $process.WaitForExit()
                throw 'The published worker --help did not exit within 10 seconds.'
            }
            $stdout = $process.StandardOutput.ReadToEnd()
            $stderr = $process.StandardError.ReadToEnd()
            Assert-Check ($process.ExitCode -eq 0 -and $stdout -like 'AgentBroker.Worker*' -and
                          [string]::IsNullOrWhiteSpace($stderr)) 'the shipped self-contained apphost starts without a configured shared runtime'
        }
        finally { $process.Dispose() }
        $zipPath = Join-Path $scratch 'agentbroker.zip'
        $zip = & (Join-Path $RepoRoot 'packaging\New-ModuleDistZip.ps1') -ModuleId agentbroker -SourceDirectory $output -DestinationPath $zipPath
        Assert-Check ($zip.Bytes -gt 0 -and $zip.Bytes -le 100MB) 'the complete module ZIP fits the host 100 MiB download limit'
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName })
            Assert-Check ($names -contains 'AgentBroker.dll' -and $names -contains 'worker/AgentBroker.Worker.exe') 'the module ZIP preserves the nested worker layout'
            Assert-Check (@($names | Where-Object { $_ -like '*.pdb' }).Count -eq 0) 'the module ZIP includes no debug symbol files'
            Assert-Check ($names -contains 'worker/licenses/mcp-sdk-license.txt') 'the module ZIP includes dependency licensing'
        }
        finally { $archive.Dispose() }
        Write-Host ("AgentBroker packaged ZIP: {0:N0} bytes." -f $zip.Bytes)
    }
    Write-Host ("AgentBroker packaging PASS: {0} assertions." -f $checks)
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        # Delete only the exact files created by this test, then empty directories. An unexpected
        # file makes cleanup fail without reading or deleting it; no recursive delete is needed.
        foreach ($relative in @('agentbroker.zip.tmp', 'agentbroker.zip',
                                'repo-sibling\Outside.fixtureproj', 'repo\global.json',
                                'repo\modules\Directory.Build.props', 'repo\src\Directory.Build.props',
                                'repo\packaging\publish.ps1', 'repo\src\Atomic.cs',
                                'repo\src\Host\Host.csproj', 'repo\src\ModuleKit\Kit.csproj',
                                'repo\src\Core\Core.csproj', 'repo\tools\Native\Native.csproj',
                                'repo\tools\Worker\Worker.csproj', 'repo\modules\Broker\Broker.csproj')) {
            $path = Join-Path $scratch $relative
            if (Test-Path -LiteralPath $path) {
                Remove-DesktopAICompanionSafeFile -Path $path -AllowedRoot $scratch -TrustedRoot $scratchBase
            }
        }
        foreach ($relative in @('repo\src\ModuleKit', 'repo\src\Host', 'repo\src\Core',
                                'repo\tools\Native', 'repo\tools\Worker', 'repo\modules\Broker',
                                'repo\src', 'repo\tools', 'repo\modules', 'repo\packaging', 'repo-sibling', 'repo')) {
            $directory = Join-Path $scratch $relative
            if (Test-Path -LiteralPath $directory) {
                [void](Assert-DesktopAICompanionPathChainSafe -Path $directory -TrustedRoot $scratchBase)
                [IO.Directory]::Delete($directory, $false)
            }
        }
        [void](Assert-DesktopAICompanionPathChainSafe -Path $scratch -TrustedRoot $scratchBase)
        [IO.Directory]::Delete($scratch, $false)
    }
}
