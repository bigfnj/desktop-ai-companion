# Shimeji action parity status

No action family has a supported parity verdict yet. Support requires controlled oracle and host traces judged
under the frozen parity contract maintained by the parity lane (tools/ShimejiParity/CONTRACT.md after integration).
A completed host run alone does not establish it.

The test-only command emits the tick subset described below from the ordered source IR:

```text
ShimejiConvert.exe paritypack <source-pack> <new-output-directory> [main-image-set]
```

Each image-set directory gets `animations.xml` and `parity-map.json`. The map preserves source configuration
locations, separate next-reference behaviours, frame image paths, effective cached anchors and image sizes. Root choices
retain declaration order and zero frequencies. The output directory must be new and outside the source pack.
Cross-member output and source-hidden members require the caller to name the main image set. An explicit main
also requests pack metadata when no member references another. Such output declares packs and gives every member
one shared pack id derived from source configuration and consumed sprite bytes, rather than generated XML.
Other members are forms, except the source chooser's hidden members (a leading dot, or unused ignoring case),
which are hidden helpers. It omits own-set member defaults and keeps a binding when dropping it would expose a
different inherited member. This prototype is for parity testing; it is not the Studio installation API.

Studio can prepare the same subset through the production, in-memory engine API:

```csharp
string error;
PackConversionResult result = ShimejiEngine.ConvertPack(extractedPackRoot,
    new PackConversionOptions { MainMember = "Main", IncludeParityMaps = true }, out error);
```

`MainMember` is an exact source image-set key. Optional `Members` metadata selects each member's role and
visibility; `Startup` supplies the initial member and behaviour. Egg roles and startup are caller choices.
Every member receives pack metadata, including a pack with one member. Long member keys remain exact while
the generated display petname is bounded by the host's shorter display-name limit.

Keep the extracted source unchanged during the call, which writes no files. Each member report carries a parsed
`Root`, canonical `EmittedXml`, optional
`ParityMapJson`, residue and graph diagnostics, `Valid`, `RoundTrips`, and its disposition. It checks the real
document validator with the tick-format features enabled, deep-copy and serialization preservation, then the
shared pack validator. The classic validator entry point keeps its existing feature gate. Missing member
references produce warnings and retain the runtime's current-type fallback. Unreachable nodes remain in the
document and graph report.

The caller must require the whole result's `Accepted` before saving or installing any member. Successful
members can remain as diagnostic previews when a sibling is refused; a refused member exposes no document,
XML or parity map. If source preparation refuses any member, the result's `PackId` is null. Returned roots are
independent mutable copies; `EmittedXml` records the validated result before any caller edits. The API is linked
into Studio, but the import UI and its installation transaction are separate work.

| Source action | Converter stage | Supported | Evidence available |
|---|---|---|---|
| Stay | Literal lowering | pending | Historical packet exact: S-slice-stand-walk-01 |
| Move | Literal lowering | pending | Historical packet exact: S-slice-stand-walk-01 |
| Animate | Literal lowering | pending | Synthetic converter checks |
| Sequence, Select | Literal lowering | pending | Synthetic graph and binding checks |
| Embedded Breed | Literal lowering | pending | Historical packet exact: S-slice-breed-01; two nonexact edge cases below |
| Embedded Transform | Literal lowering | pending | Historical packet exact: S-slice-transform-01; blank-behavior case nonexact below |
| Embedded Fall, Dragged | lowered; historical packet exact | pending | S-fall-params-01, S-fall-sweep-01, S-fall-wall-01, S-input-drag-01 |
| Embedded Jump | lowered; historical packet comparison below | pending | S-jump-arc-01, S-jump-flat-01, S-jump-reaim-01 |
| Embedded MoveWithTurn | lowered; historical packet comparison below | pending | S-move-withturn-01 |
| Embedded Regist | lowered; historical packet comparison below | pending | S-input-regist-02 |
| Embedded BreedMove, BreedJump, BreedFall | lowered; historical packet comparison below | pending | S-breedmove-interval-01, S-breedmove-interval-02, S-breedmove-turn-01, S-breedjump-interval-01, S-breedjump-interval-02, S-breedfall-interval-01, S-breedfall-interval-02 |
| Embedded ScanMove | lowered; historical packet comparison below | pending | S-scan-first-01, S-scan-loss-01 |
| Embedded ScanInteract | lowered; historical packet comparison below | pending | S-scan-reacquire-01 |
| Embedded Interact | lowered; historical packet exact, including interaction after arrival | pending | S-interact-arrive-01, S-interact-shared-01 |
| Embedded WalkWithIE | lowered; historical packet comparison below | pending | S-window-carry-on-01, S-window-carry-off-01, S-window-throwing-off-01 |
| Embedded ThrowIE | lowered; historical packet comparison below | pending | S-window-throw-on-01, S-window-throw-off-01 |
| Embedded MinimizeIE | lowered; historical packet comparison below | pending | S-window-minimize-on-01, S-window-minimize-off-01 |
| Animation Hotspot (shape, origin, size, behaviour) | lowered; historical packet comparison below | pending | S-input-hotspot-01 |
| Literal Affordance (`<offers>`) | lowered; historical packet comparison below | pending | S-scan-first-01, S-scan-loss-01, S-scan-reacquire-01 |
| Toggleable behaviours (header `<toggles>`, behaviour `toggle`) | lowered; synthetic converter checks only | pending | No paired trace comparison in this packet |
| Embedded Look, Offset, BringToFront | lowered; synthetic converter checks only | pending | S-lower-instant-01 exists; no host trace, not compared |
| Embedded Turn, Mute, SelfDestruct, ScanJump, ComplexMove, ComplexJump, FallWithIE, JumpWithIE, Broadcast, BroadcastStay, BroadcastMove, BroadcastJump | lowered; synthetic converter checks only | pending | No paired trace comparison in this packet |
| `${}` / `#{}` parameters and bindings | lowered; historical packet comparison below | pending | S-vars-cache-01 |
| Scripted action Condition (cached parameter, guard when) | lowered; historical packet comparison below | pending | S-vars-transform-01 |
| Scripted Animation Condition (cached parameter, variant when) | lowered; synthetic converter checks only | pending | No paired trace comparison in this packet |
| Scripted Behavior and `<Condition>` selection conditions | lowered; synthetic converter checks only | pending | No paired trace comparison in this packet |

The 2026-11-07 comparison used all 58 scenarios with historical `host-cw` packets. Fresh oracle runs used
JDK 1.8.0_504, the pinned source archive, and the private patched oracle artifact below. All 58 runs completed;
document witnesses and the trace-on/off observation proof passed. The comparer reported 52 `exact` and six `fail`.
Every named paired scenario in the table was compared. Four raw failures are manually classified policy divergences
and two are real open mismatches under the frozen v1 contract. The comparer has no mechanical policy-verdict branch,
so raw verdicts remain 52 exact and six fail; reviewed contract classifications are 52 exact, four policy, two fail:

| Scenario | Raw verdict | Reviewed classification | Observed difference |
|---|---|---|---|
| S-breed-blankbehavior-01 | fail: event-mismatch | OPEN: real v1 mismatch | Error-message wording at tick 10; both then refuse attempt 1 with null child and reason `error`. |
| S-transform-blankbehavior-01 | fail: event-mismatch | OPEN: real v1 mismatch | Error-message wording at tick 6 after the same transform to Beta with an empty behavior. |
| S-cap-at-01 | fail: event-mismatch | policy:birth-cap | At tick 6, attempt 16, oracle initializes a child while host refuses with reason `cap`. Host admits 15 children plus the parent; attempts 16 and 17 are refused. |
| S-window-carry-off-01 | fail: missing-event | policy:window-off-plays | Oracle records 70 window moves; host suppresses them and reaches its next behavior at tick 70. |
| S-window-minimize-off-01 | fail: missing-event | policy:window-off-plays | Oracle minimizes at tick 6; host suppresses it. Both select the next behavior at tick 7. |
| S-window-throw-off-01 | fail: missing-event | policy:window-off-plays | Oracle records ten window moves; host suppresses them and selects its next behavior at tick 11. |

WindowActions is host-only; with it off and Throwing on, the host plays the base action and suppresses window
effects (`TickWindowFamily.cs`, HasNext and the window-effect methods). Throwing is the source switch retained by the host;
the real host pins it on (HostTickSettings.Get). The oracle's WindowActions property is inert. S-window-throwing-off-01
sets the separate Throwing switch off, ends Carry during initialization, and compares exact. The three off-window
cases have identical state lines and only missing window effects, matching policy:window-off-plays. S-cap-below-01
is exact; S-cap-at-01 witnesses policy:birth-cap. CONTRACT v1.1 must correct the old policy's source-side wording.

The two OPEN blank-behavior cases differ in error.message only; every other event and state line matches. Frozen
v1 section 3 defines that payload and section 6 compares it exactly; no policy exempts its wording. The coordinator
will file both cases as open backlog entries for a separately reviewed error-message rule or named policy. Neither
this page nor the v1.1 proposal exempts them. Mechanical policy-shape checking is a separate open comparer follow-up,
since a raw fail alone would not distinguish a policy regression from today's intended difference.

Each Interact case records 58 `interact` events over ticks 3 through 31 in both paired traces. The arrival case adds
the third companion to the targets at tick 31; the shared-target case preserves manager order and excludes the
out-of-range fourth companion. Both cases enter Huddled at tick 31. This packet exercises interaction beyond
initialization; the older tick-0 termination note does not describe these paired traces.

These compare packs lowered by the historical 1.10.0 converter and executed by the historical 1.10.0 host. They do
not verify this branch's map/2 lowering or establish a supported-family verdict. Current map/2 host traces await the
N-shimeji-1106-13 host half, which must integrate with the producer and converter readers.
Every paired scenario and scenario-specific pack hash matched. The host header's `commit` is a version string,
not a source commit; the executable was neither located nor rerun, so the comparer reports its witness as
`shape-only`. The oracle witness is `artifact-bound`. The input snapshot was generated at 184fbe0; it retains
the original S-slice-breed-01 description to preserve these scenario hashes.

| Evidence | SHA-256 |
|---|---|
| Pinned source archive | cf9e72f74d93f45e9f70a4f69cb1e56ffaedd38af2c5e5413482944f78faccff |
| Executed oracle artifact | 33eae68b85ceec6c0a456539d0d91c3434a2a4781322755cc4da10628fcf9d5f |
| Historical host executable, header claim only | 0f7785bb1c0d4caf58c9491a74c00a0315c737d5ded050edf1fcef9083f582ca |
| Historical converter code, header claim only | d98778e849ad6534f599de14e38f2b113c46a26709a47ffefbefa338cfbb636f |
| Historical engine code, header claim only | f6c046a9a30b5da226306f679ab4dc05550fc617558930f0a8099386df3c94bd |
| Private oracle results (`oracle-selected/v1-da86559a/results.json`) | 313659dee2435fe200e8c7ea09e405b490c7a519a0035a43b57ff4d00dde75ed |
| Private per-case comparison record (`historical-comparison-results.json`) | 73da9250c00d229103f592ceb530cff3e1e96101e38da9682419132ecdae97af |
| Reviewed classifications (`reviewed-classifications.json`) | a423f9080eaeb08239e31284403de5de2eb778317d635eba11c068721cebf779 |

The two records, all 58 oracle and host traces, original synthetic inputs, executed oracle JAR and original tool
bytes are retained in the durable private research packet
`D:/.ai-work/shimeji-catalog/research/parity/oracle-v1/codex-2026-11-07`. They have no automatic TEMP cleanup.
`retention-manifest.json` records file hashes and relocates paths: original records retain execution-time paths,
with copies under `oracle-selected/v1-da86559a`, `host-cw` and `scenarios-184fbe0`. Its SHA-256 is
`e42bb10b14fbf04cb77456d1eb7303cf8a49e7ebd4a1b90f828b32197833824d`. Retire it only with the campaign record.

Twelve generated scenarios have no host-cw trace and were not compared or run on the oracle in this packet:
S-conf-tiers-01, S-lower-info-01, S-lower-instant-01, S-lower-japanese-01, S-lower-japanese-02, S-lower-japanese-03,
S-lower-nan-left-01, S-lower-nested-01, S-lower-sound-01, S-lower-spelling-01, S-lower-vars-01, S-select-missing-01.

The command targets the headless parity runner. It does not infer egg roles or startup metadata and does not
replace the classic conversion commands or Studio import. A `${...}` or `#{...}` value lowers to its body with
cache `action` or `frame` where the source reads it as a script: an action parameter, a reference or behaviour
binding, a scripted action Condition (a cached parameter the guard's `when` names), a scripted Animation
Condition (a cached `AnimationConditionN` parameter the variant's `when` names) and a selection condition (the
edge's `when`, uncached). Every body is parsed with the host's own tick grammar (`SafeScript`) in the scope
the validator gives it, and one that does not parse is refused with the attribute and the parser's message; a
scripted Affordance is refused, and so is a script-shaped value in any attribute the source reads literally.
Every embedded class of the host's closed list is lowered, with its parameters by the names the host's family
files read; the member declares `lifecycle`, `social` and `windows` for the classes the validator gates by
them, and ComplexMove and ComplexJump declare theirs by their Characteristics, split on commas exactly.
A Hotspot becomes a `<hotspot>` of its variant (Rectangle as `rect`, Ellipse as `ellipse`, in any case, as the
source compares them ignoring case; any other shape refused; a behaviour must be a root behaviour). A literal
Affordance becomes the action's `<offers>` and stays
a parameter, which the scan classes read; an Affordance binding onto an action that offers its own literal is
refused, since the binding could not change the offer. A Toggleable behaviour becomes a header toggle whose
id and label are its name (the source keys a disabled behaviour by name), and each Behavior or
BehaviorReference that says Toggleable itself names it; a name over 64 characters is refused. A behaviour
named exactly Fall, Thrown or Dragged never becomes a toggle, whatever it says, as the source's builder
overrides the flag for those three.
A Japanese-vocabulary document's enumerated values are read through its own schema: its Type and BorderType
values map to the English ones the host reads, and one with no mapping, or a scripted BorderType, is refused by
name. A member whose behaviour document is Japanese (with no information document after it) enters its
Japanese engine behaviours on a fall, a drag and a release, so those three take the host's names Fall, Dragged
and Thrown wherever a literal names them; an English Fall beside them, a scripted behaviour name in such a
member, a TargetBehavior naming one, and a literal engine name in a pack that mixes the two vocabularies are
refused.
Attributes on a configuration document's root element, and direct text in any configuration element, are ignored,
as the source never reads them; so is a Class on an action that is not Embedded. An instant
embedded action (Look, Offset, Mute, BringToFront) written with no Animation becomes an act with no variants, as
the source builds those classes without animations; any other posed action without an Animation is refused.
An anonymous nested action becomes a node named after its place, `<parent>/<position>`; a name that collides
with another action's is refused. An attribute the schema does not name on an action, a reference or a
behaviour is a variable of the source's action map: it is carried under its own name when it is a plain
identifier the host does not read as a parameter of its own and some script of the member names it, and the
scripts of that map see it; one nothing reads is refused, as before.
`engine` and `context` follow the custom-variable rules above: declared or incoming scalar bindings are carried
when a script of the member names them. `arguments` remains reserved rather than carried.
The pinned-runtime `DraftBridgeProbe` distinguishes unbound objects from explicit bindings: numeric `engine`
and `context` bindings take effect, while `arguments` retains its global object even when bound.
The converter refuses unbound uses of all three names; it does not model their global objects.
A script's zero-argument JavaBean getter call
(`getScreen()`, `isLookRight()`) is written as the property it reads, which Nashorn evaluates with the same call.
`Math.random` named without a call is NaN in the source, with no draw; where it is the value of a Move's or a Jump's
TargetX or TargetY, or of a ternary branch that is that value, it is written as 0, the int those readers make of
NaN. NaN anywhere else (a comparison, a condition, text, a call other than a NaN-propagating Math function, a part
holding a draw, any other reader) is refused by name. A Behavior's children other than its
NextBehaviorList elements (a NextBehavior among them) are ignored, as 1.0.23 never reads them, and so is any child
of a behaviour list other than a Condition and a Behavior (root list) or BehaviorReference (next list).
A pose's Sound becomes a named WAV clip, its id the Sound text and its bytes unchanged, resolved as the source does
(sound/, sound/<set>/, img/<set>/sound/), with the pose's Volume in dB; the member declares 'wav'. A missing clip,
MP3 bytes, a WAVE the host's rule rejects, a Volume outside -80 to 6.02 dB and a clip or document over the host's
audio limits are refused by name.
It refuses unsupported attributes and
classes, and constants. A next reference without a root definition is NOT lowered exactly. By default it refuses
(TICK_NEXT_MISSING, naming the image set, the behaviour and the entry). Recovery is OPT-IN (decision 64): only a caller
that selects it, for that call (PackConversionOptions.RecoverMissingNext, paritypack --recover-missing-next, or Pet
Studio's "Import with recovery" after a strict refusal), gets the entry dropped with a warning, an opt-in POLICY
DIVERGENCE recorded below (`policy:next-missing-dropped`). One whose condition, its own or an enclosing Condition's,
is a script refuses either way (TICK_NEXT_MISSING_SCRIPTED). A member refused during preparation keeps none of the
warnings it gathered before its refusal (review Q2); it is reported as refused, with that refusal. An information document (info.xml) is
accepted when it holds only <Information> (chooser and credits metadata, which no mascot reads); a Constant, an
ActionList or a BehaviourList in it is refused.
Bindings across differing document schemas are refused until target-schema resolution is implemented.
An image set with no configuration anywhere the source looks (its own conf/<set>/ or img/<set>/conf/, or the pack's
shared conf/ default) is skipped with a warning (TICK_MEMBER_SKIPPED), as the source drops such a set and loads the
others (decision 69, a source-compatible mapping); the pack refuses only if it is the main or the startup member
(TICK_PACK_MAIN_SKIPPED, TICK_PACK_STARTUP_SKIPPED), and a Breed or Transform naming it names a missing member.

Recorded divergences from the source (each a correction of a source defect, never a reproduction of it):

| policy | source | lowering |
|---|---|---|
| `policy:next-missing-dropped` (decision 55) | A next-list entry naming no root behaviour is a behaviour of its own and can be selected: its Frequency is part of every draw over that list (Configuration.java:216-257), and its behaviour runs. A drag or a fall replaces it with no crash; only when it ENDS (UserBehavior.java:286) does Configuration.java:210-211 dereference the missing root builder, and the NullPointerException ends the tick thread (Mascot.java:545-554, Manager.java:88-108), stopping every companion. | Opt-in (decision 64): without the caller's choice the entry refuses (TICK_NEXT_MISSING). With it, the entry is dropped, with a warning naming it and its behaviour (each one structured residue item, PreparedMember.Dropped; the pack API files each, once, under its member's Residue.Dropped, so it counts in the member's residue total and residue.txt lists it under "Dropped entirely", and adds one member-prefixed warning to PackConversionResult.Warnings, which Pet Studio shows; the paritypack verb prints each as a WARNING line). It is never selected, its behaviour never runs and its weight leaves every draw over the list, so the lowering differs from the source from the first draw that could pick it, not only at the crash. A list left empty runs as the source runs an empty list (Configuration.java:208-245): with Add false the companion drops in and falls, with Add true the root list stays. An entry whose own or enclosing condition is a script stays refused under both settings. Not exact: an opt-in policy divergence. |

Composite activation cycles, depth above 32 and expansion above 4096 actions are refused before output is written.

Sheet cells use the maximum source image width and height, with each image copied at its cell's top-left. The
cells form a near-square grid. When that grid breaks one of the host's sheet limits (4096 pixels a side, 256 tiles an
axis, 1024 tiles), the sheet takes, of the grids that satisfy all of them, the one with the fewest cells, then the
fewest columns; a member that no grid fits refuses by name (TICK_SHEET_NO_GRID). Every member the file-writing API and
paritypack write has first passed the host's validator (TICK_OUTPUT_INVALID otherwise).
A configuration file is named as the pack spells it on disk; two directory entries that differ only in case refuse by
name (SOURCE_PATH_AMBIGUOUS). That refusal can be stricter than the source on a case-sensitive file system; it refuses
0 corpus packs, so a finer rule is DECLINED-MEASURED.
Every visible pose carries its image size and effective anchor. Explicit right-facing images carry their own
size and frame; their pixels are not mirrored. The image-pair cache retains the first loaded anchor for repeated
left/right path pairs, including pairs in unused actions. The parity map records that effective anchor so it
agrees with every pose using the frame; the source IR retains each pose's authored value.
A pose of Duration 0 is left out of its variant: the source never makes it the current pose (Animation.getPoseAt,
Animation.java:54-67) and it adds nothing to the animation's length (:70-78). Its image, anchor and clip are still
checked as the source loads them. The converter's `shimeji-parity-map/2` records each retained pose's source index
and exact Pose refKey in `poses`, keyed by emitted action node, variant and pose positions. A skipped pose still
consumes its source index; omitted animations also preserve the final emitted variant positions. Image-less poses
retain their origins, while permanently false synthetic poses supply no source entry. The host reads map/2 only,
binds each emitted pose to its entry and traces that source index (src/dotNet/Shimeji/ParityRun.cs, ParityMap.BindPoses
and ParityTraceWriter.State), under CONTRACT.md v1.1 section 9. An Animation whose every pose has Duration 0 (decisions 68, review N2): in an Animate or an Interact
action, whose hasNext is false at once (Animate.java:38-42, Interact.java:34-36), the action ends before its first
step, written as one variant that never holds (`when="false"`, one image-less pose), which the host's Animate also
ends on at once. Under a Sequence or Select, in an instant class, or after an unconditional Animation, the source
never plays it, and it is left out. Only a class that steps the animation (Stay, Move and the others) can play it,
and there the source divides by the length 0 and stops every companion, so it refuses (TICK_POSE_ZERO_LENGTH); in an
Animate or Interact where a condition can select it (TICK_POSE_ZERO_LENGTH_SELECTABLE), it refuses too.
Distinct source image paths keep separate frame identities. Pixel copying preserves alpha and ignores sprite DPI;
images are not resized. A member-local ImageRight must start with a separator. Right-only poses are refused
because they can depend on an earlier cache entry. Java hexadecimal floating literals are refused until the
host's literal parser can preserve their meaning.

Synthetic self-tests run through the emitter suite and `selftest pack-conversion`. Private slice traces and source archives remain
outside the repository. Controlled oracle traces and class-specific branch scenarios are required before any
row above changes to supported.
