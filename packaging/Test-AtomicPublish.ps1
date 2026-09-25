#requires -Version 5
<#
.SYNOPSIS
    Every refusal in Publish-DesktopAICompanionAtomicFile, exercised.

.DESCRIPTION
    That function took eleven parameters and read TWO of them until 2026-09-17, while
    installer\build-installer.ps1 stated in a comment that it enforced the seal hash "on the way
    into dist\". Five checks now exist; this is the file that proves each one can refuse, because a
    check nobody has seen fail is a guess -- and this particular set had been a guess for months.

    A green baseline comes first: a correct publish must LAND, or every refusal below proves nothing
    except that the function throws.

    Cheap enough for the gate (~1s, no desktop, all under %TEMP%), and it cleans up after itself.
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

$scratch = Join-Path $env:TEMP ('dp-atomic-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

$fired = 0
$cases = 0

function Try-Publish([hashtable]$parameters, [string]$name, [string]$expect) {
    $script:cases++
    try {
        [void](Publish-DesktopAICompanionAtomicFile @parameters)
        Write-Host ("  {0,-56} PUBLISHED ANYWAY -- the check does not fire" -f $name) -ForegroundColor Red
    }
    catch {
        $message = $_.Exception.Message
        if ($message -like "*$expect*") {
            $script:fired++
            Write-Host ("  {0,-56} REFUSED" -f $name) -ForegroundColor Green
            Write-Host ("        {0}" -f $message.Substring(0, [Math]::Min(120, $message.Length))) -ForegroundColor DarkGray
        }
        else {
            Write-Host ("  {0,-56} THREW SOMETHING ELSE" -f $name) -ForegroundColor Yellow
            Write-Host ("        {0}" -f $message.Substring(0, [Math]::Min(120, $message.Length))) -ForegroundColor DarkGray
        }
    }
}

function New-Staged([string]$label, [string]$content) {
    $path = Join-Path $scratch "$label.bin"
    [IO.File]::WriteAllText($path, $content)
    return $path
}

try {
    # BASELINE: a correct publish must SUCCEED, or every refusal below proves nothing.
    $temp = New-Staged 'good' 'payload'
    $hash = [DesktopAICompanionPackagingHashUtil]::HashFile($temp, 'SHA256')
    $destination = Join-Path $scratch 'published.bin'
    $published = Publish-DesktopAICompanionAtomicFile `
        -TemporaryPath $temp -DestinationPath $destination -TrustedRoot $scratch `
        -ExpectedTemporarySha256 $hash -DestinationMustBeAbsent
    if (-not (Test-Path -LiteralPath $published -PathType Leaf)) {
        Write-Host 'BASELINE FAILED: a correct publish did not land' -ForegroundColor Red
        exit 2
    }
    Write-Host '  baseline: a correct publish lands' -ForegroundColor DarkGray

    # 1. A wrong expected temp hash: 64 zeroes, the audit's own example.
    $temp1 = New-Staged 'wronghash' 'payload'
    Try-Publish @{
        TemporaryPath = $temp1
        DestinationPath = (Join-Path $scratch 'out1.bin')
        TrustedRoot = $scratch
        ExpectedTemporarySha256 = ('0' * 64)
    } 'a staged file that does not match its seal hash' 'changed after it was sealed'

    # 2. -DestinationMustBeAbsent against a destination that exists.
    $temp2 = New-Staged 'absent' 'payload'
    Try-Publish @{
        TemporaryPath = $temp2
        DestinationPath = $published
        TrustedRoot = $scratch
        DestinationMustBeAbsent = $true
    } 'DestinationMustBeAbsent with the destination present' 'asserted the destination was absent'

    # 3. A destination whose bytes changed since the caller looked.
    $temp3 = New-Staged 'changed' 'payload'
    Try-Publish @{
        TemporaryPath = $temp3
        DestinationPath = $published
        TrustedRoot = $scratch
        ExpectedDestinationSha256 = ('1' * 64)
    } 'a destination that changed since the caller inspected it' 'destination changed since'

    # 4. A destination that IS a protected build input.
    $temp4 = New-Staged 'protected' 'payload'
    $protectedInput = New-Staged 'an-input-this-build-read' 'do not clobber me'
    Try-Publish @{
        TemporaryPath = $temp4
        DestinationPath = $protectedInput
        TrustedRoot = $scratch
        ProtectedPaths = @($protectedInput)
    } 'a destination that is a protected build input' 'overwrite a protected build input'

    # 5. A destination outside the trusted root.
    $temp5 = New-Staged 'escape' 'payload'
    Try-Publish @{
        TemporaryPath = $temp5
        DestinationPath = (Join-Path $env:TEMP 'dp-atomic-escape.bin')
        TrustedRoot = $scratch
        # NAMED, like cases 1-4. This was '' -- which makes the match in Try-Publish `-like "**"`, so
        # ANY exception counted as proof. That matters here more than anywhere else in this suite: the
        # guard this case exercises raises "Trusted staging root is missing or is not a directory"
        # BEFORE it ever performs the containment test, so if $scratch has gone (a concurrent run under
        # the same %TEMP%, or the finally block of an earlier run) case 5 reported REFUSED on the wrong
        # exception and the escape guard was never exercised at all.
    } 'a destination outside the trusted root' 'escaped the trusted root'
}
finally {
    try { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    try { Remove-Item -LiteralPath (Join-Path $env:TEMP 'dp-atomic-escape.bin') -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host ("{0}/{1} refused." -f $fired, $cases)
if ($fired -ne $cases) {
    throw "Atomic publication checks: only $fired of $cases refused. A check that does not fire is not a check."
}
