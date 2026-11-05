# Shimeji action parity status

No action family has a supported parity verdict yet. Support requires controlled oracle and host traces judged
under the frozen parity contract maintained by the parity lane (tools/ShimejiParity/CONTRACT.md after integration).
A completed host run alone does not establish it.

The test-only command emits the initial literal subset from the ordered source IR:

```text
ShimejiConvert.exe paritypack <source-pack> <new-output-directory> [main-image-set]
```

Each image-set directory gets `animations.xml` and `parity-map.json`. The map preserves source configuration
locations, separate next-reference behaviours, frame image paths, authored anchors and image sizes. Root choices
retain declaration order and zero frequencies. The output directory must be new and outside the source pack.
Cross-member output requires the caller to name its main image set. Such output declares packs, gives every member
one shared content-derived pack id, and labels the other members form. It omits own-set member defaults and keeps a
binding when dropping it would expose a different inherited member.

| Source action | Converter stage | Supported | Evidence available |
|---|---|---|---|
| Stay | Literal lowering | pending | Host execution: S-slice-stand-walk-01 |
| Move | Literal lowering | pending | Host execution: S-slice-stand-walk-01 |
| Animate | Literal lowering | pending | Synthetic converter checks |
| Sequence, Select | Literal lowering | pending | Synthetic graph and binding checks |
| Embedded Breed | Literal lowering | pending | Host execution: S-slice-breed-01 |
| Embedded Transform | Literal lowering | pending | Host execution: S-slice-transform-01 |
| Embedded Fall, Dragged | Reserved definitions retained | pending | Present in slice packs; action semantics unexercised |
| Other embedded classes | Refused by this initial lowering | pending | No tick conversion evidence |

This command targets the headless parity runner. It does not infer egg/helper roles or startup metadata and does not
replace the classic conversion commands or Studio import. It refuses expressions, unsupported attributes and
classes, sounds, hotspots, constants, toggles, information metadata, anonymous
nested actions and next references without a root definition. This first emitter refuses non-base action schemas.
Bindings across differing document schemas are refused until target-schema resolution is implemented.

Sheet cells use the maximum source image width and height, with each image copied at its cell's top-left.
Every visible pose carries its image size and effective anchor. Explicit right-facing images carry their own
size and frame; their pixels are not mirrored. The image-pair cache retains the first loaded anchor for repeated
left/right path pairs, including pairs in unused actions. The parity map retains each frame's authored anchor.
Distinct source image paths keep separate frame identities. Pixel copying preserves alpha and ignores sprite DPI;
images are not resized. A member-local ImageRight must start with a separator. Right-only poses are refused
because they can depend on an earlier cache entry. Java hexadecimal floating literals are refused until the
host's literal parser can preserve their meaning.

Synthetic self-tests run through the existing emitter suite. Private slice traces and source archives remain
outside the repository. Controlled oracle traces and class-specific branch scenarios are required before any
row above changes to supported.
