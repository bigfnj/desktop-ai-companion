# Third-party modules — roadmap

**Status: design notes, nothing built.** The SDK that exists today (contract + ModuleKit + template +
packaging + [authoring guide](module-authoring.md)) is aimed at *first-party* modules living in this repo.
This file records what a genuinely open ecosystem would additionally need, and the questions to answer before
building any of it, so the decisions aren't re-derived from scratch later.

Read [`module-authoring.md`](module-authoring.md) first; this only covers what changes when the author isn't
us.

---

## What already works in our favour

- **The contract is frozen.** `DesktopAICompanion.Contracts` has `AssemblyVersion 1.0.0.0` permanently, so a module
  built today keeps binding to it. That is the hard part of a plugin ecosystem, already done.
- **Modules are isolated by construction.** Each loads in its own collectible `AssemblyLoadContext`, the
  loader logs-and-skips every per-module failure, and `MinHostVersion` is enforced before `Init`. One bad
  third-party module cannot take the companion down.
- **Consent already exists.** The Modules pane shows a module's declared `ModulePermissions` before download,
  and every payload is HTTPS + SHA-256 pinned.
- **Install/update/remove are solved**, including the locked-DLL problem (deferred to next launch) and
  preserving a module's data directory across an update.

So the gap is not the runtime. It is **trust** and **discovery**.

---

## 1. Trust: code signing + consent

**The model to copy is VS Code / Notepad++, not a sandbox.** A module is an ordinary in-process assembly with
the user's full privileges; pretending otherwise would be security theatre. What that buys us is *attribution*
plus *informed consent*, which is honest and achievable, rather than *containment*, which is not.

Sketch:

- **Verify an Authenticode signature** on the module DLL at load, and surface the publisher in the Modules
  pane. `ModuleHost.LoadFrom` is already the single choke point.
- **Trust is per publisher, not per module.** Consent once for "Jane Smith", then her later modules install
  without re-prompting; revoking her trust disables all of them.
- **Unsigned modules are sideload-only** — installable by hand with a blunt warning, never offered by a
  catalog.
- **Record what was consented to.** Permissions plus publisher, so a module that later wants `Network` when it
  had `Speech` re-prompts instead of silently widening.

Open questions:

1. Do we *require* signing for catalog listing? A certificate costs real money and would exclude hobbyists —
   the exact people most likely to write a companion module. A middle path: allow unsigned in the catalog but badge
   it loudly and default to "signed only".
2. What happens to a module whose certificate expires or is revoked after install? Keep running (it worked
   yesterday) or refuse (revocation means something)?
3. Do we pin a publisher's key on first trust, so a stolen-cert swap is detectable?

## 2. Discovery: a third-party catalog

Today `catalog.json` is generated from this repo and served from `master` — implicitly trusted because we
wrote it. A third-party marketplace needs:

- **A separate signed index**, so a listing can't be forged, with our first-party catalog remaining a distinct
  and more-trusted tier in the UI.
- **Submission and review** — even a light "does it load, does it declare honest permissions, does it have a
  self-test" pass. This is the part with an ongoing human cost, and the reason not to build it before there is
  demand.
- **Reporting/removal**, and a kill-list the app can honour for a module found to be malicious.

Open questions:

1. Who hosts the index and who pays for the bandwidth of a popular 30 MB module?
2. Is a review queue sustainable for a hobby project, or should the ecosystem be curated-links-only ("here are
   modules we like") — which is far cheaper and nearly as useful at small scale?
3. Versioning across a third-party catalog: the existing update rule (`offered > installed`) is fine, but who
   arbitrates two modules claiming the same id?

## 3. Building outside this repo

Today a module is a project inside the repo with `ProjectReference`s up the tree. An external author needs:

- **`DesktopAICompanion.Contracts` and `DesktopAICompanion.ModuleKit` as NuGet packages.** Contracts is already
  package-shaped: frozen `AssemblyVersion`, no dependencies. ModuleKit needs a real version policy once it is
  published, since it would then move independently of the app.
- **The template as a NuGet template package**, so `dotnet new install DesktopAICompanion.Templates` works without
  cloning. The current template's `ProjectReference`s to `..\..\src\...` become `PackageReference`s — the one
  change that matters.
- **A self-test story that doesn't need the host.** `ModuleKit.Testing.RecordingHost` already covers most of
  it; the missing piece is the *loader* half (does the module load in a real collectible ALC with a shared
  contract). A small published test harness would close that — a version of the throwaway runner used while
  building the SDK.
- **Docs published somewhere reachable**, i.e. the authoring guide as a GitHub Pages page rather than a repo
  file.

Open questions:

1. Do external modules get a compatibility promise beyond the frozen contract? ModuleKit going 2.0 shouldn't
   break them, which argues for keeping ModuleKit's public surface conservative from now on.
2. Should the host expose its version and ABI capabilities more richly than one `MinHostVersion` string, so an
   external module can degrade rather than refuse (feature-detect instead of version-gate)?

---

## Suggested order, if this is ever picked up

1. **NuGet-publish Contracts + ModuleKit + the template.** Lowest cost, immediately unblocks anyone motivated,
   and requires no policy decisions.
2. **Signature verification + publisher display**, defaulting to permissive. Attribution without gatekeeping.
3. **A curated links page** instead of a marketplace, to find out whether demand is real.
4. **Only then** a submission/review pipeline, and only if step 3 shows it is warranted.

The honest read: steps 1–2 are a weekend and make the ecosystem *possible*; steps 3–4 are an ongoing
commitment and should wait for evidence that anyone outside this repo wants to write a module at all.
