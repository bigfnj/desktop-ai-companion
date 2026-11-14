# Shimeji parity contract Mute proposal (map/3; v1.4 if revision H signs as v1.3)

Status: DRAFT, NOT ADOPTED OR SIGNED. This separate proposal does not edit the adopted CONTRACT.md or the frozen revision H packet. It is sequenced after H's proposed v1.3 adoption; if map/3 must be adopted before H is signed, H needs a jointly reviewed rebase; adopting this text never implies one.

Base: b107727cb18914c9909417d4f6e3ae32c0f48dad. Review input: Claude's REVIEW-mute-wire.md, SHA-256 a20cd2e74d66e5bce4a165456bfa0bf8ef697118273ae191db5f79b6d17f9b0f. Exact normative amendments and directed controls are set out below. No producer behavior, fresh trace evidence or backlog closure is established by this file.

## Adoption boundary

This is concrete text proposed for joint exact-text review after amendments P1-A through P1-G, P2-A through P2-E, FC-1 through FC-6 and P3-A through P3-E. It is not an adoption record. CONTRACT.md remains v1.2 until the separate adoptions are applied. Revision H and REGISTRY.draft.json remain unchanged and unsigned by this proposal.

Claude's AgentBroker message c7a2e94f-3b16-4d58-9e0a-6f1d8b4c2e73 at 2026-11-12 22:59:37Z agreed Q1 and Q2, without signing this exact text. Q1 amends P2-B by requiring passive observation of every completed read, including a null read with Sounds off and no public event. Q2 replaces the review's faulted P2-C row (REVIEW-mute-wire.md:78) with the orthogonal fault rule at the end of P2-C: retain the actual result and branch, including a non-null result after a nested reported fault. Literal MUTE_VALUE_NOT_STRING (NOT_STRING below) depends on sourceKind; evaluated NOT_STRING depends on the captured runtime kind.

The latest agreed m4 clarification supersedes the written review's earlier recommendation: a provenance-defect read records only provenance, with neither MUTE_VALUE_FAULTED nor MUTE_VALUE_NOT_STRING information; engine fault events remain unchanged. Missing OwnerType or owner map alone is not a defect for an absent selection. These decisions are reflected below and remain subject to exact-text review.

All normative statements below describe proposed behavior and required verification, not completed evidence. No runtime witness or signature is claimed by this drafting task.

## Contract placement and explicit exceptions

Only after revision H is adopted, apply the signed text to CONTRACT.md as follows, marking changed passages (v1.4), with the actual version chosen after H. The new signature record binds this proposal's hash and each pasted passage's hash, following the v1.2 record. Passages are pasted verbatim. The only permitted editorial change is deleting a conditional on H's adoption ('once H is adopted', 'where H is adopted', 'where section 11 is adopted'), and the signature record lists each deletion. This placement recipe edits neither CONTRACT.md nor H now.

- Set the title/version and extend the preamble qualification of 'Sections 1 to 9 are v1.1's, unchanged' to include v1.4's section 2, 3 and 9 edits. Beside the prior exception paragraphs add: '(v1.4) Version 1.4 requires shimeji-parity-map/3 (section 9) and adds Mute read, observation and host failure-channel obligations to 10.8. Explicit exceptions, each retaining its schema version: trace/2 is retained and mute.clip in trace/2 has the source-intent meaning of 9.2; scenario/1 and scenario/2 where section 11 is adopted are retained with fields unchanged. This version defines no trace/1 Mute meaning. Pre-v1.4 host trace/2 artifacts retain their archived meaning and are not regraded. Map/2 retention is superseded prospectively for new host parity runs only.'
- Section 2's script-site paragraph says: 'Map/2 and map/3 do not yet let the host write this form (section 9).'
- Section 3's mute row gains: '(v1.4) In trace/2, clip has the meaning of 9.2.'
- Replace section 9's map/2-only paragraph with '(v1.4) A new host parity run reads shimeji-parity-map/3 only (9.1).' New 9.1 holds P1-B through P1-G; new 9.2 holds P3-A. Where v1.3 states that map/2 wire fields are unchanged, add '(v1.4) Superseded for new host parity runs by map/3 (9.1).' The script-site limitation names both map/2 and map/3. The payload list explicitly adds '(v1.4) mute.clip'.
- Section 10.1 gains the next numbered item: '(v1.4) mute.clip has the source-intent meaning of 9.2, for trace/2 only; a pre-v1.4 host trace/2 artifact retains its archived meaning.' Change the following sentence to 'Everything else in sections 1 to 9, as amended, applies to trace/2 unchanged.'
- Section 10.8's host paragraph gains P2-A through P2-E and FC-1 through FC-6; its oracle paragraph gains P3-B; its comparer paragraph gains P3-D's exact self-test obligation.
- 10.8's 'Both producers' instrumentation sentence becomes: 'Instrumentation must preserve existing exception type/catch behavior and state-write order, except the explicitly agreed RNG continuation rule and (v1.4) the host's private observation-only fault counter (P2-A) and its observation catches (FC-2), which also cover the existing Mute writer.'
- Section 10.9 gains P3-C, P3-D's fixture, no-regrade and no-policy sentences, and P3-E, each marked (v1.4).
- The signed proposal is kept beside CONTRACT.md unchanged and is named in the v1.4 preamble paragraph. Its Required directed controls table and its Ownership and review evidence section stay binding acceptance obligations by reference, as v1.2 keeps its consolidated text and control matrix beside CONTRACT.md. Pasted passages keep their P1, P2, FC and P3 labels as run-in headings, and a control id inside a pasted passage refers to that kept table.
- Put the v1.4 signature record after H's eventual v1.3 record. Preserve historical signature records and archive semantics.

## Proposed section 9 map/3 replacement

### P1-A Placement and sequencing

Map/3 is a contract version step with a section 9 edit, adopted as the version after H's signed version, v1.4 if H signs as v1.3. It supersedes v1.3's map/2 retention prospectively for new host parity runs only. Section 11's registry, admission order, slots and models are unchanged because admission binds scenario and source-pack bytes, never converted map bytes. If map/3 must be adopted before H is signed, H needs a jointly reviewed rebase; adopting this text never implies one.

Every reference here to section 11, scenario/2, admission, slots, registry or registered bytes applies only once H is adopted as section 11. Before then this text imposes nothing on H, REGISTRY.draft.json or its finalization. P3-E is checked against draft registry rows and the five calibration digests without treating them as registered.

### P1-B Row shape

A map/3 object has `schema` exactly `shimeji-parity-map/3`, retains every other map/2 property with its map/2 meaning, and adds required top-level `soundParameters`, an array. It is empty when the converted member has no emitted Sound parameter or binding.

Each row has exactly the following properties. Field names and string comparisons are ordinal and case-sensitive.

| Property | Meaning and type |
|---|---|
| `kind` | String, exactly `action`, `behaviour` or `child` |
| `nodeId` | Canonical nonnegative JSON integer within Int32, read from its raw token; the emitted owning node's id |
| `childIndex` | Present for `child` only; canonical nonnegative JSON integer within Int32, read from its raw token; position in final emitted child order |
| `sourceRefKey` | String; the source element defining the parameter or binding, under P1-D |
| `emittedValue` | String; exact decoded `value` attribute of the emitted Sound param or bind, including a lowered script body rather than its original wrapper |
| `emittedCache` | String, exactly `none`, `action` or `frame`; exact emitted `cache` attribute |
| `mode` | String, exactly `literal` or `evaluated` |
| `sourceValue` | Required for `literal` only, forbidden for `evaluated`; string containing the exact decoded authored Sound attribute, including empty or unchanged text |
| `sourceKind` | Required for `literal` only, forbidden for `evaluated`; string, exactly `string`, `number` or `boolean`, computed under P1-C |

Canonical integer tokens have no negative sign, negative zero, fraction, exponent, whitespace or leading zero except `0` itself. A literal row has `emittedCache` `none`. An evaluated row has `emittedCache` `action` or `frame`. An evaluated definition does not require a non-null compiled Program: an empty evaluated body can compile to no program.

### P1-C Source constant classification

Compute `sourceKind` from `sourceValue` without evaluating a script. The script-envelope check uses the untrimmed decoded sourceValue:

0. A literal row whose sourceValue has length at least 3, ends with `}` and starts with `${` or `#{` is a source script, not a constant; refuse the map as a source-kind disagreement.

1. Exactly `true` or `false`, before any trimming or case conversion, is `boolean`.
2. Otherwise, a value accepted by Java's `Double.parseDouble` after Java's `String.trim` is `number`.
3. Everything else is `string`, including `null` and the empty string.

Java trim removes only U+0000 through U+0020 at the ends. It does not remove U+0085, U+00A0, U+2003 or U+3000. Boolean equality is exact before trimming: padded `true` is not classified as a Boolean by this rule. The Java number language includes its accepted signed decimal and hexadecimal floating forms, NaN and Infinity, and the accepted f/F/d/D suffix forms; a suffix is not accepted merely because one of those letters appears. Java acceptance, rather than the host's general `.NET string.Trim` or `double.TryParse` behavior, defines the result.

The converter records the kind and the host independently recomputes it. A disagreement refuses the map. `TickType.ParseLiteral` or its current `TryParseJavaDouble` cannot be reused unmodified for this classification: their number path uses .NET trim.

Literal restoration is qualified by `sourceKind` `string` alone. Neither emitted text nor the compiled host kind qualifies or disqualifies it. Source String literals `/1`, `/true`, `TRUE`, `False`, `/01` and `/0X1P1` can become numeric or Boolean host constants; their exact source text is still restored for raw intent. Actual TickSound arguments and effects remain unchanged and outside that restoration claim.

### P1-D Host binding

Each row binds to the compiled definition under the ordinal parameter name `Sound` at this location:

| `kind` | Compiled location | Required source map entry |
|---|---|---|
| `action` | `ActionsById[nodeId].Params["Sound"]` | `actions[nodeId]` |
| `behaviour` | `BehavioursById[nodeId].Binds["Sound"]` | `behaviours[nodeId]` |
| `child` | `ActionsById[nodeId].Children[childIndex].Binds["Sound"]` | `children["nodeId:childIndex"]` |

Hold the binding in a map-owned dictionary keyed by `TickParamDef` reference identity, never value equality. `emittedValue` and `emittedCache` must equal the actual validated XML node's decoded `value` and `cache` attributes as exact strings. The `cache` attribute must be present. Parsed constant-value equality is insufficient because distinct XML strings can compile to the same Number. Reading a script's stored text is passive, but it never substitutes for the XML attribute comparison above; evaluating a script to validate a map is forbidden.

`mode` matches the compiled definition: `literal` exactly when its cache is none; otherwise `evaluated`. `sourceRefKey` equals the required action, behaviour or child map entry at that location, and its first pipe-separated field equals the map's `imageSet`. An absent required entry refuses the map, including an absent `children` entry for a child with a Sound binding. `sourceRefKey` need not be unique across rows.

### P1-E Coverage and refusals

Rows form a bijection with every compiled Sound definition in the three container kinds, including unchanged, unselected, unreachable and non-Mute sites. Inventory by node id includes definitions shadowed by name lookup. No row represents absent Sound.

Before opening the trace, refuse with exit 2 and no trace for each of the following:

- `soundParameters` absent or not an array, or a row that is not an object;
- repeated, unknown or missing row properties;
- `childIndex` present on a non-child row or absent on a child row;
- `sourceValue` or `sourceKind` present on an evaluated row or absent on a literal row;
- any required string field that is not a string;
- a noncanonical, negative or out-of-range `nodeId` or `childIndex`;
- `kind`, `mode`, `emittedCache` or `sourceKind` outside its closed set, or a mode/cache inconsistency;
- duplicate locations, counting `(kind, nodeId, childIndex when applicable)`;
- a location naming no compiled container or one without Sound, which is surplus;
- a compiled Sound definition consumed by no row, which is missing;
- emitted value/cache, source refKey, image-set or source-kind disagreement;
- an unknown top-level map/3 property.

The allowed top-level map/3 properties are the map/2 properties `schema`, `imageSet`, `frames`, `actions`, `children`, `behaviours`, optional `sourceNames`, and `poses`, plus `soundParameters`. `schema` must be exactly `shimeji-parity-map/3`; existing map/2 requirements govern the presence and contents of the other retained properties. Unknown-property refusal is new for map/3; duplicate-root checks already exist.

### P1-F Retirement and readers

A new host parity run reads map/3 only. Refuse map/1 or map/2 before opening a trace, exit 2 and no trace, with exactly the following diagnostic, where N is the refused file's map version:

`the map is shimeji-parity-map/N; regenerate it with the current converter (map/3 required)`

For an admitted scenario/2 candidate this is a LoadPets refusal after scenario and pack admission, once H is adopted. Regeneration runs the converter on the source pack into a new output folder; hand-editing a map/2 file, including changing its schema string, is not migration. Archived traces, verdicts, revision H's P2 calibration packet and other frozen packets retain their map/2 manifests and are reproducible only with their original build. Every map reader, including validation probes and self-test readers, moves in one integration change; no map reader accepts two map versions. The comparer's grading code is unchanged; P3-D adds one pair to compare.py's self_test, changing its bytes and hash.

### P1-G Product neutrality

Emitted animations.xml is byte-identical to the base converter's output for every existing converter self-test input and every make_packs pack that the base converter converts; without an injected defect the new converter refuses none of them. Sound provenance adds no PackConversion refusal when IncludeParityMaps is false. A converter provenance defect is confined to conversions requesting maps: the public API then returns a refused member naming the defect, with no EmittedXml, and ConvertPack refuses before writing any output. No-map conversion, underlying XML generation and TickSound arguments remain unchanged.

The existing TICK_SOUND_MUTE_OBSERVED conversion refusal remains. Controls whose input reads Sound in a script use members with no normalizing Mute literal; normalizing-literal controls use members whose scripts do not read Sound. C15 separately proves the no-map path and the actual public API's map-defect disposition.

## Proposed Mute read and observer rules

### P2-A Single read and passive capture

TickMute.Apply reads Sound through one helper preserving EvalString's engine steps: Present, followed by one Read when present. Alongside the unchanged engine string, retain from that same read the selection (absent when Present is false, local, or the exact winning TickParamDef from merged own-then-incoming definitions), and the ScriptValue kind before ToDisplayString.

Also retain whether a fault was reported through the reading action's own script context between the start of Present and the end of Read. Use a private observation-only counter for faults reported through that context; the host chooses its location. That counter is the one permitted added state write and changes no engine-visible state. Faults reported before Present, including during Condition, never mark a later read as faulted. Faults reported through other contexts do not mark this read as faulted. Snapshot operations themselves are observation code; a snapshot exception is handled under FC-2, without skipping or catching the engine's Present/Read.

Selection uses a passive view of the same TickVars state, no second Get or Has. The helper adds no evaluation, cache write, settings read, draw, script call site, Clip call or TickSound call. Observer delivery follows P2-B; no second callback is added on a branch that already delivers Muted. Engine-side observation code may consult one test-only injection point per FC-2 site. It is null in every product and parity build unless a host self-test installs it, does no engine work, and exists only so C18 can raise an exception inside that site. Cached and fresh reads keep their existing cache behavior. A Read that throws is not a completed read and propagates exactly as before; no observation catch surrounds engine Present/Read.

### P2-B Activation, owner and per-read delivery

The Mute action object is the activation. Use its retained `OwnerType` for provenance, never the companion's current Type or PoseType. Correlation is per read, not per object: a looping Sequence can reinitialize the same child object.

Each completed Mute read reaches the observer exactly once, correlated with its synchronous emission when there is one. The existing named and null-with-Sounds-on Muted callbacks carry the read record on event branches; ITickObserver.MuteReadNoEvent carries it on the Sounds-off null branch. Delivery validates identity/provenance and records per-read information without creating a public event on the no-event branch. The existing actual Muted call emits its public Mute line, if any, using that same read record. Process each read once, with no stale record carried into the next initialization, and add no settings read.

Only the existing named branch and null-with-Sounds-on branch request a public Mute event. Their Muted observer callbacks remain before their respective TickSound call. No callback or information record turns a no-event branch into an event. TickHost's Muted and MuteReadNoEvent observer behavior remains a no-op.

### P2-C Disposition of each Mute read

First validate selection and its required binding for every completed read. Absent means Present was false; missing OwnerType or owner map alone is not a defect for this selection, which follows its existing null branch. For a non-absent selection, local, missing OwnerType/map, an unbound winning definition or an inconsistent retained row invokes FC-2: no public Mute line, only provenance disposition, while the engine still performs its unchanged read and branch.

An inconsistent row has mode disagreeing with the winner's cache (literal with action/frame, evaluated with none), or a literal row lacking retained sourceValue/sourceKind. SourceKind versus compiled literal kind, or emittedValue versus display text, is never an inconsistency.

A qualified read has valid selection, no fault reported during it, and either a literal winner with sourceKind string or an evaluated winner of kind String. Other valid-selection reads are non-qualified; absent/Null reads make no String claim. Payload handling follows the actual branch; information can apply even when no Mute event is emitted. Exactly one table row applies to each valid-selection read; the fault rule below is orthogonal to the rows.

| Read | Payload and information |
|---|---|
| absent | Existing null branch: mute null only with current-type Sounds on; no Mute line with it off |
| literal sourceKind string | Exact sourceValue, regardless of compiled host kind |
| literal sourceKind number or boolean | Engine display text; MUTE_VALUE_NOT_STRING with suffix `literal number` or `literal boolean` |
| evaluated kind String | Actual evaluated lowered-script text, including a value cached by an earlier read in the same activation |
| evaluated kind Null | Existing null branch |
| evaluated Number, Boolean or Point | Engine display text; MUTE_VALUE_NOT_STRING suffix `evaluated number`, `evaluated boolean` or `evaluated point` |

For a valid-selection read with a reported fault, preserve its actual result and branch and log `MUTE_VALUE_FAULTED tick <t> <sid>` once. Also log NOT_STRING when the evaluated non-null result is Number, Boolean or Point. Literal NOT_STRING is instead determined by sourceKind: a lowered String for numeric Infinity logs it, while source String /1 compiled as Number does not. A fault flag never forces null.

A read handled under FC-2, whether for a P2-C provenance defect or an observation exception, records only provenance, with no MUTE_VALUE_FAULTED or MUTE_VALUE_NOT_STRING information, because its classification inputs are not trusted. Engine fault events remain unchanged. Only provenance defects invoke FC-2/FC-3. Neither non-String nor faulted values set ProvenanceFault, status or exit code. No qualified source String is replaced by canonical text and no named value is replaced by null. These records preserve host behavior; they do not claim that product coercion was fixed.

### P2-D Witness corrections and product leads

The oracle's failed script or cast reaches no successful Mute intent hook. A host script fault can return Null and, with Sounds on, produce the actual error event then mute null and MuteAll. Preserve this divergence rather than turning it into a producer failure or erasing its event; when no earlier section-10.7 rule applies, ordinary comparison grades it fail at that tick, recorded as L3. Sounds-off null writes no Mute line. Two absent Mute lines do not establish successful Mute intent.

Zero added evaluations is measured against b107727c on the same scenario and tape, for full lifecycle draw and state sequences and for event sequences apart from mute payloads that 9.2 changes, in both observation modes. The proposed null expression is predicted to draw twice during Init (Condition then Apply), not twice over the full activation: later HasNext polls can read Condition again. Measure and retain the complete baseline rather than hard-coding a full-activation count. C7 is host-only because script sites remain unmapped for paired grading. It does not mean every Apply advances an evaluation counter once: Condition may have read Sound first, action/frame caching may reuse it, and a null value is not cached.

Retain the product leads separately: L1 is display-prefix drift after literal normalization; L2 is EvalString's non-String coercion; L3 is a faulted null read reaching MuteAll; L4 is the stale converter hex-literal comment; L5 is Unicode whitespace accepted by the host's general .NET trim path. This wire records those behaviors and closes none of them.

### P2-E Product behavior

Capture is passive. TickSound receives exactly the previous argument; evaluation count, exceptions, cache effects and state-write order are unchanged apart from the private observation-only fault counter in P2-A. Restoration changes parity observation only. TickHost.Muted and TickHost.MuteReadNoEvent remain no-ops. This is neither a product audio repair nor a sound-stop equivalence claim.

## Proposed host failure channel

### FC-1 Static refusals and precedence

New P1-D/P1-E Sound row shape, unknown-top-level, coverage, binding and sourceKind faults are InvalidDataException map refusals inside LoadPets, after the existing pose and geometry checks. They log `refused <map path>: <reason>`, exit 2 and leave no trace file.

Preserve the existing parser, JSON syntax, duplicate-root and retained-field checks and their current precedence. The schema check keeps its position and changes only as P1-F states. Preserve the legacy pose/geometry order. New validation (Sound rows and the unknown-top-level check) is deferred until those existing checks have succeeded; retaining the raw Sound JSON for that later validation is permitted. This ordering does not move an existing parser or schema refusal behind Sound validation. An admitted scenario/2 slot order is unchanged.

### FC-2 Runtime provenance faults

For a completed read with a P2-C provenance defect, including a no-event read:

1. Write no Mute line for that read: no canonical value, source guess or null.
2. Retain `MUTE_PROVENANCE_MISSING tick <t> <sid>: <reason>` as the writer's first Mute fault if none is retained yet.
3. Set ParityTraceWriter.ProvenanceFault to it only if that property is null.

The engine still performs the unchanged Present/Read and TickSound branch and continues. No exception raised by observation code, including either fault-count snapshot, passive selection, callback, identity lookup, source-value lookup or Mute writer, may leave observation code. Catch it where it arises and apply steps 1-3 with reason `observer exception <type>: <message>`. If it occurs before the engine read, retain the defect and still perform that read and branch unchanged. Never catch an exception from the engine's own Present or Read; it propagates as before and is not a completed observation.

An observation exception reaching TickMascot.Tick or the startup wrapper would create an internal-fault product event and dispose the companion while status could stay complete; that is forbidden. In a parity run, a caught observation exception without a retained defect is also forbidden. Under TickHost's no-op observer, an exception caught in engine-side observation code (either snapshot or passive selection) is discarded and changes no product behavior. A read handled under FC-2 emits neither information token (P2-C).

### FC-3 Finishing both trace modes

ParityRun.Finish remains the only ending in both modes. Non-null ProvenanceFault sets footer status error and logs `parity trace: <ProvenanceFault>`. If the first Mute fault differs from ProvenanceFault because an earlier fault won, also log `parity trace: <first Mute fault>`. Then emit the information lines under FC-6, write the footer and return exit 1 for the provenance error. The first provenance fault and first Mute provenance fault are both visible, not every later fault of a kind already logged. Any provenance fault makes status error. On a mid-run scenario-invalid path the footer then says error and the run still exits 2.

### FC-4 Existing wire only

Add no exit code, footer member, trace field, event kind or section-10.3.2 error code. MUTE_PROVENANCE_MISSING is a host log token only, never a footer member or extension at any status. Unknown footer extensions are ignored by comparison and one beside complete would read as success. It is never an error event: producers do not manufacture product fault events.

### FC-5 Ordering and monotonicity

Map refusals precede the trace and retain exit 2. Scenario-invalid retains exit 2. A run in error remains in error; nothing changes it back to complete. The unchanged comparer grades status error as `unjudgeable` / `aborted` before event comparison. Required provenance faults are not hidden as unclassified typed errors.

### FC-6 Information occurrences

For valid-selection reads not handled under FC-2, retain MUTE_VALUE_NOT_STRING and MUTE_VALUE_FAULTED once per applicable occurrence with tick and sid, in occurrence order, including no-event reads. A read handled under FC-2 retains only provenance, and any information already formed for it is discarded. The exact NOT_STRING suffix set is `literal number`, `literal boolean`, `evaluated number`, `evaluated boolean`, `evaluated point`. Each line is exactly `MUTE_VALUE_NOT_STRING tick <t> <sid>: <suffix>` or `MUTE_VALUE_FAULTED tick <t> <sid>`.

Finish logs each retained information record as `<line> (information)`, after all applicable `parity trace:` lines and the existing rng-tape-exhausted information line, and before the footer, on every ending and both trace modes. Information never changes status, exit code or ProvenanceFault. Trace-off forms the same passive checks and information records while withholding public event lines.

## Proposed trace/2 forward clarification

### P3-A Intent payload

P3 is an explicit version exception recorded in the adopted preamble; its date and signatures belong to that version's signature record. Retain trace/2 and scenario/1, and scenario/2 where H is adopted, with fields unchanged. mute.clip is a name-bearing payload under section 9. For a qualified P2-C read it is the String returned by the source Sound accessor for that activation, in source spelling, including the empty string; a qualified literal read records that String from the bound sourceValue. For evaluated String, the host writes its own evaluation of the lowered script; equality with the source accessor is what the fresh pair tests, not what the host asserts. Null is written only for the actual all-clips branch with current image-set Sounds on.

This is intent, not cache-key, tier or stop evidence. Non-qualified values preserve the actual host result under P2-C without a source-intent claim and are graded ordinarily. No policy exception hides a mismatch.

### P3-B No new diagnostic events

The existing `sound` event row is unchanged. Neither producer writes sound events from Mute in this step. Optional O2-after through O5 diagnostics, O8 completion, observer-defect ledgers and MUTE_* information belong in producer logs or result records. They never become trace lines, footer members or event kinds. An observer defect ends its producer with status aborted or error under the existing rules, including for trace-off output.

Required oracle hooks are O1, O2-before, O6, O7 and O8. O2-before observes named intent using the already evaluated local, including empty string. O7 observes null only after the Sounds guard admits the all-clips branch. O8 proves completion, including a Sounds-off null no-op. Attribute by exact activation VariableMap identity, never a second parameter evaluation, a current-member guess or a Clip-method call. New hooks must be non-throwing and record named defects that are checked before any successful footer, including exceptional source exits. The oracle logs each such defect with the token `MUTE_OBSERVER_DEFECT` and its reason, writes footer status aborted or error, and exits 1, its existing non-success exit. Lift only the supported Mute-entry refusal as these seams and controls land; retain the independent Sounds-on pose-audio refusal.

Hook O1-O8 definitions are the Mute seam anchors: ANCHORS.json SHA-256 db96982b3684e8f751746988c29479ffcf96af18f0dc826c4bf43c8011ca5d5b (authoritative), with ANCHORS.md SHA-256 8dea913a3b0f8d15c88648ea00c64fa0eb5ab8d62b08b2effa2123566db3e8e6, Part B, or a later revision both owners explicitly name by hash. O1 captures the evaluated local; O2-before observes named intent; O2-after/O3/O4 diagnose tiers 1/2/3; O5 diagnoses the guarded stop; O6 marks null-branch entry before Sounds; O7 marks its admitted all-stop; O8 marks normal completion. Control ids W1-W8 below are not hook ids.

### P3-C Fresh evidence

Mute-intent evidence is citable only from a fresh trace/2 pair whose comparison supplies and matches the host EXE, host engine and oracle JAR. `buildWitness.host.artifactBound` must be true under H's engine-inclusive meaning for a call supplying the engine, and `buildWitness.oracle.artifactBound` must be true; EXE-only host binding does not qualify. Producer evidence names the actually loaded DLL path and hash, equal to the graded engineSha256, for the build containing adopted map/3 and P2-A through P2-E. The oracle JAR contains required O1/O2-before/O6/O7/O8 hooks. This requirement is prospective on H's engine-binding adoption; it cannot be satisfied by the base comparer saying only EXE-bound.

A pre-adoption host trace/2 keeps its archived meaning, is never regraded and is not Mute-intent evidence. The host writes trace/2 only; no host trace/1 meaning is defined and the oracle trace/1 Mute refusal stays.

### P3-D No regrade or policy

Require fresh trace/2 pairs meeting P3-C from new scenario/1 Mute fixtures with no policy member. Keep compare.py's grading code unchanged; only self_test gains one pair that grades fail with class `event-mismatch` for `/TO` versus `to`. That changes compare.py's bytes/hash, bound as actually loaded in closure evidence. No archive is regraded and no policy/exemption is created, consistent with 10.9's N-parity-1107-03 rule.

### P3-E Registry identity

Adding Mute sections to make_packs.py must leave the four registered scenario/pack byte sets and all five calibration-control digests byte-identical on regeneration. Demonstrate that by hash comparison. Change no registry bytes. The frozen generator reference and policy packets remain historical artifacts; a new generator build does not rewrite them.

## Required directed controls

Run each target mutation alone except deliberately combined C12. Fixture diagnostic expectations are declared before execution, never derived from the caught exception. A killed mutation identifies the right reader/control and rebuilt/executed artifact; these are acceptance obligations, not passed results.

The ten wire controls are accounted for: W1-W5 retain own-normalized-literal, same-canonical-distinct-spelling, incoming-over-own, nested-reference and retained-owner. W6 is corrected branch-controls. C7 replaces computed-single-evaluation; C6 corrects failed reads. W7 retains constant-type-boundary. W8 retains normalization-crosses-constant-type, extended by C1-C4. Strict-reader is C13 with C11 for a local override. Reserve O identifiers for the pinned hooks.

For information controls below, substitute exact fixture tick/sid and the closed lowercase suffixes. Finish's full expected line includes ` (information)`. Where stated, assert complete/exit 0 and no parity-trace log on an otherwise isolated read, so routing information through ProvenanceFault fails.

| Id | Required input and result | Killing mutation |
|---|---|---|
| C1 | Own Sound `&#160;1` is sourceKind string and restores U+00A0 then 1. U+0020 then 1 is number, writes `1`, and logs exactly `MUTE_VALUE_NOT_STRING tick <t> <sid>: literal number (information)`; complete/exit 0, no parity-trace log. Fresh source intent passes for the former and ordinarily fails with extra-event for the latter if the oracle emits no Mute; unexpected oracle intent is recorded and escalated | .NET trim or the current TickType.ParseLiteral classification, or information sets ProvenanceFault |
| C2 | `TRUE` and `False` restore those exact source Strings despite compiled Boolean values | Emit host display text or case-fold Boolean source classification |
| C3 | Infinity/-Infinity/NaN are sourceKind number, compile after lowering as Strings, and report exactly `infinity`, `-infinity`, `nan`, each with exact literal-number information line; complete/exit 0, no parity-trace log | Qualify by compiled kind, restore numeric source text, call kind differences inconsistent, or information sets ProvenanceFault |
| C4 | Numeric `0x1p1` writes `2` with exact literal-number information, complete/exit 0, no parity-trace log; `/0X1P1` restores its raw String | Qualify by emitted/compiled value or change status for information |
| C5 | No used pose sound: `/`, one backslash and empty Sound all emit empty canonical prefix but report their three distinct source Strings | Key restoration by emitted value |
| C6 | Own Sound `${Foo}`, action cache, referenced once with Foo `/a` and once without, Sounds on. First forwards `/a`; second produces existing error then mute null and exactly one FAULTED information line, complete/exit 0. Ordinarily pair fails at that tick, reporting earlier-rule outcomes. If refused, record it and run `${Sound}` fallback, recording which input executed | Write anything other than actual null on second ref, including stale `/a` or emitted text; erase Mute, drop information, set error or treat the value divergence as provenance |
| C7 | Host-only: Condition `${Sound == null}`, action-cached Sound `${Math.random() < 2 ? null : 'x'}`, Sounds off, at least two activations. Run new and b107727c on each build's converter output, requiring byte-identical animations.xml and the same scenario/tape, in both trace modes, comparing complete lifecycle draws/events/states and per-activation counts against the measured base. Source predicts two draws during Init only; later HasNext Condition polls can add reads and must match the full measured baseline. TickHost no-op observer self-test follows the same baseline. Paired script-site outcome remains pending/refused, not a paired control | Added helper/observer read in one or both modes, altered caching or one-evaluation assumption |
| C8 | Recording ITickClips: `/TO`, `/01`, `/1d` still send `to`, `1`, `1` to MuteNamed | Feed restored sourceValue to product TickSound |
| C9 | Delete children entry for a Sound-binding reference. Direct reader test expects InvalidDataException and the fixture-predeclared full absent-children-entry message; CLI expects that identical reason after `refused <map path>:` with exit 2/no trace. Generic dictionary/accessor error fails | Skip the absent-child-entry guard |
| C10 | `${'1'}` forwards String `1`; `${1}` writes `1` and exact `MUTE_VALUE_NOT_STRING tick <t> <sid>: evaluated number (information)`; `${true}` writes `true` with exact `evaluated boolean` information; a Point-valued Sound writes the engine display text with exact `evaluated point` information; frame-cached `#{'1'}` and `#{1}` repeat the first two results. All complete/exit 0, no parity-trace log | Decide by display text, route information to ProvenanceFault, mislabel a kind, or treat a frame-cached evaluated row as inconsistent |
| C11 | Before Init, `Vars.Put("Sound", ScriptValue.FromString("x"))` on a Mute also declaring own `/TO`. No Mute, named MUTE_PROVENANCE_MISSING with a fixture-predeclared reason, error/exit 1, comparer aborted | Treat local as absent, emit x, or consult definitions before locals |
| C12 | Synthetic pose provenance fault at tick 2 then Mute provenance fault at tick 5: both required parity-trace lines, error status | First-wins hides first Mute code |
| C13 | One map per P1-E rule, including wrong value/cache/source/member and an untrimmed script envelope in literal sourceValue; each breaks exactly one rule. Fixture predeclares full message. Direct reader asserts exact InvalidDataException and full message; CLI reason must equal it after refused-map prefix, exit 2/no trace. Each removal mutant produces exactly one failure naming its removed reader check. Another guard or generic JSON/dictionary exception fails, never substitutes | Remove the individual check |
| C14 | Map/1 and map/2 each give the exact P1-F map/3 regenerate diagnostic, exit 2/no trace. Every reader gets its own accepts-map/2 mutant | Leave a reader accepting retired maps |
| C15 | Compare emitted animations.xml against base for every existing converter self-test input and every make_packs pack the base converts; a new refusal of any of them fails. With one capture defect injected, maps false succeeds with identical XML/no new refusal. With maps true, test both entry points: the public API returns its refused-member/no-EmittedXml result with the named map defect, and ConvertPack refuses before emitting any output. Separately prove underlying XML equality without the injected fault; do not demand XML from a refused API result | Provenance changes XML, capture runs with maps off, or capture refusal leaks into no-map conversion |
| C16 | Literal text `null` is sourceKind string. Fresh own-Sound null-text pair, Sounds on, has clip equal to String `null` on both sides at O2-before. This source rule previously rested on a port comment; if oracle instead takes all-clips, record/reopen P1-C before the v1.4 placement; such a pair is a probe, not P3-C evidence | Treat null-text as absent or JSON null |
| C17 | If a distinct schema Sound spelling exists, a Japanese-schema input uses it and canonical Sound enumeration. Otherwise record the condition and table evidence; do not invent a spelling | Enumerate source local names rather than canonical parameter identity |
| C18 | Synthetic host: use Sounds on for event-branch sites and Sounds off with a null result for MuteReadNoEvent. Inject InvalidOperationException, one variant per site, at every FC-2 observation site, taking the site list from FC-2 and never from the implementation's catch regions: each fault-count snapshot, passive selection, both callback forms (Muted and MuteReadNoEvent), identity lookup, source-value lookup and the Mute writer. Each variant retains the exact fixture provenance reason `observer exception InvalidOperationException: <fixture message>`, logs `parity trace: MUTE_PROVENANCE_MISSING tick <t> <sid>: observer exception InvalidOperationException: <fixture message>`, writes no Mute line or information for that read, writes no internal-fault event, disposes nothing and ends error/exit 1; the engine read count, branch and recording-ITickClips argument equal the base witness. A site whose injected exception escapes observation code fails the control; no site is skipped. A separate variant throws from the engine Read through a source reachable unchanged at b107727c: a test ITickEnvironment whose Cursor or Screen read throws, armed only while an evaluated Sound script reads it. That variant preserves the base exception, error event, disposal and call count, and logs no MUTE_PROVENANCE_MISSING. Neither variant passes by skipping execution | Remove, or never write, the catch at any one site; swallow without retaining provenance; wrap engine Read in an observation catch; or skip or change the engine operation after a pre-read observation failure |
| C19 | Fixed corpus from C1-C4/C16/W7 plus +NaN, NaNd, Infinityf, 0x1p1d, 1., .5, 1e, 0x1, 1_0, U+0085 then 1, tab then 1 and U+0020 then true. Actual JDK String.trim/Double.parseDouble and both actual production C# classifiers agree. Use no upstream source | In each classifier separately: .NET Trim in place of Java trim (U+0085 then 1), trimmed or case-insensitive Boolean equality (TRUE, and U+0020 then true), a suffix accepted after NaN or Infinity (NaNd, Infinityf), a hex form accepted without its p exponent (0x1), an exponent with no digits (1e), and underscores accepted (1_0). Each must fail on its named entry. Copying a correct classifier is not a killing mutation |
| W1 | Normalized own `/TO` emits `to` but reports `/TO`, beside unchanged-literal control | Emit canonical instead of source literal |
| W2 | Same canonical prefix from distinct definitions preserves respective authored spellings | Key by canonical text or value equality |
| W3 | Behaviour binding overrides distinct own Sound, with root and next-reference bindings | Always choose own or root binding |
| W4 | Distinct child-reference bindings survive separate activations of one Mute declaration; parent incoming bindings do not leak into child construction | Key by declaration, current stack or outer activation RefKey |
| W5 | Failed Transform retains Alpha's action while current member is Beta; use Alpha provenance and current Beta for the existing null-branch Sounds setting | Choose current-member or PoseType map |
| W6 | Empty, absent and computed null keep actual branch and event order under Sounds on and off; oracle failed reads have no successful intent. An isolated injected oracle observer defect logs `MUTE_OBSERVER_DEFECT` with its reason, exits 1 and writes footer status aborted or error, never success, including trace-off. A combined source-exception test additionally asserts both diagnostics | Collapse empty/null, emit null at hook O6 not O7, omit O2-before/O8, or suppress defect failure into a successful footer |
| W7 | Own true/false are sourceKind boolean, report true/false with exact literal-boolean information. Own 1/01 are number and report 1 with literal-number information; complete/exit 0. Empty/null-text stay String. Padded numbers, NaN/Infinity and hex follow C1/C3/C4. Computed Number/Boolean are not String | Restore/qualify by not-number, drop Boolean classification, or assume every authored/non-null value is a String; payload equality alone cannot kill a Boolean mutant, so assert classification/information |
| W8 | Source String /1,/true,/01 emit 1,true,01 but report /1,/true,/01 without NOT_STRING. TickSound still receives 1,true,1 | Reject all non-String compiled literals or substitute display text |
| Q1a | Synthetic local-null selection, and separately a null read through an unbound winning definition, each with Sounds off, records provenance, error/exit 1 and no Mute event | Validate only actual Muted calls, or check any provenance condition only on those calls |
| Q1b | Faulted null with Sounds off logs FAULTED once per read, no Mute, complete/exit 0; repeat same reinitialized child | Skip/double-process, reuse old record or route information to ProvenanceFault |
| Q2 | Compiled-definition baseline b107727c for action-cached `${Sound == null ? 'x' : 'y'}` establishes x/String, and `${Sound == null ? 1 : 2}` establishes 1/Number, in both Sounds settings. Record the actual base TickSound calls, and establish each result kind at b107727c with a separate probe that calls TickVars.Get on a fresh activation of the same compiled definition, never from the new helper; if either prediction differs, reopen rather than pass. In each Sounds setting and each trace mode the new build must equal the baseline branch and recording-ITickClips call and argument, preserve error ordering, log FAULTED per read and additionally evaluated-number information for Number, no NOT_STRING for String, complete/exit 0. With Sounds on String x writes x without source-intent claim. Record conversion/validation refusal and use compiled-definition fallback, never skip | Force null, change argument in either/both modes, make information exclusive, infer source intent after fault, or change status for information |
| Q3 | Synthetic host: evaluated Sound recovers a nested reported fault to Number 1, but its owner map/binding is unavailable. The actual base engine branch and TickSound argument remain unchanged; new output has only the named provenance defect, no Mute line and neither information token, error/exit 1. A second defect variant binds a literal row with sourceKind number to an action-cached winner that evaluates to Number 1 (an inconsistent retained row): the same provenance-only result, with neither `literal number` nor `evaluated number` information. Positive contrasts: absent Sound with OwnerType or owner map unavailable in each Sounds setting preserves the base null/no-event branch, with no provenance defect, complete/exit 0. Run the contrasts separately | Leak FAULTED or NOT_STRING information from a provenance-defect read, or require owner binding for an absent selection, or classify an inconsistent row from its retained sourceKind |

All diagnostic placeholders become concrete expected fixture values before execution. Full expected messages come from independent fixture declarations, not the exception just caught. No control promises a grade if an earlier existing validation/run/attribution rule wins. Where complete, otherwise valid records contain an extra host Mute after aligned unknown-cause errors, it is ordinarily `extra-event` and beats `error-unclassified` under existing 10.3.5/10.7 precedence; report the unchanged comparer's actual precedence. Two absent Mute lines alone are not a Mute-intent result.

## Ownership and review evidence

Codex drafts this joint contract and owns converter provenance capture/map/3 writer, converter tests, map validation probes, oracle hooks/edit-spec/runner, make_packs Mute and receiver sections, directed Mute and receiver scenarios and the comparer self-test pair. Claude owns the strict host map reader, definition binding, TickVars/TickMute passive read helper, observation interface/implementers, writer/Finish and host tests, including the CoreTests hotspot receiver legs. Codex owns the C14 accepts-map/2 host mutant in verify-map-readers.ps1, written against the final ParityMap.Parse bytes supplied by Claude after host commit 4a. Each owner edits only its own mutation-guard entries in the shared mutation harness. The comparer grading code is unchanged; only its self_test gains the P3-D pair. The map/2 pin at tests/mutate-selftest-guards.py:21289 guards the converter writer and belongs to Codex; other host-reader guard entries belong to Claude.

Before adoption, both owners review the exact final text and record its hash, date and signatures. This file contains no signatures. The Mute seam anchors named in P3-B remain source-neutral design input; upstream source behavior remains peer-attested. No upstream source text is added here.

Source checks made at b107727c for this draft:

| Public source | Checked implication |
|---|---|
| TickModel.cs:13-27, 372-374, 447-451, 472-474, 501-511 | Distinct compiled parameter objects and lossy literal/cache compilation; evaluated empty body need not have a Program |
| TickModel.cs:528-576 | Boolean equality before number parse; current number helper uses .NET Trim, so it cannot independently implement P1-C unmodified |
| TickRuntime.cs:186-240, 618-623, 785-789 | Incoming-over-own selection; locals first; self-reference reports fault and returns Null; caches non-null values; EvalString coerces non-null; Condition can precede Apply |
| SafeScript.cs:1000-1012, 1019, 1139-1149 | Program ScriptFault becomes reported Null; stored value kinds and display formatting |
| TickInstantFamily.cs:64-74 | Named and null-with-Sounds-on are the only existing Muted calls; each precedes TickSound; no null/Sounds-off call |
| ParityRun.cs:24-94, 199-226, 698-708, 760, 787-790, 880-889, 1696-1707, 1769-1772, 2050-2064 | Existing map/2 parsing/apply checks, first-wins provenance, current direct Mute writer, pre-trace LoadPets, and Finish's error/log/exit channel |
| TickPackEmitter.cs:1365-1389, 1498-1512, 1644-1670 | Literal normalization versus evaluated lowering; within-member prefix check; exact source attributes available during emission |
| ShimejiParser.cs:625 | Public normalization table maps Sound to Sound; C17 stays conditional rather than inventing a Japanese spelling |
| compare.py:449-454, 627, 670-695, 762-768 | Run status precedes events; artifact-bound witness field; later mismatches can win over aligned unclassified errors |

Only this proposed text was written by this drafting task. No build, producer code, test, real app, native audio, fresh pair, commit, backlog closure or policy-H edit is claimed.
