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
# The fixture module --module-host-selftest loads for its rollback and finder checks. Built with the fixed
# set since 2026-09-30: a merge that changed it (fix/host) left the pre-merge DLL in the build folder and
# the baseline went red on F344's rollback checks for a defect that was not in the tree.
TESTMODULE_CSPROJ = os.path.join(REPO, "modules", "TestModule", "TestModule.csproj")
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
PETREPORT = os.path.join(REPO, "modules", "PetStudio", "PetReport.cs")
PETSTUDIO_WINDOW = os.path.join(REPO, "modules", "PetStudio", "PetStudioWindow.cs")
ANIM_CAPABILITY = os.path.join(REPO, "modules", "PetStudio", "AnimCapability.cs")
BLINKINGLED_DLL = os.path.join(BIN, "modules", "blinkingled", "BlinkingLed.dll")
PETSTUDIO_DLL = os.path.join(BIN, "modules", "petstudio", "PetStudio.dll")
BLINKINGLED_CSPROJ = os.path.join(REPO, "modules", "BlinkingLed", "BlinkingLed.csproj")
PETSTUDIO_CSPROJ = os.path.join(REPO, "modules", "PetStudio", "PetStudio.csproj")
MODULE_HOST = os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleHost.cs")
REMINDER_CSPROJ = os.path.join(REPO, "modules", "Reminder", "Reminder.csproj")
REMINDER_DLL = os.path.join(BIN, "modules", "reminder", "Reminder.dll")
REMINDER_PARSER = os.path.join(REPO, "modules", "Reminder", "PersonalReminderParser.cs")
CACHING_CALENDAR_SOURCE = os.path.join(REPO, "modules", "Reminder", "CachingCalendarSource.cs")
REMINDER_MODULE = os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs")
TESTMODULE_CS = os.path.join(REPO, "modules", "TestModule", "TestModule.cs")
TESTMODULE_CSPROJ = os.path.join(REPO, "modules", "TestModule", "TestModule.csproj")
TESTMODULE_DLL = os.path.join(BIN, "modules", "testmodule", "TestModule.dll")
REMINDER_QUIET_HOURS = os.path.join(REPO, "modules", "Reminder", "QuietHours.cs")
REMINDER_ICS = os.path.join(REPO, "modules", "Reminder", "IcsUrlSource.cs")
AIBRAIN_CSPROJ = os.path.join(REPO, "modules", "AiBrain", "AiBrain.csproj")
AIBRAIN_DLL = os.path.join(BIN, "modules", "aibrain", "AiBrain.dll")
AIBRAIN_ENGINE = os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs")
EMBEDDER = os.path.join(REPO, "modules", "Fortunes", "engine", "Embedder.cs")
SMART_FORTUNES = os.path.join(REPO, "modules", "Fortunes", "engine", "SmartFortunes.cs")
FORTUNE_IMPORTER = os.path.join(REPO, "modules", "Fortunes", "engine", "FortuneFileImporter.cs")
MODULE_HOST_SELFTEST = os.path.join(REPO, "src", "dotNet", "Plugins", "ModuleHostSelfTest.cs")
FORTUNES_ENGINE_SELFTEST = os.path.join(REPO, "src", "dotNet", "Plugins", "FortunesEngineSelfTest.cs")
REMEMBRANCE_CSPROJ = os.path.join(REPO, "modules", "Remembrance", "Remembrance.csproj")
REMEMBRANCE_DLL = os.path.join(BIN, "modules", "remembrance", "Remembrance.dll")
REMEMBRANCE_MODULE = os.path.join(REPO, "modules", "Remembrance", "RemembranceModule.cs")
AUDIO_RECORDER = os.path.join(REPO, "modules", "Remembrance", "AudioRecorder.cs")
CAPTURE_STORE = os.path.join(REPO, "modules", "Remembrance", "CaptureStore.cs")
OLLAMA_SUMMARIZER = os.path.join(REPO, "modules", "Remembrance", "OllamaSummarizer.cs")
TRANSCRIBER = os.path.join(REPO, "modules", "Remembrance", "Transcriber.cs")
WHISPER_INSTALLER = os.path.join(REPO, "modules", "Remembrance", "WhisperInstaller.cs")

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
MODULEKIT_FAKES = os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "Testing", "Fakes.cs")
MODULEKIT_RECORDING_HOST = os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "Testing", "RecordingHost.cs")
# ModuleKit.dll as COPIED into a module's folder by its ProjectReference: a ModuleKit edit recompiles
# ModuleKit and the module build copies the new DLL beside the module, while the module's own DLL need not
# move, so this copy is the artefact for a module self-test case whose mutation lives in ModuleKit.
BLINKINGLED_MODULEKIT_DLL = os.path.join(BIN, "modules", "blinkingled", "DesktopAICompanion.ModuleKit.dll")
APPSETTINGS_STORE = os.path.join(REPO, "src", "Portable", "AppSettingsStore.cs")
APPPATHS = os.path.join(REPO, "src", "Portable", "AppPaths.cs")
# The pseudo-flag a case names to run CoreTests instead of the host exe. Its marker is None.
CORETESTS = "CORETESTS"
CORETESTS_PROGRAM = os.path.join(REPO, "tests", "DesktopAICompanion.CoreTests", "Program.cs")

# --security-selftest writes no marker either: SecuritySelfTest.Check prints "[PASS] x" / "[FAIL] x" to
# stdout and Program exits Run() ? 0 : 1 (tests/Invoke-SelfTests.ps1 registers it with a $null marker for
# the same reason). This pseudo-flag reshapes that stdout into the marker vocabulary the ladder grades, the
# way CORETESTS does. Until 2026-09-30 nothing in this file could grade a security assertion at all, so the
# checks lane fix/deadcode repaired there (F298, F300) had nowhere to prove they fire.
SECURITY = "SECURITY"
SECURITY_SELFTEST = os.path.join(REPO, "src", "dotNet", "SecuritySelfTest.cs")
ANIMATIONS = os.path.join(REPO, "src", "dotNet", "Animations.cs")
XML_CS = os.path.join(REPO, "src", "dotNet", "Xml.cs")
RUNTIME_GEOMETRY = os.path.join(REPO, "src", "dotNet", "RuntimeGeometry.cs")
AISETTINGS = os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs")
AIBRAIN_MODULE = os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs")
AIENGINE_SECURITY = os.path.join(REPO, "modules", "AiBrain", "engine", "AiEngineProbe.Security.cs")

# The Shimeji converter is a third runner: the gate's last two steps build tools\ShimejiConvert and run its
# `verify` and `selftest` verbs, and the emitter under test (tools\ShimejiConvert.Engine) is also
# source-linked into PetStudio. The engine DLL as copied into the CLI's output by its ProjectReference is
# the artefact: an engine edit recompiles the engine, not the CLI, so ShimejiConvert.exe's timestamp does
# not move and only that copy proves the mutated code is what the run loaded (lane fix/followups).
SHIMEJI_CSPROJ = os.path.join(REPO, "tools", "ShimejiConvert", "ShimejiConvert.csproj")
SHIMEJI_EXE = os.path.join(REPO, "tools", "ShimejiConvert", "bin", "Release", "ShimejiConvert.exe")
SHIMEJI_ENGINE_DLL = os.path.join(REPO, "tools", "ShimejiConvert", "bin", "Release", "ShimejiConvert.Engine.dll")
PET_EMITTER = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "Emit", "PetEmitter.cs")
# The pseudo-flag a case names to run the converter's selftest verb. Its marker is None.
SHIMEJI = "SHIMEJI"

# Lane burn/host-shell targets.
PENDING_REMOVALS = os.path.join(REPO, "src", "dotNet", "Plugins", "PendingModuleRemovals.cs")
PENDING_UPDATES = os.path.join(REPO, "src", "dotNet", "Plugins", "PendingModuleUpdates.cs")
WEBLINKS = os.path.join(REPO, "src", "Portable", "WebLinks.cs")
MODULEKIT_CSPROJ = os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "DesktopAICompanion.ModuleKit.csproj")
MODULEKIT_JSONSTORE = os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "JsonSettingsStore.cs")
MODULEKIT_MEMORY_SETTINGS = os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "MemoryModuleSettings.cs")
MODULEKIT_WAV = os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "WavAudio.cs")
LOCALDATA = os.path.join(REPO, "src", "Portable", "LocalData.cs")
AUDIO_OUTPUT = os.path.join(REPO, "src", "dotNet", "AudioOutput.cs")
AUDIO_SELFTEST = os.path.join(REPO, "src", "dotNet", "AudioOutputSelfTest.cs")
WPF_SELFTEST = os.path.join(REPO, "src", "dotNet", "WpfOptionsSelfTest.cs")
COMPANION_CATALOG = os.path.join(REPO, "src", "dotNet", "CompanionCatalog.cs")
FORTUNES_SELFTEST = os.path.join(REPO, "src", "dotNet", "Plugins", "FortunesModuleSelfTest.cs")
PETSTUDIO_FIXTURE = os.path.join(REPO, "modules", "PetStudio", "Resources", "selftest-companion.xml")
PETSTUDIO_REPORT = os.path.join(REPO, "modules", "PetStudio", "PetReport.cs")
FORMCOMPANION_CS = os.path.join(REPO, "src", "dotNet", "FormCompanion.cs")
VALIDATOR_CS = os.path.join(REPO, "src", "dotNet", "CompanionXmlValidator.cs")
SECURE_DOWNLOAD_CS = os.path.join(REPO, "src", "dotNet", "SecureDownload.cs")
TYPE_REGISTRY = os.path.join(REPO, "src", "dotNet", "CompanionTypeRegistry.cs")

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
    # best-effort promise its own comment makes. Re-pointed 2026-09-30 by lane burn/host-shell (RA-280):
    # PublishContext raises through RaiseEach now, so "stops raising" hands RaiseEach no delegate and
    # "takes down the tick" invokes the multicast bare.
    ("publishing context stops raising ContextChanged",
     COMPANION_HOST,
     b"            RaiseEach<Action<string>>(_contextChanged, \"ContextChanged\", h => h(key));",
     b"            RaiseEach<Action<string>>(null, \"ContextChanged\", h => h(key));",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "publishing RAISES ContextChanged"),

    ("a throwing subscriber takes down the publisher's tick",
     COMPANION_HOST,
     b"            RaiseEach<Action<string>>(_contextChanged, \"ContextChanged\", h => h(key));",
     b"            Action<string> whole = _contextChanged; if (whole != null) whole(key);",
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
    # Re-pointed 2026-10-01 (lane burn/host-shell): fb167fc put PublishSnapshot() between the Add and the
    # count (RA-282), so the two-line pattern matched zero times and the whole harness exited 1. Same defect,
    # same place: a failure recorded for every module the loader accepts.
    ("LoadFrom records a spurious failure for every module it loads",
     MODULE_HOST,
     b"                    _loaded.Add(new Loaded { Module = module, Alc = alc });\n"
     b"                    PublishSnapshot();\n"
     b"                    count++;",
     b"                    _loaded.Add(new Loaded { Module = module, Alc = alc });\n"
     b"                    PublishSnapshot();\n"
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
     b"                if (!QuietHours.TryParseTimeOfDay(tok, out hhmm)) { error = \"After 'at', give a time like 15:00. \" + Help; return false; }\n"
     b"                r.Kind = PersonalReminder.KindOnce; r.When = TodayOrTomorrowAt(now, hhmm); r.Text = text;\n"
     b"            }\n",
     b"",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "PersonalReminderParser"),

    # Re-pointed 2026-09-29 by lane fix/reminder: DoRefresh took a generation argument for F186, and the
    # whole-harness run reported this case NO-OP against the old shape.
    ("the calendar feed is read inline on the caller's thread",
     CACHING_CALENDAR_SOURCE,
     b"                Task.Run(() => DoRefresh(key, generation));",
     b"                DoRefresh(key, generation);",
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

    # Re-pointed 2026-09-30 by lane fix/followups: the recorded lists are appended under a lock into private
    # fields (N-remembrance-01), so the `OpenedLinks.Add` shape matched 0 times. Same regression, same
    # expected assertion.
    ("RecordingHost.OpenLink stops refusing a module without Network",
     os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "Testing", "RecordingHost.cs"),
     b"            if (Refuses(ModulePermissions.Network)) return false;\n"
     b"            lock (_recordSync) _openedLinks.Add(httpsUrl ?? \"\");",
     b"            lock (_recordSync) _openedLinks.Add(httpsUrl ?? \"\");",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit recording host"),

    # BlinkingLed's corrective toggle in Stop() (F114). The stopProbe check read PhaseOn, which Stop()
    # resets unconditionally, so the toggle block could be deleted or its gate dropped under a green
    # suite. Both regressions, one case each: the block gone, and the key read gone.
    #
    # Re-pointed 2026-09-29 by lane fix/blinkingled: Stop() now keeps the belief when the corrective
    # toggle is refused (F116), so the block reads `stillOurs = ScrollLockReader() && !Toggle()` and
    # the old patterns matched 0 times. Same two regressions, same two expected assertions.
    ("Stop()'s corrective toggle block is deleted",
     SCROLLLOCK_BLINKER,
     b"                bool stillOurs;\n"
     b"                try { stillOurs = ScrollLockReader() && !Toggle(); }\n"
     b"                catch { stillOurs = true; }   // unknown: keep the belief, a retry costs one keypress\n"
     b"                _phaseOn = stillOurs;",
     b"                _phaseOn = false;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "Stop() attempts the corrective toggle"),

    ("Stop() toggles without reading the key first",
     SCROLLLOCK_BLINKER,
     b"                try { stillOurs = ScrollLockReader() && !Toggle(); }",
     b"                try { stillOurs = !Toggle(); }",
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
    # Re-pointed 2026-10-01 (lane burn/host-shell): fb167fc wrapped the final marker rewrite in a try/catch
    # that logs a failed write (RA-296), so the bare two-line pattern matched zero times. Same defect, same
    # place: the marker rewritten EMPTY whatever happened, so a locked folder's uninstall is forgotten.
    ("a pending removal that could not finish is forgotten again",
     os.path.join(REPO, "src", "dotNet", "Plugins", "PendingModuleRemovals.cs"),
     b"            try { WriteIds(markerPath, unfinished); }\n",
     b"            try { WriteIds(markerPath, new List<string>()); }\n",
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


    # F318's no-stage case ("the no-stage loader decodes the sprite sheet anyway") stood here until the
    # overload it targeted went with RA-270 / RA-272 (lane burn/host-shell, 2026-10-01).

    # F241: the chooser evaluates the chosen animation itself again, which the counter sees.
    ("the chooser evaluates an expression again",
     os.path.join(REPO, "src", "dotNet", "Animations.cs"),
     b'            TAnimation ani = SheepAnimations[id];\n            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "new animation: "',
     b'            TAnimation ani = SheepAnimations[id];\n            ani.UpdateValues();\n            StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "new animation: "',
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "choosing the next animation evaluates no expression"),

    # F271: the fixed 12 MiB drop buffer comes back, which the allocation count sees.
    ("the drop read allocates the 12 MiB ceiling again",
     os.path.join(REPO, "src", "dotNet", "FormCompanion.cs"),
     b"            bytes = new byte[(int)BoundedReadCapacity(stream, maximumBytes)];\n",
     b"            bytes = new byte[checked(maximumBytes + 1)];\n",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "allocating under 1 MiB"),

    # F255: the tree disposal stops disposing an item's Image.
    ("DisposeItemTree leaves the Image alive",
     os.path.join(REPO, "src", "dotNet", "ContextMenus.cs"),
     b"                if (image != null) { item.Image = null; image.Dispose(); }\n",
     b"                if (image != null) { item.Image = null; }\n",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "disposes a submenu child's Image"),

    # F260: a listing that throws is logged but no longer counted as a failure.
    ("SafeList stops counting a listing that throws",
     os.path.join(REPO, "src", "dotNet", "FactoryReset.cs"),
     b'                failed++;\n                if (log != null) log.Add("    could not list " + what',
     b'                if (log != null) log.Add("    could not list " + what',
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "counts as a failure and is logged"),

    # F273: the row cap is twice what it says.
    ("the debug window's row cap is doubled",
     os.path.join(REPO, "src", "dotNet", "FormDebug.cs"),
     b"\t\t\twhile (listView1.Items.Count > MaxRows)\n",
     b"\t\t\twhile (listView1.Items.Count > MaxRows * 2)\n",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "the row count is capped at MaxRows"),

    # F274: the debug text goes to a random file name instead of one fixed file per kind.
    ("the debug text file gets a random name",
     os.path.join(REPO, "src", "dotNet", "FormDebug.cs"),
     b'\t\t\t\t"dp-debug-" + (safe.Length == 0 ? "text" : safe.ToString()) + ".txt");\n',
     b'\t\t\t\t"dp-debug-" + (safe.Length == 0 ? "text" : safe.ToString()) + Guid.NewGuid().ToString("N") + ".txt");\n',
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "one per-kind file under TEMP"),

    # F325: a quote inside a label is no longer escaped.
    ("the DOT label leaves a quote unescaped",
     os.path.join(REPO, "src", "Tools", "XmlToDot.cs"),
     b"\t\t\t\t\tcase '\"': escaped.Append(\"\\\\\\\"\"); break;\n",
     b"\t\t\t\t\tcase '\"': escaped.Append('\"'); break;\n",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "escaped in the label"),

    # F229: the segment-boundary rule answers true for any suffix, so the decoy wins again.
    ("MatchesResourceName accepts a suffix that starts mid-segment",
     os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "EmbeddedResources.cs"),
     b"            return manifestName[manifestName.Length - fileNameSuffix.Length - 1] == '.';\n",
     b"            return true;\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit embedded resources"),

    # F230: Update writes defaults over an unreadable document again.
    ("JsonSettingsStore.Update writes over an unreadable document",
     os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "JsonSettingsStore.cs"),
     b"                        if (result == ReadResult.Unreadable) return false;\n",
     b"",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit json settings store"),

    # F230: the write keeps no backup.
    ("JsonSettingsStore.Save keeps no backup",
     os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "JsonSettingsStore.cs"),
     b"            return AtomicFile.TryWriteAllText(_path, json, BackupPath_);\n",
     b"            return AtomicFile.TryWriteAllText(_path, json, null);\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit json settings store"),

    # F231: Update proceeds without the lease.
    ("JsonSettingsStore.Update proceeds without the lease",
     os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "JsonSettingsStore.cs"),
     b"                        if (lease == null) return false;\n                        T current;\n",
     b"                        if (lease == null) { }\n                        T current;\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit json settings store"),

    # N-host-04: a decided monitor can be re-decided by a lower window, so the rain window at the bottom of the
    # z-order re-blocks a monitor a normal window above it had released.
    ("a decided monitor is re-decided by a lower window",
     os.path.join(REPO, "src", "dotNet", "FullscreenScan.cs"),
     b"                    if (decided[i]) continue;\n",
     b"",
     HOST_CSPROJ, EXE,
     "--fullscreen-selftest", "dp-fullscreen-selftest.txt", "small-above-rain-left"),

    # N-host-04: the decider stops excluding the companions' own windows (F278's promise).
    ("the decider lets a companion's own window decide a monitor",
     os.path.join(REPO, "src", "dotNet", "FullscreenScan.cs"),
     b"                if (petHandles != null && petHandles.Contains(hWnd)) return true;\n",
     b"",
     HOST_CSPROJ, EXE,
     "--fullscreen-selftest", "dp-fullscreen-selftest.txt", "companion-above-rain-left"),

    # ---- lane fix/tools ----


    # ---- lane fix/remembrance ----
    # Every case names Remembrance.csproj and the module DLL the host loads, so a stale DLL cannot score
    # a mutation as survived. Named "remembrance: ..." so one --only=remembrance run covers the lane.

    # BUG-009 / F168: the capture is built inside a no-context window. Put the caller's context back in
    # play and the self-test's fake capture -- which reproduces NAudio's threading exactly -- posts
    # RecordingStopped to a dead message loop, the way the real one did on the shutdown path.
    ("remembrance: captures are built with the UI context current (BUG-009)",
     AUDIO_RECORDER,
     b"            SynchronizationContext.SetSynchronizationContext(null);\n"
     b"            try { return construct(); }",
     b"            try { return construct(); }",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "constructed with NO SynchronizationContext current"),

    ("remembrance: the shutdown save stops logging its timing (F168)",
     REMEMBRANCE_MODULE,
     b"                    Log(\"stopped on shutdown: audio saved as \" + paths.BaseName + \" in \" +\n"
     b"                        stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + \" ms (capture stop \" +\n"
     b"                        ((long)recorder.LastCaptureStopTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) +\n"
     b"                        \" ms, mix \" + ((long)recorder.LastMixTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) +\n"
     b"                        \" ms), transcription skipped\");",
     b"                    Log(\"stopped on shutdown: audio saved as \" + paths.BaseName + \", transcription skipped\");",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "logs how long the capture stop and the mix took"),

    # F169: a loopback source runs a silent render stream beside itself, or the system track goes short.
    ("remembrance: the loopback source loses its silent keep-alive (F169)",
     AUDIO_RECORDER,
     b"                try { s.KeepAlive = KeepAliveFactory(device, capture.WaveFormat); }\n"
     b"                catch (Exception ex) { s.KeepAlive = null; KeepAliveFailure = ex.Message; }",
     b"                s.KeepAlive = null;",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a loopback source gets a silent render stream"),

    # F171: the two scratch shapes in both purge branches, the header-only stub, and the empty folder.
    ("remembrance: the in-folder scratch shapes leave the purge (F171)",
     CAPTURE_STORE,
     b"                    || lower == FolderPrefix + AudioRecorder.SystemScratchSuffix\n"
     b"                    || lower == FolderPrefix + AudioRecorder.MicScratchSuffix\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a system scratch track inside a capture folder IS ours to purge"),

    ("remembrance: the root scratch shapes leave the purge (F171)",
     CAPTURE_STORE,
     b"                || IsScratchAudioName(lower)\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a flat-mode system scratch track IS ours to purge"),

    ("remembrance: a failed start keeps its header-only scratch (F171)",
     AUDIO_RECORDER,
     b"                DeleteIfEmptyRecording(systemTemp);\n"
     b"                DeleteIfEmptyRecording(micTemp);\n"
     b"                throw;",
     b"                throw;",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "header-only system scratch it had already created is deleted"),

    ("remembrance: the failed-start folder is kept (F171)",
     CAPTURE_STORE,
     b"                Directory.Delete(directory);\n"
     b"                return true;",
     b"                return false;",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "empty capture folder from a failed start is removed"),

    # F173: a fold that stopped early must say so, and a client timeout is not the user cancelling.
    ("remembrance: the fold stops in silence again (F173)",
     OLLAMA_SUMMARIZER,
     b"                if (coverage.Length > 0)\n"
     b"                {\n"
     b"                    text = text + \"\\n\\n\" + coverage;\n"
     b"                    result.Message = coverage;\n"
     b"                }\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a partial fold says which parts it covers"),

    ("remembrance: a model that timed out reads as cancelled again (F173)",
     OLLAMA_SUMMARIZER,
     b"                result.Message = cancellationToken.IsCancellationRequested\n"
     b"                    ? \"Summarizing was cancelled.\"\n"
     b"                    : \"The model did not answer within \" +\n"
     b"                      ((int)GenerationTimeout.TotalMinutes).ToString(CultureInfo.InvariantCulture) +\n"
     b"                      \" minutes; the transcript is unaffected.\";",
     b"                result.Message = \"Summarizing was cancelled.\";",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "ran out of time is reported as a timeout"),

    # F175: nothing is announced on the shutdown path.
    ("remembrance: the shutdown path announces again (F175)",
     REMEMBRANCE_MODULE,
     b"                _lastStatus = \"Saving (app closing): \" + paths.BaseName;\n",
     b"                _lastStatus = \"Saving (app closing): \" + paths.BaseName;\n"
     b"                Announce(\"Recording stopped. Saving and transcribing\xe2\x80\xa6\");\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "nothing is announced on the shutdown path"),

    # F176: the shutdown hook waits for a save a normal stop left in flight. Drop the wait from the hook
    # the host raises first and the mix is left to die with the process, as it did.
    ("remembrance: shutdown no longer waits for a save in flight (F176)",
     REMEMBRANCE_MODULE,
     b"            try { if (_recording) StopRecording(shuttingDown: true); } catch { }\n"
     b"            FlushPendingSave();\n"
     b"        }\n",
     b"            try { if (_recording) StopRecording(shuttingDown: true); } catch { }\n"
     b"        }\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "shutdown waits for a save still in flight"),

    # F177 / F181: the capture and encode leave the hotkey's thread. Inline the body and the probe that
    # holds the capture open sees the hotkey block on it.
    ("remembrance: the snapshot encodes on the hotkey thread again (F177, F181)",
     REMEMBRANCE_MODULE,
     b"                Task.Run(delegate\n"
     b"                {\n"
     b"                    bool ok = false;\n"
     b"                    try { ok = SnapshotCapture(png); }\n"
     b"                    catch (Exception) { ok = false; }\n"
     b"                    finally { Interlocked.Exchange(ref _snapshotInFlight, 0); }\n"
     b"                    Announce(ok ? \"Snapshot saved.\" : \"Snapshot failed.\");\n"
     b"                });",
     b"                {\n"
     b"                    bool ok = false;\n"
     b"                    try { ok = SnapshotCapture(png); }\n"
     b"                    catch (Exception) { ok = false; }\n"
     b"                    finally { Interlocked.Exchange(ref _snapshotInFlight, 0); }\n"
     b"                    Announce(ok ? \"Snapshot saved.\" : \"Snapshot failed.\");\n"
     b"                }",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "returns before the capture completes"),

    # F178 / F180: Init neither enumerates the endpoints nor purges.
    ("remembrance: Init enumerates the devices again (F178)",
     REMEMBRANCE_MODULE,
     b"            string[] renderNames = DeviceOptions(null, _settings.Get(\"sysDevice\", \"\"));",
     b"            string[] renderNames = DeviceOptions(AudioDevices.RenderDevices(), _settings.Get(\"sysDevice\", \"\"));",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "Init enumerates no WASAPI endpoints"),

    ("remembrance: Init purges again (F180)",
     REMEMBRANCE_MODULE,
     b"            _installCts = new CancellationTokenSource();\n"
     b"\n"
     b"            _hostShutdownHandler = OnHostShutdown;",
     b"            _installCts = new CancellationTokenSource();\n"
     b"            RunPurge();\n"
     b"\n"
     b"            _hostShutdownHandler = OnHostShutdown;",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "Init starts no purge"),

    # F182: whisper's limit follows the recording length.
    ("remembrance: whisper's limit is flat again (F182)",
     TRANSCRIBER,
     b"            double scaled = audioLength.TotalMilliseconds * WhisperTimeoutFactor;",
     b"            double scaled = MinimumWhisperTimeout.TotalMilliseconds;",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "an hour of audio gets four hours"),

    # F183: the download button opens the release list, never /latest.
    ("remembrance: the download button points at /latest again (F183)",
     WHISPER_INSTALLER,
     b"        public const string ReleasesPageUrl = \"https://github.com/ggml-org/whisper.cpp/releases\";",
     b"        public const string ReleasesPageUrl = \"https://github.com/ggml-org/whisper.cpp/releases/latest\";",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the human releases link is the LIST"),

    # F184: the release lookup gives up at its own bound.
    ("remembrance: the release lookup loses its bound (F184)",
     WHISPER_INSTALLER,
     b"                    bounded.CancelAfter(LookupBound);\n"
     b"                    using (HttpResponseMessage response = await http.GetAsync(ReleaseApiUrl, bounded.Token)",
     b"                    using (HttpResponseMessage response = await http.GetAsync(ReleaseApiUrl, bounded.Token)",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "gives up at its own bound"),

    # F226 (the in-boundary half): the LaunchProcess disclosure gates nothing, so only this notices it gone.
    ("remembrance: the LaunchProcess disclosure is dropped (F226)",
     REMEMBRANCE_MODULE,
     b"                | ModulePermissions.Network | ModulePermissions.LaunchProcess,",
     b"                | ModulePermissions.Network,",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "including the LaunchProcess disclosure"),


    # ---- lane fix/blinkingled ----

    # BUG-011 (F116, F115): the phase flag moves only with the key. Each mutation puts back one shipped
    # shape. The self-test drives acceptance and refusal through the engine's KeypressSender seam, so
    # these fire on a box that accepts every synthesized keypress and on a headless runner alike.
    ("a refused blink-once flips the phase again",
     SCROLLLOCK_BLINKER,
     b"            try { if (Toggle()) _phaseOn = !_phaseOn; }",
     b"            try { Toggle(); _phaseOn = !_phaseOn; }",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "a refused blink-once leaves the phase where it was"),

    ("a refused cadence tick flips the phase again",
     SCROLLLOCK_BLINKER,
     b"                if (Toggle()) _phaseOn = !_phaseOn;\n",
     b"                Toggle(); _phaseOn = !_phaseOn;\n",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "a refused cadence tick leaves the phase where it was"),

    # Re-pointed 2026-09-30 by lane fix/followups: Start() adopts the key's state (variant C, N-blinkingled-01),
    # so the variant-B reconciliation line matched 0 times. Same regression, same expected assertion.
    ("Start() zeroes the phase against a key it lit again (the 1.0.5 shape)",
     SCROLLLOCK_BLINKER,
     b"            try { _phaseOn = ScrollLockReader(); } catch { }",
     b"            _phaseOn = false;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "Start() keeps the belief that it lit the key"),

    ("Start() arms the dark gap whatever phase the key is in",
     SCROLLLOCK_BLINKER,
     b"            _timer.Interval = Math.Max(1, _phaseOn ? _onMs : _offMs);",
     b"            _timer.Interval = Math.Max(1, _offMs);",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "arms the LIT phase's interval"),

    # Inverted 2026-09-30 by lane fix/followups. This case used to put ADOPTION in as the mutation and expect
    # "does not adopt a lit key it never lit"; the coordinator chose that adoption (variant C, N-blinkingled-01),
    # so the regression is now the 1.0.6 first cut coming back: a lit key the blinker never lit is left
    # un-adopted and the cadence starts inverted.
    ("Start() stops adopting a lit key it never lit (the 1.0.6 first cut)",
     SCROLLLOCK_BLINKER,
     b"            try { _phaseOn = ScrollLockReader(); } catch { }",
     b"            try { if (_phaseOn && !ScrollLockReader()) _phaseOn = false; } catch { }",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "Start() adopts a lit key it never lit"),

    ("Stop() zeroes the belief after a refused corrective toggle",
     SCROLLLOCK_BLINKER,
     b"                _phaseOn = stillOurs;",
     b"                _phaseOn = false;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "a refused corrective toggle keeps the belief"),

    # The interop on the suite's one real call (the class of defect 1.0.3 shipped with): a P/Invoke that
    # throws used to hide behind the outcome-agnostic delivery line.
    ("the SendInput P/Invoke resolves against a DLL that does not exist",
     SCROLLLOCK_BLINKER,
     b'        [DllImport("user32.dll", SetLastError = true)]\n'
     b'        private static extern uint SendInput(',
     b'        [DllImport("user32-that-does-not-exist.dll", SetLastError = true)]\n'
     b'        private static extern uint SendInput(',
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "completes without throwing"),

    # F113: the suite's real keypresses are paired, asserted rather than trusted. The mutation deletes the
    # pairing press, which is the previous suite's shape: an odd count. The mutated run still leaves the
    # developer's key where it was, measured on 2026-09-29 (OFF before, OFF after): the unpaired press is
    # the module's own blinker's, so Shutdown -> Stop() finds the belief true and the key lit and clears
    # it, which is the 1.0.4 corrective toggle doing its job. On a box whose key read lags, press Scroll
    # Lock once if the LED is on afterwards.
    ("the self-test's pairing keypress is deleted",
     BLINKINGLED_MODULE,
     b"                    module._blinker.BlinkOnce();\n"
     b"                    long realMade = ScrollLockBlinker.RealKeypressCount - realBefore;",
     b"                    long realMade = ScrollLockBlinker.RealKeypressCount - realBefore;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "real keypresses are paired"),

    # F110: Save() answers false for a failed write and never throws, so the lines that report the write
    # test the bool. Each mutation puts back a shipped shape: a "saved" line whatever Save() answered, and
    # a tray handler that drops the bool.
    ("the Caps Lock stop reports 'saved' whatever Save() answered",
     BLINKINGLED_MODULE,
     b"                saved = s.Save();",
     b"                s.Save(); saved = true;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "says the off was NOT persisted"),

    ("a failed tray speed pick is silent again",
     BLINKINGLED_MODULE,
     b'                if (!s.Save()) Log("tray pick not persisted (rate " + rate + ", on), so the live state is unchanged");',
     b"                s.Save();",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "a tray speed pick whose write fails is logged"),

    ("a failed tray Off is silent again",
     BLINKINGLED_MODULE,
     b'                if (!s.Save()) Log("tray pick not persisted (" + (on ? "on" : "off") + "), so the live state is unchanged");',
     b"                s.Save();",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "a tray Off whose write fails is logged"),


    # ---- lane fix/aibrain ----

    # BUG-010 (F066) is a DECISION pinned by an assertion: the unprompted drop asks WITH vision allowed. The
    # mutation is the fix the audit proposed and the owner declined, so a future change to that argument fails
    # here by name instead of quietly re-litigating the decision. Through --module-selftest=aibrain, which
    # drives a real AiBrainModule through ModuleKit's RecordingHost (engine/AiEngineProbe.Module.cs).
    ("aibrain: the unprompted drop stops allowing vision (the fix the owner declined)",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b"            return Ask(pet, true);",
     b"            return Ask(pet, false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the unprompted drop asks WITH vision allowed"),

    # F067: the stand-down guard inside Ask, which the hotkey and the tray row reach with no responder in
    # front of them. The mutation leaves the call in place and makes it unreachable, the shape a source regex
    # for the call's presence cannot see.
    ("aibrain: the explicit ask ignores the fullscreen stand-down again",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b"            if (FullscreenBlocked())\n"
     b"            {\n"
     b'                try { host.Log(Info.Id, "ask declined: fullscreen stand-down"); } catch { }',
     b"            if (FullscreenBlocked() && host == null)\n"
     b"            {\n"
     b'                try { host.Log(Info.Id, "ask declined: fullscreen stand-down"); } catch { }',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the tray ask is DECLINED while a fullscreen app runs"),

    # F226: LaunchProcess is a disclosure that gates nothing at runtime, so only an assertion notices it gone.
    ("aibrain: the LaunchProcess disclosure is dropped",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b"                          ModulePermissions.Hotkey | ModulePermissions.Storage |\n"
     b"                          ModulePermissions.LaunchProcess,",
     b"                          ModulePermissions.Hotkey | ModulePermissions.Storage,",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "declares LaunchProcess"),

    # N-gates-02: the temp fallback root put back exactly as it shipped. Load still refuses to write (the
    # HasRoot gate in LoadWithin is a second, independent guard), but acquiring the cross-session lock for the
    # refused Save creates %TEMP%\DesktopAICompanion.AiBrain and its .lock, which is the leak in miniature and
    # is what the directory-state assertion sees. The write-blocked assertion stays green under this mutation
    # by design, so it is not the one named here.
    ("aibrain: the settings root falls back to %TEMP% again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiPaths.cs"),
     b'                    throw new InvalidOperationException("The AI settings root has not been set by the host.");',
     b'                    r = Path.Combine(Path.GetTempPath(), "DesktopAICompanion.AiBrain");',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "nothing was written to the old %TEMP% fallback directory"),

    # F098, both halves. The BOM branch made unreachable by a runtime condition (a literal false would be
    # CS0162 under warnings-as-errors), and the preservation dropped so the recovery overwrites the rejected
    # primary as it used to.
    ("aibrain: a UTF-8 BOM is corruption again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)",
     b"            if (bytes.Length < 0 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a UTF-8 BOM on ai-settings.json is not corruption"),

    ("aibrain: the rejected primary is destroyed by the recovery again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"            string preserved = result == ReadResult.Unreadable ? PreserveCorruptPrimary() : null;",
     b"            string preserved = null;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a rejected primary is kept beside the store"),

    # F096: the timeout path goes quiet again (defaults, writes blocked, no warning). `ex` stays referenced,
    # because a plain `= null` leaves the catch variable unused and CS0168 breaks the build instead of testing
    # the assertion.
    ("aibrain: a load that times out says nothing again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"                    blocked.LoadWarning = DescribeLoadFailure(ex);",
     b"                    blocked.LoadWarning = ex != null ? null : DescribeLoadFailure(ex);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a load that times out on the lock says so"),

    # F099: the empty-endpoint clamp made unreachable.
    ("aibrain: an empty endpoint survives normalization again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"            if (Endpoint.Length == 0)\n            {\n                Endpoint = \"http://localhost:11434\";",
     b"            if (Endpoint.Length < 0)\n            {\n                Endpoint = \"http://localhost:11434\";",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an empty local endpoint normalizes back to the default"),

    # F086: one sub-check forgets to give the root back. Run's closing assertion is what notices.
    ("aibrain: a probe leaves its borrowed settings root behind",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiEngineProbe.Security.cs"),
     b"                AiPaths.SwapRoot(borrowedRoot);\n"
     b"                try\n"
     b"                {\n"
     b"                    if (Directory.Exists(directory))\n"
     b"                        Directory.Delete(directory, true);\n"
     b"                }\n"
     b"                catch\n"
     b"                {\n"
     b'                    ok &= Check(sb, "LocalBackendKind self-test cleanup", false);',
     b"                try\n"
     b"                {\n"
     b"                    if (Directory.Exists(directory))\n"
     b"                        Directory.Delete(directory, true);\n"
     b"                }\n"
     b"                catch\n"
     b"                {\n"
     b'                    ok &= Check(sb, "LocalBackendKind self-test cleanup", false);',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "every probe restored the settings root it borrowed"),

    # F101, both halves: CanUse lets a blank cloud model through again, and the brain's constructor fills a
    # blank cloud model with the local default again.
    ("aibrain: CanUse accepts a cloud provider with no cloud text model again",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b"                if (string.IsNullOrWhiteSpace(s.CloudTextModel))\n"
     b"                {\n"
     b'                    error = "Pick a cloud text model first (Refresh cloud models, then choose one).";',
     b"                if (s.CloudTextModel == null)\n"
     b"                {\n"
     b'                    error = "Pick a cloud text model first (Refresh cloud models, then choose one).";',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a cloud provider with no cloud text model does not pass CanUse"),

    ("aibrain: a blank cloud model is filled with the local default again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b"            _textModel = AiModelPolicy.TryNormalize(\n"
     b"                _settings.TextModel, out normalizedModel)\n"
     b"                ? normalizedModel\n"
     b'                : (cloudSlot ? "" : "gemma3:4b");',
     b"            _textModel = AiModelPolicy.TryNormalize(\n"
     b"                _settings.TextModel, out normalizedModel)\n"
     b"                ? normalizedModel\n"
     b'                : "gemma3:4b";',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a cloud snapshot with no model never invents the local default"),

    # F103: the composite stops being a lister (the method stays; only the interface goes).
    ("aibrain: the cloud+local composite cannot be enumerated again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "FallbackBackend.cs"),
     b"    internal sealed class FallbackBackend : ICompanionBrainBackend, IModelLister",
     b"    internal sealed class FallbackBackend : ICompanionBrainBackend",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the cloud+local composite can be enumerated"),

    # F102: the marker miss becomes a hard gate again on a backend that reports nothing.
    ("aibrain: a marker miss refuses a configured vision model again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"                if (needVision && listing.Vision == false)\n"
     b"                {\n"
     b"                    listedButCannotSee = true;",
     b"                if (needVision && !IsVisionCapable(listing.Id, listing.Vision))\n"
     b"                {\n"
     b"                    listedButCannotSee = true;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a listed vision model with no marker and no report is used as configured"),

    # F107, both halves: the log category and the probe.
    ("aibrain: an answered status is filed as backend-unreachable again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b'            if (http != null) return "http-" + http.StatusCode.ToString(CultureInfo.InvariantCulture);',
     b'            if (http != null && http.StatusCode < 0) return "http-" + http.StatusCode.ToString(CultureInfo.InvariantCulture);',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an answered HTTP status is its own category in the log"),

    ("aibrain: an answered 401 reads as not reachable again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OpenAiCompatBackend.cs"),
     b"                    return await AiEndpointPolicy.SendAndCheckAnsweredAsync(",
     b"                    return await AiEndpointPolicy.SendAndCheckSuccessAsync(",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an answered 401 is reachable"),

    # F080: the failing body is never read again.
    ("aibrain: a provider's error body is dropped again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiEndpointPolicy.cs"),
     b"                body = await ReadResponseStringAsync(\n"
     b"                    response.Content,\n"
     b"                    cancellationToken,\n"
     b"                    MaximumProviderErrorBytes).ConfigureAwait(false);",
     b'                body = "";',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the provider's error message reaches the exception"),

    # F078: the listing reads under the reply cap again.
    ("aibrain: the cloud model listing reads under the 1 MiB reply cap again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OpenAiCompatBackend.cs"),
     b"                        ct,\n"
     b"                        AiEndpointPolicy.MaximumListingResponseBytes).ConfigureAwait(false);\n"
     b"                    JsonNode obj = JsonNode.Parse(json);\n"
     b'                    JsonArray data = obj?["data"] as JsonArray;',
     b"                        ct).ConfigureAwait(false);\n"
     b"                    JsonNode obj = JsonNode.Parse(json);\n"
     b'                    JsonArray data = obj?["data"] as JsonArray;',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a /models catalogue above the 1 MiB reply cap still lists"),

    # F073: the failure line reads the SETTING again instead of the model that was sent.
    ("aibrain: the failure line names the configured model instead of the one sent again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b'                   " model=" + (choice != null && !string.IsNullOrEmpty(choice.Model) ? choice.Model : "(unresolved)") +',
     b'                   " model=" + (_useVision ? _visionModel : _textModel) +',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a failure after resolution names the model actually SENT"),

    # F072: a usable substitution stops being announced before the capture.
    ("aibrain: a substitution is announced only after the generation again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b"                advisory = AdvisoryOnce(choice.Advisory);\n"
     b"                if (advisory != null) return false;",
     b"                advisory = AdvisoryOnce(choice.Advisory);\n"
     b"                if (advisory != null && !choice.Usable) return false;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a substitution is announced before any capture"),

    # F106: the warm-up pins its own ten minutes again.
    ("aibrain: the warm-up hard-codes keep_alive 10m again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OllamaClient.cs"),
     b'                    ["keep_alive"] = KeepAliveSeconds.HasValue ? (JsonNode)KeepAliveSeconds.Value : (JsonNode)"10m"',
     b'                    ["keep_alive"] = "10m"',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the warm-up carries the residency's keep_alive"),

    # F105, four legs: each probe borrows the chat deadline again, and the composite goes sequential again.
    ("aibrain: the cloud reachability probe borrows the chat deadline again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OpenAiCompatBackend.cs"),
     b"                        _probeDeadline,",
     b"                        _deadline,",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a cloud reachability probe is bounded by the probe deadline"),

    ("aibrain: the local reachability probe borrows the chat deadline again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OllamaClient.cs"),
     b"            return await IsAvailableAsync(_availabilityDeadline, ct).ConfigureAwait(false);",
     b"            return await IsAvailableAsync(_deadline, ct).ConfigureAwait(false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the local reachability probe is bounded the same way"),

    ("aibrain: the composite probes cloud then local again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "FallbackBackend.cs"),
     b"            return await FirstUpAsync(primary, local).ConfigureAwait(false);",
     b"            return await primary.ConfigureAwait(false) || await local.ConfigureAwait(false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "without waiting out a hung cloud probe"),

    ("aibrain: the composite readies the local leg only after the cloud leg again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "FallbackBackend.cs"),
     b"            Task<bool> primary = _primary.EnsureServerAsync(ct);\n"
     b"            Task<bool> local = _local.EnsureServerAsync(ct);",
     b"            Task<bool> primary = _primary.EnsureServerAsync(ct);\n"
     b"            await primary.ConfigureAwait(false);\n"
     b"            Task<bool> local = _local.EnsureServerAsync(ct);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the local leg is readied while the cloud probe is still pending"),

    # F095: retirement evicts regardless of what the caller said again.
    ("aibrain: a same-backend retirement evicts the model again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSessionManager.cs"),
     b"            if (!releaseModel)\n"
     b"            {\n"
     b"                try { brain.Dispose(); } catch { }\n"
     b"                return;\n"
     b"            }",
     b"            if (!releaseModel && brain == null)\n"
     b"            {\n"
     b"                try { brain.Dispose(); } catch { }\n"
     b"                return;\n"
     b"            }",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a same-backend Apply retires the brain without evicting its model"),

    # F091: the not-entered branch disposes before it unloads again.
    ("aibrain: the timed-out dispose disposes the backend before releasing the model again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSessionManager.cs"),
     b"                                Task unload = active.UnloadAsync(unloadBudget.Token);",
     b"                                active.Dispose();\n"
     b"                                Task unload = active.UnloadAsync(unloadBudget.Token);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the model was released BEFORE the backend was disposed"),

    # F081: the post-consume cancellation check comes back.
    ("aibrain: a finished reply is discarded when the deadline fired during the read again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiEndpointPolicy.cs"),
     b"                            boundedToken).ConfigureAwait(false);\n"
     b"                        // No cancellation check AFTER the consumer.",
     b"                            boundedToken).ConfigureAwait(false);\n"
     b"                        boundedToken.ThrowIfCancellationRequested();\n"
     b"                        // No cancellation check AFTER the consumer.",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a complete reply is returned even when the deadline fired"),

    # F070: the audition brain sends the residency's keep_alive:0 again.
    ("aibrain: the audition brain evicts after every sample again",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b"                ? (int?)AuditionKeepAliveWindowSeconds\n"
     b"                : s.KeepAliveForRequests;",
     b"                ? s.KeepAliveForRequests\n"
     b"                : s.KeepAliveForRequests;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the audition brain holds the model between samples"),

    # F063: PrepareAsync warms regardless of the switch again.
    ("aibrain: the audition's preparation warms the model again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b"                if (up && warmUp && _settings.WarmUpDesired)",
     b"                if (up && (warmUp || !warmUp) && _settings.WarmUpDesired)",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an audition's preparation warms nothing"),

    # F104: the fallover's local model comes from the id again.
    ("aibrain: a fallover picks the local model by id again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "FallbackBackend.cs"),
     b"                string localModel = HasImage(messages) ? _localVisionModel : _localTextModel;",
     b"                string localModel = LocalModelFor(model);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a text fallover with one cloud model for both slots lands on the local TEXT model"),


    # F068/F100: the factory's settings copy shares the live credential dictionary again.
    ("aibrain: the brain's settings copy shares the live credential dictionary again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"            clone.ApiKeysEnc = ApiKeysEnc == null ? null : new Dictionary<string, string>(ApiKeysEnc, StringComparer.Ordinal);",
     b"            clone.ApiKeysEnc = ApiKeysEnc;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the brain's settings copy owns its credential dictionary and its collections"),

    # F100: a throwing brain factory is silent again (the task faults, nothing is logged).
    ("aibrain: a throwing brain factory is silent again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSessionManager.cs"),
     b"                        AiBrain.LogBuildFailure(ex);\n                        return false;",
     b"                        if (ex == null) AiBrain.LogBuildFailure(ex);\n                        return false;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a throwing brain factory is reported and does not fault the reconfigure"),

    # F071: the inventory is no longer taken when the backend is first seen up (nor when it comes back).
    ("aibrain: the inventory is no longer taken on the transition to reachable",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b"            if (up && !wasUp) await RefreshInventoryAsync(ct).ConfigureAwait(false);",
     b"            if (up && !wasUp && wasUp) await RefreshInventoryAsync(ct).ConfigureAwait(false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an unprepared brain learns the inventory on its first reachability check"),

    # F071: the pane's refresh no longer reaches the live brain.
    ("aibrain: the pane's model refresh no longer reaches the live brain",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSessionManager.cs"),
     b"            try { return brain.RefreshInventoryAsync(ct); }",
     b"            try { return brain != null ? Task.CompletedTask : brain.RefreshInventoryAsync(ct); }",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the pane's model refresh reaches the live brain's inventory through the session"),

    # F077: the PATH walk throws once per entry again.
    ("aibrain: the PATH walk throws once per entry again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiExecutablePolicy.cs"),
     b"            if (!File.Exists(canonical)) return false;",
     b"            if (canonical == null) return false;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "resolving an absent executable from PATH throws nothing per entry"),

    # F077: tesseract is resolved on every ask again.
    ("aibrain: the OCR engine is resolved on every ask again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b"            if (_tesseractResolved) return _resolvedTesseract;",
     b"            if (_tesseractResolved && _resolvedTesseract == null && _resolvedTesseract != null) return _resolvedTesseract;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the OCR engine is resolved once per brain, not once per ask"),

    # F109: Windows OCR goes back through the PNG codec (the raw copy throws, the fallback runs).
    ("aibrain: Windows OCR goes through the PNG round trip again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "WindowsOcr.cs"),
     b"                int rowBytes = width * 4;",
     b"                int rowBytes = width * 4; if (rowBytes > 0) throw new NotSupportedException();",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "Windows OCR is fed the capture's pixels, not a PNG round trip"),

    # F062: the audition guard admits a second press again.
    ("aibrain: the audition guard admits a second press again",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b"            return Interlocked.CompareExchange(ref _auditionRunning, 1, 0) == 0;",
     b"            return Interlocked.CompareExchange(ref _auditionRunning, 1, 0) >= 0;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a second audition press while one is running is refused with an explanation"),


    # ---- round 2 (2026-09-30, early regression review) ----

    # R-011: a vision toggle no longer changes the fingerprint (the vision model stays resident under "keep").
    ("aibrain: a vision toggle no longer changes the fingerprint",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b'                s.UseVision ? "vision" : "text",',
     b'                s.UseVision ? "vision" : "vision",',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a vision toggle changes the fingerprint"),

    # R-014: the cloud listing borrows the chat deadline again.
    ("aibrain: the cloud listing borrows the chat deadline again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OpenAiCompatBackend.cs"),
     b"                        _listingDeadline,\n                        ct,\n                        AiEndpointPolicy.MaximumListingResponseBytes",
     b"                        _deadline,\n                        ct,\n                        AiEndpointPolicy.MaximumListingResponseBytes",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a cloud listing that hangs is bounded by the listing deadline"),

    # R-014: the local listing borrows the chat deadline again.
    ("aibrain: the local listing borrows the chat deadline again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OllamaClient.cs"),
     b"                        _listingDeadline,\n                        ct,\n                        AiEndpointPolicy.MaximumListingResponseBytes",
     b"                        _deadline,\n                        ct,\n                        AiEndpointPolicy.MaximumListingResponseBytes",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the local listing is bounded the same way"),

    # R-014: an empty re-list replaces a good inventory again.
    ("aibrain: an empty re-list replaces a good inventory again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b"                if (listed.Count == 0 && _available != null)",
     b"                if (listed.Count == 0 && _available != null && _available == null)",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an empty re-list (a bound that tripped) keeps the previous inventory"),

    # R-015: Test OCR no longer reaches the live brain's cache.
    ("aibrain: Test OCR no longer reaches the live brain's cache",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSessionManager.cs"),
     b"            try { brain.ForgetTesseractResolution(configuredTesseractPath); } catch { }",
     b"            try { if (brain == null) brain.ForgetTesseractResolution(configuredTesseractPath); } catch { }",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the Test OCR button makes the LIVE brain resolve its engine afresh"),

    # R-020: the substitution loop applies the union again and picks a reported-blind model with a marker.
    ("aibrain: the substitution loop picks a reported-blind model again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"                if (needVision && (listing.Vision == false || !IsVisionCapable(listing.Id, listing.Vision))) continue;",
     b"                if (needVision && !IsVisionCapable(listing.Id, listing.Vision)) continue;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a reported-blind model is never the substitute"),

    # R-022: CreateBrain leaves substitution on for a cloud primary again.
    ("aibrain: CreateBrain leaves substitution on for a cloud primary again",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b"            brain.SubstituteMissingModel = IsLocalSlot(s);",
     b"            brain.SubstituteMissingModel = IsLocalSlot(s) || !IsLocalSlot(s);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "CreateBrain turns substitution off for a cloud primary"),

    # R-022: ChooseModel ignores the substitution policy again.
    ("aibrain: ChooseModel ignores the substitution policy again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"            if (!allowSubstitution)",
     b"            if (!allowSubstitution && allowSubstitution)",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a cloud primary never substitutes"),


    # ---- lane fix/fortunes ----

    # Every case runs the module's own SelfTest through the convention flag, which is where the probe's
    # assertions live since 1.0.11. Names carry the "fortunes:" prefix so `--only=fortunes:` runs the lane.

    # F130: an undeclared tagged pack with one strict-parse fault fell back to prose and recited its own
    # metadata. The guard is the looks-tagged test before the fallback; this disables it.
    ("fortunes: a faulty undeclared tagged pack is demoted to prose again",
     FORTUNE_PROVIDER,
     b"                if (LooksTagged(content))",
     b"                if (LooksTagged(content) && content.Length < 0)",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "one blank line is refused, not demoted to prose"),

    # F132: the text column was validated RAW and decoded afterwards. This puts the raw column back
    # into the validator, so an entity-only text decodes to "" and enters the pool again.
    ("fortunes: the tagged text column is validated before it is decoded again",
     FORTUNE_PROVIDER,
     b"            string text = DecodeScrapedText(fields[5]);\n"
     b"            if (!ValidateCommonFields(fields[0], fields[3], fields[4], text, out error))",
     b"            string text = DecodeScrapedText(fields[5]);\n"
     b"            if (!ValidateCommonFields(fields[0], fields[3], fields[4], fields[5], out error))",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "only escaped zero-width spaces is refused after decoding"),

    # F129 (name): drop the unpaired-surrogate refusal. The per-file catch still keeps the later pack
    # alive, so the check that fires is the one saying WHY the file was refused: it now reads as an
    # exception, not as a bad name.
    ("fortunes: a lone surrogate in a pack's file name is accepted as a source id again",
     FORTUNE_PROVIDER,
     b"                   !ContainsControlCharacter(source) &&\n"
     b"                   !ContainsUnpairedSurrogate(source);",
     b"                   !ContainsControlCharacter(source);",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "counted as a refused name, not as an error"),

    # F129 (slot): a refused file spends a pack slot again, as it did before the charge moved after
    # the parse.
    ("fortunes: a refused pack file consumes a pack slot again",
     FORTUNE_PROVIDER,
     b"                    if (staged == null)\n"
     b"                    {\n"
     b"                        CountSkip(skips, skip);\n"
     b"                        continue;\n"
     b"                    }",
     b"                    if (staged == null)\n"
     b"                    {\n"
     b"                        CountSkip(skips, skip);\n"
     b"                        files++;\n"
     b"                        continue;\n"
     b"                    }",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "does not consume the only pack slot"),

    # F130 (surfacing): the pane's note about refused pack files goes quiet.
    ("fortunes: the pane stops mentioning refused pack files",
     FORTUNES_MODULE,
     b"            if (skipped <= 0) return \"\";\n"
     b"            return \" \xe2\x9a\xa0 \"",
     b"            if (skipped <= 0 || skipped > 0) return \"\";\n"
     b"            return \" \xe2\x9a\xa0 \"",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "refused packs are counted on the pane"),

    # F137: the two early exits in Warm set no stand-down again, one case each.
    ("fortunes: an oversized pool leaves the stand-down flag unset again",
     SMART_FORTUNES,
     b"                    _standDown = SmartStandDownReason.PoolTooLarge;",
     b"                    _standDown = SmartStandDownReason.None;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "a pool above the vector-cache cap stands the index down"),

    ("fortunes: a missing model asset leaves the stand-down flag unset again",
     SMART_FORTUNES,
     b"                    _standDown = SmartStandDownReason.ModelAbsent;",
     b"                    _standDown = SmartStandDownReason.None;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "a missing model asset stands the index down"),

    # F118: the session exception is swallowed without a record again. `ex` stays referenced: a
    # catch variable left unused is CS0168, which warnings-as-errors turns into a BROKEN verdict.
    ("fortunes: a failed model load records no reason again",
     EMBEDDER,
     b"                    _loadFailure = \"model: \" + ex.GetType().Name;",
     b"                    _loadFailure = ex.Message.Length < 0 ? \"model\" : null;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "a broken model file is named as the failing asset"),

    # F145: the warm's completion and cancellation lines, each silenced.
    ("fortunes: the warm stops reporting its completion",
     SMART_FORTUNES,
     b"            Say(\"smart index complete: \" +",
     b"            Say(\"smart index finished: \" +",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "the warm reports its completion through the sink"),

    ("fortunes: a cancelled warm is silent again",
     SMART_FORTUNES,
     b"                            Say(\"smart index warm cancelled (superseded or shutting down)\");",
     b"                            Say(\"\");",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "a cancelled warm says so through the sink"),

    # F135: the raw vectors are kept after the final save again.
    ("fortunes: the vector cache keeps its raw copy after the final save again",
     SMART_FORTUNES,
     b"            if (_cache.Save(token)) _cache.ReleaseMemory();",
     b"            _cache.Save(token);",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "releases its raw copy of the vectors"),

    # F138: every save re-parses the file it just wrote again.
    ("fortunes: a save re-reads the cache's own file again",
     SMART_FORTUNES,
     b"                    if (!UnchangedSinceOurWrite() &&\n"
     b"                        TryReadCacheFile(",
     b"                    if (\n"
     b"                        TryReadCacheFile(",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "does not re-parse it"),

    # F142: the bulk write changes the byte order; the encoding fixture is what notices.
    ("fortunes: the vector cache writes big-endian floats",
     SMART_FORTUNES,
     b"                            BinaryPrimitives.WriteSingleLittleEndian(",
     b"                            BinaryPrimitives.WriteSingleBigEndian(",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "the on-disk float encoding is unchanged"),

    # N-gates-01: a failed save records nothing again.
    ("fortunes: a failed vector-cache save is swallowed again",
     SMART_FORTUNES,
     b"                lock (_lock) _lastSaveFailure = ex.GetType().Name;",
     b"                lock (_lock) _lastSaveFailure = null;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "a failed vector-cache save is reported, not swallowed"),

    # F148: the status derives "enabled" from the picker object again, which is null while a build is in
    # flight. The read straight after Init is the deterministic observer: RebuildEngine has just queued
    # the build and the status is read before the worker can have published. The button press is the
    # same read behind an asynchronous rebuild, and there the worker can win the race, so a case naming
    # it read WRONG once the rebuild went off the UI thread (measured 2026-09-29).
    ("fortunes: the status derives 'enabled' from the picker object again",
     FORTUNES_MODULE,
     b"            return SmartStatusFor(_smartWanted, provider.Count, AnyPacksInstalled(), reason, detail,",
     b"            return SmartStatusFor(sm != null, provider.Count, AnyPacksInstalled(), reason, detail,",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "with smart picks ON and a build in flight, the button's status says indexing"),

    # F147: an unchanged pool rebuilds the picker again (`&& force` makes the keep decision always false).
    ("fortunes: an unchanged pool rebuilds the smart picker again",
     FORTUNES_MODULE,
     b"                bool current = wanted && !force && _smartBuilding && !_smartBuildFailed &&\n"
     b"                               string.Equals(signature, _indexedSignature, StringComparison.Ordinal);",
     b"                bool current = wanted && !force && _smartBuilding && !_smartBuildFailed &&\n"
     b"                               string.Equals(signature, _indexedSignature, StringComparison.Ordinal) && force;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "keeps the smart picker instead of rebuilding it"),

    # F147: a failed Save rebuilds the engine anyway, from the settings that did not change.
    ("fortunes: a failed Save rebuilds the engine anyway",
     FORTUNES_MODULE,
     b"                return false;\n"
     b"            }\n"
     b"            _stagedDisabled.Clear();",
     b"                RebuildEngine();\n"
     b"            }\n"
     b"            _stagedDisabled.Clear();",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "an Apply whose Save failed rebuilds nothing"),

    # F143: the superseded picker is disposed on the applying thread again, before the worker starts.
    ("fortunes: the superseded picker is disposed on the applying thread again",
     FORTUNES_MODULE,
     b"            System.Threading.Tasks.Task.Run(delegate { BuildSmartPicker(generation, old, pool); });",
     b"            if (old != null) { try { old.Dispose(); } catch { } }\n"
     b"            System.Threading.Tasks.Task.Run(delegate { BuildSmartPicker(generation, null, pool); });",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "disposed on a pool thread, not the thread that applied"),

    # F145: the publish-time line claims readiness again.
    ("fortunes: the publish-time line claims the picker is ready again",
     FORTUNES_MODULE,
     b"            return \"smart picker constructed, warming \" + Invariant(lines) + \" lines in the background\";",
     b"            return \"smart picker ready (\" + Invariant(lines) + \" lines indexed)\";",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "says constructed and warming, never ready or indexed"),

    # F147: the pool signature ignores the topic again.
    ("fortunes: the pool signature ignores the topic again",
     FORTUNES_MODULE,
     b"                    for (int i = 0; i < topic.Length; i++) { hash ^= topic[i]; hash *= 1099511628211UL; }",
     b"                    for (int i = 0; i < 0; i++) { hash ^= topic[i]; hash *= 1099511628211UL; }",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "the same texts under a different topic fingerprint differently"),

    # F127: the folder-changing rebuild parses inline on the calling thread again. Task.Yield keeps the
    # method honestly async; without it CS1998 (warnings-as-errors) would make the verdict BROKEN.
    ("fortunes: the folder-changing rebuild parses on the calling thread again",
     FORTUNES_MODULE,
     b"                provider = await Task.Run(delegate\n"
     b"                {\n"
     b"                    System.Threading.Volatile.Write(ref _lastParseThread, Environment.CurrentManagedThreadId);\n"
     b"                    return new FortuneProvider(settings);\n"
     b"                });",
     b"                System.Threading.Volatile.Write(ref _lastParseThread, Environment.CurrentManagedThreadId);\n"
     b"                provider = new FortuneProvider(settings);\n"
     b"                await Task.Yield();",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "Rescan's parse ran on a pool thread"),

    # F122: the import runs inline on the calling thread again.
    ("fortunes: the import runs on the calling thread again",
     FORTUNES_MODULE,
     b"                FortuneImportBatchResult result = await Task.Run(delegate\n"
     b"                {\n"
     b"                    return FortuneFileImporter.Import(chosen, directory, null, token);   // no overwrite approved (see summary)\n"
     b"                });",
     b"                FortuneImportBatchResult result = FortuneFileImporter.Import(chosen, directory, null, token);\n"
     b"                await Task.Yield();",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "the import ran on a pool thread"),

    # F123: the importer ignores the loader's per-file cache again.
    ("fortunes: the importer re-validates every existing pack again",
     FORTUNE_IMPORTER,
     b"                    if (FortuneProvider.TryGetCachedPack(",
     b"                    if (path.Length < 0 && FortuneProvider.TryGetCachedPack(",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "validated no existing pack again"),

    # F129 (the importer's half): a file the loader refuses is charged a slot and its bytes again.
    ("fortunes: the importer charges a file the loader refuses again",
     FORTUNE_IMPORTER,
     b"                            existing.Loadable = FortuneProvider.TryValidateCustomPackBytes(",
     b"                            existing.Loadable = true | FortuneProvider.TryValidateCustomPackBytes(",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "holds no slot and no bytes in the importer's admission"),

    # N-gates-02: the module's SelfTest stops redirecting the engine to its scratch root (SetRoot
    # ignores null, so the root in effect stays whatever the runner left).
    ("fortunes: the module self-test runs against the engine's live root again",
     FORTUNES_MODULE,
     b"                FortunePaths.SetRoot(scratch);",
     b"                FortunePaths.SetRoot(null);",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "landed under its scratch root"),

    # N-tools-02: the commit stops retrying a transient lock (one attempt, then surfaced).
    ("fortunes: a transient lock during the import's commit is surfaced at once again",
     FORTUNE_IMPORTER,
     b"        private const int ReplaceAttempts = 8;",
     b"        private const int ReplaceAttempts = 1;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "retried, not surfaced"),

    # F121: a host with no settings store gets the expensive default again.
    ("fortunes: a host with no settings store gets smart picks ON again",
     FORTUNES_MODULE,
     b"                s.SmartFortunes = false;\n"
     b"                return s;",
     b"                s.SmartFortunes = true;\n"
     b"                return s;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "gets smart picks OFF"),


    # ---- lane fix/petstudio ----

    # BUG-012 (F155): the analyzer's reachability stage adopts the validator's parse and stages no sprite.
    # The mutation puts back the shipped shape: the 1.1.17 loader call, which parsed the text again and
    # decoded and tiled the sheet. Analyze records the fact on the report, so the assertion reads what the
    # shipped path did. (A second case here reintroduced the host's stageImages:false overload, which decoded
    # no tile but parsed the text a second time; that overload is gone since RA-270 / RA-272, lane
    # burn/host-shell, 2026-10-01, and the case went with it.)
    ("petstudio: the reachability stage tiles the sheet again (BUG-012)",
     PETREPORT,
     b"                    xml.AnimationXML = root;\n"
     b"                    xml.LoadAnimations(animations);",
     b"                    string stageError;\n"
     b"                    if (!xml.TryReadXml(animationsXml, out stageError)) throw new InvalidOperationException(stageError);\n"
     b"                    xml.LoadAnimations(animations);",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "decodes no sprite frame"),

    # F165: the timeline's dropped-step note leaves the verdict sentence again.
    ("petstudio: the verdict loses the timeline's dropped-step note (F165)",
     PETSTUDIO_WINDOW,
     b"            if (droppedSteps > 0)\n",
     b"            if (droppedSteps < 0)\n",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "carries the timeline's dropped-step note"),

    # F429 (the tools lane's note): the converter's stricter acceptance bar is announced as the host's
    # rejection again, for a pet the validator accepted.
    ("petstudio: an accepted import is announced as rejected by the host again (F429)",
     PETSTUDIO_WINDOW,
     b"            string prefix = \"Imported '\" + name + \"'\" + (extra ?? \"\");\n",
     b"            string prefix = \"Imported '\" + name + \"'\" + (extra ?? \"\") + \", but the host would reject it\";\n",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "is not announced as one the host would reject"),

    # F150 / F151: the surface-pose growth. Each mutation puts back one shipped shape or breaks one of the
    # three bounds; the expected fragments alternate between a NAMED fixture label (so F151's point -- the
    # fixture check can fail -- is itself proved) and a hand-built case.
    ("petstudio: the growth stops at a flip turn again (F150)",
     ANIM_CAPABILITY,
     b"            if (IsTurn(target))\n"
     b"            {\n"
     b"                if (queued.Add(key)) pending.Enqueue(new KeyValuePair<int, Surface>(id, kind));\n"
     b"                return;\n"
     b"            }\n",
     b"            if (IsTurn(target)) return;\n",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "reached only through a flip turn, is a CLIMB"),
    ("petstudio: the axis test admits any travel (F150)",
     ANIM_CAPABILITY,
     b"            return kind == Surface.Wall ? vertical && !horizontal : horizontal && !vertical;",
     b"            return horizontal || vertical;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "the sheep's wall bounce is MOVE, not a CLIMB"),
    ("petstudio: the axis test admits travel across the surface (F150)",
     ANIM_CAPABILITY,
     b"            return kind == Surface.Wall ? vertical && !horizontal : horizontal && !vertical;",
     b"            return kind == Surface.Wall ? vertical : horizontal;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "a wall pose travels only up or down"),
    ("petstudio: a wall descent's border edge is followed onto the floor (F150)",
     ANIM_CAPABILITY,
     b"                        if (Descends(from)) continue;   // the edge below a descent is the floor\n",
     b"",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "the pose the descent lands in is not a surface pose"),
    ("petstudio: the surface kind no longer flips at a border (F150)",
     ANIM_CAPABILITY,
     b"                        if (Moves(from)) next = current.Value == Surface.Wall ? Surface.Ceiling : Surface.Wall;",
     b"                        if (Moves(from)) next = current.Value;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "the growth passes through the turn"),

    # F226 (the PetStudio third): LaunchProcess gates nothing at runtime, so only the assertion notices it gone.
    ("petstudio: the LaunchProcess disclosure is dropped (F226)",
     PETSTUDIO_MODULE,
     b"                          | ModulePermissions.Companions | ModulePermissions.Storage\n"
     b"                          | ModulePermissions.LaunchProcess,",
     b"                          | ModulePermissions.Companions | ModulePermissions.Storage,",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "Storage and LaunchProcess"),

    # F164: the walk gives up at the first folder it cannot list again (the bare catch that read as "no bundle").
    # The assertion's fixture keeps the bundle TWO levels down behind the denied folder; with it one level
    # down this case SURVIVED, because breadth-first the bundle was found in the root's own listing before
    # the denied sibling was ever listed and the catch never ran.
    ("petstudio: FindBundleRoot abandons the walk at a folder it cannot list (F164)",
     PETSTUDIO_WINDOW,
     b"                catch (Exception) { continue; }   // one folder we cannot list, not the whole walk (F164)",
     b"                catch (Exception) { return null; }",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "keeps walking past a subfolder it cannot list"),


    # ---- lane fix/reminder ----

    # CachingCalendarSource's refresh latch (F186): one bool cleared only when the fetch returned, so a fetch
    # that never returned froze the slot silently. Four mutations, one per part of the repair: the stall report,
    # the retry, the cap on parked attempts, and the generation rule that keeps a late result from an
    # abandoned attempt from overwriting the newer one. The retry and cap mutations also fail the later steps
    # of the same sequential scenario; the first FAIL line is the one named here.
    ("the refresh stall is never reported on the served snapshot",
     CACHING_CALENDAR_SOURCE,
     b"                    if (_started.Count > 0 && nowUtc - oldest > deadline)\n",
     b"                    if (_started.Count < 0 && nowUtc - oldest > deadline)\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a refresh that outlives its deadline is reported on the served snapshot"),

    ("an overdue refresh never triggers a retry",
     CACHING_CALENDAR_SOURCE,
     b"                    abandoned = _started.Count > 0 && nowUtc - newest > deadline;\n",
     b"                    abandoned = _started.Count < 0 && nowUtc - newest > deadline;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "one more attempt is started while the first stays parked"),

    ("a late result from an abandoned refresh overwrites the newer one",
     CACHING_CALENDAR_SOURCE,
     b"                if (generation < _landedGeneration) return;\n",
     b"",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a result from the abandoned attempt, landing late, does not overwrite the newer one"),

    ("the cap on parked refresh attempts is one higher than declared",
     CACHING_CALENDAR_SOURCE,
     b"                kick = stale && (!inFlight || abandoned) && _started.Count < MaximumOutstandingRefreshes;\n",
     b"                kick = stale && (!inFlight || abandoned) && _started.Count < MaximumOutstandingRefreshes + 1;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "parked attempts are capped at"),

    # The refresh key is computed on the caller's thread and handed to FetchCore (F192); the regression is
    # DoRefresh recomputing it on the pool thread, which is what every subclass used to do for itself.
    ("the background fetch recomputes the refresh key off-thread",
     CACHING_CALENDAR_SOURCE,
     b"            try { result = FetchCore(key, DateTimeOffset.Now) ?? new CalendarSnapshot { Events = Array.Empty<CalendarEvent>() }; }\n",
     b"            try { result = FetchCore(RefreshKey(), DateTimeOffset.Now) ?? new CalendarSnapshot { Events = Array.Empty<CalendarEvent>() }; }\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "never ran off the caller's thread"),

    # The file slot's interval sat exactly on the module's tick (F191). The mutation is the shipped value.
    ("the file slot's refresh interval equals the tick again",
     os.path.join(REPO, "modules", "Reminder", "LocalJsonSource.cs"),
     b"        public LocalJsonSource(Func<string> pathGetter) : base(TimeSpan.FromSeconds(10))\n",
     b"        public LocalJsonSource(Func<string> pathGetter) : base(TimeSpan.FromSeconds(20))\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "sits below the module tick"),

    # IcsUrlSource.Download's two bounds (F189), each put back the way it shipped: a body read with no token,
    # and a size cap that does not run while the body arrives. The self-test's loopback server stalls, so
    # each mutation is caught by the test's own bound ("hung past the 8 s bound") rather than hanging the run.
    ("the .ics body read carries no deadline",
     os.path.join(REPO, "modules", "Reminder", "IcsUrlSource.cs"),
     b"                            int read = body.ReadAsync(chunk, 0, chunk.Length, cts.Token).GetAwaiter().GetResult();\n",
     b"                            int read = body.ReadAsync(chunk, 0, chunk.Length, CancellationToken.None).GetAwaiter().GetResult();\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "stalls is cut off by the deadline"),

    ("the .ics size cap is not checked while the body arrives",
     os.path.join(REPO, "modules", "Reminder", "IcsUrlSource.cs"),
     b"                            if (buffer.Length + read > maximumBytes)\n",
     b"                            if (buffer.Length + read > long.MaxValue)\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "past the size cap is refused while it is still arriving"),

    # A user's click re-reads the feeds (F201): Invalidate on the base, forwarded by the aggregate. Both halves.
    ("Invalidate forgets nothing",
     CACHING_CALENDAR_SOURCE,
     b"            lock (_lock) _lastFetchUtc = DateTimeOffset.MinValue;\n",
     b"            lock (_lock) { }\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "Invalidate makes the next Fetch re-read"),

    ("the aggregate does not forward Invalidate to its slots",
     os.path.join(REPO, "modules", "Reminder", "AggregateCalendarSource.cs"),
     b"                try { slot.Source.Invalidate(); } catch { }\n",
     b"                try { } catch { }\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "Invalidate makes the next Fetch re-read"),

    # A reminder due while no companion is on screen is held, not spent (F199). The first mutation is the
    # shipped code (no gate); the second blinds the companion-manager source, which is what covers a pet that
    # was out before the module loaded and so never came through CompanionSpawned.
    ("a reminder is spent while no companion is on screen",
     os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs"),
     b"                bool suppress = quiet || hush || nobody;\n",
     b"                bool suppress = quiet || hush;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "with no companion on screen a due reminder is not spent"),

    ("a pet the module was never told about does not count as on screen",
     os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs"),
     b"                        if (c != null && c.Count > 0) return true;\n",
     b"                        if (c != null && c.Count < 0) return true;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a companion the module was never told about still counts as on screen"),

    # An unchanged feed error is logged once (F197). The mutation drops the comparison: every tick logs again.
    ("the feed error is logged on every tick again",
     os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs"),
     b"            if (string.Equals(current, _lastLoggedFeedError, StringComparison.Ordinal)) return;\n",
     b"",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "an unchanged feed error is logged once, not on every tick"),

    # An Apply keeps the slots whose type it did not change (F200). The mutation makes the type comparison
    # never match, which is the shipped behaviour: every Apply rebuilt every slot.
    ("every Apply rebuilds every slot again",
     os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs"),
     b"                if (!string.Equals(type, _slotTypes[i], StringComparison.Ordinal))\n",
     b"                if (!string.Equals(type, _slotTypes[i] + \"?\", StringComparison.Ordinal))\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "an Apply that did not change the slot's type keeps its source instance"),

    # The fired-id prune judges per slot (F204). First mutation: the 1.0.3 whole-snapshot gate put back in
    # front of the per-slot branch. Second: the aggregate reports every slot healthy, so an erroring slot's
    # ids are pruned as if it had vouched for them.
    ("the prune is gated on the whole snapshot's Error again",
     os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs"),
     b"            Dictionary<string, bool> slots = snap.SlotHealthy;\n",
     b"            if (!string.IsNullOrEmpty(snap.Error)) return false;\n"
     b"            Dictionary<string, bool> slots = snap.SlotHealthy;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a healthy slot's stale id is dropped even while another slot errors"),

    ("an erroring slot is reported healthy to the prune",
     os.path.join(REPO, "modules", "Reminder", "AggregateCalendarSource.cs"),
     b"                bool healthy = string.IsNullOrEmpty(snap.Error);\n",
     b"                bool healthy = true;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "the erroring slot's id is kept"),

    # The custom chime's read leaves the caller's thread (F188). The mutation runs PlayCustom inline, which is
    # the shipped shape; the self-test's gated loader then completes before Play returns.
    ("the custom chime is read inline on the caller's thread",
     os.path.join(REPO, "modules", "Reminder", "Chime.cs"),
     b"                try { Task.Run(() => PlayCustom(host, path, loadCustom)); }\n",
     b"                try { PlayCustom(host, path, loadCustom); }\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a custom chime's read does not run on the caller's thread"),

    # Teams' short work-tenant join link (F193). The mutation deletes the provider; SelfCheck names the case
    # in its detail line and the suite's FAIL line names the detector.
    ("the Teams short join link is no longer matched",
     os.path.join(REPO, "modules", "Reminder", "MeetingLinkDetector.cs"),
     b"            new Provider(\"Teams\", @\"https://teams\\.microsoft\\.com/meet/[^\\s\"\"'<>]+\"),\n",
     b"",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "MeetingLinkDetector"),

    # Round 2, R-043: retained events belong to the key that produced them. First mutation: the last-good
    # list is handed back whatever key asks, the shipped shape, so a failing new URL carries the old one's
    # events behind its error. Second: the cache is not cleared on a key change, so the tick after the edit
    # serves the old target's events as healthy.
    ("the last-good list is restored for a different key",
     CACHING_CALENDAR_SOURCE,
     b"                return string.Equals(_lastGoodKey, key ?? \"\", StringComparison.Ordinal) ? _lastGood : null;\n",
     b"                return _lastGood;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "serves nothing behind its error, not the previous target's events"),

    ("a key change keeps serving the previous target's cache",
     CACHING_CALENDAR_SOURCE,
     b"                if (changed) _cache = null;\n",
     b"",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "does not serve the previous target's events as healthy"),


    # ---- lane fix/followups ----
    # The N-* items other lanes filed and could not fix (their file was outside the lane). Every case names
    # the csproj that compiles the code under test and the artefact that run loads, so a stale DLL cannot
    # score a mutation as survived. Named "followups: ..." so one --only=followups: run covers the lane.

    # N-gates-01: the MoveFileEx fallback is handed the plain path again, which Win32 refuses past MAX_PATH
    # in a process that has not opted into long paths (this one has not). The CoreTests group forces the
    # fallback through the File.Replace seam at a 400-character path. ModuleKit's copy lands in the CoreTests
    # output through its ProjectReference, so that copy is the artefact. Until 2026-09-30 a second case here
    # mutated the host's twin in src\Portable\AppSettingsStore.cs; F358 (lane fix/deadcode) deleted that copy
    # and the host compiles this same file by source link, so one case covers the one implementation.
    ("followups: ModuleKit's MoveFileEx fallback drops the long-path form",
     MODULEKIT_ATOMIC,
     b"            if (!MoveFileEx(ExtendedLengthPath(temporaryPath), ExtendedLengthPath(destinationPath),",
     b"            if (!MoveFileEx(temporaryPath, destinationPath,",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit AtomicFile: the MoveFileEx fallback threw past MAX_PATH"),

    # N-remembrance-01: the recorded lists are handed out live again. The CoreTests group holds a view,
    # appends through the host, and requires the view's count not to move.
    ("followups: RecordingHost hands out its live SaidLines list again",
     MODULEKIT_RECORDING_HOST,
     b"        public List<string> SaidLines { get { lock (_recordSync) return new List<string>(_saidLines); } }",
     b"        public List<string> SaidLines { get { return _saidLines; } }",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "A list handed to a test moved under it"),

    # N-blinkingled-02: a failed Save() keeps the unsaved values again, the shape the fake shipped with. Two
    # checks see it: the fake's own contract in CoreTests, and BlinkingLed's strengthened F110 check, which
    # reads the settings back after a tray pick whose write failed and requires the old values.
    ("followups: the fake settings keep a failed Save()'s values again (CoreTests)",
     MODULEKIT_FAKES,
     b"            if (FailSaves) { RevertToSaved(); return false; }",
     b"            if (FailSaves) { return false; }",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "A failed Save() kept the unsaved value"),

    ("followups: the fake settings keep a failed Save()'s values again (BlinkingLed reads them back)",
     MODULEKIT_FAKES,
     b"            if (FailSaves) { RevertToSaved(); return false; }",
     b"            if (FailSaves) { return false; }",
     BLINKINGLED_CSPROJ, BLINKINGLED_MODULEKIT_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "leaves the saved values as they were"),

    # N-aibrain-02: a module handed no storage gets the %TEMP%\DesktopAICompanion.<id> folder again, the
    # shape the class shipped with and N-gates-02 removed from AiBrain.
    ("followups: ModulePaths falls back to a %TEMP% folder again",
     os.path.join(REPO, "src", "DesktopAICompanion.ModuleKit", "ModulePaths.cs"),
     b"            if (string.IsNullOrWhiteSpace(root)) return new ModulePaths(null, moduleId);",
     b'            if (string.IsNullOrWhiteSpace(root)) return new ModulePaths(Path.Combine(Path.GetTempPath(), "DesktopAICompanion." + moduleId), moduleId);',
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "A null storage produced a root"),

    # N-remembrance-02: the .part is kept when the CALLER's token cancels the download, the shipped shape (only
    # the download's own idle bound deleted it). The self-test cancels after the first chunk has landed.
    ("followups: remembrance: a download the caller cancels keeps its .part again",
     WHISPER_INSTALLER,
     b"                if (!completed) TryDelete(temporary);",
     b"                if (!completed && !cancellationToken.IsCancellationRequested) TryDelete(temporary);",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a download the CALLER cancels leaves no .part behind"),

    # N-blinkingled-01: Start() adopts the INVERSE of what the key reads, the purest form of the inverted
    # cadence. The adopted-key probe's Stop() then believes it holds nothing and leaves the lit key lit.
    ("followups: blinkingled: Start() adopts the inverse of what the key reads",
     SCROLLLOCK_BLINKER,
     b"            try { _phaseOn = ScrollLockReader(); } catch { }",
     b"            try { _phaseOn = !ScrollLockReader(); } catch { }",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "and Stop() then clears it, so stopping always leaves the light off"),

    # N-reminder-03: the two on-demand paths stop asking whether a companion is on screen, the shipped shape.
    # `_host == null &&` keeps each guard compiling and never true after Init, with no unreachable-code
    # warning (CS0162 would fail the module's warnings-as-errors build).
    ("followups: reminder: 'Test this reminder' reports sent with nobody on screen again",
     os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs"),
     b"                if (!AnyCompanionOnScreen()) return NoCompanionStatus;",
     b"                if (_host == null && !AnyCompanionOnScreen()) return NoCompanionStatus;",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "says so instead of"),

    ("followups: reminder: the Agenda click speaks into nothing again",
     os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs"),
     b"                    if (!AnyCompanionOnScreen()) { try { _host.Log(Id, AgendaNobodyLogLine); } catch { } return; }",
     b"                    if (_host == null && !AnyCompanionOnScreen()) { try { _host.Log(Id, AgendaNobodyLogLine); } catch { } return; }",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "the Agenda tray click with no companion on screen speaks nothing"),

    # N-tools-01: a PLAYED sequence's chain step is built from a direction-collapsed member's own poses
    # again, the shipped shape: a rightward leg over the unmirrored left-facing art, a moonwalk inside the
    # run. EmitterSelfTest's StrollBackPlayed fixture compares the step's velocity with its survivor's.
    ("followups: tools: a played chain step plays a collapsed member's own mirrored poses again",
     PET_EMITTER,
     b"                    ShimejiAction source = SurvivorOf(members[i]);",
     b"                    ShimejiAction source = members[i];",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "moonwalks over the left-facing art"),


    # ---- lane burn/host-shell ----

    # RA-296: the removal marker's write swallows again, so a marker that cannot be written is silent success.
    ("burn/host-shell: a failed removal-marker write is swallowed again",
     PENDING_REMOVALS,
     b"            if (kept.Count == 0) { if (File.Exists(markerPath)) File.Delete(markerPath); return; }\n"
     b"            File.WriteAllLines(markerPath, kept, new UTF8Encoding(false));\n",
     b"            try\n"
     b"            {\n"
     b"                if (kept.Count == 0) { if (File.Exists(markerPath)) File.Delete(markerPath); return; }\n"
     b"                File.WriteAllLines(markerPath, kept, new UTF8Encoding(false));\n"
     b"            }\n"
     b"            catch { }\n",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "a removal marker that cannot be written throws"),

    # RA-296: the update marker's write swallows again.
    ("burn/host-shell: a failed update-marker write is swallowed again",
     PENDING_UPDATES,
     b"            File.WriteAllLines(markerPath, ids, new UTF8Encoding(false));\n        }\n",
     b"            try { File.WriteAllLines(markerPath, ids, new UTF8Encoding(false)); } catch { }\n        }\n",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "an update marker that cannot be written throws"),

    # RA-297: an unreadable marker reads as an empty one again, so the sweep runs over a marked payload.
    ("burn/host-shell: an unreadable update marker reads as empty again",
     PENDING_UPDATES,
     b"            catch { return null; }\n",
     b"            catch { return new List<string>(); }\n",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "a marker that cannot be read sweeps nothing"),

    # RA-295: an uninstall leaves the staging folder's copies of the module behind again.
    ("burn/host-shell: an uninstall leaves the staging copies behind again",
     PENDING_REMOVALS,
     b"                    if (!string.IsNullOrEmpty(stagingRoot))\n",
     b"                    if (stagingRoot == null && !string.IsNullOrEmpty(stagingRoot))\n",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     ".replaced and .staged copies leave the staging folder"),

    # RA-291: the fallback excludes only Contracts.dll again, so ModuleKit.dll (which sorts first) is chosen.
    ("burn/host-shell: FindModuleDll's fallback excludes only Contracts.dll again",
     MODULE_HOST,
     b"                if (!Path.GetFileName(f).StartsWith(\"DesktopAICompanion.\", StringComparison.OrdinalIgnoreCase))\n",
     b"                if (!Path.GetFileName(f).Equals(\"DesktopAICompanion.Contracts.dll\", StringComparison.OrdinalIgnoreCase))\n",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "the host's own DLLs are skipped"),

    # RA-280: one try/catch around the whole multicast again, so the first throwing subscriber starves the
    # rest. The "does not take down" line above still passes (the throw is caught); only the after-thrower
    # delivery can tell this shape from RaiseEach.
    ("burn/host-shell: a throwing context subscriber starves the ones after it again",
     COMPANION_HOST,
     b"            RaiseEach<Action<string>>(_contextChanged, \"ContextChanged\", h => h(key));",
     b"            Action<string> whole = _contextChanged; if (whole != null) { try { whole(key); } catch { } }",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "registered after a throwing one is still delivered"),

    # RA-281: the rollback stops undoing the responders and hotkeys the Init ledgered. Tray items, panes and
    # subscriptions are still undone, so every F344 line stays green; only the responder probe sees it.
    ("burn/host-shell: the Init rollback leaves the ledgered responders live",
     COMPANION_HOST,
     b"                foreach (IDisposable r in ledger.Registrations) { try { r.Dispose(); } catch { } }\n",
     b"",
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "holds none of them afterwards"),

    # R-053: a drop registration hands back a no-op handle again, so Dispose removes nothing from the chain.
    ("burn/host-shell: RecordingHost's drop registration hands back a no-op handle again",
     MODULEKIT_RECORDING_HOST,
     b"            return HandleFor(_dropChain, entry, DropResponders, onDrop);\n",
     b"            return new NoopDisposable();\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "A disposed drop responder still claimed the drop"),

    # RA-216: the speech chain ignores priority again (registration order decides), the F233-era walk.
    ("burn/host-shell: RecordingHost's speech chain walks in registration order again",
     MODULEKIT_RECORDING_HOST,
     b"                int bySpeechPriority = y.Priority.CompareTo(x.Priority);\n",
     b"                int bySpeechPriority = 0;\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "did not win over the priority-0 fallback registered first"),

    # RA-215: PlayedSounds is handed out live again, the list a pool-thread chime appends to.
    ("burn/host-shell: RecordingHost hands out its live PlayedSounds list again",
     MODULEKIT_RECORDING_HOST,
     b"        public List<byte[]> PlayedSounds { get { lock (_recordSync) return new List<byte[]>(_playedSounds); } }\n",
     b"        public List<byte[]> PlayedSounds { get { return _playedSounds; } }\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "A PlayedSounds view handed to a test moved under it"),

    # RA-213: MemoryModuleSettings stores a null again, so Get answers the null instead of the fallback.
    ("burn/host-shell: MemoryModuleSettings stores a null value again",
     MODULEKIT_MEMORY_SETTINGS,
     b"            _values[key] = value ?? \"\";\n",
     b"            _values[key] = value;\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "MemoryModuleSettings stored a null instead of"),

    # RA-208: the PathMap leaves ModuleKit.csproj, so the embedded PDB names the build machine's paths again.
    # A csproj edit rebuilds ModuleKit (the project file is a CoreCompile input), so the copied DLL moves.
    ("burn/host-shell: ModuleKit's embedded symbols name the build machine's source paths again",
     MODULEKIT_CSPROJ,
     b"    <PathMap>$(MSBuildProjectDirectory)=/_/src/DesktopAICompanion.ModuleKit</PathMap>\n",
     b"",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "ModuleKit: the shipped symbols name an absolute build path"),

    # RA-210: an I/O failure (the file held open elsewhere) is reported as a fresh document while a corrupt
    # one is still refused, which is exactly the narrower rule the old doc described. The mutant keeps the
    # JsonException arm so the F230 corrupt-file assertion stays green and only the locked-file check fails.
    ("burn/host-shell: JsonSettingsStore reports a file it could not read as loaded again",
     MODULEKIT_JSONSTORE,
     b"            catch\n            {\n                value = new T();\n                return ReadResult.Unreadable;\n            }\n",
     b"            catch (JsonException)\n            {\n                value = new T();\n                return ReadResult.Unreadable;\n            }\n"
     b"            catch\n            {\n                value = new T();\n                return ReadResult.Loaded;\n            }\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "A locked file was not reported as unreadable"),

    # RA-212: the lease stops being re-entrant for its owner, so a Save from inside Update's mutate waits
    # the full 3 s for the file lease the same thread holds and fails.
    ("burn/host-shell: JsonSettingsStore's lease stops being re-entrant for the thread that holds it",
     MODULEKIT_JSONSTORE,
     b"            if (_leaseDepth > 0 && _leaseOwnerThread == me) { _leaseDepth++; return new Lease(this, null); }\n",
     b"",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "a Save from inside Update's mutate was refused"),

    # RA-312: the project-doc predicate stops checking the host, so any HTTPS page passes as a project doc.
    ("burn/host-shell: the project-doc allowlist accepts any host again",
     WEBLINKS,
     b"                string.Equals(uri.Host, \"github.com\", StringComparison.OrdinalIgnoreCase) &&\n",
     b"",
     HOST_CSPROJ, EXE,
     SECURITY, None, "project-doc links allow only the repository's own HTTPS pages"),

    # RA-300: the per-pet monitor pins are the one persisted list Normalize leaves alone again.
    ("burn/host-shell: the per-pet monitor pins are left unvalidated on load again",
     APPSETTINGS_STORE,
     b"            changed |= NormalizePetMonitors();\n",
     b"",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "Pet-monitor pins were not deduped, filtered and capped"),

    # RA-305/RA-306/RA-307: the cross-thread branch is unreachable again (depth is never negative), so a
    # setter from another thread during a batch saves the LIVE document, batch values and all.
    ("burn/host-shell: a setter from another thread during a batch saves the live document again",
     LOCALDATA,
     b"                if (_batchDepth > 0)\n                {\n                    // Another thread, while a batch is open on its owner",
     b"                if (_batchDepth < 0)\n                {\n                    // Another thread, while a batch is open on its owner",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "not the batch's uncommitted setters"),

    # RA-308: an inner scope disposed without Commit no longer poisons the outermost.
    ("burn/host-shell: an inner batch scope abandoned without Commit no longer fails the outermost",
     LOCALDATA,
     b"                    if (!commit) _batchAbandoned = true;\n",
     b"",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "an inner scope abandoned without Commit fails the outermost Commit"),

    # RA-221: WavAudio's header says 8 bits per sample, which the CoreTests group now reads back.
    ("burn/host-shell: WavAudio writes 8 bits per sample again",
     MODULEKIT_WAV,
     b"                w.Write((short)16);                   // bits per sample\n",
     b"                w.Write((short)8);                    // bits per sample\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "Not 16 bits per sample"),

    # RA-223: the two setting gates in NotificationSound.Play swap, so switch-off + volume 0 answers Muted.
    ("burn/host-shell: the notification volume gate is consulted before the switch again",
     AUDIO_OUTPUT,
     b"                if (!data.GetNotificationSoundsEnabled()) return NotificationOutcome.SwitchedOff;\n"
     b"                double volume = data.GetVolume();\n"
     b"                if (double.IsNaN(volume) || volume <= 0.0) return NotificationOutcome.Muted;\n",
     b"                double volume = data.GetVolume();\n"
     b"                if (double.IsNaN(volume) || volume <= 0.0) return NotificationOutcome.Muted;\n"
     b"                if (!data.GetNotificationSoundsEnabled()) return NotificationOutcome.SwitchedOff;\n",
     HOST_CSPROJ, EXE,
     "--audio-selftest", "dp-audio-selftest.txt", "the switch answers first"),

    # R-054: a handle left open in the audio scratch; the asserted release names it instead of a NOTE.
    ("burn/host-shell: --audio-selftest leaks a handle into its scratch",
     AUDIO_SELFTEST,
     b'                root = DesktopAICompanion.Plugins.SelfTestScratch.Create("audio");\n'
     b'                var data = new LocalData(new AppSettingsStore(Path.Combine(root, "settings.json"), null));\n',
     b'                root = DesktopAICompanion.Plugins.SelfTestScratch.Create("audio");\n'
     b'                new FileStream(Path.Combine(root, "leak.txt"), FileMode.Create, FileAccess.Write, FileShare.None);\n'
     b'                var data = new LocalData(new AppSettingsStore(Path.Combine(root, "settings.json"), null));\n',
     HOST_CSPROJ, EXE,
     "--audio-selftest", "dp-audio-selftest.txt", "the audio scratch was released"),

    # RA-268: the dispatch reorder the WPF self-test now guards against: the log starts before Configure.
    ("burn/host-shell: --wpf-options-selftest starts the diagnostic log before configuring it",
     WPF_SELFTEST,
     b'                ok &= Check(sb, "the diagnostic log has not started in this process, so Configure below touches no file",\n',
     b'                DiagnosticLog.Start();\n'
     b'                ok &= Check(sb, "the diagnostic log has not started in this process, so Configure below touches no file",\n',
     HOST_CSPROJ, EXE,
     "--wpf-options-selftest", "dp-wpf-options-selftest.txt", "the diagnostic log has not started in this process"),

    # RA-257: Forget(null) raises Forgotten again; the counted WITNESS sees the second raise.
    ("burn/host-shell: Forget(null) raises Forgotten again",
     COMPANION_CATALOG,
     b"            if (string.IsNullOrEmpty(id)) return;\n            lock (HeaderNameCache) HeaderNameCache.Remove(id);\n",
     b"            lock (HeaderNameCache) { if (!string.IsNullOrEmpty(id)) HeaderNameCache.Remove(id); }\n",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "WITNESS Forget of nothing raises nothing"),

    # RA-258: the header read bound drops below the largest legal icon's base64, so the name behind it is lost.
    ("burn/host-shell: the header read bound drops below the largest legal icon",
     COMPANION_CATALOG,
     b"        internal const int HeaderReadBoundChars = CompanionXmlValidator.MaximumIconBytes * 4 / 3 + 70 * 1024;\n",
     b"        internal const int HeaderReadBoundChars = CompanionXmlValidator.MaximumIconBytes * 4 / 3 - 70 * 1024;\n",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "a petname behind the largest legal icon is still found"),

    # RA-259: the override is never set, so the freshness probe would land in a real library again.
    ("burn/host-shell: --hardening-selftest stops isolating its data root",
     HARD,
     b"                Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, dataRootScratch);\n"
     b"                dataRootRedirected = true;\n"
     b'                Check("hardening: the data root is isolated',
     b"                dataRootRedirected = true;\n"
     b'                Check("hardening: the data root is isolated',
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "hardening: the data root is isolated for this run"),

    # R-055: the pin probe's tidy-up is dropped; the new assertion beside it sees the three files.
    ("burn/host-shell: the pin probe's tidy-up is dropped again",
     HARD,
     b"                    DeleteSettingsProbe(pinProbePath);\n                }\n                // Asserted, as the scale probe's is (R-055)",
     b"                }\n                // Asserted, as the scale probe's is (R-055)",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "pin: the probe's .json, .bak and .lock are all removed afterwards"),

    # N-deadcode-08 / RA-255: a reflected member the suite needs is renamed; the EXC line names it now.
    ("burn/host-shell: a reflected member the hardening suite needs is renamed",
     HARD,
     b'Require(xmlT.GetProperty("SpriteCount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), "Xml.SpriteCount (property)")',
     b'Require(xmlT.GetProperty("SpriteCounts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), "Xml.SpriteCounts (property)")',
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "every member the suite reflects on exists (reflected member not found: Xml.SpriteCounts"),

    # RA-262: the loader refuses every pet after the validator accepted it; the three reachability fixtures
    # now assert the loader's verdict by name instead of falling into LoadAnimations with nothing staged.
    ("burn/host-shell: the loader refuses every pet the validator accepted",
     XML_CS,
     b"            if (!CompanionXmlValidator.TryParse(xmlText, out parsed, out sheetBytes, out iconBytes, out error))\n"
     b"                return false;\n",
     b"            if (!CompanionXmlValidator.TryParse(xmlText, out parsed, out sheetBytes, out iconBytes, out error))\n"
     b"                return false;\n"
     b'            if (parsed != null) { error = "loader refused"; return false; }\n',
     HOST_CSPROJ, EXE,
     SECURITY, None, "the bundled pet loads for the reachability walk"),

    # RA-226: a same-Xml re-add replaces the entry (and its reference count) with a fresh one at 0 again.
    ("burn/host-shell: a same-Xml re-add of a live type replaces its entry again",
     TYPE_REGISTRY,
     b"                if (ReferenceEquals(displaced.Xml, xml))\n",
     b"                if (ReferenceEquals(displaced.Xml, animations))\n",
     HOST_CSPROJ, EXE,
     "--pettyperegistry-selftest", "dp-pettyperegistry-selftest.txt", "re-add: the same pair re-added keeps its entry and its reference count"),

    # RA-286: a downloaded pack is written but never joins the live pool; the diagnostic sees it.
    ("burn/host-shell: a downloaded fortune pack no longer joins the live pool",
     FORTUNES_MODULE,
     b"                await RebuildEngineAsync(false);   // the new packs join the pool (and the smart index) right away\n",
     b"",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--fortunes-selftest", "dp-fortunes-selftest.txt", "a downloaded pack's line is in the live pool without a restart"),

    # RA-287: Fortunes' Shutdown stops disposing its drop responder; the fakes' handles show it, twice over.
    ("burn/host-shell: Fortunes' Shutdown stops disposing its drop responder",
     FORTUNES_MODULE,
     b"            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }\n",
     b"",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--fortunes-selftest", "dp-fortunes-selftest.txt", "Shutdown disposed its drop and poke responder registrations"),
    ("burn/host-shell: the convention runner sees a Fortunes responder leaked past Shutdown",
     FORTUNES_MODULE,
     b"            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }\n",
     b"",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt", "Shutdown disposed every responder, speech and hotkey registration it made"),

    # RA-285 / RA-283: the two Fortunes self-tests stop isolating their data root.
    ("burn/host-shell: --fortunes-selftest stops isolating its data root",
     FORTUNES_SELFTEST,
     b"                Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, dataRootScratch);\n"
     b"                dataRootRedirected = true;\n",
     b"                dataRootRedirected = true;\n",
     HOST_CSPROJ, EXE,
     "--fortunes-selftest", "dp-fortunes-selftest.txt", "data root isolated for this run"),
    ("burn/host-shell: --fortunes-engine-selftest stops isolating its data root",
     FORTUNES_ENGINE_SELFTEST,
     b"                Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, dataRootScratch);\n"
     b"                dataRootRedirected = true;\n",
     b"                dataRootRedirected = true;\n",
     HOST_CSPROJ, EXE,
     "--fortunes-engine-selftest", "dp-fortunes-engine-selftest.txt", "data root isolated for this run"),

    # R-026 host half / RA-284: the fakes hand the Fortunes module an EMPTY store again, so its Init warms
    # the whole corpus and the module's smart status after Init reads indexing (or a stand-down) instead of
    # off. Asserted on that STATE, which Init sets synchronously, and not on the "smart picker constructed,
    # warming" log line: that line is written from the picker's pool thread after construction, so the
    # log-line form of these checks caught the mutation in one run (final2) and missed it in the next
    # (final3, SURVIVED) on 2026-10-01.
    ("burn/host-shell: --fortunes-engine-selftest hands the module an empty store again",
     FORTUNES_ENGINE_SELFTEST,
     b'                private readonly Dictionary<string, string> _d = new Dictionary<string, string> { { "smartFortunes", "false" } };\n',
     b'                private readonly Dictionary<string, string> _d = new Dictionary<string, string>();\n',
     HOST_CSPROJ, EXE,
     "--fortunes-engine-selftest", "dp-fortunes-engine-selftest.txt", "reports smart picks off after Init"),
    ("burn/host-shell: --fortunes-selftest hands the module an empty store again",
     FORTUNES_SELFTEST,
     b'                private readonly Dictionary<string, string> _d = new Dictionary<string, string> { { "smartFortunes", "false" } };\n',
     b'                private readonly Dictionary<string, string> _d = new Dictionary<string, string>();\n',
     HOST_CSPROJ, EXE,
     "--fortunes-selftest", "dp-fortunes-selftest.txt", "reports smart picks off after Init"),
    ("burn/host-shell: --module-host-selftest hands modules an empty store again",
     MODULE_HOST_SELFTEST,
     b'                private readonly Dictionary<string, string> _d = new Dictionary<string, string> { { "smartFortunes", "false" } };\n',
     b'                private readonly Dictionary<string, string> _d = new Dictionary<string, string>();\n',
     HOST_CSPROJ, EXE,
     "--module-host-selftest", "dp-module-host-selftest.txt", "reports smart picks off after Init"),

    # R-035 (host parity half): one edge of the embedded fixture moves; the parity line names it.
    ("burn/host-shell: the embedded self-test companion's graph drifts from the bundled one",
     PETSTUDIO_FIXTURE,
     b'<next probability="2" only="window">11</next>',
     b'<next probability="2" only="window">12</next>',
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--petstudio-selftest", "dp-petstudio-selftest.txt", "parity: the embedded self-test companion carries the bundled graph's edges"),

    # RA-298: the analyzer reports a 0x0 grid; the frame-bounds check used to pass that vacuously.
    ("burn/host-shell: the analyzer reports a 0x0 tile grid",
     PETSTUDIO_REPORT,
     b"                report.TilesX = root.Image.TilesX;\n",
     b"                report.TilesX = 0;\n",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--petstudio-selftest", "dp-petstudio-selftest.txt", "analysis: the analyzer reports the sprite's tile grid"),

    # N-petstudio-05: the studio stops declaring LaunchProcess; the host's reading of the manifest sees it.
    ("burn/host-shell: the studio stops declaring LaunchProcess",
     PETSTUDIO_MODULE,
     b"                          | ModulePermissions.Companions | ModulePermissions.Storage\n"
     b"                          | ModulePermissions.LaunchProcess,\n",
     b"                          | ModulePermissions.Companions | ModulePermissions.Storage,\n",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--petstudio-selftest", "dp-petstudio-selftest.txt", "declares LaunchProcess (F226"),

    # R-056: three of c153ce0's by-hand mutations, written down where the harness can run them.
    ("burn/host-shell (R-056, F293): the gaze boundary moves off the centre",
     FORMCOMPANION_CS,
     b"            return cursorX < characterCentreX;\n",
     b"            return cursorX <= characterCentreX;\n",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "gaze: a cursor exactly on the centre faces right"),
    ("burn/host-shell (R-056, F299): the pre-probe UNC refusal is reworded",
     VALIDATOR_CS,
     b'                    "The local pet must be an absolute path on a local drive.");\n',
     b'                    "The local pet must be a path on a local drive.");\n',
     HOST_CSPROJ, EXE,
     SECURITY, None, "UNC pet XML path rejected before probing"),
    ("burn/host-shell (R-056, F301): the catalog deadline stops firing",
     SECURE_DOWNLOAD_CS,
     b"                deadlineCancellation.CancelAfter(deadline);\n",
     b"                deadlineCancellation.CancelAfter(TimeSpan.FromMinutes(59));\n",
     HOST_CSPROJ, EXE,
     SECURITY, None, "catalog deadline bounds response headers"),

    # ---- lane fix/deadcode ----
    # F291: the slot that duplicated "second absolute clipping cut" now pins the Ceiling on a fractional
    # amount, the one ClipCut behaviour nothing else asserted. Every other ClipCut case uses an integral
    # amount, so rounding down instead fails exactly this one.
    ("deadcode: ClipCut rounds a fractional amount DOWN",
     ANIMATIONS,
     b"            return Math.Min(fullExtent, (int)Math.Ceiling(amount));",
     b"            return Math.Min(fullExtent, (int)Math.Floor(amount));",
     HOST_CSPROJ, EXE,
     "--hardening-selftest", "dp-hardening-selftest.txt", "fractional clipping amount rounds up"),

    # F300: the sprite-tile WITNESS compared MaximumSpriteTiles to the constant it is DEFINED as, so no
    # change of the shared value could fail it. It now pins the literal on both sides; moving the runtime's
    # value is what has to fire it.
    ("deadcode: the shared sprite-tile limit moves away from 1024",
     XML_CS,
     b"        internal const int MaximumFrames = 1024;",
     b"        internal const int MaximumFrames = 2048;",
     HOST_CSPROJ, EXE,
     SECURITY, None, "share the sprite-tile limit"),

    # F298: a section that throws is a named FAIL line and the sections after it still run. Before the
    # per-section guard the same throw killed the process with a stack dump and no summary, which this
    # ladder reads as BROKEN (no verdict), never as a firing.
    ("deadcode: a security self-test section throws",
     SECURITY_SELFTEST,
     b"        private static void CheckRestartLifecycle(\n"
     b"            ref int failures,\n"
     b"            TextWriter output)\n"
     b"        {\n"
     b"            var events = new List<string>();\n",
     b"        private static void CheckRestartLifecycle(\n"
     b"            ref int failures,\n"
     b"            TextWriter output)\n"
     b"        {\n"
     b"            var events = new List<string>();\n"
     b"            if (events.Count == 0) throw new InvalidOperationException(\"mutation: the section throws\");\n",
     HOST_CSPROJ, EXE,
     SECURITY, None, "CheckRestartLifecycle threw InvalidOperationException"),

    # F289: the CoreTests scale pins moved from the deleted integer API onto the fractional path the product
    # uses; the one-pixel floor in FitFactorForFrameD is the branch the old pins never reached.
    ("deadcode: FitFactorForFrameD loses its one-pixel floor",
     RUNTIME_GEOMETRY,
     b"            if (smaller > 0 && (double)smaller * f < 1.0) f = 1.0 / smaller;",
     b"            if (smaller > 0 && (double)smaller * f < 0.0) f = 1.0 / smaller;",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "A frame that would shrink below one pixel was not floored."),

    # F108: an unknown provider id must change nothing. The old shape answered it with the first preset
    # (then the local Ollama row); restoring a first-row fallback is the trap coming back.
    ("deadcode: an unknown provider id falls back to the first preset again",
     AISETTINGS,
     b"            if (!AiProviders.TryGet(provider, out preset)) return OpenAiBaseUrl;",
     b"            if (!AiProviders.TryGet(provider, out preset)) preset = AiProviders.All[0];",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an unknown provider id leaves the selector and both endpoints untouched"),

    # F082: an emotion that maps to nothing is a FAIL line, not an IndexOutOfRangeException out of Run.
    # Before FirstOrEmpty this same mutation was one EXC line and no verdict, which the ladder grades as
    # BROKEN, never as a firing.
    ("deadcode: 'happy' maps to no animation",
     AIBRAIN_MODULE,
     b'                case "happy":    return new string[] { "flower", "jump", "boing", "bounce", "run", "walk" };',
     b'                case "happy":    return new string[0];',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "WITNESS 'happy' still leads with flower"),

    # F082: the reflected field is renamed. The after-retire checks now create their manager inside the try,
    # so the MissingFieldException is a FAIL line naming the check; before, it escaped to Run's outer catch.
    ("deadcode: AiSessionManager._operation is renamed under the probe",
     AIENGINE_SECURITY,
     b"            FieldInfo field = typeof(AiSessionManager).GetField(\n"
     b"                \"_operation\",\n",
     b"            FieldInfo field = typeof(AiSessionManager).GetField(\n"
     b"                \"_operationX\",\n",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "MissingFieldException"),

    # F090: a pre-check in the retry helper refuses the cancelled token before the backend is entered; the
    # cancel still surfaces as cancellation, so only the new ChatCalls check can tell the two apart.
    ("deadcode: the retry helper refuses a cancelled token before calling the backend",
     AIBRAIN_ENGINE,
     b"            string firstError = null;\n"
     b"            try\n"
     b"            {\n"
     b"                string reply = await backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);\n",
     b"            string firstError = null;\n"
     b"            try\n"
     b"            {\n"
     b"                ct.ThrowIfCancellationRequested();\n"
     b"                string reply = await backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);\n",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "ChatAsync was entered exactly once"),

    # F131: a downloaded pack is validated before it is written, and a refusal is its own download cause.
    # Dropping the refusal writes the invalid bytes and counts them installed, which is the shape this fixed.
    ("deadcode: the downloader writes a malformed catalog payload again",
     FORTUNES_MODULE,
     b"                        { failed++; malformed++; continue; }",
     b"                        { }",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--fortunes-selftest", "dp-fortunes-selftest.txt",
     "a malformed catalog payload is refused, not installed"),

    # F136: a second Warm on the same picker supersedes the first. Dropping the second call (the first warm
    # then runs to completion and publishes its own pool) is what the rewarm case has to see.
    ("deadcode: a second Warm on the same picker is dropped instead of superseding the first",
     SMART_FORTUNES,
     b"                if (_warmCancellation != null)\n"
     b"                {\n"
     b"                    try { _warmCancellation.Cancel(); } catch { }\n"
     b"                }\n"
     b"                if (snapshot == null || snapshot.Count == 0)",
     b"                if (_warmCancellation != null)\n"
     b"                {\n"
     b"                    return;\n"
     b"                }\n"
     b"                if (snapshot == null || snapshot.Count == 0)",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "rewarm_supersedes=FAIL"),

    # F120: FilterSelfTest's per-case lines reach the probe output. A sub-case made to fail must show its OWN
    # line in the module marker, not only the one-line verdict above it; an empty pool picking null is the
    # smallest production change that fails exactly one of them.
    ("deadcode: an empty pool picks null and the filter sub-report names the case",
     FORTUNE_PROVIDER,
     b"            if (n == 0) return \"\";\n            if (n == 1) return _poolE[0].Text;",
     b"            if (n == 0) return null;\n            if (n == 1) return _poolE[0].Text;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "FILTER FAIL impossible constraints did not stay empty"),

    # F185: the self-test's digest check reads the release-LIST parser, the path InstallAsync takes; a list
    # parser that loses the digest has to fail it.
    ("deadcode: the release-list parser drops the asset digest",
     WHISPER_INSTALLER,
     b"                        digestElement.ValueKind == JsonValueKind.String) digest = digestElement.GetString();",
     b"                        digestElement.ValueKind == JsonValueKind.String) digest = null;",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "release JSON yields the digest"),

    # F190: the ICS parser's id scheme is uid@startUtc, pinned by IcsUrlSource.SelfCheck; an id of the bare
    # uid collapses a recurring series onto one fired id.
    ("deadcode: an ICS occurrence id loses its start",
     REMINDER_ICS,
     b"                    string id = uid + \"@\" + startUtc.ToString(\"o\");",
     b"                    string id = uid + \"@\";",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "an occurrence id is uid@startUtc"),

    # F198: the one slot lookup stops one short, so the last slot reads as the default.
    ("deadcode: the slot lookup stops one slot short",
     REMINDER_MODULE,
     b"            for (int i = 1; i <= MaxSlots; i++)\n"
     b"                if (string.Equals(SlotId(i), sourceId, StringComparison.Ordinal)) return i;",
     b"            for (int i = 1; i < MaxSlots; i++)\n"
     b"                if (string.Equals(SlotId(i), sourceId, StringComparison.Ordinal)) return i;",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "every slot id maps to its index"),

    # F203: the shared HH:mm parser loosens to NumberStyles.Integer again, which is what the two deleted copies
    # did; the sign case fires in QuietHours' own check and through the typed-reminder and briefing callers.
    ("deadcode: the shared HH:mm parser accepts a signed hour again",
     REMINDER_QUIET_HOURS,
     b"            if (!int.TryParse(hh, NumberStyles.None, CultureInfo.InvariantCulture, out h)) return false;",
     b"            if (!int.TryParse(hh, NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a signed hour is refused"),

    # F350: the module-host self-test asserts the poke ROUTING (to the poked pet, not a broadcast), the property
    # the Say/SayAll split was added for and never asserted. The test module broadcasting again is what fails it.
    ("deadcode: the test module broadcasts a poke instead of answering the poked pet",
     TESTMODULE_CS,
     b"            if (info.Pet != null) _host.Say(info.Pet, \"poked!\"); else _host.SayAll(\"poked!\");",
     b"            _host.SayAll(\"poked!\");",
     TESTMODULE_CSPROJ, TESTMODULE_DLL,
     "--module-host-selftest", "dp-module-host-selftest.txt",
     "the poke was routed to the poked pet, not broadcast"),

    # F385: the pet-mix merge fixture's seed has to BIND. The pre-rename "pets" key rode along as extension data
    # for three weeks while the mix under test started empty; the fixture now asserts the seeded mix was read.
    ("deadcode: the merge fixture seeds the pre-rename pets key again",
     CORETESTS_PROGRAM,
     b"                \"  \\\"companions\\\": [ { \\\"id\\\": \\\"pingus\\\", \\\"count\\\": 1 } ],\\n\" +",
     b"                \"  \\\"pets\\\": [ { \\\"id\\\": \\\"pingus\\\", \\\"count\\\": 1 } ],\\n\" +",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "Merge fixture's seeded mix was not read"),
)

# DERIVED from the cases, never typed. Every (flag, marker) a case will grade runs once, unmutated,
# before anything is scored, so a self-test that is already red is REFUSED instead of scoring every
# case on that flag FIRED against the failure that was already there. The hand-kept tuple this
# replaces lacked --module-selftest=reminder while the reminder cases ran it (N-reminder-01, found by
# lane fix/reminder on 2026-09-29, which verified its baseline by hand) and --module-selftest=petstudio
# while the gates lane's petstudio case ran that (found the same way, 2026-09-30). A list a case can
# be added to without updating is the drift this repo keeps finding; the set below cannot drift.
BASELINES = tuple(sorted(set((case[6], case[7]) for case in CASES), key=lambda pair: pair[0]))


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
    """The fixed set, plus every csproj a case names. Until 2026-09-29 this built the fixed set only, so
    a case whose project was not in it (Reminder, AiBrain) left its module DLL compiled from the LAST
    MUTATION after "restoring and rebuilding the clean tree": the source was byte-identical, the artefact
    was not, and the next self-test run against that build folder failed on the very assertion the case
    had just proved. Building the union also makes the baseline trustworthy for those flags."""
    fixed = [HOST_CSPROJ, FORTUNES_CSPROJ, BLINKINGLED_CSPROJ, PETSTUDIO_CSPROJ, REMEMBRANCE_CSPROJ,
             CORETESTS_CSPROJ, TESTMODULE_CSPROJ]
    for csproj in fixed + sorted(set(c[4] for c in CASES) - set(fixed)):
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
    if flag == SHIMEJI:
        return shimeji_selftest()
    if flag == SECURITY:
        return security()
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


def shimeji_selftest():
    """The converter's `selftest` verb (the gate's last step) in the marker vocabulary the ladder grades.

    EngineSelfTest.RunAll prints every sub-test's detail, a failing sub-test's failures as indented
    `FAIL <why>` lines, and then SELFTEST PASS or SELFTEST FAIL with exit 0 or 1. The indented FAIL
    lines already read as failure lines here (the ladder strips and looks for a FAIL prefix), so a
    case names its failure text as the expected fragment; the exit code becomes the column-0 RESULT=
    line, which is the verb's real verdict.
    """
    if not os.path.isfile(SHIMEJI_EXE):
        return None, "ShimejiConvert exe is missing: " + SHIMEJI_EXE
    try:
        proc = subprocess.run([SHIMEJI_EXE, "selftest"], capture_output=True, text=True, timeout=1800,
                              env=CHILD_ENV)
    except subprocess.TimeoutExpired:
        return None, "ShimejiConvert selftest did not exit in 1800s"
    report = (proc.stdout or "") + (proc.stderr or "")
    report += "\nRESULT=PASS\n" if proc.returncode == 0 else "\nRESULT=FAIL\n"
    return report, proc.returncode


def security():
    """--security-selftest reshaped into the marker vocabulary the ladder grades.

    SecuritySelfTest.Check writes '[PASS] x' / '[FAIL] x' per assertion and a 'Security self-test: PASS'
    or 'FAIL (n checks)' summary; each bracketed line becomes 'PASS: x' / 'FAIL: x' and a column-0 RESULT=
    line carries the exit code. An unhandled exception (the shape F298 removed) leaves no FAIL line and a
    non-zero exit, which the ladder reads as BROKEN (no verdict): the truth about such a run, and never a
    firing.
    """
    try:
        proc = subprocess.run([EXE, "--security-selftest"], capture_output=True, text=True, timeout=1800,
                              env=CHILD_ENV)
    except subprocess.TimeoutExpired:
        return None, "--security-selftest did not exit in 1800s"
    lines = []
    for line in (proc.stdout or "").splitlines():
        stripped = line.strip()
        if stripped.startswith("[FAIL] "):
            lines.append("FAIL: " + stripped[len("[FAIL] "):])
        elif stripped.startswith("[PASS] "):
            lines.append("PASS: " + stripped[len("[PASS] "):])
        else:
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
    rebuilt = True
    try:
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

    finally:
        # INSIDE a finally (F406). A TimeoutExpired out of build() or selftest(), or a Ctrl+C, used to
        # skip this rebuild: the source was restored by the per-case finally, the DLL or exe compiled
        # from the last mutant stayed in build\, and a hand-run --module-selftest or smoke before the
        # next build exercised the mutant. mutate-agentflow.py already had the shape; this file had
        # not learned it. The result is CHECKED, not discarded: a clean rebuild that fails is the one
        # thing this rebuild exists to prevent.
        print("\nrestoring and rebuilding the clean tree")
        rebuilt, rebuild_output = build_all()
        if not rebuilt:
            print("THE CLEAN REBUILD FAILED -- build\\ may still hold a mutant artefact:")
            print(rebuild_output[-800:])
    print("%d/%d fired." % (fired, len(cases)))
    return 0 if fired == len(cases) and rebuilt else 1


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
