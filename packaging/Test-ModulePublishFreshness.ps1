#requires -Version 5
<#
.SYNOPSIS
    Fail when a module's published zip is older than the module's source.

.DESCRIPTION
    modules-dist\<id>.zip is the payload the in-app catalog downloads and installs, and it is a
    committed artifact: nothing rebuilds it automatically, so it silently rots whenever module
    source lands without someone remembering to re-run New-ModuleDistZip.ps1 + New-ContentCatalog.ps1.

    That is not hypothetical. Twice in one day:
      * fortunes.zip shipped without the built-in fortune corpus, because the S3 move dropped the
        EmbeddedResource from the base csproj and the module never picked it up. A lean install had
        nothing to say, and nothing anywhere reported it.
      * aibrain.zip sat one release behind PR #71, so every catalog install got an AI Brain with no
        Windows OCR fallback -- i.e. no screen reading at all without a separate Tesseract install.

    The check is deliberately git-based rather than a rebuild-and-compare-hashes: comparing hashes
    would need the module DLLs to build byte-identically across SDK versions and checkout paths,
    which is a stronger promise than this repo makes today. Commit ordering is exact, cheap, and
    environment-independent -- if a path the payload is built FROM has a commit newer than the newest
    commit touching modules-dist/<id>.zip, the published payload cannot contain that change.

    "A path the payload is built from" is a per-module WATCH SET, not just modules/<Id>/. Until
    2026-08-27 it was just the module directory, which was blind to the two ways a payload changes
    without that directory being touched -- source-linked files out of src/ and tools/, and bundled
    ProjectReferences like ModuleKit. Both are live here, and the day the watch set was widened it
    immediately found fortunes, aibrain and petstudio all shipping a ModuleKit 3-4 commits stale.
    See Get-ModuleWatchSet for how the set is derived and what is deliberately excluded.

    Since 2026-11-10 it also judges CONTENT the publish route must produce: a committed agentbroker.zip at
    1.10.4 or later, or any agentbroker.zip whose bytes are not the preserved 1.10.3 history, must carry the
    bound provenance stamps (a plain rebuild and re-zip cannot), the STALE
    remedy for agentbroker names that bound route, and no module zip outside the exact LEGACY_PRESERVED
    history may ship a SourceLink record or an absolute document name in an embedded PDB. Findings are
    printed as zip names, DLL names, counts and hashes, never as the path that offended.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER ModuleId
    Check only this module. Defaults to every id listed in modules-dist\modules.json.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$ModuleId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

# git's EXIT CODE is the only reliable verdict from a native command in this file. Under Windows
# PowerShell 5.1 a redirected stderr line -- `2>$null` included -- becomes an ErrorRecord that honours
# $ErrorActionPreference = 'Stop', so the very case each exit-code branch was written for, git saying
# on stderr WHY it failed, terminated the script on the redirect before the branch was reached: a
# `git log` on a path outside the repository died as a raw 'fatal: ... is outside repository' instead
# of this script's own message (F212, reproduced under 5.1.26100; pwsh 7 is unaffected). Same shape
# as New-ModulePublish.ps1's Invoke-Git: errors non-terminating for the one call, judged by exit code,
# preference restored in the finally. Returns the stdout lines; the exit code lands in $script:GitExit.
$script:GitExit = 0
function Invoke-GitLines([string[]]$GitArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git -C $RepoRoot @GitArgs 2>&1)
        $script:GitExit = $LASTEXITCODE
        return @($output | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { [string]$_ })
    }
    finally { $ErrorActionPreference = $previous }
}

function Get-LastCommit([string]$path) {
    # -- separates the pathspec from revisions, so a path that looks like a ref cannot be mistaken
    # for one. Empty output = the path has no commits (untracked or never committed).
    $sha = (@(Invoke-GitLines @('log', '-1', '--format=%H', '--', $path)) -join '')
    if ($script:GitExit -ne 0) { throw "git log failed for '$path'." }
    if ([string]::IsNullOrWhiteSpace($sha)) { return $null }
    return $sha.Trim()
}

# The per-module WATCH SET -- every path outside modules\<Name>\ whose content still ends up inside the
# zip -- is derived in packaging\ModuleWatchSet.ps1, shared with New-ModulePublish.ps1 since 2026-09-30
# so the publish guard refuses exactly the dirt this check would later call stale (F215). What it watches,
# what it deliberately excludes (ProductVersion.props) and why the implicit MSBuild inputs joined it
# (F220) is recorded there, once.
. (Join-Path $PSScriptRoot 'ModuleWatchSet.ps1')

# One or more entries out of a zip, by entry NAME (case-insensitive), copied through the archive's own
# entry streams and never through a scratch directory. [IO.Compression.ZipFile] is not loaded by default
# under 5.1, hence the Add-Type; WixToolchainPolicy.ps1 hashes entries the same way.
#
# -ZipBytes opens the archive over bytes already in memory, read-only, so a caller can hash and scan ONE
# byte array (the build-path scan) or judge a COMMITTED blob that never touched the working tree (the
# AgentBroker stamp check).
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Read-ZipEntryBytes {
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Path')][string]$ZipPath,
        [Parameter(Mandatory = $true, ParameterSetName = 'Bytes')][byte[]]$ZipBytes,
        [Parameter(Mandatory = $true)][string[]]$EntryNames
    )
    $wanted = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entryName in $EntryNames) { [void]$wanted.Add($entryName) }
    $found = New-Object 'Collections.Generic.List[object]'
    if ($PSCmdlet.ParameterSetName -eq 'Bytes') {
        $backing = New-Object IO.MemoryStream -ArgumentList $ZipBytes, $false
        $archive = New-Object IO.Compression.ZipArchive -ArgumentList $backing, ([IO.Compression.ZipArchiveMode]::Read)
    }
    else { $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath) }
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $wanted.Contains($entry.Name)) { continue }
            $entryStream = $entry.Open()
            try {
                $memory = New-Object IO.MemoryStream
                try {
                    $entryStream.CopyTo($memory)
                    $found.Add([pscustomobject]@{ Name = $entry.Name; FullName = $entry.FullName; Bytes = $memory.ToArray() })
                }
                finally { $memory.Dispose() }
            }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    # The bare list, enumerated on output; callers wrap the call in @(). `return @($found)` throws
    # 'Argument types do not match' under Windows PowerShell 5.1 for a List[object] of custom objects
    # (measured 2026-09-30), and pwsh 7 does the same on this shape.
    return $found
}

# ---- helpers for the 2026-11-10 rules below. Definitions only: nothing here runs before the source
# discovery throw, which tests\Test-ModuleSourceDiscovery.ps1 requires to be this script's first failure. ----

# SHA-256 as lowercase hex, the form sha256sum and the legacy list use. Convert.ToHexString is .NET 5+.
function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

# git with its stdout taken as BYTES. `& git` decodes native stdout as text in both shells and would corrupt
# a binary blob, so the bytes come off the child's own stdout stream. stdin is not redirected, so the 5.1
# stdin-preamble trap recorded in ContentCatalogAssets.ps1 cannot arise. stderr is drained concurrently, so
# a chatty child cannot stall on a full pipe, and it is NEVER echoed: git's messages can carry the
# repository's absolute path. The verdict is the exit code, as everywhere else in this file.
function Invoke-GitBytes([string]$Repository, [string]$Arguments) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'git'
    $psi.Arguments = $Arguments
    $psi.WorkingDirectory = $Repository
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($psi)
    try {
        $stderr = $process.StandardError.ReadToEndAsync()
        $memory = New-Object IO.MemoryStream
        try {
            $process.StandardOutput.BaseStream.CopyTo($memory)
            $process.WaitForExit()
            $null = $stderr.Result
            return [pscustomobject]@{ ExitCode = $process.ExitCode; Bytes = $memory.ToArray() }
        }
        finally { $memory.Dispose() }
    }
    finally { $process.Dispose() }
}

# The bytes of a COMMITTED blob, never the working-tree file: the catalog serves the committed blob, so a
# corrected working tree must not excuse a wrong commit. Forward slashes only -- `HEAD:<path>` does not
# accept a backslash (ContentCatalogAssets.ps1 records the same trap). The size git reports for the object
# must equal the bytes received, so a truncated pipe cannot pass as a short blob.
function Read-CommittedBlobBytes {
    param([Parameter(Mandatory = $true)][string]$Repository, [Parameter(Mandatory = $true)][string]$Spec)
    if ($Spec -cnotmatch '^HEAD:[A-Za-z0-9._/-]+\z') {
        throw "COMMITTED_BLOB_SPEC: '$Spec' is not a HEAD:<forward-slash path> spec."
    }
    $size = Invoke-GitBytes $Repository ('cat-file -s ' + $Spec)
    if ($size.ExitCode -ne 0) {
        throw "COMMITTED_BLOB_MISSING: git cannot resolve $Spec (exit $($size.ExitCode)), so the committed payload cannot be read."
    }
    $expectedLength = 0L
    if (-not [long]::TryParse([Text.Encoding]::ASCII.GetString($size.Bytes).Trim(), [ref]$expectedLength)) {
        throw "COMMITTED_BLOB_SIZE: git cat-file -s $Spec did not print a byte count."
    }
    $blob = Invoke-GitBytes $Repository ('cat-file blob ' + $Spec)
    if ($blob.ExitCode -ne 0) { throw "COMMITTED_BLOB_READ: git cat-file blob $Spec failed (exit $($blob.ExitCode))." }
    if ($blob.Bytes.LongLength -ne $expectedLength) {
        throw "COMMITTED_BLOB_LENGTH: git cat-file blob $Spec returned $($blob.Bytes.LongLength) byte(s) where the object holds $expectedLength."
    }
    return ,$blob.Bytes
}

# ---- AgentBroker provenance stamps, read out of the image bytes ----
# A provenance build (docs\agentbroker-release-provenance.md) stamps AgentBroker.dll and the Worker with
#   [assembly: AssemblyMetadata("AgentBroker.SourceContentSha256", <64 uppercase hex>)]
#   [assembly: AssemblyMetadata("AgentBroker.SourceParentCommit",  <40 lowercase hex>)]
# (key names: packaging\AgentBrokerReleasePayloadMetadata.cs; formats: AgentBrokerReleaseProvenance.props)
# and drops the ordinary build's "+<commit>" InformationalVersion suffix. The compiler stores each attribute
# value as one #Blob entry: compressed blob length, prolog 01 00, SerString key, SerString value, NumNamed
# 00 00. Windows PowerShell 5.1 has no System.Reflection.Metadata, so this matches those exact blob shapes in
# the image bytes -- the fortunes check's Latin-1 technique -- and never loads the assembly.
#
# RECORDED LIMITS (PLAN.md): #Blob shares identical blobs, so two identical attribute rows read as one value
# (still one unambiguous value); a blob is not bound to its owning attribute type, so a same-shaped blob
# under another attribute is counted, which can only raise the key count and fail; and the suffix rule
# refuses ANY single-string attribute value ending in +<hex>, whichever attribute owns it. The product
# reader walks the CustomAttribute table exactly at Build time; this is the committed-payload backstop.
function Find-AgentBrokerStampValues {
    param([byte[]]$Image, [string]$Text, [string]$Key, [int]$ValueLength, [string]$ValuePattern)
    # 101 bytes for the 64-hex digest blob and 76 for the 40-hex parent blob: both below 0x80, so the
    # compressed blob length is the single byte four places ahead of the key.
    $blobLength = 2 + 1 + $Key.Length + 1 + $ValueLength + 2
    $values = New-Object 'Collections.Generic.List[string]'
    $keyCount = 0
    $at = $Text.IndexOf($Key, [StringComparison]::Ordinal)
    while ($at -ge 0) {
        $keyCount++
        $start = $at - 4
        $valueLengthAt = $at + $Key.Length
        $numNamedAt = $valueLengthAt + 1 + $ValueLength
        if ($start -ge 0 -and ($numNamedAt + 2) -le $Image.Length -and
            [int]$Image[$start] -eq $blobLength -and [int]$Image[$start + 1] -eq 1 -and [int]$Image[$start + 2] -eq 0 -and
            [int]$Image[$start + 3] -eq $Key.Length -and [int]$Image[$valueLengthAt] -eq $ValueLength -and
            [int]$Image[$numNamedAt] -eq 0 -and [int]$Image[$numNamedAt + 1] -eq 0) {
            $value = $Text.Substring($valueLengthAt + 1, $ValueLength)
            if ($value -cmatch $ValuePattern) { $values.Add($value) }
        }
        $at = $Text.IndexOf($Key, $at + 1, [StringComparison]::Ordinal)
    }
    return [pscustomobject]@{ KeyCount = $keyCount; Values = @($values.ToArray()) }
}

# Single-string attribute values (01 00, compressed length, the string, 00 00) whose string ends in
# +<7-64 hex>, the source-revision suffix an ordinary build appends to InformationalVersion. Found from the
# suffix backwards, so the string's own length prefix (one byte, or two from 128 bytes) must agree exactly;
# a two-string AssemblyMetadata value that happens to end in +<hex> does not match.
function Get-GitSuffixVersionBlobCount {
    param([byte[]]$Image, [string]$Text)
    $count = 0
    foreach ($candidate in [regex]::Matches($Text, '[+][0-9a-fA-F]{7,64}\x00\x00')) {
        $end = $candidate.Index + $candidate.Length - 2
        $start = $candidate.Index
        $found = $false
        while ($start -ge 3 -and ($end - $start) -le 16383) {
            $length = $end - $start
            if ($length -lt 128) {
                $found = ([int]$Image[$start - 1] -eq $length -and [int]$Image[$start - 2] -eq 0 -and [int]$Image[$start - 3] -eq 1)
            }
            elseif ($start -ge 4) {
                $found = ([int]$Image[$start - 2] -eq (0x80 -bor ($length -shr 8)) -and [int]$Image[$start - 1] -eq ($length -band 0xFF) -and
                          [int]$Image[$start - 3] -eq 0 -and [int]$Image[$start - 4] -eq 1)
            }
            # A NUL cannot sit inside the string, so the walk stops at the first one it would cross.
            if ($found -or [int]$Image[$start - 1] -eq 0) { break }
            $start--
        }
        if ($found) { $count++ }
    }
    return $count
}

function Get-AgentBrokerStampFacts {
    param([Parameter(Mandatory = $true)][byte[]]$Image)
    # Latin-1, so every byte maps to exactly one char and a char index is a byte offset.
    $text = [Text.Encoding]::GetEncoding(28591).GetString($Image)
    $source = Find-AgentBrokerStampValues -Image $Image -Text $text -Key 'AgentBroker.SourceContentSha256' -ValueLength 64 -ValuePattern '^[0-9A-F]{64}\z'
    $parent = Find-AgentBrokerStampValues -Image $Image -Text $text -Key 'AgentBroker.SourceParentCommit' -ValueLength 40 -ValuePattern '^[0-9a-f]{40}\z'
    return [pscustomobject]@{
        SourceKeyCount     = $source.KeyCount
        SourceValues       = $source.Values
        ParentKeyCount     = $parent.KeyCount
        ParentValues       = $parent.Values
        GitSuffixBlobCount = (Get-GitSuffixVersionBlobCount -Image $Image -Text $text)
    }
}

# The rule. Each problem is a fixed code, a fixed entry name and counts; a stamp value is printed only after
# it matched its hex-only pattern, so nothing else from the image can reach a message.
function Test-AgentBrokerPayloadStamps {
    param([Parameter(Mandatory = $true)][byte[]]$ZipBytes)
    $problems = New-Object 'Collections.Generic.List[string]'
    $entries = @(Read-ZipEntryBytes -ZipBytes $ZipBytes -EntryNames @('AgentBroker.dll', 'AgentBroker.Worker.dll'))
    $sources = @{}
    $parents = @{}
    foreach ($relative in @('AgentBroker.dll', 'worker/AgentBroker.Worker.dll')) {
        $matching = @($entries | Where-Object { $_.FullName.Replace('\', '/') -ieq $relative })
        if ($matching.Count -ne 1) {
            $problems.Add("AGENTBROKER_STAMP_ENTRY $relative -- expected exactly 1 zip entry, found $($matching.Count)")
            continue
        }
        $facts = Get-AgentBrokerStampFacts -Image $matching[0].Bytes
        if ($facts.SourceKeyCount -ne 1 -or $facts.SourceValues.Count -ne 1) {
            $problems.Add("AGENTBROKER_STAMP_SOURCE $relative -- AgentBroker.SourceContentSha256 key found " +
                          "$($facts.SourceKeyCount) time(s), $($facts.SourceValues.Count) with a well-formed 64-uppercase-hex value; expected exactly 1 of each")
        }
        else { $sources[$relative] = $facts.SourceValues[0] }
        if ($facts.ParentKeyCount -ne 1 -or $facts.ParentValues.Count -ne 1) {
            $problems.Add("AGENTBROKER_STAMP_PARENT $relative -- AgentBroker.SourceParentCommit key found " +
                          "$($facts.ParentKeyCount) time(s), $($facts.ParentValues.Count) with a well-formed 40-lowercase-hex value; expected exactly 1 of each")
        }
        else { $parents[$relative] = $facts.ParentValues[0] }
        if ($facts.GitSuffixBlobCount -gt 0) {
            $problems.Add("AGENTBROKER_STAMP_GIT_SUFFIX $relative -- $($facts.GitSuffixBlobCount) version string(s) end in " +
                          "+<hex>, the ordinary-build source-revision suffix")
        }
    }
    $source = $null
    $parent = $null
    if ($sources.Count -eq 2) {
        if ($sources['AgentBroker.dll'] -cne $sources['worker/AgentBroker.Worker.dll']) {
            $problems.Add("AGENTBROKER_STAMP_MISMATCH AgentBroker.SourceContentSha256 -- AgentBroker.dll carries " +
                          "$($sources['AgentBroker.dll']), worker/AgentBroker.Worker.dll carries $($sources['worker/AgentBroker.Worker.dll'])")
        }
        else { $source = $sources['AgentBroker.dll'] }
    }
    if ($parents.Count -eq 2) {
        if ($parents['AgentBroker.dll'] -cne $parents['worker/AgentBroker.Worker.dll']) {
            $problems.Add("AGENTBROKER_STAMP_MISMATCH AgentBroker.SourceParentCommit -- AgentBroker.dll carries " +
                          "$($parents['AgentBroker.dll']), worker/AgentBroker.Worker.dll carries $($parents['worker/AgentBroker.Worker.dll'])")
        }
        else { $parent = $parents['AgentBroker.dll'] }
    }
    return [pscustomobject]@{ Problems = @($problems.ToArray()); SourceContentSha256 = $source; SourceParentCommit = $parent }
}

# 1.10.4 is the first AgentBroker version published through the bound route. Every earlier version is
# published history (owner decision: preserve history, fix future packages); Test-AgentBrokerPayloadExempt
# below exempts it only while its committed bytes ARE that history. Compared as [version], never as text: a
# string comparison would exempt 1.10.10 and demand stamps of 1.9.9. A version that does not parse is
# refused, never exempted.
function Test-AgentBrokerStampRequired([string]$DeclaredVersion) {
    $parsed = $null
    if ([string]::IsNullOrWhiteSpace($DeclaredVersion) -or -not [version]::TryParse($DeclaredVersion, [ref]$parsed)) {
        throw ("AGENTBROKER_STAMP_VERSION: modules.json gives agentbroker the version '$DeclaredVersion', which is not a " +
               "version number, so whether its payload must carry provenance stamps cannot be decided. Refusing rather " +
               "than exempting it.")
    }
    return ($parsed -ge [version]'1.10.4')
}

# The bound route, named wherever this file would otherwise send an operator to a plain rebuild and re-zip.
# An ordinary AgentBroker build is unstamped, so following the generic remedy publishes bytes that never
# went through installed acceptance, and nothing else on that route refuses (review OB-1). Parameter names
# are the v2-v4 publisher, zipper and provenance helper's.
function Get-AgentBrokerBoundRouteText {
    return ("agentbroker is published only through the bound provenance route in docs\agentbroker-release-provenance.md, " +
            "never by a plain rebuild and re-zip: run packaging\AgentBrokerReleaseProvenance.ps1 -Mode Prepare, then " +
            "-Mode Build -ManifestPath <manifest> -ExpectedSha256 <source digest> -ExpectedParent <parent commit> and " +
            "-Mode VerifyOutput -OutputReceipt <receipt> -OutputReceiptSha256 <receipt sha256>; make the candidate with " +
            "packaging\New-ModuleDistZip.ps1 -ModuleId agentbroker -SourceDirectory <Release output> -DestinationPath " +
            "<candidate zip> -AgentBrokerOutputReceipt <receipt> -AgentBrokerOutputReceiptSha256 <receipt sha256>; " +
            "complete installed acceptance and the source commit; then publish with packaging\New-ModulePublish.ps1 " +
            "-ModuleId agentbroker -SkipBuild -AgentBrokerOutputReceipt <receipt> -AgentBrokerOutputReceiptSha256 " +
            "<receipt sha256> -ExpectedAgentBrokerZipSha256 <candidate zip sha256> -Commit, which commits the zip " +
            "before it regenerates the catalog.")
}

# The STALE remedy. Every module other than agentbroker keeps the sentence this file has always printed,
# byte for byte; agentbroker gets the bound route instead. -ieq, because -ModuleId keeps the caller's casing.
function Get-StalePayloadRemedy {
    param([Parameter(Mandatory = $true)][string[]]$StaleIds)
    $plain = @($StaleIds | Where-Object { $_ -ine 'agentbroker' })
    $bound = @($StaleIds | Where-Object { $_ -ieq 'agentbroker' })
    $parts = New-Object 'Collections.Generic.List[string]'
    if ($plain.Count -gt 0) {
        $parts.Add("Published module payload(s) are behind their source: " +
                   ($plain -join ', ') +
                   ". Rebuild (build.ps1 -Release), re-zip (packaging\New-ModuleDistZip.ps1), COMMIT the zip, " +
                   "then regenerate the catalog (packaging\New-ContentCatalog.ps1) -- in that order, because " +
                   "the catalog hashes the committed blob.")
    }
    if ($bound.Count -gt 0) {
        $parts.Add("Published module payload is behind its source: agentbroker. " + (Get-AgentBrokerBoundRouteText))
    }
    return ($parts.ToArray() -join ' ')
}

# ---- the exact published history that predates the SourceLink refusal ----
# Owner decision 2026-11-10, "preserve history; fix future packages": the eight module ZIPs committed at
# 50d666ad each ship a SourceLink map in their ModuleKit PDB, and they are kept, not rewritten or replaced.
# These are their exact SHA-256 values, a FIXED LITERAL that is never regenerated from the current tree. A
# zip is LEGACY_PRESERVED only while its bytes are exactly these, so a republish leaves the set by
# construction and must pass. Never add or edit an entry.
function Get-LegacyModuleZipBaselines {
    return @{
        'modules-dist/agentbroker.zip' = '5e2d0f5079c5b7883fb6fab9b028b7704e7e5c8cbbd8be6aeae4796446b16b1c'
        'modules-dist/agentflow.zip'   = '018a034f48ce95b78396ad2ddf5af0307dc17679c23b46a3917fcaa02125e861'
        'modules-dist/aibrain.zip'     = 'cc8409bba239b5bad5e83763225c603f65979ebe1bbb90eedb7efc74716ee968'
        'modules-dist/blinkingled.zip' = '92d2b5adf35a080500b2f5397699005bacd1a20d5a9e1c380e53a8262e56b3c1'
        'modules-dist/fortunes.zip'    = 'df99f7f06f79d115134be8ef10103739bfa41f34c6846d091f665e0ac7ff7fcb'
        'modules-dist/petstudio.zip'   = '2e9c9f8f2cd875f359c80f83ef6c82994ab957cd753eee01c2fd67f8edfadd86'
        'modules-dist/remembrance.zip' = 'adc1dcd2cb02f4469ea9a6f4e13ca3d8baea30ff4a294f6c76203f8d1991b3c2'
        'modules-dist/reminder.zip'    = 'b9740a606f06a4847d14d022f036d39b18fec87bc5a9f18332eade523e0a75f6'
    }
}

# Name AND exact hash: the same bytes under another name are not history.
function Test-LegacyModuleZip($Baselines, [string]$Name, [string]$Sha256) {
    $key = 'modules-dist/' + $Name
    return ($Baselines.ContainsKey($key) -and ([string]$Baselines[$key]) -ceq $Sha256)
}

# Rule 1's exemption is the exact published history, not a version number: below 1.10.4 AND HEAD's
# agentbroker.zip byte for byte the 50d666ad blob. A re-zip committed without a version bump is new bytes,
# so it must carry the stamps like any 1.10.4+ payload; the version alone would let a hand commit at 1.10.3
# ship unbound.
function Test-AgentBrokerPayloadExempt([string]$DeclaredVersion, [string]$CommittedSha256, $Baselines) {
    if (Test-AgentBrokerStampRequired $DeclaredVersion) { return $false }
    return (Test-LegacyModuleZip $Baselines 'agentbroker.zip' $CommittedSha256)
}

# One zip's build-path and SourceLink verdict, from ONE byte array: the SHA-256 that decides
# LEGACY_PRESERVED and the scan judge the same bytes. Offender strings carry the zip name, the DLL name and
# counts, never the path or name that offended. LEGACY_PRESERVED exempts the SourceLink refusal ONLY: the
# CodeView and absolute-document checks still apply, and the legacy bytes passed them when they shipped.
# Calls EmbeddedPortablePdb.ps1, which the build-path section dot-sources before the first call.
function Get-ModuleZipPrivacyVerdict {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][byte[]]$ZipBytes,
        # Not Mandatory: binding refuses an EMPTY collection there, and an empty set is a value to judge.
        $OurAssemblies,
        $Baselines,
        [Parameter(Mandatory = $true)][regex]$CodeViewPattern
    )
    $sha256 = Get-Sha256Hex $ZipBytes
    $legacy = Test-LegacyModuleZip $Baselines $Name $sha256
    $buildPath = New-Object 'Collections.Generic.List[string]'
    $sourceLink = New-Object 'Collections.Generic.List[string]'
    $dlls = 0
    $pdbs = 0
    $documents = 0
    $sourceLinkPdbs = 0
    # Every path found, summed across vectors and DLLs: one offender line carries a count, so the number of
    # lines is not the number of paths (one ModuleKit with fifteen absolute names is one line).
    $buildPathCount = 0
    $moduleKitWithoutPdb = $false
    foreach ($dll in @(Read-ZipEntryBytes -ZipBytes $ZipBytes -EntryNames @($OurAssemblies))) {
        $dlls++
        # Latin-1, so every byte maps to exactly one char. A UTF-8 decode can merge or drop bytes and a
        # path could slip through the gap. Counted, never quoted: the match IS a build path.
        $codeView = $CodeViewPattern.Matches([Text.Encoding]::GetEncoding(28591).GetString($dll.Bytes)).Count
        if ($codeView -gt 0) {
            $buildPathCount += $codeView
            $buildPath.Add($Name + ' -> ' + $dll.Name + " : $codeView CodeView build path(s)")
        }
        # The second and third vectors: the embedded PDB's Document table and its SourceLink record, parsed.
        try { $privacy = Get-EmbeddedPortablePdbPrivacySummary -Image $dll.Bytes }
        catch { throw ($Name + ' -> ' + $dll.Name + ' : ' + $_.Exception.Message) }
        $pdbs += $privacy.EmbeddedPdbCount
        $documents += $privacy.DocumentCount
        if ($dll.Name -ieq 'DesktopAICompanion.ModuleKit.dll' -and $privacy.EmbeddedPdbCount -eq 0) { $moduleKitWithoutPdb = $true }
        if ($privacy.AbsoluteDocumentCount -gt 0) {
            $buildPathCount += $privacy.AbsoluteDocumentCount
            $buildPath.Add($Name + ' -> ' + $dll.Name + " : $($privacy.AbsoluteDocumentCount) absolute embedded PDB document name(s)")
        }
        $sourceLinkPdbs += $privacy.SourceLinkPdbCount
        if ($privacy.SourceLinkPdbCount -gt 0 -and -not $legacy) {
            $sourceLink.Add($Name + ' -> ' + $dll.Name + " : SourceLink record in $($privacy.SourceLinkPdbCount) embedded PDB(s)")
        }
    }
    return [pscustomobject]@{
        Name                        = $Name
        Sha256                      = $sha256
        Legacy                      = $legacy
        DllCount                    = $dlls
        EmbeddedPdbCount            = $pdbs
        DocumentCount               = $documents
        SourceLinkPdbCount          = $sourceLinkPdbs
        ModuleKitWithoutEmbeddedPdb = $moduleKitWithoutPdb
        BuildPathCount              = $buildPathCount
        BuildPathOffenders          = @($buildPath.ToArray())
        SourceLinkOffenders         = @($sourceLink.ToArray())
        # These exact bytes were measured with one SourceLink map in their ModuleKit PDB (review
        # 2026-11-10), so a legacy zip that reads as SourceLink-free means the reader went blind.
        LegacyWitnessFailed         = ($legacy -and $sourceLinkPdbs -lt 1)
    }
}

# The SourceLink half of the success line, from every zip's verdict. Reached only when no zip outside the
# legacy set carried SourceLink, so a non-legacy zip is called clean only if at least one embedded PDB in it
# was actually read; a zip with none had nothing to judge and is counted apart. While any LEGACY_PRESERVED
# zip remains, the legacy WITNESS has just proved the reader sees SourceLink on real bytes; once the set is
# empty the line says the reader has no live witness and names the synthetic control that remains.
function Get-SourceLinkTallyLine {
    param($Verdicts)
    $clean = 0
    $nothingToJudge = 0
    $legacy = 0
    foreach ($verdict in @($Verdicts)) {
        if ($verdict.Legacy) { $legacy++ }
        elseif ($verdict.EmbeddedPdbCount -gt 0) { $clean++ }
        else { $nothingToJudge++ }
    }
    if ($legacy -gt 0) { $witness = "SourceLink reader witnessed live by the $legacy LEGACY_PRESERVED zip(s)." }
    else {
        $witness = ('SourceLink reader: no live witness; synthetic control tests\Test-ModulePublishFreshnessRules.ps1 ' +
                    '(run by tests\run-gate.ps1 and build.yml).')
    }
    return ("SourceLink: $clean zip(s) clean (embedded PDB judged), $nothingToJudge zip(s) with no embedded PDB to judge, " +
            "$legacy zip(s) LEGACY_PRESERVED (named above; exempt from the SourceLink refusal only, NOT clean). " + $witness)
}

$modulesJson = Join-Path $RepoRoot 'modules-dist\modules.json'
if (-not (Test-Path -LiteralPath $modulesJson)) {
    throw "modules.json not found at $modulesJson"
}

$ids = @()
foreach ($m in (Get-Content -LiteralPath $modulesJson -Raw | ConvertFrom-Json).modules) {
    $ids += $m.id
}

# Deliberately-unpublished modules. This check derives its id list from modules.json, so a module
# absent from that file produces ZERO findings -- its version parity and payload freshness are simply
# never examined. That is correct for a module nobody ships and wrong for one somebody forgot, and
# until this list existed the check could not tell the two apart. Adding a module to modules/ without
# publishing it now requires saying so here, once.
#
#   testmodule  a throwaway plugin-pipeline proof, dev and self-test only, never catalogued
#
# agentflow was on this list until 2026-09-17 and came off it when it was published, which is the
# point of keeping the list short: an entry here is a claim that nobody ships the module, and a stale
# one would quietly exempt a PUBLISHED module from every freshness and parity check in this file.
$deliberatelyUnpublished = @('testmodule')

$sourceIds = @()
. (Join-Path $PSScriptRoot 'ModuleSourceDiscovery.ps1')
foreach ($proj in @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'modules') -Directory)) {
    if (Test-LegacyTemplateBuildResidue -Directory $proj -RepoRoot $RepoRoot) {
        Write-Host 'NOTE templatecheck contains only legacy editor-generated obj/bin residue; no module source is exempted.'
        continue
    }
    $sourceIds += $proj.Name.ToLowerInvariant()
}
$unaccounted = @($sourceIds | Where-Object { ($ids -notcontains $_) -and ($deliberatelyUnpublished -notcontains $_) })
if ($unaccounted.Count -gt 0) {
    throw ("These modules exist under modules\ but are neither listed in modules.json nor declared " +
           "deliberately unpublished, so nothing checks their version parity or payload freshness: " +
           ($unaccounted -join ', ') + ". Publish them, or add them to " +
           "`$deliberatelyUnpublished in packaging\Test-ModulePublishFreshness.ps1 with a reason.")
}
if ($ModuleId) {
    if ($ids -notcontains $ModuleId) { throw "Module '$ModuleId' is not listed in modules.json." }
    $ids = @($ModuleId)
}

# modules.json's version per id, for the parity check below.
$declaredVersions = @{}
foreach ($m in (Get-Content -LiteralPath $modulesJson -Raw | ConvertFrom-Json).modules) {
    $declaredVersions[[string]$m.id] = [string]$m.version
}

# catalog.json's version per id. The catalog is generated FROM modules.json, but it is a separately
# committed artifact that the app actually fetches from master, so it can be stale on its own -- and it is
# the file ModuleUpdateScan compares against to decide whether to offer an update.
$catalogVersions = @{}
$catalogPath = Join-Path $RepoRoot 'catalog.json'
$catalogAppVersion = $null
if (Test-Path -LiteralPath $catalogPath) {
    $catalogObject = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    foreach ($m in $catalogObject.modules) {
        $catalogVersions[[string]$m.id] = [string]$m.version
    }
    if ($catalogObject.PSObject.Properties['app'] -and $catalogObject.app.PSObject.Properties['version']) {
        $catalogAppVersion = [string]$catalogObject.app.version
    }
}

# ---- catalog.json's app.version must equal ProductVersion.props ----
# This is the version the LAUNCH UPDATE CHECK compares against. AppUpdateCheck reads it from the catalog
# on master rather than the GitHub releases API (deliberately -- see the class comment there), so a stale
# app.version does not degrade the check, it SILENTLY INVERTS it: every user is told they are current.
#
# It rots because nothing in a host release touches the catalog. release.yml never regenerates it, and
# New-ContentCatalog.ps1 runs only as part of a MODULE publish, so a run of host releases with no module
# publish between them leaves the number behind. That happened for real: the catalog was last generated
# while ProductVersion.props said 1.1.0, then v1.1.1, v1.1.2 and v1.1.3 shipped -- three releases, the
# last of which fixed the tray icon -- and no user on 1.1.0 was ever offered any of them.
#
# New-ContentCatalog.ps1 already refuses to write a BLANK app.version for this reason. A blank one
# disables the check; a stale one is worse, because the check still runs and still answers "no".
$productVersionProps = Join-Path $RepoRoot 'ProductVersion.props'
[xml]$versionPropsXml = Get-Content -LiteralPath $productVersionProps
$productVersion = ([string]$versionPropsXml.Project.PropertyGroup.DesktopAICompanionVersion).Trim()
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    throw "Could not read <DesktopAICompanionVersion> from $productVersionProps."
}
if ($null -eq $catalogAppVersion) {
    throw ("catalog.json has no app.version. The launch update check reads the latest version from there, " +
           "so without it the app can never tell a user an update exists. Regenerate it: " +
           "packaging\New-ContentCatalog.ps1")
}
#
# The rule is NOT "catalog.json equals ProductVersion.props", which is what it used to be. Those two
# answer different questions: props declares the build version, while catalog.json advertises an
# update version. Between an ABI bump and the release that ships it
# they are legitimately different. The old rule forced them together, which left only two ways to
# land an ABI change -- cut a release immediately, or publish a catalog advertising a version that
# does not exist on the releases page. The second is the worse one and the old rule made it the path
# of least resistance.
#
# This offline check enforces two local metadata agreements. It does not query GitHub releases or
# verify downloadable assets. A local tag alone does not prove that a release succeeded or still exists.
#
#   1. The catalog must not EXCEED props. A catalog ahead of the build offers every user an update
#      that cannot be downloaded.
#   2. The catalog must MATCH THE NEWEST LOCAL VERSION TAG. This catches a catalog lagging behind
#      the local tag set. A failed, pending or pruned GitHub release can leave such a tag behind;
#      deciding published-release availability belongs to the separate app release process.
$catalogVersionParsed = $null
$productVersionParsed = $null
if (-not [version]::TryParse($catalogAppVersion, [ref]$catalogVersionParsed)) {
    throw "catalog.json app.version '$catalogAppVersion' is not a version number."
}
if (-not [version]::TryParse($productVersion, [ref]$productVersionParsed)) {
    throw "ProductVersion.props <DesktopAICompanionVersion> '$productVersion' is not a version number."
}
if ($catalogVersionParsed -gt $productVersionParsed) {
    throw ("catalog.json app.version is '$catalogAppVersion' but ProductVersion.props only says " +
           "'$productVersion'. The catalog is what every installed app reads to decide an update " +
           "exists, so this offers every user a version that has not been built and cannot be " +
           "downloaded. Regenerate the catalog (packaging\New-ContentCatalog.ps1) and commit it.")
}

# Check 2 needs tags. It FAILS rather than degrading, for the reason spelled out in the sibling
# Test-ModuleTemplate.ps1: a control that can run degraded has to say so in a way that stops the run,
# or it is a check that quietly stopped checking.
$releaseTags = @(Invoke-GitLines @('tag', '--list', 'v*'))
if ($script:GitExit -ne 0 -or $releaseTags.Count -eq 0) {
    Write-Warning ("DEGRADED  no v* tags are reachable, so catalog.json app.version could not be " +
                   "checked against the newest local version tag (shallow clone?)")
    throw ("Coverage narrowed silently: the catalog-versus-newest-local-tag check could not run because " +
           "no v* tags are reachable. Fetch tags (CI uses fetch-depth: 0) and re-run. This refuses " +
           "rather than passing without the local tag agreement. This offline check does not prove " +
           "that a GitHub release or its downloadable assets exists.")
}
$taggedVersions = @()
foreach ($releaseTag in $releaseTags) {
    $parsedTag = $null
    if ([version]::TryParse($releaseTag.TrimStart('v'), [ref]$parsedTag)) { $taggedVersions += $parsedTag }
}
if ($taggedVersions.Count -eq 0) {
    throw "No v* tag parsed as a version number, so the newest local version tag could not be determined."
}
$newestRelease = @($taggedVersions | Sort-Object -Descending | Select-Object -First 1)[0]
if ($catalogVersionParsed -ne $newestRelease) {
    throw ("catalog.json app.version is '$catalogAppVersion' but the newest local version tag is " +
           "'v$newestRelease'. This proves a local metadata disagreement, not release availability. " +
           "Verify the app release and its assets before changing the advertised catalog version; " +
           "a failed or pending release can leave a newer local tag behind.")
}
if ($catalogVersionParsed -lt $productVersionParsed) {
    Write-Host ("OK   app -- catalog.json app.version $catalogAppVersion matches the newest local version tag " +
                "v$newestRelease; ProductVersion.props is ahead at $productVersion (built, not released yet)")
}
else {
    Write-Host "OK   app -- catalog.json app.version $catalogAppVersion matches the newest local version tag and the declared build version"
}

$stale = @()
$degraded = @()
$mismatched = @()
foreach ($id in $ids) {
    # The module's source directory is capitalized (modules\Fortunes) while its id and zip are not;
    # resolve the real directory rather than assuming either casing survives a case-sensitive host.
    $sourceDirectory = Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'modules') -Directory |
        Where-Object { $_.Name -ieq $id } |
        Select-Object -First 1
    if (-not $sourceDirectory) {
        throw "Module '$id' is listed in modules.json but has no source directory under modules\."
    }

    # ---- version parity: source ModuleInfo.Version == modules.json == catalog.json ----
    # The in-app Update button compares the module's LIVE ModuleInfo.Version against the catalog's, so a
    # mismatch is not cosmetic: publish a catalog version below the shipped one and no update is ever
    # offered; publish one above it and the update is offered forever, surviving every install.
    $moduleClass = @(Get-ChildItem -LiteralPath $sourceDirectory.FullName -Filter '*Module.cs' -File)
    if ($moduleClass.Count -ne 1) {
        $mismatched += [pscustomobject]@{
            Id = $id
            Reason = "expected exactly one *Module.cs in $($sourceDirectory.Name), found $($moduleClass.Count)"
        }
    } else {
        $moduleSource = Get-Content -LiteralPath $moduleClass[0].FullName -Raw
        # Anchored to the start of the line: an unanchored 'Version\s*=' also matches MinHostVersion, which
        # sits two lines below it in every module.
        $versionMatches = @([regex]::Matches($moduleSource, '(?m)^\s*Version\s*=\s*"([^"]+)"'))
        if ($versionMatches.Count -ne 1) {
            $mismatched += [pscustomobject]@{
                Id = $id
                Reason = "found $($versionMatches.Count) ModuleInfo.Version declarations in $($moduleClass[0].Name); expected exactly 1"
            }
        } else {
            $sourceVersion = $versionMatches[0].Groups[1].Value
            $jsonVersion = $declaredVersions[$id]
            $catalogVersion = if ($catalogVersions.ContainsKey($id)) { $catalogVersions[$id] } else { '(absent)' }
            if ($sourceVersion -ne $jsonVersion -or $sourceVersion -ne $catalogVersion) {
                $mismatched += [pscustomobject]@{
                    Id = $id
                    Reason = "version mismatch -- source $sourceVersion, modules.json $jsonVersion, catalog.json $catalogVersion"
                }
            } else {
                Write-Host "OK   $id -- version $sourceVersion agrees across source, modules.json and catalog.json"
            }
        }
    }

    $zipRelative = "modules-dist/$id.zip"
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $zipRelative))) {
        $stale += [pscustomobject]@{ Id = $id; Reason = "$zipRelative is missing" }
        continue
    }

    $zipCommit = Get-LastCommit $zipRelative
    if (-not $zipCommit) {
        $stale += [pscustomobject]@{ Id = $id; Reason = "$zipRelative is not committed" }
        continue
    }

    # Commits touching anything the published zip is built from that it cannot possibly contain. The
    # pathspecs come from the shared helper (the module directory less its Markdown, plus the watch set)
    # so this check and the publish guard judge one and the same list; the Markdown exclusion and its
    # reason are recorded beside Get-ModuleWatchPathspecs.
    $watch = Get-ModuleWatchPathspecs -RepoRoot $RepoRoot -ModuleDirectory $sourceDirectory
    $sourceRelative = $watch.SourceRelative
    $modulePathspec = $watch.ModulePathspec
    # Fail loudly rather than silently narrowing: a watch set that shrinks without saying so is how this
    # check was blind to source-linked files for months.
    #
    # This said exactly that and then called Write-Warning, which sets no exit code, throws nothing,
    # is unaffected by $ErrorActionPreference and never reached $stale. Both callers judge this script
    # by failure alone, so the watch set COULD silently narrow -- a $(Property) in an Include, an
    # out-of-repo link, an unparseable csproj or a missing referenced project all degrade coverage and
    # still printed OK. The comment was right and the code did the opposite of it.
    foreach ($note in $watch.Degraded) {
        Write-Warning "$id -- watch set degraded: $note"
        $degraded += "$id -- watch set degraded: $note"
    }

    $watchedPathspecs = @($watch.Pathspecs)
    $newer = @(& git -C $RepoRoot log --format='%h %s' "$zipCommit..HEAD" -- @watchedPathspecs)
    if ($LASTEXITCODE -ne 0) { throw "git log failed comparing '$sourceRelative' against $zipCommit." }

    if ($newer.Count -gt 0) {
        # Attribute the staleness to the specific watched path(s), so the fix is obvious. Only done on the
        # failure path, so the common case stays one git call per module.
        $culprits = New-Object 'Collections.Generic.List[string]'
        # Per pathspec GROUP (a watched directory travels with its Markdown exclusions, RA-181), so a
        # README commit under ModuleKit is not counted against ModuleKit here either.
        foreach ($candidate in (@(, $modulePathspec) + @($watch.ExternalPathspecGroups))) {
            $hits = @(& git -C $RepoRoot log --format='%h' "$zipCommit..HEAD" -- @candidate)
            if ($hits.Count -gt 0) { $culprits.Add("$($candidate[0]) ($($hits.Count))") }
        }
        $stale += [pscustomobject]@{
            Id     = $id
            Reason = "$($newer.Count) commit(s) newer than $zipRelative touch: " + ($culprits -join ', ')
            Detail = $newer
        }
    } else {
        $extra = if ($watch.External.Count -gt 0) { " (+ $($watch.External.Count) linked/bundled path(s))" } else { '' }
        Write-Host "OK   $id -- $zipRelative is current with $sourceRelative$extra"
    }
}

if ($mismatched.Count -gt 0) {
    Write-Host ''
    foreach ($m in $mismatched) { Write-Host "MISMATCH $($m.Id) -- $($m.Reason)" }
    Write-Host ''
    throw ("Module version(s) disagree across source, modules-dist\modules.json and catalog.json: " +
           ($mismatched.Id -join ', ') +
           ". Bump modules-dist\modules.json to match the module's ModuleInfo.Version, then regenerate " +
           "the catalog (packaging\New-ContentCatalog.ps1). The in-app Update button compares these, so a " +
           "mismatch either offers an update forever or never offers one at all.")
}

# A narrowed watch set is a failure in its own right, not a note. It means this check is now
# examining LESS than it believes it is, which is the state it was written to prevent.
if ($degraded.Count -gt 0) {
    Write-Host ''
    Write-Host 'Watch set degraded -- this check is examining less than it should:' -ForegroundColor Red
    foreach ($note in $degraded) { Write-Host "  - $note" -ForegroundColor Red }
    throw ("$($degraded.Count) module watch set(s) degraded. Coverage narrowed silently, which is " +
           "how this check was blind to source-linked files for months.")
}

if ($stale.Count -gt 0) {
    Write-Host ''
    foreach ($s in $stale) {
        Write-Host "STALE $($s.Id) -- $($s.Reason)"
        if ($s.PSObject.Properties.Name -contains 'Detail') {
            foreach ($line in $s.Detail) { Write-Host "        $line" }
        }
    }
    Write-Host ''
    # Every other module keeps the remedy this line has always printed, byte for byte; agentbroker is sent
    # to the bound provenance route instead (Get-StalePayloadRemedy, review OB-1).
    throw (Get-StalePayloadRemedy -StaleIds @($stale.Id))
}

Write-Host "All $($ids.Count) published module payload(s) are current with their source."

# ---------------------------------------------------------------------------------------------------
# AGENTBROKER: THE COMMITTED PAYLOAD MUST BE A BOUND PROVENANCE BUILD (1.10.4 and later, and any bytes that
# are not the preserved 1.10.3 history).
#
# Every check above passes an ordinary build. Follow a plain rebuild and re-zip for agentbroker -- build.ps1
# -Release, New-ModuleDistZip with no receipt arguments, a hand commit -- and the recency check, the
# ModuleKit WITNESS and catalog integrity all stay green while master serves a ZIP with no provenance
# stamps, the "+<HEAD>" suffix, and code that never went through installed acceptance (review OB-1).
# New-ModulePublish refuses that route but nothing stops a hand commit, so the control sits at the point
# every route reaches: this file runs in CI, in the gate and as the publisher's final verify.
#
# It reads the COMMITTED blob, HEAD:modules-dist/agentbroker.zip, because that is the byte stream the
# catalog serves. It does NOT recompute the current source digest: ProductVersion.props is in that identity
# but deliberately outside the watch set, so a host version bump would false-fail here. It requires what an
# ordinary build cannot produce: exactly one well-formed stamp of each key in AgentBroker.dll and in
# worker/AgentBroker.Worker.dll, the same values in both, and no +<hex> version suffix. The one exemption is
# the published history itself: a version below 1.10.4 whose committed bytes are exactly the legacy 50d666ad
# blob. Any other bytes, at any version, must carry the stamps, so a re-zip committed without a version bump
# is judged like a release. Runs whatever -ModuleId says, like the content checks below.
if (-not $declaredVersions.ContainsKey('agentbroker')) {
    throw ("AGENTBROKER_STAMP_SCOPE: modules.json lists no agentbroker, so the provenance-stamp check has no payload " +
           "to judge. If AgentBroker was retired on purpose, remove this block with that reason; a check that quietly " +
           "stops checking is the failure this file keeps correcting.")
}
$agentBrokerDeclared = [string]$declaredVersions['agentbroker']
# Refuses an unparseable version before anything is read.
$agentBrokerVersionRequiresStamps = Test-AgentBrokerStampRequired -DeclaredVersion $agentBrokerDeclared
$committedAgentBroker = Read-CommittedBlobBytes -Repository $RepoRoot -Spec 'HEAD:modules-dist/agentbroker.zip'
$committedAgentBrokerSha256 = Get-Sha256Hex $committedAgentBroker
if (Test-AgentBrokerPayloadExempt -DeclaredVersion $agentBrokerDeclared -CommittedSha256 $committedAgentBrokerSha256 -Baselines (Get-LegacyModuleZipBaselines)) {
    Write-Host ("EXEMPT agentbroker -- modules.json declares $agentBrokerDeclared, below 1.10.4, the first bound " +
                "provenance release, and the committed agentbroker.zip (sha256 $committedAgentBrokerSha256) is exactly the " +
                "preserved published history, so it is not required to carry stamps")
}
else {
    $stampVerdict = Test-AgentBrokerPayloadStamps -ZipBytes $committedAgentBroker
    if ($stampVerdict.Problems.Count -gt 0) {
        if ($agentBrokerVersionRequiresStamps) { $stampScope = 'at or above 1.10.4, the first bound provenance release' }
        else { $stampScope = 'below 1.10.4 but its bytes are not the preserved published history' }
        Write-Host ''
        foreach ($problem in $stampVerdict.Problems) { Write-Host "  $problem" }
        Write-Host ''
        throw ("AGENTBROKER_PAYLOAD_UNBOUND: modules-dist/agentbroker.zip committed at HEAD (sha256 " +
               "$committedAgentBrokerSha256) is declared version $agentBrokerDeclared, $stampScope, and is not a bound " +
               "provenance build: $($stampVerdict.Problems.Count) stamp problem(s) listed above. " + (Get-AgentBrokerBoundRouteText))
    }
    Write-Host ("OK   agentbroker -- committed agentbroker.zip (sha256 $committedAgentBrokerSha256) carries one " +
                "source-content stamp $($stampVerdict.SourceContentSha256) and one parent-commit stamp " +
                "$($stampVerdict.SourceParentCommit) in both AgentBroker.dll and worker/AgentBroker.Worker.dll, and no " +
                "+<hex> version suffix")
}

# ---------------------------------------------------------------------------------------------------
# CONTENT check, not a recency check. Everything above compares COMMIT ORDER: is the zip's commit newer
# than the commits touching its sources. That is blind to a zip built from stale bits and committed
# afterwards, which is exactly what shipped: fortunes.zip was committed FIVE SECONDS after the commit
# that fixed the pack grouping, so every recency test called it current, while the DLL inside it had
# been built before the fix and was short by seven pack-to-collection mappings. The user saw them land
# in the fallback "More packs" group -- a regression that had already been fixed in the repo.
#
# Generalising this to every embedded resource means reading .NET manifest resources properly, which is
# more machinery than it is worth here. collections.json is checked because it is the one that broke,
# it is small, and it is pure data the emitter copies verbatim, so a substring search over the DLL is
# sufficient and cannot false-negative: if the current bytes are absent, the zip is stale.
$collectionsPath = Join-Path $repoRoot 'packs\collections.json'
$fortunesZip = Join-Path $repoRoot 'modules-dist\fortunes.zip'
# A MISSING INPUT IS A THROW, NOT A SKIP. This block sat behind `if (both exist)` with no else, so a
# renamed collections.json would have turned the one guard against a stale-built zip into a silent
# pass, in a file that refuses on every other narrowed run (F221). collections.json is a build input
# the Fortunes module embeds and fortunes.zip is a committed artifact, so neither has a legitimate
# absent case; the condition never consulted -ModuleId, so it was only ever a skip.
if (-not (Test-Path -LiteralPath $collectionsPath -PathType Leaf)) {
    throw "packs\collections.json is missing at $collectionsPath, so the pack-to-collection content check cannot run."
}
if (-not (Test-Path -LiteralPath $fortunesZip -PathType Leaf)) {
    throw "modules-dist\fortunes.zip is missing at $fortunesZip, so the pack-to-collection content check cannot run."
}
$expected = (Get-Content -LiteralPath $collectionsPath -Raw | ConvertFrom-Json)
$expectedPairs = [System.Collections.Generic.List[string]]::new()
foreach ($c in $expected.collections) {
    foreach ($s in $c.sources) { $expectedPairs.Add("$($c.name)`t$s") }
}

# ENTRY STREAMS, not Expand-Archive. This expanded the whole 49 MB payload -- the ONNX model and the
# runtime included -- into a scratch directory under TEMP to read one sub-megabyte DLL, and the CodeView
# scan below did the same for every zip: about 150 MB written and deleted per gate run, fortunes twice
# (F222). Read-ZipEntryBytes copies only the named entry through the archive's own stream. No scratch
# directory, so the best-effort Remove-Item that used to follow, and the orphan it could leave, are gone.
$shippedFortunes = @(Read-ZipEntryBytes -ZipPath $fortunesZip -EntryNames @('Fortunes.dll'))
if ($shippedFortunes.Count -eq 0) { throw "fortunes.zip contains no Fortunes.dll to inspect." }
$shipped = [Text.Encoding]::UTF8.GetString($shippedFortunes[0].Bytes)

# THE MAPPING, STRUCTURALLY. This used to search for each source id ON ITS OWN and use
# $expectedPairs only for counts, so a pack MOVED between collections left every id present
# and passed -- while the success line claimed all the mappings were embedded. Only an ADDED
# id was ever caught, which is what the original incident happened to be. The old comment
# said the search "cannot false-negative"; true of ids, false of mappings.
#
# collections.json is embedded verbatim as a resource, so the real thing is available: find
# it, parse it, and compare collection -> sources exactly. A move, a rename, a reorder
# between collections and a dropped source all fail now.
$embedded = $null
$atCollections = $shipped.IndexOf('"collections"', [StringComparison]::Ordinal)
while ($atCollections -ge 0 -and $null -eq $embedded) {
    # Walk back to the object that owns the key, then forward to its matching brace, counting
    # depth and skipping anything inside a string so a brace in a description cannot fool it.
    $start = $shipped.LastIndexOf('{', $atCollections)
    if ($start -lt 0) { break }
    $depth = 0; $inString = $false; $escaped = $false; $end = -1
    for ($i = $start; $i -lt $shipped.Length; $i++) {
        $ch = $shipped[$i]
        if ($escaped) { $escaped = $false; continue }
        if ($ch -eq '\') { $escaped = $true; continue }
        if ($ch -eq '"') { $inString = -not $inString; continue }
        if ($inString) { continue }
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    if ($end -gt $start) {
        try { $embedded = $shipped.Substring($start, $end - $start + 1) | ConvertFrom-Json }
        catch { $embedded = $null }
        if ($null -ne $embedded -and -not $embedded.PSObject.Properties['collections']) { $embedded = $null }
    }
    $atCollections = $shipped.IndexOf('"collections"', $atCollections + 1, [StringComparison]::Ordinal)
}
# THROWS rather than falling back to the old id-only search. A check that quietly downgrades
# itself to a weaker one is exactly the failure mode this file keeps correcting.
if ($null -eq $embedded) {
    throw ("Could not locate the embedded collections.json inside the shipped Fortunes.dll, so " +
           "the pack-to-collection mapping cannot be verified. If the module stopped embedding " +
           "it verbatim, this check needs rewriting rather than skipping.")
}

function Get-CollectionMap($doc) {
    $map = @{}
    foreach ($c in $doc.collections) {
        $src = @()
        if ($c.PSObject.Properties['sources'] -and $null -ne $c.sources) { $src = @($c.sources) }
        $map[[string]$c.name] = (($src | Sort-Object) -join '|')
    }
    return $map
}

$wantMap = Get-CollectionMap $expected
$gotMap = Get-CollectionMap $embedded
$problems = [System.Collections.Generic.List[string]]::new()
foreach ($name in $wantMap.Keys) {
    if (-not $gotMap.ContainsKey($name)) { $problems.Add("collection '$name' is absent from the shipped DLL"); continue }
    if ($wantMap[$name] -ne $gotMap[$name]) {
        $problems.Add("collection '$name' has different sources: expected [" +
                      ($wantMap[$name] -replace '\|', ', ') + "], shipped [" +
                      ($gotMap[$name] -replace '\|', ', ') + "]")
    }
}
foreach ($name in $gotMap.Keys) {
    if (-not $wantMap.ContainsKey($name)) { $problems.Add("collection '$name' is in the shipped DLL but not in packs/collections.json") }
}
if ($problems.Count -gt 0) {
    Write-Host ''
    foreach ($m in $problems | Select-Object -First 12) { Write-Host "  $m" }
    if ($problems.Count -gt 12) { Write-Host "  ... and $($problems.Count - 12) more" }
    throw ("modules-dist/fortunes.zip was built before the current packs/collections.json: " +
           "$($problems.Count) pack-to-collection difference(s) between the shipped Fortunes.dll " +
           "and packs/collections.json. Affected packs fall into the fallback 'More packs' group " +
           "for every user. REBUILD the module (New-ModulePublish.ps1 WITHOUT -SkipBuild) rather " +
           "than re-zipping, then commit the zip and regenerate the catalog.")
}
Write-Host ("fortunes.zip embeds all $($expectedPairs.Count) current pack-to-collection mappings " +
            "across $($wantMap.Count) collection(s), compared structurally.")

# ---------------------------------------------------------------------------------------------------
# NO SHIPPED DLL MAY CARRY AN ABSOLUTE BUILD PATH.
#
# An embedded PDB path names the machine, the account and the directory layout of whoever built the
# assembly. DebugType=embedded is what keeps the CodeView path out, set in ModuleKit.csproj and
# Contracts.csproj with a comment on each warning against re-adding IncludeSymbols (which fails dotnet
# pack with NU5017). The setting was fixed and the zips rebuilt on 2026-09-17; what did not exist until
# now is anything that would NOTICE it being reverted, so the fix had no regression net.
#
# TWO VECTORS, and the check used to see one. A path reaches a DLL through the CodeView entry of the
# debug directory, stored as a plain NUL-terminated string in the image, which the byte scan below
# finds. DebugType=embedded keeps that one bare (the file name alone) and then ships the WHOLE PDB,
# deflate-compressed inside an EmbeddedPortablePdb debug entry, whose Document table holds every source
# file's absolute path -- as shared PARTS joined by a separator, invisible to any regex over the image
# or over the inflated blob. Every ModuleKit.dll in every committed zip carried fifteen D:\ paths that
# way while this section printed "no embedded build paths in 14 of our own published DLL(s)" (RA-197;
# measured with PEReader on 2026-09-30, 13,892-byte entry inflating to 24,056). EmbeddedPortablePdb.ps1
# parses the PE debug directory, inflates the entry and reads the Document names, and an absolute one
# is an offender like a CodeView path. The BUILD-side cure is DeterministicSourcePaths (or a PathMap)
# in the projects that embed symbols, so document names become /_/...; a project without it fails here.
#
# EVERY DLL IN EVERY ZIP, deliberately, not the two files that were once wrong. The failure this
# guards is a NEW assembly arriving without the setting, and a check scoped to where the problem
# already was is exactly the kind that cannot fail.
#
# THE THIRD VECTOR: SourceLink (2026-11-10). A normal build also writes a SourceLink map into the embedded
# PDB, keyed on the checkout's absolute path -- and on the account name whenever the checkout sits under
# the user profile. It lives in a CustomDebugInformation row, so the CodeView scan and the Document names
# both pass while it ships: all eight committed zips carry one in their ModuleKit PDB (review
# 2026-11-10, measured counts-only). EmbeddedPortablePdb.ps1 detects the row by its kind GUID and the scan
# refuses it in every zip EXCEPT the exact published history below (Get-LegacyModuleZipBaselines), which the
# owner decided to preserve rather than rewrite. Those zips are reported LEGACY_PRESERVED on every run,
# named and counted and never called clean. The legacy exemption covers the SourceLink refusal ONLY.
#
# NOTHING THE SCAN FINDS IS QUOTED. The offender lines used to print the CodeView match and the absolute
# document name, which put the very path being refused into the gate log and CI output. They are counts now;
# document names never leave EmbeddedPortablePdb.ps1.
. (Join-Path $PSScriptRoot 'EmbeddedPortablePdb.ps1')
$buildPathZips = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules-dist') -Filter '*.zip' -ErrorAction SilentlyContinue)
if ($buildPathZips.Count -eq 0) { throw 'No modules-dist/*.zip to scan for embedded build paths.' }

$buildPathPattern = [regex] '[A-Za-z]:\\[^\x00<>|]{0,200}?\.pdb'
# An absolute document name (drive-rooted, drive-relative or UNC with either slash) is judged by
# Test-AbsoluteDocumentName in EmbeddedPortablePdb.ps1. Mapped names are rooted at /_/ (the SourceLink
# convention) and carry no drive.
$legacyBaselines = Get-LegacyModuleZipBaselines
$zipVerdicts = [System.Collections.Generic.List[object]]::new()
$legacyPreserved = [System.Collections.Generic.List[object]]::new()
$legacyWithoutSourceLink = [System.Collections.Generic.List[string]]::new()
$sourceLinkOffenders = [System.Collections.Generic.List[string]]::new()
$buildPathOffenders = [System.Collections.Generic.List[string]]::new()
$buildPathCount = 0
$buildPathDlls = 0
$embeddedPdbCount = 0
$embeddedDocumentCount = 0
# WITNESS for the reader: DesktopAICompanion.ModuleKit.csproj embeds its symbols (DebugType embedded, a
# recorded decision with its own comment there), so every ModuleKit.dll in every zip carries an
# EmbeddedPortablePdb entry. A ModuleKit.dll in which the reader finds none means the reader is blind
# to the entry or the payload was built without it, and either is a red, not a clean scan.
$moduleKitWithoutEmbeddedPdb = [System.Collections.Generic.List[string]]::new()

# SCOPED TO ASSEMBLIES THIS REPO BUILDS, and derived from the .csproj files rather than a hardcoded
# list -- so a NEW project of ours arriving without the setting is caught automatically, which is the
# whole property the backlog entry wanted.
#
# The first run of this check found 7 paths and every one belonged to a prebuilt third-party package:
# Microsoft.Windows.SDK.NET and WinRT.Runtime from Microsoft's agents (C:\__w\1\s, D:\a\_work\1\s)
# and onnxruntime from ONNX's (N:\_work\1). We cannot set DebugType on a NuGet binary, those paths
# leak somebody else's CI layout rather than this user's machine, and a check nobody can make pass is
# its own defect.
$ourAssemblies = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($proj in Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue) {
    if ($proj.FullName -like '*\obj\*' -or $proj.FullName -like '*\bin\*') { continue }
    # AssemblyName when declared, otherwise the project file name, which is msbuild's own default.
    $declared = $null
    try {
        $m = [regex]::Match((Get-Content -LiteralPath $proj.FullName -Raw), '<AssemblyName>\s*([^<]+?)\s*</AssemblyName>')
        if ($m.Success) { $declared = $m.Groups[1].Value }
    }
    catch { }
    # if/else, not a ternary: `?:` is PowerShell 7 syntax and a PARSE error under 5.1, which every
    # tracked script in this repo is checked against.
    $assemblyName = $declared
    if ([string]::IsNullOrWhiteSpace($assemblyName)) {
        $assemblyName = [IO.Path]::GetFileNameWithoutExtension($proj.Name)
    }
    $null = $ourAssemblies.Add($assemblyName + '.dll')
}
if ($ourAssemblies.Count -eq 0) { throw 'No .csproj found, so the build-path scan would check nothing.' }

# ENTRY STREAMS, not Expand-Archive. Every zip used to be expanded whole into a scratch directory under
# TEMP -- the 32 MB ONNX model, the 15 MB runtime and the 24 MB WinRT projection included -- to read the
# handful of sub-megabyte first-party DLLs this scan is scoped to; with the fortunes block above doing
# the same for one DLL, about 150 MB was written and deleted per gate run (F222). Only the named entries
# are copied now, and there is no scratch directory left to remove or to orphan.
#
# Each zip is read ONCE into memory (the largest is tens of MB) so its SHA-256 and its scan judge the same
# bytes; Get-ModuleZipPrivacyVerdict still copies only the named entries out of the archive.
foreach ($bpZip in $buildPathZips) {
    $bpArguments = @{
        Name            = $bpZip.Name
        ZipBytes        = [IO.File]::ReadAllBytes($bpZip.FullName)
        OurAssemblies   = $ourAssemblies
        Baselines       = $legacyBaselines
        CodeViewPattern = $buildPathPattern
    }
    $bpVerdict = Get-ModuleZipPrivacyVerdict @bpArguments
    $zipVerdicts.Add($bpVerdict)
    $buildPathCount += $bpVerdict.BuildPathCount
    $buildPathDlls += $bpVerdict.DllCount
    $embeddedPdbCount += $bpVerdict.EmbeddedPdbCount
    $embeddedDocumentCount += $bpVerdict.DocumentCount
    if ($bpVerdict.ModuleKitWithoutEmbeddedPdb) { $moduleKitWithoutEmbeddedPdb.Add($bpVerdict.Name) }
    foreach ($bpO in $bpVerdict.BuildPathOffenders) { $buildPathOffenders.Add($bpO) }
    foreach ($slO in $bpVerdict.SourceLinkOffenders) { $sourceLinkOffenders.Add($slO) }
    if ($bpVerdict.Legacy) {
        $legacyPreserved.Add($bpVerdict)
        if ($bpVerdict.LegacyWitnessFailed) { $legacyWithoutSourceLink.Add($bpVerdict.Name) }
    }
}

# Printed on every run, pass or fail: preserved history is named and counted, never folded into "clean".
foreach ($legacyZip in $legacyPreserved) {
    Write-Host ("LEGACY_PRESERVED $($legacyZip.Name) sha256 $($legacyZip.Sha256) -- published history kept as " +
                "committed (owner decision: preserve history, fix future packages); SourceLink record in " +
                "$($legacyZip.SourceLinkPdbCount) embedded PDB(s), exempt from the SourceLink refusal only, NOT clean")
}

if ($buildPathOffenders.Count -gt 0 -or $sourceLinkOffenders.Count -gt 0) {
    Write-Host ''
    foreach ($bpO in $buildPathOffenders | Select-Object -Unique | Select-Object -First 12) {
        Write-Host "  embedded build path: $bpO"
    }
    foreach ($slO in $sourceLinkOffenders | Select-Object -Unique | Select-Object -First 12) {
        Write-Host "  SourceLink record: $slO"
    }
}
if ($buildPathOffenders.Count -gt 0) {
    throw ("$buildPathCount embedded build path(s) found in published DLLs ($($buildPathOffenders.Count) DLL finding(s) " +
           "listed above, each with its count), which names the " +
           "machine, account and directory of whoever built them. For a CodeView path set " +
           "<DebugType>embedded</DebugType> in the offending project -- and do NOT add IncludeSymbols, which " +
           "fails dotnet pack with NU5017. For an embedded PDB document, set " +
           "<DeterministicSourcePaths>true</DeterministicSourcePaths> (or a <PathMap>) in that project so the " +
           "document names are rooted at /_/ instead of the build machine. Then rebuild, re-zip, COMMIT the " +
           "zip and regenerate the catalog.")
}
if ($sourceLinkOffenders.Count -gt 0) {
    throw ("$($sourceLinkOffenders.Count) published DLL(s) outside the LEGACY_PRESERVED set carry a SourceLink record in " +
           "their embedded PDB. A SourceLink map keys on the build checkout's absolute path, so it names the machine " +
           "and, when the checkout sits under the user profile, the account. Build the module with SourceLink off " +
           "(-p:EnableSourceLink=false -p:EmbedUntrackedSources=false on the build, or <EnableSourceLink>false</EnableSourceLink> " +
           "in the project), then re-zip, COMMIT the zip and regenerate the catalog; agentbroker's bound route " +
           "(docs\agentbroker-release-provenance.md) already builds that way. Published history is not rewritten: only " +
           "a zip whose bytes exactly equal its recorded legacy SHA-256 is exempt.")
}
if ($buildPathDlls -eq 0) {
    throw ('The build-path scan examined ZERO DLLs, so it proved nothing. Either the zips contain none ' +
           'of this repo''s own assemblies, or the .csproj-derived name set stopped matching what is ' +
           'shipped. Fix the scope rather than accepting a silent pass.')
}
if ($moduleKitWithoutEmbeddedPdb.Count -gt 0) {
    throw ("WITNESS failed: DesktopAICompanion.ModuleKit.dll in " + ($moduleKitWithoutEmbeddedPdb -join ', ') +
           " carries no embedded portable PDB, yet DesktopAICompanion.ModuleKit.csproj embeds its symbols. " +
           "Either the embedded-PDB reader has gone blind to the entry (then every document check above was " +
           "vacuous) or the payload was built without the setting; fix the one or record the other.")
}
# WITNESS for the SourceLink reader, while any legacy zip remains: those exact bytes were measured with one
# SourceLink map in their ModuleKit PDB, so reading none there means the reader went blind and every clean
# verdict above is vacuous. Once every legacy zip is republished this set is empty, and the success line
# below says the reader has no live witness. The synthetic controls in tests\Test-ModulePublishFreshnessRules.ps1
# then remain its only control; tests\run-gate.ps1 and build.yml run them beside this script.
if ($legacyWithoutSourceLink.Count -gt 0) {
    throw ("WITNESS failed: LEGACY_PRESERVED " + ($legacyWithoutSourceLink -join ', ') + " showed no SourceLink record, " +
           "yet those exact bytes were measured with one in their ModuleKit PDB. The SourceLink reader has gone blind, " +
           "so every clean verdict above is vacuous; fix the reader rather than accepting the pass.")
}
# Scoped to what this section judged on every zip. A LEGACY_PRESERVED zip still ships its SourceLink map, which
# keys on an absolute checkout path, so the line does not call those DLLs free of build paths.
Write-Host ("no CodeView path or absolute embedded-document name in $buildPathDlls of our own published DLL(s) across " +
            "$($buildPathZips.Count) zip(s): CodeView records scanned, $embeddedPdbCount embedded PDB(s) parsed with " +
            "$embeddedDocumentCount document name(s) inspected. " + (Get-SourceLinkTallyLine -Verdicts $zipVerdicts))
