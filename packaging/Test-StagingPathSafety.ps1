#requires -Version 5
<#
.SYNOPSIS
    The five mandatory-and-unread safety parameters in packaging\StagingPathSafety.ps1, exercised.

.DESCRIPTION
    Until 2026-09-24 this file declared [Parameter(Mandatory)]$TrustedRoot on three functions that
    never read it -- two of which go on to call Remove-Item -Recurse -Force -- plus a mandatory
    $Root on Copy-...ValidatedInputFile and a [bool]$RejectHardLinks = $true on two functions, none
    of them read either. A caller could not omit any of them and none of them did anything.

    That is the same defect the file's own docstring records being found and fixed in
    Publish-...AtomicFile ("took ELEVEN parameters and read TWO of them"); five more instances
    survived in the same file because nothing ever tried to make them fire. This is that file.

    Two things here are deliberately stronger than "an exception was thrown":

    - The destructive cases plant a SENTINEL inside the directory the call would delete and assert
      it is still there afterwards. A guard that throws for an unrelated reason while the
      Remove-Item has already run would pass a message check and fail this one.
    - Every case names the message it expects. Passing '' would make the match read -like "**", so
      any exception counts as proof -- which is exactly how case 5 of Test-AtomicPublish.ps1 was
      passing on the wrong exception until the same day as this file was written.

    A green baseline comes first: the ordinary calls the real packaging scripts make must still
    SUCCEED, or the refusals below only prove the functions now throw at everything.

    Cheap enough for the gate (~1s, no desktop, all under %TEMP%), and it cleans up after itself.
    Junctions, not symlinks: New-Item -ItemType Junction needs no elevation and no developer mode,
    so this runs the same on a workstation and on a CI runner.
.PARAMETER RepoRoot
    Defaults to the parent of this script's directory.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
. (Join-Path $RepoRoot 'packaging\StagingPathSafety.ps1')

$scratch = Join-Path $env:TEMP ('dp-staging-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

$fired = 0
$cases = 0
$junctionsAvailable = $true

function Try-Refuse {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Expect,
        [string]$Sentinel,
        [string]$MustNotExist
    )

    $script:cases++
    $threw = $null
    try {
        & $Action
    }
    catch {
        $threw = $_.Exception.Message
    }

    if ($null -eq $threw) {
        Write-Host ("  {0,-58} ALLOWED IT -- the check does not fire" -f $Name) -ForegroundColor Red
        return
    }
    if ($threw -notlike "*$Expect*") {
        Write-Host ("  {0,-58} THREW SOMETHING ELSE" -f $Name) -ForegroundColor Yellow
        Write-Host ("        {0}" -f $threw.Substring(0, [Math]::Min(130, $threw.Length))) -ForegroundColor DarkGray
        return
    }
    # A refusal that arrives AFTER the damage is not a refusal.
    if (-not [string]::IsNullOrWhiteSpace($Sentinel) -and -not (Test-Path -LiteralPath $Sentinel)) {
        Write-Host ("  {0,-58} REFUSED, BUT DELETED FIRST" -f $Name) -ForegroundColor Red
        Write-Host ("        sentinel gone: {0}" -f $Sentinel) -ForegroundColor DarkGray
        return
    }
    # The constructive twin: a refusal that still left the directory behind is not a refusal either,
    # because the caller's finally block will delete whatever it finds there.
    if (-not [string]::IsNullOrWhiteSpace($MustNotExist) -and (Test-Path -LiteralPath $MustNotExist)) {
        Write-Host ("  {0,-58} REFUSED, BUT CREATED IT ANYWAY" -f $Name) -ForegroundColor Red
        Write-Host ("        exists: {0}" -f $MustNotExist) -ForegroundColor DarkGray
        return
    }

    $script:fired++
    Write-Host ("  {0,-58} REFUSED" -f $Name) -ForegroundColor Green
    Write-Host ("        {0}" -f $threw.Substring(0, [Math]::Min(130, $threw.Length))) -ForegroundColor DarkGray
}

function New-Probe {
    param([Parameter(Mandatory = $true)][string]$Relative)

    $path = Join-Path $scratch $Relative
    $parent = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($path, 'payload')
    return (Get-DesktopAICompanionCanonicalPath -Path $path)
}

function New-ProbeDirectory {
    param([Parameter(Mandatory = $true)][string]$Relative)

    $path = Join-Path $scratch $Relative
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return (Get-DesktopAICompanionCanonicalPath -Path $path)
}

try {
    # ------------------------------------------------------------------------------------------
    # BASELINE. The shapes installer\build-installer.ps1 and the packaging scripts really use.
    # If any of these throw, every refusal below is meaningless.
    # ------------------------------------------------------------------------------------------
    Write-Host '  baseline: the calls the real packaging scripts make'
    $realRoot = New-ProbeDirectory 'real'
    $realFile = New-Probe 'real\payload.bin'

    $handle = Open-DesktopAICompanionValidatedInputFile -Path $realFile -Root $realRoot
    $handle.Dispose()

    $copyTarget = Join-Path $realRoot 'copied.bin'
    [void](Copy-DesktopAICompanionValidatedInputFile `
        -Path $realFile -Root $realRoot -DestinationPath $copyTarget)
    if (-not (Test-Path -LiteralPath $copyTarget -PathType Leaf)) {
        throw 'BASELINE FAILED: a legitimate validated copy did not land.'
    }

    $scratchPath = Join-Path $realRoot 'work'
    $lease = Open-DesktopAICompanionNewScratchDirectory `
        -Path $scratchPath -AllowedRoot $realRoot -TrustedRoot $scratch `
        -ProtectedPaths @($realFile) -ProtectedDirectories @($realRoot)
    $lease.Dispose()
    if (-not (Test-Path -LiteralPath $scratchPath -PathType Container)) {
        throw 'BASELINE FAILED: a legitimate scratch directory was not created.'
    }

    Reset-DesktopAICompanionStagingDirectory `
        -Path $scratchPath -AllowedRoot $realRoot -TrustedRoot $scratch
    Remove-DesktopAICompanionSafeFile `
        -Path $copyTarget -AllowedRoot $realRoot -TrustedRoot $scratch
    Remove-DesktopAICompanionSafeDirectory `
        -Path $scratchPath -AllowedRoot $realRoot -TrustedRoot $scratch
    if (Test-Path -LiteralPath $scratchPath) {
        throw 'BASELINE FAILED: a legitimate safe-delete did not delete.'
    }
    Write-Host '  baseline: OK -- six legitimate calls all landed' -ForegroundColor DarkGray
    Write-Host ''

    # ------------------------------------------------------------------------------------------
    # $TrustedRoot, on the three functions that delete. Each case is INSIDE its -AllowedRoot, so
    # only the trusted-root check can refuse it. Before today all three deleted the sentinel.
    # ------------------------------------------------------------------------------------------
    $victimRoot = New-ProbeDirectory 'victim'
    $elsewhere = New-ProbeDirectory 'elsewhere'

    $victimFile = New-Probe 'victim\precious.bin'
    Try-Refuse -Name 'Remove-SafeFile outside the trusted root' `
        -Expect 'escaped the trusted root' -Sentinel $victimFile -Action {
            Remove-DesktopAICompanionSafeFile `
                -Path $victimFile -AllowedRoot $victimRoot -TrustedRoot $elsewhere
        }

    $victimTree = New-ProbeDirectory 'victim\tree'
    $treeSentinel = New-Probe 'victim\tree\precious.bin'
    Try-Refuse -Name 'Remove-SafeDirectory outside the trusted root' `
        -Expect 'escaped the trusted root' -Sentinel $treeSentinel -Action {
            Remove-DesktopAICompanionSafeDirectory `
                -Path $victimTree -AllowedRoot $victimRoot -TrustedRoot $elsewhere
        }

    $resetTree = New-ProbeDirectory 'victim\reset'
    $resetSentinel = New-Probe 'victim\reset\precious.bin'
    Try-Refuse -Name 'Reset-StagingDirectory outside the trusted root' `
        -Expect 'escaped the trusted root' -Sentinel $resetSentinel -Action {
            Reset-DesktopAICompanionStagingDirectory `
                -Path $resetTree -AllowedRoot $victimRoot -TrustedRoot $elsewhere
        }

    # ------------------------------------------------------------------------------------------
    # Open-NewScratchDirectory: $TrustedRoot, $ProtectedPaths and $ProtectedDirectories, all three
    # accepted and discarded. This one hands back a directory the caller will later delete
    # RECURSIVELY, so a scratch that swallows a protected input is a recursive delete of it.
    # ------------------------------------------------------------------------------------------
    Try-Refuse -Name 'a new scratch outside the trusted root' `
        -Expect 'escaped the trusted root' -Action {
            $unused = Open-DesktopAICompanionNewScratchDirectory `
                -Path (Join-Path $victimRoot 'scratch-a') `
                -AllowedRoot $victimRoot -TrustedRoot $elsewhere
            $unused.Dispose()
        }

    # A scratch that would SWALLOW a declared protected path. The protected path must be one that
    # does not exist YET, because the absence check above fires first on any scratch directory that
    # already contains something -- which is how the first draft of this test scored these two cases
    # on the wrong exception. That is not a contrivance: packaging\New-DeterministicPortableZip.ps1
    # passes $destinationFull, the zip it is about to produce, as a protected path precisely when it
    # does not exist yet, and Normalize-MsiDeterminism.ps1 does the same with $destinationMsiPath.
    # A scratch mis-wired onto that directory is a recursive delete of the build's own output.
    $swallowRoot = Join-Path $victimRoot 'swallow'
    $plannedOutput = Join-Path $swallowRoot 'planned-output.zip'
    Try-Refuse -Name 'a new scratch that would contain a protected input' `
        -Expect 'protected build input' -MustNotExist $swallowRoot -Action {
            $unused = Open-DesktopAICompanionNewScratchDirectory `
                -Path $swallowRoot -AllowedRoot $victimRoot -TrustedRoot $scratch `
                -ProtectedPaths @($plannedOutput)
            $unused.Dispose()
        }

    $swallowDirectoryRoot = Join-Path $victimRoot 'swallowdir'
    $plannedDirectory = Join-Path $swallowDirectoryRoot 'planned'
    Try-Refuse -Name 'a new scratch that would contain a protected directory' `
        -Expect 'protected build directory' -MustNotExist $swallowDirectoryRoot -Action {
            $unused = Open-DesktopAICompanionNewScratchDirectory `
                -Path $swallowDirectoryRoot -AllowedRoot $victimRoot -TrustedRoot $scratch `
                -ProtectedDirectories @($plannedDirectory)
            $unused.Dispose()
        }

    # ------------------------------------------------------------------------------------------
    # Copy-ValidatedInputFile: a mandatory $Root that was never read, so a copy whose -Path sat
    # anywhere on the volume succeeded while its own signature said it could not.
    # ------------------------------------------------------------------------------------------
    $outsideSource = New-Probe 'elsewhere\outside.bin'
    Try-Refuse -Name 'a validated copy whose source is outside its root' `
        -Expect 'strictly below its declared root' -Action {
            [void](Copy-DesktopAICompanionValidatedInputFile `
                -Path $outsideSource -Root $realRoot `
                -DestinationPath (Join-Path $realRoot 'smuggled.bin'))
        }

    # ------------------------------------------------------------------------------------------
    # $RejectHardLinks, on both functions that declare it defaulted to $true.
    # ------------------------------------------------------------------------------------------
    $linkTargetDirectory = New-ProbeDirectory 'linktarget'
    [IO.File]::WriteAllText((Join-Path $linkTargetDirectory 'smuggled.bin'), 'payload')
    $linkRoot = New-ProbeDirectory 'linkroot'
    $junction = Join-Path $linkRoot 'via'
    try {
        New-Item -ItemType Junction -Path $junction -Target $linkTargetDirectory -ErrorAction Stop | Out-Null
    }
    catch {
        $junctionsAvailable = $false
        Write-Warning ("Junctions could not be created under %TEMP%, so the two -RejectHardLinks " +
                       "cases did not run: $($_.Exception.Message)")
    }

    if ($junctionsAvailable) {
        $throughJunction = Join-Path $junction 'smuggled.bin'
        Try-Refuse -Name 'a validated input reached through a junction' `
            -Expect 'linked directory' -Action {
                $unused = Open-DesktopAICompanionValidatedInputFile `
                    -Path $throughJunction -Root $linkRoot
                $unused.Dispose()
            }

        Try-Refuse -Name 'a validated copy whose source is behind a junction' `
            -Expect 'linked directory' -Action {
                [void](Copy-DesktopAICompanionValidatedInputFile `
                    -Path $throughJunction -Root $linkRoot `
                    -DestinationPath (Join-Path $linkRoot 'copied.bin'))
            }

        # And the flag still means what its name says: passing $false accepts the same input.
        $permitted = Open-DesktopAICompanionValidatedInputFile `
            -Path $throughJunction -Root $linkRoot -RejectHardLinks $false
        $permitted.Dispose()
        Write-Host ('  -RejectHardLinks $false still accepts it, so the flag is read, not ignored' ) -ForegroundColor DarkGray
    }
}
finally {
    # Junctions first and by hand: Remove-Item -Recurse on a directory containing one is not worth
    # trusting across both shells, and this probe creates real ones.
    try {
        Get-ChildItem -LiteralPath $scratch -Recurse -Force -Directory -ErrorAction SilentlyContinue |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            ForEach-Object { [IO.Directory]::Delete($_.FullName) }
    }
    catch { }
    try { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host ("{0}/{1} refused." -f $fired, $cases)
if (-not $junctionsAvailable) {
    # A control that can run degraded says so on EVERY run, rather than quietly covering less.
    throw ('Staging path-safety checks ran DEGRADED: junctions were unavailable, so the two ' +
           '-RejectHardLinks cases were skipped. Coverage narrowed silently is how this file came ' +
           'to be needed in the first place.')
}
if ($fired -ne $cases) {
    throw "Staging path-safety checks: only $fired of $cases refused. A check that does not fire is not a check."
}
