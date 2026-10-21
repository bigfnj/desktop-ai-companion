#requires -Version 7
# Restore tool discovery only for this invocation when the host inherited an old PATH.
# Example: pwsh -NoProfile -File tools/agentbroker/Invoke-DeveloperTool.ps1 git status --short
$ErrorActionPreference = 'Stop'
if ($args.Count -eq 0 -or $args[0] -notin @('git', 'dotnet', 'pwsh', 'python', 'gh')) {
    throw 'Supply git, dotnet, pwsh, python or gh followed by its arguments.'
}
$Tool = [string]$args[0]
$forwarded = @($args | Select-Object -Skip 1)
$paths = [Collections.Generic.List[string]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($value in @($env:PATH, [Environment]::GetEnvironmentVariable('PATH', 'Machine'),
        [Environment]::GetEnvironmentVariable('PATH', 'User'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd'), (Join-Path $env:USERPROFILE '.local\bin'))) {
    foreach ($entry in $value.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($seen.Add($entry)) { $paths.Add($entry) }
    }
}
$env:PATH = [string]::Join(';', $paths)
& $Tool @forwarded
exit $LASTEXITCODE
