#requires -Version 7
<# Opt-in source-content release identity. No install, ZIP, Git writes or publication. #>
[CmdletBinding()]
param([ValidateSet('Prepare','Verify','Build','VerifyOutput','Library')][string]$Mode='Prepare',
 [string]$ManifestPath,[string]$ExpectedSha256,[string]$ExpectedParent,[string]$ActualSdk,
 [string]$OutputReceipt,[string]$OutputReceiptSha256)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$script:ABPRepoRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\','/')
$script:ABPProjects=@('modules/AgentBroker/AgentBroker.csproj','src/DesktopAICompanion.Contracts/DesktopAICompanion.Contracts.csproj','src/DesktopAICompanion.ModuleKit/DesktopAICompanion.ModuleKit.csproj','tools/AgentBroker.Worker/AgentBroker.Worker.csproj','src/AgentBroker.Core/AgentBroker.Core.csproj','tools/AgentBroker.CodexDesktopAdapter/AgentBroker.CodexDesktopAdapter.csproj','tools/AgentBroker.ClaudeAdapter/AgentBroker.ClaudeAdapter.csproj')
function Assert-ABP([bool]$Value,[string]$Code){if(-not $Value){throw $Code}}
function Get-ABPHash([byte[]]$Bytes){$sha=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','')}finally{$sha.Dispose()}}
function Get-ABPRelative([string]$Path){
 $full=[IO.Path]::GetFullPath($Path)
 Assert-ABP ($full.StartsWith($script:ABPRepoRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'PROVENANCE_INPUT_OUTSIDE_REPOSITORY'
 return $full.Substring($script:ABPRepoRoot.Length+1).Replace('\','/')
}
function Assert-ABPPlain([string]$Path){
 for($walk=[IO.Path]::GetFullPath($Path);$walk;$walk=[IO.Path]::GetDirectoryName($walk)){
  if([IO.File]::Exists($walk) -or [IO.Directory]::Exists($walk)){Assert-ABP (([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'PROVENANCE_REPARSE_REFUSED'}
 }
}
function Add-ABPInput($Set,[string]$Path){
 $relative=Get-ABPRelative $Path
 Assert-ABP ($relative -notmatch '(^|/)(bin|obj|artifacts|build|\.git)(/|$)') 'PROVENANCE_GENERATED_INPUT_REFUSED'
 Assert-ABP ($relative -notmatch '(?i)(^|/)(\.env(?:\..*)?|\.npmrc|privatekey[^/]*)$|\.(pem|key|pfx|p12|keystore)$') 'PROVENANCE_PROTECTED_INPUT_REFUSED'
 Assert-ABP ([IO.File]::Exists($Path)) 'PROVENANCE_DECLARED_INPUT_MISSING';Assert-ABPPlain $Path
 Assert-ABP ((Get-Item -LiteralPath $Path).Length -le 8MB) 'PROVENANCE_SOURCE_BYTES_LIMIT'
 [void]$Set.Add($relative);Assert-ABP ($Set.Count -le 4096) 'PROVENANCE_INPUT_LIMIT'
}
function Add-ABPTree($Set,[string]$Directory,[switch]$AllFiles,[switch]$WebProject){
 Assert-ABPPlain $Directory
 $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($Directory);$visited=0
 while($pending.Count){foreach($entry in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())){
  $visited++;Assert-ABP ($visited -le 16384) 'PROVENANCE_DIRECTORY_ENTRY_LIMIT'
  $attributes=[IO.File]::GetAttributes($entry);Assert-ABP (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'PROVENANCE_REPARSE_REFUSED'
  $name=[IO.Path]::GetFileName($entry);$isDirectory=($attributes -band [IO.FileAttributes]::Directory) -ne 0
  Assert-ABP ($name -ine 'runtimeconfig.template.json' -and $name -ine 'app.config') 'PROVENANCE_TODO_IMPLICIT_CONTENT'
  Assert-ABP ($isDirectory -or -not $name.EndsWith('.user',[StringComparison]::OrdinalIgnoreCase)) 'PROVENANCE_TODO_USER_PROJECT_FILE'
  if($WebProject){
   $extension=[IO.Path]::GetExtension($entry)
   Assert-ABP (-not (($isDirectory -and $name -ieq 'wwwroot') -or (-not $isDirectory -and
    ($extension -in @('.config','.cshtml','.razor') -or ($extension -ieq '.json' -and $name -ine 'packages.lock.json'))))) 'PROVENANCE_TODO_IMPLICIT_CONTENT'
  }
  if(($attributes -band [IO.FileAttributes]::Directory) -ne 0){if([IO.Path]::GetFileName($entry) -notin @('bin','obj','.git','.vs')){$pending.Push($entry)}}
  elseif($AllFiles -or [IO.Path]::GetExtension($entry) -in @('.cs','.csproj','.props','.targets','.resx') -or [IO.Path]::GetFileName($entry) -in @('.editorconfig','.globalconfig')){Add-ABPInput $Set $entry}
 }}
}
function Assert-ABPReferencePolicy([xml]$Xml){
 $forbidden=@($Xml.SelectNodes('//*')|Where-Object{$_.LocalName -ieq 'ItemDefinitionGroup' -or $_.LocalName -ieq '_GlobalPropertiesToRemoveFromProjectReferences'})
 Assert-ABP ($forbidden.Count -eq 0) 'PROVENANCE_TODO_REFERENCE_PROPERTIES_CHANGED'
}
function Get-ABPIdentity {
 $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 $references=@{
  'modules/AgentBroker/AgentBroker.csproj'=@('src/DesktopAICompanion.Contracts/DesktopAICompanion.Contracts.csproj','src/DesktopAICompanion.ModuleKit/DesktopAICompanion.ModuleKit.csproj')
  'src/DesktopAICompanion.ModuleKit/DesktopAICompanion.ModuleKit.csproj'=@('src/DesktopAICompanion.Contracts/DesktopAICompanion.Contracts.csproj')
  'tools/AgentBroker.Worker/AgentBroker.Worker.csproj'=@('src/AgentBroker.Core/AgentBroker.Core.csproj','tools/AgentBroker.CodexDesktopAdapter/AgentBroker.CodexDesktopAdapter.csproj','tools/AgentBroker.ClaudeAdapter/AgentBroker.ClaudeAdapter.csproj')
 }
 foreach($relative in $script:ABPProjects){
  $project=Join-Path $script:ABPRepoRoot $relative;$directory=Split-Path $project -Parent
  Add-ABPInput $set $project
  [xml]$xml=[IO.File]::ReadAllText($project)
  Assert-ABPReferencePolicy $xml
  Add-ABPTree $set $directory -WebProject:($xml.Project.Sdk -eq 'Microsoft.NET.Sdk.Web')
  $frameworks=@($xml.SelectNodes('//*[local-name()="TargetFramework"]'));$expectedFramework=if($relative -eq 'src/AgentBroker.Core/AgentBroker.Core.csproj'){'net10.0'}else{'net10.0-windows'}
  Assert-ABP ($frameworks.Count -eq 1 -and $frameworks[0].InnerText -ceq $expectedFramework) 'PROVENANCE_TODO_TARGET_FRAMEWORK_CHANGED'
  Assert-ABP ($xml.Project.Sdk -in @('Microsoft.NET.Sdk','Microsoft.NET.Sdk.Web') -and -not $xml.Project.GetAttribute('TreatAsLocalProperty')) 'PROVENANCE_TODO_PROJECT_EVALUATION_CHANGED'
  foreach($reference in $xml.SelectNodes('//*[local-name()="ProjectReference"]')){
   Assert-ABP ($reference.HasAttribute('Include') -and -not [string]::IsNullOrWhiteSpace($reference.GetAttribute('Include'))) 'PROVENANCE_TODO_REFERENCE_SHAPE'
   foreach($attribute in $reference.Attributes){Assert-ABP ($attribute.Name -in @('Include','Private')) 'PROVENANCE_TODO_REFERENCE_PROPERTIES_CHANGED'}
   foreach($metadata in $reference.ChildNodes){if($metadata -is [Xml.XmlElement]){Assert-ABP ($metadata.LocalName -ceq 'Private' -and $metadata.Attributes.Count -eq 0) 'PROVENANCE_TODO_REFERENCE_PROPERTIES_CHANGED'}}
  }
  $actual=@()
  foreach($node in $xml.SelectNodes('//*[@Include or @Update]')){
   if($node.HasAttribute('Update')){
    if($node.LocalName -in @('Compile','None','Content','EmbeddedResource')){
     foreach($metadata in @($node.Attributes)+@($node.ChildNodes|Where-Object{$_ -is [Xml.XmlElement]})){
      $name=[string]$metadata.LocalName
      if($name -notin @('CopyToOutputDirectory','CopyToPublishDirectory','LogicalName','Link')){continue}
      $value=if($metadata -is [Xml.XmlAttribute]){[string]$metadata.Value}else{[string]$metadata.InnerText}
      $copy=$name -in @('CopyToOutputDirectory','CopyToPublishDirectory')
      Assert-ABP ([string]::IsNullOrWhiteSpace($value) -or ($copy -and $value.Trim() -ieq 'Never')) 'PROVENANCE_TODO_ITEM_UPDATE'
     }
    }
    continue
   }
   $include=[string]$node.GetAttribute('Include')
   if($node.LocalName -eq 'ProjectReference'){
    Assert-ABP (-not $node.GetAttribute('GlobalPropertiesToRemove') -and -not $node.SelectSingleNode('GlobalPropertiesToRemove')) 'PROVENANCE_TODO_REFERENCE_PROPERTIES_CHANGED'
    $resolved=Get-ABPRelative ([IO.Path]::GetFullPath((Join-Path $directory $include)));$actual+=,$resolved
    Assert-ABP ($resolved -in $script:ABPProjects) 'PROVENANCE_TODO_NEW_PROJECT_REFERENCE'
   }elseif($node.LocalName -in @('Compile','EmbeddedResource','ModulePayloadInput','ModulePayloadProject','Content','None')){
    if($node.LocalName -eq 'None' -and -not $node.GetAttribute('CopyToOutputDirectory') -and -not $node.SelectSingleNode('CopyToOutputDirectory') -and -not $node.GetAttribute('CopyToPublishDirectory') -and -not $node.SelectSingleNode('CopyToPublishDirectory')){continue}
    if($include -eq '$(IntermediateOutputPath)SharedAtomicFile.g.cs' -and $relative -eq 'src/AgentBroker.Core/AgentBroker.Core.csproj'){continue}
    Assert-ABP ($include -notmatch '\$\(') 'PROVENANCE_TODO_UNRESOLVED_INPUT'
    if($include.EndsWith('\**\*',[StringComparison]::Ordinal) -or $include.EndsWith('/**/*',[StringComparison]::Ordinal)){
     Assert-ABP ($node.LocalName -eq 'ModulePayloadInput') 'PROVENANCE_TODO_NEW_GLOB'
     Add-ABPTree $set ([IO.Path]::GetFullPath((Join-Path $directory $include.Substring(0,$include.Length-5)))) -AllFiles
    }else{Assert-ABP ($include -notmatch '[*?]') 'PROVENANCE_TODO_NEW_GLOB';Add-ABPInput $set ([IO.Path]::GetFullPath((Join-Path $directory $include)))}
   }else{Assert-ABP ($node.LocalName -in @('PackageReference','InternalsVisibleTo','FrameworkReference')) 'PROVENANCE_TODO_NEW_INPUT_KIND'}
  }
  $expected=@();if($references.ContainsKey($relative)){$expected=@($references[$relative])}
  Assert-ABP ($actual.Count -eq $expected.Count -and @($actual|Where-Object{$_ -notin $expected}).Count -eq 0) 'PROVENANCE_TODO_PROJECT_GRAPH_CHANGED'
  foreach($node in $xml.SelectNodes('//*[local-name()="Target"]')){Assert-ABP (($relative -eq 'modules/AgentBroker/AgentBroker.csproj' -and $node.GetAttribute('Name') -eq 'PublishAgentBrokerWorker') -or ($relative -eq 'src/AgentBroker.Core/AgentBroker.Core.csproj' -and $node.GetAttribute('Name') -eq 'PrepareSharedAtomicFile')) 'PROVENANCE_TODO_NEW_GENERATOR_TARGET'}
  if($relative -eq 'src/AgentBroker.Core/AgentBroker.Core.csproj'){$copies=@($xml.SelectNodes('//*[local-name()="Copy"]'));Assert-ABP ($copies.Count -eq 1 -and $copies[0].GetAttribute('SourceFiles') -ceq '$(MSBuildThisFileDirectory)..\DesktopAICompanion.ModuleKit\AtomicFile.cs') 'PROVENANCE_TODO_SHARED_GENERATOR_CHANGED'}
  $lock=Join-Path $directory 'packages.lock.json';if([IO.File]::Exists($lock)){Add-ABPInput $set $lock}else{Assert-ABP ($xml.SelectNodes('//*[local-name()="PackageReference"]').Count -eq 0) 'PROVENANCE_PACKAGE_LOCK_REQUIRED'}
  for($walk=$directory;$walk.StartsWith($script:ABPRepoRoot,[StringComparison]::OrdinalIgnoreCase);$walk=Split-Path $walk -Parent){
   Assert-ABP (-not [IO.File]::Exists((Join-Path $walk 'Directory.Build.rsp'))) 'PROVENANCE_TODO_RESPONSE_FILE'
   foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','.editorconfig','.globalconfig','NuGet.Config')){$candidate=Join-Path $walk $name;if([IO.File]::Exists($candidate)){Add-ABPInput $set $candidate}}
   if($walk -ceq $script:ABPRepoRoot){break}
  }
 }
 for($walk=Split-Path $script:ABPRepoRoot -Parent;$walk;$walk=Split-Path $walk -Parent){foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','Directory.Build.rsp','.editorconfig','.globalconfig')){Assert-ABP (-not [IO.File]::Exists((Join-Path $walk $name))) 'PROVENANCE_TODO_EXTERNAL_IMPLICIT_INPUT'}}
 foreach($relative in @('global.json','ProductVersion.props','packaging/AgentBrokerReleaseProvenance.ps1','packaging/AgentBrokerReleaseProvenance.props','packaging/Publish-AgentBrokerWorker.ps1','packaging/StagingPathSafety.ps1','packaging/AgentBrokerReleaseOutput.ps1','packaging/AgentBrokerReleasePayloadMetadata.cs')){Add-ABPInput $set (Join-Path $script:ABPRepoRoot $relative)}
 # Refuse unreviewed imports: this is a current-closure identity, not a general MSBuild evaluator.
 foreach($relative in @($set|Where-Object{[IO.Path]::GetExtension($_) -in @('.props','.targets','.csproj')})){
  $path=Join-Path $script:ABPRepoRoot $relative;[xml]$xml=[IO.File]::ReadAllText($path)
  Assert-ABPReferencePolicy $xml
  Assert-ABP (-not $xml.Project.GetAttribute('TreatAsLocalProperty')) 'PROVENANCE_TODO_LOCAL_PROPERTY_OVERRIDE'
  Assert-ABP ($xml.SelectNodes('//*[local-name()="UsingTask"]').Count -eq 0) 'PROVENANCE_TODO_CUSTOM_BUILD_TASK'
  if([IO.Path]::GetExtension($relative) -in @('.props','.targets') -and $relative -cne 'packaging/AgentBrokerReleaseProvenance.props'){
   Assert-ABP ($xml.SelectNodes('//*[@Include or @Update or @Remove]').Count -eq 0 -and $xml.SelectNodes('//*[local-name()="Target"]').Count -eq 0) 'PROVENANCE_TODO_IMPORTED_INPUT_DECLARATION'
  }
  foreach($import in $xml.SelectNodes('//*[local-name()="Import"]')){
   $value=([string]$import.Project).Replace('$(MSBuildThisFileDirectory)',([IO.Path]::GetDirectoryName($path)+[IO.Path]::DirectorySeparatorChar))
   Assert-ABP ($value -notmatch '\$\(' -and $value -notmatch '[*?]') 'PROVENANCE_TODO_UNRESOLVED_IMPORT'
   if(-not [IO.Path]::IsPathRooted($value)){$value=Join-Path ([IO.Path]::GetDirectoryName($path)) $value}
   Assert-ABP ($set.Contains((Get-ABPRelative ([IO.Path]::GetFullPath($value))))) 'PROVENANCE_TODO_IMPORT_NOT_CAPTURED'
  }
 }
 $global=ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $script:ABPRepoRoot 'global.json')))
 Assert-ABP ($global.sdk.rollForward -ceq 'disable' -and $global.sdk.allowPrerelease -eq $false -and $global.sdk.version -match '^\d+\.\d+\.\d+$') 'PROVENANCE_SDK_NOT_PINNED'
 $paths=[string[]]@($set);[Array]::Sort($paths,[StringComparer]::Ordinal);$sources=@();[long]$total=0
 foreach($relative in $paths){$path=Join-Path $script:ABPRepoRoot $relative;$length=(Get-Item -LiteralPath $path).Length;$total+=$length;Assert-ABP ($length -le 8MB -and $total -le 64MB) 'PROVENANCE_SOURCE_BYTES_LIMIT';$bytes=[IO.File]::ReadAllBytes($path);Assert-ABP ($bytes.Length -eq $length) 'PROVENANCE_SOURCE_CHANGED';Assert-ABP ([IO.Path]::GetExtension($relative) -ieq '.png' -or -not ($bytes -contains [byte]13)) 'PROVENANCE_WORKING_TREE_NOT_NORMALIZED';$sources+=[ordered]@{path=$relative;bytes=$bytes.Length;sha256=(Get-ABPHash $bytes)}}
 return [ordered]@{schema=1;scope='AgentBroker reviewed project-source closure (conservative CSharp set)';settings=[ordered]@{configuration='Release';sdk=[string]$global.sdk.version;moduleTarget='net10.0-windows';workerTarget='net10.0-windows';workerRid='win-x64';workerSelfContained=$true;optimize=$true;importUserLocationsByWildcard=$false;deterministic=$true;publishTrimmed=$false;publishSingleFile=$false;moduleDebugType='none';moduleDebugSymbols=$false;moduleKitDebugType='embedded';workerGraphDebugType='None';workerGraphDebugSymbols=$false;enableSourceLink=$false;embedUntrackedSources=$false;embedAllSources=$false;includeGitSuffix=$false;pathMap='repository-root=/_/';lockedRestore=$true;restorePolicy='clear sources; nuget.org v3 only'};sources=$sources}
}
function Get-ABPIdentityBytes($Identity){[Text.UTF8Encoding]::new($false).GetBytes(($Identity|ConvertTo-Json -Depth 12 -Compress -EscapeHandling EscapeNonAscii))}
function Get-ABPOutputRoot {[IO.Path]::GetFullPath((Join-Path $script:ABPRepoRoot 'build/AgentBrokerReleaseProvenance'))}
function Get-ABPRestoreText {'<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'}
function Read-AgentBrokerReleaseManifest([string]$Path,[string]$Sha,[string]$Parent,[string]$Sdk){
 Assert-ABP ($Sha -cmatch '^[A-F0-9]{64}\z' -and $Parent -cmatch '^[a-f0-9]{40}\z') 'PROVENANCE_IDENTITY_REQUIRED'
 $full=[IO.Path]::GetFullPath($Path);Assert-ABP ($full -ceq (Join-Path (Get-ABPOutputRoot) ($Sha+'.json'))) 'PROVENANCE_FIXED_MANIFEST_REQUIRED'
 Assert-ABPPlain $full;Assert-ABP ([IO.File]::Exists($full) -and (Get-Item -LiteralPath $full).Length -le 2MB) 'PROVENANCE_MANIFEST_REQUIRED'
 $record=ConvertFrom-Json ([IO.File]::ReadAllText($full));$identity=Get-ABPIdentity;$current=Get-ABPHash (Get-ABPIdentityBytes $identity)
 Assert-ABP ($record.schema -eq 1 -and $record.sourceContentSha256 -ceq $Sha -and $current -ceq $Sha -and $record.parentCommit -ceq $Parent -and (Get-ABPHash (Get-ABPIdentityBytes $record.identity)) -ceq $Sha) 'PROVENANCE_SOURCE_CHANGED'
 if($Sdk){Assert-ABP ($Sdk -ceq $identity.settings.sdk) 'PROVENANCE_ACTUAL_SDK_MISMATCH'}
 return $record
}
function Get-AgentBrokerReleaseProperties([string]$Path,[string]$Sha,[string]$Parent){
 $null=Read-AgentBrokerReleaseManifest $Path $Sha $Parent ''
 $config=Join-Path (Get-ABPOutputRoot) 'NuGet.Config'
 Assert-ABPPlain $config;Assert-ABP ([IO.File]::Exists($config) -and [IO.File]::ReadAllText($config) -ceq (Get-ABPRestoreText)) 'PROVENANCE_RESTORE_POLICY_CHANGED'
 return @('-noAutoResponse','-p:AgentBrokerReleaseProvenance=true','-p:SkipAgentBrokerWorkerPublish=false','-p:DesignTimeBuild=false','-p:BuildProjectReferences=true','-p:Optimize=true','-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false','-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false','-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false','-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false',('-p:AgentBrokerReleaseManifest='+[IO.Path]::GetFullPath($Path)),('-p:AgentBrokerSourceContentSha256='+$Sha),('-p:AgentBrokerSourceParentCommit='+$Parent),'-p:EnableSourceLink=false','-p:EmbedUntrackedSources=false','-p:EmbedAllSources=false','-p:IncludeSourceRevisionInInformationalVersion=false','-p:Deterministic=true','-p:PublishTrimmed=false','-p:PublishSingleFile=false',('-p:PathMap='+$script:ABPRepoRoot+'=/_/'),'-p:RestoreLockedMode=true',('-p:RestoreConfigFile='+$config))
}
. (Join-Path $PSScriptRoot 'AgentBrokerReleaseOutput.ps1')
if($Mode -eq 'Library'){return}
if($Mode -eq 'Prepare'){
 $identity=Get-ABPIdentity;$sha=Get-ABPHash (Get-ABPIdentityBytes $identity)
 $parent=(& git -c ('safe.directory='+$script:ABPRepoRoot) -C $script:ABPRepoRoot rev-parse HEAD);Assert-ABP ($LASTEXITCODE -eq 0 -and $parent -cmatch '^[a-f0-9]{40}\z') 'PROVENANCE_PARENT_UNAVAILABLE'
 $outputRoot=Get-ABPOutputRoot;Assert-ABPPlain $outputRoot;[void][IO.Directory]::CreateDirectory($outputRoot)
 $config=Join-Path $outputRoot 'NuGet.Config';$text=Get-ABPRestoreText
 if([IO.File]::Exists($config)){Assert-ABPPlain $config;Assert-ABP ([IO.File]::ReadAllText($config) -ceq $text) 'PROVENANCE_RESTORE_POLICY_CHANGED'}else{$stream=[IO.File]::Open($config,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes($text);$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}}
 $path=Join-Path $outputRoot ($sha+'.json');$record=[ordered]@{schema=1;sourceContentSha256=$sha;parentCommit=[string]$parent;identity=$identity}
 $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($record|ConvertTo-Json -Depth 14));$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
 $null=Read-AgentBrokerReleaseManifest $path $sha $parent ''
 [pscustomobject]@{ManifestPath=$path;SourceContentSha256=$sha;ParentCommit=$parent;SourceCount=$identity.sources.Count}
}elseif($Mode -eq 'Verify'){
 Assert-ABP (-not [string]::IsNullOrWhiteSpace($ActualSdk)) 'PROVENANCE_ACTUAL_SDK_REQUIRED'
 $null=Read-AgentBrokerReleaseManifest $ManifestPath $ExpectedSha256 $ExpectedParent $ActualSdk
 Write-Output ('AgentBroker source content verified: '+$ExpectedSha256)
}elseif($Mode -eq 'VerifyOutput'){
 $record=Test-AgentBrokerReleaseOutput $OutputReceipt $OutputReceiptSha256 (Get-ABPCanonicalPayloadRoot)
 [pscustomobject]@{OutputVerified=$true;SourceContentSha256=$record.sourceContentSha256;InventorySha256=$record.inventorySha256;FileCount=$record.fileCount}
}else{
 $properties=Get-AgentBrokerReleaseProperties $ManifestPath $ExpectedSha256 $ExpectedParent;$failure=$null
 try{& dotnet build (Join-Path $script:ABPRepoRoot $script:ABPProjects[0]) -c Release --no-incremental @properties;Assert-ABP ($LASTEXITCODE -eq 0) 'PROVENANCE_MODULE_BUILD_FAILED'}catch{$failure=$_}
 try{$null=Read-AgentBrokerReleaseManifest $ManifestPath $ExpectedSha256 $ExpectedParent ''}catch{if($null -eq $failure){throw};Write-Error 'PROVENANCE_POSTBUILD_SOURCE_CHANGED' -ErrorAction Continue}
 if($null -ne $failure){throw $failure}
 New-AgentBrokerReleaseOutputReceipt $ManifestPath $ExpectedSha256 $ExpectedParent
}
