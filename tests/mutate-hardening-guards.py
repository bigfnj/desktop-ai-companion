#!/usr/bin/env python3
"""Prove the three replacements for vacuous assertions can actually fail.

Replacing an assertion that cannot fail with another that cannot fail is the failure mode this
whole exercise is about, so each replacement gets its own mutation. Byte-exact restore.

    python tests/mutate-hardening-guards.py [--only=<substring>]

--only selects cases by name, case-insensitively, like the three sibling harnesses (N-petstudio-01:
every case runs the whole invariant script, so a lane could not run its own legs without the full
suite and ran them through a scratch driver instead). A selection that matches nothing is a refusal,
exit 2, never "0/0 fired." (RA-353). Run the file WHOLE before reporting, as the release checklist says.
"""

import argparse
import io
import os
import re
import subprocess
import sys

# Derived from this file's own location, never hard-coded. The absolute path that used to sit here
# meant every run from a git worktree mutated the MAIN checkout instead -- editing files a
# concurrent session was working in, and scoring the wrong tree's assertions. The three sibling
# mutation harnesses already do it this way.
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HARDENING = os.path.join(REPO, "tests", "runtime-hardening-selftest.ps1")
APPUPDATE = os.path.join(REPO, "src", "dotNet", "AppUpdateCheck.cs")
SMOKETEST = os.path.join(REPO, "SMOKETEST.md")
SELFTESTS = os.path.join(REPO, "tests", "Invoke-SelfTests.ps1")
PETSPANE = os.path.join(REPO, "src", "Portable", "Wpf", "CompanionsPaneControl.cs")
PETSPANE_MODULES = os.path.join(REPO, "src", "Portable", "Wpf", "ModulesPaneControl.cs")
FORMPET = os.path.join(REPO, "src", "dotNet", "FormCompanion.cs")
STARTUP = os.path.join(REPO, "src", "dotNet", "StartUp.cs")
BUILDPS1 = os.path.join(REPO, "build.ps1")
FORTUNE_PROVIDER = os.path.join(REPO, "modules", "Fortunes", "engine", "FortuneProvider.cs")
FORTUNES_MODULE = os.path.join(REPO, "modules", "Fortunes", "FortunesModule.cs")
REMINDER_MODULE = os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs")
FORTUNES_CSPROJ = os.path.join(REPO, "modules", "Fortunes", "Fortunes.csproj")
PETGRAPH_CS = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "PetGraph.cs")
LOADER_XML_CS = os.path.join(REPO, "src", "dotNet", "Xml.cs")
WEBLINKS = os.path.join(REPO, "src", "Portable", "WebLinks.cs")
RELEASE_YML = os.path.join(REPO, ".github", "workflows", "release.yml")
BUILD_YML = os.path.join(REPO, ".github", "workflows", "build.yml")
DEBUG_SMOKE = os.path.join(REPO, "tests", "debug-menu-smoke.ps1")
TRAY_SMOKE = os.path.join(REPO, "tests", "tray-menu-smoke.ps1")
PETSTUDIO_WINDOW = os.path.join(REPO, "modules", "PetStudio", "PetStudioWindow.cs")
OPTIONS_SHELL = os.path.join(REPO, "src", "Portable", "Wpf", "OptionsShell.cs")
OPTIONS_CONTROLLER = os.path.join(REPO, "src", "Portable", "Options", "OptionsController.cs")
MODULE_HOST_CS = os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleHost.cs")
COMPANION_HOST_CS = os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs")
PETSTUDIO_MODULE_CS = os.path.join(REPO, "modules", "PetStudio", "PetStudioModule.cs")
TIMELINE_PANE = os.path.join(REPO, "modules", "PetStudio", "TimelinePane.cs")
PROGRAM_CS = os.path.join(REPO, "tools", "ShimejiConvert", "Program.cs")
TEMPLATE_MODULE_CS = os.path.join(REPO, "templates", "desktop-ai-companion-module", "SampleModule.cs")
REMEMBRANCE_MODULE = os.path.join(REPO, "modules", "Remembrance", "RemembranceModule.cs")
AIBRAIN_PROBE = os.path.join(REPO, "modules", "AiBrain", "engine", "AiEngineProbe.cs")
AIBRAIN_FALLBACK = os.path.join(REPO, "modules", "AiBrain", "engine", "FallbackBackend.cs")
AGENTFLOW_MODULE = os.path.join(REPO, "modules", "AgentFlow", "AgentFlowModule.cs")
AGENTFLOW_PANE = os.path.join(REPO, "modules", "AgentFlow", "AgentFlowPane.cs")
AGENTFLOW_DETECTOR = os.path.join(REPO, "modules", "AgentFlow", "BlockedDetector.cs")
AGENTFLOW_SETUP = os.path.join(REPO, "modules", "AgentFlow", "VsCodeSetup.cs")
AUDIO_OUTPUT = os.path.join(REPO, "src", "dotNet", "AudioOutput.cs")
DIAGNOSTIC_LOG = os.path.join(REPO, "src", "dotNet", "DiagnosticLog.cs")
PROGRAM = os.path.join(REPO, "src", "dotNet", "Program.cs")
LOCALDATA_CS = os.path.join(REPO, "src", "Portable", "LocalData.cs")


def read(p):
    with io.open(p, "rb") as h:
        return h.read()


def write(p, data):
    with io.open(p, "wb") as h:
        h.write(data)


def windows_powershell_env():
    """A child environment whose PSModulePath is Windows PowerShell's own.

    Launched from pwsh 7, a powershell.exe child inherits pwsh's PSModulePath with the PowerShell 7
    module folders FIRST, and the 5.1 engine then autoloads Get-FileHash from the 7-only
    Microsoft.PowerShell.Utility manifest it cannot run: the invariant script died with
    "Get-FileHash is not recognized" and this harness refused its own baseline, while the same
    command passed from a Windows shell (N-fortunes-01, measured 2026-09-29 and again 2026-09-30:
    exit 1 under a pwsh parent, exit 0 under Git Bash). Which shell started the harness must not
    decide whether it can run, so the child keeps only the WindowsPowerShell entries it inherited
    and is guaranteed the two system defaults.
    """
    env = dict(os.environ)
    kept = [p for p in (env.get("PSModulePath") or "").split(os.pathsep)
            if p and "windowspowershell" in p.lower()]
    for default in (os.path.join(env.get("ProgramFiles", r"C:\Program Files"), "WindowsPowerShell", "Modules"),
                    os.path.join(env.get("SystemRoot", r"C:\Windows"), "System32", "WindowsPowerShell", "v1.0", "Modules")):
        if default.lower() not in [p.lower() for p in kept]:
            kept.append(default)
    env["PSModulePath"] = os.pathsep.join(kept)
    return env


def run():
    proc = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", HARDENING],
        capture_output=True, text=True, timeout=900, env=windows_powershell_env())
    return (proc.returncode, (proc.stdout or "") + (proc.stderr or ""))


CASES = (
    (
        "ORDER: stamp the result before fetching it",
        APPUPDATE,
        b"                string latest = await RemoteCatalogClient.FetchAppVersionAsync(token).ConfigureAwait(false);",
        b"                string latest = null; data.SetAppUpdateResult(DateTimeOffset.UtcNow, \"\");\n"
        b"                latest = await RemoteCatalogClient.FetchAppVersionAsync(token).ConfigureAwait(false);",
        "FETCHES before it stamps",
    ),
    (
        "the sidebar sample window is empty",
        HARDENING,
        b"    $sampleX1 = [int](355.0 / 370.0 * $dialogBmp.Width)",
        b"    $sampleX1 = $sampleX0",
        "sample window is non-empty",
    ),
    (
        "the .wxs set is empty",
        HARDENING,
        b"$wxsFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter '*.wxs' -File)",
        b"$wxsFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter '*.nosuchext' -File)",
        "at least one .wxs",
    ),
    # The doc-count invariants. Both directions matter: the count going stale in the DOC, and the
    # count changing in the SOURCE without the doc following. One mutation each, because a check
    # written against only one side would pass while the other drifted -- which is how
    # "84 source invariants" and "18 self-tests" both survived being wrong.
    # The signing guard, with the -not dropped: every PR build would then call signtool with an
    # EMPTY thumbprint. The old form of this invariant matched the WORDS and survived exactly this.
    ("the signing guard fires on an EMPTY thumbprint",
     BUILDPS1,
     b"if (-not [string]::IsNullOrWhiteSpace($SigningCertThumbprint)) {",
     b"if ([string]::IsNullOrWhiteSpace($SigningCertThumbprint)) {",
     "guards signing on a NON-EMPTY thumbprint"),

    # The sass bypass, restored: this is the code as it shipped, calling FormCompanion.Say directly.
    ("the poke sass goes straight to a bubble again",
     STARTUP,
     b"                    if (Host == null || !Host.RaiseSpeechRequest(subject, s)) subject.Say(s);",
     b"                    subject.Say(s);",
     "the poke sass is offered to the speech responders"),

    # The consent ORDER check, mutated the way it would actually regress: the download moves AHEAD
    # of the consult. "Prefetch the payload while the user reads the prompt" is a plausible
    # optimisation, and it is precisely what the order assertion exists to forbid, because bytes on
    # disk before consent is a notification rather than a prompt.
    #
    # The first attempt at this case SURVIVED, and the reason is worth keeping: it wrapped the
    # consult in `if (false)`, which leaves the text exactly where it was, so the ORDER was still
    # correct. That mutation was aimed at REACHABILITY, which no source-text check can see -- the
    # assertion was not vacuous, the mutation was testing something else. Presence is covered by
    # the separate "consults ModulePermissionConsent at all" assertion beside it.
    #
    # Re-pointed 2026-10-02 by lane feature/modules-update-all: the consent and the download moved into
    # ConfirmUpdatePermissions and StageUpdateAsync (which Update all takes too), so the regression is now a
    # stage call placed ahead of the consent call inside UpdateModuleAsync. Same intent, new bytes.
    (
        "the module payload is downloaded BEFORE the permission prompt",
        PETSPANE_MODULES,
        b"            ModulePermissions added;\n"
        b"            if (!ConfirmUpdatePermissions(module, installed, out added))",
        b"            string prefetched = await StageUpdateAsync(module, _netCts.Token);\n"
        b"            ModulePermissions added;\n"
        b"            if (!ConfirmUpdatePermissions(module, installed, out added))",
        "BEFORE the update is downloaded",
    ),
    (
        "DOC DRIFT: SMOKETEST.md quotes the wrong invariant count",
        SMOKETEST,
        re.compile(rb"(\d+) source invariants"),
        rb"7\1 source invariants",
        "source-invariant count matches this file",
    ),
    (
        "SOURCE DRIFT: a self-test flag is added and no doc follows",
        SELFTESTS,
        b"    '--security-selftest'                = $null",
        b"    '--security-selftest'                = $null\n"
        b"    '--invented-selftest'                = $null",
        "self-test count matches Invoke-SelfTests.ps1",
    ),
    # One of three call sites made synchronous again. The count-based form is what makes this
    # detectable: a pattern-ordered check would still find a Task.Run somewhere in the file.
    (
        "a Pets-pane staleness diff goes back on the UI thread",
        PETSPANE,
        b"                List<StalePet> restale = await Task\n"
        b"                    .Run(delegate { return DiffStale(cached); }).ConfigureAwait(true);\n"
        b"                if (!IsLoaded) return;\n"
        b"                RenderUpdates(restale);",
        b"                RenderUpdates(DiffStale(cached));",
        "runs off the UI thread",
    ),
    (
        "the update card re-hashes the pet the diff already classified",
        PETSPANE,
        b"            CompanionFreshness freshness = entry.Freshness;",
        b"            CompanionFreshness freshness = FreshnessOf(entry.Pet);",
        "instead of re-hashing",
    ),
    # The fullscreen stand-down, whose two properties pull in opposite directions: the SCAN must be
    # shared (it was per companion, so one desktop-wide answer cost 16 z-order walks per cycle at
    # MAX_SHEEPS) while the ENFORCEMENT must not be throttled at all. A change that helps one and
    # breaks the other looks like an optimisation and re-ships "anything visible over a fullscreen
    # game", which is on SMOKETEST.md's regression watchlist. One mutation per direction.
    (
        "each companion walks the z-order for itself again",
        FORMPET,
        b"                blocked = Program.Mainthread != null\n"
        b"                    ? Program.Mainthread.BlockedMonitorsForStandDown()\n"
        b"                    : null;",
        b"                blocked = FullscreenScan.BlockedMonitors(\n"
        b"                    Program.Mainthread != null ? Program.Mainthread.SheepHandles() : null);",
        "shared per cycle",
    ),
    (
        "the shared cache forgets which MONITOR was blocked",
        STARTUP,
        b"            _fullscreenBlocked = blocked;\n",
        b"",
        "per MONITOR",
    ),
    (
        "a per-companion time gate is re-added ahead of the scan",
        FORMPET,
        b"            bool[] blocked;\n            try\n",
        b"            if ((DateTime.UtcNow - _lastRelocateUtc).TotalMilliseconds < 300) return;\n"
        b"            bool[] blocked;\n            try\n",
        "not re-throttled per companion",
    ),
    # ---- 2026-09-29 audit campaign: each lane adds its cases directly under its own anchor so parallel
    # branches do not touch the same lines. Comments inside the literal are fine for Python.
    # ---- lane fix/gates ----

    # F287: the pack file cap drifts below the catalog entry cap (the exact 128-vs-512 regression the
    # --catalog-selftest check describes and cannot see). Repointed by lane fix/deadcode (F124): the module now
    # compiles the HOST's FortunePackLoadPolicy.cs, so the one cap lives there and the invariant compares it
    # with the catalog entry cap directly.
    (
        "the pack file cap drops to 128",
        os.path.join(REPO, "src", "dotNet", "Ai", "FortunePackLoadPolicy.cs"),
        b"        public const int MaximumFiles = 512;",
        b"        public const int MaximumFiles = 128;",
        "covers every pack the catalog may list",
    ),
    # F149: "nothing changed" compares what was read with itself again. Re-pointed by lane feature/fortunes-index
    # (fortunes 1.1.0): the 'Rebuild smart index' guard this case mutated went with the button, and the same
    # question now sits in the unchanged-inputs skip every folder rebuild asks.
    (
        "a rebuild's no-op check compares the provider's folder fingerprint with itself",
        FORTUNES_MODULE,
        b"                   string.Equals(FortuneProvider.CustomFolderSignatureNow(), provider.CustomSignature, StringComparison.Ordinal);",
        b"                   string.Equals(provider.CustomSignature, provider.CustomSignature, StringComparison.Ordinal);",
        "never the recorded one with itself",
    ),
    # F412: an unpinned redirect in the `Process.Start(new ProcessStartInfo { ... }))` shape, which the
    # regex slicer merged with whatever followed it up to the next `};`.
    (
        "an unpinned redirect appears in a }))-closed initialiser",
        WEBLINKS,
        b"                using (Process process = Process.Start(new ProcessStartInfo\n"
        b"                {\n"
        b"                    FileName = normalized,\n"
        b"                    UseShellExecute = true\n"
        b"                }))",
        b"                using (Process process = Process.Start(new ProcessStartInfo\n"
        b"                {\n"
        b"                    FileName = normalized,\n"
        b"                    RedirectStandardOutput = true,\n"
        b"                    UseShellExecute = true\n"
        b"                }))",
        "pins its own encoding, per SITE",
    ),
    # F412: the slicer's own witness -- a slice that runs past its closing brace lets the second
    # initialiser's pin leak into the first.
    (
        "the initialiser slicer runs each slice to the end of the file",
        HARDENING,
        b"        $slices += $Code.Substring($open, $pos - $open)",
        b"        $slices += $Code.Substring($open)",
        "does not leak into the unpinned first one",
    ),
    # F412: the synchronous-read scan reads raw text again, so a comment counts.
    (
        "the synchronous-read scan stops stripping comments",
        HARDENING,
        b"    $code = Remove-LineComments $Text\n"
        b"    return @([regex]::Matches($code,",
        b"    $code = $Text\n"
        b"    return @([regex]::Matches($code,",
        "sees code and ignores comments",
    ),


    # ---- lane fix/fortunes ----

    # F144: the smart picker's check-and-publish and its bump-and-clear each move to a DIFFERENT lock,
    # which is the unlocked race in a compiling disguise (a lock on another object orders nothing
    # against the other pair). One case per pair.
    (
        "the smart picker's publish takes a different lock from its supersession",
        FORTUNES_MODULE,
        b"                lock (_smartLock)\n"
        b"                {\n"
        b"                    // CHECK AND PUBLISH UNDER THE ONE LOCK",
        b"                lock (_stagedDisabled)\n"
        b"                {\n"
        b"                    // CHECK AND PUBLISH UNDER THE ONE LOCK",
        "sit inside one lock (_smartLock), check first",
    ),
    (
        "a rebuild bumps the smart generation under a different lock",
        FORTUNES_MODULE,
        b"            lock (_smartLock)\n"
        b"            {\n"
        b"                bool current =",
        b"            lock (_stagedDisabled)\n"
        b"            {\n"
        b"                bool current =",
        "clears the picker inside the same lock",
    ),


    # ---- lane fix/petstudio ----
    # (between two merged lanes' blocks on purpose: the block below fix/host is where the still-running
    # lanes add theirs, and an insertion next to it would conflict at the merge)

    # BUG-012 (F155): the analysis comes back onto the UI thread, and an overtaken result renders anyway.
    (
        "the studio analyzes on the UI thread again",
        PETSTUDIO_WINDOW,
        b"                report = await Task.Run(delegate { return PetAnalyzer.Analyze(xml); });",
        b"                report = PetAnalyzer.Analyze(xml); await Task.Yield();",
        "inside the awaited Task.Run",
    ),
    (
        "an overtaken analysis renders anyway",
        PETSTUDIO_WINDOW,
        b"            if (Volatile.Read(ref _analyzeGeneration) == generation && report != null) RenderAnalysis(report, statusPrefix);",
        b"            if (report != null) RenderAnalysis(report, statusPrefix);",
        "compared ahead of RenderAnalysis",
    ),
    # F165: the count Resync returns is discarded before the status is written.
    (
        "the dropped-step count is discarded before the status",
        PETSTUDIO_WINDOW,
        b"            SetStatus(statusPrefix + AnalysisStatus(report.IsValid, report.UnreachableAnimations.Count, droppedSteps));",
        b"            SetStatus(statusPrefix + AnalysisStatus(report.IsValid, report.UnreachableAnimations.Count, 0));",
        "reaches the one status the analysis writes",
    ),
    # F158: the zip import's flag is set late again (the 1.1.17 shape), or set a second time after the await.
    (
        "the zip import no longer sets _importing before extracting",
        PETSTUDIO_WINDOW,
        b"            _importing = true;\n"
        b"            try\n"
        b"            {\n"
        b"                RememberSkinDir(Path.GetDirectoryName(dlg.FileName));",
        b"            try\n"
        b"            {\n"
        b"                RememberSkinDir(Path.GetDirectoryName(dlg.FileName));",
        "sets _importing before its extraction await",
    ),
    (
        "the zip import sets _importing after the extraction await",
        PETSTUDIO_WINDOW,
        b"                await ImportSkinFromRootCoreAsync(destination);",
        b"                _importing = true;\n"
        b"                await ImportSkinFromRootCoreAsync(destination);",
        "sets _importing before its extraction await",
    ),
    # F163: one of the two document-swap guards is weakened to a condition that never holds there.
    (
        "Open is no longer refused during an import",
        PETSTUDIO_WINDOW,
        b"            if (_importing) { SetStatus(StillConverting); return; }\n"
        b"            BeginOrphanSweep();\n"
        b"            try\n"
        b"            {\n"
        b"                // Own the dialog here",
        b"            if (_importing && _pets == null) { SetStatus(StillConverting); return; }\n"
        b"            BeginOrphanSweep();\n"
        b"            try\n"
        b"            {\n"
        b"                // Own the dialog here",
        "Open and the installed picker are refused",
    ),
    (
        "the installed picker is no longer refused during an import",
        PETSTUDIO_WINDOW,
        b"            if (_importing)\n"
        b"            {\n"
        b"                // Refused like a second Import (F163), and the dropdown",
        b"            if (_importing && _pets == null)\n"
        b"            {\n"
        b"                // Refused like a second Import (F163), and the dropdown",
        "Open and the installed picker are refused",
    ),
    # F159: a recursive delete comes back onto the UI thread, in the click handler or in Closed.
    (
        "the previous skin's tree is deleted on the UI thread again",
        PETSTUDIO_WINDOW,
        b"                await Task.Run(delegate\n"
        b"                {\n"
        b"                    DeleteTree(previous);\n"
        b"                    Directory.CreateDirectory(destination);",
        b"                DeleteTree(previous);\n"
        b"                await Task.Run(delegate\n"
        b"                {\n"
        b"                    Directory.CreateDirectory(destination);",
        "deleted and swept on a pool thread",
    ),
    (
        "Closed deletes the extraction tree inline again",
        PETSTUDIO_WINDOW,
        b"            Task.Run(delegate { DeleteTree(path); });",
        b"            DeleteTree(path);",
        "deleted and swept on a pool thread",
    ),
    # F160: one load path stops sweeping.
    (
        "the folder import no longer starts the orphan sweep",
        PETSTUDIO_WINDOW,
        b"            RememberSkinDir(root);\n"
        b"            BeginOrphanSweep();\n"
        b"            await ImportSkinFromRootAsync(root);",
        b"            RememberSkinDir(root);\n"
        b"            await ImportSkinFromRootAsync(root);",
        "started at construction and on every load path",
    ),
    # F162: the core remembers the root it is handed again, which on the zip path is the extraction tree.
    (
        "the import core remembers the extraction tree as the skin folder again",
        PETSTUDIO_WINDOW,
        b"                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { SetStatus(\"No such folder.\"); return; }\n",
        b"                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { SetStatus(\"No such folder.\"); return; }\n"
        b"                RememberSkinDir(root);\n",
        "the import core remembers nothing",
    ),
    # F157: the installed picker leaves Save in whatever state history left it again. The first draft of
    # this case added a self-assignment ABOVE the comment and left the real line below it, and the harness
    # scored it SURVIVED, correctly: the assertion still saw `= true`. The line itself has to go.
    (
        "picking an installed companion no longer offers Save",
        PETSTUDIO_WINDOW,
        b"            _saveButton.IsEnabled = true;\n"
        b"            SetEditorText(xml);\n"
        b"            Analyze();\n",
        b"            SetEditorText(xml);\n"
        b"            Analyze();\n",
        "offers Save, as Open and an import do",
    ),


    # ---- lane fix/host ----

    # F278: the exclusion set drops the speech bubble again -- a TopMost window that decides a monitor as clear.
    (
        "the fullscreen exclusion set forgets the speech bubble",
        FORMPET,
        b"            if (bubble != null && !bubble.IsDisposed && bubble.IsHandleCreated) into.Add(bubble.Handle);\n",
        b"",
        "gathers the children recursively AND the speech bubble",
    ),
    # F270: the screen-position test comes back in place of the rect-shape test. Re-pointed 2026-09-30 by lane
    # burn/host-core: the inert `|| sTitle == "sheep"` clause beside the test is gone (RA-240).
    (
        "CheckTopWindow rejects title bars above screen y=0 again",
        FORMPET,
        b"                                titleBarInfo.rcTitleBar.Bottom >= titleBarInfo.rcTitleBar.Top)\n",
        b"                                titleBarInfo.rcTitleBar.Bottom >= 0)\n",
        "by its title bar having a shape",
    ),
    # F268: relocation stops un-suppressing the bubble, which is the code as it shipped.
    (
        "RelocateToDisplay leaves the bubble suppressed",
        FORMPET,
        b"            if (_speech != null && !_speech.IsDisposed) _speech.SetFullscreenSuppressed(false);\n"
        b"            DisplayIndex = target;",
        b"            DisplayIndex = target;",
        "both exits from the fullscreen stand-down un-suppress",
    ),
    # The stood-down bubble: the guard weakens to the hidden flag alone, so a stood-down but visible
    # (relocating) companion opens a bubble over the game again.
    (
        "SayWithDwell stops testing the fullscreen marker",
        FORMPET,
        b"            if (hwndFullscreenWindow != IntPtr.Zero || _fullscreenHidden)\n"
        b"            {\n"
        b"                _deferredSpeechText = text;",
        b"            if (_fullscreenHidden)\n"
        b"            {\n"
        b"                _deferredSpeechText = text;",
        "defers its line before the repeat guard",
    ),
    # F263: the degenerate-rect release loses its bNewAnimation, so one of the sites finishes the tick in
    # the old pose again. Re-pointed 2026-09-30 by lane burn/host-core: every release now also zeroes the
    # velocity on the next line (RA-236), so the pattern carries that line through.
    (
        "the degenerate-rect grip release skips bNewAnimation",
        FORMPET,
        b"                    ReleaseWindowGrip(true);\n"
        b"                    bNewAnimation = true;\n"
        b"                    x = 0; y = 0;\n"
        b"                }\n"
        b"                else if (windowGrip == WindowGrip.Bottom)",
        b"                    ReleaseWindowGrip(true);\n"
        b"                    x = 0; y = 0;\n"
        b"                }\n"
        b"                else if (windowGrip == WindowGrip.Bottom)",
        "every grip release in NextStep is followed by bNewAnimation",
    ),
    # F264: the fork's horizontal-velocity gate returns in front of FollowWindow.
    (
        "FollowWindow is gated on horizontal velocity again",
        FORMPET,
        b"                        if (FollowWindow())\n                        {",
        b"                        if (CurrentAnimation.Start.X.Value != 0 && FollowWindow())\n                        {",
        "rides a moving window",
    ),
    # F309/F331: the worker-thread post is removed; the inline raise still stands, which is exactly why the
    # invariant asserts the ORDER and not the presence of a raise.
    (
        "NoteFullscreenScan raises FullscreenChanged on whatever thread scanned",
        STARTUP,
        b"            if (uiContext != null && Thread.CurrentThread.ManagedThreadId != uiThreadId)\n"
        b"            {\n"
        b"                bool posted = any;\n"
        b"                uiContext.Post(delegate { if (!disposed && Host != null) Host.RaiseFullscreenChanged(posted); }, null);\n"
        b"                return;\n"
        b"            }\n",
        b"",
        "posts its FullscreenChanged raise to the UI thread",
    ),
    # F310: the module-facing getter stops stamping the attempt. Re-pointed 2026-09-30 by lane burn/host-core:
    # the inline comment was shortened to keep the F310 pin's regex window when the RA-242 thread test landed.
    (
        "IsFullscreenActive stops stamping the scan attempt",
        STARTUP,
        b"                _fullscreenScanUtc = DateTime.UtcNow;   // the ATTEMPT (F310)\n"
        b"                try { NoteFullscreenScan(FullscreenScan.BlockedMonitors(SheepHandles())); }",
        b"                try { NoteFullscreenScan(FullscreenScan.BlockedMonitors(SheepHandles())); }",
        "stamps the attempt before it walks the desktop",
    ),
    # F373: the interactive refresh attaches a second click handler again, so a stale cache opens two tabs.
    (
        "the update stamp's refresh attaches a second click handler",
        os.path.join(REPO, "src", "Portable", "Wpf", "OptionsWindow.cs"),
        b"                label.Text = text;\n"
        b"                MarkAsUpdateLink(label);\n",
        b"                label.Text = text;\n"
        b"                MarkAsUpdateLink(label);\n"
        b"                label.MouseLeftButtonUp += delegate { OpenReleasesPage(runningVersion); };\n",
        "attaches exactly one click handler",
    ),
    # F372: the constructor goes back to a fixed height beside the fit it no longer uses.
    (
        "the settings window opens at a fixed 820 again",
        os.path.join(REPO, "src", "Portable", "Wpf", "OptionsWindow.cs"),
        b"            Width = fitted.Width;\n            Height = fitted.Height;\n",
        b"            Width = fitted.Width;\n            Height = 820;\n",
        # Re-pointed 2026-09-30 by lane burn/host-shell: the invariant's label moved with RA-326 (the
        # window is fitted to the monitor it opens on, not the primary).
        "fitted to the monitor it opens on",
    ),
    # F369: one diagnostic-log setter drops its ok &= again.
    (
        "a Preferences setter discards its durable result again",
        os.path.join(REPO, "src", "Portable", "Wpf", "OptionsShell.cs"),
        b"                    ok &= data.SetDiagnosticLogMutedModules(CollectMutedModules(values));",
        b"                    data.SetDiagnosticLogMutedModules(CollectMutedModules(values));",
        "folds its durable result into ok",
    ),
    # F371: the reset touches the dormant theme again.
    (
        "reset to defaults resets the dormant theme again",
        os.path.join(REPO, "src", "Portable", "Wpf", "OptionsShell.cs"),
        b"                data.SetAudioDeviceId(def.AudioDeviceId);\n",
        b"                data.SetThemeMode(def.ThemeMode);\n                data.SetAudioDeviceId(def.AudioDeviceId);\n",
        "does not touch the dormant theme mode",
    ),
    # F363: the sound link shows the click instead of the store again.
    (
        "the sound link stops following the store",
        PETSPANE,
        b"                    enabled = stored;\n",
        b"",
        "shows what the store holds",
    ),
    # F364: the success-shaped line overwrites persistPending's failure line again.
    (
        "the size row's success line ignores the store's verdict again",
        PETSPANE,
        b"                if (!storeTookIt) return;\n",
        b"",
        "only when the store took it",
    ),
    # F363: the combo keeps showing the pin that did not take.
    (
        "a failed screen pin leaves the combo on the failed choice",
        PETSPANE,
        b"                    syncingBox = true;\n"
        b"                    try { box.SelectedIndex = storedChoice >= 0 ? storedChoice + 1 : 0; }\n"
        b"                    finally { syncingBox = false; }\n",
        b"",
        "puts the combo back on the stored screen",
    ),
    # F365: the built-in card bypasses the cache again; and the Icon goes back to the finalizer.
    (
        "the built-in card re-encodes its icon on every rebuild",
        PETSPANE,
        b"img = LoadAppIconCached();",
        b"img = LoadAppIcon();",
        "takes its icon from the cache",
    ),
    (
        "the app icon resource is left to the finalizer again",
        PETSPANE,
        b"                using (System.Drawing.Icon icon = DesktopAICompanion.Properties.Resources.icon)\n"
        b"                using (var bmp = icon.ToBitmap())\n",
        b"                using (var bmp = DesktopAICompanion.Properties.Resources.icon.ToBitmap())\n",
        "disposed after it is converted",
    ),
    # F367: a new module is extracted straight into modules/<id> again.
    (
        "a new module is unpacked straight into modules/<id> again",
        PETSPANE_MODULES,
        b"                using (var zipStream = new MemoryStream(bytes))\n"
        b"                    await ZipFile.ExtractToDirectoryAsync(zipStream, stagedHere, true, _netCts.Token);\n"
        b"                Directory.CreateDirectory(ModulesRoot());\n"
        b"                Directory.Move(stagedHere, installDir);\n"
        b"                stagedHere = null;   // it is the install folder now\n",
        b"                Directory.CreateDirectory(installDir);\n"
        b"                using (var zipStream = new MemoryStream(bytes))\n"
        b"                    await ZipFile.ExtractToDirectoryAsync(zipStream, installDir, true, _netCts.Token);\n",
        "never extracted in place",
    ),
    # F366: the update's cancel goes silent and strands its staging folder again.
    (
        "a cancelled module update goes silent and strands its staging folder",
        PETSPANE_MODULES,
        b"            catch (OperationCanceledException)\n"
        b"            {\n"
        b"                DiscardStaged(stagedHere);\n"
        b"                if (IsLoaded) _status.Text = \"Stopped updating \" + module.Name + \".\";\n"
        b"            }\n",
        b"            catch (OperationCanceledException) { }\n",
        "discards its staging folder in both catches",
    ),
    # F366: the companion download's cancel goes silent again.
    (
        "a cancelled companion download goes silent again",
        PETSPANE,
        b"            catch (OperationCanceledException)\n"
        b"            {\n"
        b"                if (IsLoaded) _status.Text = \"Stopped \" + (isUpdate ? \"updating \" : \"downloading \") + display + \".\";\n"
        b"            }\n",
        b"            catch (OperationCanceledException) { }\n",
        "a cancelled companion download says so",
    ),
    # F328: the bare Safe() wrapper comes back in the drop/poke chain, so a throwing responder is silent again.
    (
        "a throwing drop or poke responder is swallowed without a record again",
        os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs"),
        b"                try { handled = fn(handle); }\n"
        b"                catch (Exception ex)\n"
        b"                {\n"
        b"                    Log(r.ModuleId, \"responder threw and was treated as declined: \" + ex.GetType().Name + \": \" + ex.Message);\n"
        b"                }\n",
        b"                Safe(() => { handled = fn(handle); });\n",
        "logged under its module id and treated as declined",
    ),
    # F332: the speech chain walks the live list again.
    (
        "the speech chain walks the live responder list again",
        os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs"),
        b"                foreach (SpeechResponder r in _speechResponders.ToArray())",
        b"                foreach (SpeechResponder r in _speechResponders)",
        "walks a snapshot",
    ),
    # F333: only the targeted branch is marshalled again.
    (
        "a broadcast bubble re-shown from a worker draws inline again",
        os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs"),
        b"                    if (_host._ui != null && Thread.CurrentThread.ManagedThreadId != _host._uiThreadId)\n"
        b"                        _host._ui.Post(delegate { try { draw(); } catch (Exception ex) { _host.Log(null, \"bubble draw failed: \" + ex.Message); } }, null);\n"
        b"                    else if (_target != null && _target.InvokeRequired) _target.BeginInvoke(draw);\n",
        b"                    if (_target != null && _target.InvokeRequired) _target.BeginInvoke(draw);\n",
        "posted to the UI thread before the targeted-only marshal",
    ),
    # F334 (replacing F335's volatile case, whose field is gone): the browse verb fetches its own copy again.
    (
        "the browse verb bypasses RemoteCatalogClient's shared copy",
        os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs"),
        b"        public async System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind)\n"
        b"        {\n"
        b"            RemoteCatalog catalog = await RemoteCatalogClient\n"
        b"                .FetchSharedAsync(System.Threading.CancellationToken.None)\n",
        b"        public async System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind)\n"
        b"        {\n"
        b"            RemoteCatalog catalog = await RemoteCatalogClient\n"
        b"                .FetchAsync(System.Threading.CancellationToken.None)\n",
        "read RemoteCatalogClient's shared copy",
    ),
    # F329: the third foreground read is the answer again.
    (
        "the screen context reads the foreground process a third time again",
        os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs"),
        b"                ProcessName = !string.IsNullOrEmpty(foregroundProcess) ? foregroundProcess : ActiveWindow.ProcessName(),",
        b"                ProcessName = ActiveWindow.ProcessName(),",
        "from the window snapshot",
    ),
    # F327: the brain is left ON into the engine leg again.
    (
        "--aibrain-selftest leaves the brain enabled into the engine leg",
        os.path.join(REPO, "src", "dotNet", "Plugins", "AiBrainModuleSelfTest.cs"),
        b"                    ok &= Check(sb, \"brain toggled back OFF before the engine leg\", host.ClickTray(\"Enable AI\"));\n",
        b"",
        "presses Enable a second time",
    ),
    # F341: the runner stops naming the loader's reason.
    (
        "the convention runner refuses a module without saying why",
        os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleConventionSelfTest.cs"),
        b"                        foreach (ModuleLoadFailure f in loader.Failures)\n"
        b"                            sb.AppendLine(\"  loader: \" + f.Id + \" -- \" + f.Reason +\n"
        b"                                (f.Reason != null && f.Reason.Contains(\"NullReference\")\n"
        b"                                    ? \" (this convention host returns null from GetStorage/GetSettings, which the shipped host never does; see IHost.GetStorage)\"\n"
        b"                                    : \"\"));\n",
        b"",
        "reported with the loader's reason",
    ),
    # F353: the staging folder drops out of the factory reset.
    (
        "factory reset leaves the module staging folder alone again",
        os.path.join(REPO, "src", "dotNet", "FactoryReset.cs"),
        b"            ok &= Wipe(stagingRoot, \"staged module updates\", log);\n",
        b"",
        "wipes the module staging folder",
    ),
    # F352: the launch stops telling the loader which removals did not finish.
    (
        "the launch loads a folder whose removal could not finish",
        STARTUP,
        b"msg => AddDebugInfo(DEBUG_TYPE.info, \"[module] \" + msg), stillRemoving);",
        b"msg => AddDebugInfo(DEBUG_TYPE.info, \"[module] \" + msg));",
        "removals that could not finish",
    ),
    # F308: a kill mid-reload persists the shrinking mix again.
    (
        "a pet closed by a reload persists the mix again",
        STARTUP,
        b"            if (bSheepRemoved && !wasTransient && !reloadInProgress) PersistMix();",
        b"            if (bSheepRemoved && !wasTransient) PersistMix();",
        "does not persist the mix; the reload persists once",
    ),
    # F312: the flag is never raised.
    (
        "KillSheeps stops raising the shutting-down flag",
        STARTUP,
        b"            shuttingDown = true;\n",
        b"",
        "raises the shutting-down flag before it disposes the tray icon",
    ),
    # F312: SetIcon dereferences the disposed icon again.
    (
        "SetIcon touches the disposed tray icon again",
        os.path.join(REPO, "src", "dotNet", "ProcessIcon.cs"),
        b"            if (ni == null) return;\n",
        b"",
        "no-op once the tray icon is disposed",
    ),
    # F305: the fallback keeps the rejected pet's key.
    (
        "the built-in fallback keeps the rejected pet's id",
        STARTUP,
        b"                activeId = CompanionCatalog.BuiltInPetId;\n",
        b"",
        "leaves the built-in keyed as the built-in",
    ),
    # F307: the preview spawn's throw path leaks the entry again.
    (
        "a throwing preview spawn leaks its registry entry again",
        STARTUP,
        b"            try { spawned = AddSheepCore(entry.Xml, entry.Animations, entry); }\n"
        b"            catch { registry.DropIfUnused(entry); throw; }\n",
        b"            spawned = AddSheepCore(entry.Xml, entry.Animations, entry);\n",
        "drops its registry entry on the way out",
    ),
    # F282: the tray click dereferences Program.Mainthread bare again.
    (
        "the tray click dereferences the main thread unguarded again",
        os.path.join(REPO, "src", "dotNet", "ProcessIcon.cs"),
        b"                StartUp main = Program.Mainthread;\n"
        b"                if (main == null) return;\n"
        b"                main.TopMostSheeps();",
        b"                Program.Mainthread.TopMostSheeps();",
        "guard the main thread before using it",
    ),
    # F286: the app-version check downloads for itself again.
    (
        "the app-version check downloads the catalog for itself again",
        os.path.join(REPO, "src", "dotNet", "RemoteCatalog.cs"),
        b"            byte[] bytes = await FetchSharedBytesAsync(cancellationToken).ConfigureAwait(false);\n"
        b"            return ParseAppVersion(",
        b"            byte[] bytes = await SecureDownload.DownloadBytesAsync(uri, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);\n"
        b"            return ParseAppVersion(",
        "reads the shared catalog bytes",
    ),
    # F286: the Companions pane's check drops the shared copy without refilling it again.
    (
        "the Companions check-now drops the shared catalog without refilling it",
        PETSPANE,
        b"                _lastCatalog = await RemoteCatalogClient.RefreshSharedAsync(_netCts.Token);",
        b"                RemoteCatalogClient.InvalidateShared();\n"
        b"                _lastCatalog = await RemoteCatalogClient.FetchAsync(_netCts.Token);",
        "refill the shared catalog copy",
    ),
    # F249: the Studio uninstall stops forgetting the caches.
    (
        "a Studio uninstall leaves the per-id caches serving a deleted pet",
        os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs"),
        b"                CompanionCatalog.Forget(typeId);   // the caches hold a pet that no longer exists (F249)\n",
        b"",
        "its uninstall forgets too",
    ),
    # F336: the Studio install stops reloading the on-screen copies.
    (
        "a Studio install leaves the on-screen copies on the old definition",
        os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs"),
        b"                if (_startUp != null)\n"
        b"                {\n"
        b"                    int reloaded; string reloadError;\n"
        b"                    try { _startUp.ReloadPetType(typeId, out reloaded, out reloadError); } catch { }\n"
        b"                }\n",
        b"",
        "reloads the on-screen copies",
    ),
    # F361: the Preferences Apply discards the batch's durable result.
    (
        "the Preferences Apply discards its batch commit's result",
        os.path.join(REPO, "src", "Portable", "Wpf", "OptionsShell.cs"),
        b"                    ok &= batch.Commit();\n",
        b"                    batch.Commit();\n",
        "commits it once after the last",
    ),

    # F318: the loader grows a base64 decode of its own again. The fragment is the clause every re-wording of
    # the label has kept: the burn/host-shell merge (6e760a0) took host-core's "sheet, icon and sound bytes"
    # wording over host-shell's "sheet and icon bytes", and the old fragment went stale (WRONG, 2026-10-01).
    (
        "the loader decodes the icon's base64 itself again",
        os.path.join(REPO, "src", "dotNet", "Xml.cs"),
        b"            stagedIcon = new MemoryStream(iconBytes ?? new byte[0], false);\n",
        b'            stagedIcon = new MemoryStream(Convert.FromBase64String(root.Header.Icon ?? ""), false);\n',
        "decodes no base64 of its own",
    ),
    # F317: the alpha flag is written before the commit block again.
    (
        "the alpha flag is assigned before DisposeAssets again",
        os.path.join(REPO, "src", "dotNet", "Xml.cs"),
        b"                DisposeAssets();\n                AnimationXML = parsed;\n",
        b"                usesAlpha = stagedUsesAlpha;\n                DisposeAssets();\n                AnimationXML = parsed;\n",
        "the alpha flag is committed with the rest",
    ),
    # F241: the chooser evaluates the chosen animation against the primary screen again.
    (
        "the chooser evaluates the chosen animation itself again",
        os.path.join(REPO, "src", "dotNet", "Animations.cs"),
        b'            TAnimation ani = SheepAnimations[id];\n            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "new animation: "',
        b'            TAnimation ani = SheepAnimations[id];\n            ani.UpdateValues();\n            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "new animation: "',
        "the chooser only announces the chosen animation",
    ),
    # F271: the fixed 12 MiB drop buffer comes back.
    (
        "the drop read allocates the 12 MiB ceiling again",
        FORMPET,
        b"            bytes = new byte[(int)BoundedReadCapacity(stream, maximumBytes)];\n",
        b"            bytes = new byte[checked(maximumBytes + 1)];\n",
        "the drop read allocates from the file",
    ),
    # F229: the bare EndsWith comes back into the resource lookup.
    (
        "the resource lookup matches a bare suffix again",
        os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "EmbeddedResources.cs"),
        b"                if (MatchesResourceName(candidate, fileNameSuffix))\n",
        b"                if (candidate.EndsWith(fileNameSuffix, StringComparison.OrdinalIgnoreCase))\n",
        "the resource lookup matches an exact name",
    ),
    # F230: Update writes defaults over an unreadable document again.
    (
        "JsonSettingsStore.Update stops refusing an unreadable document",
        os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "JsonSettingsStore.cs"),
        b"                        if (result == ReadResult.Unreadable) return false;\n",
        b"",
        "Update takes its lease first",
    ),
    # F207: TestModule walks the installed library by hand again.
    (
        "TestModule's preview reads %LOCALAPPDATA% by hand again",
        os.path.join(REPO, "modules", "TestModule", "TestModule.cs"),
        b"                if (pets.TryReadTypeXml(type.TypeId, out xml, out readError)) break;\n",
        b'                xml = System.IO.File.ReadAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "x")); if (xml != null) break;\n',
        "the preview verb reads the installed pet",
    ),
    # F330 follow-up: the template fetches a fresh settings handle per call again.
    (
        "the module template fetches settings per call again",
        os.path.join(REPO, "templates", "desktop-ai-companion-module", "SampleModule.cs"),
        b"            if (_settings == null && _host != null) _settings = _host.GetSettings(Info.Id);\n",
        b"            _settings = _host != null ? _host.GetSettings(Info.Id) : null;\n",
        "memoises one handle instead of fetching",
    ),
    # F256: a host drop-down is Clear()ed without disposal again.
    (
        "the Add-a-companion drop-down is Clear()ed without disposal again",
        os.path.join(REPO, "src", "dotNet", "ContextMenus.cs"),
        b"            ClearAndDispose(addPetMenuItem.DropDownItems);\n",
        b"            addPetMenuItem.DropDownItems.Clear();\n",
        "no tray drop-down is Clear()ed",
    ),
    # F255: the item is disposed before its children, which orphans their Images.
    (
        "DisposeItemTree disposes the item before its children",
        os.path.join(REPO, "src", "dotNet", "ContextMenus.cs"),
        b"            if (item == null) return;\n            var dropDown = item as ToolStripDropDownItem;\n",
        b"            if (item == null) return;\n            try { item.Dispose(); } catch { }\n            var dropDown = item as ToolStripDropDownItem;\n",
        "DisposeItemTree disposes the children and the Image",
    ),
    # F272: the never-shown Icon is deserialised per spawn again.
    (
        "the companion form deserialises its Icon again",
        os.path.join(REPO, "src", "dotNet", "FormCompanion.Designer.cs"),
        b"\t\t\tthis.ShowIcon = false;\n",
        b'\t\t\tthis.ShowIcon = false;\n\t\t\tthis.Icon = ((System.Drawing.Icon)(new System.ComponentModel.ComponentResourceManager(typeof(FormCompanion)).GetObject("$this.Icon")));\n',
        "the companion form loads no Icon",
    ),
    # F273: the row cap is not applied after a plain add.
    (
        "the debug window stops trimming after a plain add",
        os.path.join(REPO, "src", "dotNet", "FormDebug.cs"),
        b"\t\t\tlistView1.Items.Add(item);\n\t\t\tTrimRows();\n",
        b"\t\t\tlistView1.Items.Add(item);\n",
        "the debug window trims after every add",
    ),
    # F274: a failed handoff is swallowed again (the catch no longer starts with the log line).
    (
        "the debug window swallows a failed text handoff again",
        os.path.join(REPO, "src", "dotNet", "FormDebug.cs"),
        b"\t\t\tcatch (Exception ex)\n\t\t\t{\n\t\t\t\tStartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error,\n",
        b"\t\t\tcatch (Exception ex)\n\t\t\t{\n\t\t\t\tif (ex == null) return;\n\t\t\t\tStartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error,\n",
        "the debug window trims after every add",
    ),
    # F325: an animation name goes into the label verbatim again.
    (
        "the DOT export emits an animation name unescaped again",
        os.path.join(REPO, "src", "Tools", "XmlToDot.cs"),
        b".Append(EscapeLabel(anim.Name))",
        b".Append(anim.Name)",
        "every animation name and only-flag",
    ),
    # F260: a listing that throws is logged but no longer counted.
    (
        "a root that cannot be listed is a clean wipe again",
        os.path.join(REPO, "src", "dotNet", "FactoryReset.cs"),
        b'                failed++;\n                if (log != null) log.Add("    could not list " + what',
        b'                if (log != null) log.Add("    could not list " + what',
        "a listing that throws counts as a failure",
    ),
    # F321: the per-file layout rule is disabled while its statement stays in place.
    (
        "the classifier stops rejecting a row whose field count differs",
        os.path.join(REPO, "src", "Fortunes", "classify-corpus.py"),
        b"                elif len(parts) != layout:\n",
        b"                elif False and len(parts) != layout:\n",
        "the classifier fixes the field layout",
    ),
    # F323: the stripped count moves back before the drop test.
    (
        "the stripper counts a dropped row as stripped again",
        os.path.join(REPO, "src", "Fortunes", "strip-authors.py"),
        b"                    if len(new) < 8:\n                        dropped += 1\n                        continue\n",
        b"                    if new != text:\n                        changed += 1\n                    if len(new) < 8:\n                        dropped += 1\n                        continue\n",
        "the stripper counts a byline as stripped only",
    ),
    # N-host-04: the cloak/shell filter is gone from the enumeration, so a cloaked or shell window can decide.
    (
        "the fullscreen scan offers cloaked and shell windows to the decider",
        os.path.join(REPO, "src", "dotNet", "FullscreenScan.cs"),
        b"                    if (IsCloaked(hWnd) || IsShell(hWnd)) return true;\n",
        b"",
        "the enumeration filters hidden, iconic, cloaked",
    ),


    # ---- lane fix/settings ----


    # ---- lane fix/scripts ----

    # F002: the timestamp URL goes back between single quotes in the ZIP build's run body, the exact
    # shape release.yml shipped with. Anchored on the following step's name so the ZIP step's line, not
    # the MSI step's identically-worded one, is the one mutated.
    (
        "release.yml pastes vars.SIGN_TIMESTAMP_URL into a run body again",
        RELEASE_YML,
        b"          -SignTimestampUrl $env:SIGN_TIMESTAMP_URL\n\n      - name: Install WiX and build the MSI",
        b"          -SignTimestampUrl '${{ vars.SIGN_TIMESTAMP_URL }}'\n\n      - name: Install WiX and build the MSI",
        "no release.yml run body interpolates",
    ),
    # N-scripts-01: the debug smoke's first sweep goes back to matching every checkout's build output.
    (
        "debug-menu-smoke.ps1 sweeps every checkout's build tree again",
        DEBUG_SMOKE,
        b"    Where-Object { $_.Path -and $_.Path.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase) } |\n"
        b"    ForEach-Object { try { $_.Kill(); [void]$_.WaitForExit(5000) } catch { } }\n"
        b"\n"
        b"$VK_SHIFT = 0x10",
        b"    Where-Object { $_.Path -like '*\\build\\*' } |\n"
        b"    ForEach-Object { try { $_.Kill(); [void]$_.WaitForExit(5000) } catch { } }\n"
        b"\n"
        b"$VK_SHIFT = 0x10",
        "sweeps scoped to this checkout",
    ),
    # N-scripts-01, the quieter regression: the tray smoke's finally sweep loses the case-insensitive
    # comparison, so a checkout reached under different casing sweeps nothing.
    (
        "tray-menu-smoke.ps1's finally sweep compares case-sensitively",
        TRAY_SMOKE,
        b"        Where-Object { $_.Path -and $_.Path.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase) } |\n"
        b"        ForEach-Object { try { $_.Kill(); [void]$_.WaitForExit(5000) } catch { } }\n"
        b"    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $previousRoot",
        b"        Where-Object { $_.Path -and $_.Path.StartsWith($buildRoot) } |\n"
        b"        ForEach-Object { try { $_.Kill(); [void]$_.WaitForExit(5000) } catch { } }\n"
        b"    $env:DESKTOP_AI_COMPANION_DATA_ROOT = $previousRoot",
        "sweeps scoped to this checkout",
    ),

    # ---- lane fix/followups ----


    # ---- lane burn/reminder ----
    # The lane's behavioural checks live in the module self-test and are graded by tests/mutate-selftest-guards.py;
    # this file holds its one source invariant.

    # RA-123, the Reminder site (2026-10-01): the Join link's shell-execute Process.Start loses its using and leaks
    # a handle per click again. The invariant under the lane's anchor counts the module's Start sites against the
    # wrapped ones and names the file of a bare site; the fragment stops before that name so a console wrap of the
    # thrown message cannot hide the match (the FIRED line shows the whole label).
    (
        "a Reminder Process.Start stops disposing what it returns",
        REMINDER_MODULE,
        b"                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })) { }",
        b"                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });",
        "every Process.Start in the Reminder module disposes what it returns (sites 1, disposed 0",
    ),

    # ---- lane burn/blinkingled ----
    # ---- lane burn/scripts-tests ----

    # RA-360 / R-067: the redirect scan's judgement is a function with a WITNESS per offender branch. One
    # case per branch deleted (the tree has no offender, so only the witness can notice), one for the
    # synchronous-read scan narrowed back to files that declare a redirect, and one real file: WebLinks.cs
    # gains a synchronous read while declaring no redirect of its own, which the old admission test skipped.
    # The fragments are the labels' distinctive MIDDLE, well inside the first ~90 columns: PowerShell wraps
    # the thrown message at the console width the harness inherited and the grader matches per line, so a
    # fragment reaching past the wrap read WRONG for a case that had fired (measured 2026-09-30 on the
    # synchronous-read witness, whose "... as exactly one offender" landed on the continuation line).
    (
        "the stdout-unpinned branch of the redirect judgement is deleted",
        HARDENING,
        b"        if ($redirectsOut -and ($body -notmatch 'StandardOutputEncoding\\s*=')) {\n"
        b"            $offenders += \"$Relative (site $sites): stdout redirected, encoding unpinned\"\n"
        b"        }\n",
        b"",
        "judgement reports stdout unpinned",
    ),
    (
        "the stderr-unpinned branch of the redirect judgement is deleted",
        HARDENING,
        b"        if ($redirectsErr -and ($body -notmatch 'StandardErrorEncoding\\s*=')) {\n"
        b"            $offenders += \"$Relative (site $sites): stderr redirected, encoding unpinned\"\n"
        b"        }\n",
        b"",
        "judgement reports stderr unpinned",
    ),
    (
        "the declared-versus-covered branch of the redirect judgement is deleted",
        HARDENING,
        b"    if ($declaredRedirects -gt $coveredRedirects) {\n"
        b"        $offenders += (\"$Relative`: $($declaredRedirects - $coveredRedirects) redirect \" +\n"
        b"            'assignment(s) sit outside a ProcessStartInfo initialiser, so this scan cannot judge ' +\n"
        b"            'their encoding -- move them into the initialiser, or teach this check that form')\n"
        b"    }\n",
        b"",
        "judgement reports redirect outside an initialiser",
    ),
    (
        "the synchronous-read scan runs only for files that declare a redirect again",
        HARDENING,
        b"    foreach ($syncRead in @(Get-SynchronousReadSites $Code)) {",
        b"    foreach ($syncRead in @($(if ($Code -match 'RedirectStandard(Output|Error)\\s*=\\s*true') { Get-SynchronousReadSites $Code }))) {",
        "judgement reports synchronous read in a file",
    ),
    (
        "a file that declares no redirect drains a stream synchronously",
        WEBLINKS,
        b"                using (Process process = Process.Start(new ProcessStartInfo\n"
        b"                {\n"
        b"                    FileName = normalized,\n"
        b"                    UseShellExecute = true\n"
        b"                }))\n"
        b"                {\n"
        b"                }",
        b"                using (Process process = Process.Start(new ProcessStartInfo\n"
        b"                {\n"
        b"                    FileName = normalized,\n"
        b"                    UseShellExecute = true\n"
        b"                }))\n"
        b"                {\n"
        b"                    if (process != null) process.StandardOutput.ReadToEnd();\n"
        b"                }",
        "pins its own encoding, per SITE",
    ),

    # RA-362: the three checks that read raw source or a whole-line-only stripper, each fed the comment
    # that used to satisfy it. The update check: a stamp moved AHEAD of the fetch under a comment naming
    # the fetch (raw IndexOf found the comment first and the order held).
    (
        "ORDER: a comment naming the fetch sits above a stamp moved ahead of it",
        APPUPDATE,
        b"                string latest = await RemoteCatalogClient.FetchAppVersionAsync(token).ConfigureAwait(false);",
        b"                // FetchAppVersionAsync can throw, so stamp the attempt first\n"
        b"                data.SetAppUpdateResult(DateTimeOffset.UtcNow, \"\");\n"
        b"                string latest = await RemoteCatalogClient.FetchAppVersionAsync(token).ConfigureAwait(false);",
        "FETCHES before it stamps",
    ),
    # The poke sass: the fix reverted, with the offer's name left in a TRAILING comment on the line above the
    # bare Say (the whole-line stripper kept trailing comments, so the word sat ahead of `.Say(` and the
    # order held).
    (
        "the poke sass goes straight to a bubble under a trailing comment naming the offer",
        STARTUP,
        b"                    if (Host == null || !Host.RaiseSpeechRequest(subject, s)) subject.Say(s);",
        b"                    string sass = s; // RaiseSpeechRequest is not needed here\n"
        b"                    subject.Say(sass);",
        "the poke sass is offered to the speech responders",
    ),
    # The reset method: the Configure call gone, its name left in a comment inside the method (the slice
    # was taken from the raw source, so Contains found the comment).
    (
        "the reset stops re-applying the logger under a comment naming the call",
        os.path.join(REPO, "src", "Portable", "Wpf", "OptionsShell.cs"),
        b"                DesktopAICompanion.DiagnosticLog.Configure(\n"
        b"                    data.GetDiagnosticLog(),\n"
        b"                    data.GetDiagnosticLogMaxKilobytes(),\n"
        b"                    data.GetDiagnosticLogKeep(),\n"
        b"                    data.GetDiagnosticLogMutedCategories(),\n"
        b"                    data.GetDiagnosticLogMutedModules());\n",
        b"                // DiagnosticLog.Configure( is applied by the pane on its next rebuild\n",
        "re-applies the diagnostic-log settings to the RUNNING logger",
    ),

    # RA-364: the "current" read becomes an alias of what was already read, the exact F149 defect with the old
    # tokens in place. Re-pointed by lane feature/fortunes-index: CustomFolderSignatureNow answers with the
    # published snapshot's signature instead of walking the folder, which compiles and makes the skip skip a
    # real change.
    (
        "the folder's current fingerprint is the snapshot it already read",
        FORTUNE_PROVIDER,
        b"            return CustomDirSignature(directory);",
        b"            CustomSnapshot snap = _custom;\n"
        b"            return snap == null ? \"\" : snap.Signature;",
        "never the recorded one with itself",
    ),

    # RA-365: each order check's anchor is reshaped, so IndexOf answers -1 and the order used to hold vacuously.
    (
        "the chosen animation is assigned through a local, and the order anchor is gone",
        FORMPET,
        b"                CurrentAnimation = Animations.GetAnimation(id);\n",
        b"                TAnimation next = Animations.GetAnimation(id);\n"
        b"                CurrentAnimation = next;\n",
        "the chooser only announces the chosen animation",
    ),
    (
        "the debug window adds its row through a renamed local, and the order anchor is gone",
        os.path.join(REPO, "src", "dotNet", "FormDebug.cs"),
        b"\t\t\tlistView1.Items.Add(item);\n",
        b"\t\t\tListViewItem row = item;\n"
        b"\t\t\tlistView1.Items.Add(row);\n",
        "the debug window trims after every add",
    ),


    # ---- lane burn/host-shell ----

    # RA-227 (the host-side half): CompanionHost.SpeechRoutingKey's no-pet fallback reads the persisted active id
    # again, so on F305's fallback branch a key is built from the rejected pet's id, which no tray entry reads.
    (
        "CompanionHost's speech routing key falls back to the persisted active id again",
        COMPANION_HOST_CS,
        b"            try { return Program.Mainthread != null ? (Program.Mainthread.DefaultTypeId ?? \"\") : \"\"; }\n",
        b"            try { return Program.MyData != null ? (Program.MyData.GetActivePetId() ?? \"\") : \"\"; }\n",
        "no-pet fallback reads the running default type",
    ),

    # N-burn-tools-04: the convention self-test's author note sends authors to the retired flag lists again
    # (the sentence F397 retired, back on the line after the one that names Test-ModuleSelfTests.ps1).
    (
        "ModuleConventionSelfTest's author note sends authors to the retired flag lists again",
        os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleConventionSelfTest.cs"),
        b"    /// list is the one place to add it: the gate deliberately fails on a covered self-test that did not\n",
        b"    /// list is the one place to add it. Still add the flag to tests\\run-gate.ps1 and .github\\workflows\\build.yml:\n",
        "no longer sends authors to the retired run-gate.ps1",
    ),

    # RA-212: Update takes the cross-session lease directly again, bypassing TryLease's owner re-entry, so a
    # Save from inside its mutate waits the full 3 s on the file half and fails; the F231 invariant now pins
    # TryLease() in Update and no direct acquisition there.
    (
        "Update takes the cross-session lease directly again, bypassing the owner re-entry",
        os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "JsonSettingsStore.cs"),
        b"                    using (IDisposable lease = TryLease())\n"
        b"                    {\n"
        b"                        if (lease == null) return false;\n"
        b"                        T current;\n",
        b"                    using (IDisposable lease = CrossSessionLock.TryAcquire(MutexName(), _path, LockTimeoutMilliseconds))\n"
        b"                    {\n"
        b"                        if (lease == null) return false;\n"
        b"                        T current;\n",
        "Update takes its lease first",
    ),

    # RA-248 / RA-249: the uninstall site bypasses the save-then-restart gate again (a bare marker write and a
    # bare restart), so the helper-call and restart-site counts come apart.
    (
        "the Modules pane's uninstall site bypasses the save-then-restart gate again",
        PETSPANE_MODULES,
        b"                Program.TryRequestRestartAfterSave(\n"
        b"                    delegate { DesktopAICompanion.Plugins.PendingModuleRemovals.MarkForRemoval(id); return true; },\n"
        b"                    RestartToApply);\n",
        b"                DesktopAICompanion.Plugins.PendingModuleRemovals.MarkForRemoval(id);\n"
        b"                RestartToApply();\n",
        # Re-pointed 2026-10-02 by lane feature/modules-update-all: Update all added the fifth restart site, and
        # the fragment is the label's opening words, ahead of where powershell.exe wraps a thrown message.
        "the Modules pane's five restart sites all go through",
    ),

    # RA-320 / RA-321: the Preferences Save stops reading the Run key back, so a refused write is success again;
    # and the reset path likewise.
    (
        "the Preferences Save stops reading the Run key back",
        OPTIONS_SHELL,
        b"                        startupOk = StartupRegistration.IsEnabled() == b;\n",
        b"                        startupOk = true;\n",
        "reads the Run-key registration back after writing it",
    ),
    (
        "reset to defaults stops reading the Run key back",
        OPTIONS_SHELL,
        b"                try { StartupRegistration.Set(false); startupCleared = !StartupRegistration.IsEnabled(); } catch { }\n",
        b"                try { StartupRegistration.Set(false); } catch { }\n",
        "reads the Run-key registration back after clearing it",
    ),
    # RA-322: the output device is applied inside the batch with the requested value again (the F361-era
    # shape), and the post-commit apply is skipped.
    (
        "the output device is applied inside the batch from the requested value again",
        OPTIONS_SHELL,
        b"                        ok &= data.SetAudioDeviceId(toStore);\n"
        b"                        audioDeviceChosen = true;\n",
        b"                        ok &= data.SetAudioDeviceId(toStore);\n"
        b"                        try { if (Program.Mainthread != null) Program.Mainthread.ApplyAudioDevice(toStore); } catch { }\n"
        b"                        audioDeviceChosen = false;\n",
        "applied after the commit, from what the store holds",
    ),
    # RA-325: the reset writes the global size fallback again.
    (
        "reset to defaults writes the global size fallback again",
        OPTIONS_SHELL,
        b"                    data.SetAudioDeviceId(def.AudioDeviceId);\n",
        b"                    data.SetAudioDeviceId(def.AudioDeviceId);\n"
        b"                    data.SetScale(def.ScaleLevel);\n",
        "does not write the global size fallback",
    ),
    # RA-311: AddPet dereferences a null runtime again.
    (
        "AddPet dereferences a null runtime again",
        OPTIONS_CONTROLLER,
        b"            if (_runtime == null) return OpResult.Fail(\"No running companion host to add it to.\");\n",
        b"",
        "refuse a null runtime before they dereference it",
    ),
    # RA-313: the Remove button reports a removal whatever RemoveOnePet answered.
    (
        "the Remove button reports a removal it did not make again",
        PETSPANE,
        b"                    _status.Text = removed ? (\"Removed one \" + row.DisplayName + \".\") : (\"No \" + row.DisplayName + \" was on screen to remove.\");\n",
        b"                    _status.Text = \"Removed one \" + row.DisplayName + \".\";\n",
        "reports a removal only when RemoveOnePet says one happened",
    ),
    # RA-310: the Add button guesses at the cap again instead of showing the controller's reason.
    (
        "the Add button guesses at the cap again",
        PETSPANE,
        b"                _status.Text = r.Ok ? (\"Added \" + row.DisplayName + \".\") : (\"Couldn't add \" + row.DisplayName + \": \" + PaneText.Short(r.Message));\n",
        b"                _status.Text = r.Ok ? (\"Added \" + row.DisplayName + \".\") : \"Couldn't add (max companions reached?).\";\n",
        "show the controller's reason on failure",
    ),
    # RA-314: the download calls ForgetStats directly again beside the Forget that already reaches it.
    (
        "the download forgets the stats cache twice again",
        PETSPANE,
        b"                CompanionCatalog.Forget(pet.Id);\n\n                // An update to a pet that is ON SCREEN",
        b"                CompanionCatalog.Forget(pet.Id);\n                ForgetStats(pet.Id);\n\n                // An update to a pet that is ON SCREEN",
        "through the one Forget call",
    ),
    # RA-315: UninstallPet grows its inline containment copy back.
    (
        "UninstallPet contains the delete with an inline copy of the library rule again",
        PETSPANE,
        b"                try { dir = CompanionProvenance.SafeLibraryDirectory(id); }\n",
        b"                try { string root = Path.GetFullPath(AppPaths.LibraryPetsDirectory); dir = Path.GetFullPath(Path.Combine(root, id ?? \"\")); if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(\"outside\"); }\n",
        "through CompanionProvenance.SafeLibraryDirectory",
    ),
    # RA-276: Modules projects the live list again.
    (
        "ModuleHost.Modules projects the live list again",
        MODULE_HOST_CS,
        b"        public IReadOnlyList<IModule> Modules { get { return _snapshot; } }\n",
        b"        public IReadOnlyList<IModule> Modules { get { return _loaded.Select(l => l.Module).ToList(); } }\n",
        "answers from a snapshot republished",
    ),
    # RA-275: the styled Say offers the line without its style again, so a claimed line re-shown through
    # ShowBubble comes back plain.
    (
        "the styled Say drops the style from the speech offer again",
        COMPANION_HOST_CS,
        b"            if (RaiseSpeechRequest(p.Pet, text, style)) return;\n            Safe(() => p.Pet.SayWithDwell(text, 0, style));",
        b"            if (RaiseSpeechRequest(p.Pet, text)) return;\n            Safe(() => p.Pet.SayWithDwell(text, 0, style));",
        "keeps its style through the host-supplied ShowBubble",
    ),
    # N-host-shell-01: the styled broadcast offers the plain pair again.
    (
        "the styled SayAll drops the style from the speech offer again",
        STARTUP,
        b"            if (Host != null && Host.RaiseSpeechRequest(null, text, style)) return;\n",
        b"            if (Host != null && Host.RaiseSpeechRequest(null, text)) return;\n",
        "a styled broadcast offered to the speech chain carries its style",
    ),
    # N-deadcode-06 / RA-309: the OpenExeConfiguration candidate grows back.
    (
        "LocalData builds the OpenExeConfiguration candidate again",
        LOCALDATA_CS,
        b"            return new List<string>(AppPaths.LegacySettingsFiles);\n",
        b"            var candidates = new List<string>(AppPaths.LegacySettingsFiles);\n"
        b"            candidates.Add(System.Configuration.ConfigurationManager.OpenExeConfiguration(System.Configuration.ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath);\n"
        b"            return candidates;\n",
        "from AppPaths.LegacySettingsFiles alone",
    ),

    # ---- lane burn/petstudio ----

    # RA-145: the WIRING of the rejected-re-parse guard, both halves. The rule itself (drop nothing without a
    # graph) is pinned in --module-selftest=petstudio. Each half alone saves the author's chain, which is why the
    # invariant asserts both: one rotting unseen would leave the other as the only guard.
    (
        "RenderMap resyncs the timeline against a rejected report again",
        PETSTUDIO_WINDOW,
        b"            return _timeline != null ? _timeline.Resync(report.IsValid) : 0;",
        b"            return _timeline != null ? _timeline.Resync(true) : 0;",
        "a rejected re-parse does not resync the timeline",
    ),
    (
        "RenderMap clears the last accepted graph for a rejected report again",
        PETSTUDIO_WINDOW,
        b"            if (report.IsValid)\n"
        b"            {\n"
        b"                _nodesById.Clear();\n",
        b"            _nodesById.Clear();\n"
        b"            if (report.IsValid)\n"
        b"            {\n",
        "a rejected re-parse does not resync the timeline",
    ),
    # RA-140: a close overtakes the analysis in flight and refuses what lands afterwards. One mutant drops the
    # generation bump from the Closed handler; the other lets a conversion land in the closed editor.
    (
        "Closed no longer overtakes the analysis in flight",
        PETSTUDIO_WINDOW,
        b"                _closed = true;\n"
        b"                Interlocked.Increment(ref _analyzeGeneration);\n",
        b"                _closed = true;\n",
        "closing the studio overtakes the analysis in flight",
    ),
    (
        "a conversion lands in the closed editor again",
        PETSTUDIO_WINDOW,
        b"            if (_closed) return;\n"
        b"            _openedPath = null;",
        b"            _openedPath = null;",
        "closing the studio overtakes the analysis in flight",
    ),
    # RA-133: the chain build comes back onto the UI thread (the 1.1.17 shape, with an await kept so the method
    # still compiles as async).
    (
        "Run chain compiles the debug pet on the UI thread again",
        TIMELINE_PANE,
        b"                built = await Task.Run(delegate\n"
        b"                {\n"
        b"                    string e;\n"
        b"                    string x = BehaviourChain.BuildDebugXml(xml, steps, loop, faceRight, out e);\n"
        b"                    error = e;\n"
        b"                    return x;\n"
        b"                });",
        b"                { string e; built = BehaviourChain.BuildDebugXml(xml, steps, loop, faceRight, out e); error = e; }\n"
        b"                await Task.Yield();",
        "Run chain compiles the debug pet on a pool thread",
    ),
    # RA-142: the zip import's refusal moves back to after its picker alone.
    (
        "the zip import refuses a second import only after its picker again",
        PETSTUDIO_WINDOW,
        b"            if (_importing) { SetStatus(StillConverting); return; }\n"
        b"            var dlg = new Microsoft.Win32.OpenFileDialog",
        b"            var dlg = new Microsoft.Win32.OpenFileDialog",
        "the zip import refuses a second import before its picker",
    ),
    # RA-138: the pane status wiring. OpenStatus is pinned as a pure function in --module-selftest=petstudio;
    # the mutant bypasses it with the shipped constant.
    (
        "OpenAsync answers the constant success text again",
        PETSTUDIO_MODULE_CS,
        b"            return System.Threading.Tasks.Task.FromResult(OpenStatus(TryOpen(out failureCategory), failureCategory));",
        b"            TryOpen(out failureCategory);\n"
        b"            return System.Threading.Tasks.Task.FromResult(\"Companion Studio is open.\");",
        "reports a failed open on the pane",
    ),
    # F161 / RA-147: a second <summary> stacked on a member, the shape that displaced three members.
    (
        "a second <summary> is stacked on CanPlayOnPreview",
        PETSTUDIO_WINDOW,
        b"        internal static bool CanPlayOnPreview(bool hostPresent, bool previewAlive, string selectedName)",
        b"        /// <summary>A second summary, stacked on the first.</summary>\n"
        b"        internal static bool CanPlayOnPreview(bool hostPresent, bool previewAlive, string selectedName)",
        "no PetStudio member carries two <summary> blocks",
    ),
    # ---- lane burn/tools ----
    # The converter CLI (tools\ShimejiConvert\Program.cs) has no self-test of its own, so its shapes are pinned
    # as source invariants; each case here restores the shape an item removed. The harness builds nothing, so a
    # mutation need not compile, but every one below does.

    # RA-368: reloop's version skip is routed through SkipOrStranded like every other version-gated verb.
    (
        "burn-tools: reloop's version skip bypasses SkipOrStranded again",
        PROGRAM_CS,
        b"                    !string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersion, StringComparison.Ordinal))\n"
        b"                { Console.WriteLine(name.PadRight(36) + SkipOrStranded(root.Header.Version)); skipped++; continue; }",
        b"                    !string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersion, StringComparison.Ordinal))\n"
        b"                { Console.WriteLine(name.PadRight(36) + \" skip (format \" + (root.Header.Version ?? \"?\") + \", not one this migration understands)\"); skipped++; continue; }",
        "reloop's version skip goes through SkipOrStranded",
    ),
    # RA-369: reground adds the three situations unconditionally; the unreachable already-present guard is gone.
    (
        "burn-tools: reground grows its unreachable already-present guard back",
        PROGRAM_CS,
        b"                    foreach (string situation in situations)\n"
        b"                        edges.Add(new XmlData.NextNode { Value = fall.Id, Probability = 100, OnlyFlag = situation });",
        b"                    foreach (string situation in situations)\n"
        b"                    {\n"
        b"                        bool already = false;\n"
        b"                        if (already) continue;\n"
        b"                        edges.Add(new XmlData.NextNode { Value = fall.Id, Probability = 100, OnlyFlag = situation });\n"
        b"                    }",
        "reground adds the three situations unconditionally",
    ),
    # RA-370: the CLI reads the one reserved-name array through the engine's facade; no literal copy.
    (
        "burn-tools: a fourth copy of the reserved names grows back in the CLI",
        PROGRAM_CS,
        b"            foreach (string magic in ShimejiEngine.ReservedEntryPointNames)",
        b'            foreach (string magic in new[] { "fall", "drag", "kill", "sync" })',
        "no literal copy of the reserved animation names outside PetGraph",
    ),
    # RA-372: both convert verbs print the engine's reason whenever it has one.
    (
        "burn-tools: the convert verb gates the engine's reason on !Valid again",
        PROGRAM_CS,
        b"            Console.WriteLine(\"wrote \" + residuePath);\n"
        b"            PrintConversionError(r);\n"
        b"            return r.Accepted ? 0 : 1;\n"
        b"        }\n"
        b"\n"
        b"        /// <summary>\n"
        b"        /// Write a migrated animations.xml back",
        b"            Console.WriteLine(\"wrote \" + residuePath);\n"
        b"            if (!r.Valid) Console.Error.WriteLine(\"validator: \" + r.Error);\n"
        b"            return r.Accepted ? 0 : 1;\n"
        b"        }\n"
        b"\n"
        b"        /// <summary>\n"
        b"        /// Write a migrated animations.xml back",
        "both convert verbs print the engine's reason whenever it has one",
    ),
    # N-deadcode-03: every migration verb commits through CommitMigratedPet; none writes the file itself.
    (
        "burn-tools: a migration verb inlines the write tail again",
        PROGRAM_CS,
        b"                string commitFailure;\n"
        b"                if (!CommitMigratedPet(path, root, out commitFailure))\n"
        b"                { Console.Error.WriteLine(name.PadRight(36) + \" \" + commitFailure); failures++; continue; }\n"
        b"                petsChanged++; renamed += map.Count;",
        b"                WritePetXmlPreservingEncoding(path, ShimejiEngine.Serialize(root));\n"
        b"                petsChanged++; renamed += map.Count;",
        "every migration verb commits through CommitMigratedPet",
    ),
    # The heading item's reclimb half: the migration keeps the repeatfrom a sequence carries.
    (
        "burn-tools: reclimb flattens repeatfrom again",
        PROGRAM_CS,
        b"                    int repeatFrom = Math.Max(0, Math.Min(frames - 1, a.Sequence.RepeatFromFrame));\n"
        b"                    a.Sequence.RepeatFromFrame = repeatFrom;",
        b"                    int repeatFrom = 0;\n"
        b"                    a.Sequence.RepeatFromFrame = 0;",
        "reclimb keeps the repeatfrom a sequence carries",
    ),
    # RA-333: the template's self-test doc block sends an in-tree author to Test-ModuleSelfTests.ps1.
    (
        "burn-tools: the template tells authors to wire the flag into run-gate.ps1 again",
        TEMPLATE_MODULE_CS,
        b"        /// For an IN-TREE module, then add the id to $Covered in tests\\Test-ModuleSelfTests.ps1 (the one\n",
        b"        /// then add that flag to tests\\run-gate.ps1 and .github\\workflows\\build.yml so CI runs it too.\n",
        "the template's self-test doc block sends an in-tree author to Test-ModuleSelfTests.ps1",
    ),


    # ---- lane burn/remembrance ----

    # RA-157: a Remembrance pane action discards a Save() result again -- the shape that answered "✓ storage"
    # over a settings file that could not be written. The self-test drives the adopt path; this invariant is
    # what covers the five actions that open a dialog or reach the network, so a bare `_settings.Save();`
    # anywhere in the module has to fail it. Re-pointed by lane feature/layout-remembrance: the site it mutated,
    # "Browse for a storage folder…", went with the module's own Browse buttons (the host's path fields do that
    # job), so the mutant is now the Ollama "Refresh local models", a pane action that reaches the network and
    # saves what it found.
    (
        "a Remembrance pane action discards its Save() result again",
        REMEMBRANCE_MODULE,
        b"                string notPersisted;\n"
        b"                if (!TrySaveSettings(\"the discovered model list\", out notPersisted)) return notPersisted;\n",
        b"                _settings.Save();\n",
        "no Remembrance pane action discards a Save() result",
    ),

    # ---- lane feature/remembrance-2 ----

    # The Transcription Validate's clip: TryVerify goes back to its fixed second of silence while the answer says two,
    # which the module self-test cannot see because it stands the run in for.
    (
        "feature/remembrance-2: TryVerify builds a fixed second of silence again",
        os.path.join(REPO, "modules", "Remembrance", "WhisperInstaller.cs"),
        b"                byte[] silence = ModuleKit.WavAudio.FromPcm(new short[clipSamples], 16000, 1);\n",
        b"                byte[] silence = ModuleKit.WavAudio.FromPcm(new short[16000], 16000, 1);\n",
        "clip length honoured",
    ),
    (
        "feature/remembrance-2: TryVerify loses its clip-length parameter",
        os.path.join(REPO, "modules", "Remembrance", "WhisperInstaller.cs"),
        b"        public static bool TryVerify(string exePath, string modelPath, out string detail, int clipSamples = 16000)\n",
        b"        public static bool TryVerify(string exePath, string modelPath, out string detail, int clipSamples)\n",
        "WITNESS TryVerify takes a clip length",
    ),
    # remembrance.busy for "Transcribe a WAV file...", the one span behind a dialog no self-test passes: the clear
    # leaves the run's finally, and the run stops going through the seam.
    (
        "feature/remembrance-2: Transcribe a WAV file stops clearing remembrance.busy in a finally",
        REMEMBRANCE_MODULE,
        b"                        finally { busy.Dispose(); }\n",
        b"",
        "manual transcription busy",
    ),
    (
        "feature/remembrance-2: Transcribe a WAV file bypasses the TranscribeWav seam",
        REMEMBRANCE_MODULE,
        b"                            TranscribeWav(wav, transcript, whisperExe, model, name, null, null, out did);\n",
        b"                            Transcriber.Transcribe(wav, transcript, whisperExe, model, name, null, null, out did);\n",
        "WITNESS Transcribe a WAV file runs whisper-cli through the TranscribeWav seam",
    ),

    # ---- lane burn/scripts-pack ----

    # RA-001: the module self-test step loses its `if:` on the build step's outcome and is back under
    # GitHub's default success(), so one red host self-test skips all seven module suites again.
    (
        "build.yml: the module self-test step loses its if: on the build outcome",
        BUILD_YML,
        b"        if: ${{ !cancelled() && steps.build.outcome == 'success' }}\n        shell: pwsh\n        run: .\\tests\\Test-ModuleSelfTests.ps1\n",
        b"        shell: pwsh\n        run: .\\tests\\Test-ModuleSelfTests.ps1\n",
        "runs whenever the build step succeeded",
    ),
    # RA-190: the pack step's run line replaced, so dotnet pack first runs on a v* tag again.
    (
        "build.yml: the NuGet pack step no longer packs",
        BUILD_YML,
        b"        run: .\\packaging\\New-NuGetPackages.ps1 -OutputDirectory dist\\nuget\n",
        b"        run: Write-Host 'pack skipped'\n",
        "packs the module-author NuGet packages",
    ),
    # RA-002: the release notes hardcode Unsigned again while the signing scaffolding promises no edit.
    (
        "release.yml: the release notes hardcode Unsigned again",
        RELEASE_YML,
        b'              --notes "$signedWord x64 build for $tag. Verify downloads against SHA256SUMS.txt."\n',
        b'              --notes "Unsigned x64 build for $tag. Verify downloads against SHA256SUMS.txt."\n',
        "words the notes from it",
    ),
    # RA-203: a restore slipped back ahead of the module-list assertions, the pre-fix order.
    (
        "build.ps1: the restore runs before the module-list assertions again",
        BUILDPS1,
        b"$moduleProjects = @(\n",
        b"Write-Host 'Restoring NuGet packages...' -ForegroundColor Cyan\n& $dotnet restore $projectPath '-p:Platform=x64' '--nologo' '-v:minimal'\n$moduleProjects = @(\n",
        "BEFORE it restores NuGet packages",
    ),
    # ---- lane burn/aibrain ----

    # RA-069, RA-070, R-017: the aibrain probe writes no SKIP: line. A SKIP put back beside the recognizer check
    # (ADDED, not substituted, so the positive control that the FAIL-naming check exists still passes and the
    # negative is what fires) is the shape both runners grade as a failure of the whole run.
    (
        "the aibrain probe skip-passes an absent OCR recognizer again",
        AIBRAIN_PROBE,
        b'                    ok &= Check(sb, "Windows OCR recognizer available for this machine\'s languages, so the in-context read can be asserted", false);\n',
        b'                    ok &= Check(sb, "Windows OCR recognizer available for this machine\'s languages, so the in-context read can be asserted", false);\n'
        b'                    sb.AppendLine("SKIP: no Windows OCR recognizer for this machine\'s languages");\n',
        "writes no SKIP: line",
    ),

    # RA-086: a private observe-fault continuation grows back beside AiEndpointPolicy.ObserveTaskFailure.
    (
        "a second observe-fault continuation grows back in the AiBrain engine",
        AIBRAIN_FALLBACK,
        b"        private static bool HasImage(IList<ChatMessage> messages)\n",
        b"        private static void ObserveOutcome(Task task)\n"
        b"        {\n"
        b"            task.ContinueWith(delegate(Task finished) { var ignored = finished.Exception; }, CancellationToken.None,\n"
        b"                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);\n"
        b"        }\n\n"
        b"        private static bool HasImage(IList<ChatMessage> messages)\n",
        "one observe-fault continuation",
    ),
    # ---- lane burn/fortunes ----

    # R-029: an invisible U+FEFF literal grows back inside the engine's source, the shape LooksTagged carried
    # (a per-line BOM strip spelled as a raw character between quotes). The scan skips the file's own BOM at
    # byte 0, so this lands one in a comment mid-file.
    (
        "a raw U+FEFF literal grows back inside the Fortunes engine",
        FORTUNE_PROVIDER,
        b"        internal static string DecodeScrapedText(string text)\n",
        b"        internal static string DecodeScrapedText(string text)   // \xef\xbb\xbf\n",
        "no U+FEFF literal",
    ),

    # RA-118 / RA-119: the two supersession checks a smart build makes on its way to a warm, one case each.
    # (a) the check after the dispose it waited on goes: a rebuild scheduled during that wait constructs a
    # picker (a cache.bin parse) it can only drop.
    (
        "a smart build no longer re-checks its generation after the dispose it waited on",
        FORTUNES_MODULE,
        b"            if (SmartBuildSuperseded(generation)) return;\n"
        b"            SmartFortunes built = null;",
        b"            SmartFortunes built = null;",
        "re-checks its generation after the dispose",
    ),
    # (b) the check before the warm goes: a build superseded while constructing warms (a session load and
    # an embed) what its successor's dispose then cancels three seconds later.
    (
        "a superseded smart build goes straight from construction to warm again",
        FORTUNES_MODULE,
        b"                if (SmartBuildSuperseded(generation))\n"
        b"                {\n"
        b"                    try { built.Dispose(); } catch { }\n"
        b"                    return;\n"
        b"                }\n"
        b"                built.Warm(pool);",
        b"                built.Warm(pool);",
        "never goes straight from construction to warm",
    ),
    # RA-123: a shell-execute Process.Start loses its using and leaks a handle per click again.
    (
        "a Fortunes Process.Start stops disposing what it returns",
        FORTUNES_MODULE,
        b"                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true })) { }",
        b"                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });",
        "every Process.Start in the Fortunes module disposes what it returns (sites 1, disposed 0)",
    ),


    # ---- lane burn/agentflow ----

    # RA-021: the tick hands its sweep pass the live switch. A constant here reopens the F026 window (a press
    # landing after the tray switch moved) with every runtime assertion green, because the self-test drives
    # SweepPass with a switch of its own; only the source can see what the TICK passes.
    (
        "burn: the tick hands the sweep pass a constant instead of StillArmed",
        AGENTFLOW_MODULE,
        b"                    var pass = new SweepPass(cdpPort, mayPress, allProjects, similar, _pressBudget, StillArmed);",
        b"                    var pass = new SweepPass(cdpPort, mayPress, allProjects, similar, _pressBudget, () => true);",
        "the tick hands its sweep pass the live StillArmed switch",
    ),
    # R-005: a second, bare writer of the mode grows back beside SetMode. It even mirrors the flag correctly,
    # which is the point: the invariant is about the CHOKE POINT, not about this writer getting it right.
    (
        "burn: the tray toggle writes the mode past SetMode again",
        AGENTFLOW_MODULE,
        b"            SetMode(next ? AgentMode.AutoApprove : AgentMode.Notify);",
        b"            _settings.Set(SettingMode, next ? AgentMode.AutoApprove : AgentMode.Notify); _pressArmed = next;",
        "the mode is written in ONE place, SetMode",
    ),
    # RA-032: the setup line moves back inside Check now's Task.Run, onto a pool thread.
    (
        "burn: Check now renders its setup line inside the Task.Run again",
        AGENTFLOW_MODULE,
        b"            string setup = SetupStatusLine();\n            bool ranInline = RanInline;\n            return Task.Run(() =>\n            {",
        b"            bool ranInline = RanInline;\n            return Task.Run(() =>\n            {\n                string setup = SetupStatusLine();",
        "'Check now' renders the setup line on the calling thread",
    ),
    # RA-042: a stranded <summary> block grows back in AgentFlowPane.cs, documenting whatever follows it.
    (
        "burn: a stacked summary returns to AgentFlowPane.cs",
        AGENTFLOW_PANE,
        b"        /// <summary>\n        /// True until Init has returned, because during Init this module IS NOT YET REGISTERED.",
        b"        /// <summary>\n        /// Installed types rather than the pets currently on screen.\n        /// </summary>\n"
        b"        /// <summary>\n        /// True until Init has returned, because during Init this module IS NOT YET REGISTERED.",
        "no stacked summary in AgentFlowPane.cs",
    ),
    # N-records-02: the detector's recall figure drifts from the one docs/BLOCKED.md settles.
    (
        "burn: the detector's recall figure drifts from the record",
        AGENTFLOW_DETECTOR,
        b"    ///      is 83% (25/30) under the semantics THIS detector ships",
        b"    ///      is 93% (28/30) under the semantics THIS detector ships",
        "BlockedDetector's recall figure matches the one docs/BLOCKED.md settles",
    ),
    # RA-031: Disable caches the report it took BEFORE the write again, so the pane's Status row and
    # 'Check now' keep the old state for the rest of the session in Off mode.
    (
        "burn: Disable caches the pre-write inspection again",
        AGENTFLOW_MODULE,
        b"                SetupReport after = VsCodeSetup.Inspect(overridePath, 250);\n                _setupCache = after;\n                return \"",
        b"                SetupReport after = report;\n                _setupCache = after;\n                return \"",
        "Disable re-inspects argv.json AFTER it writes it",
    ),
    # R-010: StripLineComments goes public again, an entry point for the next offset-based edit.
    (
        "burn: StripLineComments is public API again",
        AGENTFLOW_SETUP,
        b"        internal static string StripLineComments(string text)",
        b"        public static string StripLineComments(string text)",
        "VsCodeSetup.StripLineComments is internal, not public API",
    ),
    # ---- lane burn/host-core ----

    # RA-218 / RA-219: SetDevice goes back to rebuilding only when the stored GUID moves, which is the shape
    # that left the audio on the fallback default after a re-apply of the same device.
    (
        "SetDevice rebuilds only when the stored GUID moves again",
        AUDIO_OUTPUT,
        b"                bool rebuild = g != _deviceId || (_runningDevice.HasValue && _runningDevice.Value != g);\n",
        b"                bool rebuild = g != _deviceId;\n",
        "rebuilds the output when the running device differs",
    ),
    # RA-220: each decode moves back under _sync, one case per method, so a UI-thread Play() waits for it again.
    (
        "PlayOwned decodes under the lock again",
        AUDIO_OUTPUT,
        b"            float[] samples = DecodeModuleAudio(audio);\n",
        b"            float[] samples; lock (_sync) { samples = DecodeModuleAudio(audio); }\n",
        "decode between their two lock blocks",
    ),
    (
        "PlayNotification decodes under the lock again",
        AUDIO_OUTPUT,
        b"            float[] samples = NotificationSound.Resolve(chosen, builtIn);\n",
        b"            float[] samples; lock (_sync) { samples = NotificationSound.Resolve(chosen, builtIn); }\n",
        "decode between their two lock blocks",
    ),
    # RA-231: Start() rotates with the user's keep count again, which before settings load is the field default.
    (
        "the launch rotation uses the keep count again",
        DIAGNOSTIC_LOG,
        b"                    RotateAtLaunchIn(_directory);\n",
        b"                    RotateNoLock();\n",
        "the launch rotation shifts with the ceiling",
    ),
    # RA-267: the launch stops migrating the pre-rename Run entry (the log line is left, unconditional).
    (
        "the pre-rename Run entry waits for a Preferences Apply again",
        PROGRAM,
        b"                if (StartupRegistration.MigrateLegacy())\n",
        b"",
        "the pre-rename Run entry is migrated at launch",
    ),
    # RA-247: the writability probe is disabled in place, so an unwritable root reads as a running instance again.
    (
        "an unwritable data root reads as a running instance again",
        PROGRAM,
        b"                if (!TryProbeDataRootWritable(out dataRootFault))\n",
        b"                if (false && !TryProbeDataRootWritable(out dataRootFault))\n",
        "probes the data root for writability before it is reported",
    ),
    # RA-250: slot 1 is waited on for the whole lease timeout before slot 2 is tried, the shape as it shipped.
    (
        "the second instance waits out slot 1's lease timeout again",
        PROGRAM,
        b"            IDisposable lease = TryAcquireInstanceSlot(1, 0) ?? TryAcquireInstanceSlot(2, 0);\n",
        b"            IDisposable lease = TryAcquireInstanceSlot(1, InstanceSlotTimeoutMilliseconds) ?? TryAcquireInstanceSlot(2, 0);\n",
        "probed with no wait before either is retried",
    ),
    # RA-245: the refusal is disabled in place, so the wipe runs under a live instance again.
    (
        "the factory reset wipes under a running instance again",
        os.path.join(REPO, "src", "dotNet", "FactoryReset.cs"),
        b"                    if (slot1 == null || slot2 == null)\n",
        b"                    if (false)\n",
        "refuses a running instance before its first wipe",
    ),
    # RA-232: the Run entry outlives the reset again.
    (
        "the factory reset leaves the Run entry again",
        os.path.join(REPO, "src", "dotNet", "FactoryReset.cs"),
        b"                ok &= ClearStartupRegistration(log);\n",
        b"",
        "removes the Run entry with the files",
    ),
    # RA-234: the tick catch shows a modal box again, and (separately) stops logging the fault.
    (
        "a tick fault is shown in a modal box again",
        FORMPET,
        b"                if (IsDisposed) return;\n                _tickFaults++;\n",
        b"                if (IsDisposed) return;\n                MessageBox.Show(\"Fatal Error: \" + ex.Message, \"App error\");\n                _tickFaults++;\n",
        "never shown in a modal box",
    ),
    (
        "a tick fault goes unlogged again",
        FORMPET,
        b"                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error,\n                    \"tick failed: \"",
        b"                System.Diagnostics.Debug.WriteLine(\n                    \"tick failed: \"",
        "never shown in a modal box",
    ),
    # RA-235: the kill fade re-seeds at full opacity, and (separately) the kill test is disabled in place so the
    # roll runs on every fade tick again.
    (
        "the kill fade re-seeds at full opacity again",
        FORMPET,
        b"KillFade.Seed(petOpacity)",
        b"KillFade.Seed(1.0)",
        "the kill fade is seeded from the opacity the pet shows",
    ),
    (
        "the kill test is disabled and the roll runs on every fade tick again",
        FORMPET,
        b"                if (CurrentAnimation.ID == Animations.AnimationKill)\n                {\n                    // The kill is tested FIRST (RA-235).",
        b"                if (false)\n                {\n                    // The kill is tested FIRST (RA-235).",
        "the kill fade is seeded from the opacity the pet shows",
    ),
    # RA-236: one grip release moves the pet by the climb's velocity again.
    (
        "a grip release moves the pet by the climb's velocity again",
        FORMPET,
        b"                    ReleaseWindowGrip(true);\n                    bNewAnimation = true;\n                    x = 0; y = 0;\n                }\n                else if (y < 0 && PositionY + ins.Top + y < gripRect.Top)",
        b"                    ReleaseWindowGrip(true);\n                    bNewAnimation = true;\n                }\n                else if (y < 0 && PositionY + ins.Top + y < gripRect.Top)",
        "zeroes the velocity beside its bNewAnimation",
    ),
    # RA-240: the inert title clause comes back.
    (
        "CheckTopWindow admits a window titled sheep again",
        FORMPET,
        b"                                titleBarInfo.rcTitleBar.Bottom >= titleBarInfo.rcTitleBar.Top)\n",
        b"                                (titleBarInfo.rcTitleBar.Bottom >= titleBarInfo.rcTitleBar.Top || sTitle.ToString() == \"sheep\"))\n",
        "no inert title clause",
    ),
    # RA-242: the getter scans on whichever thread asks again.
    (
        "IsFullscreenActive scans on a module's worker thread again",
        STARTUP,
        b"                if (uiContext != null && Thread.CurrentThread.ManagedThreadId != uiThreadId) return _fullscreenActive;   // RA-242\n",
        b"",
        "answers a worker thread from its cache",
    ),
    # RA-243: the host's whitespace guard goes, and (separately) the bubble's own guard is disabled in place.
    (
        "an empty line reaches the bubble again",
        FORMPET,
        b"            if (string.IsNullOrWhiteSpace(text)) return;\n\n            // A companion stood down for a fullscreen window does not open a bubble NOW.",
        b"            // A companion stood down for a fullscreen window does not open a bubble NOW.",
        "refused before the stand-down guard",
    ),
    (
        "the bubble shows an empty line again",
        os.path.join(REPO, "src", "dotNet", "FormSpeech.cs"),
        b"            if (_fullText.Length == 0)\n            {\n                _dismissed = true;\n",
        b"            if (false)\n            {\n                _dismissed = true;\n",
        "refused before the stand-down guard",
    ),
    # RA-238 / RA-266: a summary is stacked over another member's again.
    (
        "a summary is stacked over another again",
        FORMPET,
        b"        /// <summary>Tick faults this pet has logged this session; the third removes it (see Timer1_Tick's catch).</summary>\n        private int _tickFaults;",
        b"        /// <summary>Stacked.</summary>\n        /// <summary>Tick faults this pet has logged this session; the third removes it (see Timer1_Tick's catch).</summary>\n        private int _tickFaults;",
        "no <summary> block is stacked over another",
    ),
    # RA-225: the settings store's literal drifts from the validator's, and (separately) the catalog grows its
    # own literal back.
    (
        "the settings store's pet-XML cap drifts from the validator's",
        os.path.join(REPO, "src", "Portable", "AppSettingsStore.cs"),
        b"        public const int MaximumXmlBytes = 12 * 1024 * 1024;\n",
        b"        public const int MaximumXmlBytes = 16 * 1024 * 1024;\n",
        "the pet-XML cap is one number",
    ),
    (
        "CompanionCatalog grows a third pet-XML literal again",
        os.path.join(REPO, "src", "dotNet", "CompanionCatalog.cs"),
        b"        internal const int MaximumPetXmlBytes = CompanionXmlValidator.MaximumXmlBytes;\n",
        b"        internal const int MaximumPetXmlBytes = 12 * 1024 * 1024;\n",
        "the pet-XML cap is one number",
    ),
    # RA-227 / RA-265: the tray's "" entry reads the persisted id again; PetTypeIdOf ignores the pet's own type.
    (
        "the tray's speech key reads the persisted pet again",
        os.path.join(REPO, "src", "dotNet", "ContextMenus.cs"),
        b"            try { return Program.Mainthread != null ? Program.Mainthread.DefaultTypeId : \"\"; }\n",
        b"            try { return Program.MyData != null ? (Program.MyData.GetActivePetId() ?? \"\") : \"\"; }\n",
        "the running default type has one resolver",
    ),
    (
        "PetTypeIdOf ignores the pet's own type again",
        STARTUP,
        b"            if (pet != null && !string.IsNullOrEmpty(pet.PetTypeId)) return pet.PetTypeId;\n",
        b"",
        "the running default type has one resolver",
    ),
    # RA-226: a same-Xml re-add falls through to the fresh entry again.
    (
        "a same-Xml re-add builds a fresh entry over the borrowed pair again",
        os.path.join(REPO, "src", "dotNet", "CompanionTypeRegistry.cs"),
        b"                    displaced.IsTransient = transient;\n                    return displaced;\n",
        b"                    displaced.IsTransient = transient;\n",
        "returns the existing entry, with its reference count",
    ),
    # RA-228: the base items are not disposed as trees.
    (
        "the tray's base items keep their Images at exit again",
        os.path.join(REPO, "src", "dotNet", "ContextMenus.cs"),
        b"                foreach (ToolStripItem item in baseItems) DisposeItemTree(item);\n",
        b"",
        "disposed as trees before the menu",
    ),
    # RA-230: the smoke's one failure loses its FAIL prefix, so a throwing walk would read as a pass.
    (
        "the desktop-windows smoke stops reporting a throwing walk",
        os.path.join(REPO, "src", "dotNet", "DesktopWindows.cs"),
        b"                sb.AppendLine(\"FAIL live walk threw: \" + ex.Message);\n",
        b"                sb.AppendLine(\"live walk threw: \" + ex.Message);\n",
        "the live walk is a smoke whose one failure is a throw",
    ),
    # RA-264: the reload's exit test loses the shutting-down half, so a reload mid-exit stages again.
    (
        "ReloadPetType stages before it checks the exit",
        STARTUP,
        b"            if (disposed || shuttingDown)\n            {\n                error = disposed ? \"The pet runtime is shutting down.\" : \"The app is shutting down.\";",
        b"            if (disposed)\n            {\n                error = disposed ? \"The pet runtime is shutting down.\" : \"The app is shutting down.\";",
        "every spawn, restage and persist entry point declines",
    ),
    # RA-271: the loader decodes from the string again; and (separately) the commit block keeps the base64.
    (
        "the loader decodes every sound a second time again",
        LOADER_XML_CS,
        b"                        animations.AddSound(node.Id, node.Probability, node.Loop, stagedSoundBytes[i]);\n",
        b"                        animations.AddSound(node.Id, node.Probability, node.Loop, node.Base64);\n",
        "a sound is decoded once per staged type",
    ),
    (
        "the sound base64 is retained in the graph again",
        LOADER_XML_CS,
        b"                        if (sound != null) sound.Base64 = string.Empty;\n",
        b"",
        "a sound is decoded once per staged type",
    ),
    # RA-123 (the OptionsWindow sites): one shell-execute Start drops its Process again.
    (
        "an OptionsWindow Process.Start drops its Process again",
        os.path.join(REPO, "src", "Portable", "Wpf", "OptionsWindow.cs"),
        b"                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo\n                {\n                    FileName = AppUpdateCheck.ReleasesUrl,",
        b"                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo\n                {\n                    FileName = AppUpdateCheck.ReleasesUrl,",
        "OptionsWindow.cs: every Process.Start is using-wrapped",
    ),
    # N-deadcode-07: the fullscreen scan grows a P/Invoke and a filter of its own back.
    (
        "the fullscreen scan grows its own cloaking test again",
        os.path.join(REPO, "src", "dotNet", "FullscreenScan.cs"),
        b"    internal static class FullscreenScan\n    {\n",
        b"    internal static class FullscreenScan\n    {\n        [System.Runtime.InteropServices.DllImport(\"dwmapi.dll\")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);\n        private static bool IsCloaked(IntPtr hWnd) { int c; return DwmGetWindowAttribute(hWnd, 14, out c, 4) == 0 && c != 0; }\n",
        "declares none of its own",
    ),
    # N-burn-tools-03: a validator refusal names the pet-XML figure as prose again. The absence check is the
    # assertion judged first, so the label is the absence's, not the formatted-site count's.
    (
        "a validator refusal hard-codes the pet-XML figure again",
        os.path.join(REPO, "src", "dotNet", "CompanionXmlValidator.cs"),
        b'                        "The local pet must be no larger than " + MebibytesOf(MaximumXmlBytes) + ".");\n',
        b'                        "The local pet must be no larger than 12 MiB.");\n',
        "CompanionXmlValidator.cs carries no literal MiB figure",
    ),
    # N-burn-host-core-01: the drop read's refusal names the figure as prose again.
    (
        "the drop read's refusal hard-codes the pet-XML figure again",
        FORMPET,
        b'                throw new InvalidDataException("Pet XML exceeds the " + CompanionXmlValidator.MebibytesOf(maximumBytes) + " limit.");\n',
        b'                throw new InvalidDataException("Pet XML exceeds the 12 MiB limit.");\n',
        "formats its figure from the bound it enforces",
    ),
    # N-burn-host-core-02: the custom-notification refusal names the figure as prose again.
    (
        "the notification refusal hard-codes the 8 MiB figure again",
        AUDIO_OUTPUT,
        b'                return "that file is over " + CompanionXmlValidator.MebibytesOf(MaximumCustomFileBytes) + "; pick a short notification sound.";\n',
        b'                return "that file is over 8 MiB; pick a short notification sound.";\n',
        "AudioOutput.cs carries no literal MiB figure",
    ),

    # ---- lane fix/deadcode ----

    # F124: the Fortunes module compiles the host's FortunePackLoadPolicy.cs instead of carrying a copy kept
    # equal by a comment. Both directions: the link dropped from the project, and a copy growing back.
    (
        "the Fortunes project stops compiling the host's pack policy",
        FORTUNES_CSPROJ,
        b'    <Compile Include="..\\..\\src\\dotNet\\Ai\\FortunePackLoadPolicy.cs" Link="engine\\FortunePackLoadPolicy.cs" />\n',
        b'',
        "compiles the host's FortunePackLoadPolicy.cs",
    ),
    (
        "a second FortunePackLoadPolicy grows back inside the Fortunes module",
        FORTUNE_PROVIDER,
        b"    internal sealed class FortuneProvider\n",
        b"    internal static class FortunePackLoadPolicy { }\n    internal sealed class FortuneProvider\n",
        "compiles the host's FortunePackLoadPolicy.cs",
    ),

    # F432: the reserved-name array and the loader's switch are compared as sets. Both directions: an element
    # dropped from the array, and a fifth entry point added to the loader.
    (
        "the reserved-name array loses an entry point",
        PETGRAPH_CS,
        b'        internal static readonly string[] ReservedEntryPointNames = { "fall", "drag", "kill", "sync" };',
        b'        internal static readonly string[] ReservedEntryPointNames = { "fall", "drag", "kill" };',
        "names exactly the animation names Xml.cs binds",
    ),
    (
        "the loader gains a fifth entry point the array does not know",
        LOADER_XML_CS,
        b'                    case "sync": animations.AnimationSync = node.Id; break;\n',
        b'                    case "sync": animations.AnimationSync = node.Id; break;\n'
        b'                    case "wave": animations.AnimationSync = node.Id; break;\n',
        "names exactly the animation names Xml.cs binds",
    ),

    # ---- lane feature/modules-update-all ----

    # Update all's restart bypasses the save-then-restart gate: the fifth site asks without the helper, so the
    # two counts come apart.
    (
        "feature/modules-update-all: Update all's restart bypasses the save-then-restart gate",
        PETSPANE_MODULES,
        b"                Program.TryRequestRestartAfterSave(\n"
        b"                    delegate { return staged.Count > 0; },\n"
        b"                    delegate { RestartToApply(staged.Count); });\n",
        b"                if (staged.Count > 0) RestartToApply(staged.Count);\n",
        "the Modules pane's five restart sites all go through",
    ),
    # The re-pointed presence halves of the row's consent-order check: the consent helper stops consulting
    # ModulePermissionConsent, and the shipped download seam drops the catalog's hash.
    (
        "feature/modules-update-all: the consent helper stops consulting ModulePermissionConsent",
        PETSPANE_MODULES,
        b"            added = DesktopAICompanion.Plugins.ModulePermissionConsent.NewlyRequested(\n"
        b"                installed != null ? installed.Permissions : ModulePermissions.None, module.Permissions);\n",
        b"            added = module.Permissions & ~(installed != null ? installed.Permissions : ModulePermissions.None);\n",
        "consults ModulePermissionConsent at all",
    ),
    (
        "feature/modules-update-all: the shipped download seam drops the catalog's hash",
        PETSPANE_MODULES,
        b"RemoteCatalogClient.DownloadVerifiedAsync(m.Url, m.Sha256, RemoteCatalogClient.MaximumModuleBytes, t);",
        b"RemoteCatalogClient.DownloadVerifiedAsync(m.Url, \"\", RemoteCatalogClient.MaximumModuleBytes, t);",
        "still downloads through DownloadVerifiedAsync",
    ),
    # The three invariants under the lane anchor: located, the shipped wiring (WITNESS), and the absence.
    (
        "feature/modules-update-all: an update-path method the seam check slices is renamed away",
        PETSPANE_MODULES,
        b"        private void RestartToApply(int changes)\n",
        b"        private void RestartToApply(int count)\n",
        "ModulesPaneSeams.Live and the five update-path methods",
    ),
    (
        "feature/modules-update-all: the shipped seams count a pane as loaded",
        PETSPANE_MODULES,
        b"                CountsAsLoaded = false,\n",
        b"                CountsAsLoaded = true,\n",
        "WITNESS the shipped Modules pane seams are the real calls",
    ),
    (
        "feature/modules-update-all: the consent question goes straight to a MessageBox again",
        PETSPANE_MODULES,
        b"            return _seams.AskYesNo(\n"
        b"                DesktopAICompanion.Plugins.ModulePermissionConsent.PromptText(module.Name, module.Version, added),\n"
        b"                \"Update \" + (module.Name ?? module.Id) + \"?\",\n"
        b"                MessageBoxImage.Warning);\n",
        b"            return MessageBox.Show(\n"
        b"                DesktopAICompanion.Plugins.ModulePermissionConsent.PromptText(module.Name, module.Version, added),\n"
        b"                \"Update \" + (module.Name ?? module.Id) + \"?\",\n"
        b"                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;\n",
        "update path asks and downloads only through its seams",
    ),
    # Update all's ORDER: a payload fetched ahead of the consent loop.
    (
        "feature/modules-update-all: Update all fetches a payload before its consent loop",
        PETSPANE_MODULES,
        b"                var lines = new string[offers.Count];\n"
        b"                var toFetch = new List<int>();\n",
        b"                var lines = new string[offers.Count];\n"
        b"                if (offers.Count > 0) await StageUpdateAsync(offers[0].Module, CancellationToken.None);\n"
        b"                var toFetch = new List<int>();\n",
        "Update all puts every consent to the user BEFORE its first download",
    ),
    # ---- lane feature/aibrain-standdown ----
    # The Remembrance reason's ORDER invariant (runtime-hardening-selftest.ps1, under this lane's anchor). Each
    # responder's own release-free check, deleted or moved past its Ask: Ask's copy refuses the same turn one call
    # later. The module self-test scored both deletions SURVIVED while that refusal was log-only; it speaks now, and
    # tests/mutate-selftest-guards.py carries the two deletions again, so these are the source-level second pin.
    (
        "aibrain-standdown: the drop leaves the Remembrance check to Ask",
        os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
        b"            if (RemembranceBlockingPhase() != null) return false;\n"
        b"            // allowVision: TRUE, deliberately, and pinned by the module self-test.",
        b"            // allowVision: TRUE, deliberately, and pinned by the module self-test.",
        "the drop and the poke decline for Remembrance BEFORE they ask",
    ),
    (
        "aibrain-standdown: the poke leaves the Remembrance check to Ask",
        os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
        b"            if (RemembranceBlockingPhase() != null) return false;   // and the drop's second reason, likewise\n",
        b"",
        "the drop and the poke decline for Remembrance BEFORE they ask",
    ),
    # The ORDER, not the presence: the check still in the body, after the Ask it should precede. (Source only: this
    # harness builds nothing, so the unreachable statement is never compiled.)
    (
        "aibrain-standdown: the poke checks Remembrance after it has asked",
        os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
        b"            if (RemembranceBlockingPhase() != null) return false;   // and the drop's second reason, likewise\n"
        b"            return Ask(pet, false);",
        b"            return Ask(pet, false);\n"
        b"            if (RemembranceBlockingPhase() != null) return false;   // and the drop's second reason, likewise",
        "the drop and the poke decline for Remembrance BEFORE they ask",
    ),
    # The positive control: the predicate the order check searches for, renamed, must be a failure of its own.
    (
        "aibrain-standdown: the Remembrance predicate is not where the order check looks",
        os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
        b"        private string RemembranceBlockingPhase()",
        b"        private string RemembranceBlockingPhaseNow()",
        "the Remembrance predicate and the AI drop and poke responders were found",
    ),
    # ---- lane feature/catalog-insight ----
    # The wiring BUG-014's fix depends on and no running check can see: the refusing app-version read, ParseBytes as
    # the one door, --catalog-parse-file's verdict, the weekly stamps held back while an entry of their kind was
    # refused, both panes' failure words, the gate's app-parser step and the publish scripts' order.
    (
        "catalog-insight: the launch app-version check reads leniently again",
        os.path.join(REPO, "src", "dotNet", "RemoteCatalog.cs"),
        b"            return ParseAppVersion(SecureDownload.DecodeUtf8(bytes), true);",
        b"            return ParseAppVersion(SecureDownload.DecodeUtf8(bytes), false);",
        "the launch app-version check reads the catalog with the refusing read",
    ),
    (
        "catalog-insight: the shared fetch parses the decoded string directly",
        os.path.join(REPO, "src", "dotNet", "RemoteCatalog.cs"),
        b"            RemoteCatalog parsed = ParseBytes(bytes);",
        b"            RemoteCatalog parsed = Parse(SecureDownload.DecodeUtf8(bytes));",
        "every fetched catalog is read through ParseBytes",
    ),
    (
        "catalog-insight: --catalog-parse-file passes a catalog with entries refused",
        os.path.join(REPO, "src", "dotNet", "Program.cs"),
        b"                        if (parsedCatalog.Rejected.Count == 0)",
        b"                        if (parsedCatalog.Rejected.Count >= 0)",
        "--catalog-parse-file reads the file as a fetch does and passes only",
    ),
    (
        "catalog-insight: the weekly pet check stamps with a companion refused",
        os.path.join(REPO, "src", "dotNet", "StartUp.cs"),
        b"                if (refusedPets == 0) data.SetPetUpdateResult(DateTimeOffset.UtcNow);",
        b"                data.SetPetUpdateResult(DateTimeOffset.UtcNow);",
        "the weekly pet check stamps itself done only when",
    ),
    (
        "catalog-insight: the weekly module check stamps with a module refused",
        os.path.join(REPO, "src", "dotNet", "StartUp.cs"),
        b"                if (refusedModules == 0) data.SetModuleUpdateResult(DateTimeOffset.UtcNow);",
        b"                data.SetModuleUpdateResult(DateTimeOffset.UtcNow);",
        "the weekly module check stamps itself done only when",
    ),
    (
        "catalog-insight: the Modules pane's on-open fetch swallows its failure again",
        PETSPANE_MODULES,
        b"            catch (OperationCanceledException) { }\n"
        b"            catch (Exception ex)\n"
        b"            {\n"
        b"                // A Check press landing meanwhile owns the status line; only this fetch's own failure is said.\n"
        b"                if (!token.IsCancellationRequested && IsLoaded && !_checkInFlight) ShowFetchFailure(ex);\n"
        b"            }",
        b"            catch { }",
        "the Modules pane's on-open fetch and its Check button both say",
    ),
    (
        "catalog-insight: the Modules Check button says couldn't reach for every failure",
        PETSPANE_MODULES,
        b"            catch (Exception ex) { if (IsUp) ShowFetchFailure(ex); }",
        b"            catch (Exception ex) { if (IsUp) _status.Text = \"Couldn't reach the catalog: \" + PaneText.Short(ex.Message); }",
        "the Modules pane's on-open fetch and its Check button both say",
    ),
    (
        "catalog-insight: ShowFetchFailure writes the old line instead of the panel",
        PETSPANE_MODULES,
        b"            ShowProblem(CatalogText.ProblemForFailure(failure, now, _occasion));",
        b"            _status.Text = \"Couldn't reach the catalog: \" + PaneText.Short(failure.Message);",
        "the Modules pane's on-open fetch and its Check button both say",
    ),
    # The F286 invariant's Modules half, re-pointed by this lane at the check-now seam and its shipped wiring.
    (
        "catalog-insight: the Modules check-now drops the shared catalog without refilling it",
        PETSPANE_MODULES,
        b"                RefreshCatalog = delegate (CancellationToken token) { return RemoteCatalogClient.RefreshSharedAsync(token); },",
        b"                RefreshCatalog = delegate (CancellationToken token) { RemoteCatalogClient.InvalidateShared(); return RemoteCatalogClient.FetchAsync(token); },",
        "both panes' check-now buttons refill the shared catalog copy",
    ),
    (
        "catalog-insight: Try again presses beside a check or an Update all",
        PETSPANE_MODULES,
        b"                if (_updatingAll || _checkInFlight) return;\n"
        b"                CheckButton_Click(sender, e);",
        b"                CheckButton_Click(sender, e);",
        "Try again is the Check press",
    ),
    (
        "catalog-insight: Copy details writes the clipboard directly",
        PETSPANE_MODULES,
        b"                _seams.CopyText(_shownProblem.ForCopy());",
        b"                Clipboard.SetText(_shownProblem.ForCopy());",
        "Try again is the Check press",
    ),
    (
        "catalog-insight: the Companions Check line says couldn't reach for every failure",
        PETSPANE,
        b"                _status.Text = CatalogText.FetchFailed(ex, DateTime.Now, CheckButtonText);",
        b"                _status.Text = \"Couldn't reach the catalog: \" + PaneText.Short(ex.Message);",
        "neither pane says \"Couldn't reach the catalog\" for every failure",
    ),
    (
        "catalog-insight: the unreachable case loses its words",
        os.path.join(REPO, "src", "dotNet", "RemoteCatalog.cs"),
        b"            return \"\xe2\x9c\x97 Couldn't reach the catalog at \" + at + \": \"",
        b"            return \"\xe2\x9c\x97 The catalog could not be fetched at \" + at + \": \"",
        "WITNESS CatalogText.FetchFailed still says",
    ),
    (
        "catalog-insight: the integrity check no longer runs the app's parser",
        os.path.join(REPO, "packaging", "Test-ContentCatalogIntegrity.ps1"),
        b"$appParse = Invoke-AppCatalogParse -RepoRoot $RepoRoot -CatalogPath $catalogPath\n",
        b"$appParse = [pscustomobject]@{ Passed = $true; ExitCode = 0; Lines = @('catalog_parse=PASS (not run)') }\n",
        "the gate runs the app's own parser over catalog.json",
    ),
    (
        "catalog-insight: the integrity check's controls stop trying 1025",
        os.path.join(REPO, "packaging", "Test-ContentCatalogIntegrity.ps1"),
        b"    foreach ($controlLength in @(1025, 1024)) {",
        b"    foreach ($controlLength in @(1024)) {",
        "the gate runs the app's own parser over catalog.json",
    ),
    # The ORDER: the integrity step before the build that makes the parser it runs. (The gate is not run here.)
    (
        "catalog-insight: the gate checks the catalog before it has built the parser",
        os.path.join(REPO, "tests", "run-gate.ps1"),
        b"    try { & (Join-Path $repoRoot 'build.ps1') @buildParams }",
        b"    try { & (Join-Path $repoRoot 'packaging\\Test-ContentCatalogIntegrity.ps1') } catch { }\n"
        b"    try { & (Join-Path $repoRoot 'build.ps1') @buildParams }",
        "the gate runs the app's own parser over catalog.json",
    ),
    (
        "catalog-insight: the generator copies the catalog into place before judging it",
        os.path.join(REPO, "packaging", "New-ContentCatalog.ps1"),
        b"    $candidateParse = Invoke-AppCatalogParse -RepoRoot $RepoRoot -CatalogPath $candidatePath -BuildIfStale\n",
        b"    [IO.File]::Copy($candidatePath, $OutputPath, $true)\n"
        b"    $candidateParse = Invoke-AppCatalogParse -RepoRoot $RepoRoot -CatalogPath $candidatePath -BuildIfStale\n",
        "New-ContentCatalog.ps1 has the app's parser judge",
    ),
    (
        "catalog-insight: the generator writes catalog.json directly again",
        os.path.join(REPO, "packaging", "New-ContentCatalog.ps1"),
        b"    [IO.File]::WriteAllText(\n        $candidatePath,\n",
        b"    [IO.File]::WriteAllText(\n        $OutputPath,\n",
        "New-ContentCatalog.ps1 has the app's parser judge",
    ),
    (
        "catalog-insight: the generator judges the module entries after hashing",
        os.path.join(REPO, "packaging", "New-ContentCatalog.ps1"),
        b"$preflightModulesJson = Join-Path (Join-Path $RepoRoot 'modules-dist') 'modules.json'\n",
        b"$null = Get-CatalogAsset $RepoRoot 'packs/collections.json' (Join-Path $RepoRoot 'packs\\collections.json')\n"
        b"$preflightModulesJson = Join-Path (Join-Path $RepoRoot 'modules-dist') 'modules.json'\n",
        "New-ContentCatalog.ps1 has the app's parser judge",
    ),
    (
        "catalog-insight: the generator's module preflight is gone",
        os.path.join(REPO, "packaging", "New-ContentCatalog.ps1"),
        b"        $preflight = Test-ModuleEntriesWithAppParser -RepoRoot $RepoRoot -Entries $preflightEntries -Branch $Branch -BuildIfStale\n",
        b"        $preflight = [pscustomobject]@{ Passed = $true; Lines = @() }\n",
        "the generator's preflight, first hash, judgement and copy were located",
    ),
    (
        "catalog-insight: the publish builds before it judges the entry",
        os.path.join(REPO, "packaging", "New-ModulePublish.ps1"),
        b"$preflightEntries = @()\n",
        b"if ($false) { & dotnet build $csproj.FullName }\n"
        b"$preflightEntries = @()\n",
        "New-ModulePublish.ps1 has the app's parser judge",
    ),
    (
        "catalog-insight: the publish ignores the parser's refusal",
        os.path.join(REPO, "packaging", "New-ModulePublish.ps1"),
        b"    if (-not $preflight.Passed) {\n",
        b"    if ($false) {\n",
        "New-ModulePublish.ps1 has the app's parser judge",
    ),

    # ---- lane feature/cli-backend ----
    # The coding-agent CLI runner's source invariants (runtime-hardening-selftest.ps1, under this lane's anchor): both
    # modules link the one copy, the real child's drain-before-feed order and its kill, and the refused alternatives.
    (
        "cli-backend: AI Brain stops linking the shared runner",
        os.path.join(REPO, "modules", "AiBrain", "AiBrain.csproj"),
        b'    <Compile Include="..\\..\\shared\\CodingAgentCli\\CodingAgentCli.cs" Link="cli\\CodingAgentCli.cs" />\n',
        b"",
        "AI Brain and Remembrance both compile the shared CLI runner by link",
    ),
    (
        "cli-backend: Remembrance drops the runner's trust rules",
        os.path.join(REPO, "modules", "Remembrance", "Remembrance.csproj"),
        b'    <Compile Include="..\\AiBrain\\engine\\AiExecutablePolicy.cs" Link="cli\\AiExecutablePolicy.cs" />\n',
        b"",
        "AI Brain and Remembrance both compile the shared CLI runner by link",
    ),
    (
        "cli-backend: the real runner is not where the drain check looks",
        os.path.join(REPO, "shared", "CodingAgentCli", "CodingAgentCli.cs"),
        b"        internal static async Task<CliProcessResult> RunRealProcessAsync(\n",
        b"        internal static async Task<CliProcessResult> RunTheRealProcessAsync(\n",
        "RunRealProcessAsync could be sliced out for its drain-order checks",
    ),
    (
        "cli-backend: the feed is awaited before the exit wait",
        os.path.join(REPO, "shared", "CodingAgentCli", "CodingAgentCli.cs"),
        b"                Task feed = FeedAsync(process, standardInput);\n",
        b"                Task feed = FeedAsync(process, standardInput);\n"
        b"                await feed.ConfigureAwait(false);\n",
        "the CLI runner never awaits its stdin feed ahead of the exit wait",
    ),
    (
        "cli-backend: stdin is fed before the readers start",
        os.path.join(REPO, "shared", "CodingAgentCli", "CodingAgentCli.cs"),
        b"                var output = new BoundedPump(process.StandardOutput, MaximumOutputCharacters);\n"
        b"                var error = new BoundedPump(process.StandardError, MaximumErrorCharacters);\n"
        b"                Task feed = FeedAsync(process, standardInput);\n",
        b"                Task feed = FeedAsync(process, standardInput);\n"
        b"                var output = new BoundedPump(process.StandardOutput, MaximumOutputCharacters);\n"
        b"                var error = new BoundedPump(process.StandardError, MaximumErrorCharacters);\n",
        "the CLI runner drains both streams, then feeds stdin",
    ),
    (
        "cli-backend: a cancelled wait leaves the child running",
        os.path.join(REPO, "shared", "CodingAgentCli", "CodingAgentCli.cs"),
        b"                    try { process.Kill(true); } catch { }\n",
        b"",
        "the CLI runner drains both streams, then feeds stdin",
    ),
    (
        "cli-backend: the pick no longer asks Codex for its catalog",
        os.path.join(REPO, "shared", "CodingAgentCli", "CodingAgentCli.cs"),
        b'new[] { "debug", "models" }',
        b'new[] { "debug", "model-list" }',
        "WITNESS Codex's model is picked from its own catalog call",
    ),
    (
        "cli-backend: the pick reads Codex's shared cache file",
        os.path.join(REPO, "shared", "CodingAgentCli", "CodingAgentCli.cs"),
        b"            string fingerprint = Fingerprint(install.Executable);\n",
        b"            string fingerprint = Fingerprint(install.Executable);\n"
        b'            string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");\n',
        "no code reads models_cache.json, touches auth.json or sets a CODEX_HOME of its own",
    ),
    (
        "cli-backend: Codex gets a CODEX_HOME of the module's",
        os.path.join(REPO, "shared", "CodingAgentCli", "CodingAgentCli.cs"),
        b'                startInfo.Environment["CODEX_MANAGED_BY_NPM"] = "1";\n',
        b'                startInfo.Environment["CODEX_MANAGED_BY_NPM"] = "1";\n'
        b'                startInfo.Environment["CODEX_HOME"] = workingDirectory;\n',
        "no code reads models_cache.json, touches auth.json or sets a CODEX_HOME of its own",
    ),
    (
        "cli-backend: Codex's credentials are copied in",
        os.path.join(REPO, "shared", "CodingAgentCli", "CodingAgentCli.cs"),
        b"            string fingerprint = Fingerprint(install.Executable);\n",
        b"            string fingerprint = Fingerprint(install.Executable);\n"
        b'            string credentials = Path.Combine(_scratchRoot ?? "", "auth.json");\n',
        "no code reads models_cache.json, touches auth.json or sets a CODEX_HOME of its own",
    ),

    # AI Brain's Status card (aibrain 1.3.0): AskCoreAsync records every started turn after the session answers and
    # before a silent return, and the failure it shows is a class, never an exception's message.
    (
        "cli-backend: AskCoreAsync is not where the record check looks",
        os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
        b"        private async Task AskCoreAsync(AiSessionManager session,",
        b"        private async Task AskTheCoreAsync(AiSessionManager session,",
        "AskCoreAsync could be sliced out for its Status card order check",
    ),
    (
        "cli-backend: a silent turn returns before it is recorded",
        os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
        b"            RecordRemark(DateTime.Now, clock.ElapsedMilliseconds,\n"
        b'                r != null && !string.IsNullOrWhiteSpace(r.Text) ? null : (session.LastAskFailure ?? "nothing came back"));\n'
        b"            if (r == null || string.IsNullOrWhiteSpace(r.Text)) return;\n",
        b"            if (r == null || string.IsNullOrWhiteSpace(r.Text)) return;\n"
        b"            RecordRemark(DateTime.Now, clock.ElapsedMilliseconds,\n"
        b'                r != null && !string.IsNullOrWhiteSpace(r.Text) ? null : (session.LastAskFailure ?? "nothing came back"));\n',
        "every started AI Brain turn is recorded for the Status card",
    ),
    (
        "cli-backend: an ask keeps the previous ask's failure",
        os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
        b"            LastFailure = null;\n",
        b"",
        "AskAboutScreenAsync could be sliced out, and clears its last failure",
    ),
    (
        "cli-backend: the Status card shows an exception's message",
        os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
        b"                    : DescribeError(ex);\n",
        b"                    : ex.Message;\n",
        "AI Brain's last failure, which the Status card shows, is a class or a category",
    ),

    # ---- lane feature/fortunes-index ----
    # The invariants under this lane's anchor in runtime-hardening-selftest.ps1 (fortunes 1.1.0). Names carry the
    # "fortunes-index:" prefix so `--only=fortunes-index:` runs the lane. Source only: this harness builds nothing.

    # Download lets the folder watcher back in BEFORE its own rebuild: the ORDER half (the release is still in the
    # finally, so a presence check would pass).
    (
        "fortunes-index: Download releases the folder watcher before its own rebuild",
        FORTUNES_MODULE,
        b"                await RebuildEngineAsync(IndexChange.Packs);   // the new packs join the pool (and the smart index) right away\n",
        b"                ownWrites.Dispose();\n"
        b"                await RebuildEngineAsync(IndexChange.Packs);   // the new packs join the pool (and the smart index) right away\n",
        "Download holds the folder watcher off from before its first write until after its own rebuild",
    ),
    # Import stops taking the bracket at all.
    (
        "fortunes-index: Import no longer holds the folder watcher off",
        FORTUNES_MODULE,
        b"                ownWrites = BeginOwnFolderWrites();\n",
        b"",
        "Import holds the folder watcher off from before its import until after its own rebuild",
    ),
    # The shutdown gate goes from ScheduleSmartPicker, the one place every build passes.
    (
        "fortunes-index: ScheduleSmartPicker schedules a build after Shutdown again",
        FORTUNES_MODULE,
        b"                if (_shuttingDown) return;\n",
        b"",
        "no smart build is scheduled once Shutdown has begun",
    ),
    # ...or Shutdown stops raising it.
    (
        "fortunes-index: Shutdown no longer raises the build gate",
        FORTUNES_MODULE,
        b"                _shuttingDown = true;\n",
        b"",
        "no smart build is scheduled once Shutdown has begun",
    ),
    # The watcher's pattern drifts from the loader's.
    (
        "fortunes-index: the folder watcher watches a different pattern from the loader",
        FORTUNES_MODULE,
        b"                watcher = new FileSystemWatcher(FortunePaths.FortunesDirPath, \"*.txt\")",
        b"                watcher = new FileSystemWatcher(FortunePaths.FortunesDirPath, \"*.fortune\")",
        "watches exactly the files the loader fingerprints",
    ),
    # ...or descends into subfolders the loader never reads.
    (
        "fortunes-index: the folder watcher watches subfolders the loader never reads",
        FORTUNES_MODULE,
        b"                    IncludeSubdirectories = false,",
        b"                    IncludeSubdirectories = true,",
        "watches exactly the files the loader fingerprints",
    ),
    # N-catalog-insight-05: the catalog check's catch goes back to one "Couldn't reach" for every error.
    (
        "fortunes-index: a failed catalog check reads 'Couldn't reach' for every cause again",
        FORTUNES_MODULE,
        b"            catch (Exception ex) { return CatalogFailureText(ex); }",
        b"            catch (Exception ex) { return \"\xe2\x9c\x97 Couldn't reach the catalog: \" + Short(ex.Message); }",
        "a failed catalog check is worded by its cause",
    ),
)


# The ONE baseline failure a branch is allowed to carry, and the reason it is safe to score
# against. The self-test compares SMOKETEST.md's "N source invariants" figure to its own
# assertion count, so a branch that ADDS an assertion cannot be green until the doc is updated --
# which happens at the merge, in a file the branch may not own.
#
# What makes tolerating it rigorous rather than convenient: the self-test runs under
# ErrorActionPreference = Stop and Assert-True throws, so it aborts at its FIRST failing
# assertion -- and the doc-count invariants are its LAST two. Seeing this text in the output is
# therefore proof that every assertion before it passed. A mutated assertion aborts the run
# earlier and prints its own text instead of this one, so the two cannot be confused.
DOC_COUNT_DRIFT = "source-invariant count matches this file"


def saw_doc_count_drift(out):
    return any(DOC_COUNT_DRIFT in line and not line.strip().startswith("PASS:")
               for line in out.splitlines())



def line_ending_variant(base, old, new):
    """Pick the (old, new) pair whose line endings match the FILE being mutated.

    Every pattern in this file is written with LF. Several target files are CRLF in the working tree,
    and a CRLF file cannot contain an LF pattern, so those cases printed "NO-OP (pattern matched 0
    times)" and covered nothing at all -- a silent loss of coverage, which the release checklist is
    explicit is worse than a failure because it looks like a result.

    Measured 2026-09-25: this accounted for ALL SEVEN no-op cases across this harness and its sibling
    (one here, six in mutate-agentflow.py). Every one of them was read as "the source moved"; none of
    them had. CompanionsPaneControl.cs, for instance, is 1014 CRLF lines and 0 bare LF.

    Restoring is unaffected either way: the loops below write back the ORIGINAL bytes they read, so a
    file's line endings are never rewritten by a mutation run.
    """
    if base.count(old) == 1:
        return old, new
    as_crlf = lambda b: b.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    crlf_old, crlf_new = as_crlf(old), as_crlf(new)
    if base.count(crlf_old) == 1:
        return crlf_old, crlf_new
    return old, new

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", default=None)
    args = parser.parse_args()
    # Selected BEFORE the baseline run, and an empty selection is refused where it costs nothing (RA-353,
    # N-petstudio-01): a mistyped substring would otherwise run the baseline and print "0/0 fired." with
    # exit 0. Case-insensitive, like tests/mutate-selftest-guards.py.
    cases = [c for c in CASES if args.only is None or args.only.lower() in c[0].lower()]
    if not cases:
        print("no case matched --only=%s" % args.only)
        return 2

    print("baseline: the hardening self-test must pass before anything is scored")
    code, out = run()
    degraded = code != 0 and saw_doc_count_drift(out)
    if code != 0 and not degraded:
        print("BASELINE NOT GREEN, refusing to score.")
        print(out[-1200:])
        return 2
    if degraded:
        print("  *** BASELINE DEGRADED: SMOKETEST.md's invariant count is not updated yet. ***")
        print("  Scoring continues: the self-test aborts at its FIRST failure and the doc count is")
        print("  its LAST assertion, so everything this harness scores ran and passed.")
    print("  baseline %s (%d assertions)\n"
          % ("DEGRADED" if degraded else "PASS", out.count("PASS:")))

    print("mutation run: %d case(s)%s\n" % (len(cases), "" if args.only is None else " matching --only=%s" % args.only))
    fired = 0
    for name, path, old, new, expect in cases:
        base = read(path)
        # `old` may be a compiled regex. That exists for one reason: a case whose target is a number
        # the suite is SUPPOSED to change (the documented assertion count) would otherwise go no-op
        # the first time an assertion is added, print "matched 0 times", and quietly stop covering
        # anything -- which is exactly the rot mutate-diagnostics.py was carrying.
        if hasattr(old, "subn"):
            mutant, count = old.subn(new, base)
        else:
            old_v, new_v = line_ending_variant(base, old, new)
            count = base.count(old_v)
            mutant = base.replace(old_v, new_v)
        if count != 1:
            print("  %-46s NO-OP (pattern matched %d times)" % (name, count))
            continue
        write(path, mutant)
        try:
            code, out = run()
        finally:
            write(path, base)

        if code == 0:
            print("  %-46s SURVIVED -- the assertion is still vacuous" % name)
            continue
        # Do NOT key on the word "failed.". PowerShell renders a thrown error across several lines
        # and splits the message from that word, so the assertion text and "failed." end up on
        # DIFFERENT lines -- which made this harness report WRONG for a case that fired correctly.
        # It also puts `throw "$Name failed."` (the helper's own source) in the rendering, which a
        # naive match picks up; mutate-diagnostics.py records that half of the trap.
        #
        # A non-zero exit means this mutation broke something -- unless the baseline was degraded,
        # in which case the doc-count failure is there whatever the mutation did. It broke the RIGHT
        # thing when the expected assertion text appears anywhere in the output on a line that is
        # not a PASS.
        bad = [l.strip() for l in out.splitlines()
               if expect in l and not l.strip().startswith("PASS:")]
        if bad:
            fired += 1
            print("  %-46s FIRED" % name)
            print("        %s" % bad[0][:150])
        elif degraded and saw_doc_count_drift(out):
            # The run reached the LAST assertion, so nothing this mutation touched was noticed.
            # Reported as SURVIVED and not as WRONG: with a degraded baseline a non-zero exit is
            # not evidence of anything on its own, and calling it "failed on something else" would
            # read as a harness fault rather than as a vacuous assertion.
            print("  %-46s SURVIVED -- only the baseline doc-count failure" % name)
        else:
            print("  %-46s WRONG -- failed on something else" % name)
            other = [l.strip() for l in out.splitlines() if "failed" in l][:2]
            for line in other[:2]:
                print("        %s" % line[:150])

    print("\n%d/%d fired." % (fired, len(cases)))
    return 0 if cases and fired == len(cases) else 1


if __name__ == "__main__":
    sys.exit(main())
