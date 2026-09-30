#!/usr/bin/env python3
"""Mutation harness for host-side self-test assertions. A guard nobody has seen fail is a guess.

Each case breaks exactly ONE thing a host self-test claims to check, rebuilds whatever
project actually contains the code under test, re-runs that self-test, and requires it to
fail NAMING THE RIGHT ASSERTION.

The per-case BUILD is the part that matters, and it is why this file grew past its first
two cases. Some of these assertions live in the host but watch code that compiles into a
MODULE dll. Rebuilding only the host would have reported SURVIVED while the mutated source
was never compiled -- which is exactly how the BUG-002 harness produced a clean 0/7. So a
case names its own csproj and its own artifact, and the artifact's timestamp must have
ADVANCED before any verdict is believed.

Three channels decide a verdict, not one. A self-test that throws writes `EXC: ...` and
`RESULT=FAIL` with no FAIL line, and exits 1; until 2026-09-29 this file read only the FAIL
lines, so such a run printed "baseline clean" and a throwing mutation printed SURVIVED
(F405). Now the exit code, the column-0 RESULT= line and the FAIL/EXC/SKIP lines must
agree before anything is scored, and a run with no verdict is BROKEN, never SURVIVED.

Every child runs with a private TEMP (one directory per harness run, deleted at the end),
because the marker names are fixed by the flag and the exe writes them under
Path.GetTempPath(): two same-user runners sharing %TEMP% -- a gate in the main checkout and
this harness in a worktree -- could grade each other's build (F418).

Restore is byte-exact from a copy read into memory first, never from git.

    python tests/mutate-selftest-guards.py [--only=<substring>]
"""

import argparse
import io
import os
import shutil
import subprocess
import sys
import time
import uuid

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(REPO, "build", "DesktopAICompanionPortable", "bin", "Release", "x64")
EXE = os.path.join(BIN, "DesktopAICompanion.exe")
FORTUNES_DLL = os.path.join(BIN, "modules", "fortunes", "Fortunes.dll")

HOST_CSPROJ = os.path.join(REPO, "src", "DesktopAICompanion_Portable.csproj")
FORTUNES_CSPROJ = os.path.join(REPO, "modules", "Fortunes", "Fortunes.csproj")

HARD = os.path.join(REPO, "src", "dotNet", "RuntimeHardeningSelfTest.cs")
HOST = os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs")
FORTUNES_MODULE = os.path.join(REPO, "modules", "Fortunes", "FortunesModule.cs")
FORTUNE_PROVIDER = os.path.join(REPO, "modules", "Fortunes", "engine", "FortuneProvider.cs")
FRESHNESS = os.path.join(REPO, "src", "dotNet", "CompanionFreshness.cs")
CONSENT = os.path.join(REPO, "src", "dotNet", "Plugins", "ModulePermissionConsent.cs")
COMPANION_HOST = os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs")
BLINKINGLED_MODULE = os.path.join(REPO, "modules", "BlinkingLed", "BlinkingLedModule.cs")
SCROLLLOCK_BLINKER = os.path.join(REPO, "modules", "BlinkingLed", "engine", "ScrollLockBlinker.cs")
PETSTUDIO_MODULE = os.path.join(REPO, "modules", "PetStudio", "PetStudioModule.cs")
BEHAVIOUR_CHAIN = os.path.join(REPO, "modules", "PetStudio", "BehaviourChain.cs")
BLINKINGLED_DLL = os.path.join(BIN, "modules", "blinkingled", "BlinkingLed.dll")
PETSTUDIO_DLL = os.path.join(BIN, "modules", "petstudio", "PetStudio.dll")
BLINKINGLED_CSPROJ = os.path.join(REPO, "modules", "BlinkingLed", "BlinkingLed.csproj")
PETSTUDIO_CSPROJ = os.path.join(REPO, "modules", "PetStudio", "PetStudio.csproj")
MODULE_HOST = os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleHost.cs")
REMINDER_CSPROJ = os.path.join(REPO, "modules", "Reminder", "Reminder.csproj")
REMINDER_DLL = os.path.join(BIN, "modules", "reminder", "Reminder.dll")
REMINDER_PARSER = os.path.join(REPO, "modules", "Reminder", "PersonalReminderParser.cs")
CACHING_CALENDAR_SOURCE = os.path.join(REPO, "modules", "Reminder", "CachingCalendarSource.cs")
AIBRAIN_CSPROJ = os.path.join(REPO, "modules", "AiBrain", "AiBrain.csproj")
AIBRAIN_DLL = os.path.join(BIN, "modules", "aibrain", "AiBrain.dll")
AIBRAIN_ENGINE = os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs")
EMBEDDER = os.path.join(REPO, "modules", "Fortunes", "engine", "Embedder.cs")
MODULE_HOST_SELFTEST = os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleHostSelfTest.cs")
FORTUNES_ENGINE_SELFTEST = os.path.join(REPO, "src", "dotNet", "Plugins", "FortunesEngineSelfTest.cs")

# CoreTests is a second runner, not a flag on the host exe: a console harness with its own csproj,
# whose verdict is its exit code. Two of its groups bound to the wrong types until 2026-09-29 (F382),
# which is what put it in this file: the mutations that prove the rebinding rebuild ModuleKit and
# AppSettingsStore, so they need the same build-then-check-the-artefact discipline as the rest.
CORETESTS_CSPROJ = os.path.join(REPO, "tests", "DesktopAICompanion.CoreTests",
                                "DesktopAICompanion.CoreTests.csproj")
CORETESTS_DLL = os.path.join(REPO, "tests", "DesktopAICompanion.CoreTests", "bin", "Release",
                             "DesktopAICompanion.CoreTests.dll")
CORETESTS_EXE = os.path.join(REPO, "tests", "DesktopAICompanion.CoreTests", "bin", "Release",
                             "DesktopAICompanion.CoreTests.exe")
# ModuleKit.dll as COPIED into the CoreTests output by its ProjectReference. A mutation in ModuleKit's
# sources recompiles ModuleKit, not CoreTests, so CoreTests.dll's timestamp does not move and only
# this copy proves the mutated code is what the run loaded.
CORETESTS_MODULEKIT_DLL = os.path.join(REPO, "tests", "DesktopAICompanion.CoreTests", "bin", "Release",
                                       "DesktopAICompanion.ModuleKit.dll")
MODULEKIT_ATOMIC = os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "AtomicFile.cs")
MODULEKIT_UNICODE = os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "UnicodeTextProgress.cs")
APPSETTINGS_STORE = os.path.join(REPO, "src", "Portable", "AppSettingsStore.cs")
APPPATHS = os.path.join(REPO, "src", "Portable", "AppPaths.cs")
# The pseudo-flag a case names to run CoreTests instead of the host exe. Its marker is None.
CORETESTS = "CORETESTS"

TEMP = os.environ.get("TEMP", ".")
# One private TEMP per harness run, created in main() and handed to every child through its environment
# (Path.GetTempPath() reads TMP, then TEMP). Deleted at the end: the SelfTestScratch sweep inside the child
# now sweeps THIS directory rather than the real %TEMP%, so nothing else would collect it.
RUN_TEMP = None
CHILD_ENV = None

# (name, source file, find, replace, csproj to build, artifact that must advance,
#  flag, marker, fragment of the assertion that must fail)
CASES = (
    ("CheckAccepts given an input that is REJECTED",
     HARD,
     b'                    CheckAccepts("exact sprite pixel budget accepted",\n'
     b'                        () => validateBudget.Invoke(null, new object[] { 32, 32, 128, 128 }));',
     b'                    CheckAccepts("exact sprite pixel budget accepted",\n'
     b'                        () => validateBudget.Invoke(null, new object[] { 41, 25, 1, 1 }));',
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "exact sprite pixel budget accepted"),

    ("an EMPTY hotkey combo returns null instead of a no-op handle",
     HOST,
     b"            if (string.IsNullOrWhiteSpace(combo) || onPressed == null) return new Noop();",
     b"            if (string.IsNullOrWhiteSpace(combo) || onPressed == null) return null;",
     HOST_CSPROJ, EXE,
     "--aibrain-selftest", "dp-aibrain-selftest.txt", "EMPTY combo"),

    # The embedded welcome corpus. "welcome speaks + is personalized" passes on a corpus of one,
    # so the payload assertion has to be its own. The code under test is in the MODULE.
    ("the embedded welcome corpus fails to load",
     FORTUNES_MODULE,
     b'            return EmbeddedResources.LoadJson<string[]>(typeof(FortunesModule).Assembly, "welcome.json")\n'
     b"                ?? Array.Empty<string>();",
     b"            return Array.Empty<string>();",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--fortunes-selftest", "dp-fortunes-selftest.txt", "welcome corpus loaded"),

    # The writable-folder cache must invalidate on a change. Pin the fingerprint and the cache
    # answers from a stale parse forever, which is the whole failure this test exists for.
    ("the custom-corpus cache never invalidates",
     FORTUNE_PROVIDER,
     b"            string signature = CustomDirSignature(directory);",
     b'            string signature = "pinned";',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--fortunes-selftest", "dp-fortunes-selftest.txt",
     "custom-corpus cache reflects add/edit/remove"),

    # The stale-companion composition, both directions. One mutation each, because a check that only
    # asserts the positive passes on a function that returns EVERYTHING, and one that only asserts
    # the negative passes on a function that returns NOTHING -- and "returns nothing" is the actual
    # failure mode here: it silently stops offering companion updates for ever, which is regression
    # watchlist #12 and has shipped once already.
    #
    # Neither mutation uses `if (false)`, which would be unreachable code and fail the build under
    # src/'s warnings-as-errors: a BROKEN verdict proves nothing about the assertion.
    ("no installed companion is ever classified as stale",
     FRESHNESS,
     b"                if (IsStale(freshness)) stale[pet.Id] = freshness;",
     b"                if (freshness == CompanionFreshness.NotInstalled) stale[pet.Id] = freshness;",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt",
     "the catalog disagrees with comes back stale"),

    ("every installed companion is classified as stale",
     FRESHNESS,
     b"                if (IsStale(freshness)) stale[pet.Id] = freshness;",
     b"                if (freshness != CompanionFreshness.NotInstalled) stale[pet.Id] = freshness;",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt",
     "own hash is NOT offered as an update"),

    # The shared-context PUSH half, which had never executed before 2026-09-17. The raise, and the
    # best-effort promise its own comment makes.
    ("publishing context stops raising ContextChanged",
     COMPANION_HOST,
     b"            if (handler != null) { try { handler(key); } catch { } }",
     b"            if (handler == null) { try { handler(key); } catch { } }",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "publishing RAISES ContextChanged"),

    ("a throwing subscriber takes down the publisher's tick",
     COMPANION_HOST,
     b"            if (handler != null) { try { handler(key); } catch { } }",
     b"            if (handler != null) { handler(key); }",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "THROWING subscriber does not take down"),

    # The module instrumentation added 2026-09-17. The track that wrote it mutation-tested by hand
    # and asked for these to be recorded here, which is the whole point of this file: a mutation
    # proved once in a transcript is a mutation nobody can re-run.
    #
    # Four modules log through a wrapper or a static sink rather than calling IHost.Log directly, so
    # the mutation is to break the WIRING, not a call site: that is the single point where all of a
    # module's lines disappear at once, and it is the failure the assertions exist to catch.
    ("Fortunes' log wrapper stops reaching IHost.Log",
     FORTUNES_MODULE,
     b"            try { host.Log(Info.Id, message); } catch { }",
     b"            try { if (message == null) host.Log(Info.Id, message); } catch { }",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--fortunes-engine-selftest", "dp-fortunes-engine-selftest.txt",
     "engine line reached IHost.Log"),

    ("BlinkingLed's engine sink is never wired",
     BLINKINGLED_MODULE,
     b"            ScrollLockBlinker.LogSink = delegate(string line)",
     b"            ScrollLockBlinker.LogSink = null;\n"
     b"            Action<string> unusedSink = delegate(string line)",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "records whether Windows accepted it"),

    ("BlinkingLed's module-side log wrapper stops reaching IHost.Log",
     BLINKINGLED_MODULE,
     b"            if (host == null || string.IsNullOrEmpty(message)) return;",
     b"            if (host == null || string.IsNullOrEmpty(message) || message.Length > 0) return;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "Caps Lock stop is recorded"),

    ("PetStudio's failure report stops reaching IHost.Log",
     PETSTUDIO_MODULE,
     b"            try { host.Log(Info.Id, what + \": \" + Categorize(ex)); } catch { }",
     b"            try { if (what == null) host.Log(Info.Id, what + \": \" + Categorize(ex)); } catch { }",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--petstudio-selftest", "dp-petstudio-selftest.txt",
     "recorded in the diagnostic log"),

    # The permission-widening diff. Both directions, because a check that only asserts the widening
    # is satisfied by a function that reports EVERY update as a widening, and one that only asserts
    # the silent case is satisfied by a function that never reports anything -- which is precisely
    # the state this replaced.
    ("no permission widening is ever detected",
     CONSENT,
     b"            return offered & ~installed;",
     b"            return ModulePermissions.None;",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt",
     "adding AgentTranscripts is reported as newly requested"),

    ("every update reports the whole new set as newly requested",
     CONSENT,
     b"            return offered & ~installed;",
     b"            return offered;",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt",
     "asking for nothing new is silent"),

    # The prompt has to name what it found. A correct diff behind a message that does not say what
    # changed is still a silent widening from the reader's side.
    ("the consent prompt stops naming the added permissions",
     CONSENT,
     b'                          + "    " + Describe(added)',
     b'                          + "    " + ""',
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt",
     "the prompt names the module, the version and every added flag"),
    # ---- 2026-09-29 audit campaign: each lane adds its cases directly under its own anchor so parallel
    # branches do not touch the same lines. Comments inside the literal are fine for Python.
    # ---- lane fix/gates ----

    # The only= propagation check used to exercise walk -> rotate1a, whose flag is the DEFAULT "none", so
    # flattening every flag to "none" passed it (F153). It now insists on a flagged edge (walk ->
    # vertical_walk_up only="vertical") and compares against the analyzer's edge, so this flattening fails
    # it. Through the module's own SelfTest, which is where the check runs since petstudio became covered.
    ("BehaviourChain.Classify flattens every only= flag to the default",
     BEHAVIOUR_CHAIN,
     b'                    Only = best.Only ?? "",',
     b'                    Only = "none",',
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "the border join carries the edge's only= flag through"),

    # The OTHER direction of the writable-folder cache (F134). The case above pins the fingerprint so
    # the cache never invalidates; this one makes it unique per read so the cache never CACHES. Every
    # freshness line still passes on that -- a stateless re-read is always fresh -- and only the
    # parse-counter assertion added 2026-09-29 tells the two apart.
    ("the custom-corpus cache never caches (a fresh fingerprint on every read)",
     FORTUNE_PROVIDER,
     b"            string signature = CustomDirSignature(directory);",
     b'            string signature = Guid.NewGuid().ToString("N");',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--fortunes-selftest", "dp-fortunes-selftest.txt",
     "unchanged folder is served from the cache"),

    # The tidy-up assertions added 2026-09-29 beside the two %TEMP% leaks in --hardening-selftest
    # and the registry leak in --pettyperegistry-selftest (F294, F290). Each mutation puts the
    # leak back exactly as it shipped.
    ("the scale-percent probe deletes only its .json again",
     HARD,
     b'            foreach (string leftover in new[] { path, path + ".bak", path + ".lock" })',
     b'            foreach (string leftover in new[] { path })',
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt",
     "scale percent: the probe's .json, .bak and .lock are all removed"),

    ("the registry scratch leaves its empty parent key behind again",
     HARD,
     b"                Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(parent, false);",
     b"                if (parent.Length == 0) Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(parent, false);",
     HOST_CSPROJ, EXE,
     "--pettyperegistry-selftest", "dp-pettyperegistry-selftest.txt",
     "registry scratch: an empty Software"),

    # The storage root the two module-loading self-tests hand every module (F351, F338). The
    # mutation is the code as it shipped: Path.GetTempPath() itself.
    ("--module-host-selftest hands modules the TEMP root again",
     MODULE_HOST_SELFTEST,
     b"                var host = new RecordingHost { StorageRoot = scratch };",
     b"                var host = new RecordingHost { StorageRoot = Path.GetTempPath() };",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "storage writes land under the scratch root"),

    ("--fortunes-engine-selftest hands modules the TEMP root again",
     FORTUNES_ENGINE_SELFTEST,
     b"                var host = new RecordingHost(scratch);",
     b"                var host = new RecordingHost(Path.GetTempPath());",
     HOST_CSPROJ, EXE,
     "--fortunes-engine-selftest", "dp-fortunes-engine-selftest.txt",
     "storage writes land under the scratch root"),

    # 'failures: a healthy load reports none' used to read a ModuleHost that had never called
    # LoadFrom (F347). This is the regression it now catches: LoadFrom recording a failure for
    # every module it loads, which the Modules pane would have shown as log noise per module.
    ("LoadFrom records a spurious failure for every module it loads",
     MODULE_HOST,
     b"                    _loaded.Add(new Loaded { Module = module, Alc = alc });\n"
     b"                    count++;",
     b"                    _loaded.Add(new Loaded { Module = module, Alc = alc });\n"
     b'                    _failures.Add(new ModuleLoadFailure { Id = Path.GetFileName(dir), Reason = "spurious" });\n'
     b"                    count++;",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "failures: a healthy load reports none"),

    # CoreTests. Two of its ModuleKit groups bound to the production twins compiled into the
    # harness rather than to ModuleKit.dll, so nothing tested the copies every module ships
    # (F382). Both mutations break the MODULEKIT copy only; a group still bound to the host's twin
    # would survive them.
    ("ModuleKit's AtomicFile writes a UTF-8 BOM",
     MODULEKIT_ATOMIC,
     b"                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))",
     b"                using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit atomic file writes"),

    ("ModuleKit's UnicodeTextProgress truncates through a surrogate pair",
     MODULEKIT_UNICODE,
     b"                char.IsLowSurrogate(text[length]))\n"
     b"                length--;",
     b"                char.IsLowSurrogate(text[length]))\n"
     b"                length += 0;",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit unicode boundaries"),

    # The corrupt-primary recovery group could not tell "restored from the backup" from "reset to
    # defaults", because the backup held the default volume (F384). This is the regression it now
    # catches: a store that never consults its backup.
    ("the settings store never consults its backup",
     APPSETTINGS_STORE,
     b"            ReadResult backupResult = TryRead(_backupPath, out loaded, out changed);",
     b"            ReadResult backupResult = ReadResult.Missing; loaded = null; changed = false;",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "Settings corrupt-primary recovery"),

    # The legacy-lookup loop compared every candidate against a file name the list never yields,
    # so it could not fail on the regression its own message named (F383).
    ("the legacy settings lookup trusts the current directory",
     APPPATHS,
     b'                AddUnique(result, Path.Combine(LocalAppData, "DesktopPet", "DesktopPet.config"));',
     b'                AddUnique(result, Path.Combine(LocalAppData, "DesktopPet", "DesktopPet.config"));\n'
     b'                AddUnique(result, Path.Combine(Environment.CurrentDirectory, "DesktopPet.config"));',
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "AppPaths current-directory independence"),

    # Reminder: the parser's 'at' branch had no case while the detail line claimed it did (F194), and
    # the "not read on the caller's thread" check passed an inline read (F205).
    ("the reminder parser loses its 'at' branch",
     REMINDER_PARSER,
     b"            else if (headLower == \"at\")\n"
     b"            {\n"
     b"                string tok = FirstWord(rest, out string text);\n"
     b"                if (!TryHhmm(tok, out hhmm)) { error = \"After 'at', give a time like 15:00. \" + Help; return false; }\n"
     b"                r.Kind = PersonalReminder.KindOnce; r.When = TodayOrTomorrowAt(now, hhmm); r.Text = text;\n"
     b"            }\n",
     b"",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "PersonalReminderParser"),

    ("the calendar feed is read inline on the caller's thread",
     CACHING_CALENDAR_SOURCE,
     b"                Task.Run(() => DoRefresh(key));",
     b"                DoRefresh(key);",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "Fetch returns before the read completes"),

    # AiBrain: the already-said bound (F084) is asserted on the builder with more remarks than it
    # quotes; an unbounded builder quotes them all.
    ("the already-said list quotes every remark",
     AIBRAIN_ENGINE,
     b"            int from = Math.Max(0, alreadySaid.Count - RemarksQuotedInPrompt);",
     b"            int from = 0;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--aibrain-selftest", "dp-aibrain-selftest.txt",
     "bounded, not a growing transcript"),

    # ModuleKit's RecordingHost: priority arbitration (F233) and the gates Declared enforces (F234).
    # Both mutations put back the shipped shape.
    ("RecordingHost stops sorting its responder chains by priority",
     os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "Testing", "RecordingHost.cs"),
     b"                int byPriority = y.Priority.CompareTo(x.Priority);\n"
     b"                return byPriority != 0 ? byPriority : x.Seq.CompareTo(y.Seq);",
     b"                int byPriority = 0;\n"
     b"                return byPriority != 0 ? byPriority : x.Seq.CompareTo(y.Seq);",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit recording host"),

    ("RecordingHost.OpenLink stops refusing a module without Network",
     os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "Testing", "RecordingHost.cs"),
     b"            if (Refuses(ModulePermissions.Network)) return false;\n"
     b"            OpenedLinks.Add(httpsUrl ?? \"\");",
     b"            OpenedLinks.Add(httpsUrl ?? \"\");",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit recording host"),

    # BlinkingLed's corrective toggle in Stop() (F114). The stopProbe check read PhaseOn, which Stop()
    # resets unconditionally, so the toggle block could be deleted or its gate dropped under a green
    # suite. Both regressions, one case each: the block gone, and the key read gone.
    ("Stop()'s corrective toggle block is deleted",
     SCROLLLOCK_BLINKER,
     b"            if (_phaseOn)\n"
     b"            {\n"
     b"                try { if (ScrollLockReader()) Toggle(); }\n"
     b"                catch { }\n"
     b"            }\n"
     b"            _phaseOn = false;",
     b"            _phaseOn = false;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "Stop() attempts the corrective toggle"),

    ("Stop() toggles without reading the key first",
     SCROLLLOCK_BLINKER,
     b"                try { if (ScrollLockReader()) Toggle(); }",
     b"                try { Toggle(); }",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "leaves a key that does not read lit alone"),

    # The InputSynthesis disclosure gates nothing at runtime, so only an assertion notices it gone (F111).
    ("BlinkingLed drops the InputSynthesis disclosure",
     BLINKINGLED_MODULE,
     b"                          | ModulePermissions.Storage\n"
     b"                          | ModulePermissions.InputSynthesis,",
     b"                          | ModulePermissions.Storage,",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "including the InputSynthesis disclosure"),

    # PetStudio's pane action check passed on an empty Actions array (F355): the label is what it pins now.
    ("PetStudio renames its open-studio action",
     PETSTUDIO_MODULE,
     b'                    new PaneAction { Label = "Open Companion Studio\xe2\x80\xa6", InvokeAsync = OpenAsync, Group = "Companion Studio" },',
     b'                    new PaneAction { Label = "Open the studio\xe2\x80\xa6", InvokeAsync = OpenAsync, Group = "Companion Studio" },',
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--petstudio-selftest", "dp-petstudio-selftest.txt",
     "opening the studio is offered as a pane action"),

    # The host-side checks repaired in the same lane, each with the regression it now catches.
    ("SpriteBounds' visible-pixel scan inverts its visibility test",
     os.path.join(REPO, "src", "dotNet", "SpriteBounds.cs"),
     b"                if (maxX < minX || maxY < minY) return new Rectangle(0, 0, w, h);   // nothing visible -> full frame",
     b"                if (maxX < minX || maxY < minY) return new Rectangle(0, 0, w, h);   // nothing visible -> full frame\n"
     b"                minX = 0;",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt",
     "sprite visible-bounds"),


    # ---- lane fix/host ----

    # F352: the removal marker is cleared whatever happened, which is the code as it shipped: a locked
    # folder's uninstall is lost.
    ("a pending removal that could not finish is forgotten again",
     os.path.join(REPO, "src", "dotNet", "Plugins", "PendingModuleRemovals.cs"),
     b"            WriteIds(markerPath, unfinished);\n            return unfinished;",
     b"            WriteIds(markerPath, new List<string>());\n            return unfinished;",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt", "the locked module stays marked"),

    # F352: the loader stops honouring the pending-removal list (the folder never matches).
    ("the loader loads a folder whose removal is still pending",
     MODULE_HOST,
     b"                if (string.Equals(id, folder, StringComparison.OrdinalIgnoreCase)) return true;",
     b"                if (string.Equals(id, folder + \"-\", StringComparison.OrdinalIgnoreCase)) return true;",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "does not load a folder whose removal is pending"),

    # F353: the discarded payload stays on disk again.
    ("a discarded update leaves its staging folder behind again",
     os.path.join(REPO, "src", "dotNet", "Plugins", "PendingModuleUpdates.cs"),
     b"                        discard = true;\n"
     b"                        if (log != null) log(\"module '\" + id + \"' is no longer installed; discarded its update\");",
     b"                        if (log != null) log(\"module '\" + id + \"' is no longer installed; discarded its update\");",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "a discarded payload's staging folder is removed"),

    # F353: the strand sweep is dropped from the launch path.
    ("abandoned staging folders are never swept",
     os.path.join(REPO, "src", "dotNet", "Plugins", "PendingModuleUpdates.cs"),
     b"            SweepStrands(modulesRoot, stagingRoot, unfinished, log);\n",
     b"",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "unmarked staging folder older than the age limit is swept"),

    # F343: the refusal is filed under Info.Id again, which the pane cannot match to a folder.
    ("a MinHostVersion refusal is keyed by Info.Id again",
     MODULE_HOST,
     b"                            Id = folder,\n",
     b"                            Id = declaredId.Length > 0 ? declaredId : folder,\n",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt", "keyed by the folder name"),

    # F344: an Init that throws after contributing keeps its contributions (the code as it shipped).
    ("a module whose Init threw keeps its tray items, pane and subscriptions",
     MODULE_HOST,
     b"                            else attributing.RollBackModuleInit();",
     b"                            else attributing.EndModuleInit();",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt", "holds no tray item, no pane"),

    # F339: stage two takes the first match again instead of failing on more than one.
    ("the self-test finder hands over the first of two module-type matches",
     os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleConventionSelfTest.cs"),
     b"            if (moduleCandidates.Count == 1) { entry = moduleCandidates[0]; return true; }",
     b"            if (moduleCandidates.Count >= 1) { entry = moduleCandidates[0]; return true; }",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt", "reported as ambiguous"),

    # F339: GetMethod(name) again, whose AmbiguousMatchException is swallowed into "no SelfTest".
    ("an overload beside SelfTest(out string) hides it again",
     os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleConventionSelfTest.cs"),
     b"            try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static); }\n"
     b"            catch { return null; }",
     b"            try { methods = new[] { type.GetMethod(\"SelfTest\", BindingFlags.Public | BindingFlags.Static) }; }\n"
     b"            catch { return null; }\n"
     b"            if (methods[0] == null) return null;",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt", "no longer hides it"),

    # F345: the data root is no longer redirected before the module Inits run.
    ("--module-host-selftest runs module Inits against the real data root again",
     MODULE_HOST_SELFTEST,
     b"                Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, dataRootScratch);\n",
     b"",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt", "data root isolated for this run"),

    # F328: a throwing responder takes the chain down with it instead of being treated as declined.
    ("a throwing responder aborts the chain",
     HOST,
     b"                    Log(r.ModuleId, \"responder threw and was treated as declined: \" + ex.GetType().Name + \": \" + ex.Message);\n"
     b"                }\n"
     b"                if (handled) return true;",
     b"                    Log(r.ModuleId, \"responder threw and was treated as declined: \" + ex.GetType().Name + \": \" + ex.Message);\n"
     b"                    throw;\n"
     b"                }\n"
     b"                if (handled) return true;",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt", "treated as declined"),

    # F354: the top-level copy comes back, and the isolated PetStudio runs without its native\ folder.
    ("--petstudio-selftest copies the module's top level only again",
     os.path.join(REPO, "src", "dotNet", "Plugins", "PetStudioModuleSelfTest.cs"),
     b"                SelfTestScratch.CopyTree(bundled, dest);\n",
     b"                Directory.CreateDirectory(dest);\n"
     b"                foreach (string file in Directory.GetFiles(bundled))\n"
     b"                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);\n",
     HOST_CSPROJ, EXE,
     "--petstudio-selftest", "dp-petstudio-selftest.txt", "carries the module's subfolders"),

    # F249/F336: Forget stops telling the pane's caches, so a Studio install keeps the old icon and counts.
    # The raise is gated on a condition the early return above it makes impossible, rather than deleted:
    # an event that is declared and never raised is CS0067, and warnings are errors, so the deletion
    # would not compile and the case would prove nothing.
    ("CompanionCatalog.Forget stops raising Forgotten",
     os.path.join(REPO, "src", "dotNet", "CompanionCatalog.cs"),
     b"            if (listeners != null) { try { listeners(id); } catch { } }\n",
     b"            if (listeners != null && id.Length == 0) { try { listeners(id); } catch { } }\n",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "Forget raises Forgotten"),

    # F250: the header read stops at the old 32K again.
    ("the header read stops at 32K again",
     os.path.join(REPO, "src", "dotNet", "CompanionCatalog.cs"),
     b"        internal const int HeaderReadBoundChars = CompanionXmlValidator.MaximumIconBytes * 4 / 3 + 70 * 1024;",
     b"        internal const int HeaderReadBoundChars = 32 * 1024;",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "past the old 32K read"),

    # F251: the header cache stops keying on the file's write time and length, so a rewrite is a hit.
    ("the header cache ignores a rewritten file",
     os.path.join(REPO, "src", "dotNet", "CompanionCatalog.cs"),
     b"                    if (HeaderByPath.TryGetValue(xmlPath, out hit) && hit.WrittenUtc == writtenUtc && hit.Length == length)",
     b"                    if (HeaderByPath.TryGetValue(xmlPath, out hit))",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "a rewritten file is read again"),

    # F251: nothing is ever found in the cache, so every look reads the file (the code as it shipped). The
    # entry is stored under a key no lookup uses rather than not stored at all: deleting the store leaves
    # CachedHeader's fields never assigned, which is CS0649, and warnings are errors.
    ("the header read is uncached again",
     os.path.join(REPO, "src", "dotNet", "CompanionCatalog.cs"),
     b"                    HeaderByPath[xmlPath] = new CachedHeader { WrittenUtc = writtenUtc, Length = length, Name = name };",
     b"                    HeaderByPath[xmlPath + \"#never-found\"] = new CachedHeader { WrittenUtc = writtenUtc, Length = length, Name = name };",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "do not read it again"),

    # F361: a batched setter never marks the batch dirty, so Commit writes nothing.
    ("a batch commit writes nothing",
     os.path.join(REPO, "src", "Portable", "LocalData.cs"),
     b"                    _batchDirty = true;\n                    return true;",
     b"                    return true;",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "Commit is ONE durable write"),

    # F245: the one place a device failure is visible is PlaybackStopped. Drop the subscription and the
    # output stays 'started' on a device that does not exist, which is the code as it shipped.
    ("AudioOutput stops observing PlaybackStopped",
     os.path.join(REPO, "src", "dotNet", "AudioOutput.cs"),
     b"                output.PlaybackStopped += OnPlaybackStopped;\n",
     b"",
     HOST_CSPROJ, EXE,
     "--audio-selftest", "dp-audio-selftest.txt",
     "asynchronous failure is observed"),


    # ---- lane fix/tools ----


    # ---- lane fix/remembrance ----


    # ---- lane fix/blinkingled ----


    # ---- lane fix/aibrain ----


    # ---- lane fix/fortunes ----


    # ---- lane fix/petstudio ----


    # ---- lane fix/reminder ----


    # ---- lane fix/deadcode ----
)

BASELINES = (
    ("--hardening-selftest", "dp-hardening-selftest.txt"),
    ("--pettyperegistry-selftest", "dp-pettyperegistry-selftest.txt"),
    ("--aibrain-selftest", "dp-aibrain-selftest.txt"),
    ("--fortunes-selftest", "dp-fortunes-selftest.txt"),
    ("--module-host-selftest", "dp-module-host-selftest.txt"),
    ("--fortunes-engine-selftest", "dp-fortunes-engine-selftest.txt"),
    ("--petstudio-selftest", "dp-petstudio-selftest.txt"),
    ("--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt"),
    ("--audio-selftest", "dp-audio-selftest.txt"),
    (CORETESTS, None),
)


def read(path):
    with io.open(path, "rb") as handle:
        return handle.read()


def write(path, data):
    with io.open(path, "wb") as handle:
        handle.write(data)


def build(csproj):
    proc = subprocess.run(["dotnet", "build", csproj, "-c", "Release", "--nologo", "-v:quiet"],
                          capture_output=True, text=True, timeout=1800)
    return proc.returncode == 0, (proc.stdout or "")


def build_all():
    for csproj in (HOST_CSPROJ, FORTUNES_CSPROJ, BLINKINGLED_CSPROJ, PETSTUDIO_CSPROJ, CORETESTS_CSPROJ):
        ok, out = build(csproj)
        if not ok:
            return False, out
    return True, ""


def selftest(flag, marker):
    """(report, exit code) for one run, or (None, why) when nothing can be concluded from it.

    The marker is deleted first and the delete is ASSERTED: a marker that cannot be removed would
    otherwise be read as this run's verdict (the STALEMARKER hole Test-ModuleSelfTests.ps1 closed on
    2026-09-28). A run that exits without writing one is a non-result too, never a pass.
    """
    if flag == CORETESTS:
        return coretests()
    path = os.path.join(RUN_TEMP, marker)
    try:
        os.remove(path)
    except OSError:
        pass
    if os.path.isfile(path):
        return None, "stale marker could not be removed: " + path
    try:
        proc = subprocess.run([EXE, flag], capture_output=True, text=True, timeout=1800, env=CHILD_ENV)
    except subprocess.TimeoutExpired:
        return None, "%s did not exit in 1800s" % flag
    if not os.path.isfile(path):
        return None, "no marker written (exit %d)" % proc.returncode
    with io.open(path, encoding="utf-8", errors="replace") as handle:
        return handle.read(), proc.returncode


def coretests():
    """CoreTests reshaped into the marker vocabulary the ladder below already grades.

    It has no marker file: PASS: lines go to stdout, and a failure is one 'FAIL: N regression
    group(s).' header on stderr followed by indented '<group>: <message>' lines. Each indented line
    becomes 'FAIL: <group>: <message>', so a case can name its group as the expected fragment, and a
    column-0 RESULT= line carries the exit code, which is the harness's real verdict.
    """
    if not os.path.isfile(CORETESTS_EXE):
        return None, "CoreTests exe is missing: " + CORETESTS_EXE
    # WITHOUT the data-root override, on purpose. CoreTests writes only under its own directory in
    # TEMP (which CHILD_ENV already redirects) and never touches the real data root, and its
    # current-directory-independence group has two arms: the one with content -- the anchored
    # legacy-settings list -- runs only when DESKTOP_AI_COMPANION_DATA_ROOT is unset, exactly as CI
    # runs it. A harness that inherited the override from an isolated shell would score the F383
    # mutation SURVIVED against the empty arm.
    env = dict(CHILD_ENV)
    env.pop("DESKTOP_AI_COMPANION_DATA_ROOT", None)
    try:
        proc = subprocess.run([CORETESTS_EXE], capture_output=True, text=True, timeout=1800, env=env)
    except subprocess.TimeoutExpired:
        return None, "CoreTests did not exit in 1800s"
    lines = (proc.stdout or "").splitlines()
    in_failures = False
    for line in (proc.stderr or "").splitlines():
        if line.startswith("FAIL:"):
            in_failures = True
            lines.append(line)
        elif in_failures and line.startswith("  ") and line.strip():
            lines.append("FAIL: " + line.strip())
        else:
            in_failures = False
            lines.append(line)
    lines.append("RESULT=PASS" if proc.returncode == 0 else "RESULT=FAIL")
    return "\n".join(lines) + "\n", proc.returncode


def line_ending_variant(base, old, new):
    """Pick the (old, new) pair whose line endings match the FILE being mutated.

    The patterns in this file are written with LF. Several targets are CRLF in the working tree --
    RuntimeHardeningSelfTest.cs is 1317 CRLF lines and 0 bare LF -- and a CRLF file cannot contain an
    LF pattern, so those cases printed "NO-OP (pattern matched 0 times)" and proved nothing. The
    release checklist is explicit that a NO-OP is worse than a failure, because it looks like a
    result; this is the mechanism behind every no-op measured on 2026-09-25 across the three
    mutation harnesses, and not one of them was the moved call site they were read as.

    Restoring is unaffected: the loop writes back the ORIGINAL bytes it read.
    """
    if base.count(old) == 1:
        return old, new
    as_crlf = lambda b: b.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    crlf_old, crlf_new = as_crlf(old), as_crlf(new)
    if base.count(crlf_old) == 1:
        return crlf_old, crlf_new
    return old, new


def _verdict_lines(report, prefixes):
    """Lines whose prefix-stripped form starts with one of `prefixes`, allowing the "[moduleid] "
    prefix ModuleConventionSelfTest puts in front of a module's own lines."""
    out = []
    for line in report.splitlines():
        stripped = line.strip()
        if stripped.startswith("[") and "] " in stripped:
            stripped = stripped.split("] ", 1)[1].strip()
        if stripped.startswith(prefixes):
            out.append(line.strip())
    return out


def failure_lines(report):
    """The lines a self-test marker uses to report a FAILED ASSERTION, and only those.

    `"FAIL" in report` is not that test, and the difference is not academic: the aibrain self-test
    carries a PASSING assertion labelled "an empty reply is reported as a FAILURE, not a green tick",
    and a bare substring match reads that word inside a green report as a failure. The effect was
    total -- this harness refused its own baseline with "BASELINE NOT GREEN for --aibrain-selftest"
    against a self-test that exits 0 with every line PASS, so the whole suite had been inert since
    the day that label was written.

    A failure line is one whose stripped form STARTS with FAIL, allowing for the "[moduleid] "
    prefix the module self-tests put in front of it. A label mentioning failure anywhere else on the
    line is prose, not a verdict.
    """
    return _verdict_lines(report, ("FAIL",))


def aborted_lines(report):
    """EXC: (a self-test that threw instead of asserting) and SKIP: (one that did not run part of
    itself). Neither is a failed assertion, and neither is a pass: a report carrying one has no
    verdict on the mutation, which is the third outcome this ladder had been missing (F405). The
    same three prefixes Invoke-SelfTests.ps1 greps for."""
    return _verdict_lines(report, ("EXC", "SKIP:"))


def has_failure(report):
    return bool(failure_lines(report))


def passed(report):
    """A column-0 RESULT=PASS line, unstripped. ModuleConventionSelfTest re-emits the module's own
    RESULT=PASS as '  [<id>] RESULT=PASS', so `"RESULT=PASS" in report` is true under an outer
    RESULT=FAIL whenever the module's probe passed and a host-side convention check did not."""
    return any(line.startswith("RESULT=PASS") for line in report.splitlines())


def unhealthy(report, code):
    """Why a report is not a clean pass, or None when it is. All three channels must agree: the exit
    code (Program.cs exits Run() ? 0 : 1), the column-0 verdict line, and the absence of any
    FAIL/EXC/SKIP line."""
    problems = []
    if code != 0:
        problems.append("exit code %d" % code)
    if not passed(report):
        problems.append("no RESULT=PASS at column 0")
    problems.extend(failure_lines(report)[:3])
    problems.extend(aborted_lines(report)[:3])
    return "; ".join(problems) if problems else None


def score(args):
    print("baseline: build + every self-test in play must pass")
    ok, out = build_all()
    if not ok:
        print("BASELINE BUILD FAILED")
        print(out[-800:])
        return 2
    for flag, marker in BASELINES:
        report, code = selftest(flag, marker)
        if report is None:
            print("BASELINE NOT GREEN for", flag, "--", code)
            return 2
        why = unhealthy(report, code)
        if why:
            print("BASELINE NOT GREEN for", flag, "--", why)
            return 2
    print("  baseline clean\n")

    cases = [c for c in CASES if args.only is None or args.only.lower() in c[0].lower()]
    if not cases:
        print("no case matched --only=%s" % args.only)
        return 2

    fired = 0
    for (name, path, old, new, csproj, artifact, flag, marker, expect) in cases:
        base = read(path)
        old_v, new_v = line_ending_variant(base, old, new)
        if base.count(old_v) != 1:
            print("  %-52s NO-OP (pattern matched %d times)" % (name, base.count(old_v)))
            continue
        before = os.path.getmtime(artifact)
        write(path, base.replace(old_v, new_v))
        time.sleep(1.1)
        report = None
        code = None
        verdict = None
        try:
            built, _ = build(csproj)
            if not built:
                verdict = "BROKEN (does not compile)"
            elif os.path.getmtime(artifact) <= before:
                verdict = "BROKEN (%s not rebuilt)" % os.path.basename(artifact)
            else:
                report, code = selftest(flag, marker)
        finally:
            write(path, base)

        if verdict is not None:
            print("  %-52s %s" % (name, verdict))
            continue
        if report is None:
            print("  %-52s BROKEN (%s)" % (name, code))
            continue
        # NOT startswith("FAIL"). A module's own assertions are re-emitted by
        # ModuleConventionSelfTest with a '  [<id>] ' prefix, so a real firing reads
        # '  [blinkingled] FAIL: ...' and never starts with FAIL at all -- which made two correct
        # cases report WRONG, pointing at the outer 'the module's own self-test passed' instead.
        # mutate-agentflow.py already carried this scar; this file had not learned it.
        # Excluding PASS lines is what keeps it honest: an assertion label can contain the word
        # 'failed' and several do.
        hit = [l for l in failure_lines(report) if expect in l]
        aborted = aborted_lines(report)
        if hit:
            fired += 1
            print("  %-52s FIRED" % name)
            print("        %s" % hit[0][:140])
        elif has_failure(report):
            print("  %-52s WRONG -- failed elsewhere" % name)
            for line in failure_lines(report)[:2]:
                print("        %s" % line[:140])
        elif aborted or code != 0 or not passed(report):
            # No FAIL line, but no clean pass either: the self-test threw, skipped, or exited
            # without a column-0 verdict. That is not "the check still passed", so it is never
            # SURVIVED; it is a run that proved nothing about the assertion.
            print("  %-52s BROKEN (no verdict: %s)" % (
                name, (aborted[0] if aborted else "exit %s, RESULT=PASS %s" % (
                    code, "present" if passed(report) else "absent"))[:120]))
        else:
            print("  %-52s SURVIVED" % name)

    print("\nrestoring and rebuilding the clean tree")
    build_all()
    print("%d/%d fired." % (fired, len(cases)))
    return 0 if fired == len(cases) else 1


def main():
    global RUN_TEMP, CHILD_ENV
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", default=None)
    args = parser.parse_args()

    # Short on purpose: the fortunes VectorCache replace-fallback probe fails once TEMP itself reaches
    # ~107 characters (its temp path hits MAX_PATH in a MoveFileEx P/Invoke), and a runner must not
    # fail a self-test on a path length it never sees in production. Measured 2026-09-29.
    RUN_TEMP = os.path.join(TEMP, "dp-msg-" + uuid.uuid4().hex[:12])
    os.makedirs(RUN_TEMP)
    CHILD_ENV = dict(os.environ)
    CHILD_ENV["TEMP"] = RUN_TEMP
    CHILD_ENV["TMP"] = RUN_TEMP
    try:
        return score(args)
    finally:
        shutil.rmtree(RUN_TEMP, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
