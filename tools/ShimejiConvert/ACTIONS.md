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
| Stay | Literal lowering | pending | Host execution: S-slice-stand-walk-01 |
| Move | Literal lowering | pending | Host execution: S-slice-stand-walk-01 |
| Animate | Literal lowering | pending | Synthetic converter checks |
| Sequence, Select | Literal lowering | pending | Synthetic graph and binding checks |
| Embedded Breed | Literal lowering | pending | Host execution: S-slice-breed-01 |
| Embedded Transform | Literal lowering | pending | Host execution: S-slice-transform-01 |
| Embedded Fall, Dragged | Reserved definitions retained | pending | Present in slice packs; action semantics unexercised |
| Embedded Jump | lowered; host trace complete; oracle comparison pending | pending | S-jump-arc-01, S-jump-flat-01, S-jump-reaim-01 |
| Embedded MoveWithTurn | lowered; host trace complete; oracle comparison pending | pending | S-move-withturn-01 |
| Embedded Regist | lowered; host trace complete; oracle comparison pending | pending | S-input-regist-02 |
| Embedded BreedMove, BreedJump, BreedFall | lowered; host trace complete; oracle comparison pending | pending | S-breedmove-interval-01, S-breedmove-interval-02, S-breedmove-turn-01, S-breedjump-interval-01, S-breedjump-interval-02, S-breedfall-interval-01, S-breedfall-interval-02 |
| Embedded ScanMove | lowered; host trace complete; oracle comparison pending | pending | S-scan-first-01, S-scan-loss-01 |
| Embedded ScanInteract | lowered; host trace complete; oracle comparison pending | pending | S-scan-reacquire-01 |
| Embedded Interact | lowered; host traces complete but end both Huddle companions at tick 0 (a start companion has no manager); oracle comparison pending | pending | S-interact-arrive-01, S-interact-shared-01 |
| Embedded WalkWithIE | lowered; host trace complete; oracle comparison pending | pending | S-window-carry-on-01, S-window-carry-off-01, S-window-throwing-off-01 |
| Embedded ThrowIE | lowered; host trace complete; oracle comparison pending | pending | S-window-throw-on-01, S-window-throw-off-01 |
| Embedded MinimizeIE | lowered; host trace complete; oracle comparison pending | pending | S-window-minimize-on-01, S-window-minimize-off-01 |
| Animation Hotspot (shape, origin, size, behaviour) | lowered; host trace complete; oracle comparison pending | pending | S-input-hotspot-01 |
| Literal Affordance (`<offers>`) | lowered; host trace complete; oracle comparison pending | pending | S-scan-first-01, S-scan-loss-01, S-scan-reacquire-01 |
| Toggleable behaviours (header `<toggles>`, behaviour `toggle`) | lowered; synthetic converter checks only | pending | No directed scenario yet |
| Embedded Look, Turn, Offset, Mute, BringToFront, SelfDestruct, ScanJump, ComplexMove, ComplexJump, FallWithIE, JumpWithIE, Broadcast, BroadcastStay, BroadcastMove, BroadcastJump | lowered; synthetic converter checks only | pending | No directed scenario yet |
| `${}` / `#{}` parameters and bindings | lowered; host trace complete; oracle comparison pending | pending | S-vars-cache-01 |
| Scripted action Condition (cached parameter, guard when) | lowered; host trace complete; oracle comparison pending | pending | S-vars-transform-01 |
| Scripted Animation Condition (cached parameter, variant when) | lowered; synthetic converter checks only | pending | No directed scenario yet |
| Scripted Behavior and `<Condition>` selection conditions | lowered; synthetic converter checks only | pending | No directed scenario yet |

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
scripts of that map see it; one nothing reads is refused, as before. A script's zero-argument JavaBean getter call
(`getScreen()`, `isLookRight()`) is written as the property it reads, which Nashorn evaluates with the same call. A Behavior's children other than its
NextBehaviorList elements (a NextBehavior among them) are ignored, as 1.0.23 never reads them, and so is any child
of a behaviour list other than a Condition and a Behavior (root list) or BehaviorReference (next list).
It refuses unsupported attributes and
classes, sounds, constants and next references without a root definition. An information document (info.xml) is
accepted when it holds only <Information> (chooser and credits metadata, which no mascot reads); a Constant, an
ActionList or a BehaviourList in it is refused.
Bindings across differing document schemas are refused until target-schema resolution is implemented.
Composite activation cycles, depth above 32 and expansion above 4096 actions are refused before output is written.

Sheet cells use the maximum source image width and height, with each image copied at its cell's top-left.
Every visible pose carries its image size and effective anchor. Explicit right-facing images carry their own
size and frame; their pixels are not mirrored. The image-pair cache retains the first loaded anchor for repeated
left/right path pairs, including pairs in unused actions. The parity map records that effective anchor so it
agrees with every pose using the frame; the source IR retains each pose's authored value.
Distinct source image paths keep separate frame identities. Pixel copying preserves alpha and ignores sprite DPI;
images are not resized. A member-local ImageRight must start with a separator. Right-only poses are refused
because they can depend on an earlier cache entry. Java hexadecimal floating literals are refused until the
host's literal parser can preserve their meaning.

Synthetic self-tests run through the emitter suite and `selftest pack-conversion`. Private slice traces and source archives remain
outside the repository. Controlled oracle traces and class-specific branch scenarios are required before any
row above changes to supported.
