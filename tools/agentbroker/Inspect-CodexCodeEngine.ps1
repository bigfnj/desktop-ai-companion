#requires -Version 7
# Read only known process identity metadata and fixed flag booleans. No client configuration or history.
[CmdletBinding()]
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
function Get-FixedCodeFlags([string[]]$Arguments) {
    $expected = @('-c', 'features.code_mode_host=true', 'app-server', '--analytics-default-enabled')
    $same = $Arguments.Count -eq $expected.Count
    if ($same) { for ($position = 0; $position -lt $expected.Count; $position++) { if ($Arguments[$position] -cne $expected[$position]) { $same = $false } } }
    return @{ AppServer = $Arguments -ccontains 'app-server'; CodeModeHost = $Arguments -ccontains 'features.code_mode_host=true';
        AnalyticsDefault = $Arguments -ccontains '--analytics-default-enabled'; MatchesPublicExtensionSpawn = $same }
}
if ($SelfTest) {
    $good = Get-FixedCodeFlags @('-c', 'features.code_mode_host=true', 'app-server', '--analytics-default-enabled')
    if (-not ($good.AppServer -and $good.CodeModeHost -and $good.AnalyticsDefault -and $good.MatchesPublicExtensionSpawn)) { throw 'expected_flags_refused' }
    $extra = Get-FixedCodeFlags @('-c', 'features.code_mode_host=true', 'app-server', '--analytics-default-enabled', '--unexpected')
    if ($extra.MatchesPublicExtensionSpawn) { throw 'extra_argument_accepted' }
    $wrong = Get-FixedCodeFlags @('-c', 'features.code_mode_host=false', 'app-server', '--analytics-default-enabled')
    if ($wrong.CodeModeHost -or $wrong.MatchesPublicExtensionSpawn) { throw 'wrong_flag_accepted' }
    Write-Output 'CodeEngineMetadataSelfTest=3 PASS; ProcessesInspected=False'
    exit 0
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CodexMetadataArguments {
  [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CommandLineToArgvW(string text,out int count);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
  public static string[] Parse(string text) {
    if (text == null || text.Length > 32768) throw new InvalidOperationException("metadata_arguments_unavailable");
    IntPtr memory=CommandLineToArgvW(text,out int count);
    if(memory==IntPtr.Zero) throw new InvalidOperationException("metadata_arguments_unavailable");
    try { if(count<1 || count>64) throw new InvalidOperationException("metadata_argument_count_refused"); var result=new string[count-1]; for(int i=1;i<count;i++) result[i-1]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size)); return result; }
    finally { LocalFree(memory); }
  }
}
'@
$expected = Join-Path $env:USERPROFILE '.vscode\extensions\openai.chatgpt-26.1007.21434-win32-x64\bin\windows-x86_64\codex.exe'
$expectedHash = 'F5521B058CA08E249AFD3B25B4681E81100181D92E73AA6B94BB8E75C2AA487B'
$rows = @(Get-CimInstance Win32_Process -Filter "Name='codex.exe'" -Property ProcessId,ParentProcessId,Name,ExecutablePath,CreationDate)
if ($rows.Count -gt 256) { throw 'engine_metadata_count_refused' }
$result = @()
foreach ($row in $rows) {
    $ancestors = [Collections.Generic.List[string]]::new()
    $parentId = [int]$row.ParentProcessId
    $codeAncestor = $false
    for ($depth = 0; $depth -lt 6 -and $parentId -gt 0; $depth++) {
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$parentId" -Property ProcessId,ParentProcessId,Name,CreationDate
        if (-not $parent -or $parent.CreationDate -gt $row.CreationDate) { break }
        $ancestors.Add($parent.Name)
        if ($parent.Name -ieq 'Code.exe') { $codeAncestor = $true; break }
        $parentId = [int]$parent.ParentProcessId
    }
    $matchesImage = $row.ExecutablePath -and $row.ExecutablePath.Equals($expected, [StringComparison]::OrdinalIgnoreCase)
    $item = @{ ProcessId = [int]$row.ProcessId; ParentProcessId = [int]$row.ParentProcessId;
        CreatedUtc = $row.CreationDate.ToUniversalTime().ToString('o'); HasCodeAncestor = $codeAncestor;
        DirectCodeChild = $ancestors.Count -gt 0 -and $ancestors[0] -ieq 'Code.exe'; AncestorNames = $ancestors.ToArray();
        PinnedExtensionImage = [bool]$matchesImage; FlagsInspected = $false }
    if ($codeAncestor -and $matchesImage) {
        for ($component = $expected; $component; $component = [IO.Path]::GetDirectoryName($component)) {
            if (([IO.File]::GetAttributes($component) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'linked_public_engine_refused' }
        }
        if ((Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash -cne $expectedHash) { throw 'public_engine_hash_mismatch' }
        $birth = $row.CreationDate.ToUniversalTime().ToString('yyyyMMddHHmmss.ffffff', [Globalization.CultureInfo]::InvariantCulture) + '+000'
        $argumentsRow = Get-CimInstance Win32_Process -Filter "ProcessId=$($row.ProcessId) AND CreationDate='$birth'" -Property CreationDate,CommandLine
        if (-not $argumentsRow -or $argumentsRow.CreationDate -ne $row.CreationDate) { throw 'engine_metadata_changed' }
        $item.Flags = Get-FixedCodeFlags ([CodexMetadataArguments]::Parse($argumentsRow.CommandLine))
        $item.FlagsInspected = $true
        $argumentsRow = $null
    }
    $result += $item
}
@{ MetadataOnly = $true; ClientConfigurationRead = $false; ConversationRead = $false; InputSent = $false; Engines = $result } | ConvertTo-Json -Depth 6 -Compress
