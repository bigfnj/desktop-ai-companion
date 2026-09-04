#requires -Version 5
<#
.SYNOPSIS
    Authenticode-sign one or more files with signtool, or do nothing if no certificate is configured.

.DESCRIPTION
    Scaffolding for a code-signing certificate that does not exist yet. Every caller is opt-in on a
    thumbprint, so with no thumbprint supplied this is never invoked and an ordinary build is byte-identical
    to one from before signing existed. That property is not tidiness: build.yml runs both build.ps1 and
    build-installer.ps1 on every pull request, and neither has a certificate.

    Deliberately NOT modelled on the sibling runas-helper project's approach. That hangs signtool off MSBuild
    targets in a .wixproj (SignPublishedApps / SignMsi), and desktopPet has no .wixproj at all -- its MSI is
    produced by invoking the `wix` dotnet tool directly from build-installer.ps1. So the ordering that project
    expresses with AfterTargets/BeforeTargets has to be expressed here as explicit call sites instead.

.PARAMETER Path
    Files to sign. A file that is already validly signed by SOMEONE ELSE is skipped rather than re-signed:
    System.Numerics.Tensors.dll ships with a valid Microsoft signature, and signing over it would replace
    Microsoft's attestation with ours for a binary we did not build.

.PARAMETER Thumbprint
    SHA-1 thumbprint of a code-signing certificate in the current user's store. Empty means do nothing.

.PARAMETER TimestampUrl
    RFC3161 timestamp server. A timestamped signature outlives the certificate, at the cost of MSI
    byte-reproducibility (the token embeds the signing time), which is the property
    Normalize-MsiDeterminism.ps1 exists to preserve. Left to the caller as a conscious trade; empty skips it.

.PARAMETER Description
    What signtool records as the signed content's description (/d), shown in UAC and signature dialogs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [string]$Thumbprint = '',
    [string]$TimestampUrl = '',
    [string]$Description = 'Desktop AI Companion'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
    Write-Verbose 'No signing thumbprint supplied; leaving the payload unsigned.'
    return
}
if ($Thumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
    throw "A signing thumbprint must be 40 hex characters (SHA-1); got '$Thumbprint'."
}

# Resolve the newest SDK signtool rather than trusting PATH: a stale one in PATH signs with an older default
# digest, and /fd SHA256 on a tool that predates it fails in a way that reads as a certificate problem.
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
    Select-Object -First 1
if (-not $signtool) {
    throw 'signtool.exe was not found. Install the Windows SDK, or build without -SigningCertThumbprint.'
}

# Confirm the certificate is actually usable BEFORE signing anything, so a missing private key fails once
# here rather than once per file with a signtool error that does not say which part is wrong.
$certificate = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $Thumbprint.ToUpperInvariant() } |
    Select-Object -First 1
if (-not $certificate) {
    throw "No certificate with thumbprint $Thumbprint in Cert:\CurrentUser\My."
}
if (-not $certificate.HasPrivateKey) {
    throw "Certificate $Thumbprint has no private key in this store, so it cannot sign."
}
if ($certificate.NotAfter -lt (Get-Date)) {
    throw "Certificate $Thumbprint expired on $($certificate.NotAfter.ToString('yyyy-MM-dd'))."
}
Write-Host ("Signing with {0} (expires {1})" -f $certificate.Subject, $certificate.NotAfter.ToString('yyyy-MM-dd'))

foreach ($item in $Path) {
    $resolved = [IO.Path]::GetFullPath($item)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Nothing to sign at $resolved."
    }

    # Skip a file that already carries somebody else's valid signature.
    $existing = Get-AuthenticodeSignature -LiteralPath $resolved
    if ($existing.Status -eq [Management.Automation.SignatureStatus]::Valid -and
        $null -ne $existing.SignerCertificate -and
        $existing.SignerCertificate.Thumbprint -ne $Thumbprint.ToUpperInvariant()) {
        Write-Host ("  skip  {0} (already signed by {1})" -f
            [IO.Path]::GetFileName($resolved), $existing.SignerCertificate.Subject)
        continue
    }

    $arguments = @('sign', '/sha1', $Thumbprint, '/fd', 'SHA256')
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    $arguments += @('/d', $Description, $resolved)

    & $signtool.FullName @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $resolved (exit $LASTEXITCODE)." }

    # Verify what was just produced. /pa uses the Authenticode policy, which is what Windows itself applies;
    # signing can succeed while producing a signature that does not validate (an untrusted chain, most
    # obviously), and shipping that is worse than shipping nothing.
    & $signtool.FullName @('verify', '/pa', $resolved) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw ("signtool verify failed on $resolved. The signature was written but does not validate " +
               'under the Authenticode policy, usually an untrusted certificate chain.')
    }
    Write-Host ("  ok    {0}" -f [IO.Path]::GetFileName($resolved))
}
