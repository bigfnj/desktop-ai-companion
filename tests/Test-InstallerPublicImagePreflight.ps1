#requires -Version 5
<#
.SYNOPSIS
    Read-only process-image preflight for the isolated MSI lifecycle test.
.DESCRIPTION
    Reads PID, retained-handle creation time and kernel-reported executable path.
    No command line, history, credentials, executable contents or live application
    input is read. Unknown/protected/reparse image locations fail before file access.
    Evidence is a short-lived safety check, not a human approval marker.
#>
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$workspace = [IO.Path]::GetFullPath((Split-Path $repo -Parent))
$output = [IO.Path]::GetFullPath($OutputPath)
foreach ($part in ($output -split '[\\/]')) {
    if ($part -match '(?i)(\.env$|^\.env\.|\.pem$|\.key$|\.p12$|\.pfx$|\.keystore$|^privatekey|^\.npmrc$|^\.aws$|^\.ssh$|^\.kube$|^\.config$|^\.mozilla$|^\.git$|^credentials?$)') {
        throw 'preflight_output_protected_scope_refused'
    }
}
if (-not $output.StartsWith((Join-Path $repo 'build') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($output) -cne '.json') { throw 'Preflight evidence must be a new JSON file below this checkout build directory.' }
. (Join-Path $repo 'packaging\StagingPathSafety.ps1')
[void](Assert-DesktopAICompanionPathChainSafe -Path $output -TrustedRoot $repo)
if (-not ('InstallerPublicImagePreflight' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerPublicImagePreflight {
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint length);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
  [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr process,uint milliseconds);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetFileAttributes(string path);
  public static void AssertNoReparse(string path) {
    string root=System.IO.Path.GetPathRoot(path);
    string current=root;
    foreach(string part in path.Substring(root.Length).Split('\\')) {
      if(part.Length==0) continue;
      current=System.IO.Path.Combine(current,part);
      uint attributes=GetFileAttributes(current);
      if(attributes==0xffffffff) {
        int error=Marshal.GetLastWin32Error();
        if(error==2 || error==3) return;
        throw new InvalidOperationException("public_image_path_metadata_unavailable");
      }
      if((attributes & 0x400)!=0) throw new InvalidOperationException("public_image_reparse_refused");
    }
  }
  public static string[] Read(uint pid) {
    IntPtr process=OpenProcess(0x00101000,false,pid);
    if(process==IntPtr.Zero) {
      if(Marshal.GetLastWin32Error()==87) return null;
      throw new InvalidOperationException("companion_image_identity_unavailable");
    }
    try {
      uint waited=WaitForSingleObject(process,0);
      if(waited==0) return null;
      if(waited==0xffffffff) throw new InvalidOperationException("companion_image_identity_unavailable");
      var path=new StringBuilder(32768); uint length=(uint)path.Capacity; long created,exited,kernel,user;
      if(!QueryFullProcessImageName(process,0,path,ref length) || !GetProcessTimes(process,out created,out exited,out kernel,out user))
        throw new InvalidOperationException("companion_image_identity_unavailable");
      return new[]{path.ToString(),created.ToString(System.Globalization.CultureInfo.InvariantCulture)};
    } finally { CloseHandle(process); }
  }
}
'@
}
[InstallerPublicImagePreflight]::AssertNoReparse($output)
$canonicalInstalled = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\Desktop AI Companion\DesktopAICompanion.exe'
$images = New-Object 'Collections.Generic.List[object]'
foreach ($process in [Diagnostics.Process]::GetProcessesByName('DesktopAICompanion')) {
    try {
        $metadata = [InstallerPublicImagePreflight]::Read([uint32]$process.Id)
        if ($null -eq $metadata) { continue }
        $image = [IO.Path]::GetFullPath($metadata[0])
        # Restrict this test to known ordinary workspace images and the exact
        # public installed host. Anything else needs a separate reviewed scope.
        if (-not $image.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase) -and $image -ine $canonicalInstalled) {
            throw 'companion_image_outside_reviewed_public_scope'
        }
        foreach ($part in ($image -split '[\\/]')) {
            if ($part -match '(?i)(\.env$|^\.env\.|\.pem$|\.key$|\.p12$|\.pfx$|\.keystore$|^privatekey|^\.npmrc$|^\.aws$|^\.ssh$|^\.kube$|^\.config$|^\.mozilla$|^\.git$|^credentials?$|^User Data$|^Firefox$|^Keychains$)') {
                throw 'companion_image_protected_scope_refused'
            }
        }
        # Only now inspect the approved ordinary image's path metadata.
        [InstallerPublicImagePreflight]::AssertNoReparse($image)
        $trusted = if ($image.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase)) { $workspace } else { Split-Path $canonicalInstalled -Parent }
        [void](Assert-DesktopAICompanionPathChainSafe -Path $image -TrustedRoot $trusted)
        $images.Add([pscustomobject]@{ Pid=$process.Id; Created=$metadata[1]; Image=$image })
    } finally { $process.Dispose() }
}
$report = [ordered]@{ ScopedShutdownSafe=$true; ObservedAtUtc=[DateTime]::UtcNow.ToString('o'); Processes=@($images.ToArray());
    CommandLinesRead=$false; ExecutableContentsRead=$false; InstalledApplicationTouched=$false }
$parent = Split-Path $output -Parent
[void][IO.Directory]::CreateDirectory($parent)
[InstallerPublicImagePreflight]::AssertNoReparse($output)
[void](Assert-DesktopAICompanionPathChainSafe -Path $output -TrustedRoot $repo)
$bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($report | ConvertTo-Json -Depth 5))
$stream = [IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
[pscustomobject]@{ ScopedShutdownSafe=$true; ImageCount=$images.Count; Evidence=$output; InstalledApplicationTouched=$false }
