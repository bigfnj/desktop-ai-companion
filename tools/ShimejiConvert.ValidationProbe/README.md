This probe source-links the host's actual XML DTO, XSD, script parser, and validators from an explicit `HostSourceRoot`. It calls `CompanionXmlValidator.TryParseWithFeatures` with `CompanionFormatFeatures.All` for each emitted XML. It also requires rejection of copies with missing feature declarations and a route pointing to itself. Pack outputs get another control that removes only the `packs` token and requires a diagnostic naming `packs`.

Inputs with per-pose geometry get separate rejection controls for zero or oversized dimensions, anchors outside the supported range, and explicit right-facing dimensions without a right-facing frame. Cell dimensions come from the input sheet and tile counts.

Every input must have a sibling parity-map.json. The probe checks every visible pose against its left and explicit-right frame's effective anchor and image size, using the unscaled rule from the host's ParityMap.CheckGeometry. Separate controls change each map anchor/size component and an explicit-right anchor, and require a diagnostic naming the frame. This consistency check supplements the XML validator; a real headless host run remains the integration proof. The runner hashes both XML and map before and after execution.

Run `Run.ps1 -HostSourceRoot <host-tree> -XmlPath <member/animations.xml>[,<another/animations.xml>] -OutputRoot <fresh-evidence-folder>`. In PowerShell, pass multiple paths as an array. Optional `-ExpectedCommit <full-sha>` also requires the seven linked files to match that commit before and after execution. An immutable source snapshot can be supplied without `ExpectedCommit`; its exact source hashes are recorded.

The runner uses child-only isolation settings, builds into the fresh evidence folder, records source and input hashes, and checks that the executed assembly matches the fresh build. It leaves the evidence folder for review.

This is a core format validation probe. `TickFeatures.cs` and the application runtime are excluded. The explicit feature override permits checking proposed format output; a passing result does not establish production runtime support or behavioral parity.
