# Shimeji action parity status

No action family has a supported parity verdict yet. Support requires controlled oracle and host traces judged
under the frozen parity contract maintained by the parity lane (tools/ShimejiParity/CONTRACT.md after integration).
A completed host run alone does not establish it.

The test-only command emits the initial literal subset from the ordered source IR:

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

This command targets the headless parity runner. It does not infer egg roles or startup metadata and does not
replace the classic conversion commands or Studio import. It refuses expressions, unsupported attributes and
classes, sounds, hotspots, constants, toggles, information metadata, anonymous
nested actions and next references without a root definition. This first emitter refuses non-base action schemas.
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

Synthetic self-tests run through the existing emitter suite. Private slice traces and source archives remain
outside the repository. Controlled oracle traces and class-specific branch scenarios are required before any
row above changes to supported.
