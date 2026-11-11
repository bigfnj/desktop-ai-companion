#requires -Version 5
<#
.SYNOPSIS
    The document names inside an assembly's EMBEDDED portable PDB, read out of the image bytes, so a
    build-path scan can see the vector DebugType=embedded actually ships.

.DESCRIPTION
    Test-ModulePublishFreshness.ps1 refuses a published DLL that names the machine, account and directory
    of whoever built it. Its scan looked for the CodeView record's path, which is stored as a plain
    NUL-terminated string in the image and which DebugType=embedded does keep bare (the file name alone).
    But embedded symbols carry the whole PDB, deflate-compressed inside an EmbeddedPortablePdb debug entry,
    and the portable PDB's Document table holds every source file's absolute path -- stored as PARTS
    ("D:", ".ai-work", "projects", ...) that the blob heap shares between documents and joins with a
    separator, so neither a regex over the image nor a regex over the inflated blob can see them. Every
    ModuleKit.dll in every committed zip carried fifteen such D:\ paths while the gate printed "no embedded
    build paths" (RA-197). This file parses the PE debug directory, inflates the embedded PDB and reads the
    Document table's names, which is the only way to see that vector.

    Windows PowerShell 5.1 has no System.Reflection.Metadata, so the PE headers, the metadata root, the
    stream headers, the #~ table stream and the #Blob heap are read by hand from the ECMA-335 and Portable
    PDB layouts. It reads; it never writes.

    THE THIRD VECTOR, AND COUNTS ONLY (2026-11-10). A normal build also writes a SourceLink map into the
    embedded PDB: a CustomDebugInformation row of kind CC110556-A091-4D38-9FEC-25AB9A351A6A whose JSON keys
    on the checkout's absolute path, and which neither the Document table nor any scan of the image can see.
    CustomDebugInformation.Kind is an index into the #GUID heap, so such a row cannot exist unless those
    sixteen bytes are in the heap: scanning the heap's slots is a sound test that sizes no table, and its only
    possible error is an unreferenced slot, which over-reports and so fails closed. A WITNESS ties the heap to
    the Document table: every Document row's Language index must land inside the heap and at least one must
    resolve to a known language GUID, or the heap was misread and a "no SourceLink" verdict would be vacuous.
    Get-EmbeddedPortablePdbPrivacySummary returns COUNTS (documents, absolute document names, PDBs carrying
    SourceLink) and judges the names in here, so no name or path ever reaches a caller or its output.

    Deliberately 5.1-compatible: the freshness check runs inside the gate under both shells.
#>

function Read-PeDebugDirectoryEntries {
    param([Parameter(Mandatory = $true)][byte[]]$Image)

    if ($Image.Length -lt 0x40 -or $Image[0] -ne 0x4D -or $Image[1] -ne 0x5A) {
        throw 'not a PE image: no MZ header'
    }
    $peOffset = [BitConverter]::ToInt32($Image, 0x3C)
    if ($peOffset -le 0 -or ($peOffset + 24) -gt $Image.Length -or $Image[$peOffset] -ne 0x50 -or $Image[$peOffset + 1] -ne 0x45) {
        throw 'not a PE image: no PE signature at e_lfanew'
    }
    $coff = $peOffset + 4
    $numberOfSections = [int][BitConverter]::ToUInt16($Image, $coff + 2)
    $sizeOfOptionalHeader = [int][BitConverter]::ToUInt16($Image, $coff + 16)
    $optional = $coff + 20
    $magic = [int][BitConverter]::ToUInt16($Image, $optional)
    # PE32+ (0x20B) and PE32 (0x10B) place the data directories at different offsets; the entry just
    # ahead of them is NumberOfRvaAndSizes, and the debug directory is entry 6.
    if ($magic -eq 0x20B) { $dataDirectoriesAt = $optional + 112 }
    elseif ($magic -eq 0x10B) { $dataDirectoriesAt = $optional + 96 }
    else { throw ('unknown optional-header magic 0x{0:X}' -f $magic) }
    $numberOfRvaAndSizes = [long][BitConverter]::ToUInt32($Image, $dataDirectoriesAt - 4)
    # The list is enumerated on output (no unary comma): callers wrap the call in @() and get one array
    # element per entry, or an empty array. A comma-wrapped list would reach them as ONE element whose
    # .Type is the array of every type, which `-eq 17` then passes as a whole.
    $entries = New-Object 'Collections.Generic.List[object]'
    if ($numberOfRvaAndSizes -le 6) { return $entries }
    $debugRva = [long][BitConverter]::ToUInt32($Image, $dataDirectoriesAt + 6 * 8)
    $debugSize = [long][BitConverter]::ToUInt32($Image, $dataDirectoriesAt + 6 * 8 + 4)
    if ($debugSize -eq 0) { return $entries }

    # RVA -> file offset through the section table (40-byte headers after the optional header).
    $sectionTable = $optional + $sizeOfOptionalHeader
    $debugOffset = -1L
    for ($s = 0; $s -lt $numberOfSections; $s++) {
        $at = $sectionTable + $s * 40
        $virtualSize = [long][BitConverter]::ToUInt32($Image, $at + 8)
        $virtualAddress = [long][BitConverter]::ToUInt32($Image, $at + 12)
        $sizeOfRawData = [long][BitConverter]::ToUInt32($Image, $at + 16)
        $pointerToRawData = [long][BitConverter]::ToUInt32($Image, $at + 20)
        $extent = [Math]::Max($virtualSize, $sizeOfRawData)
        if ($debugRva -ge $virtualAddress -and $debugRva -lt ($virtualAddress + $extent)) {
            $debugOffset = $debugRva - $virtualAddress + $pointerToRawData
            break
        }
    }
    if ($debugOffset -lt 0) { throw 'the debug directory RVA maps into no section' }
    $count = [int]($debugSize / 28)
    for ($i = 0; $i -lt $count; $i++) {
        $e = [int]($debugOffset + $i * 28)
        if (($e + 28) -gt $Image.Length) { throw 'the debug directory runs past the end of the image' }
        $entries.Add([pscustomobject]@{
            Type             = [long][BitConverter]::ToUInt32($Image, $e + 12)
            SizeOfData       = [long][BitConverter]::ToUInt32($Image, $e + 16)
            AddressOfRawData = [long][BitConverter]::ToUInt32($Image, $e + 20)
            PointerToRawData = [long][BitConverter]::ToUInt32($Image, $e + 24)
        })
    }
    return $entries
}

# Debug directory type 17: 'MPDB', the uncompressed size, then a raw deflate stream of the portable PDB.
function Expand-EmbeddedPortablePdb {
    param([Parameter(Mandatory = $true)][byte[]]$Image, [Parameter(Mandatory = $true)]$Entry)

    $at = [int]$Entry.PointerToRawData
    $size = [int]$Entry.SizeOfData
    if ($size -lt 8 -or ($at + $size) -gt $Image.Length) { throw 'the embedded PDB entry lies outside the image' }
    $magic = [long][BitConverter]::ToUInt32($Image, $at)
    if ($magic -ne 0x4244504D) { throw ('embedded PDB magic 0x{0:X} is not MPDB' -f $magic) }
    $uncompressed = [int][BitConverter]::ToUInt32($Image, $at + 4)
    if ($uncompressed -le 0 -or $uncompressed -gt 256MB) { throw "embedded PDB declares an implausible size of $uncompressed bytes" }
    $input = New-Object IO.MemoryStream($Image, ($at + 8), ($size - 8))
    $deflate = New-Object IO.Compression.DeflateStream($input, [IO.Compression.CompressionMode]::Decompress)
    $out = New-Object byte[] $uncompressed
    $read = 0
    try {
        while ($read -lt $uncompressed) {
            $n = $deflate.Read($out, $read, $uncompressed - $read)
            if ($n -le 0) { break }
            $read += $n
        }
    }
    finally {
        $deflate.Dispose()
        $input.Dispose()
    }
    if ($read -ne $uncompressed) { throw "the embedded PDB inflated to $read bytes where its header says $uncompressed" }
    return ,$out
}

# ECMA-335 compressed unsigned integer: 1, 2 or 4 bytes by the leading bits. Advances $Position.
function Read-CompressedUInt {
    param([byte[]]$Data, [ref]$Position)
    $p = $Position.Value
    $b0 = [int]$Data[$p]
    if (($b0 -band 0x80) -eq 0) { $Position.Value = $p + 1; return $b0 }
    if (($b0 -band 0xC0) -eq 0x80) {
        $Position.Value = $p + 2
        return ((($b0 -band 0x3F) -shl 8) -bor [int]$Data[$p + 1])
    }
    $Position.Value = $p + 4
    return ((($b0 -band 0x1F) -shl 24) -bor ([int]$Data[$p + 1] -shl 16) -bor ([int]$Data[$p + 2] -shl 8) -bor [int]$Data[$p + 3])
}

function Read-BlobBytes {
    param([byte[]]$Data, [int]$HeapOffset, [int]$Index)
    $pos = $HeapOffset + $Index
    $length = Read-CompressedUInt $Data ([ref]$pos)
    if (($pos + $length) -gt $Data.Length) { throw "a #Blob entry at index $Index runs past the heap" }
    $bytes = New-Object byte[] $length
    if ($length -gt 0) { [Array]::Copy($Data, $pos, $bytes, 0, $length) }
    return ,$bytes
}

# Portable PDB document name: one UTF-8 separator character (0x00 for none) followed by the parts, each a
# compressed #Blob index of a UTF-8 string, 0 standing for the empty string. "D:\a\b.cs" is stored as
# '\' + ["D:", "a", "b.cs"]; "/_/a/b.cs" as '/' + ["", "_", "a", "b.cs"].
function Read-DocumentName {
    param([byte[]]$Data, [int]$HeapOffset, [int]$Index)
    $blob = Read-BlobBytes $Data $HeapOffset $Index
    if ($blob.Length -eq 0) { return '' }
    $separatorLength = 1
    $separator = ''
    $lead = [int]$blob[0]
    if ($lead -ne 0) {
        if ($lead -ge 0xF0) { $separatorLength = 4 } elseif ($lead -ge 0xE0) { $separatorLength = 3 } elseif ($lead -ge 0xC0) { $separatorLength = 2 }
        $separator = [Text.Encoding]::UTF8.GetString($blob, 0, $separatorLength)
    }
    $pos = $separatorLength
    $parts = New-Object 'Collections.Generic.List[string]'
    while ($pos -lt $blob.Length) {
        $partIndex = Read-CompressedUInt $blob ([ref]$pos)
        if ($partIndex -eq 0) { $parts.Add('') }
        else { $parts.Add([Text.Encoding]::UTF8.GetString((Read-BlobBytes $Data $HeapOffset $partIndex))) }
    }
    return ($parts.ToArray() -join $separator)
}

# The metadata root (always at offset 0 of the inflated blob), the stream headers, and where the Document
# table's rows sit in the #~ stream. One copy of the layout, shared by the document-name reader and the
# privacy facts below, so the two can never disagree about where a row starts.
function Read-PortablePdbLayout {
    param([Parameter(Mandatory = $true)][byte[]]$Pdb)

    if ($Pdb.Length -lt 32 -or [BitConverter]::ToUInt32($Pdb, 0) -ne 0x424A5342) { throw 'the portable PDB has no BSJB metadata signature' }
    # Metadata root: signature, major, minor, reserved, version length (padded to 4), version, flags, streams.
    $versionLength = [int][BitConverter]::ToUInt32($Pdb, 12)
    $at = 16 + $versionLength
    $streamCount = [int][BitConverter]::ToUInt16($Pdb, $at + 2)
    $at += 4
    $streams = @{}
    for ($i = 0; $i -lt $streamCount; $i++) {
        $offset = [int][BitConverter]::ToUInt32($Pdb, $at)
        $size = [int][BitConverter]::ToUInt32($Pdb, $at + 4)
        $nameStart = $at + 8
        $nameEnd = $nameStart
        while ($nameEnd -lt $Pdb.Length -and $Pdb[$nameEnd] -ne 0) { $nameEnd++ }
        $name = [Text.Encoding]::ASCII.GetString($Pdb, $nameStart, $nameEnd - $nameStart)
        $streams[$name] = @{ Offset = $offset; Size = $size }
        # The name is NUL-terminated and padded to the next 4-byte boundary (at least one NUL).
        $nameField = $nameEnd - $nameStart + 1
        $nameField = [int]([Math]::Ceiling($nameField / 4.0) * 4)
        $at = $nameStart + $nameField
    }
    foreach ($required in @('#~', '#Blob')) {
        if (-not $streams.ContainsKey($required)) { throw "the portable PDB has no $required stream" }
    }
    $tables = [int]$streams['#~'].Offset
    $heapSizes = [int]$Pdb[$tables + 6]
    # Valid is a 64-bit table presence mask; read as two halves because PowerShell's shift operators do
    # not take a UInt64. Document is table 0x30, bit 16 of the high half.
    $validLow = [long][BitConverter]::ToUInt32($Pdb, $tables + 8)
    $validHigh = [long][BitConverter]::ToUInt32($Pdb, $tables + 12)
    $blobIndexSize = 2
    if (($heapSizes -band 0x04) -ne 0) { $blobIndexSize = 4 }
    $guidIndexSize = 2
    if (($heapSizes -band 0x02) -ne 0) { $guidIndexSize = 4 }
    $layout = [pscustomobject]@{
        Streams         = $streams
        DocumentPresent = (($validHigh -band 0x10000) -ne 0)
        DocumentRows    = 0
        RowDataAt       = 0
        RowSize         = 0
        BlobIndexSize   = $blobIndexSize
        GuidIndexSize   = $guidIndexSize
    }
    if (-not $layout.DocumentPresent) { return $layout }
    # A portable PDB's #~ stream holds the debug tables (0x30-0x37) only; type-system tables ahead of
    # Document would shift the rows this reader cannot size, so that shape is refused rather than misread.
    if ($validLow -ne 0 -or ($validHigh -band 0xFFFF) -ne 0) {
        throw 'the portable PDB #~ stream carries type-system tables ahead of Document; this reader expects the debug tables only'
    }
    $presentTables = 0
    foreach ($half in @($validLow, $validHigh)) {
        $bits = $half
        while ($bits -ne 0) { $presentTables += [int]($bits -band 1); $bits = $bits -shr 1 }
    }
    $rowsAt = $tables + 24
    $layout.DocumentRows = [int][BitConverter]::ToUInt32($Pdb, $rowsAt)
    $layout.RowDataAt = $rowsAt + 4 * $presentTables
    # Document row: Name (blob), HashAlgorithm (guid), Hash (blob), Language (guid). Document is the
    # first present table, so its rows start the table data.
    $layout.RowSize = 2 * $blobIndexSize + 2 * $guidIndexSize
    return $layout
}

# A #Blob or #GUID heap index of the width HeapSizes declared.
function Read-PortablePdbHeapIndex([byte[]]$Data, [int]$At, [int]$Size) {
    if ($Size -eq 4) { return [int][BitConverter]::ToUInt32($Data, $At) }
    return [int][BitConverter]::ToUInt16($Data, $At)
}

function Read-PortablePdbDocumentNames {
    param([Parameter(Mandatory = $true)][byte[]]$Pdb)

    $layout = Read-PortablePdbLayout -Pdb $Pdb
    if (-not $layout.DocumentPresent) { return @() }
    $blobHeap = [int]$layout.Streams['#Blob'].Offset
    $names = New-Object 'Collections.Generic.List[string]'
    for ($r = 0; $r -lt $layout.DocumentRows; $r++) {
        $row = $layout.RowDataAt + $r * $layout.RowSize
        $nameIndex = Read-PortablePdbHeapIndex $Pdb $row $layout.BlobIndexSize
        $names.Add((Read-DocumentName $Pdb $blobHeap $nameIndex))
    }
    # Enumerated on output; the caller wraps the call in @(). See Read-PeDebugDirectoryEntries.
    return $names.ToArray()
}

# A drive-rooted (C:\ or C:/), drive-relative (C:x) or UNC (two leading slashes of either kind) document
# name. Mapped names are rooted at /_/ (the SourceLink convention) and carry no drive. Widened on 2026-11-10
# from the freshness script's ^(?:[A-Za-z]:[\\/]|\\\\), which let a forward-slash UNC name and a bare drive
# prefix through. A Unix-rooted name (/home/...) is NOT matched: this repo's builds run on Windows, and
# refusing every name not under /_/ is the stricter optional rule recorded in the proposal.
function Test-AbsoluteDocumentName([string]$Name) {
    return [regex]::IsMatch($Name, '^(?:[A-Za-z]:|[\\/]{2})')
}

function Test-PortablePdbGuidAt([byte[]]$Data, [int]$At, [byte[]]$Guid) {
    for ($i = 0; $i -lt 16; $i++) {
        if ($Data[$At + $i] -ne $Guid[$i]) { return $false }
    }
    return $true
}

# Counts only. The names are judged here and never returned, so no caller can print one.
function Read-PortablePdbPrivacyFacts {
    param([Parameter(Mandatory = $true)][byte[]]$Pdb)

    $layout = Read-PortablePdbLayout -Pdb $Pdb
    if (-not $layout.DocumentPresent) {
        return [pscustomobject]@{ DocumentCount = 0; AbsoluteDocumentCount = 0; SourceLinkGuidCount = 0; EmbeddedSourceGuidCount = 0; KnownLanguageDocumentCount = 0 }
    }
    if (-not $layout.Streams.ContainsKey('#GUID')) {
        throw 'the portable PDB has no #GUID stream, so its Document languages and any SourceLink kind cannot be read'
    }
    $guidHeap = [int]$layout.Streams['#GUID'].Offset
    $guidSize = [int]$layout.Streams['#GUID'].Size
    if ($guidSize -lt 16 -or ($guidSize % 16) -ne 0 -or ($guidHeap + $guidSize) -gt $Pdb.Length) {
        throw "the portable PDB #GUID heap ($guidSize bytes) is not a whole number of 16-byte GUIDs inside the blob"
    }
    $slots = [int]($guidSize / 16)
    # [Guid]::ToByteArray is the on-disk order the #GUID heap uses.
    $sourceLinkKind = ([Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A').ToByteArray()
    # Same embedded-source kind as AgentBrokerReleasePayloadMetadata.CheckModuleKitSymbols.
    $embeddedSourceKind = ([Guid]'0E8A571B-6926-466E-B4AD-8AB04611F5FE').ToByteArray()
    # Document.Language values from the Portable PDB specification: C#, Visual Basic, F#.
    $knownLanguages = @(
        ([Guid]'3F5162F8-07C6-11D3-9053-00C04FA302A1').ToByteArray(),
        ([Guid]'3A12D0B8-C26C-11D0-B442-00A0244A1DD2').ToByteArray(),
        ([Guid]'AB4F38C9-B6E6-43BA-BE3B-58080B2CCCE3').ToByteArray())
    $sourceLinkSlots = 0
    $embeddedSourceSlots = 0
    for ($s = 0; $s -lt $slots; $s++) {
        if (Test-PortablePdbGuidAt $Pdb ($guidHeap + 16 * $s) $sourceLinkKind) { $sourceLinkSlots++ }
        if (Test-PortablePdbGuidAt $Pdb ($guidHeap + 16 * $s) $embeddedSourceKind) { $embeddedSourceSlots++ }
    }
    $blobHeap = [int]$layout.Streams['#Blob'].Offset
    # Language is the fourth column: Name (blob), HashAlgorithm (guid), Hash (blob), Language (guid).
    $languageAt = 2 * $layout.BlobIndexSize + $layout.GuidIndexSize
    $absolute = 0
    $knownLanguage = 0
    for ($r = 0; $r -lt $layout.DocumentRows; $r++) {
        $row = $layout.RowDataAt + $r * $layout.RowSize
        $name = Read-DocumentName $Pdb $blobHeap (Read-PortablePdbHeapIndex $Pdb $row $layout.BlobIndexSize)
        if (Test-AbsoluteDocumentName $name) { $absolute++ }
        $language = Read-PortablePdbHeapIndex $Pdb ($row + $languageAt) $layout.GuidIndexSize
        if ($language -lt 1 -or $language -gt $slots) {
            throw "a Document row's Language index $language lies outside the $slots-entry #GUID heap, so the Document table or the heap was misread"
        }
        foreach ($known in $knownLanguages) {
            if (Test-PortablePdbGuidAt $Pdb ($guidHeap + 16 * ($language - 1)) $known) { $knownLanguage++; break }
        }
    }
    if ($layout.DocumentRows -gt 0 -and $knownLanguage -eq 0) {
        throw ('WITNESS failed: no Document row of the embedded portable PDB resolves to a known language GUID, so the ' +
               '#GUID heap was misread and its SourceLink verdict would be vacuous.')
    }
    return [pscustomobject]@{
        DocumentCount              = $layout.DocumentRows
        AbsoluteDocumentCount      = $absolute
        SourceLinkGuidCount        = $sourceLinkSlots
        EmbeddedSourceGuidCount    = $embeddedSourceSlots
        KnownLanguageDocumentCount = $knownLanguage
    }
}

<#
.SYNOPSIS
    Every source document name recorded in the assembly's embedded portable PDB(s), and how many such
    PDBs the image carries.

.DESCRIPTION
    An image with no EmbeddedPortablePdb debug entry (DebugType none, or a separate .pdb) yields
    EmbeddedPdbCount 0 and no names, which is a fact about the image and not a failure. An embedded PDB
    that yields ZERO documents is refused: a compiled assembly always has at least one source file, so
    zero means this reader lost the Document table, and scoring that as "no build paths" would be the
    check-cannot-fail shape the scan exists to close.
#>
function Get-EmbeddedPortablePdbDocumentNames {
    param([Parameter(Mandatory = $true)][byte[]]$Image)

    $entries = @(Read-PeDebugDirectoryEntries -Image $Image)
    $embedded = @($entries | Where-Object { $_.Type -eq 17 })
    $names = New-Object 'Collections.Generic.List[string]'
    foreach ($entry in $embedded) {
        $pdb = Expand-EmbeddedPortablePdb -Image $Image -Entry $entry
        $documents = @(Read-PortablePdbDocumentNames -Pdb $pdb)
        if ($documents.Count -eq 0) {
            throw ('an embedded portable PDB parsed to ZERO documents. A compiled assembly has source files, so ' +
                   'the Document table was lost by this reader; refusing to score the assembly as free of build paths.')
        }
        foreach ($document in $documents) { $names.Add($document) }
    }
    return [pscustomobject]@{
        EmbeddedPdbCount = $embedded.Count
        DocumentNames    = @($names.ToArray())
    }
}

<#
.SYNOPSIS
    The privacy verdict inputs for one assembly image, as COUNTS: embedded PDBs, their documents, how many
    document names are absolute, and how many embedded PDBs carry SourceLink or EmbeddedSource kind GUIDs.

.DESCRIPTION
    The same refusals as Get-EmbeddedPortablePdbDocumentNames (an embedded PDB with ZERO documents means the
    reader lost the Document table), plus the #GUID-heap witness in Read-PortablePdbPrivacyFacts. No name,
    path or SourceLink content is returned or decoded: a SourceLink record is detected by its kind GUID
    alone, and its JSON blob is never read. EmbeddedSource uses the same heap-presence test. Each PDB counts
    at most once per kind, including an unreferenced kind slot, which over-reports and fails closed. Neither
    count claims to enumerate CustomDebugInformation records or decode embedded source content.
#>
function Get-EmbeddedPortablePdbPrivacySummary {
    param([Parameter(Mandatory = $true)][byte[]]$Image)

    $entries = @(Read-PeDebugDirectoryEntries -Image $Image)
    $embedded = @($entries | Where-Object { $_.Type -eq 17 })
    $documentCount = 0
    $absoluteCount = 0
    $sourceLinkPdbs = 0
    $embeddedSourcePdbs = 0
    foreach ($entry in $embedded) {
        $pdb = Expand-EmbeddedPortablePdb -Image $Image -Entry $entry
        $facts = Read-PortablePdbPrivacyFacts -Pdb $pdb
        if ($facts.DocumentCount -eq 0) {
            throw ('an embedded portable PDB parsed to ZERO documents. A compiled assembly has source files, so ' +
                   'the Document table was lost by this reader; refusing to score the assembly as free of build paths.')
        }
        $documentCount += $facts.DocumentCount
        $absoluteCount += $facts.AbsoluteDocumentCount
        if ($facts.SourceLinkGuidCount -gt 0) { $sourceLinkPdbs++ }
        if ($facts.EmbeddedSourceGuidCount -gt 0) { $embeddedSourcePdbs++ }
    }
    return [pscustomobject]@{
        EmbeddedPdbCount      = $embedded.Count
        DocumentCount         = $documentCount
        AbsoluteDocumentCount = $absoluteCount
        SourceLinkPdbCount    = $sourceLinkPdbs
        EmbeddedSourcePdbCount = $embeddedSourcePdbs
    }
}
