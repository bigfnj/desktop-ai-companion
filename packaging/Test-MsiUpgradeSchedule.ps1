#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
    throw "MSI not found: $MsiPath"
}

$absoluteMsi = [IO.Path]::GetFullPath($MsiPath)

function Get-ActionSequence {
    param([Parameter(Mandatory = $true)][string]$Action)

    if ($Action -notmatch '^[A-Za-z][A-Za-z0-9_]*$') {
        throw "Unsafe Windows Installer action name: '$Action'."
    }

    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($absoluteMsi, 0)
        $view = $database.OpenView(
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = '$Action'")
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "MSI InstallExecuteSequence is missing required action '$Action'."
        }
        $sequence = [int]$record.IntegerData(1)
        $duplicate = $view.Fetch()
        if ($null -ne $duplicate) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($duplicate)
            throw "MSI InstallExecuteSequence contains duplicate '$Action' rows."
        }
        return $sequence
    }
    finally {
        foreach ($value in @($record, $view, $database, $installer)) {
            if ($null -ne $value -and
                [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
            }
        }
    }
}

$installInitialize = Get-ActionSequence -Action 'InstallInitialize'
$removeExistingProducts = Get-ActionSequence -Action 'RemoveExistingProducts'
$installFinalize = Get-ActionSequence -Action 'InstallFinalize'

if ($removeExistingProducts -le $installInitialize -or
    $removeExistingProducts -ge $installFinalize) {
    throw (
        ('Unsafe major-upgrade schedule: RemoveExistingProducts sequence {0} must be ' +
        'after InstallInitialize {1} and before InstallFinalize {2} so removal of the ' +
        'previous product is inside the rollback transaction.') -f
        $removeExistingProducts,
        $installInitialize,
        $installFinalize)
}

Write-Host (
    ('Major-upgrade rollback boundary verified: InstallInitialize={0}, ' +
    'RemoveExistingProducts={1}, InstallFinalize={2}.') -f
    $installInitialize,
    $removeExistingProducts,
    $installFinalize) -ForegroundColor Green

if ($SelfTest) {
    $scratchRoot = Join-Path ([IO.Path]::GetTempPath()) (
        'DesktopAICompanion-MsiSchedule-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null
        foreach ($testCase in @(
                [pscustomobject]@{ Name = 'before transaction'; Sequence = 1401 },
                [pscustomobject]@{ Name = 'after finalization'; Sequence = 6700 })) {
            $testMsi = Join-Path $scratchRoot (
                'invalid-' + $testCase.Sequence + '.msi')
            Copy-Item -LiteralPath $absoluteMsi -Destination $testMsi

            $installer = $null
            $database = $null
            $view = $null
            try {
                $installer = New-Object -ComObject WindowsInstaller.Installer
                $database = $installer.OpenDatabase($testMsi, 1)
                $view = $database.OpenView(
                    "UPDATE ``InstallExecuteSequence`` SET ``Sequence`` = $($testCase.Sequence) WHERE ``Action`` = 'RemoveExistingProducts'")
                [void]$view.Execute()
                [void]$database.Commit()
            }
            finally {
                foreach ($value in @($view, $database, $installer)) {
                    if ($null -ne $value -and
                        [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
                    }
                }
            }

            $failure = $null
            try {
                & $PSCommandPath -MsiPath $testMsi
            }
            catch {
                $failure = $_
            }
            if ($null -eq $failure) {
                throw "MSI schedule self-test '$($testCase.Name)' did not fail closed."
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $scratchRoot) {
            $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
            $resolvedTemp = [IO.Path]::GetFullPath(
                [IO.Path]::GetTempPath()).TrimEnd('\')
            if (-not $resolvedScratch.StartsWith(
                    $resolvedTemp + '\DesktopAICompanion-MsiSchedule-',
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove unsafe MSI schedule scratch path: $resolvedScratch"
            }
            Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
        }
    }
    Write-Host (
        'MSI major-upgrade schedule negative controls passed for pre-transaction ' +
        'and post-finalization removal.'
    ) -ForegroundColor Green
}
