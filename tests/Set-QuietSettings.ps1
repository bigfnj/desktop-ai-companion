#requires -Version 5.1
#
# Seeds a FRESH data root with tests\quiet-settings.json as its settings.json, so a soak, smoke or probe that launches
# the real app never reaches the network on its own (N-shimeji-1106-17). A fresh data root has no settings.json, the
# app writes the fresh-install defaults, and those turn the three update checks ON (module, app and companion), each
# armed on every launch (StartUp's constructor), so every driver run made network requests nothing in the test asked
# for.
#
# The seed carries the three checks and the schema version and NOTHING else: every key it leaves out loads as a fresh
# install has it (AppSettingsDocument's initializers, N-bd-w4-options-ui-1-02), so a driver still tests the defaults.
# CoreTests' "Settings quiet test seed" holds the file to exactly that shape. Rejected: a test-mode switch in the app,
# which would be a code path that only tests take and the shipped app could be left in.
#
# Callers: tests\runtime-resource-soak.ps1, tests\tray-menu-smoke.ps1, tests\debug-menu-smoke.ps1,
# tests\tray-question-smoke.ps1 and tests\fullscreen-standdown-probe\standdown-ab.ps1, each BEFORE its launch
# (runtime-hardening-selftest.ps1 asserts the order).
#
#     & (Join-Path $PSScriptRoot 'Set-QuietSettings.ps1') -DataRoot $dataRoot
param(
    [Parameter(Mandatory = $true)] [string] $DataRoot
)
$ErrorActionPreference = 'Stop'

$fixture = Join-Path $PSScriptRoot 'quiet-settings.json'
$target = Join-Path $DataRoot 'settings.json'
# Refused, not overwritten: every caller makes a fresh root, so a settings.json already there means the caller is
# pointed at a root it did not create, and replacing that file would discard someone's settings.
if (Test-Path -LiteralPath $target) {
    throw "refusing to seed '$DataRoot': it already holds a settings.json"
}
Copy-Item -LiteralPath $fixture -Destination $target
