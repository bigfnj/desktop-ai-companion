#!/usr/bin/env python3
"""Prove the three replacements for vacuous assertions can actually fail.

Replacing an assertion that cannot fail with another that cannot fail is the failure mode this
whole exercise is about, so each replacement gets its own mutation. Byte-exact restore.
"""

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
WEBLINKS = os.path.join(REPO, "src", "Portable", "WebLinks.cs")


def read(p):
    with io.open(p, "rb") as h:
        return h.read()


def write(p, data):
    with io.open(p, "wb") as h:
        h.write(data)


def run():
    proc = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", HARDENING],
        capture_output=True, text=True, timeout=900)
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
    (
        "the module payload is downloaded BEFORE the permission prompt",
        PETSPANE_MODULES,
        b"            ModulePermissions added = DesktopAICompanion.Plugins.ModulePermissionConsent.NewlyRequested(",
        b"            byte[] prefetched = await RemoteCatalogClient.DownloadVerifiedAsync(\n"
        b"                module.Url, module.Sha256, RemoteCatalogClient.MaximumModuleBytes, _netCts.Token);\n"
        b"            ModulePermissions added = DesktopAICompanion.Plugins.ModulePermissionConsent.NewlyRequested(",
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

    # F287: the Fortunes module's pack file cap drifts below the catalog entry cap (the exact 128-vs-512
    # regression the --catalog-selftest check describes and cannot see, because it reads the host copy).
    (
        "the Fortunes module's pack file cap drops to 128",
        FORTUNE_PROVIDER,
        b"        public const int MaximumFiles = 512;",
        b"        public const int MaximumFiles = 128;",
        "equals the host copy",
    ),
    # F149: the 'Rebuild smart index' guard compares the indexed pool with itself again.
    (
        "'Rebuild smart index' compares the pool with itself again",
        FORTUNES_MODULE,
        b"                    FortuneProvider fresh = new FortuneProvider(LoadFortuneSettings(_host));\n"
        b"                    if (complete && _indexedSignature == PoolSignature(fresh.PoolEntries()))",
        b"                    if (complete && _indexedSignature == PoolSignature(provider.PoolEntries()))",
        "compares the index against a FRESHLY built pool",
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


    # ---- lane fix/host ----

    # F278: the exclusion set drops the speech bubble again -- a TopMost window that decides a monitor as clear.
    (
        "the fullscreen exclusion set forgets the speech bubble",
        FORMPET,
        b"            if (bubble != null && !bubble.IsDisposed && bubble.IsHandleCreated) into.Add(bubble.Handle);\n",
        b"",
        "gathers the children recursively AND the speech bubble",
    ),
    # F270: the screen-position test comes back in place of the rect-shape test.
    (
        "CheckTopWindow rejects title bars above screen y=0 again",
        FORMPET,
        b"(titleBarInfo.rcTitleBar.Bottom >= titleBarInfo.rcTitleBar.Top || sTitle.ToString() == \"sheep\"))",
        b"(titleBarInfo.rcTitleBar.Bottom >= 0 || sTitle.ToString() == \"sheep\"))",
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
    # the old pose again.
    (
        "the degenerate-rect grip release skips bNewAnimation",
        FORMPET,
        b"                    ReleaseWindowGrip(true);\n"
        b"                    bNewAnimation = true;\n"
        b"                }\n"
        b"                else if (windowGrip == WindowGrip.Bottom)",
        b"                    ReleaseWindowGrip(true);\n"
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
    # F310: the module-facing getter stops stamping the attempt.
    (
        "IsFullscreenActive stops stamping the scan attempt",
        STARTUP,
        b"                _fullscreenScanUtc = DateTime.UtcNow;   // the ATTEMPT, stamped as the stand-down does (F310)\n"
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
        "fitted to the primary work area",
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
    # F335: the volatile goes.
    (
        "the shared catalog cache loses its volatile",
        os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs"),
        b"        private volatile RemoteCatalog _catalogCache;",
        b"        private RemoteCatalog _catalogCache;",
        "volatile publish",
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


    # ---- lane fix/settings ----


    # ---- lane fix/scripts ----


    # ---- lane fix/deadcode ----
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

    fired = 0
    for name, path, old, new, expect in CASES:
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

    print("\n%d/%d fired." % (fired, len(CASES)))
    return 0 if fired == len(CASES) else 1


if __name__ == "__main__":
    sys.exit(main())
