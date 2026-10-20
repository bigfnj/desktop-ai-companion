# Module template: maintainer notes

`dotnet new desktop-ai-companion-module` scaffolds from this folder. This README is excluded from the
output by `.template.config/template.json`, so nothing here reaches a scaffolded module.

## Why the project file is `SampleModule.templateproj`

`dotnet new` renames it to `<Name>.csproj` (the `rename` entry in `template.json`, then `sourceName`).
It is not stored as a `.csproj` because the raw file is not a buildable project: its `<!--#if (standalone) -->`
blocks are XML comments to MSBuild, so both branches are live, and the standalone branch references the
packages at the literal version `SAMPLE_PKG_VERSION`. Anything that discovers projects by `**/*.csproj`
then tries to restore it and fails with "'SAMPLE_PKG_VERSION' is not a valid version string". C# Dev Kit
does exactly that whenever a folder without a root solution is opened (this repo, or a parent folder
holding several repos), and because it restores one generated solution in a single pass, the one bad
project left every other project in the window unrestored.

The extension still matches the template engine's `**/*.*proj` glob, so the MSBuild-style conditionals
are processed exactly as before. A name that does not end in `proj` (`SampleModule.csproj.template`,
say) gets renamed but leaves both branches in every scaffolded module.

Measured 2026-10-20: modules scaffolded from this layout and from the old `SampleModule.csproj` were
byte-identical, both with and without `--standalone`. `packaging/Test-ModuleTemplate.ps1` proves the
default scaffold still builds and loads in the real host.

Because the file is not a `.csproj`, `git ls-files '*.csproj'` does not count it, so it is not one of the
projects the Readme's project count refers to.
