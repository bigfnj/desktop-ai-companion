"""Mutation test for the diagnostic-log guards.

A guard nobody has seen fail is a guess. Each case below breaks exactly one thing the new logging
module promises, runs the check that claims to cover it, and requires that check to FAIL naming the
right assertion. Anything that survives its mutation is not a guard and should not ship.

Two rules this harness exists to enforce, both learned the hard way in this repo:
  * The baseline is the WORKING TREE, captured in memory before anything is touched, and restored
    from that copy. Restoring with `git checkout --` destroys uncommitted work, and a previous run
    of this harness did exactly that -- every "FIRED" it printed was measured against a file whose
    code under test no longer existed.
  * Every mutation asserts its target text is PRESENT first. A pattern that silently matches nothing
    is a no-op, and a no-op mutation always looks like a passing test.

Usage:  %TOOLBOX_PYTHON% tests/mutate-diagnostics.py [--fast]
        --fast skips the cases that need a rebuild (source-invariant cases only).
"""

import io
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "build", "DesktopAICompanionPortable", "bin", "Release", "x64",
                   "DesktopAICompanion.exe")
WPF_RESULT = os.path.join(os.environ.get("TEMP", ""), "dp-wpf-options-selftest.txt")

OPTIONS = "src/Portable/Wpf/OptionsShell.cs"
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

    ("the tray path stops recording its outcome", PROCICON,
     '"tray icon set: success="', '"tray icon attempted"',
     "gate", "SetIcon records its outcome"),

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

    ("the master switch does nothing", DIAG,
     "                if (!_enabled) return false;\n"
     "                if (_mutedCategories.Contains(category)) return false;",
     "                if (_mutedCategories.Contains(category)) return false;",
     "wpf", "the master switch turns everything off"),
]


def run_gate():
    """Source invariants only. Returns (ok, text)."""
    p = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-File", os.path.join(ROOT, "tests", "runtime-hardening-selftest.ps1")],
                       cwd=ROOT, capture_output=True, text=True)
    return p.returncode == 0, p.stdout + p.stderr


def run_wpf():
    """Rebuild, then the in-process pane self-test. Returns (ok, text)."""
    b = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-File", os.path.join(ROOT, "build.ps1"), "-Release"],
                       cwd=ROOT, capture_output=True, text=True)
    if b.returncode != 0:
        # A mutation that will not compile proves nothing about the check, so say so rather than
        # counting it as a pass.
        return None, "BUILD FAILED"
    subprocess.run([EXE, "--wpf-options-selftest"], cwd=ROOT, capture_output=True, text=True)
    try:
        text = io.open(WPF_RESULT, encoding="utf-8", errors="replace").read()
    except OSError as e:
        return None, "no result file: %s" % e
    return "RESULT=PASS" in text, text


def main():
    fast = "--fast" in sys.argv
    only = ""
    for a in sys.argv[1:]:
        if a.startswith("--only="):
            only = a[len("--only="):]
    cases = [c for c in CASES if not (fast and c[4] == "wpf") and (not only or only in c[0])]

    baseline = {}
    for _, rel, _, _, _, _ in cases:
        path = os.path.join(ROOT, rel)
        baseline[rel] = io.open(path, encoding="utf-8-sig").read()

    def restore():
        for rel, text in baseline.items():
            io.open(os.path.join(ROOT, rel), "w", encoding="utf-8-sig", newline="").write(text)

    results = []
    try:
        for name, rel, find, repl, checker, expect in cases:
            src = baseline[rel]
            if src.count(find) != 1:
                results.append(("NO-OP", name,
                                "pattern matched %d times in %s -- the mutation would change nothing"
                                % (src.count(find), rel)))
                continue
            io.open(os.path.join(ROOT, rel), "w", encoding="utf-8-sig", newline="").write(
                src.replace(find, repl))
            ok, text = (run_gate() if checker == "gate" else run_wpf())
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
                return "FAIL" in ln or "MISSING" in ln or ln.rstrip().endswith("failed.")
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

    print("")
    for verdict, name, detail in results:
        print("%-9s %s\n          %s" % (verdict, name, detail))
    bad = [r for r in results if r[0] != "FIRED"]
    print("\n%d/%d fired" % (len(results) - len(bad), len(results)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
