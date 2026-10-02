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
agree before anything is scored -- on the baseline since F405, and on the FIRED and WRONG
rungs since R-063: a FAIL line under exit 0 and RESULT=PASS is a plumbing fault (a Check()
whose bool is not folded into ok), printed as one and never counted as a firing, because the
gate that reads the exit code would stay green on it. A run with no verdict is BROKEN, never
SURVIVED.

Every child runs with a private TEMP (one directory per harness run), because the marker
names are fixed by the flag and the exe writes them under Path.GetTempPath(): two same-user
runners sharing %TEMP% -- a gate in the main checkout and this harness in a worktree -- could
grade each other's build (F418). The directory is removed after a clean run and KEPT, named
on the console, after anything else, with every non-FIRED case's whole report under a
per-case name (R-060, R-065): the markers used to go with the directory in every outcome,
so a WRONG verdict survived only as two 140-character console lines.

Restore is byte-exact from a copy read into memory first, never from git.

    python tests/mutate-selftest-guards.py [--only=<substring>]
"""

import argparse
import io
import os
import re
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
# TESTMODULE_CSPROJ is defined ONCE, above with the fixed-set comment: a second, identical copy sat here
# from the fix/deadcode merge, and being later in the file it was the one that ran, so an edit to the
# documented definition alone would have been silently overridden (RA-356, RA-357).
TESTMODULE_DLL = os.path.join(BIN, "modules", "testmodule", "TestModule.dll")
REMINDER_QUIET_HOURS = os.path.join(REPO, "modules", "Reminder", "QuietHours.cs")
REMINDER_ICS = os.path.join(REPO, "modules", "Reminder", "IcsUrlSource.cs")
AIBRAIN_CSPROJ = os.path.join(REPO, "modules", "AiBrain", "AiBrain.csproj")
AIBRAIN_DLL = os.path.join(BIN, "modules", "aibrain", "AiBrain.dll")
AIBRAIN_ENGINE = os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs")
EMBEDDER = os.path.join(REPO, "modules", "Fortunes", "engine", "Embedder.cs")
SMART_FORTUNES = os.path.join(REPO, "modules", "Fortunes", "engine", "SmartFortunes.cs")
FORTUNE_IMPORTER = os.path.join(REPO, "modules", "Fortunes", "engine", "FortuneFileImporter.cs")
FORTUNES_PROBE = os.path.join(REPO, "modules", "Fortunes", "engine", "FortuneEngineProbe.cs")
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
MODULEKIT_TESTS = os.path.join(REPO, "tests", "DesktopAICompanion.CoreTests", "ModuleKitTests.cs")

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
DIAGNOSTIC_LOG = os.path.join(REPO, "src", "dotNet", "DiagnosticLog.cs")
STARTUP_REGISTRATION = os.path.join(REPO, "src", "dotNet", "StartupRegistration.cs")
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
# The pseudo-flag a case names to run packaging\Test-ModuleTemplate.ps1: it scaffolds the module TEMPLATE into a
# throwaway module, builds it and loads it through the real host's --module-selftest, then removes it again.
# A case on this flag names no csproj and no artefact (both None): the script's own scaffold IS the rebuild
# from the mutated template source (it deletes the sample first and asserts the built DLL exists), and it
# leaves nothing in build\ for the gate's module-list check to trip over. Until 2026-09-30 nothing graded
# the template's SelfTest at all (lane burn/tools).
TEMPLATE = "TEMPLATE"
TEMPLATE_PS1 = os.path.join(REPO, "packaging", "Test-ModuleTemplate.ps1")
TEMPLATE_MODULE = os.path.join(REPO, "templates", "desktop-ai-companion-module", "SampleModule.cs")
# Lane burn/tools' converter targets: the engine files its cases mutate, all compiled into SHIMEJI_ENGINE_DLL.
SHIMEJI_ENGINE_CS = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "Engine.cs")
SHIMEJI_PARSER = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "Shimeji", "ShimejiParser.cs")
SKIN_LAYOUT = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "Shimeji", "SkinLayout.cs")
SPRITE_SHEET_BUILDER = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "Shimeji", "SpriteSheetBuilder.cs")
EMITTER_SELFTEST = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "Shimeji", "EmitterSelfTest.cs")
PETGRAPH_SELFTEST = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "Shimeji", "PetGraphSelfTest.cs")
PETGRAPH_ENGINE = os.path.join(REPO, "tools", "ShimejiConvert.Engine", "PetGraph.cs")

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

    # Re-pointed 2026-09-30 by lane burn/petstudio (RA-134): the wiring assertion's one home is now
    # PetStudioModule.SelfTest, so it is graded through --module-selftest=petstudio; the copy the host's
    # --petstudio-selftest reached through BehaviourChainSelfCheck ran a second time per module self-test and
    # is gone. The mutation is unchanged.
    ("PetStudio's failure report stops reaching IHost.Log",
     PETSTUDIO_MODULE,
     b"            try { host.Log(Info.Id, what + \": \" + Categorize(ex)); } catch { }",
     b"            try { if (what == null) host.Log(Info.Id, what + \": \" + Categorize(ex)); } catch { }",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "a failure is logged under the module id as a category"),

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
    # Re-pointed again 2026-09-30 by lane burn/blinkingled: the read and the press are two statements
    # (`bool? lit` then `if (Toggle()) _phaseOn = false`) so a throwing press is recorded as -1 (R-024).
    # Same two regressions, same two expected assertions.
    ("Stop()'s corrective toggle block is deleted",
     SCROLLLOCK_BLINKER,
     b"                bool? lit;\n"
     b"                try { lit = ScrollLockReader(); } catch { lit = null; }\n"
     b"                if (lit == false) _phaseOn = false;\n"
     b"                else if (lit == true)\n"
     b"                {\n"
     b"                    try { if (Toggle()) _phaseOn = false; }\n"
     b"                    catch { LastWin32Error = -1; NoteDelivery(false, -1); }\n"
     b"                }",
     b"                _phaseOn = false;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "Stop() attempts the corrective toggle"),

    ("Stop() toggles without reading the key first",
     SCROLLLOCK_BLINKER,
     b"                try { lit = ScrollLockReader(); } catch { lit = null; }",
     b"                lit = true;",
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

    # Re-pointed 2026-09-30 by lane burn/blinkingled: `_phaseOn = stillOurs;` went with the one-expression
    # shape (R-024); zeroing regardless of the press's answer is now `Toggle(); _phaseOn = false;`. Same
    # regression, same expected assertion.
    ("Stop() zeroes the belief after a refused corrective toggle",
     SCROLLLOCK_BLINKER,
     b"                    try { if (Toggle()) _phaseOn = false; }",
     b"                    try { Toggle(); _phaseOn = false; }",
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
    # Corrected 2026-09-30 by lane burn/blinkingled (N-burn-blinkingled-01): the suite has pinned its own
    # blinker's reader dark since cf27664, so that Stop() reads dark and presses nothing, and since Shutdown()
    # no longer clears a light the session never drove (RA-090) no later headless Init puts the key back
    # either. The suite itself does now: an odd number of ACCEPTED presses on its instance is followed by one
    # more, after the FAIL is recorded, so the mutated run still leaves the key where it was.
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
    # (Re-pointed by lane burn/aibrain, RA-060: the log line moved into LogDeclined.)
    ("aibrain: the explicit ask ignores the fullscreen stand-down again",
     os.path.join(REPO, "modules", "AiBrain", "AiBrainModule.cs"),
     b"            if (FullscreenBlocked())\n"
     b"            {\n"
     b'                LogDeclined(host, "fullscreen stand-down");',
     b"            if (FullscreenBlocked() && host == null)\n"
     b"            {\n"
     b'                LogDeclined(host, "fullscreen stand-down");',
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

    # (Re-pointed by lane burn/aibrain, R-018: PreserveCorruptPrimary reports whether the primary was removed.)
    ("aibrain: the rejected primary is destroyed by the recovery again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSettings.cs"),
     b"            string preserved = result == ReadResult.Unreadable ? PreserveCorruptPrimary(out primaryRemoved) : null;",
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
    # Re-pointed by lane burn/aibrain (RA-062): the condition also re-lists while the inventory is unknown; the
    # mutant requires the backend to have been up already, so no transition ever lists.
    ("aibrain: the inventory is no longer taken on the transition to reachable",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b"            if (up && (!wasUp || _available == null)) await RefreshInventoryAsync(ct).ConfigureAwait(false);",
     b"            if (up && wasUp && (!wasUp || _available == null)) await RefreshInventoryAsync(ct).ConfigureAwait(false);",
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

    # R-014: an empty re-list replaces a good inventory again. Re-pointed by lane burn/aibrain (RA-062): the
    # branch no longer tests _available, because an empty listing is never stored; the mutant confines it to
    # the unknown-inventory case, so a re-list that comes back empty overwrites a known one again.
    ("aibrain: an empty re-list replaces a good inventory again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiBrain.cs"),
     b"                if (listed.Count == 0)\n"
     b"                {\n"
     b"                    // A listing whose bound tripped comes back empty",
     b"                if (listed.Count == 0 && _available == null)\n"
     b"                {\n"
     b"                    // A listing whose bound tripped comes back empty",
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
     b"                    _loadFailure = DescribeLoadFailure(ex);",
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
     b"                bool current = buildable && !force && _smartBuilding && !_smartBuildFailed &&\n"
     b"                               !(_smart != null && _smart.StandDownReason == SmartStandDownReason.WarmFailed) &&\n"
     b"                               string.Equals(signature, _indexedSignature, StringComparison.Ordinal);",
     b"                bool current = buildable && !force && _smartBuilding && !_smartBuildFailed &&\n"
     b"                               !(_smart != null && _smart.StandDownReason == SmartStandDownReason.WarmFailed) &&\n"
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
     b"            System.Threading.Tasks.Task.Run(delegate { BuildSmartPickerCounted(generation, old, pool); });",
     b"            if (old != null) { try { old.Dispose(); } catch { } }\n"
     b"            System.Threading.Tasks.Task.Run(delegate { BuildSmartPickerCounted(generation, null, pool); });",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "disposed on a pool thread, not the thread that applied"),

    # F145: the publish-time line claims readiness again.
    ("fortunes: the publish-time line claims the picker is ready again",
     FORTUNES_MODULE,
     b"                return \"smart picker constructed, warming \" + Invariant(lines) + \" lines in the background\";",
     b"                return \"smart picker ready (\" + Invariant(lines) + \" lines indexed)\";",
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
    # Re-pointed 2026-09-30 by lane burn/reminder: the scan skips attempts a newer landing superseded (R-044),
    # so `_started.Count > 0` became `live` on both lines; the mutations negate it.
    ("the refresh stall is never reported on the served snapshot",
     CACHING_CALENDAR_SOURCE,
     b"                    if (live && nowUtc - oldest > deadline)\n",
     b"                    if (!live && nowUtc - oldest > deadline)\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a refresh that outlives its deadline is reported on the served snapshot"),

    ("an overdue refresh never triggers a retry",
     CACHING_CALENDAR_SOURCE,
     b"                    abandoned = live && nowUtc - newest > deadline;\n",
     b"                    abandoned = !live && nowUtc - newest > deadline;\n",
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

    # Re-pointed 2026-09-30 by lane burn/reminder: the predicate gained the kicked-key term (RA-169), and the cap
    # check now reads OutstandingRefreshes, which Fetch updates under its lock, so this fires at its named line
    # whatever the pool's scheduling latency (R-049).
    ("the cap on parked refresh attempts is one higher than declared",
     CACHING_CALENDAR_SOURCE,
     b"                kick = stale && (!inFlight || abandoned || !pendingForKey) && _started.Count < MaximumOutstandingRefreshes;\n",
     b"                kick = stale && (!inFlight || abandoned || !pendingForKey) && _started.Count < MaximumOutstandingRefreshes + 1;\n",
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
    # each mutation is caught by the test's own bound ("hung past the 12 s bound") rather than hanging the run.
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
    # Re-pointed 2026-09-30 by lane burn/reminder: PlayCustom answers an outcome the Test button awaits
    # (N-deadcode-02), so the call returns its Task; the inline shape wraps the synchronous result instead.
    ("the custom chime is read inline on the caller's thread",
     os.path.join(REPO, "modules", "Reminder", "Chime.cs"),
     b"                try { return Task.Run(() => PlayCustom(host, path, loadCustom)); }\n",
     b"                try { return Task.FromResult(PlayCustom(host, path, loadCustom)); }\n",
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
    # Re-pointed 2026-09-30 by lane burn/reminder: the gate sits after the chime and answers one of two
    # constants (RA-176), so the guard line is the two-line `if`; the mutation still makes it never true.
    ("followups: reminder: 'Test this reminder' reports sent with nobody on screen again",
     os.path.join(REPO, "modules", "Reminder", "ReminderModule.cs"),
     b"                if (!AnyCompanionOnScreen())\n"
     b"                    return chime == null ? NoCompanionStatus : NoCompanionChimedStatus + await ChimeOutcome(chime);",
     b"                if (_host == null && !AnyCompanionOnScreen())\n"
     b"                    return chime == null ? NoCompanionStatus : NoCompanionChimedStatus + await ChimeOutcome(chime);",
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


    # ---- lane burn/reminder ----
    # The Phase 8 burn-down of the Reminder module (2026-09-30). Named "burn: ..." so one --only=burn: run covers
    # the lane; every case names Reminder.csproj and Reminder.dll, the artefact --module-selftest=reminder loads.
    # The module's per-case SelfCheck details are FAIL: lines of their own since RA-172, so a case can name the
    # exact link shape or parser input that broke instead of the suite that contains it.

    # RA-169: the kick predicate waits for the in-flight attempt again, whatever key it was kicked for, so an
    # edit made while the old target's attempt is parked is not fetched until it lands or outlives the deadline
    # (the F200 regression as shipped).
    ("burn: an edit while the old target's attempt is parked waits for the deadline again",
     CACHING_CALENDAR_SOURCE,
     b"                kick = stale && (!inFlight || abandoned || !pendingForKey) && _started.Count < MaximumOutstandingRefreshes;\n",
     b"                kick = stale && (!inFlight || abandoned) && _started.Count < MaximumOutstandingRefreshes;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "is fetched on the next tick, not after the deadline"),

    # RA-169, the other direction: the pending test compares against the LANDED key, which is stamped only when
    # an attempt lands, so while the new target's own attempt is in flight every tick reads as a change and
    # kicks again (the over-eager predicate the re-audit warned against).
    ("burn: a key change kicks a new attempt on every tick while the new target is in flight",
     CACHING_CALENDAR_SOURCE,
     b"                bool pendingForKey = inFlight && string.Equals(key, _kickedKey, StringComparison.Ordinal);\n",
     b"                bool pendingForKey = inFlight && string.Equals(key, _lastKey, StringComparison.Ordinal);\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "further ticks do not kick it again"),

    # R-044: the stall scan takes every parked attempt again, the ones a newer landing superseded included.
    ("burn: a superseded parked attempt drives the stall report again",
     CACHING_CALENDAR_SOURCE,
     b"                        if (attempt.Key < _landedGeneration) continue;\n",
     b"",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a superseded attempt still parked does not drive the stall report"),

    # RA-170: ParseIcs reports garbage as a healthy empty calendar, which the slot would vouch for and the F204
    # prune would then act on. The THROW path: iCal.Net throws on "this is not a calendar" rather than returning
    # null (measured 2026-09-30: the same mutation on the null-calendar return SURVIVED). `ex` stays used, since
    # an unused catch variable is a warning and the module builds warnings-as-errors.
    ("burn: garbage parses as a healthy empty calendar",
     REMINDER_ICS,
     b"                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = \"The calendar feed is not valid iCalendar: \" + Short(ex.Message) };\n",
     b"                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = string.IsNullOrEmpty(ex.Message) ? \"The calendar feed is not valid iCalendar.\" : null };\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "garbage is an error snapshot"),

    # RA-171: the Webex provider takes any path on any *webex.com host again (the shipped pattern), so a help
    # article ahead of the join link is what the tray would open.
    ("burn: Webex matches any path on any webex.com host again",
     os.path.join(REPO, "modules", "Reminder", "MeetingLinkDetector.cs"),
     b"            new Provider(\"Webex\", @\"https://(?:[a-z0-9\\-]+\\.)*webex\\.com/(?:(?:[a-z0-9._\\-]+/)*(?:j|e|g)\\.php\\?|(?:meet|join)/|webappng/sites/|wbxmjs/joinservice/)[^\\s\"\"'<>]+\"),\n",
     b"            new Provider(\"Webex\", @\"https://[a-z0-9.\\-]*webex\\.com/[^\\s\"\"'<>]+\"),\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "help.webex.com is not a join link"),

    # RA-171: the teams.live.com provider deleted; it had been accepted all along and asserted never.
    ("burn: the teams.live.com join link is no longer matched",
     os.path.join(REPO, "modules", "Reminder", "MeetingLinkDetector.cs"),
     b"            new Provider(\"Teams\", @\"https://teams\\.live\\.com/meet/[^\\s\"\"'<>]+\"),\n",
     b"",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "teams.live.com short link keeps its passcode"),

    # RA-172: F194's mutation (the parser loses its 'at' branch), graded on the CASE the detail names rather
    # than on the suite's FAIL line; before RA-172 the detail quoted the last must-fail case's refusal text.
    ("burn: the parser loses its 'at' branch and the detail names the case that broke",
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
     "'at 15:00 Call the vet' was refused"),

    # RA-176: the companion gate moves back in front of the chime, so a tray-only user cannot audition one.
    ("burn: 'Test this reminder' withholds the chime with nobody on screen again",
     REMINDER_MODULE,
     b"                System.Threading.Tasks.Task<string> chime = _settings.GetBool(SlotKey(slot, \"chimeOn\"), true)\n",
     b"                if (!AnyCompanionOnScreen()) return NoCompanionStatus;\n"
     b"                System.Threading.Tasks.Task<string> chime = _settings.GetBool(SlotKey(slot, \"chimeOn\"), true)\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "still auditioned with no companion on screen"),

    # RA-177: a guard that stops the per-slot prune on an empty feed. Before the six 1.0.3 checks moved onto a
    # one-slot aggregate this SURVIVED: measured by hand on 2026-09-30, RESULT=PASS with the guard in place.
    ("burn: the per-slot prune keeps everything when the feed is empty",
     REMINDER_MODULE,
     b"            return fired.RemoveWhere(id =>\n"
     b"            {\n"
     b"                string slot = SlotOf(id);\n",
     b"            if (feedIds.Count == 0) return false;\n"
     b"            return fired.RemoveWhere(id =>\n"
     b"            {\n"
     b"                string slot = SlotOf(id);\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a loaded but empty calendar drops everything"),

    # RA-177: the null-health branch prunes on the whole snapshot again (the 1.0.3 rule it replaced).
    ("burn: a snapshot without per-slot health prunes on its Error again",
     REMINDER_MODULE,
     b"                // Those checks now run through a one-slot aggregate, and this branch prunes nothing.\n"
     b"                return false;\n",
     b"                // Those checks now run through a one-slot aggregate, and this branch prunes nothing.\n"
     b"                return fired.RemoveWhere(id => !feedIds.Contains(ReminderScheduler.EventIdOf(id))) > 0;\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a snapshot without per-slot health prunes nothing"),

    # RA-178: a failed settings write is swallowed again (SaveSettings answers true whatever Save() said).
    ("burn: a failed settings write is swallowed again",
     REMINDER_MODULE,
     b"            try { ok = _settings.Save(); } catch { ok = false; }\n",
     b"            try { _settings.Save(); ok = true; } catch { ok = false; }\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "the failed settings write is logged"),

    # RA-178: the tick stops retrying a failed write.
    ("burn: the tick no longer retries a failed settings write",
     REMINDER_MODULE,
     b"                if (changed || _savePending) SaveFired();\n",
     b"                if (changed) SaveFired();\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "retried on the next tick"),

    # R-045: the chime guard is keyed on nothing again, one slot for every file (the F188 shape as shipped).
    ("burn: the chime guard is keyed on nothing again",
     os.path.join(REPO, "modules", "Reminder", "Chime.cs"),
     b"                    if (!_customReadsInFlight.Add(path)) return Task.FromResult(DuplicateOutcome);\n",
     b"                    if (!_customReadsInFlight.Add(\"\")) return Task.FromResult(DuplicateOutcome);\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "a chime for a different file during that read is not dropped"),

    # R-050: the deadline message names the wrong phase (headers for a stalled body), so the stall check no
    # longer proves the body read was what the deadline cut.
    ("burn: the .ics deadline names the headers phase for a stalled body",
     REMINDER_ICS,
     b"                throw new TimeoutException(headersReceived\n",
     b"                throw new TimeoutException(!headersReceived\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "in the body phase"),

    # N-deadcode-02: the refused-file log line is dropped.
    ("burn: a refused chime file is no longer logged",
     os.path.join(REPO, "modules", "Reminder", "Chime.cs"),
     b"            try { host.Log(ReminderModule.Id, \"chime: \" + outcome); } catch { }\n",
     b"            try { } catch { }\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "the refusal is logged"),

    # N-deadcode-02: the Test button drops the chime's outcome: "test sent" over a silent fall-back again.
    ("burn: 'Test this reminder' reports sent over a refused chime file again",
     REMINDER_MODULE,
     b"            return string.IsNullOrEmpty(outcome) ? \"\" : \" Chime: \" + outcome + \".\";\n",
     b"            return string.IsNullOrEmpty(outcome) ? \"\" : \"\";\n",
     REMINDER_CSPROJ, REMINDER_DLL,
     "--module-selftest=reminder", "dp-module-reminder-selftest.txt",
     "says the built-in chime played instead"),
    # ---- lane burn/blinkingled ----
    # Phase 8 burn-down of the re-audit's BlinkingLed lines. Every case drives the module self-test through
    # the engine's seams (KeypressSender, ScrollLockReader, CapsLockReader), so each fires on a box that
    # accepts every synthesized keypress and on a headless runner alike. Named "burn: ..." so one
    # --only=burn: run covers the lane.

    # R-024: Stop()'s corrective toggle swallows a throwing press again, the one-catch shape, so the delivery
    # log carries no win32=-1 line for the path that runs at Off, at a Caps Lock stop and at Shutdown.
    ("burn: Stop()'s throwing corrective toggle is swallowed without the -1 marker again",
     SCROLLLOCK_BLINKER,
     b"                    try { if (Toggle()) _phaseOn = false; }\n"
     b"                    catch { LastWin32Error = -1; NoteDelivery(false, -1); }",
     b"                    try { if (Toggle()) _phaseOn = false; }\n"
     b"                    catch { }",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "a throwing corrective toggle in Stop() is recorded as -1"),

    # RA-094 (c): BlinkOnce() stops re-arming the timer, the shipped shape, so a blink made during the dark
    # gap leaves the LED lit until that gap's tick. The re-arm line is one of three identical lines in the
    # file (SetRate and Tick carry the others), so the comment line above it anchors the pattern.
    ("burn: a blink-once while the cadence runs leaves the timer on the old gap again",
     SCROLLLOCK_BLINKER,
     b"            // re-arm while the feature is off: _timer is null then, and a refused press moves nothing.\n"
     b"            if (_timer != null) _timer.Interval = Math.Max(1, _phaseOn ? _onMs : _offMs);\n"
     b"        }",
     b"            // re-arm while the feature is off: _timer is null then, and a refused press moves nothing.\n"
     b"        }",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "a blink-once during the dark gap lights the key and re-arms the LIT interval"),

    # RA-088: the enable path stops asking about Caps Lock, the shipped shape. `&& _host == null` keeps the
    # condition compiling and never true after Init, with no unreachable-code warning (CS0162 would fail the
    # module's warnings-as-errors build), the same device the followups lane used for Reminder.
    ("burn: enabling under Caps Lock starts and speaks the ON line again (the shipped shape)",
     BLINKINGLED_MODULE,
     b"            if (enabled && !was && announce && _blinker.CapsLockStopsNow())",
     b"            if (enabled && !was && announce && _blinker.CapsLockStopsNow() && _host == null)",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "enabling under Caps Lock does not start the blinker"),

    # RA-088, the decision half: the refusal is for the user's enable only. Dropping the announce gate refuses
    # at startup too, where nothing was said and the first tick has always done the stopping.
    ("burn: the Caps Lock refusal applies at startup too",
     BLINKINGLED_MODULE,
     b"            if (enabled && !was && announce && _blinker.CapsLockStopsNow())",
     b"            if (enabled && !was && _blinker.CapsLockStopsNow())",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "at startup (announce false) Caps Lock is left to the first tick"),

    # RA-090: Shutdown() clears the light whatever this session did, the shipped shape: every headless host that
    # Inits the module over a lit Scroll Lock and shuts it down then presses the user's key off for real.
    # THAT INCLUDES THIS CASE'S OWN RUN: the convention runner's loader-owned instance adopts the developer's
    # real key and, under the mutant, presses a lit Scroll Lock off at ShutdownAll (the defect, reinstated), and
    # no later case turns it back on. Measured 2026-10-01: a whole run that started with the key ON ended with
    # it OFF, this case being the only press in the run that the suite's own pairing does not cover. Run the
    # harness with Scroll Lock off, or expect to press it once afterwards.
    ("burn: Shutdown clears a light this session never drove again (the shipped shape)",
     BLINKINGLED_MODULE,
     b"                if (_blinker.AttemptCount > 0) { try { _blinker.Stop(); } catch { } }   // leaves the LED off rather than stuck lit",
     b"                try { _blinker.Stop(); } catch { }   // leaves the LED off rather than stuck lit",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "Shutdown leaves a lit key it adopted but never drove"),

    # N-burn-blinkingled-02 (RA-094 b, taken 2026-10-01): the cadence tick stops re-syncing its belief from the
    # key, the shipped shape, so a manual Scroll Lock press mid-run inverts belief and key for the rest of the
    # run and Stop() leaves the LED lit. `keyLit = _phaseOn;` keeps the local used, so the mutant compiles clean.
    ("burn: the cadence tick stops re-syncing the belief from the key",
     SCROLLLOCK_BLINKER,
     b"                try { keyLit = ScrollLockReader(); } catch { keyLit = _phaseOn; }",
     b"                keyLit = _phaseOn;",
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "a manual press mid-cadence is re-synced on the next tick"),

    # RA-091: the pane's rate list is compared with the pinned literal now, not with the engine array it IS.
    # A pane that drops a rate fails this line and nothing else (the tray submenu reads the engine array).
    ("burn: the pane's rate list drops Hyper",
     BLINKINGLED_MODULE,
     b"                        Options = ScrollLockBlinker.RateNames,",
     b'                        Options = new[] { "Glacial", "Sluggish", "Slow", "Normal", "Fast" },',
     BLINKINGLED_CSPROJ, BLINKINGLED_DLL,
     "--module-selftest=blinkingled", "dp-module-blinkingled-selftest.txt",
     "the pane offers exactly the pinned rate table"),

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

    # N-reminder-05 (the animations half; RA-215 did the sounds): PlayedAnimations is the live list again.
    ("burn/host-shell: RecordingHost hands out its live PlayedAnimations list again",
     MODULEKIT_RECORDING_HOST,
     b"        public List<string> PlayedAnimations { get { lock (_recordSync) return new List<string>(_playedAnimations); } }\n",
     b"        public List<string> PlayedAnimations { get { return _playedAnimations; } }\n",
     CORETESTS_CSPROJ, CORETESTS_MODULEKIT_DLL,
     CORETESTS, None, "A PlayedAnimations view handed to a test moved under it"),

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
    # ---- lane burn/petstudio ----

    # RA-145: a rejected re-parse has no graph, and the timeline's drop rule must drop nothing without one. The
    # mutant ignores the flag, which is the shipped shape restored: every step is "missing" from the empty node
    # list. The window-side wiring of the same guard is a source invariant (mutate-hardening-guards.py).
    ("burn/petstudio: the timeline drop rule ignores whether a graph exists (RA-145)",
     BEHAVIOUR_CHAIN,
     b"            if (steps == null || !graphKnown || nodes == null) return gone;",
     b"            if (steps == null || nodes == null) return gone;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "a rejected re-parse drops no timeline step"),

    # RA-128: ENGINE follows the runtime's binding (AnimNode.IsEngineEntry); the mutant puts back the
    # case-insensitive name match, which badges a hand-authored 'Kill' ENGINE. Fires on the hand-built graph
    # and on the re-spelled fixture alike.
    ("burn/petstudio: ENGINE matches the reserved names case-insensitively again (RA-128)",
     ANIM_CAPABILITY,
     b"            if (node.IsEngineEntry) return true;",
     b"            foreach (string magic in PetGraph.ReservedEntryPointNames)\n"
     b"                if (string.Equals(node.Name, magic, StringComparison.OrdinalIgnoreCase)) return true;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "a hand-authored 'Kill' the runtime did not bind is not ENGINE"),

    # RA-130: the `fall` growth bound. The mutant deletes the engine exclusion from Holdable. The hand-built
    # case now reaches `fall` by a SEQUENCE edge, on which a y-only drop travels along the wall, so the exclusion
    # is the only rule between `fall` and the surface set and the floor behind it reads CLING without it; with
    # the border edge it had before, the kind flip and the axis test cut `fall` first and this mutant SURVIVED.
    ("burn/petstudio: Holdable stops excluding the engine's own animations (RA-130)",
     ANIM_CAPABILITY,
     b"            return node != null && !node.HasGravity && !IsEngineOwned(node);",
     b"            return node != null && !node.HasGravity;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "the floor behind it stays floor"),

    # RA-135: 'the original animations are left untouched' was a count. The mutant makes each clone SHARE its
    # source's Sequence node, so PointEveryExitAt rewires the original too; the count is unchanged and every
    # other chain check still passes, and only the edge-for-edge comparison against a fresh parse notices.
    ("burn/petstudio: a clone shares its source's Sequence node (RA-135)",
     BEHAVIOUR_CHAIN,
     b"                Sequence = new XmlData.SequenceNode\n"
     b"                {\n"
     b"                    RepeatFromFrame = source.Sequence != null ? source.Sequence.RepeatFromFrame : 0,\n"
     b"                    RepeatCount = source.Sequence != null ? source.Sequence.RepeatCount : \"0\",\n"
     b"                    Frame = source.Sequence != null && source.Sequence.Frame != null\n"
     b"                        ? (int[])source.Sequence.Frame.Clone() : new[] { 0 },\n"
     b"                    Action = source.Sequence != null ? source.Sequence.Action : null,\n"
     b"                    Next = new XmlData.NextNode[0],\n"
     b"                },\n",
     b"                Sequence = source.Sequence,\n",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "the original animations are left untouched, edge for edge"),

    # RA-148: the strip counts plays as the compiler does. The mutant counts chips again; the at-cap pins in
    # LimitsHold read the count.
    ("burn/petstudio: the timeline counts chips instead of plays again (RA-148)",
     BEHAVIOUR_CHAIN,
     b"                    if (s != null) plays += Math.Max(1, s.Repeat);",
     b"                    if (s != null) plays += 1;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "the strip refuses a chip once the chain plays"),

    # RA-132: Classify's within-kind pick had no assertion. The mutant inverts it, so the lighter twin wins.
    ("burn/petstudio: Classify picks the lighter of two same-kind edges (RA-132)",
     BEHAVIOUR_CHAIN,
     b"                    if (best == null || e.Probability > best.Probability) best = e;",
     b"                    if (best == null || e.Probability < best.Probability) best = e;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "the heavier wins and brings its own flag"),

    # N-petstudio-02: the FALL label. The mutant deletes the rule, so a drop reads 'Plays in place' again.
    ("burn/petstudio: a descent reads 'plays in place' again (N-petstudio-02)",
     ANIM_CAPABILITY,
     b"            if (Descends(node)) return AnimCapability.Fall;\n",
     b"",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "a gravity-less drop holding nothing is a FALL"),

    # RA-138: the pane status. The mutant answers the success text for a failed open, which is the shipped
    # constant back under a new name.
    ("burn/petstudio: the pane reports a failed open as open again (RA-138)",
     PETSTUDIO_MODULE,
     b"            if (opened) return \"Companion Studio is open.\";",
     b"            if (opened || failureCategory != null) return \"Companion Studio is open.\";",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "a failed open is not reported to the pane as open"),

    # RA-141: Save()'s false discarded again. The mutant returns without logging whenever nothing was said yet,
    # which is every first failure, so a persistently unwritable settings file is never said.
    ("burn/petstudio: a folder that cannot be remembered is never said (RA-141)",
     PETSTUDIO_WINDOW,
     b"            if (settings.Save() || alreadyReported) return;",
     b"            if (settings.Save() || !alreadyReported) return;",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "a folder that cannot be remembered is said once"),

    # RA-134: the path-leak assertion, moved into the module SelfTest from the chain check. The mutant logs the
    # exception MESSAGE, which carries the profile path, in place of the category.
    ("burn/petstudio: ReportFailure logs the path-carrying message (RA-134)",
     PETSTUDIO_MODULE,
     b"            try { host.Log(Info.Id, what + \": \" + Categorize(ex)); } catch { }",
     b"            try { host.Log(Info.Id, what + \": \" + (ex == null ? \"none\" : ex.Message)); } catch { }",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "does NOT reach the log"),
    # N-burn-tools-01 (reopened lane, 2026-10-01): a multi-skin archive is the author's pick. The mutant converts
    # skins[0] again, the shipped shape, so the recording picker is never consulted.
    ("burn/petstudio: a multi-skin archive converts its first skin again (N-burn-tools-01)",
     PETSTUDIO_WINDOW,
     b"            return picker == null ? null : picker(skins);",
     b"            return skins[0];",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "a multi-skin archive is put to the picker"),

    # N-burn-tools-02: the round-trip diagnostic leaves the import status again (the one-clause shape).
    ("burn/petstudio: the import status drops the round-trip diagnostic again (N-burn-tools-02)",
     PETSTUDIO_WINDOW,
     b"                if (!string.IsNullOrWhiteSpace(roundTripDiagnostic)) prefix += \" (\" + roundTripDiagnostic.Trim() + \")\";\n",
     b"",
     PETSTUDIO_CSPROJ, PETSTUDIO_DLL,
     "--module-selftest=petstudio", "dp-module-petstudio-selftest.txt",
     "F427's first-difference diagnostic reaches the import status"),


    # ---- lane burn/tools ----
    # Every converter case names ShimejiConvert.csproj and the ENGINE dll (the file that is mutated compiles into
    # it; the CLI exe's timestamp does not move for an engine edit). Named "burn-tools: ..." so one
    # --only=burn-tools run covers the lane. Mutations keep the build green under warnings-as-errors: a
    # condition is inverted or widened, never made constant, because `if (false)` is CS0162.

    # The heading item "every converted climb replays its mount pose on every cycle". The prefix rule reads two
    # declared facts (a one-shot block whose leading poses hold still); this inverts the declaration it gates on,
    # so the one-shot climb repeats from 0 and the LOOPed stock rhythm would be cut instead.
    ("burn-tools: the mount prefix is read from LOOPED blocks instead of one-shot ones",
     PET_EMITTER,
     b"            if (e == null || e.Source == null || !e.Source.PlaysOnce || e.ForcedVelY.HasValue) return 0;",
     b"            if (e == null || e.Source == null || e.Source.PlaysOnce || e.ForcedVelY.HasValue) return 0;",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "mount prefix sets repeatfrom: the one-shot climb repeats from frame 0"),

    # The coupling the item named: the node writes the prefix while the reach solver is fed the old assumption,
    # so one pass lands 1600px short of the distance it was solved for.
    ("burn-tools: the reach solver is fed repeatfrom 0 while the node writes the prefix",
     PET_EMITTER,
     b"                repeatFrom = MountPrefixLength(e, false);\n"
     b"                repeat = SurfaceRepeatForReach(e.Frames.Count, repeatFrom);",
     b"                repeatFrom = MountPrefixLength(e, false);\n"
     b"                repeat = SurfaceRepeatForReach(e.Frames.Count, 0);",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "so the solver was fed a different repeatfrom than the node carries"),

    # RA-371: the memo that makes a clip embedded N times encode once. Emptied on every write, so every node
    # re-encodes and the five <sound> nodes stop sharing one string.
    ("burn-tools: a shared clip is base64-encoded once per embedding again",
     PET_EMITTER,
     b"                    encodedByClip[mp3] = base64;",
     b"                    encodedByClip.Remove(mp3);",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "base64-encoded more than once"),

    # R-068: the validator's 256-sound count cap. Lifted out of reach, the 328-embedding fixture writes 328 nodes
    # and the validator refuses the document on the 257th.
    ("burn-tools: the sound loop stops budgeting the validator's count cap",
     PET_EMITTER,
     b"                if (soundNodes.Count >= DesktopAICompanion.CompanionXmlValidator.MaximumSounds) { soundNoSlot++; continue; }",
     b"                if (soundNodes.Count >= int.MaxValue) { soundNoSlot++; continue; }",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "more sounded animations than the format allows sounds was not accepted"),

    # R-069: a Group2 wall action on a left-out region belongs to the left-out note, not to Degraded. Inverted,
    # the fixture's Group2 ClimbWall is filed as kept-but-simplified a few lines above "left out".
    ("burn-tools: a left-out Group2 wall action is filed as degraded again",
     PET_EMITTER,
     b"                    bool leftOutWithRegion = (!wallRegionEmitted && IsWallAction(a))",
     b"                    bool leftOutWithRegion = (wallRegionEmitted && IsWallAction(a))",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "listed under 'Kept but simplified' although its region was"),

    # RA-374: the compositor's member set and the emitter's chain admission are one helper; a compositor that
    # withholds a member the emitter still expects trips the contract leg (and the chain is refused).
    ("burn-tools: the compositor drops a set-piece member the emitter still chains",
     PET_EMITTER,
     b"                foreach (ShimejiAction m in resolved) members.Add(m.Name);",
     b"                foreach (ShimejiAction m in resolved) if (m.Name != \"RunOff\") members.Add(m.Name);",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "does not name the gator ride's off-screen legs"),

    # ...and the shared bound is load-bearing on both sides: narrowed, the three-member gator ride is refused.
    ("burn-tools: the set-piece member bound is narrowed on the shared admission",
     PET_EMITTER,
     b"        private const int MaxSetPieceMembers = 8;",
     b"        private const int MaxSetPieceMembers = 2;",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "expected 3 set-piece chain steps for GatorRide"),

    # RA-375, both halves. A gaze stops being a leaf of the frequency walk; a collapsed mirror's frequency stops
    # reaching its survivor.
    ("burn-tools: a gaze is no longer a leaf of the frequency walk",
     PET_EMITTER,
     b"            if (IsFloorAction(a) || IsGazeAction(a)) { outSpokes.Add(name); return; }",
     b"            if (IsFloorAction(a)) { outSpokes.Add(name); return; }",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "a Group2 gaze is not a"),

    ("burn-tools: a collapsed mirror's frequency stops reaching its survivor",
     PET_EMITTER,
     b"                result[survivor] = current + lost;",
     b"                result[survivor] = current;",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "the collapsed mirror 'WalkBack' carries the"),

    # RA-382, both halves. The suite's main block now has a catch, so a throw mid-way is a named FAIL line that
    # keeps the failures already collected; and RunAll's guard reports the frame that threw on the FAIL line.
    ("burn-tools: the emitter self-test's main block throws mid-way",
     EMITTER_SELFTEST,
     b"                if (!r.Valid) failures.Add(\"emitted XML failed the validator: \" + r.Error);",
     b"                if (!r.Valid) failures.Add(\"emitted XML failed the validator: \" + r.Error);\n"
     b"                if (r.Valid) throw new InvalidOperationException(\"injected throw\");",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "main fixtures threw: System.InvalidOperationException: injected throw"),

    ("burn-tools: a suite that throws is reported with the frame that threw",
     PETGRAPH_SELFTEST,
     b"            var failures = new List<string>();\n",
     b"            var failures = new List<string>();\n"
     b"            if (failures.Count == 0) throw new InvalidOperationException(\"injected throw\");\n",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "PetGraphSelfTest threw InvalidOperationException: injected throw -- at DesktopAICompanion.Tools.ShimejiConvert.Shimeji.PetGraphSelfTest.Run"),

    # RA-383: the floor's post-condition on the 400-spoke pool and on the budgeted 63-spoke pool, each caught by
    # an early return the old `Count == 400 && Min() > 0` assertion could not see.
    ("burn-tools: the minimum-share floor quietly returns for enormous pools",
     PET_EMITTER,
     b"            if (weights == null || weights.Count == 0 || minimumPercent <= 0) return;",
     b"            if (weights == null || weights.Count == 0 || minimumPercent <= 0 || weights.Count >= 400) return;",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "an enormous spoke set still gets its floor"),

    ("burn-tools: the budgeted floor stops raising above the crossover",
     PET_EMITTER,
     b"            if (weights == null || weights.Count == 0 || minimumPercent <= 0) return;",
     b"            if (weights == null || weights.Count == 0 || minimumPercent <= 0 || weights.Count >= 60) return;",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "budgeted floor RAISES the tail"),

    # RA-384, both directions of the fallback list.
    ("burn-tools: PetGraph gives sync the fall/drag fallback",
     PETGRAPH_ENGINE,
     b'        private static readonly string[] FallbackResolvedNames = { "fall", "drag" };',
     b'        private static readonly string[] FallbackResolvedNames = { "fall", "drag", "sync" };',
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "'Sync' was rooted"),

    ("burn-tools: PetGraph drops drag's contains-the-word fallback",
     PETGRAPH_ENGINE,
     b'        private static readonly string[] FallbackResolvedNames = { "fall", "drag" };',
     b'        private static readonly string[] FallbackResolvedNames = { "fall" };',
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "'Dragging' is not a root"),

    # RA-387: the wiring, in the direction that does not probe. A gate that can never be true builds no baker
    # for the sounded skin, and the positive leg asks who was asked.
    ("burn-tools: ConvertSkin never builds a baker for a sounded skin",
     SHIMEJI_ENGINE_CS,
     b"            if (!bundled && HasSoundedPose(config))",
     b"            if (!bundled && HasSoundedPose(config) && config == null)",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "asked the transcoder for [], expected exactly hi.wav once"),

    # RA-377: the probe gate reads the emitter's view of the actions, not the document-wide pose census.
    ("burn-tools: the probe gate reads the pose census again",
     PET_EMITTER,
     b"            foreach (ShimejiAction a in config.Actions)\n"
     b"                if (a != null && FirstSoundClip(a) != null) return true;",
     b"            foreach (ShimejiPose p in config.Poses)\n"
     b"                if (p != null && !string.IsNullOrWhiteSpace(p.Sound)) return true;",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "inside a nested composite is reported as sounded"),

    # RA-379: the override variable is read, or a box whose ffmpeg is a .cmd shim converts every sounded skin silent.
    ("burn-tools: the ffmpeg override is never read",
     SHIMEJI_ENGINE_CS,
     b"                string configured = Environment.GetEnvironmentVariable(FfmpegOverrideVariable);",
     b"                string configured = null;",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "the override is not being read"),

    # RA-385, both file-name sets. The parser forgets the Japanese actions leaf; the parser forgets the singular
    # behaviours leaf. UTF-8 bytes for the Japanese name, since the harness works in bytes.
    ("burn-tools: the parser forgets the Japanese actions file name",
     SHIMEJI_PARSER,
     '        public static readonly string[] ActionsFileNames = { "actions.xml", "動作.xml", "one.xml", "1.xml" };'.encode("utf-8"),
     b'        public static readonly string[] ActionsFileNames = { "actions.xml", "one.xml", "1.xml" };',
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "ParseConfDirectory refused a conf directory holding"),

    ("burn-tools: the parser forgets the singular Behavior.xml",
     SHIMEJI_PARSER,
     '            { "behaviors.xml", "behavior.xml", "behaviours.xml", "行動.xml", "two.xml", "2.xml" };'.encode("utf-8"),
     '            { "behaviors.xml", "behaviours.xml", "行動.xml", "two.xml", "2.xml" };'.encode("utf-8"),
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "Behavior.xml (singular) beside actions.xml was not read"),

    # RA-386, both halves: one conf for the whole root again; a sprite folder called img named after the root again.
    ("burn-tools: Detect resolves one conf for the whole root again",
     SKIN_LAYOUT,
     b"                string confDir = FindConfDirFor(rootDir, imgDir);\n"
     b"                ownConf.Add(confDir);",
     b"                string confDir = null;\n"
     b"                ownConf.Add(confDir);",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "Bob's sprites were paired with"),

    ("burn-tools: a sprite folder called img is named after the root again",
     SKIN_LAYOUT,
     b"                if (fullParent != null && !string.Equals(fullParent, fullRoot, StringComparison.OrdinalIgnoreCase))\n"
     b"                    return parent.Name;",
     b"                if (fullParent != null && !string.Equals(fullParent, fullRoot, StringComparison.OrdinalIgnoreCase))\n"
     b"                    return new DirectoryInfo(root).Name;",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "expected Bob (richer, first) and Alice"),

    # RA-389: the duplicates' bitmaps are released before the compose loop, or six stay resident for four pictures.
    ("burn-tools: duplicate bitmaps stay resident through the compose loop",
     SPRITE_SHEET_BUILDER,
     b"                        if (!referenced.Contains(name)) unreferenced.Add(name);",
     b"                        if (!referenced.Contains(name) && name == null) unreferenced.Add(name);",
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "decoded bitmaps when it began"),

    # R-071: the spawn sweep's WITNESS that the expression reads `random`; a constant X sits inside the screen at
    # every draw and the sweep alone would pass it.
    ("burn-tools: a spawn expression stops reading random",
     PET_EMITTER,
     b'                        new SpawnNode { Id = 1, Probability = 50, X = "random*(screenW-imageW-50)/100+25", Y = "-imageH-20"',
     b'                        new SpawnNode { Id = 1, Probability = 50, X = "25", Y = "-imageH-20"',
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "never read `random`"),

    # RA-376: the residue's figures come from the constants; a typed literal drifts the moment the cap moves.
    ("burn-tools: the no-room residue figure is typed again",
     PET_EMITTER,
     b'                    + MebibytesOf(SpriteSheetBuilder.XmlBudgetBytes) + " pet limit (or the "',
     b'                    + "11 MiB" + " pet limit (or the "',
     SHIMEJI_CSPROJ, SHIMEJI_ENGINE_DLL,
     SHIMEJI, None, "does not state the pet limit and audio total from the validator's constants"),

    # The module TEMPLATE (RA-334, F379), through Test-ModuleTemplate.ps1: no csproj, no artefact -- the script
    # scaffolds and rebuilds from the mutated template itself. A SavePaneValues that skips Save() used to pass
    # the round-trip (the one handle reads what it wrote); a "Test it" that always reports success used to pass
    # with speech switched off.
    ("burn-tools: template: SavePaneValues skips Save()",
     TEMPLATE_MODULE,
     b"            return settings.Save();",
     b"            return true;",
     None, None,
     TEMPLATE, None, "the pane persisted (Save() was called once)"),

    ("burn-tools: template: 'Test it' reports success whatever happened",
     TEMPLATE_MODULE,
     b"            return System.Threading.Tasks.Task.FromResult(asked ? TestSpokeStatus : TestSpeechOffStatus);",
     b"            return System.Threading.Tasks.Task.FromResult(TestSpokeStatus);",
     None, None,
     TEMPLATE, None, "'Test it' reports a refusal when speech is off"),

    # ---- lane burn/remembrance ----
    # Phase 8 burn-down of the Remembrance module (RA-150..168, R-036..042, N-deadcode-01). Every case names
    # Remembrance.csproj and the module DLL the host loads; named "burn/remembrance: ..." so one
    # --only=burn/remembrance run covers the lane. Each is the defect as it shipped, put back.

    # RA-151: the purge descends into every immediate subfolder again, so Documents\Zoom\recording.wav goes.
    ("burn/remembrance: the purge descends into every subfolder again (RA-151)",
     CAPTURE_STORE,
     b"                    if (!IsCaptureFolderName(Path.GetFileName(sub))) continue;\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a foreign subfolder's recording.wav survives the purge"),

    # R-037: a capture folder the purge has just emptied stays behind for ever again.
    ("burn/remembrance: the purge leaves an emptied capture folder behind (R-037)",
     CAPTURE_STORE,
     b"                    if (aged) TryRemoveEmptyCaptureFolder(sub);\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a capture folder the purge has emptied is removed with its last file"),

    # RA-154: the keep-alive is started in a format that is not the capture's; the label claims "in its own
    # format" and used to test only Format != null.
    ("burn/remembrance: the keep-alive starts in a format that is not the capture's (RA-154)",
     AUDIO_RECORDER,
     b"                try { s.KeepAlive = KeepAliveFactory(device, capture.WaveFormat); }\n",
     b"                try { s.KeepAlive = KeepAliveFactory(device, new WaveFormat(44100, 16, 2)); }\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a loopback source gets a silent render stream in its own format"),

    # RA-150: Stop stops asking the keep-alive whether it died, and the capture thread's death is dropped.
    ("burn/remembrance: Stop stops reading the keep-alive's failure (RA-150)",
     AUDIO_RECORDER,
     b"                try { if (s.KeepAlive != null && s.KeepAlive.Failure != null) KeepAliveEndedEarly = s.KeepAlive.Failure; }\n"
     b"                catch { }\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a silent render stream that stopped mid-capture is reported at Stop"),

    ("burn/remembrance: the capture's RecordingStopped drops its exception again (RA-150)",
     AUDIO_RECORDER,
     b"                try\n"
     b"                {\n"
     b"                    if (e != null && e.Exception != null)\n"
     b"                        CaptureFailure = (s.Loopback ? \"the system output capture\" : \"the microphone capture\")\n"
     b"                                         + \" stopped with an error: \" + e.Exception.Message;\n"
     b"                }\n"
     b"                catch { }\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a capture whose thread died is reported at Stop"),

    # R-038: the mix filters scratch tracks by file size again, so a header-only pair mixes to an empty file.
    ("burn/remembrance: the mix filters scratch tracks by file size again (R-038)",
     AUDIO_RECORDER,
     b"            var live = inputs.Where(HasAudio).ToList();\n"
     b"            foreach (string p in inputs) { if (!live.Contains(p)) DeleteIfEmptyRecording(p); }\n",
     b"            var live = inputs.Where(p => { try { return new FileInfo(p).Length > 44; } catch { return false; } }).ToList();\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a stop before any packet arrived returns null rather than an empty recording.wav"),

    # R-038: the module says "audio saved as" over a stop that captured nothing.
    ("burn/remembrance: a stop that captured nothing says 'audio saved' again (R-038)",
     REMEMBRANCE_MODULE,
     b"                    saved.TrySetResult(wav != null);\n"
     b"                    LogCaptureTroubles(recorder);\n"
     b"                    if (wav == null)\n",
     b"                    saved.TrySetResult(wav != null);\n"
     b"                    LogCaptureTroubles(recorder);\n"
     b"                    if (wav == null && wav != null)\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a stop that captured nothing says so in the log"),

    # R-038: the save's outcome is set true whatever happened, so a faulted stop reads as "finished".
    ("burn/remembrance: a faulted save reads as finished again (R-038)",
     REMEMBRANCE_MODULE,
     b"                        saved.TrySetException(ex);\n",
     b"                        saved.TrySetResult(ex != null);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a save that faults while shutdown waits for it is reported as failed, never as finished"),

    # R-038: only the latest save is tracked, so a stop-restart-stop leaves the first save unwaited.
    ("burn/remembrance: only the latest save is tracked again (R-038)",
     REMEMBRANCE_MODULE,
     b"                _pendingSaves.RemoveAll(t => t.IsCompleted);\n"
     b"                _pendingSaves.Add(save);\n",
     b"                _pendingSaves.Clear();\n"
     b"                _pendingSaves.Add(save);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a second stop inside the first save's window is tracked BESIDE the first"),

    # RA-155: a flush that timed out waits the whole bound again on the next hook. The flag is set FALSE
    # rather than the line deleted: with no writer at all the field is CS0414 (assigned but never used) and
    # the module's warnings-as-errors turns the case into BROKEN, which proves nothing about the assertion.
    ("burn/remembrance: a timed-out flush waits again on the next hook (RA-155)",
     REMEMBRANCE_MODULE,
     b"                lock (_pendingSaves) { _flushGaveUp = true; }\n",
     b"                lock (_pendingSaves) { _flushGaveUp = false; }\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the second hook on the same exit does not wait the bound a second time"),

    # RA-156: two presses inside one second overwrite each other again, at each of the three places that
    # keep them apart: the unique-path helper, TakeSnapshot's call to it, and the purge parsers' suffix.
    ("burn/remembrance: a second snapshot in the same second overwrites the first (RA-156)",
     CAPTURE_STORE,
     b"            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;\n",
     b"            if (path != null) return path;\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a second snapshot in the same second lands beside the first as ' (2)'"),

    ("burn/remembrance: TakeSnapshot stops asking for a unique path (RA-156)",
     REMEMBRANCE_MODULE,
     b"                string png = CaptureStore.UniqueSnapshotPath(System.IO.Path.Combine(dir, prefix + \" \" + stamp + \".png\"));\n",
     b"                string png = System.IO.Path.Combine(dir, prefix + \" \" + stamp + \".png\");\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "two presses inside the same second write two files"),

    ("burn/remembrance: the purge parsers forget the collision suffix (RA-156)",
     CAPTURE_STORE,
     b"            if (!TryStripSuffix(lowerFileName, \".png\", out stem)) return false;\n"
     b"            stem = StripCollisionSuffix(stem);\n"
     b"            if (!TryStripTrailingStamp(stem, out head)) return false;\n"
     b"            return string.Equals(head, \"snap \", StringComparison.Ordinal);\n",
     b"            if (!TryStripSuffix(lowerFileName, \".png\", out stem)) return false;\n"
     b"            if (!TryStripTrailingStamp(stem, out head)) return false;\n"
     b"            return string.Equals(head, \"snap \", StringComparison.Ordinal);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a collision-suffixed root snapshot IS ours to purge"),

    # RA-157: a failed settings write is answered with a tick again.
    ("burn/remembrance: a failed settings write is answered with a tick again (RA-157)",
     REMEMBRANCE_MODULE,
     b"            if (ok) return true;\n"
     b"            Log(what + \" could not be written to the settings file; it applies to this session only\");\n",
     b"            if (ok || !ok) return true;\n"
     b"            Log(what + \" could not be written to the settings file; it applies to this session only\");\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a Whisper adoption whose settings write fails is not answered with a tick"),

    # RA-158: Set up adopts any detected pair, whatever the dropdown asks for.
    ("burn/remembrance: Set up adopts any detected pair whatever the dropdown says (RA-158)",
     WHISPER_INSTALLER,
     b"            return string.Equals(found, chosen, StringComparison.OrdinalIgnoreCase)\n"
     b"                ? SetupStep.AdoptDetected\n"
     b"                : SetupStep.FetchChosenModel;\n",
     b"            return found.Length >= 0 && chosen.Length >= 0 ? SetupStep.AdoptDetected : SetupStep.FetchChosenModel;\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a detected pair whose model is not the one chosen fetches the chosen model"),

    # RA-159 / RA-160: the pull loses its single-flight gate, and runs on CancellationToken.None again.
    ("burn/remembrance: a second press starts a second pull (RA-159)",
     REMEMBRANCE_MODULE,
     b"            if (Interlocked.CompareExchange(ref _pullInFlight, 1, 0) != 0)\n"
     b"                return \"\xe2\x9a\xa0 a model download is already running; reopen this pane to watch the Status line.\";\n",
     b"            Interlocked.Exchange(ref _pullInFlight, 1);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a second press while a pull is running starts no second pull"),

    # Re-pointed 2026-10-02 by lane feature/remembrance-2: the progress callback also records the latest line for
    # the download's "still running" answer (BUG-013), so the call's bytes moved. Same regression, same assertion.
    ("burn/remembrance: the pull runs on CancellationToken.None again (RA-160)",
     REMEMBRANCE_MODULE,
     b"                        endpoint, id, p => { _lastStatus = p; _lastPullProgress = p; }, token).ConfigureAwait(false);\n",
     b"                        endpoint, id, p => { _lastStatus = p; _lastPullProgress = p; }, CancellationToken.None).ConfigureAwait(false);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the pull runs under the module's own token, not CancellationToken.None"),

    # RA-153: the pull loses its idle bound, then its header bound.
    ("burn/remembrance: the Ollama pull loses its idle bound (RA-153)",
     OLLAMA_SUMMARIZER,
     b"                                idle.CancelAfter(PullIdleBound);\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a pull that falls silent mid-download gives up at the idle bound"),

    ("burn/remembrance: the Ollama pull loses its header bound (RA-153)",
     OLLAMA_SUMMARIZER,
     b"                    idle.CancelAfter(PullHeaderBound);\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a pull Ollama never answers gives up at the header bound"),

    # RA-152: the display fallback indexes a table row again instead of looking the default up.
    ("burn/remembrance: the display fallback indexes a row again (RA-152)",
     OLLAMA_SUMMARIZER,
     b"            foreach (string[] row in Recommended)\n"
     b"                if (string.Equals(row[0], DefaultRecommendedId, StringComparison.Ordinal)) return row[1];\n"
     b"            return Recommended[0][1];\n",
     b"            return Recommended[0][1];\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the display fallback is the default recommendation"),

    # RA-167: detection adopts an install whose run check failed.
    ("burn/remembrance: detection adopts an install that failed its check (RA-167)",
     WHISPER_INSTALLER,
     b"                if (IsMarkedUnverified(root)) continue;\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "an install that failed its run check is passed over by detection"),

    # RA-168: a cancelled release lookup falls into the general catch and reads as a GitHub failure.
    ("burn/remembrance: a cancelled release lookup reads as a GitHub failure again (RA-168)",
     WHISPER_INSTALLER,
     b"            catch (OperationCanceledException)\n"
     b"            {\n"
     b"                // The CALLER's cancellation -- Shutdown mid-lookup -- is the caller's to report: InstallAsync\n"
     b"                // says \"Setup was cancelled.\" Until 2026-09-30 it fell through to the catch below and the\n"
     b"                // diagnostic log read \"whisper setup failed: Could not reach GitHub: A task was canceled.\",\n"
     b"                // an app exit dressed up as a network fault (RA-168).\n"
     b"                throw;\n"
     b"            }\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the caller's own cancellation surfaces as a cancellation"),

    # R-042: DownloadAsync's two bounds, each removed on its own.
    ("burn/remembrance: DownloadAsync loses its idle bound (R-042)",
     WHISPER_INSTALLER,
     b"                                    idle.CancelAfter(ReadIdleBound);\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a download that falls silent after its first chunk gives up at the idle bound"),

    ("burn/remembrance: DownloadAsync loses its header bound (R-042)",
     WHISPER_INSTALLER,
     b"                        idle.CancelAfter(LookupBound);\n"
     b"                        using (HttpResponseMessage response = await http\n",
     b"                        using (HttpResponseMessage response = await http\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a download whose headers never arrive gives up at the header bound"),

    # RA-163: an out-of-range registry value is returned as a policy state.
    ("burn/remembrance: an out-of-range Network Protection value reads as a state (RA-163)",
     WHISPER_INSTALLER,
     b"            return n >= 0 && n <= 2 ? (int?)(int)n : null;\n",
     b"            return (int?)(int)n;\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a value outside 0..2 reads as unknown"),

    # RA-166: the transcript header prints the clock at transcription time again.
    ("burn/remembrance: the transcript header prints the clock again (RA-166)",
     TRANSCRIBER,
     b"            sb.AppendLine(RecordedLine(recordedAt));\n",
     b"            sb.AppendLine(\"Recorded: \" + DateTime.Now.ToString(\"f\"));\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the transcript header's 'Recorded:' is the capture's start"),

    # R-041: the kill message prints the zero sentinel as a length again.
    ("burn/remembrance: the kill message prints the zero sentinel as a length again (R-041)",
     TRANSCRIBER,
     b"            if (audioLength <= TimeSpan.Zero)\n"
     b"            {\n"
     b"                rule = \"The recording's length could not be read from its WAV header, so the default \"\n"
     b"                       + Minutes(MinimumWhisperTimeout) + \"-minute limit applied.\";\n"
     b"            }\n"
     b"            else\n"
     b"            {\n",
     b"            {\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "an unreadable WAV length is reported as unreadable"),

    # ---- lane feature/remembrance-2 ----
    # The 2026-10-02 feature batch for Remembrance 2.0.0. Every case names Remembrance.csproj and the module DLL the
    # host loads; named "feature/remembrance-2: ..." so one --only=feature/remembrance-2 run covers the lane. Each
    # is the shipped shape put back, or the new guard removed.

    # BUG-013 (a): the actions read the SAVED settings again, one read at a time, and the on-screen copy loses the
    # saved values beneath the pane's.
    ("feature/remembrance-2: Download that model pulls the saved model again (BUG-013)",
     REMEMBRANCE_MODULE,
     b"            string id = OllamaSummarizer.RecommendedIdFromDisplay(\n"
     b"                shown.Get(\"recommendedModel\", OllamaSummarizer.DefaultRecommendedId));\n",
     b"            string id = OllamaSummarizer.RecommendedIdFromDisplay(\n"
     b"                _settings.Get(\"recommendedModel\", OllamaSummarizer.DefaultRecommendedId));\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "Download that model pulls the model ON SCREEN, not the saved one"),

    ("feature/remembrance-2: Download that model asks the saved address again (BUG-013)",
     REMEMBRANCE_MODULE,
     b"            string endpoint = shown.Get(\"ollamaEndpoint\", OllamaSummarizer.DefaultEndpoint);\n"
     b"            string id = OllamaSummarizer.RecommendedIdFromDisplay(\n",
     b"            string endpoint = _settings.Get(\"ollamaEndpoint\", OllamaSummarizer.DefaultEndpoint);\n"
     b"            string id = OllamaSummarizer.RecommendedIdFromDisplay(\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "...and asks the address ON SCREEN whether Ollama is there"),

    ("feature/remembrance-2: the on-screen copy drops the saved values under the pane's (BUG-013)",
     REMEMBRANCE_MODULE,
     b"                if (saved != null) copy.Set(id, saved);\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "WITNESS with the model and the address absent from what the pane handed over, the saved ones are used"),

    ("feature/remembrance-2: Open the download pages opens the saved model again (BUG-013)",
     REMEMBRANCE_MODULE,
     b"            string modelId = WhisperInstaller.ResolveModelId(\n"
     b"                OnScreenSettings(pending).Get(\"whisperModelChoice\", WhisperInstaller.DefaultModelId));\n"
     b"            string modelUrl = WhisperInstaller.ModelUrl(modelId);\n",
     b"            string modelId = WhisperInstaller.ResolveModelId(\n"
     b"                _settings.Get(\"whisperModelChoice\", WhisperInstaller.DefaultModelId));\n"
     b"            string modelUrl = WhisperInstaller.ModelUrl(modelId);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "Open the download pages opens the model ON SCREEN, not the saved one"),

    ("feature/remembrance-2: the download saves the address on screen as a side effect (BUG-013)",
     REMEMBRANCE_MODULE,
     b"            string endpoint = shown.Get(\"ollamaEndpoint\", OllamaSummarizer.DefaultEndpoint);\n"
     b"            string id = OllamaSummarizer.RecommendedIdFromDisplay(\n",
     b"            string endpoint = shown.Get(\"ollamaEndpoint\", OllamaSummarizer.DefaultEndpoint);\n"
     b"            _settings.Set(\"ollamaEndpoint\", endpoint);\n"
     b"            string id = OllamaSummarizer.RecommendedIdFromDisplay(\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the press saves no unrelated edit on screen"),

    # The floor InvokeWithPendingAsync needs: an older host fails at the property's setter inside Init.
    ("feature/remembrance-2: the module's host floor drops below InvokeWithPendingAsync (BUG-013)",
     REMEMBRANCE_MODULE,
     b"            MinHostVersion = \"1.2.5\",\n",
     b"            MinHostVersion = \"1.0.0\",\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the module asks for host 1.2.5 or newer"),

    # BUG-013 (b): the download answers at once again, or never at its bound, or a refusal reads as a success,
    # or nothing answering no longer stops it.
    ("feature/remembrance-2: Download that model answers before the pull ends again (BUG-013)",
     REMEMBRANCE_MODULE,
     b"            if (answer == null) return started;   // the single-flight gate refused: one is already running\n",
     b"            if (answer == null || answer != null) return started;   // the single-flight gate refused: one is already running\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a pull that ends inside the bound is answered in the pane"),

    ("feature/remembrance-2: a pull still running holds the button with no bound (BUG-013)",
     REMEMBRANCE_MODULE,
     b"                Task bound = Task.Delay(PullAnswerBound, stopWaiting.Token);\n",
     b"                Task bound = Task.Delay(Timeout.Infinite, stopWaiting.Token);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a pull still running at the bound is answered with its latest progress"),

    ("feature/remembrance-2: a refused pull is answered with a tick (BUG-013)",
     REMEMBRANCE_MODULE,
     b"                        outcome.TrySetResult(\"\xe2\x9c\x97 \" + pull.Message);\n",
     b"                        outcome.TrySetResult(\"\xe2\x9c\x93 \" + pull.Message);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a pull Ollama refuses is answered with its reason"),

    ("feature/remembrance-2: nothing answering no longer stops the download (BUG-013)",
     REMEMBRANCE_MODULE,
     b"            bool reachable = await IsReachable(endpoint, CancellationToken.None).ConfigureAwait(true);\n"
     b"            if (!reachable)\n",
     b"            bool reachable = await IsReachable(endpoint, CancellationToken.None).ConfigureAwait(true);\n"
     b"            if (!reachable && endpoint == null)\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "nothing answering at the address on screen is answered before any pull starts"),

    # BUG-013 (c): Apply writes the pull's selection over again, the pull's write goes unrecorded, the record beats
    # the user's own pick, or every untouched field is kept (the diff-every-field rule addendum 1 refused, which
    # leaves the derived summary model unsaved).
    ("feature/remembrance-2: Apply writes over a background write again (BUG-013)",
     REMEMBRANCE_MODULE,
     b"                    ApplyPaneValues(_settings, values, BackgroundWritesToKeep(values));\n",
     b"                    ApplyPaneValues(_settings, values, null);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a model the pull selected while the pane was open survives an Apply"),

    ("feature/remembrance-2: the pull's selection is written without being recorded (BUG-013)",
     REMEMBRANCE_MODULE,
     b"                        SetInBackground(\"summaryModel\", id);\n",
     b"                        _settings.Set(\"summaryModel\", id);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a model the pull selected while the pane was open survives an Apply"),

    ("feature/remembrance-2: Apply keeps a background write over the user's pick (BUG-013)",
     REMEMBRANCE_MODULE,
     b"                    if (string.Equals(StoredFormOf(id, onScreen), StoredFormOf(id, shown), StringComparison.Ordinal))\n"
     b"                        keep.Add(id);\n",
     b"                    keep.Add(id);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "WITNESS a model the user picked on screen wins over the pull's background selection"),

    ("feature/remembrance-2: Apply keeps every untouched field, the refused diff rule (BUG-013)",
     REMEMBRANCE_MODULE,
     b"                foreach (string id in _writtenSinceLoad.Keys)\n",
     b"                foreach (string id in values.Keys)\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "WITNESS an untouched Apply still persists the derived preselection"),

    # BUG-013 (d): the start, then the success, drop out of the log.
    ("feature/remembrance-2: the pull's start is not logged (BUG-013)",
     REMEMBRANCE_MODULE,
     b"            Log(\"model pull started: \" + id + \" from \" + OllamaSummarizer.NormalizeEndpoint(endpoint));\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the pull's start and its success are logged"),

    ("feature/remembrance-2: the pull's success is not logged (BUG-013)",
     REMEMBRANCE_MODULE,
     b"                    Log(\"model pull finished: \" + id + \" is installed at \" + OllamaSummarizer.NormalizeEndpoint(endpoint));\n",
     b"",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the pull's start and its success are logged"),

    # Summary card (item 2): the old label comes back, the Validate reads the saved address, skips a step, drops the
    # untagged-name rule, ticks a failed answer, loses its timing, runs on the caller's thread or twice at once; the
    # Refresh lists the saved address or picks over the user; the button the answer names is the old one.
    ("feature/remembrance-2: the Summary card's refresh is Find local summary models again",
     REMEMBRANCE_MODULE,
     b"                    new PaneAction { Label = \"Refresh local models\", Group = \"Summary (local AI)\", ReloadPaneAfter = true,\n",
     b"                    new PaneAction { Label = \"Find local summary models\", Group = \"Summary (local AI)\", ReloadPaneAfter = true,\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "WITNESS the Summary card offers Refresh local models and Validate"),

    ("feature/remembrance-2: the Summary Validate asks the saved address",
     REMEMBRANCE_MODULE,
     b"            string address = shown.Get(\"ollamaEndpoint\", OllamaSummarizer.DefaultEndpoint);\n",
     b"            string address = _settings.Get(\"ollamaEndpoint\", OllamaSummarizer.DefaultEndpoint);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "Validate asks the address and the model ON SCREEN"),

    ("feature/remembrance-2: the Summary Validate skips the reachability step",
     REMEMBRANCE_MODULE,
     b"            if (!answering)\n",
     b"            if (!answering && endpoint == null)\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "nothing answering is named as the step that failed"),

    ("feature/remembrance-2: the Summary Validate skips the installed check",
     REMEMBRANCE_MODULE,
     b"            if (!IsInstalled(installed, model))\n",
     b"            if (installed == null)\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a model Ollama does not have is named as not installed"),

    ("feature/remembrance-2: an untagged model no longer matches its :latest",
     REMEMBRANCE_MODULE,
     b"            string tagged = wanted.IndexOf(':') < 0 ? wanted + \":latest\" : null;\n",
     b"            string tagged = null;\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a model named without a tag is installed when Ollama lists it as name:latest"),

    ("feature/remembrance-2: a model that does not answer the test reads as working",
     REMEMBRANCE_MODULE,
     b"            if (answer == null || !answer.Ok)\n",
     b"            if (answer == null)\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a model that does not answer the test is named as the step that failed"),

    ("feature/remembrance-2: the Summary Validate's success stops naming its steps and its time",
     REMEMBRANCE_MODULE,
     b"            return \"\xe2\x9c\x93 Ollama answers, \" + model + \" is installed, and it answered in \" +\n",
     b"            return \"\xe2\x9c\x93 \" + model + \" answered in \" +\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a summary set-up that works is answered with all three steps and the time the answer took"),

    ("feature/remembrance-2: the Summary Validate runs on the caller's thread",
     REMEMBRANCE_MODULE,
     b"            return Task.Run(() => CheckSummaryOnceAsync(address, model));\n",
     b"            return CheckSummaryOnceAsync(address, model);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "the test answer is asked for off the calling thread"),

    ("feature/remembrance-2: a second Summary Validate starts a second check",
     REMEMBRANCE_MODULE,
     b"            if (Interlocked.CompareExchange(ref _summaryCheckInFlight, 1, 0) != 0)\n"
     b"                return Task.FromResult(\"\xe2\x9a\xa0 the summary set-up is already being checked; wait for that answer.\");\n",
     b"            Interlocked.Exchange(ref _summaryCheckInFlight, 1);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "a second Validate while one is running starts no second check"),

    ("feature/remembrance-2: Refresh local models lists the saved address",
     REMEMBRANCE_MODULE,
     b"            string endpoint = shown.Get(\"ollamaEndpoint\", OllamaSummarizer.DefaultEndpoint);\n"
     b"            try\n",
     b"            string endpoint = _settings.Get(\"ollamaEndpoint\", OllamaSummarizer.DefaultEndpoint);\n"
     b"            try\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "Refresh local models lists the models at the address ON SCREEN"),

    ("feature/remembrance-2: Refresh local models picks a first model over the user's unapplied pick",
     REMEMBRANCE_MODULE,
     b"                if (string.IsNullOrWhiteSpace(shown.Get(\"summaryModel\", \"\"))) _settings.Set(\"summaryModel\", models[0]);\n",
     b"                if (string.IsNullOrWhiteSpace(_settings.Get(\"summaryModel\", \"\"))) _settings.Set(\"summaryModel\", models[0]);\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "...and leaves a model picked on screen to Apply"),

    ("feature/remembrance-2: Summarize a transcript names the button that is gone",
     REMEMBRANCE_MODULE,
     b"            if (string.IsNullOrWhiteSpace(model)) return \"\xe2\x9c\x97 Pick a summary model first (\\\"Refresh local models\\\").\";\n",
     b"            if (string.IsNullOrWhiteSpace(model)) return \"\xe2\x9c\x97 Pick a summary model first (\\\"Find local summary models\\\").\";\n",
     REMEMBRANCE_CSPROJ, REMEMBRANCE_DLL,
     "--module-selftest=remembrance", "dp-module-remembrance-selftest.txt",
     "Summarize a transcript names the button that exists"),
    # ---- lane burn/scripts-pack ----
    # (no self-test guard cases: the lane's checks live in packaging suites, run-time script guards and
    # source invariants; see mutate-hardening-guards.py under the same anchor)
    # ---- lane burn/aibrain ----
    # The Phase 8 burn-down of the aibrain lane (RA-054 to RA-087, R-012, R-013, R-016 to R-019, R-021, R-023).
    # Every case runs --module-selftest=aibrain, whose module instances aim their local slot at 127.0.0.1:9 and
    # whose backends are doubles; nothing here loads a model or contacts a provider. Names carry the
    # "burn-aibrain:" prefix so `--only=burn-aibrain` runs the lane.

    # RA-063: the audition takes the overload that defaults allowSubstitution to true again, so a cloud primary's
    # audition bills five requests to whatever the provider lists first.
    ("burn-aibrain: the audition substitutes on a cloud primary again",
     AIBRAIN_ENGINE,
     b"                SubstituteMissingModel, BackendHostDescription);",
     b"                true, BackendHostDescription);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the audition never substitutes on a cloud primary"),

    # RA-065: the release names the configured ids only, so the substitute a turn sent stays resident.
    ("burn-aibrain: the release forgets the substitute it sent again",
     AIBRAIN_ENGINE,
     b"            foreach (string model in new[] { _textModel, _visionModel, _sentTextModel, _sentVisionModel })",
     b"            foreach (string model in new[] { _textModel, _visionModel })",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the release names the substitute a turn sent"),

    # RA-058: a typed key that could not be stored no longer refuses the save. (Re-pointed 2026-10-01 with
    # N-burn-aibrain-02: the decision line is now `if (!storable)`, after the copy has judged the key.)
    ("burn-aibrain: a typed key that is not stored still reports a saved pane",
     AIBRAIN_MODULE,
     b"            if (!storable)",
     b"            if (!storable && keyError == null)",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a key typed with no provider selected is not stored"),

    # N-burn-aibrain-02: the live instance is written before the copy has judged the key again.
    ("burn-aibrain: a refused save writes the pane's other values onto the live settings again",
     AIBRAIN_MODULE,
     b"            bool storable = PendingSettings(s, values, out keyError) != null && ApplyPaneValues(s, values, out keyError);",
     b"            bool storable = ApplyPaneValues(s, values, out keyError) && PendingSettings(s, values, out keyError) != null;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a save refused for its key leaves the live settings as they were"),

    # RA-055: the pending-aware press auditions the saved settings again.
    ("burn-aibrain: the audition ignores the pending disposition again",
     AIBRAIN_MODULE,
     b"            AiSettings s = pending == null ? saved : PendingSettings(saved, pending, out pendingError);",
     b"            AiSettings s = pending == null || pending != null ? saved : PendingSettings(saved, pending, out pendingError);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the audition reads the pending disposition"),

    # RA-056: the audition-end eviction runs under the chat deadline again (unbounded against a hung unload).
    ("burn-aibrain: the audition-end unload is unbounded again",
     AIBRAIN_MODULE,
     b"                        await UnloadWithinAsync(brain, AuditionUnloadBudget, run.Token).ConfigureAwait(false);",
     b"                        await brain.UnloadAsync(run.Token).ConfigureAwait(false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the audition-end unload is bounded"),

    # RA-057, both cuts: a UTF-16 index again.
    ("burn-aibrain: the audition pane splits a surrogate pair again",
     AIBRAIN_MODULE,
     b'            return one.Length > maximum ? UnicodeTextProgress.TruncateAtCodePointBoundary(one, maximum) + "\xe2\x80\xa6" : one;',
     b'            return one.Length > maximum ? one.Substring(0, maximum) + "\xe2\x80\xa6" : one;',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the audition pane cuts a sample at a code point boundary"),
    ("burn-aibrain: the already-said clause splits a surrogate pair again",
     AIBRAIN_ENGINE,
     b'                if (said.Length > PerRemark) said = UnicodeTextProgress.TruncateAtCodePointBoundary(said, PerRemark) + "\xe2\x80\xa6";',
     b'                if (said.Length > PerRemark) said = said.Substring(0, PerRemark) + "\xe2\x80\xa6";',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the already-said clause quotes a remark cut at a code point boundary"),

    # RA-059: the cloud listing drops the failure it folds into an empty list again.
    ("burn-aibrain: an answered 401 on a model refresh reads as no models again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OpenAiCompatBackend.cs"),
     b"            catch (Exception ex) { LastListingFailure = ex; }",
     b"            catch (Exception ex) { LastListingFailure = ex == null ? ex : null; }",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a model refresh names an answered refusal"),

    # RA-060: two of the explicit path's refusals go quiet again.
    ("burn-aibrain: the tray ask declines with no companion silently again",
     AIBRAIN_MODULE,
     b'            if (pet == null || !host.IsCompanionAlive(pet)) return Declined(host, explicitPath, "no companion");',
     b'            if (pet == null || !host.IsCompanionAlive(pet)) return Declined(host, false, "no companion");',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an explicit ask with no companion says so in the log"),
    ("burn-aibrain: the tray ask declines with speech off silently again",
     AIBRAIN_MODULE,
     b'            if (!host.SpeechEnabled) return Declined(host, explicitPath, "speech off");',
     b'            if (!host.SpeechEnabled) return Declined(host, false, "speech off");',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an explicit ask with speech off says so in the log"),

    # R-013: the tray row reads the stored switch again, and the status row says On whatever CanUse said.
    ("burn-aibrain: the inert tray row is offered again",
     AIBRAIN_MODULE,
     b"                    Visible = delegate { return _session.Enabled; },",
     b"                    Visible = delegate { return _settings != null && _settings.AiBrainEnabled; },",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the tray row is hidden while the brain is not started"),
    ("burn-aibrain: the status row says on while the brain is not started",
     AIBRAIN_MODULE,
     b'            if (!CanUse(s, out why)) return "Not started: " + why;',
     b'            if (!CanUse(s, out why) && why == null) return "Not started: " + why;',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the pane's status names the reason the brain is not started"),

    # RA-062, both halves: the composite lists a primary its probe saw down, and the brain stops re-listing an
    # unknown inventory.
    ("burn-aibrain: the composite enumerates a primary its probe saw down again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "FallbackBackend.cs"),
     b"            if (lister == null || _primaryProbe == PrimaryDown)",
     b"            if (lister == null)",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the composite cannot enumerate while its primary is down"),
    ("burn-aibrain: an unknown inventory is never re-listed again",
     AIBRAIN_ENGINE,
     b"            if (up && (!wasUp || _available == null)) await RefreshInventoryAsync(ct).ConfigureAwait(false);",
     b"            if (up && !wasUp) await RefreshInventoryAsync(ct).ConfigureAwait(false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an unknown inventory is re-listed on the next check"),

    # RA-067: the generation compare is made vacuous, so a Forget that lands mid-walk is cached over.
    ("burn-aibrain: a forget during a resolution walk is lost again",
     AIBRAIN_ENGINE,
     b"            if (Volatile.Read(ref _tesseractGeneration) == generation)",
     b"            if (Volatile.Read(ref _tesseractGeneration) >= generation)",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a path chosen while a resolution is walking is not lost"),

    # RA-068: the production transport follows redirects again; every earlier probe injected its own handler.
    ("burn-aibrain: the production handler follows redirects again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiEndpointPolicy.cs"),
     b"                AllowAutoRedirect = false,",
     b"                AllowAutoRedirect = true,",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the production handler follows no redirect"),

    # RA-072: the shared seed loses its loopback pin, so every module instance's local slot resolves to the
    # machine's Ollama. No instance in this probe makes a request either way; the pin is asserted so a future
    # check that does cannot reach a live server by accident.
    ("burn-aibrain: the module probe's seed loses the loopback pin",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiEngineProbe.Lifecycle.cs"),
     b'\\"Endpoint\\": \\"http://127.0.0.1:9\\"',
     b'\\"Endpoint\\": \\"http://localhost:11434\\"',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the module instance's local slot is pinned to a loopback port"),

    # RA-073, the three call sites: the audition brain built with the live residency's keep_alive, prepared with
    # a warm-up, and left resident after the run.
    ("burn-aibrain: the audition brain is built with the live keep_alive again",
     AIBRAIN_MODULE,
     b"                int? keepAlive = AuditionKeepAliveSeconds(s);",
     b"                int? keepAlive = s.KeepAliveForRequests;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the audition builds its brain with the audition window"),
    ("burn-aibrain: the audition's preparation warms the model again (call site)",
     AIBRAIN_MODULE,
     b"                    if (!await brain.PrepareAsync(run.Token, false).ConfigureAwait(false))",
     b"                    if (!await brain.PrepareAsync(run.Token).ConfigureAwait(false))",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "under 'keep' the audition still prepares without a warm-up"),
    ("burn-aibrain: the audition keeps its model after the run again",
     AIBRAIN_MODULE,
     b"                    if (string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase))\n"
     b"                        await UnloadWithinAsync(brain, AuditionUnloadBudget, run.Token).ConfigureAwait(false);",
     b"                    if (s == null && string.Equals(s.ModelResidency, AiSettings.ResidencyUnload, StringComparison.OrdinalIgnoreCase))\n"
     b"                        await UnloadWithinAsync(brain, AuditionUnloadBudget, run.Token).ConfigureAwait(false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the audition evicts when the run ends under 'unload' (call site)"),

    # RA-074: one check throws. With the per-check guard the report carries a FAIL naming THAT check and the
    # group's later checks still run; with the group guard alone the line named the group.
    ("burn-aibrain: one throwing check hides its group again",
     AIENGINE_SECURITY,
     b"        private static bool CheckAiHttpStatusPolicy(StringBuilder sb)\n"
     b"        {\n"
     b"            bool ok = true;\n",
     b"        private static bool CheckAiHttpStatusPolicy(StringBuilder sb)\n"
     b"        {\n"
     b"            bool ok = true;\n"
     b"            if (ok) throw new InvalidOperationException(\"mutation: the check throws\");\n",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "CheckAiHttpStatusPolicy threw"),

    # RA-079: a brain superseded during its build is retired with the eviction whatever comes next.
    ("burn-aibrain: a superseded fresh brain evicts the kept model again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSessionManager.cs"),
     b"                        await RetireBrainAsync(_brain, releaseModelOverride ?? !keptForNext).ConfigureAwait(false);",
     b"                        await RetireBrainAsync(_brain, releaseModelOverride ?? (keptForNext || !keptForNext)).ConfigureAwait(false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a brain superseded during its build by the same fingerprint under 'keep' is retired without eviction"),

    # R-012: the fingerprint is recorded when the Apply is ISSUED again (the module's old shape, inside the
    # session), so a queued Apply that never builds leaves it behind for the next one to match.
    ("burn-aibrain: the fingerprint is recorded when the Apply is issued again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiSessionManager.cs"),
     b"                _factoryFingerprint = backendFingerprint;\n",
     b"                _factoryFingerprint = backendFingerprint;\n"
     b"                _liveFingerprint = backendFingerprint;\n",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "an Apply cancelled while queued never becomes the fingerprint"),

    # RA-080, both writes: the default write's result discarded again, and the normalization rewrite's.
    ("burn-aibrain: a failed default write is silent again",
     AISETTINGS,
     b"            bool defaultsWritten = defaults.SaveCore();",
     b"            bool defaultsWritten = defaults.SaveCore() || true;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a default file that could not be written is said"),
    ("burn-aibrain: a failed normalization rewrite is silent again",
     AISETTINGS,
     b"                if (changed && result == ReadResult.Loaded && !loaded.SaveCore())",
     b"                if (changed && result == ReadResult.Loaded && !loaded.SaveCore() && loaded == null)",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a normalization the store could not write back is said"),

    # RA-081: the backup's fate drops out of the recovery warning again.
    ("burn-aibrain: the recovery warning drops the backup's fate again",
     AISETTINGS,
     b"            string backup = backupFailure == null",
     b"            string backup = backupFailure == null || backupFailure != null",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the reset-to-defaults warning says there was no backup"),

    # RA-082: the save over a missing file seeds an empty target again.
    ("burn-aibrain: a save over a missing file drops the unknown keys again",
     AISETTINGS,
     b"                target = (JsonObject)current.DeepClone();",
     b"                target = new JsonObject();",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a save over a missing file keeps the unknown keys"),

    # RA-083: the residency token is not trimmed again (the lowercase and known-value clamps then read the
    # padded token as unknown and fall back to unload).
    ("burn-aibrain: a padded residency token survives normalization again",
     AISETTINGS,
     b"            changed |= NormalizeString(ref ModelResidency, ResidencyUnload, 16);",
     b"            changed |= false;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a residency token with a stray space or capital normalizes to its value"),

    # RA-085: the active-slot snapshot is saveable again.
    ("burn-aibrain: the active-slot snapshot is saveable again",
     AISETTINGS,
     b"            AiSettings clone = (AiSettings)MemberwiseClone();\n"
     b"            clone._detachedCopy = true;\n"
     b"            if (!string.IsNullOrEmpty(Provider))",
     b"            AiSettings clone = (AiSettings)MemberwiseClone();\n"
     b"            clone._detachedCopy = _detachedCopy;\n"
     b"            if (!string.IsNullOrEmpty(Provider))",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the active-slot snapshot can never write the settings file"),

    # R-018, both halves: a sharing violation is corruption again, and a copy whose delete failed answers null.
    ("burn-aibrain: a held settings file is corruption again",
     AISETTINGS,
     b"            catch (IOException ex) when (!(ex is FileNotFoundException) &&",
     b"            catch (IOException ex) when (ex is FileNotFoundException &&",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a settings file held open by another process is not corruption"),
    ("burn-aibrain: a kept copy whose primary could not be removed reads as overwritten again",
     AISETTINGS,
     b"                primaryRemoved = true;\n"
     b"            }\n"
     b"            catch { }\n"
     b"            return recovery;",
     b"                primaryRemoved = true;\n"
     b"            }\n"
     b"            catch { return null; }\n"
     b"            return recovery;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "a rejected primary that could not be removed is still kept"),

    # R-016: the classification the probe asserts is the one production runs; a 429 classed deterministic in
    # EnsureSuccessAsync now fails the probe by name.
    ("burn-aibrain: the production classifier files 429 as deterministic",
     os.path.join(REPO, "modules", "AiBrain", "engine", "AiEndpointPolicy.cs"),
     b"            throw new AiBackendHttpException(statusCode, IsTransientStatus(statusCode), ExtractProviderErrorMessage(body));",
     b"            throw new AiBackendHttpException(statusCode, false, ExtractProviderErrorMessage(body));",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "HTTP 429 is retryable"),

    # R-023: the PUBLIC constructors' probe bound borrows the chat deadline again.
    ("burn-aibrain: the local probe's public wiring borrows the chat deadline again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OllamaClient.cs"),
     b"            _availabilityDeadline = AiEndpointPolicy.Shorter(_deadline, AiEndpointPolicy.AvailabilityProbeDeadline);",
     b"            _availabilityDeadline = _deadline;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the shipped Ollama client's reachability probe is bounded by the probe deadline"),
    ("burn-aibrain: the cloud probe's public wiring borrows the chat deadline again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "OpenAiCompatBackend.cs"),
     b"            _probeDeadline = AiEndpointPolicy.Shorter(_deadline, AiEndpointPolicy.AvailabilityProbeDeadline);",
     b"            _probeDeadline = _deadline;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the shipped cloud backend's reachability probe is bounded the same way"),

    # RA-087, three legs: the composite maps the warm-up by id again, forgets the model it warmed, and the brain
    # names the wrong path.
    ("burn-aibrain: the composite warms the local model by id again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "FallbackBackend.cs"),
     b"            return WarmLocalAsync(model, visionPath ? _localVisionModel : _localTextModel, ct);",
     b"            return WarmLocalAsync(model, LocalModelFor(model), ct);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the composite warms the local model for the PATH"),
    ("burn-aibrain: the composite forgets the model it warmed again",
     os.path.join(REPO, "modules", "AiBrain", "engine", "FallbackBackend.cs"),
     b"            _lastLocalModel = localModel;\n"
     b"            try { await _local.WarmUpAsync(localModel, ct).ConfigureAwait(false); } catch { }",
     b"            if (localModel == null) _lastLocalModel = localModel;\n"
     b"            try { await _local.WarmUpAsync(localModel, ct).ConfigureAwait(false); } catch { }",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the release covers the model the warm-up loaded"),
    ("burn-aibrain: the brain's warm-up names the wrong path",
     AIBRAIN_ENGINE,
     b"                    await _backend.WarmUpAsync(_useVision ? _visionModel : _textModel, _useVision, ct).ConfigureAwait(false);",
     b"                    await _backend.WarmUpAsync(_useVision ? _visionModel : _textModel, !_useVision, ct).ConfigureAwait(false);",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the brain's 'keep' warm-up names the path"),

    # RA-076: the UI save budget grows past the ceiling the pane can bear.
    ("burn-aibrain: the UI save budget grows past two seconds",
     AISETTINGS,
     b"        internal const int UiSaveBudgetMilliseconds = 1500;",
     b"        internal const int UiSaveBudgetMilliseconds = 4000;",
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the UI save budget stays under two seconds"),

    # RA-071: the Test OCR verdict stops being a pane status line.
    ("burn-aibrain: the Test OCR verdict is blank",
     AIBRAIN_MODULE,
     b"                return verdict;",
     b'                return "";',
     AIBRAIN_CSPROJ, AIBRAIN_DLL,
     "--module-selftest=aibrain", "dp-module-aibrain-selftest.txt",
     "the Test OCR verdict is a pane status line"),
    # ---- lane burn/fortunes ----

    # Every case runs the module's own SelfTest through the convention flag, where the probe's assertions
    # live. Names carry the "burn-fortunes:" prefix so `--only=burn-fortunes:` runs the lane; the second
    # word names the group (bulk, loader, importer, smart, embedder).

    # RA-121: "Select none" on the packs card saves nothing again. The write and the rebuild stay; the fold
    # into the stored list is what goes, which is the shape the finding describes from the user's side (a
    # bulk choice that the saved state never received).
    ("burn-fortunes: bulk: 'Select none' saves no selection again",
     FORTUNES_MODULE,
     b'            ms.Set(key, MergeDisabled(ms.Get(key, ""), batch));',
     b'            ms.Set(key, ms.Get(key, ""));',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "saves every pack as disabled at once"),

    # RA-122: the selection is saved but the engine is not rebuilt on it, so the pool status beside the
    # unticked boxes reads the old pool and the pet keeps drawing from packs the user just turned off.
    ("burn-fortunes: bulk: a saved bulk selection no longer rebuilds the engine",
     FORTUNES_MODULE,
     b"            _stagedDisabled.Remove(key);\n"
     b"            RebuildEngine();\n"
     b"            return true;",
     b"            _stagedDisabled.Remove(key);\n"
     b"            if (ids.Count < 0) RebuildEngine();\n"
     b"            return true;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "rebuilds the live pool on it"),

    # RA-122: the status asks for an Apply again. With the selection already saved there is nothing for the
    # host to arm, so the sentence is the whole defect: it sent the user to a greyed-out button.
    ("burn-fortunes: bulk: the bulk status asks for an Apply again",
     FORTUNES_MODULE,
     b'            return (active ? "Ticked all " : "Unticked all ") + ids.Count + (ids.Count == 1 ? " pack." : " packs.");',
     b'            return (active ? "Ticked all " : "Unticked all ") + ids.Count + (ids.Count == 1 ? " pack. Apply to use it." : " packs. Apply to use it.");',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "never 'Apply to use it'"),

    # RA-095: the seam swap in RefillBag goes. The old single unseeded seam saw the repeat one run in six;
    # the seeded sweep sees it every run.
    ("burn-fortunes: loader: the shuffle-bag seam swap is deleted",
     FORTUNE_PROVIDER,
     b"            if (n >= 2 && _bag[n - 1] == _last)\n"
     b"            {\n"
     b"                int tmp = _bag[n - 1]; _bag[n - 1] = _bag[0]; _bag[0] = tmp;\n"
     b"            }",
     b"            if (n >= 2 && _bag[n - 1] == _last && n < 0)\n"
     b"            {\n"
     b"                int tmp = _bag[n - 1]; _bag[n - 1] = _bag[0]; _bag[0] = tmp;\n"
     b"            }",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "does not repeat the previous line"),

    # R-034 (cap): the walk ends in silence at the file cap again, the `break` shape.
    ("burn-fortunes: loader: a pack past the file cap vanishes in silence again",
     FORTUNE_PROVIDER,
     b"                    if (files >= limits.Files || totalEntries >= limits.Entries)\n"
     b"                    {\n"
     b"                        skips.OverCap++;\n"
     b"                        continue;\n"
     b"                    }",
     b"                    if (files >= limits.Files || totalEntries >= limits.Entries)\n"
     b"                        break;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "counted as over the cap instead of vanishing"),

    # R-034 (wording): the pane folds the valid files that did not fit into the damaged sentence again, so
    # a budget refusal reads as "malformed rows".
    ("burn-fortunes: pane: budget refusals are worded as malformed again",
     FORTUNES_MODULE,
     b"            int damaged = skips.Damaged;\n"
     b"            int didNotFit = skips.DidNotFit;",
     b"            int damaged = skips.Damaged + skips.DidNotFit;\n"
     b"            int didNotFit = 0;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "never as malformed"),

    # RA-125: the empty-pool path drops the refused-pack note again.
    ("burn-fortunes: pane: an empty pool's status drops the refused-pack note again",
     FORTUNES_MODULE,
     b'            if (lines == 0) return "\xe2\x9c\x97 " + EmptyPoolReason(AnyPacksInstalled()) + SkippedPacksNote(skips);',
     b'            if (lines == 0) return "\xe2\x9c\x97 " + EmptyPoolReason(AnyPacksInstalled());',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "blames the filters AND names the skipped file"),

    # RA-107, three arms. (a) the walk fault is swallowed whole again: not counted, not flagged.
    ("burn-fortunes: loader: a faulting folder walk is swallowed uncounted again",
     FORTUNE_PROVIDER,
     b"            catch { walkFaulted = true; }\n"
     b"            if (walkFaulted)",
     b"            catch { }\n"
     b"            if (walkFaulted)",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "counted once under error"),

    # (b) the prune runs after a faulted walk again, evicting every parse the walk never reached.
    ("burn-fortunes: loader: a faulted walk prunes the parses it never reached again",
     FORTUNE_PROVIDER,
     b"            if (seen != null && !walkFaulted) PruneCache(directory, seen);",
     b"            if (seen != null) PruneCache(directory, seen);",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "not pruned from the per-file cache"),

    # (c) a faulted walk is cached under the folder's real fingerprint again and served as a hit.
    ("burn-fortunes: loader: a faulted walk is served as a cache hit again",
     FORTUNE_PROVIDER,
     b'                string published = skips.WalkFaulted ? "faulted:" + Guid.NewGuid().ToString("N") : signature;',
     b'                string published = signature;',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "never as a cache hit"),

    # RA-108: the source/genre memo never hits (both guards compare against null), so every Sources() and
    # Genres() call walks the merged corpus again.
    ("burn-fortunes: loader: the source and genre lists are recomputed on every call again",
     FORTUNE_PROVIDER,
     b"            if (current != null && ReferenceEquals(current.Snapshot, snap)) return current;\n"
     b"            lock (_aggregatesLock)\n"
     b"            {\n"
     b"                current = _aggregates;\n"
     b"                if (current != null && ReferenceEquals(current.Snapshot, snap)) return current;\n",
     b"            if (current != null && ReferenceEquals(current.Snapshot, null)) return current;\n"
     b"            lock (_aggregatesLock)\n"
     b"            {\n"
     b"                current = _aggregates;\n"
     b"                if (current != null && ReferenceEquals(current.Snapshot, null)) return current;\n",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "from one memo"),

    # R-028: the per-file cache lookup never hits, so a changed folder re-reads every file (the folder-wide
    # cache in disguise: the importer's admission check still passes, because every parse is still STORED).
    ("burn-fortunes: loader: the per-file parse cache never hits",
     FORTUNE_PROVIDER,
     b"                    if (_packCache.TryGetValue(path, out hit) &&\n"
     b"                        string.Equals(hit.Stamp, stamp, StringComparison.Ordinal))\n"
     b"                        parse = hit;",
     b"                    if (_packCache.TryGetValue(path, out hit) &&\n"
     b"                        string.Equals(hit.Stamp, stamp, StringComparison.Ordinal))\n"
     b"                        parse = null;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "re-read exactly ONE pack file"),

    # RA-098: the fold stops re-emitting a suite-prefixed exception line as a FAIL verdict.
    ("burn-fortunes: probe: a suite-prefixed EXC line is folded without its FAIL prefix again",
     FORTUNES_PROBE,
     b'            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[A-Z][A-Z ]* EXC: ")) return true;\n',
     b'',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "re-emitted as a FAIL verdict"),

    # RA-102: every IOException and Win32Exception is transient again, whatever its code.
    ("burn-fortunes: importer: a permanent file fault is retried as transient again",
     FORTUNE_IMPORTER,
     b"            return code == ErrorSharingViolation || code == ErrorLockViolation ||\n"
     b"                   code == ErrorBusy || code == ErrorUserMappedFile;",
     b"            return code != int.MinValue;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "surface at once"),

    # RA-104: the commit's catch deletes the backup whatever state the destination is in again.
    ("burn-fortunes: importer: a torn replace deletes the backup that holds the pack again",
     FORTUNE_IMPORTER,
     b"                    RestoreTornReplace(destinationPath, backupPath);\n"
     b"                    throw;",
     b"                    TryDeleteFile(backupPath);\n"
     b"                    throw;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "undone from its backup"),

    # RA-101: the rejection status stops naming the file.
    ("burn-fortunes: pane: a rejected import no longer names the file",
     FORTUNES_MODULE,
     b'                            firstError = (name.Length > 0 ? name + ": " : "") + Short(item.Error);',
     b'                            firstError = (name.Length < 0 ? name + ": " : "") + Short(item.Error);',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "names the file it refused"),

    # RA-124: the download's content check runs on the calling thread again.
    ("burn-fortunes: pane: the download validates its pack on the calling thread again",
     FORTUNES_MODULE,
     b"                        bool loadable = await Task.Run(delegate { return ValidateDownloadedPack(bytes, item.Id); });",
     b"                        bool loadable = ValidateDownloadedPack(bytes, item.Id);",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "content validation ran on a pool thread"),

    # R-030: the decoder stops trimming, so a padded tagged text column is refused as untrimmed again (the
    # 1.0.11 contract), and the pin says which contract this build carries.
    ("burn-fortunes: loader: a padded tagged text column is refused as untrimmed again",
     FORTUNE_PROVIDER,
     b"            return text.Trim();",
     b"            return text;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "admitted with the text trimmed"),

    # RA-120 (schedule): an empty pool schedules a smart build again, which constructs a picker (a cache.bin
    # parse, retained) for a Warm that starts nothing.
    ("burn-fortunes: smart: an empty pool schedules a smart build again",
     FORTUNES_MODULE,
     b"            bool buildable = wanted && pool != null && pool.Count > 0;",
     b"            bool buildable = wanted && pool != null;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "schedules no smart build"),

    # RA-120 (log): the publish line says "warming" whatever Warm decided.
    ("burn-fortunes: smart: the publish line claims a warm after a stand-down again",
     FORTUNES_MODULE,
     b"            if (warmStandDown == SmartStandDownReason.None)\n"
     b"                return \"smart picker constructed, warming \"",
     b"            if (warmStandDown != SmartStandDownReason.ConstructionFailed)\n"
     b"                return \"smart picker constructed, warming \"",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "says the warm stood down"),

    # R-027: a build that ends no longer leaves the in-flight count, so the probe's join waits its whole
    # bound and reports it (two joins, 20 s each).
    ("burn-fortunes: smart: a smart build no longer counts itself out when it ends",
     FORTUNES_MODULE,
     b"            finally { System.Threading.Interlocked.Decrement(ref _smartBuildsInFlight); }",
     b"            finally { System.Threading.Volatile.Read(ref _smartBuildsInFlight); }",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "every smart build had ended"),

    # RA-097: Shutdown nulls the engine sink whoever installed it again.
    ("burn-fortunes: smart: Shutdown drops another owner's engine sink again",
     FORTUNES_MODULE,
     b"            if (ReferenceEquals(SmartFortunes.LogSink, _smartSink)) SmartFortunes.LogSink = null;",
     b"            if (ReferenceEquals(SmartFortunes.LogSink, _smartSink) || SmartFortunes.LogSink != null) SmartFortunes.LogSink = null;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "clears only its own"),

    # RA-126: the Rebuild button builds its currency-guard provider on the pressing thread whatever the
    # index's state again.
    ("burn-fortunes: smart: the Rebuild button parses the folder for an incomplete index again",
     FORTUNES_MODULE,
     b"                    if (complete)\n"
     b"                    {\n"
     b"                        System.Threading.Interlocked.Increment(ref _guardProvidersBuilt);",
     b"                    if (complete || !complete)\n"
     b"                    {\n"
     b"                        System.Threading.Interlocked.Increment(ref _guardProvidersBuilt);",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "builds no currency-guard provider"),

    # R-031: the warm task's catch-all records no stand-down again.
    ("burn-fortunes: smart: a warm that throws leaves no stand-down reason again",
     SMART_FORTUNES,
     b"                                    _standDownDetail = ex.GetType().Name;\n"
     b"                                    _standDown = SmartStandDownReason.WarmFailed;",
     b"                                    _standDownDetail = ex.GetType().Name;\n"
     b"                                    _standDown = SmartStandDownReason.None;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "stands the index down with WarmFailed"),

    # RA-115: a second Warm no longer cancels the first. The ReferenceEquals guards still keep the first
    # from publishing, which is why rewarm_supersedes alone could not see this.
    ("burn-fortunes: smart: a second Warm no longer cancels the first",
     SMART_FORTUNES,
     b"                if (_warmCancellation != null)\n"
     b"                {\n"
     b"                    try { _warmCancellation.Cancel(); } catch { }\n"
     b"                }",
     b"                if (_warmCancellation != null)\n"
     b"                {\n"
     b"                    try { if (_warmCancellation.IsCancellationRequested) _warmCancellation.Cancel(); } catch { }\n"
     b"                }",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "rewarm_cancels_previous=FAIL"),

    # N-burn-fortunes-01: the probe's F145 check, both halves. (a) The ORDER half: the completion line
    # moves before the flag it reports, still exactly one line, so only the order can fail; the sink
    # asks the picker at the instant the line arrives, where a 50 ms poll could not see the window.
    ("burn-fortunes: smart: the completion line goes out before the flag it reports",
     SMART_FORTUNES,
     b"                _warmComplete = true;\n"
     b"                // In the SAME lock hold as the flag (RA-115), so a reader that sees WarmProgress report\n"
     b"                // complete sees this counter too and needs no wait for the line that follows.\n"
     b"                _warmsCompleted++;\n"
     b"            }\n"
     b"            // The completion line comes from HERE, the only place that knows the warm finished. The\n"
     b"            // module's own line at publish time says the picker was constructed and the warm queued;\n"
     b"            // until 1.0.12 it said \"ready (N lines indexed)\" at that moment, when nothing had been\n"
     b"            // embedded yet, and nothing ever said the warm had finished (F145).\n"
     b"            Say(\"smart index complete: \" +\n"
     b"                validCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + \" of \" +\n"
     b"                n.ToString(System.Globalization.CultureInfo.InvariantCulture) + \" lines indexed\");\n",
     b"                Say(\"smart index complete: \" +\n"
     b"                    validCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + \" of \" +\n"
     b"                    n.ToString(System.Globalization.CultureInfo.InvariantCulture) + \" lines indexed\");\n"
     b"                _warmComplete = true;\n"
     b"                // In the SAME lock hold as the flag (RA-115), so a reader that sees WarmProgress report\n"
     b"                // complete sees this counter too and needs no wait for the line that follows.\n"
     b"                _warmsCompleted++;\n"
     b"            }\n"
     b"            // The completion line comes from HERE, the only place that knows the warm finished. The\n"
     b"            // module's own line at publish time says the picker was constructed and the warm queued;\n"
     b"            // until 1.0.12 it said \"ready (N lines indexed)\" at that moment, when nothing had been\n"
     b"            // embedded yet, and nothing ever said the warm had finished (F145).\n",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "once it is complete and not before"),

    # (b) The COUNT half: the line goes out twice. The probe waits for the warm task to end before it
    # counts, so both lines are in and "exactly one" fails; before that wait the count was taken the
    # instant the flag was seen, which could also see zero on a correct build.
    ("burn-fortunes: smart: the completion line goes out twice",
     SMART_FORTUNES,
     b"            Say(\"smart index complete: \" +\n",
     b"            Say(\"smart index complete: \" + validCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + \" of \" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + \" lines indexed\");\n"
     b"            Say(\"smart index complete: \" +\n",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "once it is complete and not before"),

    # R-025: the load-failure label stops unwrapping, so a missing native runtime reads "model:
    # TypeInitializationException" again.
    ("burn-fortunes: embedder: a wrapped native-runtime failure is labelled as the model again",
     EMBEDDER,
     b"                if (inner == null) break;\n"
     b"                root = inner;",
     b"                if (inner == null || inner != null) break;\n"
     b"                root = inner;",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "named as the runtime"),

    # R-026: the per-instance session count misreports (two per load), which the exactly-one check must
    # notice through this embedder's own counter.
    ("burn-fortunes: embedder: the per-instance session count misreports",
     EMBEDDER,
     b"                    _sessionsCreated++;   // under _lock",
     b"                    _sessionsCreated += 2;   // under _lock",
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "concurrent_first_use=FAIL"),

    # N-burn-aibrain-01: the preview clips at a bare UTF-16 index again (the RA-057 shape), severing a
    # surrogate pair that straddles the cut.
    ("burn-fortunes: pane: the preview clips a surrogate pair in half again",
     FORTUNES_MODULE,
     b'            return one.Length > maximum ? UnicodeTextProgress.TruncateAtCodePointBoundary(one, maximum) + "\xe2\x80\xa6" : one;',
     b'            return one.Length > maximum ? one.Substring(0, maximum) + "\xe2\x80\xa6" : one;',
     FORTUNES_CSPROJ, FORTUNES_DLL,
     "--module-selftest=fortunes", "dp-module-fortunes-selftest.txt",
     "backs off to the character before it"),

    # ---- lane burn/agentflow ----
    # (no host-side self-test guards: the lane's checks live in --module-selftest=agentflow and are
    # mutation-tested by tests/mutate-agentflow.py; its source invariants by tests/mutate-hardening-guards.py)
    # ---- lane burn/host-core ----
    # RA-231: the launch rotation goes back to the field default of 2, which deleted every archive above .1 on
    # each launch for a user who kept more than two files. CoreTests pins the shift against a scratch directory.
    ("host-core: the launch rotation shifts with the field default again",
     DIAGNOSTIC_LOG,
     b"            RotateIn(directory, MaximumKeep);",
     b"            RotateIn(directory, 2);",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "The launch rotation discarded an archive a larger keep setting allows."),

    # RA-267: the migration writes the current name but leaves the pre-rename value in place, so Windows keeps
    # a second startup item pointing at the uninstalled product. CoreTests pins it against the redirected key.
    ("host-core: the migration keeps the pre-rename Run entry",
     STARTUP_REGISTRATION,
     b"                    key.DeleteValue(LegacyValueName, false);\n"
     b"                    if (key.GetValue(ValueName) == null)\n",
     b"                    if (key.GetValue(ValueName) == null)\n",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "The pre-rename Run entry survived the migration."),
    # RA-232: the factory reset's removal skips the current-name value the app itself writes.
    ("host-core: the reset's removal leaves the current Run entry",
     STARTUP_REGISTRATION,
     b"                    if (current) key.DeleteValue(ValueName, false);\n",
     b"",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "The Run entry survived the factory-reset removal."),
    # RA-273: the DOT export writes the title's line breaks into its '#' comment again, so the remainder of a
    # two-line title is parsed as DOT tokens ahead of `digraph`.
    ("host-core: the DOT title comment keeps its line breaks",
     os.path.join(REPO, "src", "Tools", "XmlToDot.cs"),
     b"\t\t\treturn text.Replace(\"\\r\\n\", \" \").Replace('\\r', ' ').Replace('\\n', ' ');",
     b"\t\t\treturn text;",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "A line before the digraph is not a comment"),
    # RA-235: the kill fade seeds at full opacity whatever the pet shows, so a kill that ramped to 0 pops back.
    ("host-core: the kill fade re-seeds at full opacity",
     RUNTIME_GEOMETRY,
     b"            return Math.Max(0.0, Math.Min(1.0, currentOpacity));",
     b"            return 1.0;",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "A kill whose ramp reached 0 was re-seeded above 0."),

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
    # Re-pointed at the merge of burn/host-core (2026-10-01): the surviving call hands back the sound bytes too (RA-271),
    # so the pattern names the six-out signature; the mutation is the same refusal after an accepted parse.
    ("burn/host-shell: the loader refuses every pet the validator accepted",
     XML_CS,
     b"            if (!CompanionXmlValidator.TryParse(xmlText, out parsed, out sheetBytes, out iconBytes, out soundBytes, out error))\n"
     b"                return false;\n",
     b"            if (!CompanionXmlValidator.TryParse(xmlText, out parsed, out sheetBytes, out iconBytes, out soundBytes, out error))\n"
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
    # Re-pointed 2026-10-01 by lane burn/host-core: the always-true `smaller > 0 &&` term is gone (RA-254).
    ("deadcode: FitFactorForFrameD loses its one-pixel floor",
     RUNTIME_GEOMETRY,
     b"            if ((double)smaller * f < 1.0) f = 1.0 / smaller;",
     b"            if ((double)smaller * f < 0.0) f = 1.0 / smaller;",
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
    # Re-pointed 2026-09-30 by lane burn/fortunes: RA-124 moved the validation onto a pool thread, and the
    # refusal now sits on one line behind its result.
    ("deadcode: the downloader writes a malformed catalog payload again",
     FORTUNES_MODULE,
     b"                        if (!loadable) { failed++; malformed++; continue; }",
     b"                        if (!loadable) { }",
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

    # ---- lane burn/scripts-tests ----
    # RA-339: the read-only-fallback write guard in AppSettingsStore.Save. While the test still held the
    # settings lease, Save failed through WithFileLock's IOException as well, so this deletion passed all 37
    # groups; the assertion that sees it is the one taken AFTER the lease is released, before the reload.
    ("burn/scripts-tests: Save forgets the read-only fallback once the lock is free",
     APPSETTINGS_STORE,
     b"                if (_writesBlockedByLoadFailure) return false;\n",
     b"",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "Save ignored the read-only fallback flag"),

    # RA-338: the recording host's stress reader never runs its loop. The writer now parks at three quarters
    # until the reader has enumerated once, so a reader that never does is the writer's own timeout, named,
    # rather than a scheduler-dependent `enumerated > 0`. This case waits that timeout out (30 s).
    ("burn/scripts-tests: the stress reader never enumerates while the writer runs",
     MODULEKIT_TESTS,
     b"            while (!writer.IsCompleted)\n"
     b"            {\n"
     b"                foreach (string line in stress.LoggedLines)",
     b"            while (!writer.IsCompleted && enumerated < 0)\n"
     b"            {\n"
     b"                foreach (string line in stress.LoggedLines)",
     CORETESTS_CSPROJ, CORETESTS_DLL,
     CORETESTS, None, "The reader never signalled its first enumeration pass"),
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
    # A TEMPLATE case names no csproj (None): Test-ModuleTemplate.ps1 builds its own scaffold.
    for csproj in fixed + sorted(set(c[4] for c in CASES if c[4] is not None) - set(fixed)):
        ok, out = build(csproj)
        if not ok:
            return False, out
    return True, ""


# A mutant's run is bounded by ITS OWN unmutated run, not by the 1800 s backstop alone (lane burn/aibrain,
# 2026-09-30). During that lane's --only run an exe from its worktree's build was seen holding about ten cores
# for minutes while the user was at the machine, and the backstop would have let a spinning mutant run
# unattended for half an hour. Measured afterwards on the clean tree (2026-10-01): the F071 mutant the run was
# scoring does not spin (3.9 CPU seconds against the clean tree's 3.4-4.2; 11 s under this harness), while the
# BASELINE phase, which runs every graded flag before anything is mutated, --only or not, includes
# --fortunes-engine-selftest at 334-374 CPU seconds over a dozen threads in 45 s and --module-selftest=fortunes
# at 53 CPU seconds, which is the shape that was seen, from a clean build. The bound stays, because the next
# mutant that does spin must not need someone at the machine: the first run of each flag is score()'s
# baseline and selftest() times it; every later run of that flag gets four times that, never under five
# minutes and never over the backstop. subprocess.run kills the child when its timeout expires, and the
# verdict names the bound and the baseline it came from, so a spinning mutant scores BROKEN by name. Proved
# with the floor at 5 s and the factor at 0.01: the mutant exe was killed at 5 s and the case read
# BROKEN (--module-selftest=aibrain did not exit in 5s (killed; its unmutated run took 11s)).
MUTANT_CEILING_FLOOR_SECONDS = 300
MUTANT_CEILING_FACTOR = 4
BACKSTOP_SECONDS = 1800
BASELINE_SECONDS = {}


def mutant_ceiling(flag):
    """Seconds a run of `flag` may take: the backstop until its unmutated run has been timed, then that
    time times MUTANT_CEILING_FACTOR, never under the floor and never over the backstop."""
    baseline = BASELINE_SECONDS.get(flag)
    if baseline is None:
        return BACKSTOP_SECONDS
    scaled = int(baseline * MUTANT_CEILING_FACTOR) + 1
    return int(min(BACKSTOP_SECONDS, max(MUTANT_CEILING_FLOOR_SECONDS, scaled)))


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
    if flag == TEMPLATE:
        return template_selftest()
    path = os.path.join(RUN_TEMP, marker)
    try:
        os.remove(path)
    except OSError:
        pass
    if os.path.isfile(path):
        return None, "stale marker could not be removed: " + path
    limit = mutant_ceiling(flag)
    started = time.monotonic()
    try:
        proc = subprocess.run([EXE, flag], capture_output=True, text=True, timeout=limit, env=CHILD_ENV)
    except subprocess.TimeoutExpired:
        # subprocess.run has already killed the child and waited for it. Say so, with the figure the bound
        # came from, so the BROKEN line explains itself.
        return None, "%s did not exit in %ds (killed; its unmutated run took %ds)" % (
            flag, limit, int(BASELINE_SECONDS.get(flag, -1)))
    BASELINE_SECONDS.setdefault(flag, time.monotonic() - started)
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


def template_selftest():
    """packaging\\Test-ModuleTemplate.ps1 in the marker vocabulary the ladder grades.

    The script scaffolds the module template into modules\\TemplateCheck, builds it, loads it through the
    real host with --module-selftest=templatecheck and, when the scaffolded SelfTest reports RESULT=FAIL,
    prints the marker's last lines ('  [templatecheck] FAIL: <label>') before throwing -- so a case names
    its label as the fragment, the '[id] ' prefix is the one _verdict_lines already strips, and the
    script's exit code becomes the column-0 RESULT= line. A run that never reached the self-test (the
    scaffold did not build) prints no FAIL: line and exits 1, which the ladder reads as BROKEN: the truth
    about such a mutation, never a firing. Run under Windows PowerShell with its own module path, the way
    mutate-hardening-guards.py runs the invariant script: launched from pwsh, a powershell.exe child
    inherits pwsh's PSModulePath and autoloads 7-only manifests it cannot run (N-fortunes-01).
    """
    env = dict(CHILD_ENV)
    kept = [p for p in (env.get("PSModulePath") or "").split(os.pathsep)
            if p and "windowspowershell" in p.lower()]
    for default in (os.path.join(env.get("ProgramFiles", r"C:\Program Files"), "WindowsPowerShell", "Modules"),
                    os.path.join(env.get("SystemRoot", r"C:\Windows"), "System32", "WindowsPowerShell", "v1.0", "Modules")):
        if default.lower() not in [p.lower() for p in kept]:
            kept.append(default)
    env["PSModulePath"] = os.pathsep.join(kept)
    try:
        proc = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", TEMPLATE_PS1],
                              capture_output=True, text=True, timeout=1800, env=env, cwd=REPO)
    except subprocess.TimeoutExpired:
        return None, "Test-ModuleTemplate.ps1 did not exit in 1800s"
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


def keep_report(index, name, report):
    """A non-FIRED case's WHOLE report under a per-case name (R-060, R-065).

    Every case rewrites the same marker file, so keeping RUN_TEMP alone would have preserved only the
    last case's report; this copies each one that needs reading before the next case runs. main() then
    keeps the directory when the run is not clean and prints its path, the way Invoke-SelfTests.ps1 and
    Test-ModuleSelfTests.ps1 keep theirs, so a WRONG or BROKEN verdict can be read in full instead of
    from the two 140-character lines printed below."""
    slug = re.sub(r"[^A-Za-z0-9]+", "-", name).strip("-")[:60]
    path = os.path.join(RUN_TEMP, "case-%03d-%s.txt" % (index, slug))
    with io.open(path, "w", encoding="utf-8") as handle:
        handle.write(report)


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
        for index, (name, path, old, new, csproj, artifact, flag, marker, expect) in enumerate(cases, 1):
            base = read(path)
            old_v, new_v = line_ending_variant(base, old, new)
            if base.count(old_v) != 1:
                print("  %-52s NO-OP (pattern matched %d times)" % (name, base.count(old_v)))
                continue
            # A case with no csproj (the TEMPLATE flag) is rebuilt by the script it runs, which scaffolds a
            # fresh module from the mutated template and asserts the DLL exists; there is no artefact here
            # whose timestamp could stand for that.
            before = os.path.getmtime(artifact) if artifact is not None else None
            write(path, base.replace(old_v, new_v))
            time.sleep(1.1)
            report = None
            code = None
            verdict = None
            try:
                if csproj is None:
                    report, code = selftest(flag, marker)
                else:
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
            # THE CHANNELS MUST AGREE HERE TOO (R-063). unhealthy() made the BASELINE refuse a report whose
            # exit code, RESULT= line and FAIL lines disagree; under mutation the same disagreement in the
            # other direction was invisible: a self-test that writes 'FAIL: ...' and still exits 0 with a
            # column-0 RESULT=PASS -- a Check() whose bool is not folded into ok -- scored FIRED here while
            # Invoke-SelfTests.ps1 and CI, which read the exit code, stay green on the very regression the
            # case claims to cover. So a FAIL line inside a green run is a plumbing fault, printed as one,
            # and counted as neither a firing nor a WRONG-elsewhere.
            green = code == 0 and passed(report)
            detail = []
            if has_failure(report) and green:
                outcome = "WRONG (FAIL line but exit 0 and RESULT=PASS: the gate would stay green)"
                detail = failure_lines(report)[:2]
            elif hit:
                fired += 1
                outcome = "FIRED"
                detail = [hit[0]]
            elif has_failure(report):
                outcome = "WRONG -- failed elsewhere"
                detail = failure_lines(report)[:2]
            elif aborted or not green:
                # No FAIL line, but no clean pass either: the self-test threw, skipped, or exited
                # without a column-0 verdict. That is not "the check still passed", so it is never
                # SURVIVED; it is a run that proved nothing about the assertion.
                outcome = "BROKEN (no verdict: %s)" % (aborted[0] if aborted else "exit %s, RESULT=PASS %s" % (
                    code, "present" if passed(report) else "absent"))[:120]
            else:
                outcome = "SURVIVED"
            print("  %-52s %s" % (name, outcome))
            for line in detail:
                print("        %s" % line[:140])
            if outcome != "FIRED":
                keep_report(index, name, report)

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
    # KEPT unless the run was clean (R-060, R-065): a refused baseline's red marker, every non-FIRED
    # case's report (keep_report) and whatever a Ctrl+C or a timeout left behind stay readable, and the
    # path is printed so the leftover is deliberate. Invoke-SelfTests.ps1's aged dp-* sweep collects a
    # kept directory after an hour, the same safety net the PowerShell runners rely on.
    keep = True
    try:
        code = score(args)
        keep = code != 0
        return code
    finally:
        # ...and only when it holds something: a refused --only or a missing exe leaves nothing to read.
        if keep and any(os.scandir(RUN_TEMP)):
            print("markers kept for inspection: " + RUN_TEMP)
        else:
            shutil.rmtree(RUN_TEMP, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
