# Shimeji oracle feasibility tooling

This lane builds the pinned official source outside the checkout and compares it with the official runnable
distribution. The separate Java driver executes upstream configuration, action, manager and rendering classes.
It does not replace those classes or claim complete source-to-host parity.

Use Windows PowerShell 5.1 or later. Supply private directories outside the checkout and the installed JDK 8
directory. The source and runnable archives are downloaded from the official publisher.

```powershell
$oracle = @{
    CacheRoot = '<private-cache-directory>'
    OutputRoot = '<private-evidence-directory>'
    JdkRoot = '<installed-jdk-8-directory>'
    PythonPath = $env:TOOLBOX_PYTHON
}
.\Invoke-Oracle.ps1 @oracle -Action Preflight
.\Invoke-Oracle.ps1 @oracle -Action Fetch
.\Invoke-Oracle.ps1 @oracle -Action Build
.\Invoke-Oracle.ps1 @oracle -Action Compare
.\Invoke-Oracle.ps1 @oracle -Action Probe -Runtime rebuilt
.\Invoke-Oracle.ps1 @oracle -Action Probe -Runtime official
.\Invoke-Oracle.ps1 @oracle -Action Check
```

The entry point sets this lane's data root, TEMP/TMP, no-real-input flag and .NET tool-path flag for its child
processes. It uses the supplied JDK path directly and does not change PATH or JAVA_HOME. `CacheRoot`,
`OutputRoot`, `JdkRoot` and `PythonPath` can be supplied explicitly. Cache and evidence belong outside this
checkout. `CacheRoot` and `JdkRoot` are required, or can be supplied through `SHIMEJI_ORACLE_CACHE` and
`SHIMEJI_ORACLE_JDK8`. `OutputRoot` defaults to the cache's evidence directory. The wrapper supplies isolation
variables through the child process environment and preserves the calling shell's environment.
Every supplied path is resolved against the current PowerShell location, including JDK, shared-input and Python
paths. Cache, output, data, temporary and shared-input roots inside this tool's repository are refused before
directories are created. The Python
preflight also checks resolved physical paths. `Preflight` prints resolved paths and creates no directories.

`Fetch` checks the source archive against its pinned SHA-256 before extraction. Archive paths, symbolic links,
protected credential-file paths and expanded size are checked before payload extraction. Later verification
derives expected file hashes from the pinned archive, rather than trusting editable inventory metadata.
`Build` compiles the archive's source roots with JDK 8, UTF-8 and the original source/target 1.6 options. Compiler
commands, warnings, exit status and artifact identity are retained in the private cache.

`Compare` downloads the official runnable archive separately and verifies pinned archive and runtime JAR hashes.
It refuses a changed class inventory, class count or instruction comparison. It compares class inventories
and `javap` signatures/instructions, normalizing constant-pool indexes while retaining their resolved operands.
The comparison report explicitly distinguishes bytecode evidence from behavioral proof. The official probe
uses the runnable distribution's own libraries and configuration; the rebuild uses the source archive's versions.
`Probe -Runtime official` and `Check` require a passing comparison bound to the current artifacts, resources and tooling.

`Probe` generates its own asymmetric art and fixtures in isolated run folders. Its witnesses cover ordered
frames and instant actions, independent births and delayed membership, and same-entity transformation with
stored-variable retention. The sequence also exercises the source Nashorn evaluator. Each child JVM verifies
the loaded runtime JAR, steps the real manager, checks state, advances the real timer loop, then disposes its
owned windows and exits. Process IDs and exit results remain in private launch logs.

The unchanged virtual environment still samples the real cursor. These baseline fixtures contain no
cursor-dependent behavior and send no input. They establish execution and owned-window creation, not visual
parity. No capture is performed by these commands. The scenario adapter is a separate draft below.

Feasibility JSONL uses `oracle-feasibility/0`; it is not the shared parity trace protocol. `Check` requires
current tool, driver and runtime fingerprints, verifies trace bytes and recomputes directed state signatures.
It refuses stale evidence instead of accepting an earlier successful pair.
The fixed `Probe` fixtures additionally declare `geometrySchema: oracle-pose-geometry/1`. Their state records
include `origin` from `Mascot.getBounds()` and `poseAnchor` from the actual image's effective center, or null
without an image. Before the first applied pose both are null. Only in these fixed fixtures, time greater than
zero identifies that first pose; a shared-protocol writer must track applied-pose presence explicitly instead.
An image-less pose retains the last window bounds while its anchor moves. This addition does not supply the
shared protocol's pose index, source image identities, reference keys or semantic events.

`Shared -SharedRoot '<generated-scenarios-folder>'` accepts only scenarios declaring no controlled RNG. It
refuses tape RNG because its unchanged-runtime driver cannot intercept draws. Accepted runs record private
scenario and pack hashes and compare provisional step records. They do not produce a shared-protocol verdict.

## Draft seams

```powershell
.\Invoke-Oracle.ps1 @oracle -Action Draft
.\Invoke-Oracle.ps1 @oracle -Action Bridge
```

`Draft` reads the tracked `edit-spec.json`, which identifies original files and lines by SHA-256 and describes
edits without storing upstream source text. It extracts a fresh private source copy, normalizes Java text to
UTF-8/LF, generates the actual diff only in the private cache, checks and applies it with
`git apply --unidiff-zero`, then compiles that copy. Original file/line hashes, operation inventories and every
post-apply file hash are checked. The verified source cache is checked again afterward. Helpers in `draft/`
are original test code included as new files in the private patch.

The draft provides a manager step entry, observation callbacks, a virtual-display factory with a scenario
environment, startup interception, fail-fast dialogs, Java RNG call-site routing, and script RNG installation
into the actual Nashorn bindings. `DraftTraceWriter` is JSONL transport with caller-supplied records.
`Bridge` exercises the patched source `Script` class, its cache lifetimes, controlled draws, and an unused-lazy-
variable observation witness.

The draft is not a finished scenario runner. It does not load the shared scenario JSON, classify all semantic
events, assign canonical reference keys, dispatch input timelines, or produce the shared trace schema. Its
Java RNG labels use the contract's canonical builtin site names. The environment helper models one active window and one screen; broader
scenario behavior requires the reviewed contract adapter. Trace fields remain subject to contract sign-off.

The simulated desktop keeps native window state separate from the area cached for a tick. Moves and minimization
become visible in the cached area at the next tick; each tick resets area deltas. Actions reacquire the live target,
so a missing, minimized or offscreen window cannot be moved again. Restoration relocates a shown offscreen window
without undoing minimization or inventing a restore event. The absent-window zero rectangle follows the source's
freshly allocated RECTs after failed native calls. This assumption has not been checked with native Win32 probing.
The restore-then-move case also retains the source's cached area size, which is 0x0 after an absent-window refresh.
The virtual model then treats the zero-sized result as absent. Native Win32 minimum tracking-size clamping for a
captioned top-level window is not modelled or probed, so this case is a declared modelling assumption.

## CONTRACT v1 producer

```powershell
.\Invoke-Oracle.ps1 @oracle -Action Draft
.\Invoke-Oracle.ps1 @oracle -Action V1 -SharedRoot '<generated-scenarios-folder>' [-Only 'S-slice-,S-breed-']
```

`V1` runs every `S-*.json` under `SharedRoot` (or the ids matching `-Only`) on the private draft runtime and writes
`shimeji-parity-trace/1` traces for `tools/ShimejiParity/comparer/compare.py`. It first re-hashes the draft build it
binds: the record, the JAR, the private patch, `edit-spec.json` and the helpers in `draft/`; a missing record or field,
a changed JAR, a tampered patch or a stale specification or helper is refused by name. The header's `build` carries
the source archive hash, the patch hash, the JDK version, every library and the runner (`deps`) and the JAR hash.

`V1ScenarioRunner.java` is original driver code compiled beside the draft JAR. It installs its controller before any
environment class initialises, replaces the native factory before any companion exists (scenario-fed desktop,
never-displayed panels for windows), creates the start companions as CONTRACT section 4 describes and steps the
source's own `Manager.tick` once per tick through the `parityStep` seam; the real timer never starts. Every run ends by
checking that no window became displayable. Names, refKeys and ages come from stored fields and from the seam
callbacks the source makes while building and stepping; observation evaluates nothing. Input entries reach the
source's own mouse listeners as synthetic events at the step's environment refresh. A refKey's file is the one the
source's own search chose, reported where `Main.loadConfiguration` parses it, spelled as the pack's directory lists
each component (the source probes lower-case names, which Windows also matches to, say, `Actions.xml`); a document
without that binding is refused (`V1_REFKEY_DOCUMENT_UNBOUND`), and one whose pack entry is missing or ambiguous
(`V1_REFKEY_DOCUMENT_SPELLING`). Each `V1` run also checks the binding on private fixtures built from
`S-slice-stand-walk-01`'s pack: the pack unmodified (the scenario's own run when `V1` ran it), equal-content copies at
the lower-precedence `conf/<set>/` and `conf/` tiers, malformed XML the source never opens, the documents moved to
`conf/<set>/` (an equal copy left at `conf/`), the documents moved to `conf/`, and the documents spelled
`Behaviors.xml` and `Actions.xml`. Every case must run complete, every refKey must name the exact file that case lays
out (stack position 0 the behaviours file, every later position the actions file, each a pack manifest entry, for the
key's own image set), and every other case's stacks must match the unmodified pack's apart from the file. All cases
run before one `V1_DOCUMENT_WITNESS` refusal names every failing case; their results are in the run's
`document-witnesses.json`.

Each scenario gets one status: `complete`, or a distinct failure (`truncated`, `timeout`, `error`, or the comparer's
trace-check class). A requested scenario without a result fails the action (`V1_SCENARIO_SKIPPED`). The named proof
scenario `S-input-drag-01`, which must be directed (`none` or `tape`), is run again with observation off; both runs
must complete and its final tick (an empty one included), draw and event totals must equal the traced run's (CONTRACT
section 5). Without that scenario the proof is reported unavailable and the action is incomplete. Draws from a script `Math.random` are refused (`V1_RNG_SCRIPT_SITE_UNATTRIBUTED`) until
the contract's script site names are attributed. Scan sites report their result through `DraftParityHooks.scanned`
(the `wrap-assigned-value` spec operation, which refuses any call outside the hooks class) and Interact reports the
start of its step. A runner refusal (Mute, or a sounding pose with sounds on, whose events have no seam yet) ends the
run `aborted` and names itself; an exception from the source itself ends it `error`. Traces and launch records stay in
the private evidence folder.

`V1Failures` reuses the latest `V1` evidence on private copies, and first refuses it (`V1_EVIDENCE_STALE`) unless
`oracle.py`, the runner, the draft JAR and patch and the base trace's bytes are still the ones that run recorded and
the draft build still validates: a header without its build witness, a trace without
its footer, a tampered copy of the private patch, a draft record missing a field and a run missing a scenario must
each fail by name, and a real trace judged against itself relabelled as a host trace must be `exact` until it is
changed, then fail as `entity-mismatch` (companions out of manager order), `state-mismatch` on `pose` (another image,
same position) and on `actionAge`, `extra-event`, `missing-event`, `event-mismatch` (another draw site) and
`rng-draw-count` (an extra draw).

## Failure witnesses

```powershell
.\Invoke-Oracle.ps1 @oracle -Action SelfTest
.\Invoke-Oracle.ps1 @oracle -Action Mutate
.\Invoke-Oracle.ps1 @oracle -Action Failures
.\Test-OracleIsolation.ps1 -PythonPath $env:TOOLBOX_PYTHON -Mutate
.\Test-OraclePaths.ps1 -PythonPath $env:TOOLBOX_PYTHON -Mutate
```

`SelfTest` exercises checksum refusal, archive containment, protected-path refusal, bytecode difference
preservation, source/inventory tampering, distribution pins, exact class comparisons and isolation variables.
`Mutate` disables the corresponding controls in fresh private
script copies and requires the named witness to fail; it removes those copies and checks the tracked script
remained byte-identical.

`Draft` also exercises malformed edit specifications and in-memory guard mutations. `Test-OracleIsolation.ps1`
runs the wrapper in the calling shell and checks that its environment markers survive unchanged.
Its mutation changes only a private wrapper copy. `Test-OraclePaths.ps1` starts with different native and PowerShell
locations, checks every resolved root, exercises both repository refusals, and kills a native-CWD regression. Both
PowerShell checks remove their own fixtures and verify the tracked wrapper was unchanged.

Two additional focused checks use an explicitly supplied Python interpreter:

```powershell
& '<python-executable>' -I -B .\verify_windows.py --cache-root $oracle.CacheRoot --output-root $oracle.OutputRoot --jdk-root $oracle.JdkRoot --mutate
& '<python-executable>' -I -B .\verify_vocabulary.py --cache-root $oracle.CacheRoot --converter-parser '<converter-parser-file>'
& '<python-executable>' -I -B .\verify_pose_geometry.py --cache-root $oracle.CacheRoot --output-root $oracle.OutputRoot --jdk-root $oracle.JdkRoot --mutate
```

`verify_windows.py` builds only the original helper/probe sources over the private draft runtime, then runs headless
directed cases and optional mutants. Each mutant must produce a newly built, changed adapter class and exactly its
named execution failure. It creates no display or native window and sends no input. Evidence remains private.
`verify_vocabulary.py` compares sorted vocabulary facts with the verified private schema files. The repository holds
the canonicalization code and hashes, never properties-file contents. Its difference guard is mutation-tested in
memory. Canonical bytes are ordinal key, NUL, XML read name and LF, encoded as UTF-8 without a BOM.

`verify_pose_geometry.py` executes unchanged `Mascot` getters and `apply()` from the verified rebuilt JAR.
Its private test substitutes only the sound-setting dependency of `Main`, allocates a mascot without its native
constructor and attaches an in-memory JPanel window. No display windows or native input are created. It checks
startup nulls, a visible pose, two image-less anchor moves with frozen bounds, reappearance, and the actual
feasibility state formatter. It also constructs the unchanged virtual panel, without its GUI environment, and
checks that a first applied image-less pose retains its initial zero bounds. Fresh-build mutants recompute
the hidden origin, invent an initial empty-window origin or a hidden anchor, and
disconnect either of the writer's geometry fields; each must fail its named witness. This is a focused geometry proof,
not an execution of the manager, scenario environment or complete shared-protocol writer.
Initial zero bounds are specific to the virtual panel. Native Windows and generic windows inherit JWindow's
initial location, which JDK 8 offsets by the selected graphics configuration's screen origin and top/left screen
insets (java/awt/Window.java:503-510). Their initial size is zero, but their initial origin need not be zero.
The Mac window delegates to the generic implementation. This headless proof does not measure native windows.

`Failures` requires current successful runtime probes first. It launches isolated negative probes with an
incorrect expected offset and an incorrect runtime fingerprint, requiring named failures and exited child
processes. It also supplies stale tool metadata and an altered state trace whose old stored signature is left
unchanged. Those cases must fail the real evidence checker.

Upstream archives, extracted sources, compiler outputs, generated art, traces and launch logs stay in private
cache/evidence folders for review and the next parity phase. They are not repository inputs or release payloads.
