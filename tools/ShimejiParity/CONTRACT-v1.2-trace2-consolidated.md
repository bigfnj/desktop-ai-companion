# Trace/2: consolidated contract section for joint signature

Status: CONSOLIDATED, not signed. Written 2026-11-07 by Claude (host producer) from the host draft and Codex's review,
for Codex (oracle and comparer) and Claude (host) to sign. Nothing was built, run, tested or mutated to produce it, and
no upstream source or catalog file was opened. Independently checked the same day; the check's edits are listed in
B.3.

Inputs:

- host draft `trace2-host-draft.md`, SHA-256 56ec2d0b0ad5fabbc3aeb09697687c5ef1da917bf0d8240d2216f26a46962b58 (re-hashed, matches)
- Codex review `trace2-codex-review-2026-11-07.md`, SHA-256 3c703bad48fa33cf680ea3e53f10c0e1e2ca017c2147eacbd310a59a0d4c7936 (re-hashed, matches)
- Codex message cad2be7d (2026-11-08T04:14:09Z), the summary of its required edits
- `tools/ShimejiParity/CONTRACT.md` (v1.1), `tools/ShimejiParity/comparer/compare.py`, and host
  `src/dotNet/Shimeji/TickRuntime.cs`, `TickSocialFamily.cs`, `TickMotionFamily.cs`, `ParityRun.cs`, plus one function of
  `TickPacks.cs`, all read with `git show` at dac-bd-int-w3 HEAD 422b004821a944f1999935e71974f915e680afa5.
  `git diff --stat 1c8ae85 HEAD` over those paths is empty, so every line number Codex cites at 1c8ae85 holds at HEAD.
  HEAD has since moved to 51067d580c780645bc1ead16a086e65caeeb789e; `git diff --stat 422b004 HEAD` over
  `tools/ShimejiParity` and `src/dotNet/Shimeji` is empty, so every line cited here holds there too (B.3 K7).

Parts: A, the section text; B, the change table; C, disagreements and the host-code checks; D, the control and
mutation matrix; E, open items that do not block signing.

Codex's replacement paragraphs appear in Part A word for word. Part B names each one and where it landed.

---

## Part A. Section text (paste into CONTRACT.md)

## 10. Trace schema `shimeji-parity-trace/2` (v1.2)

### 10.1 What changes

Trace/2 is trace/1 (section 3, with the v1.1 rules) plus exactly these changes:

1. the header declares `"schema": "shimeji-parity-trace/2"`;
2. every `error` event carries a typed `error` object beside its `message` (10.3);
3. `message` on an `error` event is validated but no longer compared (10.3.5);
4. every footer carries a required boolean `tapeExhausted`, and drawing past a tape's end is no longer a run failure
   on either side (10.5);
5. a trace/2 trace-off artifact is proof-only: cross-runtime comparison refuses it (10.5.3);
6. three unjudgeable classes are added, each with CLI exit 2: `trace-version-mismatch` (10.2),
   `trace-off-not-comparable` (10.5.3) and `error-unclassified` (10.3.5);
7. startup faults and an interrupted startup snapshot are judged by 10.6.

Everything else in sections 1 to 9 applies to trace/2 unchanged. A trace/1 pair is judged under v1.1 exactly as
before, including the exact comparison of error-event messages; it is never re-judged under v1.2 without fresh
traces. Both runtimes regenerate their traces to obtain a trace/2 verdict.

### 10.2 Version declaration, mixed pairs and dispatch

- The header's `schema` is the only version declaration. No other header field changes.
- A comparer recognises `shimeji-parity-trace/1` and `shimeji-parity-trace/2`. Any other value, or a first line
  that is not a header, stays `missing-field`.
- Once both headers have been read and both schemas recognised, a pair whose schemas differ is `unjudgeable`
  with class `trace-version-mismatch`. This is decided before any other check of either trace (build witness,
  body validation, binding, run status), because the two versions are validated by different rules and a mixed
  pair can never be compared. The CLI exit is 2.
- Each trace is then validated by the rules of its own declared version.
- The new header/schema preflight dispatches recognized trace/1 pairs to their v1.1 validation and comparison rules.
  Trace/2 additions do not reorder the old pair's message, input or failure semantics. Recognized mixed versions are
  refused before version-specific body grading.

### 10.3 The `error` event

#### 10.3.1 Shape and wire types

`{ "type": "event", "tick": t, "sid": s, "kind": "error", "message": "...", "error": { "code": "...",
"lookup": { ... } | null, "via": [ ... ] } }`

- `tick`, `sid` and `kind` keep their section 3 meaning. The sid is the companion the runtime reports the fault
  on: for a failed birth, the PARENT; for a fault a tentative child raises before it fails, its provisional sid
  (section 3, v1.1 birth lineage).
- `message` is written in the runtime's own words.
- `error` is required, an object with exactly the properties `code`, `lookup` and `via`.

Each error object has exactly one occurrence of each property code, lookup and via, with no other properties. code is
a string in the closed table. Each typed lookup has exactly one occurrence of each property in that code's operand set
and no other properties. Wrong types, unknown codes/labels, duplicate properties and wrong property sets are
missing-field.

imageSet is a non-empty string. behavior and action are string or JSON null; empty, whitespace and null remain
distinct. Capture those names before null-to-empty normalization or any name rewriting. refKey is a non-empty valid
source refKey in section 3's format. parameter is a non-empty string with the meaning below. via is an array of strings
from the closed wrapper vocabulary, in outermost-first order; an empty array is valid. A producer adds only wrapper
context actually carried by this fault, never context reconstructed from a later state.

message is a required non-empty string but is not used as a classifier or compared as error meaning in trace/2.

#### 10.3.2 Codes (closed set)

| code | what failed | `lookup` (exactly these properties) |
|---|---|---|
| `behavior-missing` | a behaviour requested by name is not a root behaviour of the member searched | `imageSet`, `behavior` |
| `action-missing` | a behaviour or reference names an action the member does not declare | `imageSet`, `action` |
| `variant-missing` | an action has no animation variant it can play: none whose condition holds, none for its turn state, or fewer than its class requires | `refKey` |
| `parameter-invalid` | a parameter or guard attribute read by an action is null where a value is required, not of the required type, or outside the range the action accepts | `refKey`, `parameter` |
| `scan-target-missing` | an action needs its scan target and has none | `refKey` |
| `unclassified` | anything the runtime does not type with one of the codes above | none: `lookup` is `null` |

A code outside this table, a `lookup` whose property set is not the code's, a non-null `lookup` for
`unclassified`, or a `null` one for any other code, is `missing-field` (the trace is unjudgeable), as an unknown
event kind is today. Adding a code is a contract change. Reserving a code does not assert that both current seams can
attribute every fault in that class.

#### 10.3.3 Lookup operands and attribution

- `imageSet` is the image-set name of the member that was SEARCHED, which is not always the reporting
  companion's: for a birth, the child's member after the BornMascot fallback of section 3; for a Transform, the
  member transformed into; for an arrival's TargetBehavior, the target's member.
- `behavior` and `action` are the requested name exactly as the runtime looked it up, after evaluation and
  before anything else: not trimmed, not translated through `sourceNames` (a name that was not found has no node
  to translate). A blank name is written as the empty string or as the whitespace it holds; a name that evaluated
  to null is JSON `null`. `""`, `" "` and `null` are three different operands.
- `refKey` is the refKey (section 3) that the failing action's `actionStack` entry has, or would have had when
  the failure is in its construction: the reference's key when the action was reached through an
  `ActionReference`, else its declaration's.
- For a known built-in parameter, parameter is its canonical source schema key. A custom parameter retains its exact
  source name. A synthetic lowering parameter is never exposed as if it were an authored source parameter: type it
  only when explicit source attribution identifies the actual source parameter and refKey; otherwise use
  unclassified.

A producer emits a typed code only when the cause and every required operand are available from stored provenance at
the actual failed operation. It must not evaluate a parameter, guard, condition or script again to obtain them. An
initialized action can use its stored identity; a constructor failure requires explicit builder/reference context
carried into that construction.

If the optional fault-attribution channel cannot supply those operands, emit unclassified with lookup null. That
absence alone is not a provenance abort. A missing or invalid binding already REQUIRED by section 9 remains a
provenance fault; it must not be hidden as unclassified.

#### 10.3.4 Classification and `via`

- A runtime classifies a fault where its typed cause arises (the failed lookup or read), not by reading message
  text. Neither runtime and no comparer ever derives a code from a message.
- Each producer records its actual faults and classifies a known cause independently of the peer. Classification
  neither creates a fault event the runtime did not produce nor erases or downgrades an event to align traces. An
  absent peer event remains a mismatch or a specifically established declared policy divergence; it is not a reason
  to write unclassified.
- `via` lists the wrapper sites that caught the failure and chose its consequence, OUTERMOST FIRST, the order
  section 3 already uses for `actionStack`. The companion's own step is the implicit root and is never listed, so a
  fault caught by the step itself has `via: []`. A fault reported where it arises, with nothing catching it, also
  has `via: []`.
- Do not enforce a guessed causal stack when the fault was already caught/reported by an inner site.
- The wrapper vocabulary is closed:

| entry | the wrapper |
|---|---|
| `startup` | creating a start companion (section 4, startup step 2) |
| `press` | a left-button press dispatched to a companion |
| `release` | a left-button release dispatched to a companion |
| `birth` | entering a born child's first behaviour |
| `transform` | entering the Transform's behaviour in the new member |
| `arrive` | a scan or complex action's arrival transitions (the seeker's own Behavior, then the target's TargetBehavior) |
| `interact` | Interact's closing Behavior |

- Each wrapper's consequence, for a fault it catches:
  - `startup`: startup identifies a fault during creation/initialization of a scenario starter. The existing
    per-runtime outcome is retained. The host can dispose the starter before joining the manager; the current oracle
    runner can instead terminate with an aborted/error footer. Error typing does not normalize these outcomes or
    manufacture a startup snapshot.
  - `press`: the companion is disposed.
  - `release`: the companion is disposed.
  - `birth`: the attempt is refused with `birth-refused` reason `error`, after this event.
  - `transform`: The fault is reported without disposing the companion. The new image set and all writes already
    performed remain; there is no rollback. The previous behavior survives only when failure occurred before the new
    behavior was assigned.
  - `arrive`: the transitions not yet run are skipped; the seeker carries on. Earlier successful transitions and
    state writes remain. Only transitions not yet performed are skipped; no rollback is implied.
  - `interact`: the companion carries on.
- A wrapper that catches and does not rethrow ends the list. Today no wrapper rethrows, so `via` holds at most one
  entry; the list form is kept so a nested wrapper can be added without a schema change.

#### 10.3.5 Comparison

- In a directed trace/2 comparison an `error` event matches on `tick`, `sid`, `kind` and the whole `error`
  object (`code`, every `lookup` operand, and `via` in order), by JSON equality. `message` is not compared.

In a directed comparison, an unclassified code makes only the cause code and lookup operands inconclusive, and only
between paired error events with equal tick, sid, kind and via. Event presence, order, timing, sid and wrapper context
remain exact. A missing/extra error or a different tick, sid, kind or via keeps its ordinary failure class, even when
an error is unclassified.

After encountering such an aligned unknown cause, continue checking all other events and states. Any independently
established failure retains its class and verdict. If those checks find no failure, but an aligned error cause
remains unclassified, return unjudgeable with class error-unclassified and CLI exit 2. Do not return exact or
within-tolerance.

This uncertainty rule applies to directed semantic success. Free-running comparison keeps section 6's separate
time-share claim and its existing failure/pass path, with typed-error shape validation still required. It never
becomes a directed exact claim. Do not silently replace time-share verdicts through an unconditional step-8 pending
rule.

### 10.4 Random numbers (edits section 2)

- A draw in a `none` scenario, and a draw past the end of a `tape`, both yield exactly `0.5`, and the run goes
  on. Its `rng-draw` event records that value at its occurrence.
- In trace/2 a recorded draw past the tape's end whose value is not `0.5` is `rng-invalid` (unjudgeable), as a
  draw within the tape that does not hold the tape's value is today.

### 10.5 Tape exhaustion (edits sections 3, 5 and 6)

#### 10.5.1 The signal

- Drawing past the tape's end is BEHAVIOUR (section 6) and never a run failure: a runtime that exhausts its tape
  keeps stepping to tick N, and exhaustion does not set its footer status, which is normally `complete`.
- Every trace/2 footer carries `tapeExhausted`, a required boolean.

A producer footer reports whether its actual draw count exceeded the declared tape length. For none and splitmix64 the
flag is false. Within-tape values must match the tape; observed past-tape values must be exactly 0.5. The flag and
draw records must agree.

A harness timeout footer describes only the complete recorded prefix. Its exhaustion flag is derived from that
prefix's recorded draws; it makes no claim about unwritten work. It does not repair a malformed or partial JSON record
or overwrite an existing footer.

- The draw records are the event-level evidence: the first `rng-draw` whose occurrence exceeds the tape's length
  is the first exhausted draw. No new event kind is added, so event sequences and the section 5 digest gain
  nothing to disagree on.

Exhaustion alone does not set a run-failure status. If a genuine run failure coexists with proven exhaustion, the
run-failure class still wins; exhaustion may be a secondary finding only when the available evidence proves it.

#### 10.5.2 Consistency (validity, before anything is compared)

- In a traced run of a `tape` scenario, `tapeExhausted` must equal whether the trace records more draws than the tape
  holds; in a traced `none` or `splitmix64` run it must be false.
- Any disagreement is `malformed`.
- The v1.1 oracle-only optional `tapeExhausted` extension is subsumed: in trace/2 the field is required of both
  runtimes.

#### 10.5.3 Trace-off artifacts and the observation proof

- In trace/2, a footer that carries `unobservedDraws` or `unobservedEvents` identifies a trace-off artifact. No
  header field is added.

Trace-off artifacts are inputs to the dedicated observation-proof validator, not ordinary cross-runtime parity
inputs. Their unobservedDraws/unobservedEvents counters describe withheld observations. The public comparer always
validates against recorded rng-draw events and must not switch modes because unobservedDraws is present. Require the
trace-off counters on an off footer and refuse such proof-only artifacts as unjudgeable:trace-off-not-comparable when
submitted to cross-runtime comparison.

A trace-off timeout has suppressed draw lines and generally has no final durable unobserved tally. Do not infer
tapeExhausted=false from those absent lines or construct a successful observation proof. Without a durable tally,
retain the incomplete artifact and report an unverified/run-failure proof result. An ordinary traced timeout can still
receive the prefix footer above.

The observation proof compares the actual trace-on draw count with the trace-off unobservedDraws, both exhaustion
flags, final states and event digest. It requires successful completion; timeout/aborted/error runs cannot satisfy it.

### 10.6 Startup faults (edits section 6)

Startup runtime faults do not make an otherwise valid scenario scenario-invalid. A failed run with no startup state
remains subject to the existing aborted/error rule. A complete trace claiming a startup snapshot must meet its
validity and starter-binding rules. When a snapshot was interrupted, only an otherwise valid active proper prefix of
the expected starters, in order, with no later record proving completion is inconclusive. An already observed wrong
sid/order/pending flag remains provable evidence of mismatch.

### 10.7 Precedence (trace/2 pairs)

The comparer decides in this order and reports the first class that applies:

1. header readability and recognised schema (`missing-field`), then mixed versions (`trace-version-mismatch`); a
   recognised trace/1 pair leaves this list here for its v1.1 rules (10.2); then a trace/2 trace-off artifact on
   either side (`trace-off-not-comparable`, 10.5.3);
2. each trace's own validity under its version: `missing-field` (including the error object of 10.3.1 and
   10.3.2), `build-witness-missing`, `malformed` (including 10.5.2), `rng-invalid` (including 10.4 and 10.5.1),
   `runtime-mismatch`;
3. input and binding, unchanged from v1.1: `scenario-invalid`, `pack-invalid`, `scenario-hash-mismatch`,
   `pack-hash-mismatch`, `artifact-hash-mismatch`, `tick-count-mismatch`, `source-pin-mismatch`,
   `scenario-start-mismatch` (with 10.6);
4. run failures, unchanged from v1.1 and NOT waived for exhaustion: `truncated`, `timeout`, `aborted` (a footer
   status `error` is graded `aborted`). If a genuine run failure coexists with proven exhaustion, the run-failure
   class still wins; exhaustion may be a secondary finding only when the available evidence proves it;
5. RNG behaviour, as today: `rng-unexpected-draw` (kind `none`), `rng-tape-exhausted` (kind `tape`, either side
   drew past the end), then `rng-draw-count`;
6. `pending` with class `script-site-unmapped` (section 9);
7. events in order, then states (10.3.5); in a directed comparison an aligned unknown cause does not end the walk,
   and the first independently established failure gives the class;
8. directed comparisons only: `unjudgeable` with class `error-unclassified`, CLI exit 2, when nothing above applied
   and an aligned error cause remains unclassified (10.3.5);
9. `exact` or `within-tolerance:<name>[,<name>...]`, or, for a free-running scenario, the time-share verdict of
   section 6.

Steps 2 to 4 group the v1.1 classes and do not reorder them: those checks keep the relative order the v1.1 comparer
already gives them, and each trace/2 check named in step 2 joins the v1.1 check of the same class. In that order a
scenario or pack refused before either trace is read (`scenario-invalid` from the scenario alone, `pack-invalid`) is
decided before step 1, `source-pin-mismatch` is one of the oracle trace's own validity checks, and a `scenario-invalid`
judged from the traces (an inactive button target, section 1) follows `scenario-start-mismatch`.

### 10.8 Producer obligations

Both producers:

- write `shimeji-parity-trace/2` in the header and `tapeExhausted` in every footer, trace-off included;
- classify by 10.3.3 and 10.3.4, and write `unclassified` wherever no typed cause with its operands is available;
- continue past a tape's end on `0.5` (10.4, 10.5.1);
- Instrumentation must preserve existing exception type/catch behavior and state-write order, except the explicitly
  agreed RNG continuation rule.

Host (`src/dotNet/Shimeji`):

- types each failure where it is thrown or reported, adds the wrapper entry at each of the seven catch sites of
  10.3.4, and writes `unclassified` for every exception it has not typed, including its catch-all for exceptions
  that are not its own fault class;
- keeps every message byte-identical to today's, so the product log and its fault throttle are unaffected;
- continues past a tape's end on `0.5` without setting its footer status for it, and still logs
  `rng-tape-exhausted` as an informational line;
- types a code only from stored provenance (10.3.3); a binding that section 9 already requires stays a provenance
  fault when it is missing or invalid, and ends the run in error;
- runs its section 5 observation proof by 10.5.3.

Oracle (Codex): writes trace/2; classifies through hooks that carry stored action identity (an initialized action's
own identity, and explicit builder/reference context for a constructor fault), and writes `unclassified` where that
context is absent; labels the same seven wrapper sites where its runtime catches a fault; keeps its current startup
outcome (10.3.4, 10.6); writes `tapeExhausted` in every footer and keeps `unobservedDraws` and `unobservedEvents` on
trace-off footers.

Comparer (Codex): 10.2, the shape rules of 10.3.1 and 10.3.2 (duplicate properties included), 10.3.5, 10.4, 10.5,
10.6 and 10.7, the trace-off refusal, and unchanged trace/1 and free-running dispatch, with self-test cases that each
fail with exactly their own class.

### 10.9 Closure

- N-parity-1107-01 and -02 close when fresh paired trace/2 runs of S-breed-blankbehavior-01 and
  S-transform-blankbehavior-01 compare `exact`, each with an `error` event of code `behavior-missing`, `imageSet`
  the member searched (10.3.3), `behavior` `""` and `via` `["birth"]` and `["transform"]` respectively.
- N-parity-1107-04 closes when a fresh paired trace/2 run of a scenario whose tape is one draw short grades
  `fail` with class `rng-tape-exhausted`, with both footers `complete` and `tapeExhausted` true.
- Historical v1 and v1.1 verdicts keep their input snapshot and are not re-graded.
- Do not add an unvalidated broad policy exemption while N-parity-1107-03 remains open.

---

## Part B. Change table

### B.1 Every element of Part A

Sources: "draft" is the host draft at 56ec2d0b; "Codex T2-n" is the review at 3c703bad; "consolidation" is an
edit made here, which Codex can strike at signature.

| # | element (section, opening words) | source | note |
|---|---|---|---|
| A1 | 10.1 lead and items 1 to 4 | draft | unchanged |
| A2 | 10.1 items 5 to 7 | consolidation from Codex T2-5c, T2-4b, T2-6b and draft 10.2 | keeps "exactly these changes" true now that trace-off refusal, two more classes and the 10.6 startup rule exist; item 7 added by the check (B.3 K3) |
| A3 | 10.1 closing paragraph | draft | unchanged |
| A4 | 10.2 bullets 1 to 4 | draft | Codex T2-6 restates the mixed-version rule |
| A5 | 10.2 bullet 5, "The new header/schema preflight..." | Codex T2-6a | verbatim |
| A6 | 10.3.1 shape line; tick/sid/kind bullet | draft | unchanged |
| A7 | 10.3.1 "message is written in the runtime's own words" | draft, shortened (consolidation) | requiredness now stated by A11, so the draft's duplicate clause was dropped |
| A8 | 10.3.1 "error is required, an object..." | draft | unchanged |
| A9 | 10.3.1 "Each error object has exactly one occurrence..." | Codex T2-1a | verbatim |
| A10 | 10.3.1 "imageSet is a non-empty string..." | Codex T2-1b | verbatim |
| A11 | 10.3.1 "message is a required non-empty string..." | Codex T2-1c | verbatim |
| A12 | 10.3.2 table and its missing-field paragraph | draft | Codex: "Keep all five typed codes and unclassified" |
| A13 | 10.3.2 "Reserving a code does not assert..." | Codex T2-1 (sentence outside the quote) | verbatim |
| A14 | 10.3.3 imageSet bullet (searched member) | draft | agreed by Codex |
| A15 | 10.3.3 behavior/action bullet (raw requested name) | draft | agreed by Codex; A10 adds the capture point |
| A16 | 10.3.3 refKey bullet | draft, first sentence only | the draft's "provenance fault ... never writes a guess" sentence removed, replaced by A18 and A19 |
| A17 | 10.3.3 "For a known built-in parameter..." | Codex T2-2c | verbatim; replaces the draft's "as the source configuration spells it" bullet |
| A18 | 10.3.3 "A producer emits a typed code only when..." | Codex T2-2a | verbatim |
| A19 | 10.3.3 "If the optional fault-attribution channel..." | Codex T2-2b | verbatim |
| A20 | 10.3.4 classify-at-cause bullet | draft | unchanged |
| A21 | 10.3.4 "Each producer records its actual faults..." | Codex T2-7 | verbatim; replaces the draft's Q3 "only when both sides can emit it" |
| A22 | 10.3.4 via definition bullet | draft | unchanged |
| A23 | 10.3.4 "Do not enforce a guessed causal stack..." | Codex T2-3 (sentence outside the quote) | verbatim |
| A24 | 10.3.4 wrapper table (entry, wrapper) | draft | consequence column moved to the list below it, so verbatim paragraphs fit |
| A25 | consequence `startup` | Codex T2-3c | verbatim; replaces "the companion is disposed before it joins the manager" |
| A26 | consequence `press` | draft | host-verified (C.2 H13) |
| A27 | consequence `release` | draft | host-verified (C.2 H13) |
| A28 | consequence `birth` | draft | host-verified (C.2 H13) |
| A29 | consequence `transform` | Codex T2-3a | verbatim; replaces "the companion carries on in the behaviour it had" |
| A30 | consequence `arrive` | draft plus Codex T2-3b | Codex's two sentences appended verbatim |
| A31 | consequence `interact` | draft | see E2 |
| A32 | 10.3.4 "A wrapper that catches and does not rethrow..." | draft | host-verified (C.2 H10) |
| A33 | 10.3.5 bullet 1 (JSON equality, message not compared) | draft | unchanged |
| A34 | 10.3.5 three paragraphs "In a directed comparison...", "After encountering...", "This uncertainty rule..." | Codex T2-4a, T2-4b, T2-4c | verbatim; replace draft bullets 2 to 4 (pending becomes unjudgeable) |
| A35 | 10.4 both bullets | draft | unchanged |
| A36 | 10.5.1 bullet 1 | draft, edited (consolidation) | "ends with the status it would otherwise have had" became "exhaustion does not set its footer status"; same reason Codex gave for removing the causal-independence claim in T2-5 |
| A37 | 10.5.1 "Every trace/2 footer carries tapeExhausted, a required boolean." | draft | its definition clause replaced by A38 |
| A38 | 10.5.1 "A producer footer reports whether..." | Codex T2-5a | verbatim |
| A39 | 10.5.1 draw-records bullet | draft | unchanged |
| A40 | 10.5.1 "Exhaustion alone does not set a run-failure status..." | Codex T2-5f | verbatim |
| A41 | 10.5.2 three bullets | draft | the draft's trace-off bullet and observation-proof bullet removed, replaced by A43; bullet 1 now also requires a false flag in a traced `none` or `splitmix64` run (B.3 K2) |
| A42 | 10.5.3 identification bullet | consolidation from Codex T2-5's note "No new header field is required if the required off-footer counters identify proof-only artifacts" | see E4 |
| A43 | 10.5.3 three paragraphs | Codex T2-5c, T2-5d, T2-5e | verbatim |
| A44 | 10.5.1 paragraph after A38, "A harness timeout footer..." | Codex T2-5b | verbatim; replaces the draft's harness-footer bullet; moved from a 10.5.4 placed after T2-5d, whose "the prefix footer above" points to it (B.3 K1) |
| A45 | 10.6 paragraph | Codex T2-6b | verbatim; replaces the draft's Q4 premise |
| A46 | 10.7 precedence | draft 10.5.3, moved and edited | step 1 gains trace/1 dispatch (T2-6a) and `trace-off-not-comparable` (placement is a consolidation, see E4); step 4 loses "Because exhaustion no longer ends a run, a run failure here always has another cause" and its secondary-finding sentence becomes T2-5f's second sentence verbatim; step 7 continues past an aligned unknown (T2-4b); step 8 becomes unjudgeable and directed-only (T2-4b, T2-4c); step 9 names within-tolerance (T2-4b); a closing paragraph keeps v1.1's relative order inside steps 2 to 4 (B.3 K4) |
| A47 | 10.8 "Both producers" bullets 1 to 3 | consolidation from draft 10.6 | unchanged meaning |
| A48 | 10.8 "Instrumentation must preserve..." | Codex T2-3 (sentence outside the quote) | verbatim |
| A49 | 10.8 host bullets 1 and 2 | draft | unchanged |
| A50 | 10.8 host bullet 3 (tape) | draft, edited | "ends such a run complete" removed for the A36 reason |
| A51 | 10.8 host bullet 4 (stored provenance) | Codex T2-2, applied to the host | replaces the draft's "treats an action operand it cannot name through its map as a provenance fault" |
| A52 | 10.8 host bullet 5 (observation proof) | Codex T2-5e, applied to the host | new |
| A53 | 10.8 oracle paragraph | consolidation from Codex answers 1 and 4, T2-2 and T2-3 | the draft left it to Codex; Codex to confirm its wording |
| A54 | 10.8 comparer paragraph | draft plus Codex T2-1, T2-5c, T2-4c, T2-6a | scope list extended |
| A55 | 10.9 bullet 1 | draft plus Codex closure paragraph | adds "imageSet the member searched" because Codex requires "the exact code/operands/via" |
| A56 | 10.9 bullets 2 and 3 | draft | Codex's closure paragraph restates bullet 2 |
| A57 | 10.9 bullet 4 | Codex T2-7 (sentence outside the quote) | verbatim |
| A58 | draft 10.8 "Open for Codex before signature" | removed | all five answered: Q1 by A18 and A19; Q2 by A17; Q3 by A21; Q4 by A25 and A45; Q5 by A34 and A46 |

### B.2 Each Codex required edit and where it landed

Rows R1 to R13 are the required edits listed in message cad2be7d; R14 to R21 are the further directives in the review
file.

| # | Codex required edit | landed in |
|---|---|---|
| R1 | operand scalar and duplicate rules | A9, A10 (10.3.1); A54 |
| R2 | unclassified when optional attribution is unavailable; required map faults stay fatal | A18, A19 (10.3.3); A51 |
| R3 | Transform: no rollback | A29 |
| R4 | arrival: no rollback | A30 |
| R5 | actual oracle startup semantics | A25 (10.3.4), A45 (10.6), A53 |
| R6 | unknown-cause uncertainty only between paired errors with equal tick, sid, kind, via | A34 (T2-4a) |
| R7 | continue other event and state checks after an aligned unknown | A34 (T2-4b); A46 step 7 |
| R8 | trace-off is proof-only | A2, A42, A43; A46 step 1 |
| R9 | timeout evidence is prefix-based | A44 (10.5.1); T2-5d inside A43 |
| R10 | preserve trace/1 dispatch | A5 (10.2); A46 step 1 |
| R11 | preserve free-running dispatch | A34 (T2-4c); A46 steps 8 and 9 |
| R12 | classify independently of whether the peer emits an event | A21 |
| R13 | `unjudgeable:error-unclassified`, exit 2, blocking exact and tolerance success, after all independently provable directed failures | A2, A34 (T2-4b), A46 step 8 |
| R14 | parameter names: canonical built-in keys, exact custom names, explicit origin for synthetic ones | A17 |
| R15 | remove "a run failure here always has another cause" | A46 step 4; same reasoning applied in A36 and A50 |
| R16 | instrumentation preserves exception type, catch behaviour and state-write order | A48 |
| R17 | do not enforce a guessed causal stack | A23 |
| R18 | reserving a code does not claim every fault in its class is attributable | A13 |
| R19 | no broad policy exemption while N-parity-1107-03 is open | A57 |
| R20 | closure only on fresh paired fixtures with the exact code, operands and via; 1107-04 on the one-draw-short tape | A55, A56 |
| R21 | control and mutation matrix | Part D |

Count: 21 verbatim blocks (T2-1a, T2-1b, T2-1c, T2-2a, T2-2b, T2-2c, T2-3a, T2-3b, T2-3c, T2-4a, T2-4b, T2-4c,
T2-5a, T2-5b, T2-5c, T2-5d, T2-5e, T2-5f, T2-6a, T2-6b, T2-7) plus 6 directives outside the quotes (R15 to R20),
27 Codex edits in all. All 27 are adopted; none was rejected.

### B.3 Edits made by the independent check (Codex to confirm)

An independent check of this file (2026-11-07) changed no Codex wording. Each edit below is a placement, a pointer, a
missing control or a consolidation sentence, and Codex can strike any of them at signature.

| # | edit | why |
|---|---|---|
| K1 | T2-5b ("A harness timeout footer...") moved from its own 10.5.4 into 10.5.1, after T2-5a | T2-5d in 10.5.3 ends "can still receive the prefix footer above", but T2-5b sat below it in 10.5.4, so the pointer led nowhere; Codex's own order has T2-5b first |
| K2 | 10.5.2 bullet 1 also requires a false `tapeExhausted` in a traced `none` or `splitmix64` run | the bullet only defined agreement against a tape, so a forged true flag in a `none` trace had no class; T2-5a already says the flag is false there |
| K3 | 10.1 item 7, pointing to 10.6 | 10.6 changes how a trace/2 startup snapshot is judged (a timeout prefix of the starters is inconclusive, where the v1.1 comparer at `compare.py:453-461` reports `scenario-start-mismatch`; an observed wrong sid in a truncated trace is a mismatch, where v1.1 skips the check), so "exactly these changes" was incomplete |
| K4 | 10.7 closing paragraph: steps 2 to 4 keep the v1.1 comparer's relative order | step 3 said "unchanged from v1.1" but listed an order v1.1 does not use: a static `scenario-invalid` and `pack-invalid` come before either trace is read (`compare.py:417-418`, `:1690-1698`); `source-pin-mismatch` is decided inside the oracle's own validity (`:208-211`), before the host trace is read; the trace-derived `scenario-invalid` follows `scenario-start-mismatch` (`:447-466`), which the self-test "P2 input precedence" pins (`:908-930`) |
| K5 | Part D: CC12 gains the `none`/`splitmix64` forged flag; new rows CC17, CH20 and CO7 | Codex's matrix asks for "trace-off timeout without a tally", which had no row; CC17 makes K4 checkable |
| K6 | E10 reworded: such a fixture can at best grade `error-unclassified` | an unclassified error that is not aligned, or that sits beside another failure, keeps the ordinary class (T2-4a, T2-4b) |
| K7 | Inputs note: HEAD is now 51067d5 | `git diff --stat 422b004 HEAD -- tools/ShimejiParity src/dotNet/Shimeji` is empty, so every line cited here holds at 51067d5 |

---

## Part C. Disagreements for Codex

### C.1 Disagreements

None. Every host-semantics claim in the review matches the host code at HEAD 422b004 (C.2). Oracle-side claims
(`V1ScenarioRunner.java`, `DraftParityHooks.java`) were taken as given, per the consolidation rules. The converter
claims (`ShimejiParser.cs:479-487`, `TickPackEmitter.cs:718, 882-885, 1316-1341, 1412-1420`) were not re-read; the
parameter-name rule they support was adopted as Codex's decision. That map/2 has no parameter-name channel is
confirmed by the map/2 property list in `CONTRACT.md:372-376`.

### C.2 Host-code checks of Codex's host-semantics claims

All paths are under `src/dotNet/Shimeji/` at HEAD 422b004 unless they start with `tools/`.

| # | Codex claim | verdict | file:line that decides it |
|---|---|---|---|
| H1 | Transform keeps the new image set and every write already made; no rollback | Codex right | `TickRuntime.cs:1627` assigns `mascot.Type` before the build; `:1630` clears ArrivalBehaviour; `:1631` writes the transform event; `:1639-1641` catch only reports, with no restore and no Dispose |
| H2 | the previous behaviour survives only when failure precedes assignment | Codex right | `TickRuntime.cs:1242-1245` assigns Behaviour and writes the behavior event (`:1243`) before Init (`:1245`); a lookup failure throws earlier, at `:1537-1538`, inside the build on `:1637`. The source comment at `:1632-1634` ("in the behaviour it was in") is accurate only for build failures; see E3 |
| H3 | the transform consequence applies to what the transform wrapper catches | Codex right; the via rule A10 and A22 covers the rest | `TickRuntime.cs:1639` catches only TickCantBeAliveException; any other exception reaches the step catch-all `:1261-1267`, which disposes, and carries no `transform` entry |
| H4 | arrival transitions are sequential and nothing is rolled back | Codex right | `TickSocialFamily.cs:79-80` seeker transition, `:81` null-target throw, `:83-84` target transition, `:85-86` TargetLook, `:88-91` catch only reports (no Dispose, no restore) |
| H5 | the host disposes a failed starter before it joins the manager | Codex right | `TickRuntime.cs:1381` catches every exception, `:1388` disposes, `:1389` returns before `Add` on `:1391` |
| H6 | the draft's universal scenario-start-mismatch premise is false | Codex right | host: `ParityRun.cs:1133` status starts `complete`, `:1150-1155` carry on to ActivateStartup and the tick-0 states of the others, `:1178` writes the footer. So a one-starter host run is complete with no tick-0 state and is `malformed` at `tools/ShimejiParity/comparer/compare.py:309-310`, before binding; a multi-starter run lacks one sid and is `scenario-start-mismatch` at `compare.py:459-461`; an oracle no-state failed run is skipped at `compare.py:455-456`; without a scenario the oracle's aborted footer grades `aborted` at `compare.py:468-469` before events. The draft's "startup errors are compared only without a scenario" is false for the same reason |
| H7 | a constructor failure cannot read the reference identity applied afterwards | Codex right | `TickRuntime.cs:1713` builds the child, `:1714` sets `RefKey` only after it returns; a host constructor fault of this kind is `TickMotionFamily.cs:358-361` (MoveWithTurn with fewer than two variants) |
| H8 | under policy:animate-no-variant the host ends normally | Codex right | `TickRuntime.cs:584-590`: HasNext returns false with no fault when no variant holds |
| H9 | names must be captured before null-to-empty normalization | consistent; the host has that normalization | `TickRuntime.cs:1527, 1537, 1550, 1686` (`name ?? ""`); `:517` EvalString returns null for a null value |
| H10 | (draft claim kept) no wrapper rethrows, so via holds at most one entry | holds on the host | the seven catches `TickRuntime.cs:904-911, 1381-1390, 1475, 1482, 1639-1642` and `TickSocialFamily.cs:88-91, 400-403` do not rethrow; no wrapper body can reach another wrapper, because Breed (`TickRuntime.cs:818`; `TickMotionFamily.cs:397, 422, 447`; `TickSocialFamily.cs:502, 633`), Arrive (`TickSocialFamily.cs:189, 257, 344, 508, 618`), Transform (`TickRuntime.cs:929`) and Interact's Behavior (`TickSocialFamily.cs:396-403`) run only inside Tick bodies, and wrapper bodies only build and initialise (`TickRuntime.cs:970-998` never steps). Oracle side is Codex's |
| H11 | the host writes no error event on a missing-member fallback | host fact recorded for E1 | `TickPacks.cs:121` (the diagnostic is not an observer event), `:168` ResolveReference, `:192` reports through the host callback only |
| H12 | trace-off footers carry the counters that identify them | consistent with A42 | `ParityRun.cs:899-900` writes `unobservedDraws`, `unobservedEvents` and `eventSequenceSha256` on a trace-off footer |
| H13 | (draft claims kept) press and release dispose; birth refuses after the event | holds on the host | `TickRuntime.cs:1475, 1482` (report, Dispose "error"); `:904-910` (ReportFault on the parent, then BirthRefused "error") |
| H14 | (agreed host change, not a disagreement) tape exhaustion ends the host run in error today | change required by 10.5 | `ParityRun.cs:478` returns 0.5 and flags it; `:1161` sets status `error` and breaks |

---

## Part D. Control and mutation matrix

Rule for every row: the control proves the guard passes on good input; the mutation breaks that one condition and
must produce exactly the named failure, naming the right file. Nothing in this matrix has been run.

### D.1 Codex: comparer

| # | control | expected | mutation | expected under mutation |
|---|---|---|---|---|
| CC1 | each typed code with exactly its operand set | validates | drop the operand-set check | a missing or extra operand passes instead of `missing-field` |
| CC2 | scalar types: imageSet object, behavior number, empty refKey, empty parameter, via not an array, unknown via label | `missing-field` each | drop the type check | the case passes |
| CC3 | pair differing only in behavior `null` vs `""`, and `" "` vs `""` | `event-mismatch` each | normalize names before comparing | `exact` |
| CC4 | unknown code; duplicate property in `error` and in `lookup` | `missing-field` | parse objects last-wins | duplicate passes |
| CC5 | non-null lookup for `unclassified`; null lookup for a typed code | `missing-field` | drop the null rule | passes |
| CC6 | same paired cause, different messages | trace/2 `exact`; trace/1 `event-mismatch` | compare message in trace/2, or drop it in trace/1 | the inverse result on that leg |
| CC7 | mixed versions, with a build-witness defect in one trace | `trace-version-mismatch`, exit 2 | run body validation first | `build-witness-missing` |
| CC8 | aligned unknown cause (one side unclassified; both sides unclassified), with and without an anchor tolerance in use | `unjudgeable` `error-unclassified`, exit 2 | return the success verdict | `exact` or `within-tolerance:anchor` |
| CC9 | extra unknown error; missing unknown error; unknown error at a different tick, sid or via | `extra-event`, `missing-event`, ordinary mismatch class | treat any divergence involving `unclassified` as uncertain | `error-unclassified` |
| CC10 | aligned unknown followed by an ordinary event mismatch; by a state mismatch | `event-mismatch`; `state-mismatch` | stop at the aligned unknown | `error-unclassified` |
| CC11 | startup legs: complete/no-state; aborted/no-state; proper timeout prefix of the starters; wrong observed sid, order or pending flag; later record proving the snapshot finished short | `malformed`; `aborted`; the run-failure class, not a start mismatch; `scenario-start-mismatch`; `scenario-start-mismatch` | relabel startup faults as start mismatches, or drop the prefix exception | wrong class on the matching leg |
| CC12 | normal tape; one-draw-short tape on both sides; past-tape value 0.7; forged flag (true with no past-tape draw, false with one, true in a `none` or `splitmix64` trace) | `exact`; `fail` `rng-tape-exhausted` with complete footers; `rng-invalid`; `malformed` | drop each check in turn | that leg passes or misclassifies |
| CC13 | timeout plus proven exhaustion; timeout with exhaustion unproven | `timeout` with secondary `rng-tape-exhausted`; `timeout` alone | let exhaustion outrank the run failure, or report it unproven | `rng-tape-exhausted` as the class, or a secondary finding without evidence |
| CC14 | trace-off artifact on either side | `trace-off-not-comparable`, exit 2 | validate it by `unobservedDraws` instead of refusing | another class |
| CC15 | free-running pair with an aligned unclassified error; with a malformed error object | `time-share-pass`; `missing-field` | apply step 8 to free-running | `error-unclassified` |
| CC16 | trace/1 pair with error events | v1.1 result unchanged | route trace/1 through trace/2 rules | message difference stops failing |
| CC17 | trace/2 pairs: a static scenario error with a malformed host body; a wrong oracle source pin with a malformed host body; a start mismatch with an inactive button target | `scenario-invalid`; `source-pin-mismatch`; `scenario-start-mismatch` (v1.1 order, 10.7) | apply the step 2 and 3 lists as a strict order | `malformed`; `malformed`; `scenario-invalid` |

### D.2 Codex: oracle

| # | control | expected | mutation | expected under mutation |
|---|---|---|---|---|
| CO1 | constructor fault reached through a reference, with and without builder/reference context | typed with the reference's refKey; otherwise `unclassified` | write the declaration's key | wrong refKey in a paired fixture |
| CO2 | localized built-in parameter; custom parameter; synthetic animation-condition parameter | canonical key; exact name; `unclassified` without explicit attribution | expose the synthetic name | a typed code with a non-source name |
| CO3 | Transform lookup failure vs Init failure; partial arrival | events and states as its own runtime produces them, no rollback | add a restore | state mismatch against the host fixture |
| CO4 | startup fault | current aborted/error footer, no synthetic snapshot | manufacture a tick-0 snapshot | changes the CC11 leg |
| CO5 | one-draw-short tape; trace-off run | continues on 0.5, `tapeExhausted` in every footer, counters on trace-off | stop at exhaustion | footer `error` |
| CO6 | classification sites | no code derived from message text | add a message-based classifier | the source check fails |
| CO7 | observation proof over an exhausted tape; a trace-off run stopped by a timeout before a durable tally | the proof compares draw count with `unobservedDraws`, both flags, final states and digest, and requires completion; the timeout gives an unverified or run-failure result, with no `tapeExhausted=false` inferred (10.5.3) | drop the flag comparison, or accept the timeout | a forged flag or an incomplete run passes the proof |

### D.3 Claude: host producer

| # | control | expected | mutation | expected under mutation |
|---|---|---|---|---|
| CH1 | any run | header `shimeji-parity-trace/2` | write trace/1 | `trace-version-mismatch` against a trace/2 oracle |
| CH2 | blank BornBehavior | error at the parent sid, `behavior-missing`, imageSet the child's member after BornMascot fallback, behavior `""`, via `["birth"]`, then `birth-refused` reason `error` (`TickRuntime.cs:904-910`) | drop the via entry, or take the parent's member | `event-mismatch` in the closure fixture |
| CH3 | blank TransformBehavior | `behavior-missing`, imageSet the target member, behavior `""`, via `["transform"]`; previous behaviour kept, new type kept (`TickRuntime.cs:1627, 1637-1641`) | take imageSet from the old type | `event-mismatch` |
| CH4 | Transform whose new behaviour fails in Init | behavior event to the new behaviour, then the error; new behaviour stays assigned (`TickRuntime.cs:1242-1245`) | add a restore | the host's state check fails |
| CH5 | arrival whose seeker transition succeeds and TargetBehavior lookup fails | seeker in its new behaviour, target unchanged, TargetLook not applied, via `["arrive"]` (`TickSocialFamily.cs:79-91`) | move TargetLook before the target transition | target facing changes |
| CH6 | requested names `""`, `" "`, `null` | three distinct operands, captured before `name ?? ""` (`TickRuntime.cs:1527, 1537, 1550, 1686`) | capture after normalization | `null` written as `""` |
| CH7 | MoveWithTurn with one variant reached through an ActionReference (`TickMotionFamily.cs:358-361` inside `TickRuntime.cs:1713`) | `unclassified` unless the reference key is carried into construction; never the declaration key | write the declaration's key | wrong refKey |
| CH8 | built-in, custom and synthetic parameter faults | canonical key; exact source name; `unclassified` | write the synthetic name | a typed code with a non-source name |
| CH9 | non-TickCantBeAliveException in a step; in startup | `unclassified`, via `[]` (`TickRuntime.cs:1261-1267`); `unclassified`, via `["startup"]` (`:1381-1389`) | type it from the message | the CO6-style source check fails |
| CH10 | press, release and interact faults | via `["press"]`, `["release"]`, `["interact"]` with their consequences (`TickRuntime.cs:1475, 1482`; `TickSocialFamily.cs:400-403`) | drop a label | `event-mismatch` |
| CH11 | a fault reported where it arises (`TickRuntime.cs:492`, not a number) | via `[]` | add a wrapper label | via mismatch |
| CH12 | every error message | byte-identical to today's golden list | change one message | the golden check fails naming that message |
| CH13 | one-draw-short tape; normal tape; `none`; `splitmix64` | steps to N, footer `complete`, `tapeExhausted` true, informational log line; false for the others | restore `ParityRun.cs:1161`'s status `error` and break | footer `error` |
| CH14 | trace-off run | footer carries `tapeExhausted` with `unobservedDraws` and `unobservedEvents` (`ParityRun.cs:899-900`); the proof compares draw count with `unobservedDraws`, both flags, final states and digest, and requires `complete` | drop the flag comparison | a forged flag passes the proof |
| CH15 | a required map binding missing | provenance fault, footer `error` (`ParityRun.cs:1188-1191`), never `unclassified` | downgrade it to `unclassified` | the run ends `complete` |
| CH16 | classification sites | no code derived from a message string; the check asserts where the code is set, not that a statement exists | add a message-based classifier | the source check fails |
| CH17 | existing engine and parity self-tests | unchanged, which shows exception types, catch behaviour and state-write order are kept | reorder a write inside a wrapper | the CH4 order witness fails |
| CH18 | missing BornMascot or TransformMascot member | still no error event (`TickPacks.cs:121, 192`) | emit an observer event | the host self-test fails |
| CH19 | failed starter | stays disposed and absent from tick 0 (`TickRuntime.cs:1381-1391`); trace/2 adds no snapshot | add the starter to tick 0 | a state line the oracle never writes |
| CH20 | trace-off run stopped by a timeout before its footer tally | the proof reports unverified or a run failure, never success, and infers no `tapeExhausted=false` from the absent draw lines (10.5.3) | accept the incomplete artifact as a passed proof, or infer the flag | a proof passes without a durable tally |

### D.4 Both: closure fixtures (fresh paired trace/2 runs)

| fixture | required result |
|---|---|
| S-breed-blankbehavior-01 | `exact`, error as 10.9 with via `["birth"]` |
| S-transform-blankbehavior-01 | `exact`, error as 10.9 with via `["transform"]` |
| one-draw-short tape scenario | `fail` `rng-tape-exhausted`, both footers `complete`, `tapeExhausted` true |

---

## Part E. Open items (not blockers for signing)

1. Missing-member event details need a fresh synthetic witness. The host writes no event on a missing-member
   fallback (C.2 H11); whether the oracle writes one is unconfirmed from repo-only evidence. N-parity-1107-03 stays
   open, and no broad exemption is added (10.9).
2. Interact has the same assign-before-Init shape as Transform (`TickSocialFamily.cs:398` into
   `TickRuntime.cs:1242-1245`), so an Init failure leaves the new behaviour assigned. The draft's "the companion carries
   on" is kept because Codex did not change it; Codex may add a no-rollback sentence at signature.
3. Host source comment `TickRuntime.cs:1632-1634` says a failed Transform carries on "in the behaviour it was in",
   which is true only for build failures (C.2 H2). Correct it in the host producer change; it does not affect the
   contract.
4. Two consolidation choices sit on the comparer side and are Codex's to confirm: the footer-counter rule that
   identifies a trace-off artifact (A42), and placing `trace-off-not-comparable` in precedence step 1, ahead of body
   validity, so a trace-off trace is never misreported as `malformed` by 10.5.2 (A46). The independent check's edits
   K1 to K7 (B.3) are Codex's to confirm in the same pass.
5. "Paired" in T2-4a is read as the comparer's existing positional alignment: the same index in both event
   sequences, every earlier pair matched or itself an aligned unknown. The comparer self-test should pin that reading.
6. T2-4c's last sentence refers to the draft's "step-8 pending rule". Step 8 is now unjudgeable and directed-only, so
   the sentence is kept verbatim but reads historically; reword or drop it at signature.
7. 10.6 calls an interrupted startup prefix "inconclusive" without naming what follows. The expected reading is that
   the start check stays silent and the run-failure class (`timeout` or `truncated`) decides; confirm in CC11.
8. 10.4 says a draw in a `none` scenario yields 0.5 but makes only a past-tape non-0.5 value `rng-invalid`. Whether a
   `none` draw recording another value is also `rng-invalid` is left for a later revision.
9. `startup` errors are typed and validated, but no current pair reaches their comparison (C.2 H6). Normalizing
   startup outcomes is out of scope for trace/2.
10. Typed codes for constructor faults and synthetic parameters wait for explicit builder/reference and origin context
    on both sides. Until then those faults are `unclassified`, and a fixture that hits one can at best grade
    `error-unclassified` (when the pair is aligned and nothing else fails), never `exact` (B.3 K6).
11. Merge chores in CONTRACT.md: title to version 1.2; pointers to section 10 from section 2's tape bullet, section 3's
    v1.1 `tapeExhausted` extension note and footer, and section 6's class lists (the three new unjudgeable classes); a
    v1.2 integration record once both halves land.
12. Next step for the orchestrator: send this file's path and SHA-256 to Codex for the joint signature before briefing
    either producer.
