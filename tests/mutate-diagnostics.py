"""Mutation test for the diagnostic-log guards.

A guard nobody has seen fail is a guess. Each case below breaks exactly one thing the new logging
module promises, runs the check that claims to cover it, and requires that check to FAIL naming the
right assertion. Anything that survives its mutation is not a guard and should not ship.

Two rules this harness exists to enforce, both learned the hard way in this repo:
  * The baseline is the WORKING TREE, captured in memory before anything is touched, and restored
    from that copy, byte for byte. Restoring with `git checkout --` destroys uncommitted work, and a
    previous run of this harness did exactly that -- every "FIRED" it printed was measured against a
    file whose code under test no longer existed.
  * Every mutation asserts its target text is PRESENT first. A pattern that silently matches nothing
    is a no-op, and a no-op mutation always looks like a passing test.

Usage:  %TOOLBOX_PYTHON% tests/mutate-diagnostics.py [--fast]
        --fast skips the cases that need a rebuild (source-invariant cases only).
"""

import io
import os
import shutil
import subprocess
import sys
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "build", "DesktopAICompanionPortable", "bin", "Release", "x64",
                   "DesktopAICompanion.exe")
# The marker lives in a per-run TEMP that main() creates and hands to the exe through its environment
# (Path.GetTempPath() reads TMP, then TEMP). With the fixed name under the shared per-user %TEMP%, a gate
# in another checkout running --wpf-options-selftest could overwrite this harness's verdict between the
# exe's write and the read, or read a mutant's verdict as its own (F418). All three are set in main().
RUN_TEMP = None
WPF_RESULT = None
CHILD_ENV = None

OPTIONS = "src/Portable/Wpf/OptionsShell.cs"
OPTIONSWINDOW = "src/Portable/Wpf/OptionsWindow.cs"
DIAG = "src/dotNet/DiagnosticLog.cs"
STARTUP = "src/dotNet/StartUp.cs"
SETTINGS = "src/Portable/AppSettingsStore.cs"
PROCICON = "src/dotNet/ProcessIcon.cs"

# (name, file, find, replace, checker, expected fragment of the failing assertion)
CASES = [
    # --- source invariants: the gate reads the file, so no rebuild needed --------------------
    ("AddDebugInfo logs after the window instead of before", STARTUP,
     "            DiagnosticLog.Write(DiagnosticLog.Infer(text), type.ToString(), null, text);\n"
     "            ShowInDebugWindow(type, text);",
     "            ShowInDebugWindow(type, text);\n"
     "            DiagnosticLog.Write(DiagnosticLog.Infer(text), type.ToString(), null, text);",
     "gate", "AddDebugInfo writes to the log first"),

    ("a guard clause returns before the log write", STARTUP,
     "            DiagnosticLog.Write(DiagnosticLog.Infer(text), type.ToString(), null, text);",
     "            if (text == null) return;\n"
     "            DiagnosticLog.Write(DiagnosticLog.Infer(text), type.ToString(), null, text);",
     "gate", "AddDebugInfo writes to the log first"),

    ("rotation keeps only the current run", SETTINGS,
     "DiagnosticLogKeep = 2,", "DiagnosticLogKeep = 1,",
     "gate", "keeps at least the previous run"),

    ("rotation truncates instead of shifting", DIAG,
     "File.Move(Current(i - 1), Current(i));", "File.Delete(Current(i - 1));",
     "gate", "keeps at least the previous run"),

    # This case was STALE and had been a no-op since 1.1.1: it looked for
    # '"tray icon set: success="', which is the weak line that hid BUG-001 and was replaced by
    # 'noThrow=' plus 'shellHasIt=' when the invariant was strengthened. The harness printed
    # "pattern matched 0 times", and nothing ran it for long enough to read that -- so a
    # mutation suite reported 19 cases while covering 18 of them. The mutation now drops the
    # SHELL's verdict from the line, which is the half the strengthened invariant exists for:
    # noThrow alone cannot tell a dropped NIM_ADD from a working one.
    ("the tray line stops carrying the shell's verdict", PROCICON,
     '" shellHasIt=" + (shellHasIt.HasValue ? shellHasIt.Value.ToString() : "unknown") +',
     '" shell=" + (shellHasIt.HasValue ? shellHasIt.Value.ToString() : "unknown") +',
     "gate", "SetIcon records the SHELL"),

    # SettingField.Min/Max are honoured as of 1.1.5, after an audit found them read by nothing.
    # Both directions: a clamp that never fires leaves the ABI members decorative again, and one
    # that fires on unparseable text invents a value the user never typed.
    ("the declared Int bounds stop being applied", OPTIONSWINDOW,
     "            if (f.Min == f.Max) return text;",
     "            if (f.Min != f.Max) return text;",
     "wpf", "a value above Max is clamped down"),

    ("unparseable text is clamped into the range instead of being left alone", OPTIONSWINDOW,
     "            if (!int.TryParse((text ?? \"\").Trim(), out value)) return text;",
     "            if (!int.TryParse((text ?? \"\").Trim(), out value)) value = f.Min;",
     "wpf", "left for the module's own fallback"),

    # --- behaviour: these need the binary rebuilt -------------------------------------------
    ("the pane reads the muted list as an ALLOW list", OPTIONS,
     "                if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return false;",
     "                if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return true;",
     "wpf", "a muted category reads as OFF"),

    ("the pane shows Animation ticked on a fresh install", OPTIONS,
     "            return c != DesktopAICompanion.LogCategory.Animation;",
     "            return true;",
     "wpf", "Animation defaults to OFF"),

    ("the pane ignores a muted module", OPTIONS,
     "                if (string.Equals(raw.Trim(), moduleId, StringComparison.OrdinalIgnoreCase)) return false;",
     "                if (string.Equals(raw.Trim(), moduleId, StringComparison.OrdinalIgnoreCase)) return true;",
     "wpf", "a muted module reads as OFF"),

    ("Animation is no longer muted by default", DIAG,
     "                if (!WasNamed(mutedCategories, LogCategory.Animation) &&\n"
     "                    !WasUnmuted(mutedCategories, LogCategory.Animation))\n"
     "                    _mutedCategories.Add(LogCategory.Animation);",
     "                { }",
     "wpf", "Animation is still muted by default"),

    ("Animation can never be opted back in", DIAG,
     "                if (!WasNamed(mutedCategories, LogCategory.Animation) &&\n"
     "                    !WasUnmuted(mutedCategories, LogCategory.Animation))\n"
     "                    _mutedCategories.Add(LogCategory.Animation);",
     "                _mutedCategories.Add(LogCategory.Animation);",
     "wpf", "Animation can be opted back IN"),

    ("the log ignores the per-module mute", DIAG,
     "                if (!string.IsNullOrEmpty(moduleId) && _mutedModules.Contains(moduleId)) return false;\n"
     "                return true;",
     "                return true;",
     "wpf", "a muted module's own lines stop being recorded"),

    # Write() must not carry its own copy of the filter. IsEnabled is the only thing the pane self-test can
    # reach, so a second, drifting copy inside Write would decide what actually gets recorded while every
    # assertion kept passing -- measured, not assumed: with the copy in place, deleting Write's per-module
    # check SURVIVED the whole suite. These two cases guard the delegation that replaced it.
    ("Write stops filtering at all", DIAG,
     "                if (!IsEnabled(category, moduleId)) return;\n",
     "",
     "gate", "keeps no second copy of the rules"),

    ("Write grows a private copy of the rules back", DIAG,
     "                if (!IsEnabled(category, moduleId)) return;",
     "                if (_mutedCategories.Contains(category)) return;",
     "gate", "keeps no second copy of the rules"),

    # The classifier. None of this was covered until a real log was read, so these cases exist to prove
    # the new assertions actually bite rather than restating the prefix table back to itself.
    ("the level column exactly equals the longest level name again", DIAG,
     'level ?? "info").PadRight(9)', 'level ?? "info").PadRight(7)',
     "gate", "level column is wider than the longest level name"),

    ("sound staging falls back to App", DIAG,
     '            new KeyValuePair<string, LogCategory>("adding sound", LogCategory.Audio),\n',
     "",
     "wpf", "sound staging is Audio, not App"),

    ("animation dead-ends fall back to App", DIAG,
     '            new KeyValuePair<string, LogCategory>("no next animation", LogCategory.Animation),\n',
     "",
     "wpf", "a dead-end in the animation graph is Animation"),

    ("child teardown falls back to App", DIAG,
     '            new KeyValuePair<string, LogCategory>("removing child", LogCategory.Animation),\n',
     "",
     "wpf", "with Animation muted, animation churn is genuinely not recorded"),

    ("companion spawn falls back to App", DIAG,
     '            new KeyValuePair<string, LogCategory>("new pet", LogCategory.Companions),\n',
     "",
     "wpf", "spawning a companion is Companions, not App"),

    # The near-miss, in the other direction: a load failure swept into the muted Animation bucket
    # disappears exactly when someone needs it.
    ("a companion with no animations gets muted along with the churn", DIAG,
     '            new KeyValuePair<string, LogCategory>("no animations for", LogCategory.Companions),',
     '            new KeyValuePair<string, LogCategory>("no animations for", LogCategory.Animation),',
     "wpf", "a companion that has no animations still reports itself"),

    ("the master switch does nothing", DIAG,
     "                if (!_enabled) return false;\n"
     "                if (_mutedCategories.Contains(category)) return false;",
     "                if (_mutedCategories.Contains(category)) return false;",
     "wpf", "the master switch turns everything off"),
    # ---- 2026-09-29 audit campaign: each lane adds its cases directly under its own anchor so parallel
    # branches do not touch the same lines. Comments inside the literal are fine for Python.
    # ---- lane fix/gates ----

    # F315: section 4 of --wpf-options-selftest used to call the test's OWN InvokeAsync delegate and compare
    # the result with the literal that delegate returns, so no PaneView code ran between the call and the
    # check. It now clicks the rendered button. These two put back the regressions that used to pass: a
    # Click handler that runs nothing, and an action row that is never rendered for an InvokeAsync-only action.
    # `await Task.FromResult` rather than a bare literal so the async lambda keeps an await (CS1998 is an error).
    ("the action button's Click handler stops invoking the action", OPTIONSWINDOW,
     "                    result = action.InvokeWithPendingAsync != null\n"
     "                        ? await action.InvokeWithPendingAsync(Collect()) ?? \"\"\n"
     "                        : action.InvokeAsync != null ? await action.InvokeAsync() ?? \"\" : \"\";",
     "                    result = await System.Threading.Tasks.Task.FromResult(\"\");",
     "wpf", "pane action invokes + returns a status"),

    # The PANE-level site (Build's action grouping), not the list-card one: section 4's "Probe action" is a
    # pane action. The first draft of this case mutated the list-card renderer and SURVIVED, which is how the
    # two sites were told apart.
    ("an InvokeAsync-only action gets no button", OPTIONSWINDOW,
     "                    if (a == null || (a.InvokeAsync == null && a.InvokeWithPendingAsync == null)) continue;",
     "                    if (a == null || a.InvokeWithPendingAsync == null) continue;",
     "wpf", "the pane renders a button for its action"),

    # ---- lane fix/host ----

    # F372: the pure fit stops clamping the height, so the 1366x768 probe gets 820 back.
    ("the settings window's fit stops clamping the height to the work area", OPTIONSWINDOW,
     "                height = Math.Min(height, workArea.Height - WorkAreaClearance);",
     "                height = preferred.Height;",
     "wpf", "fits a 1366x768 laptop"),

    # F375: the pane-identity half of the guard is removed, so a ReloadPaneAfter finishing after a pane
    # switch rebuilds its pane over the one the user moved to. The _closed half stays, because a field that
    # is written and never read is a warning, and warnings are errors: the mutation has to compile.
    ("a late ReloadPaneAfter rebuilds its pane over the one on screen again", OPTIONSWINDOW,
     "                if (_closed || !ReferenceEquals(_current, pane)) return false;\n",
     "                if (_closed) return false;\n",
     "wpf", "does not rebuild its pane over the one on screen"),

    # F375: the declined rebuild leaves its stash in the one slot for the next build to consume.
    ("a declined rebuild leaves its stash behind", OPTIONSWINDOW,
     "                    if (!_requestReload())\n"
     "                    {\n"
     "                        TakeActionRebuild(_pane);\n"
     "                        return;\n"
     "                    }",
     "                    if (!_requestReload()) return;",
     "wpf", "leaves nothing stashed"),

    # F368: a redirect by title switches panes over unsaved edits again.
    ("a redirect by title discards unsaved edits again", OPTIONSWINDOW,
     "            if (IsDirty && !discardEdits) return false;\n",
     "",
     "wpf", "REFUSED while the pane has unsaved edits"),

    # F376: the resolved file is compared against the UNRESOLVED root again, which refuses every file
    # under a root reached through a junction.
    ("reveal containment compares the resolved file against the unresolved root again", OPTIONSWINDOW,
     "            if (!IsUnder(real, realRoot)) { refusal = outside; return null; }",
     "            if (!IsUnder(real, dataRoot)) { refusal = outside; return null; }",
     "wpf", "reached through a junction still allows"),

    # F316: the owned-storage probe points its directories back at the live data root.
    ("the owned-storage probe writes into the live data root again", "src/dotNet/WpfOptionsSelfTest.cs",
     "                string alphaProbeRoot = Path.Combine(scratchDataRoot, \"modules\", \"alphaprobe\");",
     "                string alphaProbeRoot = DesktopAICompanion.Wpf.PaneView.RevealRootFor(\"alphaprobe\");",
     "wpf", "never under the live data root"),

    # F377: the nav ListBox gets WPF's horizontal Auto back.
    ("the nav list can grow a horizontal scrollbar again", OPTIONSWINDOW,
     "            ScrollViewer.SetHorizontalScrollBarVisibility(nav, ScrollBarVisibility.Disabled);\n",
     "",
     "wpf", "never grows a horizontal scrollbar"),


    # ---- lane fix/settings ----
]


HOST_CSPROJ = os.path.join(ROOT, "src", "DesktopAICompanion_Portable.csproj")


def windows_powershell_env():
    """A child environment whose PSModulePath is Windows PowerShell's own.

    Launched from pwsh 7, a powershell.exe child inherits pwsh's PSModulePath with the PowerShell 7
    module folders FIRST, and the 5.1 engine then autoloads Get-FileHash from the 7-only manifest it
    cannot run, so the invariant script dies with "Get-FileHash is not recognized" and the baseline is
    refused; from a Windows shell the same command passes (N-fortunes-01, measured 2026-09-29/30 on
    the sibling harness, which spawns the same script). The child keeps only the WindowsPowerShell
    entries it inherited and is guaranteed the two system defaults.
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


def read_bytes(path):
    with io.open(path, "rb") as handle:
        return handle.read()


def write_bytes(path, data):
    with io.open(path, "wb") as handle:
        handle.write(data)


def line_ending_variant(base, old, new):
    """Pick the (old, new) pair whose line endings match the FILE being mutated.

    Every pattern in CASES is written with LF. src/dotNet/StartUp.cs is CRLF in the working tree, and a
    CRLF file cannot contain an LF pattern, so once the targets are handled as bytes (below) its two
    cases would print NO-OP and cover nothing -- the exact rot the sibling harnesses documented and
    fixed the same way (their line_ending_variant). The bytes are written back exactly as read, so a
    file's line endings are never rewritten by a mutation run.
    """
    if base.count(old) == 1:
        return old, new
    as_crlf = lambda b: b.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    crlf_old, crlf_new = as_crlf(old), as_crlf(new)
    if base.count(crlf_old) == 1:
        return crlf_old, crlf_new
    return old, new


def run_gate():
    """Source invariants only. Returns (ok, text)."""
    p = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-File", os.path.join(ROOT, "tests", "runtime-hardening-selftest.ps1")],
                       cwd=ROOT, capture_output=True, text=True, env=windows_powershell_env())
    return p.returncode == 0, p.stdout + p.stderr


def build_host():
    """`dotnet build` of the HOST project alone. Returns (ok, text).

    build.ps1 -Release ran here, for the baseline and again for every wpf case: the host plus all eight
    module projects, when every wpf target (OptionsShell.cs, OptionsWindow.cs, DiagnosticLog.cs) compiles
    into the host alone and --wpf-options-selftest loads no module (F402). The sibling harnesses build the
    one csproj that contains the code under test; so does this. The csproj fixes its OutputPath on
    Configuration alone, so no platform argument is needed, and run_wpf's timestamp assertion is what
    proves the exe was rebuilt.
    """
    p = subprocess.run(["dotnet", "build", HOST_CSPROJ, "-c", "Release", "--nologo", "-v:quiet"],
                       cwd=ROOT, capture_output=True, text=True, timeout=1800)
    return p.returncode == 0, (p.stdout or "") + (p.stderr or "")


def run_wpf(expect_rebuild):
    """Rebuild, then the in-process pane self-test. Returns (ok, text); ok is None when NOTHING can be
    concluded, which both callers report as BROKEN rather than as a verdict.

    Four non-results used to be graded from whatever marker was already sitting in %TEMP% (F403): a
    build that did not reach the exe, a self-test that hung or died before its final WriteAllText, a
    WriteAllText that threw into its swallowing catch, and a marker no run of ours wrote. The gate's
    own marker from the last run-gate.ps1 -- RESULT=PASS -- was usually the file being read, so a dead
    run printed SURVIVED and told the maintainer to add a guard that exists. Each is now its own
    refusal: the artefact must have been rebuilt when a mutation is in place, the previous marker must
    be gone before the run, the run must exit within the bound and leave a marker, and the marker's
    verdict must agree with the exit code (Program.cs exits Run() ? 0 : 1 from the same bool that
    chose the RESULT line).

    `expect_rebuild` is False for the baseline, whose build is legitimately up to date.
    """
    before = os.path.getmtime(EXE) if os.path.isfile(EXE) else 0.0
    built, build_text = build_host()
    if not built:
        # A mutation that will not compile proves nothing about the check, so say so rather than
        # counting it as a pass.
        return None, "BUILD FAILED: " + build_text[-600:].strip()
    if expect_rebuild and (not os.path.isfile(EXE) or os.path.getmtime(EXE) <= before):
        return None, ("the exe was not rebuilt (its timestamp did not advance), so the mutation never "
                      "reached the binary")
    try:
        os.remove(WPF_RESULT)
    except OSError:
        pass
    if os.path.isfile(WPF_RESULT):
        return None, "stale marker could not be removed: " + WPF_RESULT
    try:
        p = subprocess.run([EXE, "--wpf-options-selftest"], cwd=ROOT, capture_output=True, text=True,
                           timeout=900, env=CHILD_ENV)
    except subprocess.TimeoutExpired:
        return None, "the self-test did not exit in 900s"
    if not os.path.isfile(WPF_RESULT):
        return None, "no marker written (exit %d): %s" % (p.returncode, (p.stdout or "")[-400:].strip())
    try:
        text = io.open(WPF_RESULT, encoding="utf-8", errors="replace").read()
    except OSError as e:
        return None, "the marker could not be read: %s" % e
    # Column 0: the verdict line itself, not a mention of it inside an assertion label.
    ok = any(ln.startswith("RESULT=PASS") for ln in text.splitlines())
    if ok != (p.returncode == 0):
        return None, "marker and exit code disagree (exit %d, RESULT=PASS %s)" % (
            p.returncode, "present" if ok else "absent")
    return ok, text


def main():
    global RUN_TEMP, WPF_RESULT, CHILD_ENV
    # Short on purpose: a self-test's own temp paths sit below this, and one of them (the fortunes
    # VectorCache fallback probe) reaches MAX_PATH once TEMP itself is ~107 characters. Measured 2026-09-29.
    RUN_TEMP = os.path.join(os.environ.get("TEMP", "."), "dp-mdg-" + uuid.uuid4().hex[:12])
    WPF_RESULT = os.path.join(RUN_TEMP, "dp-wpf-options-selftest.txt")
    os.makedirs(RUN_TEMP)
    CHILD_ENV = dict(os.environ)
    CHILD_ENV["TEMP"] = RUN_TEMP
    CHILD_ENV["TMP"] = RUN_TEMP
    try:
        return score()
    finally:
        # The SelfTestScratch sweep inside the child sweeps THIS directory now, so nothing else
        # would collect it.
        shutil.rmtree(RUN_TEMP, ignore_errors=True)


def score():
    fast = "--fast" in sys.argv
    only = ""
    for a in sys.argv[1:]:
        if a.startswith("--only="):
            only = a[len("--only="):]
    cases = [c for c in CASES if not (fast and c[4] == "wpf") and (not only or only in c[0])]

    # BYTES, both ways. This read with universal newlines and wrote with utf-8-sig, so the CRLF StartUp.cs
    # came back as LF from its first case and from every restore(), and a BOM-less target would have
    # gained a BOM -- invisible to git (eol=lf), harmless to the gate, but not the working tree this
    # docstring promises to restore (F404). Bytes in, the same bytes out.
    baseline = {}
    for _, rel, _, _, _, _ in cases:
        path = os.path.join(ROOT, rel)
        baseline[rel] = read_bytes(path)

    def restore():
        for rel, data in baseline.items():
            write_bytes(os.path.join(ROOT, rel), data)

    # BASELINE MUST BE GREEN FIRST. Without this the whole run is worthless: if a check is already
    # failing before anything is mutated, every mutation "fails" too, the expected assertion is sitting
    # right there in the output, and all N cases report FIRED. That happened -- 20/20 was reported
    # against a red baseline, and only the gate caught it afterwards. A mutation test can only measure
    # the DIFFERENCE a mutation makes, so the starting point has to be known-good.
    checkers = set(c[4] for c in cases)
    for checker in sorted(checkers):
        ok, text = (run_gate() if checker == "gate" else run_wpf(False))
        if ok is None:
            # No verdict at all: the exe did not build, wrote no marker, hung, or its marker and exit
            # code disagree. Nothing can be scored against that, and it is not the tolerated doc-count
            # mismatch below -- a stale RESULT=PASS used to satisfy this guard (F403).
            print("\nBASELINE BROKEN for '%s' -- refusing to run: %s" % (checker, text))
            return 2
        if not ok:
            failing = [ln.strip() for ln in text.splitlines()
                       if ln[:1] and not ln[:1].isspace() and not ln.startswith("+")
                       and not ln.startswith("RESULT=")
                       and ("FAIL" in ln or "EXC:" in ln or "MISSING" in ln
                            or ln.rstrip().endswith("failed."))]
            # ...with ONE tolerated exception, added 2026-09-17. The doc-count invariants in
            # runtime-hardening-selftest.ps1 compare that file's own assertion count against the
            # number written in SMOKETEST.md, so a branch that ADDS an invariant has a legitimately
            # red baseline until the doc catches up at merge -- and this harness then refused to run
            # at all, on exactly the branches most likely to need it. Measured: a working branch
            # printed "BASELINE NOT GREEN for 'gate'" and scored nothing.
            #
            # Narrow on purpose. It tolerates that one assertion and nothing else, and it still
            # refuses if anything ELSE is failing, because the reason this gate exists is that a red
            # baseline once reported 20/20 FIRED with every mutation "failing" against a failure
            # that was already there.
            tolerated = "count matches"
            remaining = [ln for ln in failing if tolerated not in ln]
            if failing and not remaining:
                print("\nBASELINE: tolerating a documented-count mismatch and continuing "
                      "(the doc catches up at merge):")
                for ln in failing[:3]:
                    print("   " + ln)
            else:
                print("\nBASELINE NOT GREEN for '%s' -- refusing to run. Fix this first:" % checker)
                for ln in (remaining or failing)[:10] or [str(text)[:400]]:
                    print("   " + ln)
                return 2

    results = []
    rebuilt = True
    try:
        for name, rel, find, repl, checker, expect in cases:
            src = baseline[rel]
            find_b, repl_b = line_ending_variant(src, find.encode("utf-8"), repl.encode("utf-8"))
            if src.count(find_b) != 1:
                results.append(("NO-OP", name,
                                "pattern matched %d times in %s -- the mutation would change nothing"
                                % (src.count(find_b), rel)))
                continue
            write_bytes(os.path.join(ROOT, rel), src.replace(find_b, repl_b))
            ok, text = (run_gate() if checker == "gate" else run_wpf(True))
            restore()

            if ok is None:
                results.append(("BROKEN", name, text.strip()[:200]))
                continue
            if ok:
                results.append(("SURVIVED", name, "the check still passed -- it does not cover this"))
                continue
            # The two checks report failure differently: the gate THROWS ("<name> failed."), so it stops
            # at the first one; the pane self-test prints "FAIL: <name>" for each and runs to the end.
            def is_failure(ln):
                # Column 0 only. PowerShell renders a thrown error over several lines and the wrapped
                # CategoryInfo tail can itself end in "failed.", which would otherwise be miscounted as a
                # second assertion and hide the fact that exactly one fired.
                if not ln or ln[:1].isspace() or ln.startswith("+"):
                    return False
                # The pane self-test's own summary line reads RESULT=FAIL, which is a restatement of the
                # verdict rather than an assertion; counting it would make every case look like it broke
                # two things and hide a genuine second failure.
                if ln.startswith("RESULT="):
                    return False
                # EXC: as well. A caught exception writes 'EXC: <type>: <message>' plus RESULT=FAIL
                # and no FAIL line, so a case whose fragment is not in the EXC text reads WRONG with
                # the exception named, instead of WRONG with an empty 'got:' list.
                return "FAIL" in ln or "EXC:" in ln or "MISSING" in ln or ln.rstrip().endswith("failed.")
            hits = [ln.strip() for ln in text.splitlines() if is_failure(ln) and expect in ln]
            others = [ln.strip() for ln in text.splitlines() if is_failure(ln) and expect not in ln]
            if not hits:
                results.append(("WRONG", name,
                                "failed, but not on '%s'; got: %s" % (expect, "; ".join(others[:3]))))
            else:
                extra = " (+%d other failures)" % len(others) if others else ""
                results.append(("FIRED", name, hits[0][:120] + extra))
    finally:
        restore()
        # Leave build\ current with the restored source, the rule the sibling harnesses follow. A wpf case
        # compiles its mutant into build\...\DesktopAICompanion.exe and restore() alone puts the SOURCE
        # back, so every run that ended on a wpf case left a mutant exe for the next hand-run smoke or
        # self-test to load (F406's larger instance, per its verifier). Checked, not discarded: a clean
        # rebuild that fails is the one thing this rebuild exists to prevent.
        if any(c[4] == "wpf" for c in cases):
            rebuilt, rebuild_text = build_host()
            if not rebuilt:
                print("\nTHE CLEAN REBUILD FAILED -- build\\ may still hold the last mutant's exe:")
                print(rebuild_text[-800:])

    print("")
    for verdict, name, detail in results:
        print("%-9s %s\n          %s" % (verdict, name, detail))
    bad = [r for r in results if r[0] != "FIRED"]
    print("\n%d/%d fired" % (len(results) - len(bad), len(results)))
    return 1 if bad or not rebuilt else 0


if __name__ == "__main__":
    sys.exit(main())
