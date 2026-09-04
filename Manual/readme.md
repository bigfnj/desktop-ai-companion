# Manual artifacts and authority

This directory contains historical documentation artifacts plus an optional Sandcastle project.

- `Documentation.chm` and `Manual - online editor.docx` are retained historical artifacts. They
  are not shipped and do not define current product behavior.
- `DesktopAICompanion.shfbproj` is the sole supported Sandcastle generator. It may generate a current
  developer API reference under `Manual/generated-current/` and must never write into `docs/`.
- `docs/` is an immutable DesktopAICompanion 1.0.6 API snapshot. Its landing page identifies it as
  historical.
- The current [architecture guide](../grimoire/02-architecture.md), repository
  [README](../Readme.md), and [release checklist](../docs/RELEASE-CHECKLIST.md) are the maintained
  documentation. The release checklist is the authority for distribution readiness.

No manual or generated documentation is part of the canonical runtime payload.
