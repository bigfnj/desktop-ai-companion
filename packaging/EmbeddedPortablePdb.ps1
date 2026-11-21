#requires -Version 5
<#
.SYNOPSIS
    The document names and custom debug information records inside an assembly's EMBEDDED portable PDB,
    read out of the image bytes, so a build-path scan can see the vector DebugType=embedded actually ships;
    and the scan over them, the inflated PDB and the image for a build machine's identity.

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
    Document table's names, which is the only way to see that vector. It reads the CustomDebugInformation
    table too, because the Source Link record there maps the absolute checkout folder and PathMap does not
    reach it (N-bd-w13-contracts-1-02); Get-BuildIdentityFindings, further down, judges all of it.

    Windows PowerShell 5.1 has no System.Reflection.Metadata, so the PE headers, the metadata root, the
    stream headers, the #~ table stream and the #Blob heap are read by hand from the ECMA-335 and Portable
    PDB layouts. It reads; it never writes, except New-PortablePdbTestImage, which builds a synthetic image
    in memory for the scan's own proof.

    THE THIRD VECTOR, AND COUNTS ONLY (2026-11-10). A normal build also writes a SourceLink map into the
    embedded PDB: a CustomDebugInformation row of kind CC110556-A091-4D38-9FEC-25AB9A351A6A whose JSON keys
    on the checkout's absolute path, and which neither the Document table nor a regex over the image can see.
    TWO DETECTORS ON PURPOSE: Get-BuildIdentityFindings reads the CustomDebugInformation ROWS (above) for the
    build-identity scan, and the privacy summary below tests #GUID heap PRESENCE for its counts-only verdict.
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

# Where every debug table (0x30-0x37) of a portable PDB starts, how many rows it has and how wide a row is.
# Document is the first table, so its reader once skipped this; CustomDebugInformation (0x37) is the LAST, behind six
# tables whose rows are sized by the heap index widths, by the row counts of the tables they point into and, for
# LocalScope and StateMachineMethod (which name methods), by the assembly's MethodDef count, which a portable PDB
# carries in its #Pdb stream (TypeSystemTableRows) because the type system tables live in the assembly. Columns from
# the Portable PDB specification; index widths from ECMA-335 II.24.2.6: a table index is 2 bytes below 65,536 rows, a
# coded index 2 bytes while every table it can name stays below 2^(16 - tag bits) rows.
# One copy of the layout, shared by the document-name reader, the CustomDebugInformation reader and the privacy facts
# below, so they can never disagree about where a row starts. The #Pdb stream is OPTIONAL here: without it every
# type-system count stays 0, which can only under-size LocalScope and StateMachineMethod rows, so the fit check stays a
# valid necessary condition and Document's offset (which needs no type-system count) is exact. TypeSystemRowsKnown says
# which, and Read-PortablePdbCustomDebugInformation refuses a PDB whose custom debug information it cannot size.
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
    # not take a UInt64. Tables 0x30-0x37 are bits 16-23 of the high half. Any other table present is one
    # this reader cannot size, and a guessed width would misread every row after it, so that shape is
    # refused rather than misread.
    $validLow = [long][BitConverter]::ToUInt32($Pdb, $tables + 8)
    $validHigh = [long][BitConverter]::ToUInt32($Pdb, $tables + 12)
    if ($validLow -ne 0 -or ($validHigh -band 0xFF00FFFFL) -ne 0) {
        throw 'the portable PDB #~ stream carries tables other than the debug tables 0x30-0x37; this reader expects the debug tables only'
    }
    $rows = New-Object 'long[]' 8
    $at = $tables + 24
    for ($t = 0; $t -lt 8; $t++) {
        if (($validHigh -band (1L -shl (16 + $t))) -ne 0) {
            $rows[$t] = [long][BitConverter]::ToUInt32($Pdb, $at)
            $at += 4
        }
    }
    # HeapSizes 0x40 marks four bytes of extra data after the row counts (System.Reflection.Metadata skips them too).
    if (($heapSizes -band 0x40) -ne 0) { $at += 4 }
    $tableDataAt = $at

    # #Pdb: PdbId (20 bytes), EntryPoint (4), ReferencedTypeSystemTables (a 64-bit mask), then one row count per
    # referenced table, in table order: the assembly's own counts, for the columns that point into its tables.
    # Optional (see above): without it every count stays 0 and TypeSystemRowsKnown is false.
    $typeSystemRows = New-Object 'long[]' 64
    $typeSystemRowsKnown = $streams.ContainsKey('#Pdb')
    if ($typeSystemRowsKnown) {
        $pdbStream = [int]$streams['#Pdb'].Offset
        $referencedLow = [long][BitConverter]::ToUInt32($Pdb, $pdbStream + 24)
        $referencedHigh = [long][BitConverter]::ToUInt32($Pdb, $pdbStream + 28)
        $at = $pdbStream + 32
        for ($t = 0; $t -lt 64; $t++) {
            if ($t -lt 32) { $present = (($referencedLow -band (1L -shl $t)) -ne 0) }
            else { $present = (($referencedHigh -band (1L -shl ($t - 32))) -ne 0) }
            if ($present) {
                $typeSystemRows[$t] = [long][BitConverter]::ToUInt32($Pdb, $at)
                $at += 4
            }
        }
    }

    $stringIndex = 2
    if (($heapSizes -band 0x01) -ne 0) { $stringIndex = 4 }
    $guidIndex = 2
    if (($heapSizes -band 0x02) -ne 0) { $guidIndex = 4 }
    $blobIndex = 2
    if (($heapSizes -band 0x04) -ne 0) { $blobIndex = 4 }
    $width = New-Object 'int[]' 8
    for ($t = 0; $t -lt 8; $t++) { $width[$t] = 2; if ($rows[$t] -ge 0x10000) { $width[$t] = 4 } }
    $methodDef = 2
    if ($typeSystemRows[0x06] -ge 0x10000) { $methodDef = 4 }
    # HasCustomDebugInformation names 27 tables, so 5 tag bits and 2 bytes while each stays below 2^11 rows: the 22
    # type system tables, then Document, LocalScope, LocalVariable, LocalConstant and ImportScope.
    $largest = 0L
    foreach ($table in @(0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20,
                         0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B)) {
        $largest = [Math]::Max($largest, $typeSystemRows[$table])
    }
    foreach ($debugTable in @(0, 2, 3, 4, 5)) { $largest = [Math]::Max($largest, $rows[$debugTable]) }
    $hasCustomDebugInformation = 2
    if ($largest -ge 0x800) { $hasCustomDebugInformation = 4 }

    $rowSizes = @(
        (2 * $blobIndex + 2 * $guidIndex),                       # 0x30 Document: Name, HashAlgorithm, Hash, Language
        ($width[0] + $blobIndex),                                # 0x31 MethodDebugInformation: Document, SequencePoints
        ($methodDef + $width[5] + $width[3] + $width[4] + 8),    # 0x32 LocalScope: Method, ImportScope, VariableList, ConstantList, StartOffset, Length
        (4 + $stringIndex),                                      # 0x33 LocalVariable: Attributes, Index, Name
        ($stringIndex + $blobIndex),                             # 0x34 LocalConstant: Name, Signature
        ($width[5] + $blobIndex),                                # 0x35 ImportScope: Parent, Imports
        (2 * $methodDef),                                        # 0x36 StateMachineMethod: MoveNextMethod, KickoffMethod
        ($hasCustomDebugInformation + $guidIndex + $blobIndex)   # 0x37 CustomDebugInformation: Parent, Kind, Value
    )
    $layoutTables = New-Object 'object[]' 8
    $at = [long]$tableDataAt
    for ($t = 0; $t -lt 8; $t++) {
        $layoutTables[$t] = [pscustomobject]@{ Rows = [int]$rows[$t]; RowSize = [int]$rowSizes[$t]; Offset = [int]$at }
        $at += $rows[$t] * $rowSizes[$t]
    }
    # The tables must fit the stream that holds them: a row sized wrong overruns it.
    if ($at -gt ($tables + [long]$streams['#~'].Size)) {
        throw 'the portable PDB debug tables run past the end of the #~ stream, so a row width was misread'
    }
    $guidHeap = -1
    if ($streams.ContainsKey('#GUID')) { $guidHeap = [int]$streams['#GUID'].Offset }
    return [pscustomobject]@{
        Tables                    = $layoutTables
        BlobHeap                  = [int]$streams['#Blob'].Offset
        GuidHeap                  = $guidHeap
        BlobIndex                 = $blobIndex
        GuidIndex                 = $guidIndex
        HasCustomDebugInformation = $hasCustomDebugInformation
        TypeSystemRowsKnown       = $typeSystemRowsKnown
        # The Document-only names Read-PortablePdbPrivacyFacts reads, from the same sizing. Document is the first
        # debug table, so its rows start the table data.
        Streams                   = $streams
        DocumentPresent           = (($validHigh -band 0x10000) -ne 0)
        DocumentRows              = $layoutTables[0].Rows
        RowDataAt                 = $layoutTables[0].Offset
        RowSize                   = $layoutTables[0].RowSize
        BlobIndexSize             = $blobIndex
        GuidIndexSize             = $guidIndex
    }
}

# A #Blob or #GUID heap index, or a table or coded index, of the width (2 or 4 bytes) the layout sized.
function Read-PortablePdbHeapIndex([byte[]]$Data, [int]$At, [int]$Size) {
    if ($Size -eq 4) { return [int][BitConverter]::ToUInt32($Data, $At) }
    return [int][BitConverter]::ToUInt16($Data, $At)
}

function Read-PortablePdbDocumentNames {
    param([Parameter(Mandatory = $true)][byte[]]$Pdb)

    $layout = Read-PortablePdbLayout -Pdb $Pdb
    # Document row: Name (blob), HashAlgorithm (guid), Hash (blob), Language (guid).
    $table = $layout.Tables[0]
    $names = New-Object 'Collections.Generic.List[string]'
    for ($r = 0; $r -lt $table.Rows; $r++) {
        $nameIndex = Read-PortablePdbHeapIndex $Pdb ($table.Offset + $r * $table.RowSize) $layout.BlobIndex
        $names.Add((Read-DocumentName $Pdb $layout.BlobHeap $nameIndex))
    }
    # Enumerated on output; the caller wraps the call in @(). See Read-PeDebugDirectoryEntries.
    return $names.ToArray()
}

# Every CustomDebugInformation row: its parent (a HasCustomDebugInformation coded index, tag in the low five bits;
# tag 7 is Module, so a module-level record is tag 7, row 1), its kind GUID and its value blob. The two module-level
# records that hold text are Source Link (a JSON map from the checkout folder to a URL) and the compilation options;
# tests\DesktopAICompanion.CoreTests\ModuleKitTests.cs reads the same two through System.Reflection.Metadata.
$script:SourceLinkRecordKind = [Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A'
$script:CompilationOptionsRecordKind = [Guid]'B5FEEC05-8CD0-4A83-96DA-466284BB4BD8'
function Read-PortablePdbCustomDebugInformation {
    param([Parameter(Mandatory = $true)][byte[]]$Pdb)

    $layout = Read-PortablePdbLayout -Pdb $Pdb
    $table = $layout.Tables[7]
    # Fails closed: without #Pdb the MethodDef count and the type-system row counts are unknown, so the rows ahead of
    # this table and its parent index cannot be sized (the layout reads them as 0). Real PDBs always carry #Pdb.
    if ($table.Rows -gt 0 -and -not $layout.TypeSystemRowsKnown) {
        throw 'the portable PDB has custom debug information but no #Pdb stream, so its rows and parent index cannot be sized'
    }
    if ($table.Rows -gt 0 -and $layout.GuidHeap -lt 0) { throw 'the portable PDB has custom debug information but no #GUID stream' }
    $records = New-Object 'Collections.Generic.List[object]'
    for ($r = 0; $r -lt $table.Rows; $r++) {
        $row = $table.Offset + $r * $table.RowSize
        $parent = Read-PortablePdbHeapIndex $Pdb $row $layout.HasCustomDebugInformation
        $kindIndex = Read-PortablePdbHeapIndex $Pdb ($row + $layout.HasCustomDebugInformation) $layout.GuidIndex
        $valueIndex = Read-PortablePdbHeapIndex $Pdb ($row + $layout.HasCustomDebugInformation + $layout.GuidIndex) $layout.BlobIndex
        $kind = [Guid]::Empty
        if ($kindIndex -gt 0) {
            $guidBytes = New-Object byte[] 16
            [Array]::Copy($Pdb, $layout.GuidHeap + ($kindIndex - 1) * 16, $guidBytes, 0, 16)
            $kind = New-Object Guid (, $guidBytes)
        }
        $records.Add([pscustomobject]@{
            ParentTag = ($parent -band 0x1F)
            ParentRow = ($parent -shr 5)
            Kind      = $kind
            Value     = (Read-BlobBytes $Pdb $layout.BlobHeap $valueIndex)
        })
    }
    # Enumerated on output; the caller wraps the call in @(). See Read-PeDebugDirectoryEntries.
    return $records.ToArray()
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
    The assembly's embedded portable PDB(s), inflated, with every source document name and every custom
    debug information record they hold, and how many such PDBs the image carries.

.DESCRIPTION
    An image with no EmbeddedPortablePdb debug entry (DebugType none, or a separate .pdb) yields
    EmbeddedPdbCount 0 and nothing else, which is a fact about the image and not a failure. An embedded PDB
    that yields ZERO documents is refused: a compiled assembly always has at least one source file, so
    zero means this reader lost the Document table, and scoring that as "no build paths" would be the
    check-cannot-fail shape the scan exists to close.
#>
function Get-EmbeddedPortablePdbContent {
    param([Parameter(Mandatory = $true)][byte[]]$Image)

    $entries = @(Read-PeDebugDirectoryEntries -Image $Image)
    $embedded = @($entries | Where-Object { $_.Type -eq 17 })
    $names = New-Object 'Collections.Generic.List[string]'
    $records = New-Object 'Collections.Generic.List[object]'
    $pdbs = New-Object 'Collections.Generic.List[object]'
    foreach ($entry in $embedded) {
        $pdb = Expand-EmbeddedPortablePdb -Image $Image -Entry $entry
        $pdbs.Add($pdb)
        $documents = @(Read-PortablePdbDocumentNames -Pdb $pdb)
        if ($documents.Count -eq 0) {
            throw ('an embedded portable PDB parsed to ZERO documents. A compiled assembly has source files, so ' +
                   'the Document table was lost by this reader; refusing to score the assembly as free of build paths.')
        }
        foreach ($document in $documents) { $names.Add($document) }
        foreach ($record in @(Read-PortablePdbCustomDebugInformation -Pdb $pdb)) { $records.Add($record) }
    }
    return [pscustomobject]@{
        EmbeddedPdbCount = $embedded.Count
        DocumentNames    = @($names.ToArray())
        Records          = @($records.ToArray())
        Pdbs             = @($pdbs.ToArray())
    }
}

# ---------------------------------------------------------------------------------------------------
# A BUILD MACHINE'S IDENTITY IN A SHIPPED ASSEMBLY (N-bd-w13-contracts-1-02). The document names above were the only
# record this file read, so a Source Link record (a JSON map from the absolute checkout folder to a URL, outside
# PathMap's reach) passed in every ModuleKit.dll of every committed zip, and one of them maps a folder under a user
# profile: measured at 60480d4b with this reader and, independently, with System.Reflection.Metadata under PowerShell 7.
# Get-BuildIdentityFindings judges three places: the custom debug information records (any Source Link record is a
# finding), the inflated PDB and the document names, and the DLL's own bytes, each read as ASCII and as UTF-16 at both
# alignments, for a rooted user-profile path or a UNC root. It returns the KIND of each hit and never the text, because
# the gate prints what this returns and CI publishes the gate's output.
#
# FOR THE ASSEMBLIES THIS REPOSITORY BUILDS, which is all Test-ModulePublishFreshness.ps1 hands it. A package's Source
# Link record maps that package's own public repository and is not ours to remove, so a caller that ever widens the
# scan to package DLLs has to stop judging Source Link for them; the profile and UNC rules hold for any image.

# A drive, one or more separators of either kind (so the doubled "\\" of an escaped string or a JSON value reads as
# one), Users, separators, then the account segment, which ends wherever an account character cannot follow: a
# separator, a quote, any control byte, a character no file name may hold, or the end. The end was once a list (a
# separator, a quote, NUL), so a profile root whose account segment met any other control byte, as the end of a field in
# a binary record can, gave no finding (the review of N-bd-w13-contracts-1-02, measured under 5.1).
$script:UserProfilePathPattern = [regex] '(?i)(?<![A-Za-z0-9])[A-Za-z]:[\\/]+Users[\\/]+(?<account>[^\\/\x00-\x1f"*:<>?|]{1,128})(?![^\\/\x00-\x1f"*:<>?|])'
# Two or more backslashes that start a token, optionally the long form's "?\UNC\" (or "."), a host, separators, then a
# share; or the forward-slash form, exactly "//host/share". Windows takes "\\?\UNC\host\share" and "//host/share" for the
# same root as "\\host\share", and both once passed (same review). Not after a drive's colon: an escaped drive path
# ("C:\\\\work\\\\demo") holds the same doubled backslashes, and the self-tests carry several. The colon also keeps a
# URL ("https://host/x") clean. The forward form is held to single separators and refused after "+" because the DLL
# bytes hold text that is not a path: measured on the committed zips on 2026-11-12, a looser "[\\/]{2,}...[\\/]+" form
# flagged base64 image data in PetStudio ("+//91qP//..."), an iCalendar PRODID in Reminder ("-//Name//Product//EN")
# and binary in AgentBroker.Worker ("///Q/e"); base64 puts only a letter, a digit, "+" or "/" before a "//".
$script:UncRootPattern = [regex] '(?<![\\/:A-Za-z0-9])(?:\\{2,}(?:[?.]\\+UNC\\+)?(?<host>[A-Za-z0-9][A-Za-z0-9._$-]{0,62})\\+(?<share>[A-Za-z0-9._$ -]{0,79}[A-Za-z0-9_$])|(?<!\+)//(?<host>[A-Za-z0-9][A-Za-z0-9._$-]{0,62})/(?<share>[A-Za-z0-9._$ -]{0,79}[A-Za-z0-9_$]))'
# STAND-INS. Module self-tests ship inside the module DLLs (each module's public static SelfTest), and their fixtures
# hold profile and UNC paths as string literals, so the DLL bytes carry them: measured over the committed zips on
# 2026-11-12, the accounts below in Remembrance, AiBrain, PetStudio, Fortunes and AgentFlow, the two shares below in
# Remembrance and a .invalid host in AiBrain. None is a machine or a person. A host under .invalid (RFC 6761) can never
# resolve. A new fixture that names any other account, host or share fails this check: rename it to one of these rather
# than widening the list, which is the one place a real name could be waved through. The stand-in is the host AND the
# share, not the host alone: a host-only entry waved through every share on a machine that happened to share its name
# (same review).
$script:BuildIdentityStandInAccounts = @('someone', 'x', "O'Brien")
$script:BuildIdentityStandInShares = @('server\home', 'server\share')

function Find-BuildIdentityInText {
    param([AllowEmptyString()][string]$Text)
    $kinds = New-Object 'Collections.Generic.List[string]'
    foreach ($match in $script:UserProfilePathPattern.Matches($Text)) {
        $account = $match.Groups['account'].Value
        if (@($script:BuildIdentityStandInAccounts | Where-Object { $_ -ieq $account }).Count -gt 0) { continue }
        $kinds.Add('user-profile path')
        break
    }
    foreach ($match in $script:UncRootPattern.Matches($Text)) {
        $hostName = $match.Groups['host'].Value
        if ($hostName.EndsWith('.invalid', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $root = $hostName + '\' + $match.Groups['share'].Value
        if (@($script:BuildIdentityStandInShares | Where-Object { $_ -ieq $root }).Count -gt 0) { continue }
        $kinds.Add('UNC root')
        break
    }
    return $kinds.ToArray()
}

# Latin-1 maps every byte to one char, so a path stored as ASCII or UTF-8 survives whole; UTF-16 is read at both byte
# alignments because a string in the #US heap starts wherever the previous one ended.
function Get-ByteTextViews {
    param([byte[]]$Bytes)
    $views = New-Object 'Collections.Generic.List[object]'
    $views.Add([pscustomobject]@{ Encoding = 'ASCII'; Text = [Text.Encoding]::GetEncoding(28591).GetString($Bytes) })
    $views.Add([pscustomobject]@{ Encoding = 'UTF-16'; Text = [Text.Encoding]::Unicode.GetString($Bytes) })
    if ($Bytes.Length -gt 1) {
        $views.Add([pscustomobject]@{ Encoding = 'UTF-16'; Text = [Text.Encoding]::Unicode.GetString($Bytes, 1, $Bytes.Length - 1) })
    }
    return $views.ToArray()
}

<#
.SYNOPSIS
    What in an assembly this repository builds names the machine or the person that built it, as a list of KINDS,
    never the text; with the embedded PDB count and document names, so a caller parses each image once.
#>
function Get-BuildIdentityFindings {
    param([Parameter(Mandatory = $true)][byte[]]$Image)

    $content = Get-EmbeddedPortablePdbContent -Image $Image
    $findings = New-Object 'Collections.Generic.List[string]'
    # ANY Source Link record, not one whose map names a profile. A workstation build maps the absolute folder it ran in
    # (outside PathMap's reach); only CI=true or DeterministicSourcePaths turns the key into a single /_/*, measured by
    # the contracts lane on 2026-11-12, and the zips are built by packaging\New-ModulePublish.ps1, which no workflow
    # runs, so CI is not set (src\Directory.Build.props sets ContinuousIntegrationBuild only under CI). Even the /_/*
    # record names the repository and the commit. So its presence is the finding, and EnableSourceLink false is the cure (ModuleKit and
    # Contracts).
    foreach ($record in $content.Records) {
        if ($record.Kind -eq $script:SourceLinkRecordKind) { $findings.Add('a Source Link record in the embedded PDB') }
    }
    foreach ($document in $content.DocumentNames) {
        foreach ($kind in @(Find-BuildIdentityInText $document)) { $findings.Add("a $kind in an embedded PDB document name") }
    }
    foreach ($pdb in $content.Pdbs) {
        foreach ($view in @(Get-ByteTextViews $pdb)) {
            foreach ($kind in @(Find-BuildIdentityInText $view.Text)) { $findings.Add("a $kind in the embedded PDB ($($view.Encoding))") }
        }
    }
    foreach ($view in @(Get-ByteTextViews $Image)) {
        foreach ($kind in @(Find-BuildIdentityInText $view.Text)) { $findings.Add("a $kind in the DLL bytes ($($view.Encoding))") }
    }
    $languages = @(Get-CompilationOptionsLanguages -Records $content.Records)
    return [pscustomobject]@{
        EmbeddedPdbCount            = $content.EmbeddedPdbCount
        DocumentNames               = $content.DocumentNames
        CompilationOptionsRecords   = $languages.Count
        CompilationOptionsLanguages = $languages
        Findings                    = @($findings | Select-Object -Unique)
    }
}

# The 'language' value of every module-level compilation options record, one entry per record ('' when the key is
# missing). The value is NUL-separated UTF-8 key and value pairs. The SDK writes one such record into every portable
# PDB, so for the C# assemblies this repository builds, one 'C#' per embedded PDB is what a correct read of the
# CustomDebugInformation table returns: the WITNESS that the rows were not shifted by a misread width in one of the six
# tables before it, which the synthetic image (Document and CustomDebugInformation only) cannot exercise. Measured over
# the eight committed zips on 2026-11-12: one record per embedded PDB, keys version, compiler-version, language,
# source-file-count, output-kind, optimization, platform, runtime-version, language-version and define.
function Get-CompilationOptionsLanguages {
    param([object[]]$Records)
    $languages = New-Object 'Collections.Generic.List[string]'
    foreach ($record in @($Records)) {
        if ($null -eq $record -or $record.ParentTag -ne 7 -or $record.Kind -ne $script:CompilationOptionsRecordKind) { continue }
        $pairs = [Text.Encoding]::UTF8.GetString([byte[]]$record.Value).Split([char]0)
        $language = ''
        for ($i = 0; ($i + 1) -lt $pairs.Length; $i += 2) {
            if ($pairs[$i] -ceq 'language') { $language = $pairs[$i + 1]; break }
        }
        $languages.Add($language)
    }
    # Enumerated on output; the caller wraps the call in @(). See Read-PeDebugDirectoryEntries.
    return $languages.ToArray()
}

# ---------------------------------------------------------------------------------------------------
# A SYNTHETIC IMAGE, so the scan is proved on every run before it is trusted. The committed zips are the only real
# input and they change at every publish: a scan whose rules were broken would read a clean zip as clean. These build
# the smallest PE32+ the reader accepts (one section holding a debug directory with one EmbeddedPortablePdb entry)
# around a portable PDB with a Document and a CustomDebugInformation table, then append raw text after the section.
function ConvertTo-CompressedUIntBytes {
    param([int]$Value)
    if ($Value -lt 0x80) { return , [byte[]]@($Value) }
    if ($Value -lt 0x4000) { return , [byte[]]@((0x80 -bor ($Value -shr 8)), ($Value -band 0xFF)) }
    return , [byte[]]@((0xC0 -bor ($Value -shr 24)), (($Value -shr 16) -band 0xFF), (($Value -shr 8) -band 0xFF), ($Value -band 0xFF))
}

function New-PortablePdbTestImage {
    param([string[]]$Documents, [object[]]$Records, [string]$AsciiTail = '', [string]$Utf16Tail = '', [switch]$OddUtf16)

    $blob = New-Object IO.MemoryStream
    $blob.WriteByte(0)
    $addBlob = {
        param([byte[]]$Bytes)
        $index = [int]$blob.Position
        $length = ConvertTo-CompressedUIntBytes $Bytes.Length
        $blob.Write($length, 0, $length.Length)
        $blob.Write($Bytes, 0, $Bytes.Length)
        return $index
    }
    $documentIndexes = @()
    foreach ($document in $Documents) {
        # One separator, then each part as a blob index (Read-DocumentName's layout).
        $name = New-Object 'Collections.Generic.List[byte]'
        $name.Add([byte][char]'\')
        foreach ($part in $document.Split('\')) {
            if ($part.Length -eq 0) { $name.Add(0); continue }
            $partIndex = & $addBlob ([Text.Encoding]::UTF8.GetBytes($part))
            $name.AddRange([byte[]](ConvertTo-CompressedUIntBytes $partIndex))
        }
        $documentIndexes += (& $addBlob $name.ToArray())
    }
    $guids = New-Object IO.MemoryStream
    $recordRows = @()
    foreach ($record in $Records) {
        $guidBytes = ([Guid]$record.Kind).ToByteArray()
        $guids.Write($guidBytes, 0, 16)
        $recordRows += , @([int]($guids.Length / 16), (& $addBlob ([Text.Encoding]::UTF8.GetBytes([string]$record.Text))))
    }
    # A REAL LANGUAGE in every Document row: the C# GUID of the Portable PDB specification, appended AFTER the record
    # kinds so no record row's GUID index moves. Read-PortablePdbPrivacyFacts refuses a Language index outside the heap
    # and a PDB whose documents name no known language, so the 0 written here before left every image this builds
    # unreadable to the freshness check's privacy verdict (step 11 merge, R-5). No document, no GUID: the heap is unchanged.
    $languageIndex = 0
    if ($documentIndexes.Count -gt 0) {
        $languageGuid = ([Guid]'3F5162F8-07C6-11D3-9053-00C04FA302A1').ToByteArray()
        $guids.Write($languageGuid, 0, 16)
        $languageIndex = [int]($guids.Length / 16)
    }
    while (($blob.Length % 4) -ne 0) { $blob.WriteByte(0) }

    # #~: Document (0x30) and CustomDebugInformation (0x37), 2-byte indexes throughout.
    $tilde = New-Object IO.BinaryWriter (New-Object IO.MemoryStream)
    $tilde.Write([uint32]0); $tilde.Write([byte]2); $tilde.Write([byte]0); $tilde.Write([byte]0); $tilde.Write([byte]1)
    $tilde.Write([uint32]0); $tilde.Write([uint32]0x00810000); $tilde.Write([uint64]0)
    $tilde.Write([uint32]$documentIndexes.Count); $tilde.Write([uint32]$recordRows.Count)
    foreach ($index in $documentIndexes) { $tilde.Write([uint16]$index); $tilde.Write([uint16]0); $tilde.Write([uint16]0); $tilde.Write([uint16]$languageIndex) }
    foreach ($row in $recordRows) { $tilde.Write([uint16]((1 -shl 5) -bor 7)); $tilde.Write([uint16]$row[0]); $tilde.Write([uint16]$row[1]) }
    while (($tilde.BaseStream.Length % 4) -ne 0) { $tilde.Write([byte]0) }
    $pdbStream = New-Object byte[] 32

    $streams = @(
        @('#Pdb', $pdbStream),
        @('#~', ([IO.MemoryStream]$tilde.BaseStream).ToArray()),
        @('#Blob', $blob.ToArray()),
        @('#GUID', $guids.ToArray())
    )
    $version = [Text.Encoding]::ASCII.GetBytes("PDB v1.0`0`0`0`0")
    $headerLength = 16 + $version.Length + 4
    foreach ($stream in $streams) { $headerLength += 8 + [int]([Math]::Ceiling(($stream[0].Length + 1) / 4.0) * 4) }
    $pdb = New-Object IO.BinaryWriter (New-Object IO.MemoryStream)
    $pdb.Write([uint32]0x424A5342); $pdb.Write([uint16]1); $pdb.Write([uint16]1); $pdb.Write([uint32]0)
    $pdb.Write([uint32]$version.Length); $pdb.Write($version); $pdb.Write([uint16]0); $pdb.Write([uint16]$streams.Count)
    $offset = $headerLength
    foreach ($stream in $streams) {
        $pdb.Write([uint32]$offset); $pdb.Write([uint32]$stream[1].Length)
        $nameBytes = [Text.Encoding]::ASCII.GetBytes($stream[0])
        $pdb.Write($nameBytes)
        $pad = [int]([Math]::Ceiling(($nameBytes.Length + 1) / 4.0) * 4) - $nameBytes.Length
        $pdb.Write((New-Object byte[] $pad))
        $offset += $stream[1].Length
    }
    foreach ($stream in $streams) { $pdb.Write([byte[]]$stream[1]) }
    $pdbBytes = ([IO.MemoryStream]$pdb.BaseStream).ToArray()

    $deflated = New-Object IO.MemoryStream
    $deflate = New-Object IO.Compression.DeflateStream($deflated, [IO.Compression.CompressionMode]::Compress, $true)
    $deflate.Write($pdbBytes, 0, $pdbBytes.Length)
    $deflate.Dispose()
    $mpdb = New-Object IO.BinaryWriter (New-Object IO.MemoryStream)
    $mpdb.Write([uint32]0x4244504D); $mpdb.Write([uint32]$pdbBytes.Length); $mpdb.Write($deflated.ToArray())
    $mpdbBytes = ([IO.MemoryStream]$mpdb.BaseStream).ToArray()

    # MZ at 0, e_lfanew 0x40; PE32+ optional header (0xF0 bytes, 16 data directories); one section at RVA 0x1000,
    # file offset 0x200, holding the 28-byte debug directory entry followed by the MPDB blob.
    $sectionData = 28 + $mpdbBytes.Length
    $pe = New-Object IO.BinaryWriter (New-Object IO.MemoryStream)
    $pe.Write([byte[]](New-Object byte[] 0x200))
    $pe.Write([uint32]0); $pe.Write([uint32]0); $pe.Write([uint16]1); $pe.Write([uint16]0); $pe.Write([uint32]17)
    $pe.Write([uint32]$mpdbBytes.Length); $pe.Write([uint32](0x1000 + 28)); $pe.Write([uint32](0x200 + 28))
    $pe.Write($mpdbBytes)
    $pe.Write([Text.Encoding]::ASCII.GetBytes("`0" + $AsciiTail + "`0"))
    # The UTF-16 tail starts at an even file offset, or an odd one with -OddUtf16, so each of the two UTF-16 views in
    # Get-ByteTextViews has a leg of its own; left to the length of what came before, one of them would go unproved.
    $odd = ($pe.BaseStream.Position % 2) -eq 1
    if ($odd -ne [bool]$OddUtf16) { $pe.Write([byte]0) }
    $pe.Write([Text.Encoding]::Unicode.GetBytes("`0" + $Utf16Tail + "`0"))
    $image = ([IO.MemoryStream]$pe.BaseStream).ToArray()
    $image[0] = 0x4D; $image[1] = 0x5A
    [BitConverter]::GetBytes([int]0x40).CopyTo($image, 0x3C)
    $image[0x40] = 0x50; $image[0x41] = 0x45
    [BitConverter]::GetBytes([uint16]0x8664).CopyTo($image, 0x44)
    [BitConverter]::GetBytes([uint16]1).CopyTo($image, 0x46)
    [BitConverter]::GetBytes([uint16]0xF0).CopyTo($image, 0x54)
    [BitConverter]::GetBytes([uint16]0x20B).CopyTo($image, 0x58)
    [BitConverter]::GetBytes([uint32]16).CopyTo($image, 0x58 + 108)
    [BitConverter]::GetBytes([uint32]0x1000).CopyTo($image, 0x58 + 112 + 6 * 8)
    [BitConverter]::GetBytes([uint32]28).CopyTo($image, 0x58 + 112 + 6 * 8 + 4)
    $section = 0x58 + 0xF0
    [Text.Encoding]::ASCII.GetBytes('.debug').CopyTo($image, $section)
    [BitConverter]::GetBytes([uint32]$sectionData).CopyTo($image, $section + 8)
    [BitConverter]::GetBytes([uint32]0x1000).CopyTo($image, $section + 12)
    [BitConverter]::GetBytes([uint32]$sectionData).CopyTo($image, $section + 16)
    [BitConverter]::GetBytes([uint32]0x200).CopyTo($image, $section + 20)
    return , $image
}

<#
.SYNOPSIS
    Every leg of the build-identity scan, run against synthetic images: one result per leg, Passed and Name.

.DESCRIPTION
    Called by Test-ModulePublishFreshness.ps1 before it trusts a clean scan, and by
    tests\runtime-hardening-selftest.ps1 so the mutation harness sees a broken rule. A clean control comes first: a
    rule that flags everything would pass every positive leg. Each positive leg wants EXACTLY its own finding, so a
    rule that fires in the wrong place fails too, and no finding may carry the text it matched.
#>
function Test-BuildIdentityScan {
    $standIn = 'C:\Users\someone\Documents'
    $real = 'C:\Users\ExampleProfile\src\repo'
    $clean = @{ Documents = @('/_/src/Kit/A.cs'); Records = @(@{ Kind = $script:CompilationOptionsRecordKind; Text = "language`0C#" }) }
    # Name, PDB content, ASCII tail, UTF-16 tail, the findings wanted, and whether the UTF-16 tail sits at an odd offset.
    # The last four legs are the pattern gaps the review of N-bd-w13-contracts-1-02 measured under 5.1, each one a leg
    # the earlier patterns failed: an account segment ended by a control byte other than NUL, a forward-slash UNC root,
    # the long form of a UNC root, and another share on the stand-in host. The clean control carries what the
    # forward-slash rule must leave alone: a URL, and the base64, PRODID and binary shapes measured in the zips.
    $legs = @(
        @('the clean control, a mapped document and stand-in fixtures, is free of findings', $clean,
          ('\\server.invalid\share\x \\server\home\a https://buildhost/drop/x +//91qP/x -//Owner//Product//EN ///Q/e ' + $standIn),
          "C:\\Users\\O'Brien\\i.md \\.\pipe\x", @(), $false),
        @('a Source Link record is reported', @{ Documents = $clean.Documents; Records = @($clean.Records[0],
          @{ Kind = $script:SourceLinkRecordKind; Text = '{"documents":{"/_/*":"https://example.invalid/*"}}' }) },
          '', '', @('a Source Link record in the embedded PDB'), $false),
        @('a user-profile path in a document name is reported', @{ Documents = @(($real + '\A.cs')); Records = $clean.Records },
          '', '', @('a user-profile path in an embedded PDB document name'), $false),
        @('a user-profile path in an escaped PDB record is reported', @{ Documents = $clean.Documents; Records = @($clean.Records[0],
          @{ Kind = [Guid]'7E4D4708-096E-4C5C-AEDA-CB10BA6A740D'; Text = 'C:\\Users\\ExampleProfile\\obj' }) },
          '', '', @('a user-profile path in the embedded PDB (ASCII)'), $false),
        @('a user-profile path in the ASCII DLL bytes is reported', $clean, ($real.Replace('\', '/') + '/x.pdb'), '',
          @('a user-profile path in the DLL bytes (ASCII)'), $false),
        @('a user-profile path in the UTF-16 DLL bytes at an even offset is reported', $clean, '', ($real + '\B.cs'),
          @('a user-profile path in the DLL bytes (UTF-16)'), $false),
        @('a UNC root in the UTF-16 DLL bytes at an odd offset is reported', $clean, '', '\\buildhost\drop\B.cs',
          @('a UNC root in the DLL bytes (UTF-16)'), $true),
        @('a user-profile path whose account segment ends at a control byte is reported', $clean, ('C:\Users\ExampleProfile' + [char]0x0C), '',
          @('a user-profile path in the DLL bytes (ASCII)'), $false),
        @('a forward-slash UNC root in the ASCII DLL bytes is reported', $clean, '//buildhost/drop/B.cs', '',
          @('a UNC root in the DLL bytes (ASCII)'), $false),
        @('a long-form UNC root in the UTF-16 DLL bytes is reported', $clean, '', '\\?\UNC\buildhost\drop\B.cs',
          @('a UNC root in the DLL bytes (UTF-16)'), $false),
        @('a UNC root on the stand-in host under another share is reported', $clean, '\\server\builds\B.cs', '',
          @('a UNC root in the DLL bytes (ASCII)'), $false)
    )
    $results = New-Object 'Collections.Generic.List[object]'
    foreach ($leg in $legs) {
        $detail = ''
        try {
            $image = New-PortablePdbTestImage -Documents $leg[1].Documents -Records $leg[1].Records -AsciiTail $leg[2] -Utf16Tail $leg[3] -OddUtf16:([bool]$leg[5])
            $scan = Get-BuildIdentityFindings -Image $image
            $want = @($leg[4])
            $got = @($scan.Findings)
            $passed = ($scan.EmbeddedPdbCount -eq 1 -and $scan.CompilationOptionsRecords -eq 1 -and
                       (($got | Sort-Object) -join '|') -ceq (($want | Sort-Object) -join '|') -and
                       @($got | Where-Object { $_ -match 'ExampleProfile|buildhost|Users[\\/]' }).Count -eq 0)
            $detail = "found [$($got -join '; ')]"
        }
        catch { $passed = $false; $detail = 'threw: ' + $_.Exception.Message }
        $results.Add([pscustomobject]@{ Name = $leg[0]; Passed = $passed; Detail = $detail })
    }
    return $results.ToArray()
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
