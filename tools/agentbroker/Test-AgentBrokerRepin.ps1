#requires -Version 5.1
<#
.SYNOPSIS
    Read-only re-pin assistant for the AgentBroker native adapters.
.DESCRIPTION
    Finds the installed Codex desktop package, Codex VS Code extension, VS Code router and Claude Code
    executables; verifies each one's expected-publisher Authenticode signature; runs the OFFLINE public-source
    checks that docs/agentbroker-client-compatibility.md names for that vendor; and drafts the exact replacement
    pin text, with versioned evidence, for the owner to review.

    It never applies a pin. It writes no file, setting, registry value or vendor state, opens no pipe, starts no
    client and reads no credential, session, transcript or configuration file. Every failed or unproven check is
    listed by name with its reason. Live acceptance checks are always UNPROVEN here: a signature or matching
    source markers alone cannot authorize a new build.

    Verdicts: ALREADY PINNED (the exact image matches a reviewed pin), DRAFT FOR REVIEW (every offline check
    passed; live acceptance still required), NOT READY (at least one offline check failed; no draft is offered).
.PARAMETER Vendor
    Codex, Claude or All (default).
.PARAMETER Json
    Emit one JSON document instead of text.
.PARAMETER SelfTest
    Run the fixture checks only. Reads and writes only its own temporary fixture directory.
.EXAMPLE
    pwsh -NoProfile -File tools/agentbroker/Test-AgentBrokerRepin.ps1
.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/agentbroker/Test-AgentBrokerRepin.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [ValidateSet('Codex', 'Claude', 'All')][string]$Vendor = 'All',
    [switch]$Json,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

$script:OpenAIPublisher = 'OpenAI OpCo, LLC'
$script:AnthropicPublisher = 'Anthropic, PBC'
$script:MicrosoftPublisher = 'Microsoft Corporation'
$script:CodexPublisherId = '2p2nqsd0c76g0'
# docs/agentbroker-client-compatibility.md, "Claude native inbox": native Windows inbox needs 2.1.248 or later.
$script:ClaudeVersionFloor = [Version]'2.1.248'
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

function Get-PinnedProfiles([string]$Root) {
    $desktopSource = [IO.File]::ReadAllText((Join-Path $Root 'tools\AgentBroker.CodexDesktopAdapter\WindowsDesktopConnection.cs'))
    $constants = @{}
    foreach ($match in [regex]::Matches($desktopSource, 'const string (\w+) = "([^"]*)";')) { $constants[$match.Groups[1].Value] = $match.Groups[2].Value }
    $desktop = New-Object 'Collections.Generic.List[object]'
    if ($constants.ContainsKey('PackageFullName')) {
        $desktop.Add([pscustomobject]@{ Package = $constants['PackageFullName']; Version = $constants['ApplicationVersion']
                ExecutableSha256 = $constants['ExecutableSha256']; ArchiveSha256 = $constants['ArchiveSha256']; Electron = $constants['ElectronVersion'] })
    }
    $tuple = 'new\("(OpenAI\.Codex_[^"]+)",\s*"([^"]+)",\s*"([0-9A-F]{64})",\s*"([0-9A-F]{64})",\s*"([^"]+)"\)'
    foreach ($match in [regex]::Matches($desktopSource, $tuple)) {
        $desktop.Add([pscustomobject]@{ Package = $match.Groups[1].Value; Version = $match.Groups[2].Value
                ExecutableSha256 = $match.Groups[3].Value; ArchiveSha256 = $match.Groups[4].Value; Electron = $match.Groups[5].Value })
    }
    $extensionSource = [IO.File]::ReadAllText((Join-Path $Root 'tools\AgentBroker.CodexDesktopAdapter\VsCodeCompatibilityProfile.cs'))
    $extension = [pscustomobject]@{
        Version = [regex]::Match($extensionSource, 'const string Version = "([^"]+)";').Groups[1].Value
        NativeImageSha256 = [regex]::Match($extensionSource, 'NativeImageSha256 = "([0-9A-F]{64})"').Groups[1].Value
    }
    $routerSource = [IO.File]::ReadAllText((Join-Path $Root 'tools\AgentBroker.CodexDesktopAdapter\VsCodeRouterCompatibilityProfile.cs'))
    $routerConstants = @{}
    foreach ($match in [regex]::Matches($routerSource, 'const string (\w+) = "([^"]*)";')) { $routerConstants[$match.Groups[1].Value] = $match.Groups[2].Value }
    $router = @(
        [pscustomobject]@{ Version = $routerConstants['ProductVersion']; Sha256 = $routerConstants['ExecutableSha256'] },
        [pscustomobject]@{ Version = $routerConstants['AdditionalProductVersion']; Sha256 = $routerConstants['AdditionalExecutableSha256'] }) |
        Where-Object { $_.Version -and $_.Sha256 }
    $claudeSource = [IO.File]::ReadAllText((Join-Path $Root 'tools\AgentBroker.ClaudeAdapter\NativePeerModels.cs'))
    $claude = @(foreach ($match in [regex]::Matches($claudeSource, 'new\("(\d+\.\d+\.\d+)",\s*"([0-9A-F]{64})"\)')) {
            [pscustomobject]@{ Version = $match.Groups[1].Value; Sha256 = $match.Groups[2].Value } })
    [pscustomobject]@{ Desktop = $desktop.ToArray(); Extension = $extension; Router = @($router); Claude = $claude }
}

function New-Result([string]$Kind, [string]$Name, [string]$Version, [string]$Image) {
    [pscustomobject]@{ Kind = $Kind; Name = $Name; Version = $Version; Image = $Image; Sha256 = $null
        Pinned = $false; Checks = New-Object 'Collections.Generic.List[object]'; DraftPin = $null; Verdict = $null }
}

function Complete-Result($Result, [scriptblock]$Draft) {
    foreach ($live in $script:LiveChecks) {
        $Result.Checks.Add((New-Check $live 'UNPROVEN' 'live acceptance required (compatibility doc, "Required live acceptance"); never inferred offline'))
    }
    $failed = @($Result.Checks | Where-Object { $_.Status -eq 'FAIL' }).Count
    if ($Result.Pinned) { $Result.Verdict = 'ALREADY PINNED' }
    elseif ($failed -gt 0) { $Result.Verdict = 'NOT READY' }
    else { $Result.Verdict = 'DRAFT FOR REVIEW'; $Result.DraftPin = & $Draft }
    return $Result
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
    $result = New-Result 'claude' 'Claude Code' $version $Image
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
    $result.Checks.Add((New-Check 'source.claude.peer-token-classification' 'UNPROVEN' 'peer-token admission (fps/E, selfSent, mode parity) needs a person to re-read the new build; markers only show the strings remain'))
    $kind = 'Standalone'
    if ($Image -match '\\anthropic\.claude-code-[^\\]+\\') { $kind = 'VsCode' }
    $draft = { 'public static readonly EngineProfile ' + $kind + ($version -replace '\.', '') + ' = new("' + $version + '", "' + $result.Sha256 + '");' }
    return Complete-Result $result $draft
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
    $result.Checks.Add((New-Check 'source.desktop.idle-guard-order' 'UNPROVEN' 'the two pre-injection idle checks and their order need a person to re-read the owner manager'))
    $draft = { 'new("' + $name + '", "' + $Package.Version + '", "' + $result.Sha256 + '", "' + $archiveSha + '", "' + $electron + '"),' }
    return Complete-Result $result $draft
}

function Measure-CodexExtension([string]$Directory, $Pins, $Providers) {
    $leaf = Split-Path $Directory -Leaf
    $version = $leaf -replace '^openai\.chatgpt-', '' -replace '-win32-x64$', ''
    $engine = Join-Path $Directory 'bin\windows-x86_64\codex.exe'
    $result = New-Result 'codex-vscode' 'Codex VS Code extension' $version $engine
    $result.Checks.Add((Test-Publisher (& $Providers.Signature $engine) $script:OpenAIPublisher 'publisher.engine-authenticode'))
    $result.Sha256 = & $Providers.Hash $engine
    $result.Pinned = $Pins.Extension.Version -eq $version -and $Pins.Extension.NativeImageSha256 -eq $result.Sha256
    $bundles = New-Object 'Collections.Generic.List[string]'
    $extensionJs = Join-Path $Directory 'out\extension.js'
    if (Test-Path -LiteralPath $extensionJs -PathType Leaf) { $bundles.Add($extensionJs) }
    $assets = Join-Path $Directory 'webview\assets'
    if (Test-Path -LiteralPath $assets -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $assets -File | Where-Object { $_.Name -match '^(app-main|app-initial|src)-[0-9a-f]+\.js$' } | Select-Object -First 32)) {
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
        if ($any) { $holders.Add($bundle) }
    }
    foreach ($key in $script:CodexExtensionMarkers.Keys) {
        if ($found[$key]) { $result.Checks.Add((New-Check $key 'PASS' ("a public bundle contains '" + $script:CodexExtensionMarkers[$key] + "'"))) }
        else { $result.Checks.Add((New-Check $key 'FAIL' ("no public bundle contains '" + $script:CodexExtensionMarkers[$key] + "'"))) }
    }
    $result.Checks.Add((New-Check 'source.vscode.manifest-functional-hash' 'UNPROVEN' 'compute with VsCodeManifestProfile.ComputeSha256 (canonical JSON); this helper does not reimplement it'))
    $result.Checks.Add((New-Check 'source.vscode.renderer-bridge-role' 'UNPROVEN' 'which bundle owns the renderer bridge and owner manager needs a person to re-read the new build'))
    $draft = {
        $lines = New-Object 'Collections.Generic.List[string]'
        $lines.Add('public const string Version = "' + $version + '";')
        $lines.Add('public const string DirectoryName = "' + $leaf + '";')
        $lines.Add('internal const string NativeImageSha256 = "' + $result.Sha256 + '";')
        foreach ($holder in $holders) {
            $relative = $holder.Substring($Directory.Length).TrimStart('\', '/').Replace('\', '/')
            $lines.Add('["' + $relative + '"] = "' + (& $Providers.Hash $holder) + '",')
        }
        $lines -join [Environment]::NewLine
    }
    return Complete-Result $result $draft
}

function Measure-CodeRouter([string]$Image, $Pins, $Providers) {
    $version = & $Providers.Version $Image
    $result = New-Result 'vscode-router' 'VS Code router' $version $Image
    $result.Checks.Add((Test-Publisher (& $Providers.Signature $Image) $script:MicrosoftPublisher 'publisher.authenticode'))
    $result.Sha256 = & $Providers.Hash $Image
    $result.Pinned = @($Pins.Router | Where-Object { $_.Version -eq $version -and $_.Sha256 -eq $result.Sha256 }).Count -gt 0
    $draft = { '(hash == "' + $result.Sha256 + '" && version == "' + $version + '")' }
    return Complete-Result $result $draft
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
            $extensions = Join-Path $userHome '.vscode\extensions'
            if (Test-Path -LiteralPath $extensions) {
                @(Get-ChildItem -LiteralPath $extensions -Directory | Where-Object { $_.Name -match '^anthropic\.claude-code-\d+\.\d+\.\d+-win32-x64$' } |
                    ForEach-Object { Join-Path $_.FullName 'resources\native-binary\claude.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
            }
        }
    }
}

function Write-Report($Results) {
    foreach ($result in $Results) {
        Write-Output ''
        Write-Output ('== ' + $result.Name + ' ' + $result.Version + '  [' + $result.Verdict + ']')
        Write-Output ('   image  ' + $result.Image)
        Write-Output ('   sha256 ' + $result.Sha256)
        foreach ($check in $result.Checks) { Write-Output ('   {0,-8} {1,-42} {2}' -f $check.Status, $check.Name, $check.Reason) }
        if ($result.DraftPin) {
            Write-Output '   draft pin for owner approval (NOT applied; live acceptance still required):'
            foreach ($line in ([string]$result.DraftPin -split "`r?`n")) { Write-Output ('     ' + $line) }
        }
    }
    $notReady = @($Results | Where-Object { $_.Verdict -eq 'NOT READY' }).Count
    Write-Output ''
    Write-Output ("Summary: {0} build(s); {1} already pinned, {2} draft(s) for review, {3} not ready. No pin was applied." -f @($Results).Count,
        @($Results | Where-Object { $_.Verdict -eq 'ALREADY PINNED' }).Count, @($Results | Where-Object { $_.Verdict -eq 'DRAFT FOR REVIEW' }).Count, $notReady)
}

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
        # Pin sources in the exact shapes the adapters use, so the parser is exercised against real formats.
        $sources = Join-Path $fixture 'repo'
        foreach ($dir in @('tools\AgentBroker.CodexDesktopAdapter', 'tools\AgentBroker.ClaudeAdapter')) { New-Item -ItemType Directory -Path (Join-Path $sources $dir) | Out-Null }
        $hexA = 'A' * 64; $hexB = 'B' * 64; $hexC = 'C' * 64; $hexD = 'D' * 64; $hexE = 'E' * 64; $hexF = 'F' * 64
        $desktopCs = "public const string PackageFullName = ""OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0"";`npublic const string ApplicationVersion = ""1.0.0.0"";`n" +
            "public const string ExecutableSha256 = ""$hexA"";`npublic const string ArchiveSha256 = ""$hexB"";`npublic const string ElectronVersion = ""1.2.3"";`n" +
            "new(""OpenAI.Codex_1.0.1.0_x64__2p2nqsd0c76g0"", ""1.0.1.0"",`n    ""$hexC"",`n    ""$hexD"", ""1.2.3"")`n"
        $files = @{
            'tools\AgentBroker.CodexDesktopAdapter\WindowsDesktopConnection.cs' = $desktopCs
            'tools\AgentBroker.CodexDesktopAdapter\VsCodeCompatibilityProfile.cs' = "public const string Version = ""9.0.1"";`ninternal const string NativeImageSha256 = ""$hexE"";`n"
            'tools\AgentBroker.CodexDesktopAdapter\VsCodeRouterCompatibilityProfile.cs' = "public const string ProductVersion = ""1.1.0"";`npublic const string ExecutableSha256 = ""$hexF"";`n"
            'tools\AgentBroker.ClaudeAdapter\NativePeerModels.cs' = "new(""2.1.300"",`n        ""$hexA"");`n"
        }
        foreach ($entry in $files.GetEnumerator()) { [IO.File]::WriteAllText((Join-Path $sources $entry.Key), $entry.Value) }
        $before = @{}
        foreach ($entry in $files.GetEnumerator()) { $before[$entry.Key] = (Get-FileHash -LiteralPath (Join-Path $sources $entry.Key)).Hash }

        $pins = Get-PinnedProfiles $sources
        Assert (@($pins.Desktop).Count -eq 2 -and $pins.Desktop[0].Version -eq '1.0.0.0' -and $pins.Desktop[1].ArchiveSha256 -eq $hexD) 'pin parser reads both desktop tuple shapes exactly'
        Assert ($pins.Extension.Version -eq '9.0.1' -and $pins.Extension.NativeImageSha256 -eq $hexE -and @($pins.Router).Count -eq 1 -and
            @($pins.Claude).Count -eq 1 -and $pins.Claude[0].Version -eq '2.1.300') 'pin parser reads extension, router and Claude pins exactly'

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
        Assert ($new.Verdict -eq 'DRAFT FOR REVIEW' -and $new.DraftPin -ceq ('public static readonly EngineProfile Standalone21310 = new("2.1.310", "' + $hexC + '");')) 'a signed compatible new Claude build gets an exact draft pin'
        $state.Version = '2.1.310.0'
        $vsCodeImage = Join-Path $fixture 'anthropic.claude-code-2.1.310-win32-x64\resources\native-binary\claude.exe'
        $fourPart = Measure-ClaudeBuild $vsCodeImage $pins $providers
        Assert ($fourPart.Version -eq '2.1.310' -and $fourPart.DraftPin -ceq ('public static readonly EngineProfile VsCode21310 = new("2.1.310", "' + $hexC + '");')) 'a four-part public version normalizes like EngineProfile and names the VS Code profile'
        $state.Version = '2.1.310'
        Assert (@($new.Checks | Where-Object { $_.Status -eq 'UNPROVEN' }).Count -eq $script:LiveChecks.Count + 1 -and
            @($new.Checks | Where-Object { $_.Name -eq 'live.busy-ordering' -and $_.Status -eq 'UNPROVEN' }).Count -eq 1) 'every live acceptance check stays UNPROVEN by name'

        $missing = @{}; foreach ($key in $all.Keys) { $missing[$key] = $true }; $missing['source.claude.cc-msg-pipe'] = $false
        $state.Markers = $missing
        $changed = Measure-ClaudeBuild $image $pins $providers
        Assert ($changed.Verdict -eq 'NOT READY' -and $null -eq $changed.DraftPin -and
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
        Assert ($pinned.Verdict -eq 'ALREADY PINNED' -and $null -eq $pinned.DraftPin) 'an exactly pinned Claude image needs no replacement pin'
        $state.Hash = $hexB
        $repacked = Measure-ClaudeBuild $image $pins $providers
        Assert ($repacked.Verdict -eq 'DRAFT FOR REVIEW' -and -not $repacked.Pinned) 'WITNESS: a same-version image with a different hash is not treated as pinned'
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
        Assert ($desktop.Verdict -eq 'DRAFT FOR REVIEW' -and $desktop.DraftPin -ceq ('new("OpenAI.Codex_26.2000.1.0_x64__2p2nqsd0c76g0", "26.2000.1.0", "' + $hexF + '", "' + $hexF + '", "156.0.1.2"),')) 'a signed protected compatible desktop package gets an exact draft tuple'
        $state.Protected = $false
        $unprotected = Measure-CodexDesktop $package $pins $providers
        Assert ($unprotected.Verdict -eq 'NOT READY' -and $null -eq $unprotected.DraftPin -and
            @($unprotected.Checks | Where-Object { $_.Name -like 'location.protected-*' -and $_.Status -eq 'FAIL' }).Count -eq 2) 'an unprotected package location fails by name and withholds the draft'
        $state.Protected = $true
        $state.ArchiveMarkers = $null
        $noNode = Measure-CodexDesktop $package $pins $providers
        Assert (@($noNode.Checks | Where-Object { $_.Name -eq 'source.desktop.fixed-pipe' -and $_.Status -eq 'UNPROVEN' }).Count -eq 1 -and
            $noNode.Verdict -eq 'DRAFT FOR REVIEW') 'an uninspected archive stays UNPROVEN, never PASS'
        $state.ArchiveMarkers = $desktopMarkers
        $foreign = Measure-CodexDesktop ([pscustomobject]@{ Version = '26.2000.1.0'; PublisherId = 'otherpublisher'; InstallLocation = (Join-Path $fixture 'OpenAI.Codex_26.2000.1.0_x64__otherpublisher') }) $pins $providers
        Assert ($foreign.Verdict -eq 'NOT READY' -and @($foreign.Checks | Where-Object { $_.Name -eq 'location.windowsapps-package' -and $_.Status -eq 'FAIL' }).Count -eq 1) 'a package outside the OpenAI WindowsApps family fails by name'

        # Codex VS Code extension with real bundle files holding the markers.
        $extension = Join-Path $fixture 'openai.chatgpt-26.2000.5-win32-x64'
        foreach ($dir in @('bin\windows-x86_64', 'out', 'webview\assets')) { New-Item -ItemType Directory -Path (Join-Path $extension $dir) | Out-Null }
        [IO.File]::WriteAllText((Join-Path $extension 'bin\windows-x86_64\codex.exe'), 'synthetic engine')
        [IO.File]::WriteAllText((Join-Path $extension 'out\extension.js'), 'const pipe="codex-ipc"; getThreadRole(); "thread-owner-discovery";')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\app-main-0a1b.js'), '"thread-follower-start-turn";supportsUntrustedAppInput')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\src-0c0d.js'), 'inheritThreadSettings;"App context must wait until the current turn finishes"')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\code-review-0e0f.js'), 'supportsUntrustedAppInput')
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\app-initial-ffff.js'), 'unrelated')
        $realMarkers = { param($Path, $Markers) Find-FileMarkers $Path $Markers }
        $extensionProviders = $providers.Clone(); $extensionProviders.FileMarkers = $realMarkers
        $extensionProviders.Hash = { param($Path) Get-DefaultHash $Path }
        $code = Measure-CodexExtension $extension $pins $extensionProviders
        $draft = [string]$code.DraftPin
        Assert ($code.Verdict -eq 'DRAFT FOR REVIEW' -and $draft.Contains('public const string Version = "26.2000.5";') -and
            $draft.Contains('["out/extension.js"] = "' + (Get-DefaultHash (Join-Path $extension 'out\extension.js')) + '",') -and
            $draft.Contains('["webview/assets/app-main-0a1b.js"]') -and $draft.Contains('["webview/assets/src-0c0d.js"]') -and
            -not $draft.Contains('app-initial-ffff') -and -not $draft.Contains('code-review')) 'extension draft pins only the hashed owner-manager bundles that hold the reviewed markers'
        Assert (@($code.Checks | Where-Object { $_.Name -eq 'source.vscode.manifest-functional-hash' -and $_.Status -eq 'UNPROVEN' }).Count -eq 1) 'the manifest functional hash is listed as UNPROVEN, not guessed'
        [IO.File]::WriteAllText((Join-Path $extension 'webview\assets\app-main-0a1b.js'), '"thread-follower-start-turn"')
        $drifted = Measure-CodexExtension $extension $pins $extensionProviders
        Assert ($drifted.Verdict -eq 'NOT READY' -and @($drifted.Checks | Where-Object { $_.Status -eq 'FAIL' } | ForEach-Object { $_.Name }) -contains 'source.vscode.untrusted-input') 'an extension that drops a reviewed marker fails that check by name'

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
        Assert ($unchanged -and @(Get-ChildItem -LiteralPath $sources -Recurse -File).Count -eq $files.Count) 'the helper never writes or adds a pin source file'
        Write-Output ("PASS: {0} re-pin helper fixture checks" -f $script:selfTestChecks)
    }
    finally { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($SelfTest) {
    try { Invoke-SelfTest; exit 0 }
    catch { Write-Output $_.Exception.Message; Write-Output 'RESULT=FAIL'; exit 1 }
}

$pins = Get-PinnedProfiles $script:RepoRoot
$results = Get-InstalledResults $Vendor $pins (Get-DefaultProviders)
if ($Json) {
    $results | ForEach-Object { $_.Checks = @($_.Checks); $_ } | ConvertTo-Json -Depth 6
}
else { Write-Report $results }
if (@($results | Where-Object { $_.Verdict -eq 'NOT READY' }).Count -gt 0) { exit 1 }
exit 0
