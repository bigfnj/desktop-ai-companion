# Product-owned AgentBroker release payload binding. Dot-source through the provenance library.
# Uses metadata readers only; never loads or executes an assembly from the payload.
function Get-ABPMetadataReader {
 $path=Join-Path $PSScriptRoot 'AgentBrokerReleasePayloadMetadata.cs';Assert-ABPPlain $path
 $source=[IO.File]::ReadAllText($path);$hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
 $name='AgentBrokerReleasePayloadMetadata_'+$hash
 $type=$name -as [type]
 if($null -eq $type){
  $references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File|ForEach-Object FullName)
  Assert-ABP ($references.Count -gt 0) 'PROVENANCE_METADATA_RUNTIME_REFERENCES_REQUIRED'
  Add-Type -TypeDefinition ($source.Replace('AgentBrokerReleasePayloadMetadata',$name)) -ReferencedAssemblies $references -ErrorAction Stop
  $type=$name -as [type]
 }
 Assert-ABP ($null -ne $type) 'PROVENANCE_METADATA_READER_REQUIRED'
 return $type
}
function Get-ABPCanonicalPayloadRoot {Join-Path $script:ABPRepoRoot 'build/DesktopAICompanionPortable/bin/Release/x64/modules/agentbroker'}
function Get-ABPFileHash([string]$Path){Assert-ABPPlain $Path;(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Get-ABPPayloadRows([string]$Root,$Expected=$null){
 $full=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
 Assert-ABP ($full -ceq [IO.Path]::GetFullPath((Get-ABPCanonicalPayloadRoot)).TrimEnd('\','/')) 'PROVENANCE_CANONICAL_OUTPUT_REQUIRED'
 Assert-ABP ([IO.Directory]::Exists($full)) 'PROVENANCE_OUTPUT_REQUIRED';Assert-ABPPlain $full
 $files=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($full);$visited=0
 while($pending.Count){foreach($path in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())){
  $visited++;Assert-ABP ($visited -le 20000) 'PROVENANCE_OUTPUT_ENTRY_LIMIT';Assert-ABPPlain $path
  $relative=$path.Substring($full.Length+1).Replace('\','/')
  Assert-ABP ($relative -notmatch '(^|/)(\.|\.\.)(/|$)|[:\x00-\x1F]' -and $seen.Add($relative)) 'PROVENANCE_OUTPUT_NAME_REFUSED'
  $isDirectory=([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::Directory) -ne 0
  if($isDirectory){Assert-ABP ($relative -ceq 'worker' -or $relative.StartsWith('worker/',[StringComparison]::Ordinal)) 'PROVENANCE_OUTPUT_LAYOUT';$pending.Push($path)}
  elseif([IO.Path]::GetExtension($path) -notin @('.pdb','.lib')){
   Assert-ABP ($relative -in @('AgentBroker.dll','AgentBroker.deps.json','DesktopAICompanion.ModuleKit.dll') -or $relative.StartsWith('worker/',[StringComparison]::Ordinal)) 'PROVENANCE_OUTPUT_LAYOUT'
   $files.Add($relative,$path)
  }
 }}
 $names=[string[]]@($files.Keys);[Array]::Sort($names,[StringComparer]::Ordinal)
 Assert-ABP ($names.Count -gt 6) 'PROVENANCE_OUTPUT_INCOMPLETE'
 # Reject an unexpected file set before opening any file not listed by the accepted receipt.
 if($null -ne $Expected){$expectedNames=[string[]]@($Expected|ForEach-Object path);Assert-ABP ($names.Count -eq $expectedNames.Count) 'PROVENANCE_OUTPUT_CHANGED';for($i=0;$i -lt $names.Count;$i++){Assert-ABP ($names[$i] -ceq $expectedNames[$i]) 'PROVENANCE_OUTPUT_CHANGED'}}
 $rows=@();[long]$total=0
 foreach($name in $names){$path=$files[$name];$length=(Get-Item -LiteralPath $path).Length;$total+=$length;Assert-ABP ($length -le 512MB -and $total -le 1GB) 'PROVENANCE_OUTPUT_BYTES_LIMIT';$hash=Get-ABPFileHash $path;Assert-ABP ((Get-Item -LiteralPath $path).Length -eq $length) 'PROVENANCE_OUTPUT_CHANGED';$rows+=[pscustomobject][ordered]@{path=$name;bytes=$length;sha256=$hash}}
 return ,$rows
}
function Get-ABPRowsHash($Rows){Get-ABPHash ([Text.UTF8Encoding]::new($false).GetBytes(($Rows|ConvertTo-Json -Depth 4 -Compress -EscapeHandling EscapeNonAscii)))}
function Assert-ABPMetadata([string]$Root,[string]$Manifest,[string]$Sha,[string]$Parent){
 $type=Get-ABPMetadataReader
 Assert-ABP ($type::Validate($Root,$Manifest,$Sha,$Parent) -eq 0) 'PROVENANCE_PAYLOAD_METADATA_REFUSED'
}
function New-AgentBrokerReleaseOutputReceipt([string]$Manifest,[string]$Sha,[string]$Parent){
 $null=Read-AgentBrokerReleaseManifest $Manifest $Sha $Parent ''
 $root=Get-ABPCanonicalPayloadRoot
 Assert-ABPMetadata $root $Manifest $Sha $Parent
 $rows=Get-ABPPayloadRows $root;$inventory=Get-ABPRowsHash $rows
 $null=Read-AgentBrokerReleaseManifest $Manifest $Sha $Parent ''
 Assert-ABP ((Get-ABPRowsHash (Get-ABPPayloadRows $root $rows)) -ceq $inventory) 'PROVENANCE_OUTPUT_CHANGED'
 $record=[ordered]@{schema=1;moduleId='agentbroker';configuration='Release';sourceContentSha256=$Sha;parentCommit=$Parent;sourceManifestSha256=(Get-ABPFileHash $Manifest);metadataReaderSha256=(Get-ABPFileHash (Join-Path $PSScriptRoot 'AgentBrokerReleasePayloadMetadata.cs'));newModulePublishSha256=(Get-ABPFileHash (Join-Path $PSScriptRoot 'New-ModulePublish.ps1'));newModuleDistZipSha256=(Get-ABPFileHash (Join-Path $PSScriptRoot 'New-ModuleDistZip.ps1'));fileCount=$rows.Count;totalBytes=[long](($rows|Measure-Object -Property bytes -Sum).Sum);inventorySha256=$inventory;files=$rows}
 $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($record|ConvertTo-Json -Depth 8))
 $receiptSha=Get-ABPHash $bytes
 $path=Join-Path (Get-ABPOutputRoot) ($Sha+'.'+$receiptSha+'.output.json')
 # Full receipt bytes name the immutable record, including operational tool authority; no self-hash field.
 if([IO.File]::Exists($path)){Assert-ABPPlain $path;Assert-ABP ((Get-ABPFileHash $path) -ceq (Get-ABPHash $bytes)) 'PROVENANCE_OUTPUT_RECEIPT_COLLISION'}
 else{$stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}}
 [pscustomobject]@{OutputReceipt=$path;OutputReceiptSha256=(Get-ABPFileHash $path);SourceContentSha256=$Sha;InventorySha256=$inventory;FileCount=$rows.Count}
}
function Test-AgentBrokerReleaseOutput([string]$ReceiptPath,[string]$ReceiptSha256,[string]$Root){
 Assert-ABP ($ReceiptSha256 -cmatch '^[A-F0-9]{64}\z' -and -not [string]::IsNullOrWhiteSpace($ReceiptPath)) 'PROVENANCE_OUTPUT_RECEIPT_REQUIRED'
 $full=[IO.Path]::GetFullPath($ReceiptPath);Assert-ABP ($full.StartsWith((Get-ABPOutputRoot)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'PROVENANCE_OUTPUT_RECEIPT_LOCATION'
 Assert-ABP ([IO.File]::Exists($full) -and (Get-Item -LiteralPath $full).Length -le 4MB) 'PROVENANCE_OUTPUT_RECEIPT_REQUIRED'
 Assert-ABP ((Get-ABPFileHash $full) -ceq $ReceiptSha256) 'PROVENANCE_OUTPUT_RECEIPT_HASH'
 $record=ConvertFrom-Json ([IO.File]::ReadAllText($full))
 Assert-ABP ($record.schema -eq 1 -and $record.moduleId -ceq 'agentbroker' -and $record.configuration -ceq 'Release' -and $record.sourceContentSha256 -cmatch '^[A-F0-9]{64}\z' -and $record.parentCommit -cmatch '^[a-f0-9]{40}\z') 'PROVENANCE_OUTPUT_RECEIPT_SHAPE'
 Assert-ABP ($full -ceq (Join-Path (Get-ABPOutputRoot) ($record.sourceContentSha256+'.'+$ReceiptSha256+'.output.json'))) 'PROVENANCE_OUTPUT_RECEIPT_LOCATION'
 $manifest=Join-Path (Get-ABPOutputRoot) ($record.sourceContentSha256+'.json')
 $null=Read-AgentBrokerReleaseManifest $manifest $record.sourceContentSha256 $record.parentCommit ''
 Assert-ABP ($null -ne $record.PSObject.Properties['newModulePublishSha256'] -and $null -ne $record.PSObject.Properties['newModuleDistZipSha256'] -and $record.newModulePublishSha256 -cmatch '^[A-F0-9]{64}\z' -and $record.newModuleDistZipSha256 -cmatch '^[A-F0-9]{64}\z') 'PROVENANCE_OUTPUT_AUTHORITY_CHANGED'
 Assert-ABP ((Get-ABPFileHash $manifest) -ceq $record.sourceManifestSha256 -and (Get-ABPFileHash (Join-Path $PSScriptRoot 'AgentBrokerReleasePayloadMetadata.cs')) -ceq $record.metadataReaderSha256 -and (Get-ABPFileHash (Join-Path $PSScriptRoot 'New-ModulePublish.ps1')) -ceq $record.newModulePublishSha256 -and (Get-ABPFileHash (Join-Path $PSScriptRoot 'New-ModuleDistZip.ps1')) -ceq $record.newModuleDistZipSha256) 'PROVENANCE_OUTPUT_AUTHORITY_CHANGED'
 $rows=Get-ABPPayloadRows $Root $record.files
 Assert-ABP ($rows.Count -eq $record.fileCount -and [long](($rows|Measure-Object -Property bytes -Sum).Sum) -eq $record.totalBytes -and (Get-ABPRowsHash $record.files) -ceq $record.inventorySha256 -and (Get-ABPRowsHash $rows) -ceq $record.inventorySha256) 'PROVENANCE_OUTPUT_CHANGED'
 Assert-ABPMetadata $Root $manifest $record.sourceContentSha256 $record.parentCommit
 $null=Read-AgentBrokerReleaseManifest $manifest $record.sourceContentSha256 $record.parentCommit ''
 Assert-ABP ((Get-ABPRowsHash (Get-ABPPayloadRows $Root $record.files)) -ceq $record.inventorySha256) 'PROVENANCE_OUTPUT_CHANGED'
 return $record
}
function Assert-AgentBrokerReleaseZip([string]$ZipPath,$Record,[string]$ExpectedZipSha256=''){
 $expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
 foreach($row in $Record.files){$expected.Add([string]$row.path,$row)}
 $inputStream=[IO.File]::OpenRead($ZipPath)
 try{$zip=[IO.Compression.ZipArchive]::new($inputStream,[IO.Compression.ZipArchiveMode]::Read,$true)
  try{
   Assert-ABP ($zip.Entries.Count -eq $expected.Count) 'PROVENANCE_ZIP_CHANGED'
   $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
   foreach($entry in $zip.Entries){Assert-ABP ($expected.ContainsKey($entry.FullName) -and $seen.Add($entry.FullName)) 'PROVENANCE_ZIP_CHANGED'}
   foreach($entry in $zip.Entries){$row=$expected[$entry.FullName];Assert-ABP ($entry.Length -eq $row.bytes) 'PROVENANCE_ZIP_CHANGED';$stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create();try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','');Assert-ABP ($hash -ceq $row.sha256) 'PROVENANCE_ZIP_CHANGED'}finally{$sha.Dispose();$stream.Dispose()}}
  }finally{$zip.Dispose()}
 }finally{$inputStream.Dispose()}
 if($ExpectedZipSha256){Assert-ABP ($ExpectedZipSha256 -cmatch '^[A-F0-9]{64}\z' -and (Get-ABPFileHash $ZipPath) -ceq $ExpectedZipSha256) 'PROVENANCE_ACCEPTED_ZIP_CHANGED'}
}
