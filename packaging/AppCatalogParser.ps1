<#
.SYNOPSIS
    Run THE APP'S OWN catalog parser over a catalog file, and say what it refused.

.DESCRIPTION
    Added 2026-10-06 (feature/catalog-insight, BUG-014). The agentflow 1.5.0 publish wrote a 1049-character
    module description into catalog.json; RemoteCatalog.Parse refuses one over 1024, and it refused the WHOLE
    catalog for it, so every installed app lost module updates, pack downloads and the companion gallery at
    once. Every check that ran on that commit was green, because none of them ran the app's parser: the gate
    hashed the assets, the freshness check compared versions, and CI did both.

    So this runs the parser itself rather than a copy of its rules: the built DesktopAICompanion.exe with
    --catalog-parse-file=<path>, which reads the file through RemoteCatalogClient.ParseBytes, the door every
    fetched catalog takes (the download cap, strict UTF-8, Parse) and the launch check's refusing read of the
    app block, and exits 1 naming every entry it refuses and the rule each one broke. A PowerShell mirror of
    those rules was the alternative and is refused: two implementations of one rule set is how a catalog and
    its checker end up both confident and both wrong, the reason ContentCatalogAssets.ps1 exists.

    THE PRICE is a built app. A stale one would judge the catalog by an older parser, so the build output is
    compared with the parser's sources first: callers that may build (the publish scripts) pass -BuildIfStale
    and the host is rebuilt; the verifier (the gate and CI, which have just built it) refuses instead.

    ONE RULE THIS CANNOT SEE. The parser here is the one in this tree, and catalog.json on master is read by
    every RELEASED app too. A bound raised in RemoteCatalog.cs passes here at once and is still refused by
    every app released before it, which for v1.3.0 and earlier means the whole catalog. Loosening a bound
    therefore waits for the release that carries it to be the oldest one in use; the register's entry under
    feature/catalog-insight says so.

    Callers: Test-ContentCatalogIntegrity.ps1 (the gate and CI), New-ContentCatalog.ps1 (before it writes),
    New-ModulePublish.ps1 (before it builds, zips or commits anything). 5.1-compatible, because the verifier
    runs under both powershell.exe and pwsh.
#>

# The files whose code decides what the parser accepts. A build output older than any of them was compiled
# from older rules. Program.cs carries the flag itself.
$AppCatalogParserSources = @(
    'src\dotNet\RemoteCatalog.cs',
    'src\dotNet\SecureDownload.cs',
    'src\Portable\JsonRead.cs',
    'src\dotNet\Ai\FortunePackLoadPolicy.cs',
    'src\dotNet\CompanionXmlValidator.cs',
    'src\dotNet\Program.cs',
    'src\DesktopAICompanion.Contracts\PluginApi.cs'
)

function Get-AppCatalogParserExecutable([string]$RepoRoot) {
    return (Join-Path $RepoRoot 'build\DesktopAICompanionPortable\bin\Release\x64\DesktopAICompanion.exe')
}

# Why the built app cannot stand for this tree's parser, or $null when it can. The managed DLL beside the
# exe is what holds the code (the exe is the .NET apphost), so its timestamp is the one compared.
function Get-AppCatalogParserStaleness([string]$RepoRoot, [string]$ExecutablePath) {
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        return "the app is not built ($ExecutablePath is missing)"
    }
    $code = [IO.Path]::ChangeExtension($ExecutablePath, '.dll')
    if (-not (Test-Path -LiteralPath $code -PathType Leaf)) {
        return "the app's code is missing beside it ($code)"
    }
    $builtUtc = (Get-Item -LiteralPath $code).LastWriteTimeUtc
    foreach ($relative in $AppCatalogParserSources) {
        $source = Join-Path $RepoRoot $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { return "$relative is missing" }
        if ((Get-Item -LiteralPath $source).LastWriteTimeUtc -gt $builtUtc) {
            return "$relative has changed since the app was built ($code)"
        }
    }
    return $null
}

<#
    Returns @{ Passed; ExitCode; Lines } for one catalog file. Throws when the parser could not be run or gave
    no verdict, which is never a pass: a check that could not run must not read as one that found nothing.
#>
function Invoke-AppCatalogParse {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$CatalogPath,
        [string]$ExecutablePath,
        [switch]$BuildIfStale,
        [int]$TimeoutSeconds = 120
    )
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { $ExecutablePath = Get-AppCatalogParserExecutable $RepoRoot }
    $stale = Get-AppCatalogParserStaleness $RepoRoot $ExecutablePath
    if ($stale -and $BuildIfStale) {
        Write-Host "  building the app, because $stale (the catalog is judged by its parser)" -ForegroundColor DarkGray
        $hostProject = Join-Path $RepoRoot 'src\DesktopAICompanion_Portable.csproj'
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { $buildOutput = @(& dotnet build $hostProject -c Release --nologo -v:minimal 2>&1) }
        finally { $ErrorActionPreference = $previous }
        if ($LASTEXITCODE -ne 0) {
            $buildOutput | Select-Object -Last 15 | ForEach-Object { Write-Host ('    ' + $_) -ForegroundColor Red }
            throw "The app did not build (exit $LASTEXITCODE), so its catalog parser cannot judge $CatalogPath."
        }
        $stale = Get-AppCatalogParserStaleness $RepoRoot $ExecutablePath
    }
    if ($stale) {
        throw ("The app's own catalog parser cannot be run: $stale. Build it first (.\build.ps1 -Release), so " +
               "the catalog is judged by the parser in this tree rather than by an older one.")
    }

    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('dp-catparse-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $log = Join-Path $scratch 'stdout.txt'
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    try {
        # The child's marker (dp-catalog-parse.txt) lands in this private TEMP, not the shared one (F418).
        $env:TEMP = $scratch
        $env:TMP = $scratch
        # A GUI-subsystem exe does not block PowerShell, so the wait is explicit and bounded.
        $process = Start-Process -FilePath $ExecutablePath -ArgumentList ('"--catalog-parse-file=' + $CatalogPath + '"') `
            -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        $null = $process.Handle
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            throw "The app's catalog parser did not finish within $TimeoutSeconds s on $CatalogPath (killed)."
        }
        $lines = @()
        if (Test-Path -LiteralPath $log) { $lines = @(Get-Content -LiteralPath $log | ForEach-Object { "$_" } | Where-Object { $_ }) }
        $verdict = @($lines | Where-Object { $_ -like 'catalog_parse=*' })
        # THE CHANNELS MUST AGREE: exit 0 with a PASS line, or exit 1 with a FAIL line. Anything else is no
        # verdict (a crash, a flag the exe does not know, which returns to the app's normal start), never a pass.
        $passed = ($process.ExitCode -eq 0 -and $verdict.Count -eq 1 -and $verdict[0] -like 'catalog_parse=PASS*')
        $failed = ($process.ExitCode -eq 1 -and $verdict.Count -eq 1 -and $verdict[0] -like 'catalog_parse=FAIL*')
        if (-not $passed -and -not $failed) {
            throw ("The app's catalog parser gave no verdict on $CatalogPath (exit $($process.ExitCode)); it printed: " +
                   $(if ($lines.Count -gt 0) { $lines -join ' | ' } else { '(nothing)' }))
        }
        return [pscustomobject]@{ Passed = $passed; ExitCode = $process.ExitCode; Lines = $lines }
    }
    finally {
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
        try { [IO.Directory]::Delete($scratch, $true) } catch { }
    }
}

<#
    The module entries a publish is about to write, judged BEFORE anything is built, zipped, committed or
    written (feature/catalog-insight). Each entry is the publisher's own metadata (id, name, desc, version,
    permissions, minHostVersion) in a probe catalog, with a placeholder payload: the URL the generator would
    write, a 64-zero hash and a size of one byte, which the parser accepts whatever the real zip turns out to be.
    So this refuses exactly what the metadata decides (a description over its bound, an empty permission list,
    an unsafe id, a version past 32 characters), and the generator's check of the finished catalog refuses the
    rest. The probe file is written by the caller's PowerShell; it is valid JSON under 5.1 and 7 alike.
#>
function Test-ModuleEntriesWithAppParser {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][object[]]$Entries,
        [string]$Branch = 'master',
        [switch]$BuildIfStale
    )
    $rawBase = "https://raw.githubusercontent.com/bigfnj/desktop-ai-companion/$Branch"
    $modules = @()
    foreach ($entry in $Entries) {
        $id = [string]$entry.id
        $floor = ''
        if ($entry.PSObject.Properties['minHostVersion']) { $floor = [string]$entry.minHostVersion }
        $modules += [ordered]@{
            id             = $id
            name           = [string]$entry.name
            desc           = [string]$entry.desc
            version        = [string]$entry.version
            url            = "$rawBase/modules-dist/$id.zip"
            sha256         = ('0' * 64)
            bytes          = 1
            permissions    = [string]$entry.permissions
            minHostVersion = $floor
        }
    }
    $probe = [ordered]@{ version = 1; modules = @($modules) }
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('dp-catprobe-' + [guid]::NewGuid().ToString('N').Substring(0, 12) + '.json')
    try {
        [IO.File]::WriteAllText($scratch, ($probe | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
        return (Invoke-AppCatalogParse -RepoRoot $RepoRoot -CatalogPath $scratch -BuildIfStale:$BuildIfStale)
    }
    finally { try { [IO.File]::Delete($scratch) } catch { } }
}
