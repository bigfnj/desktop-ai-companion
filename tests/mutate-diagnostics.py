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

Usage:  %TOOLBOX_PYTHON% tests/mutate-diagnostics.py [--fast] [--only=<substring>]
        --fast skips the cases that need a rebuild (source-invariant cases only).
        --only selects cases by name, case-insensitively; a selection that matches nothing is a
        refusal (exit 2), not "0/0 fired" (RA-353): with no case there is no baseline either, so a
        mistyped substring used to report success having measured nothing.

The per-run TEMP is removed after a clean run and KEPT, named on the console, after anything else,
with every non-FIRED case's whole output under a per-case name (R-060, R-065): it used to go in every
outcome, so a WRONG or BROKEN verdict survived only as a 200-character console summary.
"""

import io
import os
import re
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
ABOUTWINDOW = "src/Portable/Wpf/AboutWindow.cs"
DIAG = "src/dotNet/DiagnosticLog.cs"
STARTUP = "src/dotNet/StartUp.cs"
SETTINGS = "src/Portable/AppSettingsStore.cs"
PROCICON = "src/dotNet/ProcessIcon.cs"
MODULESPANE = "src/Portable/Wpf/ModulesPaneControl.cs"
PENDING_REMOVALS = "src/dotNet/Plugins/PendingModuleRemovals.cs"
WPFTHEME = "src/Portable/Wpf/WpfTheme.cs"   # lane feature/settings-primitives

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

    # F375: the view-identity half of the guard is removed, so a ReloadPaneAfter finishing after a pane
    # switch rebuilds its pane over the one the user moved to. The _closed half stays, because a field that
    # is written and never read is a warning, and warnings are errors: the mutation has to compile.
    # Re-pointed 2026-09-30 by lane burn/host-shell: the guard keys on the build generation (RA-329,
    # RA-330), which a pane switch also advances, so dropping it still fires this case (and the same-pane
    # case below it). `gen` stays read by the NotifyDirty delegate, so the mutant compiles.
    ("a late ReloadPaneAfter rebuilds its pane over the one on screen again", OPTIONSWINDOW,
     "                if (_closed || gen != _buildGeneration) return false;\n",
     "                if (_closed) return false;\n",
     "wpf", "does not rebuild its pane over the one on screen"),

    # F375: the declined rebuild leaves its stash in the one slot for the next build to consume.
    # Re-pointed 2026-10-06 by lane feature/settings-primitives: the block also takes back the collapsible
    # cards' view state now; the mutant keeps that take, so it removes only the action stash's, as before.
    ("a declined rebuild leaves its stash behind", OPTIONSWINDOW,
     "                    if (!_requestReload())\n"
     "                    {\n"
     "                        TakeActionRebuild(_pane);\n"
     "                        TakeViewState(_pane);\n"
     "                        return;\n"
     "                    }",
     "                    if (!_requestReload()) { TakeViewState(_pane); return; }",
     "wpf", "leaves nothing stashed"),

    # F368: a redirect by title switches panes over unsaved edits again. Re-pointed 2026-09-30 by lane
    # burn/host-shell: the guard gained the busy-pane term (RA-328); deleting the whole line still fires
    # this case (and the busy case below it).
    ("a redirect by title discards unsaved edits again", OPTIONSWINDOW,
     "            if ((IsDirty || IsCurrentPaneBusy) && !discardEdits) return false;\n",
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

    # ---- lane burn/host-shell ----

    # RA-329, RA-330: the reload guard keys on PANE identity again, the F375 shape. A pane switch still
    # declines (the F375 case above covers that), but a stale continuation from a torn-down view of the SAME
    # pane rebuilds over the fresh view. Only the same-pane check can tell the two guards apart.
    ("burn/host-shell: the reload guard keys on pane identity instead of the build generation", OPTIONSWINDOW,
     "                if (_closed || gen != _buildGeneration) return false;\n",
     "                if (_closed || !ReferenceEquals(_current, pane)) return false;\n",
     "wpf", "does not rebuild over the fresh view"),

    # RA-328: the busy-pane term leaves the redirect guard, so a redirect lands over a download again.
    ("burn/host-shell: a redirect by title cancels a custom pane's download again", OPTIONSWINDOW,
     "            if ((IsDirty || IsCurrentPaneBusy) && !discardEdits) return false;\n",
     "            if (IsDirty && !discardEdits) return false;\n",
     "wpf", "REFUSED while the custom pane has a download in flight"),

    # RA-331: the Load-only cascade never overlays its stash (the condition can never hold), so the rebuilt
    # pane shows the stored value again and the edit snaps back.
    ("burn/host-shell: a ReloadOnChange rebuild on a Load-only pane drops the stash again", OPTIONSWINDOW,
     "                if (pending != null && pending.Count > 0)\n                {\n                    var overlaid",
     "                if (pending != null && pending.Count < 0)\n                {\n                    var overlaid",
     "wpf", "shows the value just picked, not the stored one"),

    # RA-332: a deferred card on a Save-less pane stages its ticks again, for a flush that never comes.
    ("burn/host-shell: a deferred card on a pane without Save stages its ticks again", OPTIONSWINDOW,
     "                        if (lc.DeferChanges && _pane != null && _pane.Save != null)\n",
     "                        if (lc.DeferChanges)\n",
     "wpf", "reaches SetChecked at once"),

    # RA-326, both halves. The pure conversion stops scaling, so a 125% panel is fitted in pixels read as
    # DIPs (a window 25% too tall for it); and the constructor goes back to the primary's work area, which
    # the re-pointed F372 source invariant refuses. The gate fragment sits in the label's first 80
    # characters on purpose: powershell.exe wraps a thrown message at the console width when its output is
    # captured, and the ladder reads only the line that ends in "failed." (the first draft of this case
    # read WRONG on exactly that wrap).
    ("burn/host-shell: the work area stops being converted to the monitor's DIPs", OPTIONSWINDOW,
     "            double scale = dpi > 0 ? 96.0 / dpi : 1.0;\n",
     "            double scale = 1.0;\n",
     "wpf", "fitted in that panel's DIPs"),
    ("burn/host-shell: the window is fitted to the primary work area again", OPTIONSWINDOW,
     "            Size fitted = InitialSize(PreferredSize, MinimumSize, StartupWorkArea());\n",
     "            Size fitted = InitialSize(PreferredSize, MinimumSize, SystemParameters.WorkArea);\n",
     "gate", "fitted to the monitor it opens on"),

    # RA-312: the About window renders any absolute URI live again, whatever the click would do with it.
    ("burn/host-shell: the About window renders a link the click would refuse", ABOUTWINDOW,
     "            if (!opens || !Uri.TryCreate(url, UriKind.Absolute, out uri))\n",
     "            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))\n",
     "wpf", "renders as plain text, since the click would refuse it"),

    # ---- lane fix/settings ----

    # ---- lane feature/modules-update-all ----
    # The Modules pane's Update all and the update-path fixes the addendum added (no build this host cannot run
    # is offered; a staged update is shown as staged; an uninstall waiting for the start is not undone), each
    # guard broken alone. All rebuild the host: the pane, PendingModuleUpdates and PendingModuleRemovals compile
    # into it, and --wpf-options-selftest drives the pane over ModulesPaneSeams fakes.
    ("feature/modules-update-all: Update all leaves the footer's button row", MODULESPANE,
     "            footerButtons.Children.Add(_updateAllButton);\n"
     "            footer.Children.Add(footerButtons);\n",
     "            footer.Children.Add(footerButtons);\n"
     "            footer.Children.Add(_updateAllButton);\n",
     "wpf", "in the footer beside the Check button"),
    ("feature/modules-update-all: Update all shows for a single offer", MODULESPANE,
     "            if (taken.Count < 2)\n",
     "            if (taken.Count < 1)\n",
     "wpf", "WITNESS one offer shows no Update all"),
    ("feature/modules-update-all: a row's Update is not held while Update all runs", MODULESPANE,
     "                    IsEnabled = !_updatingAll,   // Update all is staging this module, or another, right now\n",
     "",
     "wpf", "while it runs, Update all, every row's Update and Uninstall"),
    ("feature/modules-update-all: Uninstall is not held while Update all runs", MODULESPANE,
     "Width = 80, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_updatingAll };",
     "Width = 80, VerticalAlignment = VerticalAlignment.Center };",
     "wpf", "while it runs, Update all, every row's Update and Uninstall"),
    ("feature/modules-update-all: Update all's own button is not held while it runs", MODULESPANE,
     "            _updateAllButton.IsEnabled = !_updatingAll;\n",
     "            _updateAllButton.IsEnabled = true;\n",
     "wpf", "while it runs, Update all, every row's Update and Uninstall"),
    ("feature/modules-update-all: the Check button is not held while Update all runs", MODULESPANE,
     "            _updatingAll = true;\n"
     "            _checkButton.IsEnabled = false;\n",
     "            _updatingAll = true;\n",
     "wpf", "while it runs, Update all, every row's Update and Uninstall"),
    ("feature/modules-update-all: an Install card is not held while Update all runs", MODULESPANE,
     "            install.IsEnabled = runnable && !_updatingAll;",
     "            install.IsEnabled = runnable;",
     "wpf", "while it runs, Update all, every row's Update and Uninstall"),
    ("feature/modules-update-all: a second press starts a second run", MODULESPANE,
     "            if (_updatingAll) return;   // a second press while the run is going\n",
     "",
     "wpf", "a second press while it runs starts nothing"),
    ("feature/modules-update-all: the progress line stops naming the module", MODULESPANE,
     "                            _status.Text = \"Downloading \" + module.Name + \" v\" + module.Version +\n"
     "                                           \" (\" + (n + 1) + \" of \" + toFetch.Count + \")…\";",
     "                            _status.Text = \"Downloading update \" + (n + 1) + \" of \" + toFetch.Count + \"…\";",
     "wpf", "the progress names each module as it is fetched"),
    ("feature/modules-update-all: Update all stops marking what it staged", MODULESPANE,
     "                                _seams.MarkForUpdate(module.Id);\n"
     "                                stagedHere = null;   // marked: the next launch owns it now\n"
     "                                staged.Add(module.Id);\n",
     "                                stagedHere = null;   // marked: the next launch owns it now\n"
     "                                staged.Add(module.Id);\n",
     "wpf", "a press stages both through the row's path"),
    ("feature/modules-update-all: Update all asks to restart once per module", MODULESPANE,
     "                                staged.Add(module.Id);\n",
     "                                staged.Add(module.Id);\n"
     "                                RestartToApply(staged.Count);\n",
     "wpf", "asks to restart exactly ONCE"),
    ("feature/modules-update-all: the result stops naming a staged module", MODULESPANE,
     "                                lines[toFetch[n]] = \"✓ \" + module.Name + \" v\" + module.Version + \" is ready to apply.\";",
     "                                lines[toFetch[n]] = null;",
     "wpf", "the result names each module, then says once"),
    ("feature/modules-update-all: one failed download stops the rest of the run", MODULESPANE,
     "                                lines[toFetch[n]] = \"✗ Couldn't update \" + module.Name + \": \" + PaneText.Short(ex.Message);\n",
     "                                lines[toFetch[n]] = \"✗ Couldn't update \" + module.Name + \": \" + PaneText.Short(ex.Message);\n"
     "                                break;\n",
     "wpf", "a download that fails its check stages the others"),
    ("feature/modules-update-all: a run that staged nothing still asks to restart", MODULESPANE,
     "                    delegate { return staged.Count > 0; },",
     "                    delegate { return true; },",
     "wpf", "when nothing could be staged no restart is asked for"),
    ("feature/modules-update-all: the run takes an update that needs a newer app", MODULESPANE,
     "                    if (offer.NeedsNewerApp != null)\n"
     "                    {\n"
     "                        lines[i] = \"✗ Left \" + name + \" as it is. Needs a newer app: \"",
     "                    if (offer.NeedsNewerApp == \"never\")\n"
     "                    {\n"
     "                        lines[i] = \"✗ Left \" + name + \" as it is. Needs a newer app: \"",
     "wpf", "an update that needs a newer app is left out"),
    ("feature/modules-update-all: Update all counts an update that needs a newer app", MODULESPANE,
     "                    if (offer.Offerable && !offer.BeingRemoved)\n",
     "                    if (!offer.Staged && !offer.BeingRemoved)\n",
     "wpf", "an update that needs a newer app is left out"),
    ("feature/modules-update-all: a row offers an update that needs a newer app", MODULESPANE,
     "            else if (offer != null && offer.NeedsNewerApp != null)\n",
     "            else if (offer != null && offer.NeedsNewerApp == \"never\")\n",
     "wpf", "a row whose update needs a newer app offers no Update button"),
    ("feature/modules-update-all: the run takes a module set to be uninstalled", MODULESPANE,
     "                    if (offer.BeingRemoved)\n",
     "                    if (offer.BeingRemoved && offer.Module == null)\n",
     "wpf", "a module set to be uninstalled is left out"),
    ("feature/modules-update-all: Update all counts a module set to be uninstalled", MODULESPANE,
     "                    if (offer.Offerable && !offer.BeingRemoved)\n",
     "                    if (offer.Offerable)\n",
     "wpf", "a module set to be uninstalled is left out"),
    ("feature/modules-update-all: the pending-uninstall query never finds the id", PENDING_REMOVALS,
     "                if (string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase)) return true;\n",
     "                if (string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase)) return false;\n",
     "wpf", "a module set to be uninstalled is left out"),
    ("feature/modules-update-all: a staged update is offered again", MODULESPANE,
     "            if (_seams.IsStaged(id)) return new UpdateOffer { Installed = info, Staged = true };",
     "            if (_seams.IsStaged(id) && info == null) return new UpdateOffer { Installed = info, Staged = true };",
     "wpf", "an update already staged shows as staged on its row"),
    ("feature/modules-update-all: the row is not redrawn after its own update stages", MODULESPANE,
     "                // Restart declined: the row now says the update is staged, rather than offering it again.\n"
     "                if (IsUp) Reload();\n",
     "",
     "wpf", "once the row's own update is staged and the restart declined"),
    ("feature/modules-update-all: the Check button stays disabled after the run", MODULESPANE,
     "                    _checkButton.IsEnabled = true;\n"
     "                    // The buttons come back",
     "                    // The buttons come back",
     "wpf", "WITNESS once the run has finished every button left is enabled again"),
    ("feature/modules-update-all: Update all asks no consent", MODULESPANE,
     "                    if (!ConfirmUpdatePermissions(offer.Module, offer.Installed, out added))\n",
     "                    added = ModulePermissions.None;\n"
     "                    if (added != ModulePermissions.None)\n",
     "wpf", "consent is asked per module whose update widens its permissions"),
    ("feature/modules-update-all: a declined consent is fetched anyway", MODULESPANE,
     "                                   + DesktopAICompanion.Plugins.ModulePermissionConsent.Describe(added) + \".\";\n"
     "                        continue;\n"
     "                    }\n"
     "                    toFetch.Add(i);\n",
     "                                   + DesktopAICompanion.Plugins.ModulePermissionConsent.Describe(added) + \".\";\n"
     "                    }\n"
     "                    toFetch.Add(i);\n",
     "wpf", "consent is asked per module whose update widens its permissions"),
    ("feature/modules-update-all: Update all starts beside a row's own download", MODULESPANE,
     "            if (_downloadsInFlight > 0 || _checkInFlight)\n",
     "            if (_checkInFlight)\n",
     "wpf", "pressed while a row's own update is downloading, it starts nothing"),
    ("feature/modules-update-all: the row's own Update stops marking its payload", MODULESPANE,
     "                    delegate { _seams.MarkForUpdate(module.Id); return true; },\n",
     "                    delegate { return true; },\n",
     "wpf", "WITNESS the row's own Update still stages its one module"),

    # OptionsWindow.cs (the addendum's host fix): carried action messages keyed by card and label, and the
    # stored baseline a ReloadPaneAfter rebuild measures unsaved edits against.
    ("feature/modules-update-all: a carried action message is keyed by its label alone again", OPTIONSWINDOW,
     "            return (scope ?? \"\") + \" :: \" + (label ?? \"\");\n",
     "            return label ?? \"\";\n",
     "wpf", "no action row shows the message of a same-label row"),
    ("feature/modules-update-all: the action's own message is not carried across its rebuild", OPTIONSWINDOW,
     "                    if (action.Label != null) messages[messageKey] = result;\n",
     "",
     "wpf", "WITNESS the row whose action ran shows its message after the rebuild"),
    ("feature/modules-update-all: a view rebuilt by an action takes its restored values as what Load said", OPTIONSWINDOW,
     "            _stored = stored;\n",
     "            _stored = afterAction != null ? values : stored;\n",
     "wpf", "a second ReloadPaneAfter action keeps the edit the first one put back"),
    ("feature/modules-update-all: the action's stash hands on what the view showed as its baseline", OPTIONSWINDOW,
     "                        Before = _stored,\n",
     "                        Before = _loaded,\n",
     "wpf", "a second ReloadPaneAfter action keeps the edit the first one put back"),
    ("feature/modules-update-all: unsaved edits are measured against what the view showed", OPTIONSWINDOW,
     "                if (!_stored.TryGetValue(kv.Key, out wasLoaded)) continue;\n",
     "                if (!_loaded.TryGetValue(kv.Key, out wasLoaded)) continue;\n",
     "wpf", "a second ReloadPaneAfter action keeps the edit the first one put back"),
    ("feature/modules-update-all: the stored baseline is taken after the RA-331 overlay", OPTIONSWINDOW,
     "            if (stored == null) stored = new Dictionary<string, string>(StringComparer.Ordinal);\n",
     "            stored = values;\n",
     "wpf", "a ReloadPaneAfter action after a ReloadOnChange cascade keeps the cascade's edits"),

    # ---- lane feature/catalog-insight ----
    # What the Modules pane says about the catalog (BUG-014): the refused module entries by name and rule, and a
    # failed fetch in its own case's words. --wpf-options-selftest builds the pane over the Update all probe's
    # fakes and hands the failures in through ShowFetchFailure, so each guard is broken alone and the host rebuilt.
    # The problem panel the owner picked (mockup M2), each part broken alone.
    ("feature/catalog-insight: the Modules pane shows no panel for a refused module entry", MODULESPANE,
     "            ShowProblem(catalog == null ? null : CatalogText.ProblemForRefusals(\n"
     "                catalog.RefusedOf(CatalogRejection.Module), catalog.ReadAt, _occasion, InstalledVersion));\n",
     "            ShowProblem(null);\n",
     "wpf", "catalog: one refused module entry opens the amber panel"),
    ("feature/catalog-insight: the Modules panel lists every kind's refusals", MODULESPANE,
     "                catalog.RefusedOf(CatalogRejection.Module), catalog.ReadAt, _occasion, InstalledVersion));\n",
     "                catalog.Rejected, catalog.ReadAt, _occasion, InstalledVersion));\n",
     "wpf", "and no pack is named"),
    ("feature/catalog-insight: the Modules pane calls a refused catalog unreachable again", MODULESPANE,
     "            ShowProblem(CatalogText.ProblemForFailure(failure, now, _occasion));\n",
     "            ShowProblem(CatalogText.ProblemForFailure(new Exception(CatalogText.Reason(failure)), now, _occasion));\n",
     "wpf", "catalog: a catalog reached and refused opens the red panel"),
    ("feature/catalog-insight: the skipped module's row says nothing about its missing update", MODULESPANE,
     "            CatalogRejection refusedEntry = offer == null && info != null ? RefusedEntry(id) : null;\n",
     "            CatalogRejection refusedEntry = null;\n",
     "wpf", "the skipped module's row says why its update is not offered"),
    ("feature/catalog-insight: Copy details copies the title alone", MODULESPANE,
     "                _seams.CopyText(_shownProblem.ForCopy());\n",
     "                _seams.CopyText(_shownProblem.Title);\n",
     "wpf", "Copy details copies the panel's own words"),
    ("feature/catalog-insight: Try again does not check again", MODULESPANE,
     "                CheckButton_Click(sender, e);\n",
     "                Reload();\n",
     "wpf", "Try again checks again"),
    ("feature/catalog-insight: a clean read leaves the panel up", MODULESPANE,
     "                _problemPanel.Child = null;\n"
     "                _problemPanel.Visibility = Visibility.Collapsed;\n"
     "                return;\n",
     "                return;\n",
     "wpf", "takes the panel and the row's line away"),
    ("feature/catalog-insight: the status line stops saying when it checked", MODULESPANE,
     "            _status.Text = \"Checked today at \" + (catalog != null ? catalog.ReadAt : DateTime.Now).ToString(\n",
     "            _status.Text = \"\" + (catalog != null ? catalog.ReadAt : DateTime.Now).ToString(\n",
     "wpf", "and the status line says when it checked"),
    # ---- lane feature/settings-primitives ----
    # The host 1.4.0 settings primitives. --wpf-options-selftest draws its probe panes under each theme's own
    # resources and reads the pixels, so P0's cases are graded on what a greyed row LOOKS like, which a check on
    # IsEnabled could not see. The XAML cases are single-quoted: the C# verbatim string doubles its quotes.

    # P0: the row EnabledWhen greys is no longer dimmed. Both themes' checks name it, so the fragment is the
    # phrase their two labels share. (Re-pointed by the P2 commit, which added the dim-once term to the line.)
    ("feature/settings-primitives: a row greyed by EnabledWhen is no longer dimmed", OPTIONSWINDOW,
     "                    DimGreyed(target, !live && (cardLive == null || cardLive()));\n",
     "",
     "wpf", "a greyed row of every control kind renders dimmer than its live twin"),
    # P0: each theme's own amount. Without the resource the dim reference resolves to nothing, full opacity.
    ("feature/settings-primitives: the dark theme names no disabled opacity", WPFTHEME,
     "            res[DisabledOpacityKey] = DarkDisabledOpacity;\n",
     "",
     "wpf", "P0: in the dark theme a greyed row"),
    ("feature/settings-primitives: the light theme names no disabled opacity", WPFTHEME,
     "            res[DisabledOpacityKey] = LightDisabledOpacity;\n",
     "",
     "wpf", "P0: in the light theme a greyed row"),
    # P0: the dark Button style keeps its colours but loses its template, which is exactly the shape before
    # host 1.4.0: an enabled button looks right, a disabled one is Aero2's pale box.
    ("feature/settings-primitives: the dark Button falls back to the stock template's pale disabled box", WPFTHEME,
     '    <Setter Property=""Template"">\n      <Setter.Value>\n        <ControlTemplate TargetType=""{x:Type Button}"">\n',
     '    <Setter Property=""Tag"">\n      <Setter.Value>\n        <ControlTemplate TargetType=""{x:Type Button}"">\n',
     "wpf", "a disabled button keeps the dark surface"),
    ("feature/settings-primitives: a disabled dark button keeps a full-contrast caption", WPFTHEME,
     '              <Setter TargetName=""cp"" Property=""TextElement.Foreground"" Value=""{StaticResource dpDisabledText}""/>\n',
     "",
     "wpf", "a disabled button's caption is dimmer"),
    # The FullWidth overhang: a spanning card is given the panel's width again. The masonry probe and the
    # rendered-card check both name it, through the phrase their labels share.
    ("feature/settings-primitives: a full-width card spans the panel instead of the columns", OPTIONSWINDOW,
     "            return Math.Min(panelWidth, cols * ColumnWidth);\n",
     "            return panelWidth;\n",
     "wpf", "not past the last column"),
    ("feature/settings-primitives: a full-width card is arranged across the panel again", OPTIONSWINDOW,
     "                    child.Arrange(new Rect(0, top, SpanWidth(cols, finalSize.Width), child.DesiredSize.Height));\n",
     "                    child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));\n",
     "wpf", "not past the last column"),
    # ...and measured at the panel's width while arranged at the columns': a card whose wrapping text wants all
    # of the width it was measured with keeps the wider one and overhangs. Only the rendered-card check sees
    # this (the masonry probe's children have fixed sizes).
    ("feature/settings-primitives: a full-width card is measured at the panel's width", OPTIONSWINDOW,
     "                    child.Measure(new Size(SpanWidth(cols, fullWidth), double.PositiveInfinity));\n",
     "                    child.Measure(new Size(fullWidth, double.PositiveInfinity));\n",
     "wpf", "FullWidth: a full-width card lines up with the column grid"),
    # P2, SettingField.CardEnabledWhen. The card's gate is never read, so nothing greys.
    ("feature/settings-primitives: CardEnabledWhen is never read", OPTIONSWINDOW,
     "                string cardWhen = lead != null && !string.IsNullOrEmpty(lead.CardEnabledWhen) ? lead.CardEnabledWhen : null;\n",
     "                string cardWhen = null;\n",
     "wpf", "P2: every row and every button in a greyed card is disabled"),
    # ...or read from a later field as well as the first, which the "Later" card's second field must not do.
    ("feature/settings-primitives: CardEnabledWhen is honoured on the group's second field too", OPTIONSWINDOW,
     "                string cardWhen = lead != null && !string.IsNullOrEmpty(lead.CardEnabledWhen) ? lead.CardEnabledWhen : null;\n",
     "                string cardWhen = groupFields[g].Count > 1 && !string.IsNullOrEmpty(groupFields[g][1].CardEnabledWhen)\n"
     "                    ? groupFields[g][1].CardEnabledWhen\n"
     "                    : (lead != null && !string.IsNullOrEmpty(lead.CardEnabledWhen) ? lead.CardEnabledWhen : null);\n",
     "wpf", "P2: CardEnabledWhen on a field that is not the group's first is ignored"),
    # The body is not disabled: rows and buttons stay live, and so the button's refusal never triggers. Both
    # checks name "in a greyed card".
    ("feature/settings-primitives: a greyed card's body is not disabled", OPTIONSWINDOW,
     "                body.IsEnabled = live;\n",
     "",
     "wpf", "in a greyed card"),
    # The refusal: a raised click on a disabled button runs its action again.
    ("feature/settings-primitives: a disabled action button runs a raised click", OPTIONSWINDOW,
     "                if (!btn.IsEnabled) return;\n",
     "",
     "wpf", "P2: a button in a greyed card refuses a raised click"),
    # The look: the body is disabled but not dimmed.
    ("feature/settings-primitives: a greyed card's body is not dimmed", OPTIONSWINDOW,
     "                DimGreyed(body, !live);\n",
     "",
     "wpf", "P2: a greyed card's rows render dimmer than in the live card"),
    # Dim once: a row greyed by its own EnabledWhen inside a greyed card is dimmed again on top of the card.
    ("feature/settings-primitives: a row greyed inside a greyed card is dimmed twice", OPTIONSWINDOW,
     "                    DimGreyed(target, !live && (cardLive == null || cardLive()));\n",
     "                    DimGreyed(target, !live);\n",
     "wpf", "is dimmed once, like its siblings"),
    # The reason line: never shown, named by id rather than label, and a Bool's true/false leaking through.
    ("feature/settings-primitives: a greyed card shows no reason line", OPTIONSWINDOW,
     "                reasonBlock.Visibility = live ? Visibility.Collapsed : Visibility.Visible;\n",
     "                reasonBlock.Visibility = Visibility.Collapsed;\n",
     "wpf", "P2: a greyed card says why under its title"),
    ("feature/settings-primitives: the reason line names the field by its id", OPTIONSWINDOW,
     "            string label = other != null && !string.IsNullOrEmpty(other.Label) ? other.Label : otherId;\n",
     "            string label = otherId;\n",
     "wpf", "naming the field by its label and its value"),
    ("feature/settings-primitives: the reason line reads a Bool as true/false", OPTIONSWINDOW,
     "            if (other != null && other.Kind == SettingKind.Bool) value = ParseBool(value) ? \"on\" : \"off\";\n",
     "",
     "wpf", "P2: a card gated on a Bool reads on/off"),
    # P3, SettingField.Collapsible / StartCollapsed. Never read; read from the second field too; StartCollapsed
    # ignored (the fresh-open witness rests on it as well).
    ("feature/settings-primitives: Collapsible is never read", OPTIONSWINDOW,
     "                bool collapsible = lead != null && lead.Collapsible;\n",
     "                bool collapsible = false;\n",
     "wpf", "P3: Collapsible on a group's first field makes the card an expander"),
    ("feature/settings-primitives: Collapsible is honoured on the group's second field too", OPTIONSWINDOW,
     "                bool collapsible = lead != null && lead.Collapsible;\n",
     "                bool collapsible = (lead != null && lead.Collapsible) || (groupFields[g].Count > 1 && groupFields[g][1].Collapsible);\n",
     "wpf", "P3: Collapsible on a field that is not the group's first is ignored"),
    ("feature/settings-primitives: StartCollapsed is ignored, every collapsible card starts open", OPTIONSWINDOW,
     "            return !lead.StartCollapsed;\n",
     "            return true;\n",
     "wpf", "StartCollapsed s"),
    # The count beside the title counts a Header as a setting.
    ("feature/settings-primitives: a collapsible card's count includes its Header and Info rows", OPTIONSWINDOW,
     "                    if (f != null && f.Kind != SettingKind.Info && f.Kind != SettingKind.Header) settings++;\n",
     "                    if (f != null) settings++;\n",
     "wpf", "P3: a collapsible card's title counts what it holds"),
    # Carrying the open state across rebuilds. The rebuilt view ignores what it was handed (all three carries
    # fail, through the phrase their labels share), or the expander never records being opened.
    # (The lookup stays and its answer is dropped: deleting the line leaves `open` unused, CS0168, an error.)
    ("feature/settings-primitives: a rebuilt view ignores the card states it was handed", OPTIONSWINDOW,
     "            if (_carriedOpen != null && _carriedOpen.TryGetValue(key, out open)) return open;\n",
     "            if (_carriedOpen != null && _carriedOpen.TryGetValue(key, out open)) { }\n",
     "wpf", "stays open across"),
    ("feature/settings-primitives: opening a collapsible card is never recorded", OPTIONSWINDOW,
     "                expander.Expanded += delegate(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, expander)) _cardOpen[key] = true; };\n",
     "",
     "wpf", "stays open across"),
    # Each of the three rebuild paths forgets to hand the state on.
    ("feature/settings-primitives: a ReloadOnChange rebuild does not carry the card states", OPTIONSWINDOW,
     "            StashPendingRebuildValues(_pane, Collect());\n            StashViewState();\n",
     "            StashPendingRebuildValues(_pane, Collect());\n",
     "wpf", "P3: an opened card stays open across a ReloadOnChange rebuild"),
    ("feature/settings-primitives: a ReloadPaneAfter rebuild does not carry the card states", OPTIONSWINDOW,
     "                    // ...and which collapsible cards were open, so the card this button sits in stays open.\n"
     "                    StashViewState();\n",
     "",
     "wpf", "P3: an opened card stays open across a ReloadPaneAfter rebuild"),
    ("feature/settings-primitives: the refresh after Apply does not carry the card states", OPTIONSWINDOW,
     "                    _current.CarryViewStateIntoRebuild();\n",
     "",
     "wpf", "P3: an opened card stays open across the refresh after Apply"),
    # A declined rebuild leaves the states in the slot, for an unrelated later build to open its cards with.
    ("feature/settings-primitives: a declined ReloadOnChange rebuild leaves the card states stashed", OPTIONSWINDOW,
     "{ TakePendingRebuildValues(_pane); TakeViewState(_pane); return; }",
     "{ TakePendingRebuildValues(_pane); return; }",
     "wpf", "P3: a ReloadOnChange rebuild the window declines leaves no card state stashed"),
    ("feature/settings-primitives: a declined ReloadPaneAfter rebuild leaves the card states stashed", OPTIONSWINDOW,
     "                        TakeActionRebuild(_pane);\n                        TakeViewState(_pane);\n",
     "                        TakeActionRebuild(_pane);\n",
     "wpf", "P3: a ReloadPaneAfter rebuild the window declines leaves no card state stashed"),
    # The slot is never emptied by the build that takes it, so one view's open cards reach every later open.
    ("feature/settings-primitives: taking the card states does not empty the slot", OPTIONSWINDOW,
     "            _viewStatePane = null; _viewStateOpen = null;\n",
     "",
     "wpf", "P3: WITNESS a fresh open of the pane starts the card as its StartCollapsed says"),
    # P4, SettingKind.FilePath / FolderPath. The kinds fall through to the plain text box (the old-host degrade
    # path), so nothing shows a name. The case labels are renumbered rather than deleted, so the editor is
    # still called and the mutant compiles.
    ("feature/settings-primitives: the path kinds render as plain text boxes", OPTIONSWINDOW,
     "                case SettingKind.FilePath:\n                case SettingKind.FolderPath:\n",
     "                case (SettingKind)1000:\n                case (SettingKind)1001:\n",
     "wpf", "P4: a path field shows the file name in the box"),
    # What the box shows: the whole path instead of the name, no folder line, no tooltip.
    ("feature/settings-primitives: a path field's box shows the whole path", OPTIONSWINDOW,
     "                parts.Name.Text = name;\n",
     "                parts.Name.Text = value;\n",
     "wpf", "P4: a path field shows the file name in the box"),
    ("feature/settings-primitives: a path field's folder line is empty", OPTIONSWINDOW,
     "                parts.Folder.Text = dir;\n",
     "                parts.Folder.Text = \"\";\n",
     "wpf", "P4: a path field shows the file name in the box"),
    ("feature/settings-primitives: a path field has no tooltip naming the whole path", OPTIONSWINDOW,
     "                parts.Box.ToolTip = value;\n",
     "",
     "wpf", "P4: a path field shows the file name in the box"),
    # A root (a drive, a share) has no leaf, and splitting it anyway names it "" with the root as its folder.
    # (A first version guarded roots with an explicit GetPathRoot comparison; this harness showed that guard
    # could not change an outcome, since GetFileName already answers "" for a root, and it was removed.)
    ("feature/settings-primitives: a root path is split like any other", OPTIONSWINDOW,
     "                if (string.IsNullOrEmpty(leaf)) return;\n",
     "",
     "wpf", "a root, a drive or a share, names itself"),
    # Blank: no EmptyHint, or not muted.
    ("feature/settings-primitives: a blank path field does not show its EmptyHint", OPTIONSWINDOW,
     "                    parts.Name.Text = f.EmptyHint ?? \"\";\n",
     "                    parts.Name.Text = \"\";\n",
     "wpf", "P4: a blank path field shows its EmptyHint"),
    ("feature/settings-primitives: a blank path field's EmptyHint is not muted", OPTIONSWINDOW,
     "                    parts.Name.Foreground = MutedBrush;\n",
     "",
     "wpf", "P4: a blank path field shows its EmptyHint"),
    # Browse: the choice is dropped, is not an edit, or a cancel blanks the field.
    ("feature/settings-primitives: Browse drops the path it was handed", OPTIONSWINDOW,
     "                value = picked;\n",
     "",
     "wpf", "P4: Browse puts the choice in the field as an unsaved edit"),
    ("feature/settings-primitives: Browse does not mark the pane dirty", OPTIONSWINDOW,
     "                FieldChanged(f);\n            };\n            parts.Clear.Click += delegate\n",
     "            };\n            parts.Clear.Click += delegate\n",
     "wpf", "P4: Browse puts the choice in the field as an unsaved edit"),
    ("feature/settings-primitives: a cancelled Browse blanks the field", OPTIONSWINDOW,
     "                if (string.IsNullOrEmpty(picked) || string.Equals(picked, value, StringComparison.Ordinal)) return;   // cancelled, or no change\n",
     "                if (picked == null) picked = \"\";\n"
     "                if (string.Equals(picked, value, StringComparison.Ordinal)) return;\n",
     "wpf", "P4: a cancelled Browse changes nothing"),
    # Clear does not clear; a greyed row's Browse opens the dialog anyway.
    ("feature/settings-primitives: clear leaves the path in the field", OPTIONSWINDOW,
     "                value = \"\";\n                show();\n",
     "                show();\n",
     "wpf", "P4: clear empties the field"),
    ("feature/settings-primitives: a greyed path row's Browse opens the dialog", OPTIONSWINDOW,
     "                if (!parts.Browse.IsEnabled) return;\n",
     "",
     "wpf", "P4: a greyed path row's Browse refuses"),
    # The dialog filter keeps a leading dot, so ".bin" becomes "*..bin".
    ("feature/settings-primitives: the Browse filter keeps a dotted extension's dot", OPTIONSWINDOW,
     "                    string bare = (ext ?? \"\").Trim().TrimStart('.', '*');\n",
     "                    string bare = (ext ?? \"\").Trim();\n",
     "wpf", "P4: the Browse dialog filters on FileExtensions"),
    # P5, ListCard.MasterToggle. Never read (the condition can no longer hold for the probe's cards).
    ("feature/settings-primitives: MasterToggle is never read", OPTIONSWINDOW,
     "                if (!string.IsNullOrEmpty(lc.MasterToggle))\n",
     "                if (!string.IsNullOrEmpty(lc.MasterToggle) && lc.Title == \"never\")\n",
     "wpf", "P5: MasterToggle adds an All row"),
    # Its click moves no item, so nothing runs and nothing is staged.
    ("feature/settings-primitives: the All row's click moves no item", OPTIONSWINDOW,
     "                    if ((r.Value.IsChecked == true) != target) r.Value.IsChecked = target;\n",
     "                    { }\n",
     "wpf", "P5: ticking All runs SetChecked once per item that changed"),
    # Two-state instead of three, and no count.
    ("feature/settings-primitives: the All row is two-state", OPTIONSWINDOW,
     "                master.IsChecked = on == 0 ? (bool?)false : (on == rows.Count ? (bool?)true : null);\n",
     "                master.IsChecked = on == rows.Count;\n",
     "wpf", "P5: MasterToggle adds an All row that reads the items, tri-state"),
    ("feature/settings-primitives: the All row shows no count", OPTIONSWINDOW,
     "                count.Text = on + \" of \" + rows.Count;\n",
     "",
     "wpf", "P5: MasterToggle adds an All row"),
    # The All row and the headers are refreshed by a single tick no longer, or not after a group's click.
    ("feature/settings-primitives: a single tick refreshes neither the All row nor the headers", OPTIONSWINDOW,
     "                    r.Value.Checked += delegate { if (!_syncingGroup) refreshAll(); };\n",
     "",
     "wpf", "P5: a single tick updates the All row"),
    ("feature/settings-primitives: a group header's click refreshes only its own group", OPTIONSWINDOW,
     "                            _syncingGroup = false;\n                            refreshAll();\n                        };\n",
     "                            _syncingGroup = false;\n                            refreshGroupCheck();\n                        };\n",
     "wpf", "P5: a group header's click updates the All row too"),
    # P6, list counts and the muted detail column. The header's count is never written.
    ("feature/settings-primitives: a group header shows no ticked-of-total count", OPTIONSWINDOW,
     "                            groupCount.Text = on + \" of \" + groupBoxes.Count;\n",
     "",
     "wpf", "P6: a group header counts ticked of total"),
    # The detail column is never built (the item is a bare box again, detail lost), or it is not muted.
    ("feature/settings-primitives: an item's Detail gets no column", OPTIONSWINDOW,
     "                    bool hasDetail = !string.IsNullOrEmpty(it.Detail);\n",
     "                    bool hasDetail = false;\n",
     "wpf", "P6: an item's Detail renders as a muted column"),
    ("feature/settings-primitives: an item's Detail column is not muted", OPTIONSWINDOW,
     "                            Foreground = MutedBrush,\n                            Margin = new Thickness(8, 0, 4, 0),\n",
     "                            Margin = new Thickness(8, 0, 4, 0),\n",
     "wpf", "P6: an item's Detail renders as a muted column"),
    # The filter hides the box and leaves its row, detail and all, on screen.
    ("feature/settings-primitives: the filter hides the box but not its row", OPTIONSWINDOW,
     "                            shownAs[r.Value].Visibility = MatchesFilter(r.Key, q) ? Visibility.Visible : Visibility.Collapsed;\n",
     "                            r.Value.Visibility = MatchesFilter(r.Key, q) ? Visibility.Visible : Visibility.Collapsed;\n",
     "wpf", "P6: the filter hides an item's whole row"),
    # P7, the coloured EmptyHint: never boxed (the early return always taken; written as a condition the
    # compiler cannot fold, or the code after it is unreachable, CS0162), or boxed but left grey.
    ("feature/settings-primitives: a marked EmptyHint is never boxed", OPTIONSWINDOW,
     "            if (!pass && !fail) return text;\n",
     "            if (pass || fail || !pass) return text;\n",
     "wpf", "P7: an EmptyHint starting with"),
    ("feature/settings-primitives: a marked EmptyHint is boxed but stays grey", OPTIONSWINDOW,
     "            text.Foreground = pass ? Brushes.LimeGreen : Brushes.Salmon;\n",
     "",
     "wpf", "P7: an EmptyHint starting with"),
    # The inert default. Every card gets the body panel whether or not it names a card-level primitive, so a
    # shipped module's tree changes; or a row whose EnabledWhen is met is dimmed anyway.
    ("feature/settings-primitives: every card is dressed, primitives or not", OPTIONSWINDOW,
     "                bool dressed = cardWhen != null || collapsible;\n",
     "                bool dressed = true;\n",
     "wpf", "inert: a pane naming no host 1.4.0 member builds none of its chrome"),
    ("feature/settings-primitives: a row whose EnabledWhen is met is dimmed anyway", OPTIONSWINDOW,
     "                    DimGreyed(target, !live && (cardLive == null || cardLive()));\n",
     "                    DimGreyed(target, cardLive == null || cardLive());\n",
     "wpf", "inert: ...and leaves the opacity of every row it does not grey untouched"),
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


# The ONE baseline failure a branch is allowed to carry (RA-354). The invariant script compares
# SMOKETEST.md's "N source invariants" figure to its own assertion count, so a branch that ADDS an
# assertion is red there until the doc catches up at the merge. Tolerating it is rigorous, not
# convenient: the script runs under ErrorActionPreference Stop and Assert-True throws, so it aborts at
# its FIRST failing assertion, and this label belongs to its LAST one -- seeing it means everything
# before it passed. The tolerance used to match the substring "count matches", which five labels carry,
# four of them mid-file (Readme project count, self-test counts, MAPPING.md skins, converted pets): a
# baseline red on any of those would have been waved through while it masked every assertion after it.
# Matched on any line that is not a PASS, the sibling's shape (mutate-hardening-guards.py), because
# Windows PowerShell word-wraps a thrown message at the console width it inherited (RA-355).
DOC_COUNT_DRIFT = "source-invariant count matches this file"


def saw_doc_count_drift(text):
    return any(DOC_COUNT_DRIFT in ln and not ln.strip().startswith("PASS:") for ln in text.splitlines())


def keep_output(index, name, text):
    """A non-FIRED case's WHOLE output under a per-case name (R-060, R-065); main() keeps RUN_TEMP when
    the run is not clean and prints its path."""
    slug = re.sub(r"[^A-Za-z0-9]+", "-", name).strip("-")[:60]
    with io.open(os.path.join(RUN_TEMP, "case-%03d-%s.txt" % (index, slug)), "w", encoding="utf-8") as handle:
        handle.write(text)


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
    # KEPT unless the run was clean (R-060, R-065): a refused baseline's output, every non-FIRED case's
    # output (keep_output) and whatever a Ctrl+C or a timeout left behind stay readable, and the path is
    # printed so the leftover is deliberate. The SelfTestScratch sweep inside the child sweeps THIS
    # directory, and Invoke-SelfTests.ps1's aged dp-* sweep collects a kept one after an hour.
    keep = True
    try:
        code = score()
        keep = code != 0
        return code
    finally:
        # ...and only when it holds something: a refused --only or a missing exe leaves nothing to read.
        if keep and any(os.scandir(RUN_TEMP)):
            print("output kept for inspection: " + RUN_TEMP)
        else:
            shutil.rmtree(RUN_TEMP, ignore_errors=True)


def score():
    fast = "--fast" in sys.argv
    only = ""
    for a in sys.argv[1:]:
        if a.startswith("--only="):
            only = a[len("--only="):]
    cases = [c for c in CASES if not (fast and c[4] == "wpf") and (not only or only.lower() in c[0].lower())]
    if not cases:
        print("no case matched --only=%s%s" % (only, " with --fast" if fast else ""))
        return 2

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
        if checker == "gate" and ok and "PASS:" not in text:
            # Exit 0 and not one PASS line is not a green baseline, it is no run at all: powershell.exe
            # with no console has returned 0 and printed nothing on this box (RA-355's verifier), and a
            # mutation scored against that would read SURVIVED.
            print("\nBASELINE BROKEN for 'gate' -- the invariant script exited 0 without printing a "
                  "single PASS: line, so nothing ran: %s" % text.strip()[:300])
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
            # Narrow on purpose, and narrowed again by RA-354: it tolerates THAT one assertion, the
            # script's last, identified by its exact label on any non-PASS line (DOC_COUNT_DRIFT), and
            # nothing else. Because the script aborts at its first failure there cannot be a second
            # failing assertion beside it, so seeing the label IS the proof that everything before it
            # passed; a red pane self-test is never tolerated.
            if checker == "gate" and saw_doc_count_drift(text):
                print("\nBASELINE: tolerating a documented-count mismatch and continuing "
                      "(the doc catches up at merge):")
                for ln in [ln for ln in text.splitlines() if DOC_COUNT_DRIFT in ln][:2]:
                    print("   " + ln.strip())
            else:
                print("\nBASELINE NOT GREEN for '%s' -- refusing to run. Fix this first:" % checker)
                for ln in failing[:10] or [str(text)[:400]]:
                    print("   " + ln)
                return 2

    results = []
    rebuilt = True
    try:
        for index, (name, rel, find, repl, checker, expect) in enumerate(cases, 1):
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
                keep_output(index, name, text)
                continue
            if ok:
                results.append(("SURVIVED", name, "the check still passed -- it does not cover this"))
                keep_output(index, name, text)
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
            if checker == "gate":
                # A GATE case is graded the way mutate-hardening-guards.py grades the same script (RA-355):
                # the fragment on ANY line that is not a PASS, with the non-zero exit (ok is False here) as
                # the evidence that something threw. Windows PowerShell word-wraps the thrown message at
                # the console width the harness inherited -- measured on this box, a 157-character label
                # split at column 100 -- so requiring the fragment and "failed." on ONE column-0 line read
                # WRONG for four of this file's five gate labels whenever the width was narrower than the
                # label. There is no "+N other failures" for a gate case: the script aborts at its first
                # failing assertion, so there cannot be a second one.
                hits = [ln.strip() for ln in text.splitlines()
                        if expect in ln and not ln.strip().startswith("PASS:")]
                others = []
            else:
                hits = [ln.strip() for ln in text.splitlines() if is_failure(ln) and expect in ln]
                others = [ln.strip() for ln in text.splitlines() if is_failure(ln) and expect not in ln]
            if not hits:
                got = others if others else [ln.strip() for ln in text.splitlines() if is_failure(ln)]
                results.append(("WRONG", name,
                                "failed, but not on '%s'; got: %s" % (expect, "; ".join(got[:3]))))
                keep_output(index, name, text)
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
    return 1 if bad or not rebuilt or not results else 0


if __name__ == "__main__":
    sys.exit(main())
