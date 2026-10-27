#requires -Version 5.1
<#
.SYNOPSIS
    Read-only re-pin assistant for the AgentBroker native adapters.
.DESCRIPTION
    Checks the installed Codex desktop package, Codex VS Code extension, VS Code router and Claude Code executables,
    or ONE downloaded package (-Package): verifies each image's expected-publisher Authenticode signature, runs the
    OFFLINE public-source checks that docs/agentbroker-client-compatibility.md names for that vendor, and drafts the
    exact replacement pin as an entry of the embedded pin document (tools/AgentBroker.Pins/agentbroker-pins.json),
    with versioned evidence, for the owner to review.

    It never applies a pin. It executes no vendor file, opens no pipe, starts no client and reads no credential,
    session, transcript or configuration file. Installed mode writes nothing unless -OutputDirectory is given; package
    mode extracts only the members it reads, and writes nothing outside -OutputDirectory.

    Check states: PASS, FAIL, UNPROVEN (an offline check that could not run here, such as Node.js or PowerShell 7
    being unavailable), REVIEW (a fact of the new build only a person can establish by re-reading it; the reviewer of
    the draft pull request) and LIVE (live acceptance after publish; never inferred offline). A signature and matching
    markers alone never authorize a build.

    Verdicts: ALREADY PINNED (the exact image matches a reviewed pin), DRAFT FOR REVIEW (no check failed; the entry is
    drafted), NOT READY (at least one check failed; no draft). A draft is eligible for an automated pull request only
    when no check is UNPROVEN either (PullRequestEligible).
.PARAMETER Vendor
    Installed mode: Codex, Claude or All (default).
.PARAMETER Package
    Package mode: an npm tarball of @anthropic-ai/claude-code-win32-x64 (.tgz), that package extracted to a folder, or
    a marketplace VSIX of anthropic.claude-code or openai.chatgpt for win32-x64.
.PARAMETER ExpectedIntegrity
    For an npm tarball: the registry's published dist.integrity (sha512-...). Without it the integrity check is UNPROVEN.
.PARAMETER OutputDirectory
    Package mode (required) and -MergeEntry (required): a new or empty folder that receives the extracted members and
    result.json, entry.json and evidence.txt. Installed mode (optional): receives results.json and evidence.txt.
.PARAMETER PinDocument
    The pin document to compare with. Default: the repository's tools/AgentBroker.Pins/agentbroker-pins.json.
.PARAMETER MergeEntry
    An entry.json drafted by package mode: writes the pin document with that entry appended, in the canonical
    format, to -OutputDirectory\agentbroker-pins.json. Used by the scheduled check to prepare a pull request.
.PARAMETER Json
    Emit one JSON document instead of text.
.PARAMETER SelfTest
    Run the fixture checks only. Reads the repository's pin document and writes only its own temporary directory.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerRepin.ps1
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerRepin.ps1 -Package claude-code-win32-x64-2.1.311.tgz -ExpectedIntegrity sha512-... -OutputDirectory out
.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/agentbroker/Test-AgentBrokerRepin.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [ValidateSet('Codex', 'Claude', 'All')][string]$Vendor = 'All',
    [string]$Package,
    [string]$ExpectedIntegrity,
    [string]$OutputDirectory,
    [string]$PinDocument,
    [string]$MergeEntry,
    [switch]$Json,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:PinDocumentRelative = 'tools\AgentBroker.Pins\agentbroker-pins.json'

$script:OpenAIPublisher = 'OpenAI OpCo, LLC'
$script:AnthropicPublisher = 'Anthropic, PBC'
$script:MicrosoftPublisher = 'Microsoft Corporation'
$script:CodexPublisherId = '2p2nqsd0c76g0'
$script:ClaudeNpmPackage = '@anthropic-ai/claude-code-win32-x64'
# docs/agentbroker-client-compatibility.md, "Claude native inbox": native Windows inbox needs 2.1.248 or later.
$script:ClaudeVersionFloor = [Version]'2.1.248'
# The same bounds as the adapters' loaders: PinDocument.MaximumBytes and VsCodeManifestProfile.MaximumBytes.
$script:MaximumPinDocumentBytes = 256KB
$script:MaximumManifestBytes = 256KB
$script:LiveChecks = @(
    'live.exact-original-session', 'live.idle-wake-visible-reply', 'live.busy-ordering', 'live.held-refused-outcomes',
    'live.interruption', 'live.no-duplicate-delivery', 'live.approvals-and-sandbox-unchanged',
    'live.disconnect-reconnect', 'live.two-sessions-same-cwd', 'live.revocation', 'live.shutdown-cleanup')

# Public-source markers. Each is an exact string the compatibility document's evidence depends on.
$script:CodexDesktopMarkers = [ordered]@{
    'source.desktop.fixed-pipe'           = 'codex-ipc'
    'source.desktop.owner-discovery'      = 'thread-owner-discovery'
    'source.desktop.follower-start-turn'  = 'thread-follower-start-turn'
    'source.desktop.untrusted-input'      = 'supportsUntrustedAppInput'
    'source.desktop.inherit-settings'     = 'inheritThreadSettings'
    'source.desktop.busy-guard-error'     = 'App context must wait until the current turn finishes'
    'source.desktop.status-request'       = 'thread-stream-following-status-requested'
}
$script:CodexExtensionMarkers = [ordered]@{
    'source.vscode.fixed-pipe'            = 'codex-ipc'
    'source.vscode.owner-discovery'       = 'thread-owner-discovery'
    'source.vscode.follower-start-turn'   = 'thread-follower-start-turn'
    'source.vscode.untrusted-input'       = 'supportsUntrustedAppInput'
    'source.vscode.inherit-settings'      = 'inheritThreadSettings'
    'source.vscode.busy-guard-error'      = 'App context must wait until the current turn finishes'
    'source.vscode.thread-role'           = 'getThreadRole'
}
$script:ClaudeMarkers = [ordered]@{
    'source.claude.messaging-token-env'   = 'CLAUDE_CODE_MESSAGING_TOKEN'
    'source.claude.messaging-socket-env'  = 'CLAUDE_CODE_MESSAGING_SOCKET'
    'source.claude.cc-msg-pipe'           = 'cc-msg-'
    'source.claude.peer-token-record'     = 'peerToken'
    'source.claude.native-start-filetime' = 'procStartFt'
    'source.claude.inbound-policy'        = 'crossSessionInbound'
    'source.claude.self-sent-origin'      = 'selfSent'
}
# The bundles of a Codex VS Code extension that can hold the owner manager and renderer bridge.
$script:CodexBundlePattern = '^(app-main|app-initial|src)-[0-9A-Za-z_-]+\.js$'

function New-Check([string]$Name, [string]$Status, [string]$Reason) {
    [pscustomobject]@{ Name = $Name; Status = $Status; Reason = $Reason }
}

function Get-SubjectAttribute([string]$Subject, [string]$Key) {
    # RFC 4514-style display string: KEY=value or KEY="quoted, value". Returns every value for the key.
    $values = New-Object 'Collections.Generic.List[string]'
    $pattern = '(?:^|,\s*)' + [regex]::Escape($Key) + '=(?:"((?:[^"]|"")*)"|([^,]*))'
    foreach ($match in [regex]::Matches($Subject, $pattern)) {
        if ($match.Groups[1].Success) { $values.Add($match.Groups[1].Value.Replace('""', '"')) }
        else { $values.Add($match.Groups[2].Value.Trim()) }
    }
    return $values.ToArray()
}

function Test-Publisher($Signature, [string]$Expected, [string]$Name) {
    if ($null -eq $Signature) { return New-Check $Name 'FAIL' 'no Authenticode signature could be read' }
    if ([string]$Signature.Status -ne 'Valid') {
        return New-Check $Name 'FAIL' ("Windows did not verify the signature (" + $Signature.Status + ")")
    }
    $common = @(Get-SubjectAttribute ([string]$Signature.Subject) 'CN')
    $organization = @(Get-SubjectAttribute ([string]$Signature.Subject) 'O')
    if ($common.Count -ne 1 -or $organization.Count -ne 1 -or $common[0] -cne $Expected -or $organization[0] -cne $Expected) {
        return New-Check $Name 'FAIL' ("signer is not exactly '" + $Expected + "' (CN and O)")
    }
    return New-Check $Name 'PASS' ("valid signature by " + $Expected)
}

function Get-DefaultSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $subject = ''
    if ($null -ne $signature.SignerCertificate) { $subject = $signature.SignerCertificate.Subject }
    [pscustomobject]@{ Status = [string]$signature.Status; Subject = $subject }
}

function Get-DefaultHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }

function Get-DefaultVersion([string]$Path) { [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion }

function Test-DefaultProtectedFile([string]$Path) {
    # Packaged-app protection: owned by SYSTEM or TrustedInstaller, and no allow ACE grants write to anyone else.
    $allowed = @('S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $acl = Get-Acl -LiteralPath $Path
    $owner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
    if ($allowed -notcontains $owner) { return $false }
    $write = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, DeleteSubdirectoriesAndFiles, WriteAttributes, Delete, ChangePermissions, TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        if (($rule.FileSystemRights -band $write) -eq 0 -and ([int]$rule.FileSystemRights -band 0x50000000) -eq 0) { continue }
        if ($allowed -notcontains $rule.IdentityReference.Value) { return $false }
    }
    return $true
}

function Find-FileMarkers([string]$Path, $Markers) {
    # Streams the file in bounded chunks as Latin-1 so each ASCII marker is found byte-exactly. No decoding of
    # private data happens: only the presence of each fixed marker is returned.
    $found = @{}
    foreach ($key in $Markers.Keys) { $found[$key] = $false }
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $longest = 0
    foreach ($value in $Markers.Values) { if ($value.Length -gt $longest) { $longest = $value.Length } }
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'Read')
    try {
        $buffer = New-Object byte[] (4MB)
        $carry = ''
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $text = $carry + $latin1.GetString($buffer, 0, $read)
            foreach ($key in $Markers.Keys) {
                if (-not $found[$key] -and $text.IndexOf([string]$Markers[$key], [StringComparison]::Ordinal) -ge 0) { $found[$key] = $true }
            }
            $keep = [Math]::Min($text.Length, $longest)
            $carry = $text.Substring($text.Length - $keep)
        }
    }
    finally { $stream.Dispose() }
    return $found
}

function Find-ArchiveMarkers([string]$Archive, $Markers) {
    # Uses the reviewed read-only inspector. Without Node the archive checks stay UNPROVEN, never PASS.
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($null -eq $node) { return $null }
    $inspector = Join-Path $script:RepoRoot 'tools\agentbroker\inspect-public-asar.mjs'
    $members = @(& $node.Source $inspector $Archive --list-prefix '.vite/build/' 2>$null | ForEach-Object {
            try { ($_ | ConvertFrom-Json).member } catch { $null } } | Where-Object { $_ -and $_ -like '*.js' } | Select-Object -First 128)
    $found = @{}
    foreach ($key in $Markers.Keys) { $found[$key] = $false }
    $pattern = (@($Markers.Values) | ForEach-Object { [regex]::Escape($_) }) -join '|'
    foreach ($member in $members) {
        $hits = @(& $node.Source $inspector $Archive --member $member --pattern $pattern --context 0 --limit 64 2>$null)
        foreach ($line in $hits) {
            try { $excerpt = ($line | ConvertFrom-Json).excerpt } catch { continue }
            foreach ($key in $Markers.Keys) { if ([string]$excerpt -ceq [string]$Markers[$key]) { $found[$key] = $true } }
        }
    }
    return $found
}

# ---- The canonical manifest hash (VsCodeManifestProfile.ComputeSha256, ported) ----
# The adapter hashes package.json in canonical form: every object's members sorted ordinally, the ROOT __metadata
# member (VS Code's installation record) dropped, strings re-encoded by System.Text.Json's default writer and numbers
# kept as written, after refusing a duplicate name anywhere. This port calls the same System.Text.Json types, so it
# needs PowerShell 7; under Windows PowerShell 5.1 it returns $null and the check stays UNPROVEN.
function Get-ManifestFunctionalSha256([byte[]]$Bytes) {
    if ($PSVersionTable.PSVersion.Major -lt 7) { return $null }
    if ($Bytes.Length -gt $script:MaximumManifestBytes) { throw 'The manifest exceeds the adapter bound.' }
    $options = New-Object System.Text.Json.JsonDocumentOptions
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = 64
    # The byte overload the adapter uses (JsonDocument.Parse(byte[]) binds to ReadOnlyMemory<byte>), not the Stream
    # overload, so a byte order mark or any other input is accepted or refused exactly as the adapter does.
    $document = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($Bytes), $options)
    try {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'The manifest root is not an object.' }
        Assert-UniqueJsonNames $document.RootElement
        $canonical = New-Object IO.MemoryStream
        $writer = New-Object System.Text.Json.Utf8JsonWriter ($canonical, (New-Object System.Text.Json.JsonWriterOptions))
        try { Write-CanonicalJson $document.RootElement $writer $true; $writer.Flush() }
        finally { $writer.Dispose() }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($canonical.ToArray())) -replace '-', '').ToUpperInvariant() }
        finally { $sha.Dispose() }
    }
    finally { $document.Dispose() }
}

function Assert-UniqueJsonNames($Value) {
    # VsCodeManifestProfile.ValidateUniqueNames: the whole tree, root __metadata included, and every string decoded.
    switch ([string]$Value.ValueKind) {
        'Object' {
            $names = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            foreach ($property in @($Value.EnumerateObject())) {
                if (-not $names.Add($property.Name)) { throw 'The manifest repeats a member name.' }
                Assert-UniqueJsonNames $property.Value
            }
        }
        'Array' { foreach ($item in @($Value.EnumerateArray())) { Assert-UniqueJsonNames $item } }
        'String' { $null = $Value.GetString() }
    }
}

function Write-CanonicalJson($Value, $Writer, [bool]$Root) {
    switch ([string]$Value.ValueKind) {
        'Object' {
            $properties = @($Value.EnumerateObject())
            $sorted = [string[]]@($properties | ForEach-Object { $_.Name })
            [Array]::Sort($sorted, [StringComparer]::Ordinal)
            $Writer.WriteStartObject()
            foreach ($name in $sorted) {
                if ($Root -and $name -ceq '__metadata') { continue }
                $property = @($properties | Where-Object { $_.Name -ceq $name })[0]
                $Writer.WritePropertyName([string]$name)
                Write-CanonicalJson $property.Value $Writer $false
            }
            $Writer.WriteEndObject()
        }
        'Array' {
            $Writer.WriteStartArray()
            foreach ($item in $Value.EnumerateArray()) { Write-CanonicalJson $item $Writer $false }
            $Writer.WriteEndArray()
        }
        'String' { $Writer.WriteStringValue([string]$Value.GetString()) }
        'Number' { $Writer.WriteRawValue([string]$Value.GetRawText(), $false) }
        'True' { $Writer.WriteBooleanValue($true) }
        'False' { $Writer.WriteBooleanValue($false) }
        'Null' { $Writer.WriteNullValue() }
        default { throw 'The manifest has an unexpected JSON value.' }
    }
}

# ---- The pin document: read, write canonically, merge one entry ----
$script:PinLists = [ordered]@{
    claudeCode   = @('version', 'sha256')
    codexDesktop = @('packageFullName', 'applicationVersion', 'executableSha256', 'archiveSha256', 'electronVersion')
    codexVsCode  = @('version', 'directoryName', 'nativeImageSha256', 'manifestFunctionalSha256', 'images')
    vscodeRouter = @('productVersion', 'executableSha256')
}

function Read-PinDocument([string]$Path) {
    # The adapters' loader validates the document strictly at start; this reader needs the compared fields only, and
    # refuses a document it cannot read rather than guessing.
    if ((Get-Item -LiteralPath $Path).Length -gt $script:MaximumPinDocumentBytes) { throw "The pin document $Path is too large." }
    $document = [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false, $true))) | ConvertFrom-Json
    if ($null -eq $document -or $document.schema -ne 1) { throw "The pin document $Path is not schema 1." }
    foreach ($list in $script:PinLists.Keys) {
        if ($null -eq $document.PSObject.Properties[$list]) { throw "The pin document $Path has no $list list." }
    }
    return $document
}

function Get-PinnedProfiles([string]$Path) {
    $document = Read-PinDocument $Path
    $desktop = @(foreach ($pin in @($document.codexDesktop)) {
            [pscustomobject]@{ Package = [string]$pin.packageFullName; Version = [string]$pin.applicationVersion
                ExecutableSha256 = [string]$pin.executableSha256; ArchiveSha256 = [string]$pin.archiveSha256; Electron = [string]$pin.electronVersion } })
    $extension = @(foreach ($pin in @($document.codexVsCode)) {
            [pscustomobject]@{ Version = [string]$pin.version; NativeImageSha256 = [string]$pin.nativeImageSha256 } })
    $router = @(foreach ($pin in @($document.vscodeRouter)) {
            [pscustomobject]@{ Version = [string]$pin.productVersion; Sha256 = [string]$pin.executableSha256 } })
    $claude = @(foreach ($pin in @($document.claudeCode)) { [pscustomobject]@{ Version = [string]$pin.version; Sha256 = [string]$pin.sha256 } })
    [pscustomobject]@{ Desktop = $desktop; Extension = $extension; Router = $router; Claude = $claude; Document = $document }
}

function ConvertTo-PinJsonString([string]$Value) {
    if ($Value -match '[\x00-\x1f\x7f]') { throw 'A pin value holds a control character.' }
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function Get-PinMember($Entry, [string]$Name) {
    if ($Entry -is [Collections.IDictionary]) { return $Entry[$Name] }
    return $Entry.$Name
}

function ConvertTo-PinEntryText([string]$List, $Entry, [string]$Indent) {
    # The committed document's layout: two-member pins on one line, larger pins one member per line.
    $members = $script:PinLists[$List]
    if ($members.Count -eq 2) {
        return $Indent + '{ "' + $members[0] + '": ' + (ConvertTo-PinJsonString ([string](Get-PinMember $Entry $members[0]))) + ', "' +
            $members[1] + '": ' + (ConvertTo-PinJsonString ([string](Get-PinMember $Entry $members[1]))) + ' }'
    }
    $lines = New-Object 'Collections.Generic.List[string]'
    $lines.Add($Indent + '{')
    for ($index = 0; $index -lt $members.Count; $index++) {
        $name = $members[$index]
        $comma = ','
        if ($index -eq $members.Count - 1) { $comma = '' }
        if ($name -ceq 'images') {
            $images = Get-PinMember $Entry 'images'
            $pairs = @()
            if ($images -is [Collections.IDictionary]) { $pairs = @($images.Keys | ForEach-Object { , @([string]$_, [string]$images[$_]) }) }
            else { $pairs = @($images.PSObject.Properties | ForEach-Object { , @([string]$_.Name, [string]$_.Value) }) }
            $lines.Add($Indent + '  "images": {')
            for ($i = 0; $i -lt $pairs.Count; $i++) {
                $imageComma = ','
                if ($i -eq $pairs.Count - 1) { $imageComma = '' }
                $lines.Add($Indent + '    ' + (ConvertTo-PinJsonString $pairs[$i][0]) + ': ' + (ConvertTo-PinJsonString $pairs[$i][1]) + $imageComma)
            }
            $lines.Add($Indent + '  }' + $comma)
        }
        else { $lines.Add($Indent + '  "' + $name + '": ' + (ConvertTo-PinJsonString ([string](Get-PinMember $Entry $name))) + $comma) }
    }
    $lines.Add($Indent + '}')
    return ($lines -join "`n")
}

function ConvertTo-PinDocumentText($Document) {
    $lines = New-Object 'Collections.Generic.List[string]'
    $lines.Add('{')
    $lines.Add('  "schema": 1,')
    $lists = @($script:PinLists.Keys)
    for ($index = 0; $index -lt $lists.Count; $index++) {
        $list = $lists[$index]
        $comma = ','
        if ($index -eq $lists.Count - 1) { $comma = '' }
        $entries = @(Get-PinMember $Document $list)
        if ($entries.Count -eq 0) { $lines.Add('  "' + $list + '": []' + $comma); continue }
        $lines.Add('  "' + $list + '": [')
        for ($i = 0; $i -lt $entries.Count; $i++) {
            $entryComma = ','
            if ($i -eq $entries.Count - 1) { $entryComma = '' }
            $lines.Add((ConvertTo-PinEntryText $list $entries[$i] '    ') + $entryComma)
        }
        $lines.Add('  ]' + $comma)
    }
    $lines.Add('}')
    return (($lines -join "`n") + "`n")
}

function Get-PinMergeRefusal($Document, [string]$List, $Entry) {
    # The adapters' loader refuses these repeats (PinDocument.Unique), so a merge that would make one is refused here.
    $existing = @(Get-PinMember $Document $List)
    switch ($List) {
        'claudeCode' { $key = 'sha256' }
        'codexDesktop' { $key = 'packageFullName' }
        'codexVsCode' { $key = 'version' }
        'vscodeRouter' { $key = $null }
        default { return "unknown pin list '$List'" }
    }
    foreach ($pin in $existing) {
        if ($null -eq $key) {
            if ([string]$pin.productVersion -ceq [string](Get-PinMember $Entry 'productVersion') -and
                [string]$pin.executableSha256 -ceq [string](Get-PinMember $Entry 'executableSha256')) { return 'that router pair is already pinned' }
        }
        elseif ([string](Get-PinMember $pin $key) -ceq [string](Get-PinMember $Entry $key)) {
            return "the pin document already has a $List pin with that $key; a person must replace it rather than add a second"
        }
    }
    return $null
}

function Merge-PinEntry($Document, [string]$List, $Entry) {
    $refusal = Get-PinMergeRefusal $Document $List $Entry
    if ($refusal) { throw "The entry cannot be merged: $refusal." }
    $merged = [ordered]@{ schema = 1 }
    foreach ($name in $script:PinLists.Keys) {
        $entries = New-Object 'Collections.Generic.List[object]'
        foreach ($pin in @($Document.$name)) { $entries.Add($pin) }
        if ($name -ceq $List) { $entries.Add($Entry) }
        $merged[$name] = $entries.ToArray()
    }
    return $merged
}

# ---- Results ----
function New-Result([string]$Kind, [string]$Name, [string]$Version, [string]$Image) {
    [pscustomobject]@{ Kind = $Kind; Name = $Name; Version = $Version; Image = $Image; Sha256 = $null
        Pinned = $false; Checks = New-Object 'Collections.Generic.List[object]'; DraftList = $null; DraftEntry = $null
        DraftPin = $null; Verdict = $null; PullRequestEligible = $false; PullRequestBlockers = @(); Sources = @() }
}

function Complete-Result($Result, [scriptblock]$Draft) {
    foreach ($live in $script:LiveChecks) {
        $Result.Checks.Add((New-Check $live 'LIVE' 'live acceptance after publish (compatibility doc, "Required live acceptance"); never inferred offline'))
    }
    $failed = @($Result.Checks | Where-Object { $_.Status -eq 'FAIL' }).Count
    if ($Result.Pinned) { $Result.Verdict = 'ALREADY PINNED' }
    elseif ($failed -gt 0) { $Result.Verdict = 'NOT READY' }
    else {
        $Result.Verdict = 'DRAFT FOR REVIEW'
        $drafted = & $Draft
        $Result.DraftList = $drafted.List
        $Result.DraftEntry = $drafted.Entry
        $Result.DraftPin = ConvertTo-PinEntryText $drafted.List $drafted.Entry ''
    }
    # An automated pull request needs every offline check to have run and passed, and a complete entry. REVIEW and
    # LIVE items are the reviewer's and the owner's, listed in the pull request; UNPROVEN means a check could not run.
    $Result.PullRequestBlockers = @($Result.Checks | Where-Object { $_.Status -eq 'UNPROVEN' } | ForEach-Object { $_.Name })
    $Result.PullRequestEligible = $Result.Verdict -eq 'DRAFT FOR REVIEW' -and $Result.PullRequestBlockers.Count -eq 0 -and
        (Test-EntryComplete $Result.DraftEntry)
    return $Result
}

function Test-EntryComplete($Entry) {
    if ($null -eq $Entry) { return $false }
    foreach ($key in @($Entry.Keys)) {
        $value = $Entry[$key]
        if ($value -is [Collections.IDictionary]) { if ($value.Count -eq 0) { return $false } }
        elseif ([string]::IsNullOrEmpty([string]$value)) { return $false }
    }
    return $true
}

function ConvertTo-ClaudeVersion([string]$Value) {
    # Same normalization as EngineProfile.IsSupportedPublicVersion: a fourth part must be zero or absent.
    $parsed = $null
    if (-not [Version]::TryParse($Value, [ref]$parsed) -or $parsed.Build -lt 0 -or $parsed.Revision -gt 0) { return $null }
    return ('{0}.{1}.{2}' -f $parsed.Major, $parsed.Minor, $parsed.Build)
}

function Measure-ClaudeBuild([string]$Image, $Pins, $Providers) {
    $raw = [string](& $Providers.Version $Image)
    $version = ConvertTo-ClaudeVersion $raw
    if ($null -eq $version) { $version = $raw }
    $name = 'Claude Code CLI'
    if ($Image -match '\\anthropic\.claude-code-[^\\]+\\') { $name = 'Claude Code VS Code' }
    $result = New-Result 'claude' $name $version $Image
    $result.Checks.Add((Test-Publisher (& $Providers.Signature $Image) $script:AnthropicPublisher 'publisher.authenticode'))
    $parsed = $null
    if ([Version]::TryParse([string]$version, [ref]$parsed) -and $parsed -ge $script:ClaudeVersionFloor) {
        $result.Checks.Add((New-Check 'version.native-inbox-floor' 'PASS' ("$version is at or above $($script:ClaudeVersionFloor)")))
    }
    else { $result.Checks.Add((New-Check 'version.native-inbox-floor' 'FAIL' ("public version '$version' is not at or above $($script:ClaudeVersionFloor)"))) }
    $result.Sha256 = & $Providers.Hash $Image
    $result.Pinned = @($Pins.Claude | Where-Object { $_.Version -eq $version -and $_.Sha256 -eq $result.Sha256 }).Count -gt 0
    $markers = & $Providers.FileMarkers $Image $script:ClaudeMarkers
    foreach ($key in $script:ClaudeMarkers.Keys) {
        if ($markers[$key]) { $result.Checks.Add((New-Check $key 'PASS' ("public image contains '" + $script:ClaudeMarkers[$key] + "'"))) }
        else { $result.Checks.Add((New-Check $key 'FAIL' ("public image no longer contains '" + $script:ClaudeMarkers[$key] + "'"))) }
    }
    # Minified function names change per build, so the reviewed classification cannot be matched by pattern.
    $result.Checks.Add((New-Check 'source.claude.peer-token-classification' 'REVIEW' 'peer-token admission (fps/E, selfSent, mode parity) needs a person to re-read the new build; markers only show the strings remain'))
    $entry = [ordered]@{ version = $version; sha256 = $result.Sha256 }
    Add-MergeCheck $result $Pins 'claudeCode' $entry
    return Complete-Result $result { @{ List = 'claudeCode'; Entry = $entry } }
}

function Add-MergeCheck($Result, $Pins, [string]$List, $Entry) {
    # An unpinned build whose entry the document could not hold (a repeat the adapters' loader refuses) is not
    # draftable: replacing a reviewed pin is a person's edit, never an automated addition.
    if ($Result.Pinned -or $null -eq $Pins.Document) { return }
    $refusal = Get-PinMergeRefusal $Pins.Document $List $Entry
    if ($refusal) { $Result.Checks.Add((New-Check 'pin-document.mergeable' 'FAIL' $refusal)) }
    else { $Result.Checks.Add((New-Check 'pin-document.mergeable' 'PASS' "the entry adds to $List without a repeat the adapters' loader refuses")) }
}

function Measure-CodexDesktop($Package, $Pins, $Providers) {
    $root = [string]$Package.InstallLocation
    $image = Join-Path $root 'app\ChatGPT.exe'
    $archive = Join-Path $root 'app\resources\app.asar'
    $result = New-Result 'codex-desktop' 'Codex desktop' ([string]$Package.Version) $image
    $windowsApps = Join-Path $env:ProgramFiles 'WindowsApps'
    $name = Split-Path $root -Leaf
    $parts = $name.Split('_')
    if ($Package.PublisherId -eq $script:CodexPublisherId -and $parts.Count -eq 5 -and $parts[0] -eq 'OpenAI.Codex' -and $parts[1] -eq [string]$Package.Version -and
        $parts[3] -eq '' -and $parts[4] -eq $script:CodexPublisherId -and
        [string]::Equals((Split-Path $root -Parent), $windowsApps, [StringComparison]::OrdinalIgnoreCase)) {
        $result.Checks.Add((New-Check 'location.windowsapps-package' 'PASS' "$name under the WindowsApps root with publisher ID $($script:CodexPublisherId)"))
    }
    else { $result.Checks.Add((New-Check 'location.windowsapps-package' 'FAIL' "package '$name' is not the OpenAI.Codex family under WindowsApps")) }
    $result.Checks.Add((Test-Publisher (& $Providers.Signature $image) $script:OpenAIPublisher 'publisher.authenticode'))
    foreach ($file in @(@{ Name = 'location.protected-executable'; Path = $image }, @{ Name = 'location.protected-archive'; Path = $archive })) {
        if (& $Providers.Protected $file.Path) { $result.Checks.Add((New-Check $file.Name 'PASS' 'owned by SYSTEM/TrustedInstaller; no other principal can write')) }
        else { $result.Checks.Add((New-Check $file.Name 'FAIL' 'writable or owned outside SYSTEM/TrustedInstaller')) }
    }
    $electron = & $Providers.Version $image
    $result.Sha256 = & $Providers.Hash $image
    $archiveSha = & $Providers.Hash $archive
    $result.Pinned = @($Pins.Desktop | Where-Object { $_.Package -eq $name -and $_.ExecutableSha256 -eq $result.Sha256 -and
            $_.ArchiveSha256 -eq $archiveSha -and $_.Electron -eq $electron }).Count -gt 0
    $markers = & $Providers.ArchiveMarkers $archive $script:CodexDesktopMarkers
    foreach ($key in $script:CodexDesktopMarkers.Keys) {
        if ($null -eq $markers) { $result.Checks.Add((New-Check $key 'UNPROVEN' 'Node.js is unavailable, so the archive was not inspected')) }
        elseif ($markers[$key]) { $result.Checks.Add((New-Check $key 'PASS' ("a .vite/build member contains '" + $script:CodexDesktopMarkers[$key] + "'"))) }
        else { $result.Checks.Add((New-Check $key 'FAIL' ("no .vite/build member contains '" + $script:CodexDesktopMarkers[$key] + "'"))) }
    }
    $result.Checks.Add((New-Check 'source.desktop.idle-guard-order' 'REVIEW' 'the two pre-injection idle checks and their order need a person to re-read the owner manager'))
    $entry = [ordered]@{ packageFullName = $name; applicationVersion = [string]$Package.Version; executableSha256 = $result.Sha256
        archiveSha256 = $archiveSha; electronVersion = [string]$electron }
    Add-MergeCheck $result $Pins 'codexDesktop' $entry
    return Complete-Result $result { @{ List = 'codexDesktop'; Entry = $entry } }
}

function Measure-CodexExtension([string]$Directory, $Pins, $Providers) {
    $leaf = Split-Path $Directory -Leaf
    $version = $leaf -replace '^openai\.chatgpt-', '' -replace '-win32-x64$', ''
    $engine = Join-Path $Directory 'bin\windows-x86_64\codex.exe'
    $result = New-Result 'codex-vscode' 'Codex VS Code extension' $version $engine
    $result.Checks.Add((Test-Publisher (& $Providers.Signature $engine) $script:OpenAIPublisher 'publisher.engine-authenticode'))
    $result.Sha256 = & $Providers.Hash $engine
    $result.Pinned = @($Pins.Extension | Where-Object { $_.Version -eq $version -and $_.NativeImageSha256 -eq $result.Sha256 }).Count -gt 0
    $bundles = New-Object 'Collections.Generic.List[string]'
    $extensionJs = Join-Path $Directory 'out\extension.js'
    if (Test-Path -LiteralPath $extensionJs -PathType Leaf) { $bundles.Add($extensionJs) }
    $assets = Join-Path $Directory 'webview\assets'
    if (Test-Path -LiteralPath $assets -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $assets -File | Where-Object { $_.Name -match $script:CodexBundlePattern } | Sort-Object Name | Select-Object -First 32)) {
            $bundles.Add($file.FullName)
        }
    }
    $found = @{}
    $holders = New-Object 'Collections.Generic.List[string]'
    foreach ($key in $script:CodexExtensionMarkers.Keys) { $found[$key] = $false }
    foreach ($bundle in $bundles) {
        $hits = & $Providers.FileMarkers $bundle $script:CodexExtensionMarkers
        $any = $false
        foreach ($key in $script:CodexExtensionMarkers.Keys) { if ($hits[$key]) { $found[$key] = $true; $any = $true } }
        # Hashed: every bundle holding a reviewed marker, and every app-main and app-initial bundle, the families the
        # reviewed 26.1007.21434 pin drew from. Measured on that build's VSIX (2026-10-27) this drafts six bundles, a
        # superset of the five a person pinned; marker holders alone drafted three. A stricter pin, never a weaker one.
        if ($any -or (Split-Path $bundle -Leaf) -match '^(app-main|app-initial)-') { $holders.Add($bundle) }
    }
    foreach ($key in $script:CodexExtensionMarkers.Keys) {
        if ($found[$key]) { $result.Checks.Add((New-Check $key 'PASS' ("a public bundle contains '" + $script:CodexExtensionMarkers[$key] + "'"))) }
        else { $result.Checks.Add((New-Check $key 'FAIL' ("no public bundle contains '" + $script:CodexExtensionMarkers[$key] + "'"))) }
    }
    $manifestHash = $null
    $manifest = Join-Path $Directory 'package.json'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        $result.Checks.Add((New-Check 'source.vscode.manifest-functional-hash' 'FAIL' 'the extension has no package.json'))
    }
    else {
        try { $manifestHash = Get-ManifestFunctionalSha256 ([IO.File]::ReadAllBytes($manifest)) }
        catch { $result.Checks.Add((New-Check 'source.vscode.manifest-functional-hash' 'FAIL' ('package.json is not a canonical-hashable manifest: ' + $_.Exception.Message))) }
        if ($manifestHash) {
            $result.Checks.Add((New-Check 'source.vscode.manifest-functional-hash' 'PASS' "canonical functional hash $manifestHash (VsCodeManifestProfile.ComputeSha256, ported)"))
        }
        elseif (-not @($result.Checks | Where-Object { $_.Name -eq 'source.vscode.manifest-functional-hash' }).Count) {
            $result.Checks.Add((New-Check 'source.vscode.manifest-functional-hash' 'UNPROVEN' 'the canonical manifest hash needs PowerShell 7 (System.Text.Json); run under pwsh'))
        }
    }
    $result.Checks.Add((New-Check 'source.vscode.renderer-bridge-role' 'REVIEW' 'which hashed bundle owns the renderer bridge and owner manager needs a person to re-read the new build'))
    $images = [ordered]@{}
    foreach ($holder in $holders) {
        $relative = $holder.Substring($Directory.Length).TrimStart('\', '/').Replace('\', '/')
        $images[$relative] = & $Providers.Hash $holder
    }
    $entry = [ordered]@{ version = $version; directoryName = $leaf; nativeImageSha256 = $result.Sha256
        manifestFunctionalSha256 = $manifestHash; images = $images }
    Add-MergeCheck $result $Pins 'codexVsCode' $entry
    # Without the manifest hash the entry is incomplete: shown for review, never offered as a pull request.
    return Complete-Result $result { @{ List = 'codexVsCode'; Entry = $entry } }
}

function Measure-CodeRouter([string]$Image, $Pins, $Providers) {
    $version = & $Providers.Version $Image
    $result = New-Result 'vscode-router' 'VS Code router' $version $Image
    $result.Checks.Add((Test-Publisher (& $Providers.Signature $Image) $script:MicrosoftPublisher 'publisher.authenticode'))
    $result.Sha256 = & $Providers.Hash $Image
    $result.Pinned = @($Pins.Router | Where-Object { $_.Version -eq $version -and $_.Sha256 -eq $result.Sha256 }).Count -gt 0
    $entry = [ordered]@{ productVersion = [string]$version; executableSha256 = $result.Sha256 }
    Add-MergeCheck $result $Pins 'vscodeRouter' $entry
    return Complete-Result $result { @{ List = 'vscodeRouter'; Entry = $entry } }
}

function Get-InstalledResults([string]$Selected, $Pins, $Providers) {
    $results = New-Object 'Collections.Generic.List[object]'
    if ($Selected -in @('Codex', 'All')) {
        foreach ($package in @(& $Providers.CodexPackages)) { $results.Add((Measure-CodexDesktop $package $Pins $Providers)) }
        foreach ($directory in @(& $Providers.CodexExtensions)) { $results.Add((Measure-CodexExtension $directory $Pins $Providers)) }
        foreach ($router in @(& $Providers.CodeRouters)) { $results.Add((Measure-CodeRouter $router $Pins $Providers)) }
    }
    if ($Selected -in @('Claude', 'All')) {
        foreach ($image in @(& $Providers.ClaudeImages)) { $results.Add((Measure-ClaudeBuild $image $Pins $Providers)) }
    }
    return , $results.ToArray()
}

function Get-DefaultProviders {
    # Plain script blocks, not closures: each one resolves this script's functions and computes its own paths.
    @{
        Signature = { param($Path) Get-DefaultSignature $Path }
        Hash = { param($Path) Get-DefaultHash $Path }
        Version = { param($Path) Get-DefaultVersion $Path }
        Protected = { param($Path) Test-DefaultProtectedFile $Path }
        FileMarkers = { param($Path, $Markers) Find-FileMarkers $Path $Markers }
        ArchiveMarkers = { param($Path, $Markers) Find-ArchiveMarkers $Path $Markers }
        CodexPackages = {
            if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) {
                @(Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction SilentlyContinue | Where-Object { $_.PublisherId -eq $script:CodexPublisherId })
            }
        }
        CodexExtensions = {
            $extensions = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.vscode\extensions'
            if (Test-Path -LiteralPath $extensions) {
                @(Get-ChildItem -LiteralPath $extensions -Directory | Where-Object { $_.Name -match '^openai\.chatgpt-\d+\.\d+\.\d+-win32-x64$' } |
                    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin\windows-x86_64\codex.exe') } | ForEach-Object { $_.FullName })
            }
        }
        CodeRouters = {
            $code = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Microsoft VS Code\Code.exe'
            if (Test-Path -LiteralPath $code -PathType Leaf) { $code }
        }
        ClaudeImages = {
            $userHome = [Environment]::GetFolderPath('UserProfile')
            $standalone = Join-Path $userHome '.local\bin\claude.exe'
            if (Test-Path -LiteralPath $standalone -PathType Leaf) { $standalone }
            # The npm CLI: postinstall copies or links the win32-x64 package's claude.exe over this placeholder.
            $npm = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe'
            if (Test-Path -LiteralPath $npm -PathType Leaf) { $npm }
            $extensions = Join-Path $userHome '.vscode\extensions'
            if (Test-Path -LiteralPath $extensions) {
                @(Get-ChildItem -LiteralPath $extensions -Directory | Where-Object { $_.Name -match '^anthropic\.claude-code-\d+\.\d+\.\d+-win32-x64$' } |
                    ForEach-Object { Join-Path $_.FullName 'resources\native-binary\claude.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
            }
        }
    }
}

# ---- Package mode: one downloaded package, extracted member by member into the output folder ----
function Test-PlainMemberPath([string]$Path) {
    # Forward-slash relative member names only: no rooted path, drive, backslash, empty, "." or ".." segment.
    if ([string]::IsNullOrEmpty($Path) -or $Path.Length -gt 400 -or $Path -match '[\\:\x00-\x1f]' -or $Path.StartsWith('/')) { return $false }
    foreach ($segment in $Path.Split('/')) { if ($segment -eq '' -or $segment -eq '.' -or $segment -eq '..') { return $false } }
    return $true
}

function Resolve-InsideDirectory([string]$Root, [string]$Relative) {
    $full = [IO.Path]::GetFullPath((Join-Path $Root ($Relative.Replace('/', '\'))))
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "A package member would leave the output folder: $Relative" }
    return $full
}

function Get-FileSha512Sri([string]$Path) {
    $sha = [Security.Cryptography.SHA512]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return 'sha512-' + [Convert]::ToBase64String($sha.ComputeHash($stream)) }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Expand-NpmTarball([string]$Tarball, [string]$Destination) {
    # Windows' own bsdtar (System32\tar.exe), never a tar found on PATH. Every member name is checked before the two
    # members this check reads are extracted; bsdtar also refuses ".." and absolute names on its own.
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tar -PathType Leaf)) { throw 'Windows tar.exe is unavailable.' }
    $names = @(& $tar -tzf $Tarball)
    if ($LASTEXITCODE -ne 0) { throw 'The npm tarball could not be listed.' }
    foreach ($name in $names) { if (-not (Test-PlainMemberPath ([string]$name).TrimEnd('/'))) { throw "The npm tarball has an unsafe member name." } }
    foreach ($required in @('package/package.json', 'package/claude.exe')) {
        if ($names -cnotcontains $required) { throw "The npm tarball has no $required." }
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & $tar -xzf $Tarball -C $Destination 'package/package.json' 'package/claude.exe'
    if ($LASTEXITCODE -ne 0) { throw 'The npm tarball members could not be extracted.' }
    return (Join-Path $Destination 'package')
}

function Expand-VsixMembers($Zip, [string]$Destination, [scriptblock]$Wanted) {
    # Only members under extension/ that $Wanted selects, each re-checked against the output folder.
    foreach ($entry in @($Zip.Entries)) {
        $name = [string]$entry.FullName
        if (-not $name.StartsWith('extension/', [StringComparison]::Ordinal) -or $name.EndsWith('/')) { continue }
        $relative = $name.Substring('extension/'.Length)
        if (-not (& $Wanted $relative)) { continue }
        if (-not (Test-PlainMemberPath $relative)) { throw "The VSIX has an unsafe member name." }
        $target = Resolve-InsideDirectory $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        $source = $entry.Open()
        try {
            $output = [IO.File]::Open($target, 'CreateNew', 'Write', 'None')
            try { $source.CopyTo($output) } finally { $output.Dispose() }
        }
        finally { $source.Dispose() }
    }
}

function Read-ZipText($Zip, [string]$Name, [int]$Limit) {
    $entry = $Zip.GetEntry($Name)
    if ($null -eq $entry) { return $null }
    if ($entry.Length -gt $Limit) { throw "$Name exceeds $Limit bytes." }
    $reader = New-Object IO.StreamReader($entry.Open(), (New-Object Text.UTF8Encoding($false, $true)))
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Measure-Package([string]$Path, [string]$Integrity, [string]$Out, $Pins, $Providers) {
    $full = [IO.Path]::GetFullPath($Path)
    $sources = New-Object 'Collections.Generic.List[object]'
    $packageChecks = New-Object 'Collections.Generic.List[object]'
    $isFolder = Test-Path -LiteralPath $full -PathType Container
    if (-not $isFolder -and -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "The package $Path does not exist." }
    if (-not $isFolder) {
        $sources.Add([pscustomobject]@{ File = (Split-Path $full -Leaf); Bytes = (Get-Item -LiteralPath $full).Length; Sha256 = (Get-DefaultHash $full) })
    }
    if ($isFolder -or $full -match '\.tgz$') {
        if ($isFolder) { $root = $full; $packageChecks.Add((New-Check 'package.npm-integrity' 'UNPROVEN' 'an extracted folder has no tarball to compare with the published integrity')) }
        else {
            if ([string]::IsNullOrEmpty($Integrity)) { $packageChecks.Add((New-Check 'package.npm-integrity' 'UNPROVEN' 'no published dist.integrity was supplied (-ExpectedIntegrity)')) }
            elseif ($Integrity -notmatch '^sha512-[A-Za-z0-9+/]{86}==$') { $packageChecks.Add((New-Check 'package.npm-integrity' 'FAIL' 'the supplied integrity is not a sha512 SRI value')) }
            elseif ((Get-FileSha512Sri $full) -ceq $Integrity) { $packageChecks.Add((New-Check 'package.npm-integrity' 'PASS' "the tarball matches the registry's published $Integrity")) }
            else { $packageChecks.Add((New-Check 'package.npm-integrity' 'FAIL' "the tarball does not match the registry's published integrity")) }
            $root = Expand-NpmTarball $full (Join-Path $Out 'npm')
        }
        $manifest = [IO.File]::ReadAllText((Join-Path $root 'package.json'), (New-Object Text.UTF8Encoding($false, $true))) | ConvertFrom-Json
        $image = Join-Path $root 'claude.exe'
        if ([string]$manifest.name -ceq $script:ClaudeNpmPackage -and @($manifest.os) -contains 'win32' -and @($manifest.cpu) -contains 'x64') {
            $packageChecks.Add((New-Check 'package.identity' 'PASS' "$($script:ClaudeNpmPackage) $($manifest.version) for win32 x64"))
        }
        else { $packageChecks.Add((New-Check 'package.identity' 'FAIL' "package.json is not $($script:ClaudeNpmPackage) for win32 x64")) }
        $declared = [string]$manifest.version
    }
    elseif ($full -match '\.vsix$') {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($full)
        try {
            $vsixManifest = [xml](Read-ZipText $zip 'extension.vsixmanifest' 1MB)
            $identity = $vsixManifest.PackageManifest.Metadata.Identity
            $manifestText = Read-ZipText $zip 'extension/package.json' $script:MaximumManifestBytes
            if ($null -eq $identity -or $null -eq $manifestText) { throw 'The VSIX has no extension.vsixmanifest identity or extension/package.json.' }
            $manifest = $manifestText | ConvertFrom-Json
            $declared = [string]$manifest.version
            $id = ([string]$identity.Publisher).ToLowerInvariant() + '.' + ([string]$identity.Id).ToLowerInvariant()
            $consistent = [string]$identity.Version -ceq $declared -and ([string]$manifest.publisher).ToLowerInvariant() + '.' + ([string]$manifest.name).ToLowerInvariant() -ceq $id
            if ($id -notin @('anthropic.claude-code', 'openai.chatgpt') -or [string]$identity.TargetPlatform -cne 'win32-x64' -or -not $consistent -or
                $declared -notmatch '^\d+\.\d+\.\d+$') {
                $packageChecks.Add((New-Check 'package.identity' 'FAIL' "the VSIX is not a consistent win32-x64 anthropic.claude-code or openai.chatgpt package ($id $declared $($identity.TargetPlatform))"))
                $root = $null
            }
            else {
                $packageChecks.Add((New-Check 'package.identity' 'PASS' "$id $declared for win32-x64; the VSIX manifest and package.json agree"))
                $root = Join-Path $Out ($id + '-' + $declared + '-win32-x64')
                if ($id -ceq 'anthropic.claude-code') {
                    Expand-VsixMembers $zip $root { param($Relative) $Relative -ceq 'package.json' -or $Relative -ceq 'resources/native-binary/claude.exe' }
                    $image = Join-Path $root 'resources\native-binary\claude.exe'
                }
                else {
                    $pattern = $script:CodexBundlePattern
                    Expand-VsixMembers $zip $root { param($Relative) $Relative -ceq 'package.json' -or $Relative -ceq 'bin/windows-x86_64/codex.exe' -or
                        $Relative -ceq 'out/extension.js' -or ($Relative -like 'webview/assets/*' -and $Relative.Split('/').Count -eq 3 -and
                            $Relative.Split('/')[2] -match $pattern) }
                    $image = Join-Path $root 'bin\windows-x86_64\codex.exe'
                }
            }
        }
        finally { $zip.Dispose() }
        if ($null -eq $root) {
            $result = New-Result 'package' (Split-Path $full -Leaf) $declared $full
            foreach ($check in $packageChecks) { $result.Checks.Add($check) }
            $result.Sources = $sources.ToArray()
            return Complete-Result $result { $null }
        }
        if (-not (Test-Path -LiteralPath $image -PathType Leaf)) { throw "The VSIX has no native image at $image." }
    }
    else { throw 'A package is an npm .tgz, an extracted npm folder or a .vsix.' }

    if ($image -like '*codex.exe') { $result = Measure-CodexExtension $root $Pins $Providers }
    else {
        $result = Measure-ClaudeBuild $image $Pins $Providers
        $imageVersion = ConvertTo-ClaudeVersion ([string](& $Providers.Version $image))
        if ($imageVersion -and $imageVersion -ceq $declared) {
            $packageChecks.Add((New-Check 'package.version-matches-image' 'PASS' "the image's product version is the package version $declared"))
        }
        else { $packageChecks.Add((New-Check 'package.version-matches-image' 'FAIL' "the image's product version '$imageVersion' is not the package version '$declared'")) }
    }
    # Package checks go first, then the verdict is recomputed so a failed package check withholds the draft too.
    $merged = New-Object 'Collections.Generic.List[object]'
    foreach ($check in $packageChecks) { $merged.Add($check) }
    foreach ($check in @($result.Checks | Where-Object { $_.Status -ne 'LIVE' })) { $merged.Add($check) }
    $result.Checks = $merged
    $result.Sources = $sources.ToArray()
    # The measure function drafted its entry only if its own checks passed; the redo keeps that entry only if the
    # package checks passed too (a failed one makes the verdict NOT READY and the block below is never invoked).
    $draftList = $result.DraftList; $draftEntry = $result.DraftEntry
    $result.DraftList = $null; $result.DraftEntry = $null; $result.DraftPin = $null
    return Complete-Result $result { @{ List = $draftList; Entry = $draftEntry } }
}

function Write-Report($Results) {
    foreach ($result in $Results) {
        Write-Output ''
        Write-Output ('== ' + $result.Name + ' ' + $result.Version + '  [' + $result.Verdict + ']')
        Write-Output ('   image  ' + $result.Image)
        Write-Output ('   sha256 ' + $result.Sha256)
        foreach ($source in @($result.Sources)) { Write-Output ('   source ' + $source.File + ' (' + $source.Bytes + ' bytes, sha256 ' + $source.Sha256 + ')') }
        foreach ($check in $result.Checks) { Write-Output ('   {0,-8} {1,-42} {2}' -f $check.Status, $check.Name, $check.Reason) }
        if ($result.DraftPin) {
            Write-Output ('   draft "' + $result.DraftList + '" entry for owner review (NOT applied; REVIEW and LIVE items above remain):')
            foreach ($line in ([string]$result.DraftPin -split "`r?`n")) { Write-Output ('     ' + $line) }
            if ($result.PullRequestEligible) { Write-Output '   pull request: eligible (every offline check ran and passed)' }
            elseif (@($result.PullRequestBlockers).Count -gt 0) { Write-Output ('   pull request: not eligible; UNPROVEN: ' + (@($result.PullRequestBlockers) -join ', ')) }
            else { Write-Output '   pull request: not eligible; the entry is incomplete' }
        }
    }
    $notReady = @($Results | Where-Object { $_.Verdict -eq 'NOT READY' }).Count
    Write-Output ''
    Write-Output ("Summary: {0} build(s); {1} already pinned, {2} draft(s) for review, {3} not ready. No pin was applied." -f @($Results).Count,
        @($Results | Where-Object { $_.Verdict -eq 'ALREADY PINNED' }).Count, @($Results | Where-Object { $_.Verdict -eq 'DRAFT FOR REVIEW' }).Count, $notReady)
}

function ConvertTo-ResultJson($Results) {
    # Enumerate the checks through the pipeline: in PowerShell 7.6, @($list) on this List[object] threw "Argument types
    # do not match" for the Codex VS Code result (measured 2026-10-27), and the pipeline form does not.
    @($Results | ForEach-Object { $_.Checks = @($_.Checks | ForEach-Object { $_ }); $_ }) | ConvertTo-Json -Depth 8
}

function Initialize-OutputDirectory([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { throw '-OutputDirectory is required.' }
    $full = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Container) -or @(Get-ChildItem -LiteralPath $full -Force).Count -gt 0) {
            throw "The output folder $full must be new or empty."
        }
    }
    else { New-Item -ItemType Directory -Path $full | Out-Null }
    return $full
}

function Write-Utf8File([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false))) }

function Invoke-SelfTest {
    # Provider blocks below resolve $state dynamically from this function's scope, so fixtures can change it.
    function Assert([bool]$Condition, [string]$Label) {
        if (-not $Condition) { throw "FAIL: $Label" }
        $script:selfTestChecks++
        Write-Output "PASS: $Label"
    }
    $script:selfTestChecks = 0
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('agentbroker-repin-selftest-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixture | Out-Null
    try {
        # A pin document in the exact shape the adapters embed, so the reader is exercised against the real format.
        $sources = Join-Path $fixture 'repo'
        New-Item -ItemType Directory -Path (Join-Path $sources 'tools\AgentBroker.Pins') | Out-Null
        $hexA = 'A' * 64; $hexB = 'B' * 64; $hexC = 'C' * 64; $hexD = 'D' * 64; $hexE = 'E' * 64; $hexF = 'F' * 64
        $pinJson = '{"schema":1,"claudeCode":[{"version":"2.1.300","sha256":"' + $hexA + '"}],' +
            '"codexDesktop":[{"packageFullName":"OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0","applicationVersion":"1.0.0.0","executableSha256":"' + $hexA +
            '","archiveSha256":"' + $hexB + '","electronVersion":"1.2.3"},{"packageFullName":"OpenAI.Codex_1.0.1.0_x64__2p2nqsd0c76g0","applicationVersion":"1.0.1.0",' +
            '"executableSha256":"' + $hexC + '","archiveSha256":"' + $hexD + '","electronVersion":"1.2.3"}],' +
            '"codexVsCode":[{"version":"9.0.1","directoryName":"openai.chatgpt-9.0.1-win32-x64","nativeImageSha256":"' + $hexE +
            '","manifestFunctionalSha256":"' + $hexE + '","images":{"out/extension.js":"' + $hexE + '"}}],' +
            '"vscodeRouter":[{"productVersion":"1.1.0","executableSha256":"' + $hexF + '"}]}'
        $files = @{ $script:PinDocumentRelative = $pinJson }
        foreach ($entry in $files.GetEnumerator()) { [IO.File]::WriteAllText((Join-Path $sources $entry.Key), $entry.Value) }
        $before = @{}
        foreach ($entry in $files.GetEnumerator()) { $before[$entry.Key] = (Get-FileHash -LiteralPath (Join-Path $sources $entry.Key)).Hash }

        $pins = Get-PinnedProfiles (Join-Path $sources $script:PinDocumentRelative)
        Assert (@($pins.Desktop).Count -eq 2 -and $pins.Desktop[0].Version -eq '1.0.0.0' -and $pins.Desktop[1].ArchiveSha256 -eq $hexD) 'pin reader reads both desktop tuples exactly'
        Assert (@($pins.Extension).Count -eq 1 -and $pins.Extension[0].Version -eq '9.0.1' -and $pins.Extension[0].NativeImageSha256 -eq $hexE -and @($pins.Router).Count -eq 1 -and
            @($pins.Claude).Count -eq 1 -and $pins.Claude[0].Version -eq '2.1.300') 'pin reader reads extension, router and Claude pins exactly'

        # The committed document is in the canonical layout the scheduled check writes, so a drafted pull request
        # changes only the lines of its new entry.
        $repoPins = Join-Path $script:RepoRoot $script:PinDocumentRelative
        $committed = [IO.File]::ReadAllText($repoPins, (New-Object Text.UTF8Encoding($false, $true)))
        Assert ((ConvertTo-PinDocumentText (Read-PinDocument $repoPins)) -ceq $committed) 'the committed pin document is byte-identical to its canonical rewrite'

        $all = @{}
        foreach ($key in $script:ClaudeMarkers.Keys) { $all[$key] = $true }
        $state = @{ Signature = [pscustomobject]@{ Status = 'Valid'; Subject = 'CN="Anthropic, PBC", O="Anthropic, PBC", C=US' }
            Version = '2.1.310'; Hash = $hexC; Markers = $all; Protected = $true; ArchiveMarkers = $null }
        $providers = @{
            Signature = { param($Path) $state.Signature }
            Hash = { param($Path) $state.Hash }
            Version = { param($Path) $state.Version }
            Protected = { param($Path) $state.Protected }
            FileMarkers = { param($Path, $Markers) $state.Markers }
            ArchiveMarkers = { param($Path, $Markers) $state.ArchiveMarkers }
        }
        $image = Join-Path $fixture 'claude.exe'
        $new = Measure-ClaudeBuild $image $pins $providers
        Assert ($new.Verdict -eq 'DRAFT FOR REVIEW' -and $new.DraftList -ceq 'claudeCode' -and
            $new.DraftPin -ceq ('{ "version": "2.1.310", "sha256": "' + $hexC + '" }') -and $new.PullRequestEligible) 'a signed compatible new Claude build gets an exact claudeCode entry, eligible for a pull request'
        $state.Version = '2.1.310.0'
        $vsCodeImage = Join-Path $fixture 'anthropic.claude-code-2.1.310-win32-x64\resources\native-binary\claude.exe'
        $fourPart = Measure-ClaudeBuild $vsCodeImage $pins $providers
        Assert ($fourPart.Version -eq '2.1.310' -and $fourPart.Name -ceq 'Claude Code VS Code' -and $fourPart.DraftEntry.version -ceq '2.1.310') 'a four-part public version normalizes like EngineProfile and names the VS Code client'
        $state.Version = '2.1.310'
        Assert (@($new.Checks | Where-Object { $_.Status -eq 'LIVE' }).Count -eq $script:LiveChecks.Count -and
            @($new.Checks | Where-Object { $_.Name -eq 'live.busy-ordering' -and $_.Status -eq 'LIVE' }).Count -eq 1 -and
            @($new.Checks | Where-Object { $_.Name -eq 'source.claude.peer-token-classification' -and $_.Status -eq 'REVIEW' }).Count -eq 1 -and
            @($new.Checks | Where-Object { $_.Status -eq 'UNPROVEN' }).Count -eq 0) 'every live acceptance check is LIVE and the peer-token classification REVIEW, by name, never PASS'

        $missing = @{}; foreach ($key in $all.Keys) { $missing[$key] = $true }; $missing['source.claude.cc-msg-pipe'] = $false
        $state.Markers = $missing
        $changed = Measure-ClaudeBuild $image $pins $providers
        Assert ($changed.Verdict -eq 'NOT READY' -and $null -eq $changed.DraftPin -and -not $changed.PullRequestEligible -and
            @($changed.Checks | Where-Object { $_.Name -eq 'source.claude.cc-msg-pipe' -and $_.Status -eq 'FAIL' -and $_.Reason -like '*cc-msg-*' }).Count -eq 1) 'a missing protocol marker fails by name and withholds the draft'
        $state.Markers = $all

        foreach ($signature in @(
                [pscustomobject]@{ Status = 'Valid'; Subject = 'CN=Other Publisher, O=Other Publisher' },
                [pscustomobject]@{ Status = 'Valid'; Subject = 'CN="Anthropic, PBC", O=Other' },
                [pscustomobject]@{ Status = 'HashMismatch'; Subject = 'CN="Anthropic, PBC", O="Anthropic, PBC"' },
                [pscustomobject]@{ Status = 'NotSigned'; Subject = '' })) {
            $state.Signature = $signature
            $refused = Measure-ClaudeBuild $image $pins $providers
            Assert ($refused.Verdict -eq 'NOT READY' -and $null -eq $refused.DraftPin -and
                @($refused.Checks | Where-Object { $_.Name -eq 'publisher.authenticode' -and $_.Status -eq 'FAIL' }).Count -eq 1) ('a wrong publisher or invalid signature withholds the draft (' + $signature.Status + ')')
        }
        $state.Signature = [pscustomobject]@{ Status = 'Valid'; Subject = 'CN="Anthropic, PBC", O="Anthropic, PBC", C=US' }

        $state.Version = '2.1.300'; $state.Hash = $hexA
        $pinned = Measure-ClaudeBuild $image $pins $providers
        Assert ($pinned.Verdict -eq 'ALREADY PINNED' -and $null -eq $pinned.DraftPin -and -not $pinned.PullRequestEligible) 'an exactly pinned Claude image needs no replacement pin'
        $state.Hash = $hexB
        $repacked = Measure-ClaudeBuild $image $pins $providers
        Assert ($repacked.Verdict -eq 'DRAFT FOR REVIEW' -and -not $repacked.Pinned) 'WITNESS: a same-version image with a different hash is not treated as pinned'
        $state.Version = '2.1.301'; $state.Hash = $hexA
        $rehash = Measure-ClaudeBuild $image $pins $providers
        Assert ($rehash.Verdict -eq 'NOT READY' -and @($rehash.Checks | Where-Object { $_.Name -eq 'pin-document.mergeable' -and $_.Status -eq 'FAIL' }).Count -eq 1) 'an unpinned version whose hash another pin already names is not draftable (the loader refuses a repeated hash)'
        $state.Version = '2.1.200'
        $old = Measure-ClaudeBuild $image $pins $providers
        Assert ($old.Verdict -eq 'NOT READY' -and @($old.Checks | Where-Object { $_.Name -eq 'version.native-inbox-floor' -and $_.Status -eq 'FAIL' }).Count -eq 1) 'a build below the native inbox floor fails by name'

        # Codex desktop package in its WindowsApps location.
        $state.Version = '156.0.1.2'; $state.Hash = $hexF
        $state.Signature = [pscustomobject]@{ Status = 'Valid'; Subject = 'CN="OpenAI OpCo, LLC", O="OpenAI OpCo, LLC", L=San Francisco, C=US' }
        $desktopMarkers = @{}; foreach ($key in $script:CodexDesktopMarkers.Keys) { $desktopMarkers[$key] = $true }
        $state.ArchiveMarkers = $desktopMarkers
        $package = [pscustomobject]@{ Version = '26.2000.1.0'; PublisherId = '2p2nqsd0c76g0'
            InstallLocation = (Join-Path (Join-Path $env:ProgramFiles 'WindowsApps') 'OpenAI.Codex_26.2000.1.0_x64__2p2nqsd0c76g0') }
        $desktop = Measure-CodexDesktop $package $pins $providers
        Assert ($desktop.Verdict -eq 'DRAFT FOR REVIEW' -and $desktop.DraftList -ceq 'codexDesktop' -and
            $desktop.DraftEntry.packageFullName -ceq 'OpenAI.Codex_26.2000.1.0_x64__2p2nqsd0c76g0' -and $desktop.DraftEntry.executableSha256 -ceq $hexF -and
            $desktop.DraftEntry.electronVersion -ceq '156.0.1.2') 'a signed protected compatible desktop package gets an exact codexDesktop entry'
        $state.Protected = $false
        $unprotected = Measure-CodexDesktop $package $pins $providers
        Assert ($unprotected.Verdict -eq 'NOT READY' -and $null -eq $unprotected.DraftPin -and
            @($unprotected.Checks | Where-Object { $_.Name -like 'location.protected-*' -and $_.Status -eq 'FAIL' }).Count -eq 2) 'an unprotected package location fails by name and withholds the draft'
        $state.Protected = $true
        $state.ArchiveMarkers = $null
        $noNode = Measure-CodexDesktop $package $pins $providers
        Assert (@($noNode.Checks | Where-Object { $_.Name -eq 'source.desktop.fixed-pipe' -and $_.Status -eq 'UNPROVEN' }).Count -eq 1 -and
            $noNode.Verdict -eq 'DRAFT FOR REVIEW' -and -not $noNode.PullRequestEligible -and $noNode.PullRequestBlockers -contains 'source.desktop.fixed-pipe') 'an uninspected archive stays UNPROVEN, never PASS, and blocks an automated pull request by name'
        $state.ArchiveMarkers = $desktopMarkers
        $foreign = Measure-CodexDesktop ([pscustomobject]@{ Version = '26.2000.1.0'; PublisherId = 'otherpublisher'; InstallLocation = (Join-Path $fixture 'OpenAI.Codex_26.2000.1.0_x64__otherpublisher') }) $pins $providers
        Assert ($foreign.Verdict -eq 'NOT READY' -and @($foreign.Checks | Where-Object { $_.Name -eq 'location.windowsapps-package' -and $_.Status -eq 'FAIL' }).Count -eq 1) 'a package outside the OpenAI WindowsApps family fails by name'

        # Codex VS Code extension with real bundle files holding the markers.
        $extension = Join-Path $fixture 'openai.chatgpt-26.2000.5-win32-x64'
        foreach ($dir in @('bin\windows-x86_64', 'out', 'webview\assets')) { New-Item -ItemType Directory -Path (Join-Path $extension $dir) | Out-Null }
        [IO.File]::WriteAllText((Join-Path $extension 'bin\windows-x86_64\codex.exe'), 'synthetic engine')
        [IO.File]::WriteAllText((Join-Path $extension 'package.json'), '{"name":"chatgpt","__metadata":{"size":1},"version":"26.2000.5"}')
        [IO.File]::WriteAllText((Join-Path $extension 'out\extension.js'), 'const pipe="codex-ipc"; getThreadRole(); "thread-owner-discovery";')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\app-main-0a1b.js'), '"thread-follower-start-turn";supportsUntrustedAppInput')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\src-0c0d.js'), 'inheritThreadSettings;"App context must wait until the current turn finishes"')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\code-review-0e0f.js'), 'supportsUntrustedAppInput')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\app-initial-ffff.js'), 'unrelated')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\src-dead.js'), 'unrelated')
        $realMarkers = { param($Path, $Markers) Find-FileMarkers $Path $Markers }
        $extensionProviders = $providers.Clone(); $extensionProviders.FileMarkers = $realMarkers
        $extensionProviders.Hash = { param($Path) Get-DefaultHash $Path }
        $code = Measure-CodexExtension $extension $pins $extensionProviders
        $images = $code.DraftEntry.images
        Assert ($code.Verdict -eq 'DRAFT FOR REVIEW' -and $code.DraftList -ceq 'codexVsCode' -and $code.DraftEntry.version -ceq '26.2000.5' -and
            $code.DraftEntry.directoryName -ceq 'openai.chatgpt-26.2000.5-win32-x64' -and
            $images['out/extension.js'] -ceq (Get-DefaultHash (Join-Path $extension 'out\extension.js')) -and
            $images.Contains('webview/assets/app-main-0a1b.js') -and $images.Contains('webview/assets/src-0c0d.js') -and
            $images.Contains('webview/assets/app-initial-ffff.js') -and -not $images.Contains('webview/assets/src-dead.js') -and
            -not $images.Contains('webview/assets/code-review-0e0f.js')) 'extension entry hashes every marker-holding bundle and every app-main and app-initial bundle, and no other'
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            # The adapter's own test vector (VsCodeManifestChecks.cs "canonical"): sorted members, root __metadata dropped.
            $vector = Get-ManifestFunctionalSha256 ([Text.Encoding]::UTF8.GetBytes('{"z":true,"a":{"y":"v","x":null},"array":[1,2],"__metadata":{"when":1}}'))
            $expected = (Get-FileHash -InputStream (New-Object IO.MemoryStream (, [Text.Encoding]::UTF8.GetBytes('{"a":{"x":null,"y":"v"},"array":[1,2],"z":true}'))) -Algorithm SHA256).Hash
            Assert ($vector -ceq $expected) 'the ported canonical manifest hash reproduces the adapter test vector byte for byte'
            $ordinal = (Get-FileHash -InputStream (New-Object IO.MemoryStream (, [Text.Encoding]::UTF8.GetBytes('{"B":2,"b":1}'))) -Algorithm SHA256).Hash
            Assert ((Get-ManifestFunctionalSha256 ([Text.Encoding]::UTF8.GetBytes('{"b":1,"B":2}'))) -ceq $ordinal) 'the ported hash sorts member names ordinally (B before b), as the adapter does'
            $metadataOnly = Get-ManifestFunctionalSha256 ([Text.Encoding]::UTF8.GetBytes('{"main":"index.js","nested":{"__metadata":1}}'))
            Assert ($metadataOnly -ceq (Get-ManifestFunctionalSha256 ([Text.Encoding]::UTF8.GetBytes('{"__metadata":{"x":2},"nested":{"__metadata":1},"main":"index.js"}'))) -and
                $metadataOnly -cne (Get-ManifestFunctionalSha256 ([Text.Encoding]::UTF8.GetBytes('{"main":"index.js","nested":{"__metadata":2}}')))) 'only root installation metadata is excluded from the ported hash; nested metadata stays functional'
            $duplicate = $false
            try { $null = Get-ManifestFunctionalSha256 ([Text.Encoding]::UTF8.GetBytes('{"main":1,"main":2}')) } catch { $duplicate = $true }
            Assert $duplicate 'the ported hash refuses a repeated member name, as the adapter does'
            Assert (@($code.Checks | Where-Object { $_.Name -eq 'source.vscode.manifest-functional-hash' -and $_.Status -eq 'PASS' }).Count -eq 1 -and
                $code.DraftEntry.manifestFunctionalSha256 -ceq (Get-ManifestFunctionalSha256 ([IO.File]::ReadAllBytes((Join-Path $extension 'package.json')))) -and
                $code.PullRequestEligible) 'under PowerShell 7 the manifest functional hash is computed into the entry and the draft is eligible'
        }
        else {
            Assert (@($code.Checks | Where-Object { $_.Name -eq 'source.vscode.manifest-functional-hash' -and $_.Status -eq 'UNPROVEN' }).Count -eq 1 -and
                $null -eq $code.DraftEntry.manifestFunctionalSha256 -and -not $code.PullRequestEligible) 'under Windows PowerShell 5.1 the manifest hash stays UNPROVEN, never guessed, and blocks a pull request'
        }
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\app-main-0a1b.js'), '"thread-follower-start-turn"')
        $drifted = Measure-CodexExtension $extension $pins $extensionProviders
        Assert ($drifted.Verdict -eq 'NOT READY' -and @($drifted.Checks | Where-Object { $_.Status -eq 'FAIL' } | ForEach-Object { $_.Name }) -contains 'source.vscode.untrusted-input') 'an extension that drops a reviewed marker fails that check by name'

        # Merging a drafted entry: appended in the canonical layout; a repeat the loader refuses is refused here first.
        $merged = ConvertTo-PinDocumentText (Merge-PinEntry $pins.Document 'claudeCode' ([ordered]@{ version = '2.1.310'; sha256 = $hexC }))
        $reread = ($merged | ConvertFrom-Json)
        Assert (@($reread.claudeCode).Count -eq 2 -and $reread.claudeCode[1].version -ceq '2.1.310' -and $reread.claudeCode[1].sha256 -ceq $hexC -and
            $merged.Contains('    { "version": "2.1.310", "sha256": "' + $hexC + '" }' + "`n  ],") -and @($reread.codexDesktop).Count -eq 2) 'a merged entry is appended to its list in the canonical layout, the other lists unchanged'
        $repeat = $false
        try { $null = Merge-PinEntry $pins.Document 'codexVsCode' ([ordered]@{ version = '9.0.1'; directoryName = 'openai.chatgpt-9.0.1-win32-x64' }) } catch { $repeat = $_.Exception.Message -like '*already has a codexVsCode pin*' }
        Assert $repeat 'a merge that would repeat a pinned VS Code version is refused, leaving the replacement to a person'

        # Package mode on synthetic packages: an npm tarball (with its integrity) and a VSIX, never a real vendor file.
        $npmSource = Join-Path $fixture 'npm-source\package'
        New-Item -ItemType Directory -Path $npmSource -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $npmSource 'package.json'), '{"name":"@anthropic-ai/claude-code-win32-x64","version":"2.1.310","os":["win32"],"cpu":["x64"]}')
        [IO.File]::WriteAllText((Join-Path $npmSource 'claude.exe'), 'synthetic native image')
        $tarball = Join-Path $fixture 'claude-code-win32-x64-2.1.310.tgz'
        & (Join-Path $env:SystemRoot 'System32\tar.exe') -czf $tarball -C (Join-Path $fixture 'npm-source') 'package/package.json' 'package/claude.exe'
        $state.Version = '2.1.310.0'; $state.Hash = $hexC; $state.Signature = [pscustomobject]@{ Status = 'Valid'; Subject = 'CN="Anthropic, PBC", O="Anthropic, PBC", C=US' }
        $integrity = Get-FileSha512Sri $tarball
        $npmOut = Initialize-OutputDirectory (Join-Path $fixture 'out-npm')
        $npm = Measure-Package $tarball $integrity $npmOut $pins $providers
        Assert ($npm.Verdict -eq 'DRAFT FOR REVIEW' -and $npm.PullRequestEligible -and $npm.DraftPin -ceq ('{ "version": "2.1.310", "sha256": "' + $hexC + '" }') -and
            @($npm.Checks | Where-Object { $_.Name -in @('package.npm-integrity', 'package.identity', 'package.version-matches-image') -and $_.Status -eq 'PASS' }).Count -eq 3 -and
            (Test-Path -LiteralPath (Join-Path $npmOut 'npm\package\claude.exe'))) 'an npm tarball matching its published integrity is checked and drafted from its extracted image'
        $badOut = Initialize-OutputDirectory (Join-Path $fixture 'out-npm-bad')
        $tampered = Measure-Package $tarball ('sha512-' + [Convert]::ToBase64String((New-Object byte[] 64))) $badOut $pins $providers
        Assert ($tampered.Verdict -eq 'NOT READY' -and -not $tampered.PullRequestEligible -and
            @($tampered.Checks | Where-Object { $_.Name -eq 'package.npm-integrity' -and $_.Status -eq 'FAIL' }).Count -eq 1) 'a tarball that does not match the published integrity fails by name and withholds the draft'
        $noIntegrity = Measure-Package $tarball '' (Initialize-OutputDirectory (Join-Path $fixture 'out-npm-none')) $pins $providers
        Assert ($noIntegrity.Verdict -eq 'DRAFT FOR REVIEW' -and -not $noIntegrity.PullRequestEligible -and $noIntegrity.PullRequestBlockers -contains 'package.npm-integrity') 'a tarball without a published integrity is drafted but never an automated pull request'
        $state.Version = '2.1.309.0'
        $mismatch = Measure-Package $tarball $integrity (Initialize-OutputDirectory (Join-Path $fixture 'out-npm-version')) $pins $providers
        Assert ($mismatch.Verdict -eq 'NOT READY' -and @($mismatch.Checks | Where-Object { $_.Name -eq 'package.version-matches-image' -and $_.Status -eq 'FAIL' }).Count -eq 1) 'an image whose product version is not the package version fails by name'
        $state.Version = '2.1.310.0'

        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $vsix = Join-Path $fixture 'anthropic.claude-code-2.1.310@win32-x64.vsix'
        $zip = [IO.Compression.ZipFile]::Open($vsix, 'Create')
        try {
            foreach ($member in @(
                    @('extension.vsixmanifest', '<?xml version="1.0"?><PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"><Metadata><Identity Language="en-US" Id="claude-code" Version="2.1.310" Publisher="Anthropic" TargetPlatform="win32-x64"/></Metadata></PackageManifest>'),
                    @('extension/package.json', '{"name":"claude-code","publisher":"Anthropic","version":"2.1.310"}'),
                    @('extension/resources/native-binary/claude.exe', 'synthetic native image'),
                    @('extension/webview/index.js', 'not read'))) {
                $writer = New-Object IO.StreamWriter($zip.CreateEntry($member[0]).Open())
                try { $writer.Write($member[1]) } finally { $writer.Dispose() }
            }
        }
        finally { $zip.Dispose() }
        $vsixOut = Initialize-OutputDirectory (Join-Path $fixture 'out-vsix')
        $fromVsix = Measure-Package $vsix '' $vsixOut $pins $providers
        Assert ($fromVsix.Verdict -eq 'DRAFT FOR REVIEW' -and $fromVsix.PullRequestEligible -and $fromVsix.Name -ceq 'Claude Code VS Code' -and
            (Test-Path -LiteralPath (Join-Path $vsixOut 'anthropic.claude-code-2.1.310-win32-x64\resources\native-binary\claude.exe')) -and
            -not (Test-Path -LiteralPath (Join-Path $vsixOut 'anthropic.claude-code-2.1.310-win32-x64\webview'))) 'a Claude Code VSIX is identified by its manifest and only its native image is extracted and checked'
        Assert (-not (Test-PlainMemberPath 'extension/../x') -and -not (Test-PlainMemberPath '/abs') -and -not (Test-PlainMemberPath 'C:/x') -and
            -not (Test-PlainMemberPath 'a\b') -and (Test-PlainMemberPath 'extension/out/extension.js')) 'package members are refused unless they are plain relative paths; WITNESS: a plain member passes'
        $escape = $false
        try { $null = Resolve-InsideDirectory $vsixOut '../outside.txt' } catch { $escape = $_.Exception.Message -like '*would leave the output folder*' }
        Assert $escape 'a member that resolves outside the output folder is refused before any write'

        $userFile = Join-Path $fixture 'user-writable.exe'
        [IO.File]::WriteAllText($userFile, 'synthetic')
        $systemFile = Join-Path ([Environment]::GetFolderPath('System')) 'kernel32.dll'
        Assert (-not (Test-DefaultProtectedFile $userFile) -and (Test-DefaultProtectedFile $systemFile)) 'protected-location check refuses a user-writable file; WITNESS: a TrustedInstaller system file passes'
        # Same DACL shape as a package file (only SYSTEM can write) but owned by this user: the owner alone refuses.
        $ownedFile = Join-Path $fixture 'user-owned.exe'
        [IO.File]::WriteAllText($ownedFile, 'synthetic')
        $acl = New-Object Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-18')), 'FullControl', 'Allow')))
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')), 'ReadAndExecute', 'Allow')))
        Set-Acl -LiteralPath $ownedFile -AclObject $acl
        Assert (-not (Test-DefaultProtectedFile $ownedFile)) 'protected-location check refuses a user-owned file even when only SYSTEM can write'

        $split = Join-Path $fixture 'split.bin'
        $bytes = New-Object byte[] (4MB + 16)
        $tail = [Text.Encoding]::ASCII.GetBytes('procStartFt')
        [Array]::Copy($tail, 0, $bytes, 4MB - 4, $tail.Length)
        [IO.File]::WriteAllBytes($split, $bytes)
        $streamed = Find-FileMarkers $split ([ordered]@{ 'x' = 'procStartFt'; 'y' = 'selfSent' })
        Assert ($streamed['x'] -and -not $streamed['y']) 'streamed marker search finds a marker split across chunk boundaries'

        $after = @{}
        foreach ($entry in $files.GetEnumerator()) { $after[$entry.Key] = (Get-FileHash -LiteralPath (Join-Path $sources $entry.Key)).Hash }
        $unchanged = $true
        foreach ($key in $before.Keys) { if ($before[$key] -ne $after[$key]) { $unchanged = $false } }
        Assert ($unchanged -and @(Get-ChildItem -LiteralPath $sources -Recurse -File).Count -eq $files.Count -and
            (Get-Content -LiteralPath $repoPins -Raw -Encoding UTF8) -ceq $committed) 'the helper never writes or adds a pin document, in the fixture or the repository'
        Write-Output ("PASS: {0} re-pin helper fixture checks" -f $script:selfTestChecks)
    }
    finally { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($SelfTest) {
    try { Invoke-SelfTest; exit 0 }
    catch { Write-Output $_.Exception.Message; Write-Output 'RESULT=FAIL'; exit 1 }
}

if ([string]::IsNullOrEmpty($PinDocument)) { $PinDocument = Join-Path $script:RepoRoot $script:PinDocumentRelative }
$pins = Get-PinnedProfiles $PinDocument

if ($MergeEntry) {
    $out = Initialize-OutputDirectory $OutputDirectory
    $draft = [IO.File]::ReadAllText($MergeEntry, (New-Object Text.UTF8Encoding($false, $true))) | ConvertFrom-Json
    if ($null -eq $draft.list -or $null -eq $draft.entry -or -not $script:PinLists.Contains([string]$draft.list)) { throw 'The entry file has no known list and entry.' }
    $text = ConvertTo-PinDocumentText (Merge-PinEntry $pins.Document ([string]$draft.list) $draft.entry)
    Write-Utf8File (Join-Path $out 'agentbroker-pins.json') $text
    Write-Output ("Merged one {0} entry into {1}." -f $draft.list, (Join-Path $out 'agentbroker-pins.json'))
    exit 0
}

if ($Package) {
    $out = Initialize-OutputDirectory $OutputDirectory
    $results = @(Measure-Package $Package $ExpectedIntegrity $out $pins (Get-DefaultProviders))
}
else {
    $results = Get-InstalledResults $Vendor $pins (Get-DefaultProviders)
    $out = $null
    if ($OutputDirectory) { $out = Initialize-OutputDirectory $OutputDirectory }
}
$report = @(Write-Report $results)
if ($out) {
    Write-Utf8File (Join-Path $out 'evidence.txt') (($report -join "`n") + "`n")
    if ($Package) {
        Write-Utf8File (Join-Path $out 'result.json') (ConvertTo-ResultJson $results)
        if ($results[0].DraftEntry) {
            Write-Utf8File (Join-Path $out 'entry.json') ((([ordered]@{ list = $results[0].DraftList; entry = $results[0].DraftEntry }) | ConvertTo-Json -Depth 6) + "`n")
        }
    }
    else { Write-Utf8File (Join-Path $out 'results.json') (ConvertTo-ResultJson $results) }
}
if ($Json) { ConvertTo-ResultJson $results }
else { $report }
if (@($results | Where-Object { $_.Verdict -eq 'NOT READY' }).Count -gt 0) { exit 1 }
exit 0
