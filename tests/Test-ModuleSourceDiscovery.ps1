#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $repoRoot 'packaging\ModuleSourceDiscovery.ps1')
$scratch = [IO.Path]::GetFullPath((Join-Path $env:TEMP ('dp-module-source-' + [guid]::NewGuid().ToString('N'))))
$fixture = Join-Path $scratch 'modules\TemplateCheck'
$obj = Join-Path $fixture 'obj'
$linkTarget = Join-Path $scratch 'link-target'
$checks = 0
$utf8 = New-Object Text.UTF8Encoding($false)
function Assert-Classification([bool]$Expected, [string]$Description) {
    $actual = Test-LegacyTemplateBuildResidue -Directory (Get-Item -LiteralPath $fixture) -RepoRoot $scratch
    if ($actual -ne $Expected) { throw $Description }
    if (-not $Expected) {
        $refused = $false
        try { & (Join-Path $repoRoot 'packaging\Test-ModulePublishFreshness.ps1') -RepoRoot $scratch }
        catch {
            if ($_.Exception.Message -like '*neither listed in modules.json*templatecheck*') { $refused = $true }
            else { throw }
        }
        if (-not $refused) { throw "Freshness did not refuse the source fixture: $Description" }
    }
    $script:checks++
    Write-Host "PASS: $Description"
}
try {
    New-Item -ItemType Directory -Path (Join-Path $obj 'Debug') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $scratch 'modules-dist') | Out-Null
    [IO.File]::WriteAllText((Join-Path $scratch 'modules-dist\modules.json'), '{"modules":[]}', $utf8)
    [IO.File]::WriteAllText((Join-Path $obj 'Debug\TemplateCheck.AssemblyInfo.cs'), '// synthetic generated file', $utf8)
    Assert-Classification $true 'the exact old template fixture holding generated obj files is residue'
    foreach ($name in @('TemplateCheck.csproj', 'TemplateCheck.cs', 'module.json', 'README.md', 'bin')) {
        $path = Join-Path $fixture $name
        [IO.File]::WriteAllText($path, 'synthetic root content', $utf8)
        Assert-Classification $false "a fixture root containing $name remains a source directory"
        Remove-Item -LiteralPath $path -Force
    }
    $extra = Join-Path $fixture 'source'
    New-Item -ItemType Directory -Path $extra | Out-Null
    [IO.File]::WriteAllText((Join-Path $extra 'Nested.cs'), '// synthetic source', $utf8)
    Assert-Classification $false 'source nested outside obj/bin remains a source directory'
    Remove-Item -LiteralPath (Join-Path $extra 'Nested.cs') -Force
    [IO.Directory]::Delete($extra, $false)
    $other = Join-Path $scratch 'modules\RealModule'
    New-Item -ItemType Directory -Path (Join-Path $other 'obj') -Force | Out-Null
    if (Test-LegacyTemplateBuildResidue -Directory (Get-Item -LiteralPath $other) -RepoRoot $scratch) {
        throw 'A real module was mistaken for the template fixture.'
    }
    $checks++
    Write-Host 'PASS: another module is never exempted for holding only obj/bin'
    Remove-Item -LiteralPath (Join-Path $obj 'Debug\TemplateCheck.AssemblyInfo.cs') -Force
    [IO.Directory]::Delete((Join-Path $obj 'Debug'), $false)
    [IO.Directory]::Delete($obj, $false)
    New-Item -ItemType Directory -Path $linkTarget | Out-Null
    New-Item -ItemType Junction -Path $obj -Target $linkTarget | Out-Null
    Assert-Classification $false 'a linked generated directory is never exempted'
    Remove-Item -LiteralPath $obj -Force
    Write-Host "Module source discovery PASS: $checks classifications."
}
finally {
    # The entire scratch tree was created above, beneath the resolved TEMP root. A link, if a
    # preceding assertion failed, is removed first so recursive cleanup never traverses it.
    if (Test-Path -LiteralPath $obj) {
        if ((Get-Item -LiteralPath $obj -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Remove-Item -LiteralPath $obj -Force
        }
    }
    if (-not $scratch.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The module-source test scratch escaped TEMP.'
    }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
