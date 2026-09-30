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

function Read-PortablePdbDocumentNames {
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
    $documentPresent = (($validHigh -band 0x10000) -ne 0)
    if (-not $documentPresent) { return @() }
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
    $documentRows = [int][BitConverter]::ToUInt32($Pdb, $rowsAt)
    $rowDataAt = $rowsAt + 4 * $presentTables
    $blobIndexSize = 2
    if (($heapSizes -band 0x04) -ne 0) { $blobIndexSize = 4 }
    $guidIndexSize = 2
    if (($heapSizes -band 0x02) -ne 0) { $guidIndexSize = 4 }
    # Document row: Name (blob), HashAlgorithm (guid), Hash (blob), Language (guid). Document is the
    # first present table, so its rows start the table data.
    $rowSize = 2 * $blobIndexSize + 2 * $guidIndexSize
    $blobHeap = [int]$streams['#Blob'].Offset
    $names = New-Object 'Collections.Generic.List[string]'
    for ($r = 0; $r -lt $documentRows; $r++) {
        $row = $rowDataAt + $r * $rowSize
        if ($blobIndexSize -eq 4) { $nameIndex = [int][BitConverter]::ToUInt32($Pdb, $row) }
        else { $nameIndex = [int][BitConverter]::ToUInt16($Pdb, $row) }
        $names.Add((Read-DocumentName $Pdb $blobHeap $nameIndex))
    }
    # Enumerated on output; the caller wraps the call in @(). See Read-PeDebugDirectoryEntries.
    return $names.ToArray()
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
