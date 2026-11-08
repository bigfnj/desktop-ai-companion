#requires -Version 5
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'packaging\ContentCatalogAssets.ps1')
Add-Type -TypeDefinition @'
using System;
using System.IO;
public sealed class CatalogClosedInputStream : Stream {
    public override bool CanRead { get { return false; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return true; } }
    public override long Length { get { return 0; } }
    public override long Position { get { return 0; } set { throw new NotSupportedException(); } }
    public override void Flush() { throw new IOException("synthetic_closed_input"); }
    public override void Write(byte[] buffer,int offset,int count) { throw new IOException("synthetic_closed_input"); }
    public override int Read(byte[] buffer,int offset,int count) { throw new NotSupportedException(); }
    public override long Seek(long offset,SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
}
'@
$script:ActualCatalogStart = ${function:Start-CatalogAssetBatch}
$script:ExerciseReadEof = $false
function Start-CatalogAssetBatch([string]$RepoRoot, [string[]]$StallChild, [int]$TimeoutMs = 60000) {
    $batch = & $script:ActualCatalogStart $RepoRoot $StallChild $TimeoutMs
    if (-not $batch.Process.WaitForExit(2000)) { Close-CatalogAssetBatch $batch -Kill; throw 'CATALOG_DEAD_FIXTURE: child did not exit.' }
    # Model each deterministic ordering separately. Even after process exit a short native pipe
    # write can still succeed, so waiting for exit alone does not force the broken-input branch.
    $batch.Stdin.Dispose()
    $batch.Stdin = if ($script:ExerciseReadEof) { [IO.MemoryStream]::new() } else { [CatalogClosedInputStream]::new() }
    return $batch
}
$checks = 0
try {
    foreach ($readEof in @($false, $true)) {
        $script:ExerciseReadEof = $readEof
        $message = ''
        try { $null = Get-CatalogAsset $RepoRoot 'synthetic-never-read' 'synthetic-never-read' 1000 @('cmd.exe', '/c', 'exit 1') }
        catch { $message = $_.Exception.Message }
        $expected = if ($readEof) { 'closed its output' } else { 'closed its input' }
        if (-not $message.Contains($expected) -or -not $message.Contains('Refusing to fall back') -or $message.Contains('did not return within')) {
            throw ('CATALOG_DEAD_CHILD_' + $(if ($readEof) { 'OUTPUT' } else { 'INPUT' }) + ': dead-child refusal did not identify the actual failed boundary: ' + $message)
        }
        $checks++
    }
} finally { Stop-CatalogAssetBatch }
Write-Output ('SUMMARY: Test-CatalogDeadChild passed ' + $checks + ' checks; owned children disposed.')
