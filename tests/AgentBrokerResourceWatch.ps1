#requires -Version 5.1
# Test-only rule for explicit resources copied into or compiled into the module payload.
# This is not an MSBuild evaluator: unsupported payload declarations fail by name.

function ConvertTo-AgentBrokerWatchArgument([string]$Value) {
    # Same Windows argument quoting as Invoke-IsolatedGate, including trailing backslashes.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-AgentBrokerWatchGit {
    param([string]$RepoRoot, [string[]]$Pathspecs)
    try {
        if ($Pathspecs.Count -eq 0) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: empty watch pathspecs' }
        $git = @(Get-Command git.exe -CommandType Application -ErrorAction Stop)[0].Source
        $arguments = @('--no-optional-locks', '-C', $RepoRoot, '-c', 'core.quotePath=false',
                       'ls-files', '--cached', '--others', '--exclude-standard', '-z', '--') + $Pathspecs
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $git
        $start.Arguments = (@($arguments | ForEach-Object { ConvertTo-AgentBrokerWatchArgument $_ }) -join ' ')
        $start.WorkingDirectory = $RepoRoot
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $start
        $started = $false
        $output = New-Object IO.MemoryStream
        $errorOutput = New-Object IO.MemoryStream
        try {
            $started = $process.Start()
            if (-not $started) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: Git did not start' }
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $streams = @($process.StandardOutput.BaseStream, $process.StandardError.BaseStream)
            $buffers = @((New-Object byte[] 4096), (New-Object byte[] 4096))
            $memory = @($output, $errorOutput)
            $limits = @(8MB, 1MB)
            $reads = @($streams[0].ReadAsync($buffers[0], 0, 4096), $streams[1].ReadAsync($buffers[1], 0, 4096))
            while ($null -ne $reads[0] -or $null -ne $reads[1] -or -not $process.HasExited) {
                if ($clock.ElapsedMilliseconds -ge 15000) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: Git capture timed out' }
                $progress = $false
                for ($i = 0; $i -lt 2; $i++) {
                    if ($null -eq $reads[$i] -or -not $reads[$i].IsCompleted) { continue }
                    $count = $reads[$i].GetAwaiter().GetResult()
                    if ($count -eq 0) { $reads[$i] = $null; continue }
                    if ($memory[$i].Length + $count -gt $limits[$i]) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: Git capture byte limit' }
                    $memory[$i].Write($buffers[$i], 0, $count)
                    $reads[$i] = $streams[$i].ReadAsync($buffers[$i], 0, 4096)
                    $progress = $true
                }
                if (-not $progress) { Start-Sleep -Milliseconds 5 }
            }
            if ($process.ExitCode -ne 0) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: Git failed' }
            # Do not accept a partial ignore/config evaluation, or print private paths from stderr.
            if ($errorOutput.Length -ne 0) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: Git reported stderr' }
            return [pscustomobject]@{ Stdout = $output.ToArray(); NativeExit = $process.ExitCode }
        }
        finally {
            try {
                if ($started -and -not $process.HasExited) {
                    $process.Kill()
                    if (-not $process.WaitForExit(2000)) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: owned Git child did not close' }
                }
            }
            finally {
                $process.Dispose()
                $output.Dispose()
                $errorOutput.Dispose()
            }
        }
    }
    catch {
        if ($_.Exception.Message.StartsWith('PAYLOAD_RESOURCE_WATCH_COVERAGE:', [StringComparison]::Ordinal)) { throw }
        throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: cannot capture Git pathspec result'
    }
}
function Get-AgentBrokerPayloadResourceInputs {
    param([string]$RepoRoot, [string]$ProjectPath)
    try {
        $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
        $project = [IO.Path]::GetFullPath($ProjectPath)
        if (-not $project.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: project outside repository'
        }
        if (-not [IO.File]::Exists($project) -or (Get-Item -LiteralPath $project).Length -gt 1MB) {
            throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: project missing or oversized'
        }
        $settings = New-Object Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($project, $settings)
        try { $document = New-Object Xml.XmlDocument; $document.Load($reader) }
        finally { $reader.Dispose() }
        $selected = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($node in $document.SelectNodes('//*[local-name()="EmbeddedResource" or local-name()="None" or local-name()="Content"]')) {
            $copies = @($node.SelectNodes('*[local-name()="CopyToOutputDirectory"]'))
            if ($copies.Count -gt 1 -or ($copies.Count -eq 1 -and $node.HasAttribute('CopyToOutputDirectory'))) {
                throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: duplicate copy metadata'
            }
            $copy = [string]$node.GetAttribute('CopyToOutputDirectory')
            if ($copies.Count -eq 1) { $copy = [string]$copies[0].InnerText }
            # Pack-only/noncopying None and Content items are not module build-output inputs.
            if ($node.LocalName -ne 'EmbeddedResource' -and
                ([string]::IsNullOrWhiteSpace($copy) -or $copy.Trim() -ieq 'Never')) { continue }
            if ($node.LocalName -ne 'EmbeddedResource' -and $copy.Trim() -notin @('Always', 'PreserveNewest')) {
                throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: unsupported copy mode'
            }
            if ($null -ne $node.SelectSingleNode('ancestor-or-self::*[@Condition]') -or
                $null -ne $node.SelectSingleNode('.//*[@Condition]') -or
                $node.ParentNode.LocalName -ne 'ItemGroup' -or $node.ParentNode.ParentNode.LocalName -ne 'Project' -or
                -not $node.HasAttribute('Include') -or $node.HasAttribute('Update') -or $node.HasAttribute('Remove') -or $node.HasAttribute('Exclude')) {
                throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: unsupported conditional or nonliteral payload declaration'
            }
            $include = [string]$node.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include) -or $include -match '[*?;%]' -or $include -match '\$\(|@\(') {
                throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: unsupported payload Include'
            }
            $full = [IO.Path]::GetFullPath((Join-Path (Split-Path $project -Parent) $include))
            if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                -not [IO.File]::Exists($full)) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: payload source missing or outside repository' }
            for ($walk = $full; $walk.Length -ge $root.Length; $walk = Split-Path $walk -Parent) {
                if (([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: payload source reparse point'
                }
                if ($walk.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { break }
            }
            [void]$selected.Add($full.Substring($root.Length + 1).Replace('\', '/'))
        }
        if ($selected.Count -eq 0) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: no explicit payload resources or copies' }
        return @($selected | Sort-Object)
    }
    catch {
        if ($_.Exception.Message.StartsWith('PAYLOAD_RESOURCE_WATCH_COVERAGE:', [StringComparison]::Ordinal)) { throw }
        throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: cannot read explicit payload declarations'
    }
}
function Get-AgentBrokerResourceWatchCoverage {
    param([string]$RepoRoot, [string]$ProjectPath, [string[]]$Pathspecs)
    try {
        $inputs = @(Get-AgentBrokerPayloadResourceInputs -RepoRoot $RepoRoot -ProjectPath $ProjectPath)
        $captured = Invoke-AgentBrokerWatchGit -RepoRoot $RepoRoot -Pathspecs $Pathspecs
        $bytes = [byte[]]$captured.Stdout
        if ($bytes.Length -gt 0 -and $bytes[0] -eq 0) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: empty Git path record' }
        if ($bytes.Length -gt 0 -and $bytes[$bytes.Length - 1] -ne 0) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: unterminated Git path' }
        $utf8 = New-Object Text.UTF8Encoding($false, $true)
        $text = $utf8.GetString($bytes)
        if ($text.Contains(([string][char]0) + [char]0)) { throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: empty Git path record' }
        $matched = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($name in $text.Split([char]0)) { if ($name.Length -gt 0) { [void]$matched.Add($name) } }
        $missing = @($inputs | Where-Object { -not $matched.Contains($_) })
        return [pscustomobject]@{ Covered = $missing.Count -eq 0; Inputs = $inputs; Missing = $missing; NativeExit = $captured.NativeExit }
    }
    catch {
        if ($_.Exception.Message.StartsWith('PAYLOAD_RESOURCE_WATCH_COVERAGE:', [StringComparison]::Ordinal)) { throw }
        throw 'PAYLOAD_RESOURCE_WATCH_COVERAGE: cannot decode or evaluate resource watch coverage'
    }
}
