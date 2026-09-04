using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Modules
{
    // =====================================================================================
    // DesktopAICompanion plugin ABI (v1). A module is a class implementing IModule, shipped as a DLL that
    // references ONLY this assembly. The host loads it (in an isolated AssemblyLoadContext), calls
    // Init(host) once, and Shutdown() on unload. In Init the module subscribes to host lifecycle
    // events, calls host services, and registers its tray/options contributions. Everything here is
    // handle-based (ICompanion, not the app's FormCompanion) and framework-agnostic (no WinForms/WPF/System.Drawing)
    // so the contract stays small and stable as the host evolves.
    // =====================================================================================

    /// <summary>A pixel rectangle, so the ABI carries no System.Drawing dependency.</summary>
    public readonly struct PixelRect
    {
        public readonly int X, Y, Width, Height;
        public PixelRect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
    }

    /// <summary>Identity + requirements a module declares to the host.</summary>
    public sealed class ModuleInfo
    {
        public string Id { get; set; }              // stable, unique, path-safe (e.g. "sound", "fortunes")
        public string Name { get; set; }            // display name
        public string Version { get; set; }         // module version (semver-ish string)
        public string MinHostVersion { get; set; }  // minimum host/ABI version this module needs
        public ModulePermissions Permissions { get; set; }
    }

    /// <summary>Coarse capability flags a module declares (surfaced to the user at install/consent time).</summary>
    [Flags]
    public enum ModulePermissions
    {
        None = 0,
        Speech = 1 << 0,        // calls Say/SayAll
        Animation = 1 << 1,     // plays animations
        ScreenContext = 1 << 2, // reads foreground window / captures the screen
        Network = 1 << 3,       // makes network requests
        Hotkey = 1 << 4,        // registers a global hotkey
        Storage = 1 << 5,       // reads/writes its own data folder
        Companions = 1 << 6,          // enumerates/spawns/previews/installs pet types (see IHost.GetCompanionManager)
        Audio = 1 << 7,         // plays sound through the app's shared audio output (IHost.PlaySound)
        Voice = 1 << 8,         // sees every line the pet is about to say, and may speak it instead of
                                // showing the bubble (IHost.RegisterSpeechResponder)
        Microphone = 1 << 9,    // captures the microphone (records audio input)
        SystemAudio = 1 << 10,  // captures the system audio output / loopback (records what you hear)
    }

    /// <summary>An on-screen pet, as seen by a module (opaque handle over the host's FormCompanion).</summary>
    public interface ICompanion
    {
        int Id { get; }
        bool IsBusy { get; }   // being dragged / mid-interaction
        // Which pet TYPE this instance is: a folder/catalog id, "eSheep" for the built-in default, or "" when
        // the host cannot resolve one. This is the only join between the event stream (which hands out bare
        // pet handles) and the type-keyed ICompanionManager verbs -- without it a module can receive pets and
        // enumerate types but never correlate the two.
        string TypeId { get; }
    }

    // ---- lifecycle event payloads ----
    public sealed class PokeInfo { public ICompanion Pet { get; set; } public int PokeCount { get; set; } }
    // IdleContext and AnimationInfo were removed before the host froze. Their events (CompanionIdle,
    // AnimationStarted) were declared and bridged but never raised by the host, and shipping a
    // declared-but-silent event in a final contract is a trap: a module author subscribes, sees nothing, and
    // there is no host release left to fix it in. Raising them honestly was the alternative and cost more
    // than it was worth -- the host has no idle policy at all (the real idle predicate, a screen-change
    // delta, lives in the AI-brain module and would have had to ignore a generic host tick), and
    // AnimationInfo.AnimationId is an index into one pet's own XML with no name field and no enumeration
    // verb, so making it usable meant ADDING ABI. Sound reaches the host's own AudioOutput directly, which is
    // what left SoundData/SoundLoop unreachable when the Sound module was retired.

    /// <summary>
    /// One utterance, offered to the speech responders before any bubble is drawn.
    ///
    /// Claiming and suppressing are two separate knobs on purpose. That is what makes the three sensible
    /// behaviours expressible without overloading one bool: bubble only (decline), bubble AND spoken (claim,
    /// leave SuppressBubble false), or spoken INSTEAD of the bubble (claim, set SuppressBubble).
    /// </summary>
    public sealed class SpeechRequest
    {
        public string Text { get; set; }    // host-filled; changing it has no effect
        public ICompanion Pet { get; set; }       // host-filled; null when every pet would have said it (SayAll)
        /// <summary>Module-set: "I am speaking this, do not draw the bubble." Read only from a responder
        /// that claimed the line.</summary>
        public bool SuppressBubble { get; set; }
        /// <summary>
        /// Host-supplied, ONE-SHOT: draw this utterance's bubble now, with an optional extra dwell in seconds
        /// (0 = the user's configured duration).
        ///
        /// It exists because the responder is synchronous and on the UI thread, so a module must decide
        /// whether to claim BEFORE it knows whether its synthesis will succeed. Handing the line back by
        /// calling Say/SayAll does NOT work: SayAll compares against the last line said and, with the default
        /// suppress-repeats preference on, would swallow the replay entirely. Only the host can bypass both
        /// the chain and that guard, so only the host can offer this. Also the way to show a bubble in step
        /// with the audio rather than before it.
        /// </summary>
        public Action<double> ShowBubble { get; set; }
    }

    /// <summary>Lightweight, on-UI-thread screen context (foreground window + the pet's monitor).</summary>
    public sealed class ScreenContext
    {
        public string WindowTitle { get; set; }
        public string ProcessName { get; set; }
        public PixelRect MonitorBounds { get; set; }
        public string WindowUnderCompanion { get; set; }   // title of the window the pet is standing on (screen-zone awareness), or null
    }

    /// <summary>A tray context-menu entry contributed by a module (merged with core items by group/order).</summary>
    public sealed class TrayItem
    {
        public string Label { get; set; }                 // display text ('&' marks the mnemonic)
        public int Group { get; set; }                    // items are grouped (separators between groups)
        public int Order { get; set; }                    // order within a group
        public Func<bool> Visible { get; set; }           // re-evaluated when the host signals a state change (null => always)
        public Func<string> DynamicText { get; set; }     // overrides Label each show (e.g. Enable/Disable) (null => Label)
        public Action Click { get; set; }                 // leaf action (null for a pure submenu)
        public Func<IEnumerable<TrayItem>> BuildChildren { get; set; } // lazy submenu, rebuilt on open (null for a leaf)
        public byte[] IconPng { get; set; }               // optional PNG-encoded icon bytes (null => no icon); kept
                                                            // as raw bytes, not a concrete image type, so the
                                                            // contract carries no System.Drawing dependency
    }

    // ---- options schema (host renders a consistent UI; a module ships no UI code) ----
    // Info is display-only: the host renders the value as text with no editor and never collects it back,
    // so a module can explain state the user can't otherwise see (e.g. "no fortunes match your filters, so
    // the pet will stay silent") instead of failing quietly. A value starting with ✓/✗ is coloured like an
    // action result, matching what the buttons already do.
    public enum SettingKind { Bool, Int, Text, Enum, Secret, Info }

    public sealed class SettingField
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public SettingKind Kind { get; set; }
        public string[] Options { get; set; }   // for Enum
        public int Min { get; set; }             // for Int
        public int Max { get; set; }             // for Int
        // Optional grouping: fields (and PaneActions) sharing a Group name render in one titled card, and
        // cards flow into responsive columns. null/"" => an untitled default card. Additive since the
        // grouped-settings layout; older modules that don't set it get one default card.
        public string Group { get; set; }
    }

    /// <summary>An action button on an options pane (e.g. "Test connection", "Clear history"). The host
    /// renders a button; on click it disables it, shows a working hint, awaits <see cref="InvokeAsync"/>
    /// (so a slow network probe never freezes the UI), then shows the returned status string. Runs on the
    /// UI thread; the delegate should offload any blocking work itself (await I/O).</summary>
    public sealed class PaneAction
    {
        public string Label { get; set; }
        public Func<System.Threading.Tasks.Task<string>> InvokeAsync { get; set; }
        // Optional: render this action inside the titled card of the matching field Group (e.g. a
        // "Test connection" button in the "Provider" card). null/"" => the untitled default card.
        public string Group { get; set; }
        // Optional: after this action runs, ask the host to rebuild the pane so refreshed Load() values
        // show (e.g. a "reset to defaults" that rewrites several settings). Default false. The delegate may
        // also set this from inside InvokeAsync — the host reads it after awaiting — so an action can decline
        // the reload (e.g. when the user cancels a confirmation).
        public bool ReloadPaneAfter { get; set; }
    }

    /// <summary>One checkable row in a <see cref="ListCard"/>: a stable <see cref="Id"/> (passed back to the
    /// toggle callback), a display <see cref="Label"/>, an optional <see cref="Detail"/> (secondary text, e.g.
    /// a line count), and its current <see cref="Checked"/> state.</summary>
    public sealed class ListItem
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Detail { get; set; }
        public bool Checked { get; set; }
        // Optional grouping label. When any item in a card sets it, the host renders one collapsible
        // section per distinct group (items without one fall under "Other") instead of a flat list —
        // the difference between a browsable 150-pack card and an unreadable wall of checkboxes.
        public string Group { get; set; }
    }

    /// <summary>A dynamic, checkable list rendered as one titled card alongside the schema fields — for
    /// content a flat <see cref="SettingField"/> can't express (installed fortune packs, genres, ...). The
    /// module supplies the current items (<see cref="LoadItems"/>, re-read on each pane build) and a toggle
    /// callback (<see cref="SetChecked"/>, invoked per click with the item Id + new state — or once per
    /// changed item at Apply, see <see cref="DeferChanges"/>); optional card-level buttons reuse
    /// <see cref="PaneAction"/> (set <see cref="PaneAction.ReloadPaneAfter"/> on a mutating button — e.g.
    /// rescan/import — to refresh the card). Data + delegates only, so the ABI stays framework-agnostic:
    /// the host owns the WPF.</summary>
    public sealed class ListCard
    {
        public string Title { get; set; }
        public Func<IReadOnlyList<ListItem>> LoadItems { get; set; }
        public Action<string, bool> SetChecked { get; set; }
        public IReadOnlyList<PaneAction> Actions { get; set; }
        public string EmptyHint { get; set; }   // shown when LoadItems returns nothing
        // Ask the host for a filter box above the list (live substring match over label/detail/group).
        // Worth setting for any card that can hold more than a screenful.
        public bool Filterable { get; set; }
        // Start collapsed when the card is grouped, so a long list opens as a short list of section
        // headers the user expands, rather than every row at once. Ignored when nothing sets a Group.
        public bool CollapseGroups { get; set; }
        // Set when SetChecked is expensive (it re-reads content, rebuilds an index, ...). The host then
        // treats a click as an edit rather than a command: the box moves at once, the pane goes dirty so
        // Apply lights up, and SetChecked runs once per CHANGED item when the user applies, immediately
        // before OptionsPane.Save — so the module can stage the ids and commit them all in Save. Unapplied
        // ticks are discarded on close or on a ReloadPaneAfter action, exactly like unapplied field edits.
        // Leave false for a card whose ticks feed a button rather than the saved settings (a download
        // basket), where deferring would mean the button sees an empty selection.
        public bool DeferChanges { get; set; }
    }

    /// <summary>
    /// One item a module can download from the app's content catalog (a fortune pack today). Metadata
    /// only: the host owns the URL, its HTTPS/repository validation, and the SHA-256 verification, so a
    /// module never builds a download URL or decides for itself whether bytes are trustworthy.
    /// </summary>
    public sealed class CatalogItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }         // browsing group ("" when ungrouped)
        public string Description { get; set; }
        public int Bytes { get; set; }
        public int Count { get; set; }            // entries inside the item (lines in a pack); 0 when n/a
    }

    /// <summary>Catalog sections a module can browse (see <see cref="IHost.FetchCatalogItemsAsync"/>).</summary>
    public static class CatalogKinds
    {
        public const string Pack = "pack";        // fortune packs
        public const string Pet = "pet";          // pet types (animations.xml)
    }

    /// <summary>An installed pet TYPE, as the pet manager enumerates it.</summary>
    public sealed class CompanionTypeInfo
    {
        public string TypeId { get; set; }       // folder/catalog id; "eSheep" for the built-in default
        public string DisplayName { get; set; }  // character/display name ("Pearl")
        public bool IsBuiltIn { get; set; }
    }

    /// <summary>A count of on-screen pets of one type.</summary>
    public sealed class CompanionCount
    {
        public string TypeId { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// A live TRANSIENT "preview" pet, owned by the module that spawned it (see
    /// <see cref="ICompanionManager.SpawnPreview"/>). It is a real pet on the real desktop, but deliberately
    /// invisible to everything persistent or shared: never written to the app's settings, never restored on
    /// the next launch, never listed in the tray's "Remove a pet" submenu, never reachable by
    /// <see cref="ICompanionManager.RemoveOne"/>, and never announced through <see cref="IHost.CompanionSpawned"/> (nor
    /// made the subject of CompanionPoked/CompanionLanded) — so an author re-previewing their XML twenty times does not
    /// fire twenty welcome fortunes at the user.
    ///
    /// It DOES occupy one of <see cref="ICompanionManager.MaxCompanions"/> slots, so remove it when the user is done
    /// looking. Every preview also dies with the process. <see cref="Remove"/> is idempotent and
    /// <see cref="IDisposable.Dispose"/> calls it, so a <c>using</c> block is a safe way to hold one.
    /// </summary>
    public interface ICompanionPreview : IDisposable
    {
        /// <summary>The preview's pet handle — pass it to Say / TryPlayAnimation / CaptureScreenContext.
        /// Null once the preview has been removed.</summary>
        ICompanion Pet { get; }
        /// <summary>False once the preview is gone (removed by this module, or by the host at shutdown).</summary>
        bool IsAlive { get; }
        /// <summary>Remove it now. Safe to call twice.</summary>
        void Remove();
    }

    /// <summary>
    /// Host service for inspecting, authoring and placing pets, so a module can validate, preview, install
    /// and spawn pet types without owning the host's pet list, its type registry, or its persistence.
    /// Reached via <see cref="IHost.GetCompanionManager"/> (kept off IHost so the root surface stays thin); the
    /// calling module must declare <see cref="ModulePermissions.Companions"/>. Every member runs on the UI thread
    /// and never throws — the fallible ones report a reason instead. A type id is a folder/catalog id, or
    /// "eSheep" for the built-in default; "" means "the active/default pet".
    ///
    /// Deliberately ABSENT, and not by oversight: there is no "use this pet" verb. That operation writes the
    /// pet's XML into the app's settings, closes every pet on screen and resets the persisted mix, and the
    /// host's own Pets pane and tray already own it. A frozen host keeping its most destructive verb to
    /// itself is a feature. Per-type size, sound and voice are likewise absent: those are user preferences
    /// the host's Pets pane owns, and a module writing them would fight that pane with no arbitration.
    /// </summary>
    public interface ICompanionManager
    {
        // ---- inspect ----
        // The writable pet library on disk (…\pets), where InstallType lands a pet and where the user's
        // installed/downloaded pets live. A pet-authoring module uses it to open a file dialog where the
        // author's pets already are, rather than guessing the host's folder layout. Read-only, absolute,
        // and possibly not-yet-created; "" when the host cannot resolve it. Added after the freeze (1.4.6)
        // — a module that reads it must declare MinHostVersion 1.4.6 or the load-time check refuses it.
        string CompanionsDirectory { get; }
        IReadOnlyList<CompanionTypeInfo> InstalledTypes();
        // Read an installed pet type's animations.xml by id -- the writable library, then the bundled pets
        // beside the exe, then the built-in default -- so an authoring/analysis module can open a pet the user
        // already has without knowing (or being able to reach) the host's folder layout. Returns false with a
        // reason when the id is unknown/unsafe or the Pets permission is missing. Added in 1.8.0; a module that
        // calls it must declare MinHostVersion 1.8.0 or the load gate refuses it.
        bool TryReadTypeXml(string typeId, out string animationsXml, out string error);
        // Live pets counted by type, in first-appearance order. PREVIEW pets are deliberately not counted,
        // so this can sum to less than MaxCompanions while IsAtMax is already true.
        IReadOnlyList<CompanionCount> OnScreenMix();
        int MaxCompanions { get; }
        bool IsAtMax { get; }

        // ---- place ----
        bool SpawnOne(string typeId);    // one more pet of an INSTALLED type; false at MaxCompanions / unknown id
        bool RemoveOne(string typeId);   // remove the most recent pet of a type; false when none. Never a preview.

        // ---- author ----
        // Validate a pet XML against the HOST's own parser and limits without spawning anything, so an
        // authoring module ships no schema of its own and its verdict cannot drift from what the host will
        // actually run.
        bool ValidateXml(string animationsXml, out string error);
        // Spawn a transient preview pet from an arbitrary, not-yet-installed animations.xml (validated first,
        // by that same parser). Returns null with a reason when the XML is rejected, the pet cap is reached,
        // too many previews are already up, or the Pets permission is missing. The caller OWNS the returned
        // handle and must remove it.
        ICompanionPreview SpawnPreview(string animationsXml, out string error);
        // Install an authored (or downloaded and decoded) pet type into the user's pet library — the host
        // owns the safe-id check, the validation and the path containment — or remove an installed one.
        // After a successful install the id appears in InstalledTypes() and can be spawned. Neither touches
        // pets already on screen.
        bool InstallType(string typeId, string animationsXml, out string error);
        bool UninstallType(string typeId, out string error);
    }

    /// <summary>A module's settings pane. Declarative schema (host-rendered) is the default; secrets are
    /// write-only and never read back into the UI.</summary>
    public sealed class OptionsPane
    {
        public string Title { get; set; }
        public IReadOnlyList<SettingField> Schema { get; set; }

        // Optional action buttons (S5b), rendered below the fields — the schema is data-only, so anything
        // that runs module behavior (test a connection, clear history, ...) is expressed here.
        public IReadOnlyList<PaneAction> Actions { get; set; }

        // Optional dynamic list cards (S5b): checkable lists a flat schema can't express (fortune packs,
        // genres). Each renders as a titled card alongside the schema groups.
        public IReadOnlyList<ListCard> Lists { get; set; }

        // Persistence (S5b): the module owns its own store (which may be richer than the host's
        // IModuleSettings — e.g. DPAPI-scoped keys), so it supplies these. Load returns the current value of
        // each schema field by Id (Secret fields: return "" or omit — never the plaintext; a non-empty value
        // just signals "a secret is set" for a placeholder hint). On Apply the host calls Save with the
        // edited values by Id; a Secret key is present ONLY when the user typed a new value (blank = keep the
        // stored one). Save returns false if persistence failed. Null Load/Save => a display-only pane.
        public Func<IReadOnlyDictionary<string, string>> Load { get; set; }
        public Func<IReadOnlyDictionary<string, string>, bool> Save { get; set; }
    }

    /// <summary>Per-module writable data folder (host-provisioned, path-isolated).</summary>
    public interface IModuleStorage
    {
        string DataDirectory { get; }
    }

    /// <summary>Per-module persisted settings (string values; the host owns the atomic/locked writer and
    /// encrypts values whose field Kind is Secret).</summary>
    public interface IModuleSettings
    {
        string Get(string key, string fallback);
        int GetInt(string key, int fallback);
        bool GetBool(string key, bool fallback);
        void Set(string key, string value);
        bool Save();
    }

    /// <summary>
    /// Optional per-message speech-bubble styling a module passes with <see cref="IHost.Say(ICompanion,string,SpeechStyle)"/>
    /// / <see cref="IHost.SayAll(string,SpeechStyle)"/>. Every field is "unset" by default (null string / 0 /
    /// false), meaning "use the bubble's default look". A module owns its OWN style (typically loaded from its
    /// settings via ModuleKit's SpeechStyleSettings); the host is a dumb renderer that never stores a style or
    /// attributes one to a module. Additive: later fields default to the current look, so older callers are
    /// unaffected.
    /// </summary>
    public sealed class SpeechStyle
    {
        public string FontFamily { get; set; }   // null/blank => the bubble default (Segoe UI)
        public float FontSize { get; set; }        // points; <= 0 => default (9)
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public string TextColor { get; set; }      // "#RRGGBB" or "#AARRGGBB"; null/blank/invalid => default (black)
    }

    /// <summary>The host surface a module talks to. Events fire on the UI thread; services must be called
    /// on the UI thread unless noted.</summary>
    public interface IHost
    {
        string HostVersion { get; }
        bool SpeechEnabled { get; }
        double Volume { get; }          // 0..1
        // The display name the pet should address the user by, shared across modules (e.g. the AI brain
        // publishes it via SetOwnerName; the fortunes welcome reads it). "" when no module has set one, so
        // a consumer falls back to its own default (e.g. the Windows user name).
        string OwnerName { get; }

        // ---- lifecycle events (subscribe in Init) ----
        // Every event here is RAISED by the host. If you are adding one, wire the raise in the same change:
        // CompanionIdle and AnimationStarted were removed at the freeze precisely because they were not.
        event Action<ICompanion> CompanionSpawned;
        event Action<PokeInfo> CompanionPoked;
        event Action<ICompanion> CompanionLanded;
        event Action HostShutdown;

        // ---- host services ----
        // Say(pet, ...) is the DEFAULT for anything that is a reaction -- a poke, a drop, an answer, a landing
        // greeting. It belongs to one pet. SayAll is for announcements to the USER rather than to a pet (the
        // tray's speech test, a once-per-session welcome); with several pets on screen it makes all of them
        // say the same line at the same instant, which reads as a bug, because it mostly was one.
        // Speaking to a pet that has gone away is dropped, not redirected -- see IsCompanionAlive.
        void Say(ICompanion pet, string text);
        void SayAll(string text);
        // As Say/SayAll, but with an optional per-message SpeechStyle (font family/size, colour, bold/italic/
        // underline) the bubble renders. A null style -- or any unset field within it -- renders exactly as the
        // plain Say/SayAll, so this is a pure superset. Added in 1.8.0: a module that calls these must declare
        // MinHostVersion 1.8.0 or the load gate refuses it.
        void Say(ICompanion pet, string text, SpeechStyle style);
        void SayAll(string text, SpeechStyle style);
        bool TryPlayAnimation(ICompanion pet, string animationName);
        // Play an emotion on every live pet: for each pet, the first candidate its XML actually
        // defines wins (the caller owns the emotion->animation-name mapping). Parallels SayAll.
        void PlayAnimationAll(IReadOnlyList<string> animationCandidates);
        ScreenContext CaptureScreenContext(ICompanion pet);
        IDisposable RegisterHotkey(string combo, Action onPressed);
        IModuleStorage GetStorage(string moduleId);
        IModuleSettings GetSettings(string moduleId);

        // Publish the preferred user display name (see OwnerName). A module that owns the user's name (the AI
        // brain) sets it when enabled and clears it ("") when off, so other modules address the user the same.
        void SetOwnerName(string name);

        // Periodic "say something?" tick, arbitrated across modules by priority (higher first) until one
        // handler returns true (handled). Lets the AI module outrank Fortunes for the shared drop loop.
        IDisposable RegisterDropResponder(int priority, Func<bool> onDrop);

        // The FIRST poke of a fresh right-click session ("say something about this?"), arbitrated the same
        // way as the drop: highest priority first until one handler returns true (handled). Separate from
        // the CompanionPoked event, which stays a plain broadcast every module sees: this chain is the one that
        // gets to SPEAK, so exactly one module wins it. The base's own poke escalation (ignore -> sass ->
        // escape) is unaffected and still runs off the raw poke count. A module registered here is also
        // what the user's "Trigger Speech" preference selects between (registration id = module id).
        IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke);

        // ---- pet-aware responders (host 1.5.0+): PREFER THESE ----
        // Same two arbitrated chains, but told WHICH pet the reaction belongs to. Use them: a reaction is
        // always about one pet, and answering with SayAll makes every pet on screen say the same line at the
        // same moment -- which is what the argument-less versions above forced, because the host had no way to
        // tell a module who was poked. Both styles share ONE priority order, so mixing them is safe and a
        // module that has not migrated still competes fairly.
        //
        // Deliberately NOT overloads of the two members above. A parameterless `delegate { ... }` converts to
        // both Func<bool> and Func<ICompanion,bool> with no better-conversion tie-breaker, so overloading would make
        // `RegisterDropResponder(0, delegate { return true; })` fail to compile as CS0121 for anyone who
        // recompiles -- and LangVersion 7.3 means that spelling is everywhere. Binary compatibility would have
        // survived; source compatibility would not.
        IDisposable RegisterCompanionDropResponder(int priority, Func<ICompanion, bool> onDrop);
        IDisposable RegisterCompanionPokeResponder(string moduleId, int priority, Func<ICompanion, bool> onPoke);

        // Is this pet still on screen? A module can hold an ICompanion indefinitely -- there is no CompanionRemoved event
        // -- so a handle captured before a slow await may name a pet the user has since removed. Check before
        // acting on a stale handle; speaking to a dead pet is silently dropped rather than shown somewhere else.
        //
        // On IHost rather than ICompanion on purpose: ICompanion is implemented by module test doubles (ModuleKit ships
        // FakeCompanion), so adding a member there would break modules on recompile. IHost is implemented only by
        // hosts and their fakes.
        bool IsCompanionAlive(ICompanion pet);

        // ---- fullscreen (host 1.9.9+) ----
        // True while a FULLSCREEN window exists on ANY monitor -- foreground or not. Deliberately not "the
        // foreground window is fullscreen": an alt-tabbed game still owns its VRAM and its exclusive swap
        // chain, so the thing a module must avoid disturbing is still there when the game is not focused.
        // Maximised windows do NOT count; they leave the taskbar visible and are ordinary windows.
        //
        // This is the same scan that already keeps pets off a fullscreen game (FullscreenScan), exposed rather
        // than reimplemented, so there is one implementation of one policy and it is the one with the
        // self-test. Cheap to call: the answer is cached on the scan the pets already run.
        //
        // The motivating case is a local LLM: several GB of VRAM claimed while a game already owns it can take
        // the game down, so a module should decline the work and fall back to something free.
        bool IsFullscreenActive { get; }

        // Raised when IsFullscreenActive changes, with the new value. Lets a module react to a game STARTING
        // rather than discovering it at its own next tick -- which matters when the reaction is "release the
        // VRAM you are holding", because by the next tick the damage is done.
        event Action<bool> FullscreenChanged;

        // ---- audio (host 1.6.0+) ----
        // Play a sound through the app's SHARED output: the same mixer and device the pet's own animation
        // sounds use, so one volume and one device picker govern everything the app emits. `audio` is a
        // self-describing container the host decodes (WAV or MP3), which keeps sample formats out of the
        // contract entirely -- ModuleKit's WavAudio.FromPcm wraps raw samples for you.
        // `volume` is 0..1 RELATIVE to the user's master volume, which the host applies: a module can be
        // quieter than the pet, never louder, and a master of 0 means silence.
        // Requires ModulePermissions.Audio. Returns false whenever nothing will be heard -- refused, no usable
        // device, muted, undecodable, more than 2 channels, or over the size cap. That single bool is what
        // lets a caller fall back to showing a bubble instead.
        //
        // Deliberately no completion callback and no IsSoundPlaying: a caller knows the duration of the audio
        // it just produced (sample count / sample rate) and can time its own queue, whereas a callback would
        // mean invoking module code from the audio callback thread.
        bool PlaySound(string moduleId, byte[] audio, double volume);

        // Stop whatever this module is currently playing -- barge-in, or the user switching the voice off.
        // Only ever affects that module's own sounds, never the pet's animation audio. NOT permission-gated:
        // refusing a module the right to go quiet is nonsense, and it is strictly weaker than what it already
        // did to make the sound. True when something was actually cut.
        bool StopSound(string moduleId);

        // ---- speech interception (host 1.6.0+) ----
        // Offered every utterance BEFORE any bubble is drawn, highest priority first, until one responder
        // returns true. Returning true means "I OWN THE OUTPUT of this line; stop offering it" -- which is NOT
        // the same as "I spoke it". Whether a bubble appears is the separate SpeechRequest.SuppressBubble.
        // With nothing registered this is a no-op and behaviour is byte-for-byte what shipped before.
        //
        // Requires ModulePermissions.Voice, checked when the chain is RAISED rather than here: ModuleHost
        // calls Init before adding a module to its loaded list, so a registration-time check would answer
        // false for the very module registering and silently refuse everyone.
        IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech);

        // Browse and download a module's downloadable content from the app's HTTPS-fetched, SHA-256-pinned
        // catalog (kind = one of CatalogKinds). The HOST performs the catalog fetch, the per-asset URL
        // validation, the download, and the hash verification; the module decides only what to keep and
        // where to write it inside its own storage. Both throw on a network/verification failure, so a
        // caller reports the message rather than trusting partial content.
        System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind);
        System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id);

        // The pet inspection/authoring/placement service (see ICompanionManager). Never null: when the named module
        // has not declared ModulePermissions.Companions a refusing instance is returned (enumerations come back
        // empty, fallible verbs return false with a reason), matching how RegisterHotkey degrades to a no-op
        // handle rather than throwing into a module.
        ICompanionManager GetCompanionManager(string moduleId);

        // True when the app is presenting itself dark. A module that owns a WINDOW needs this to match the
        // rest of the app, and it cannot work it out for itself: the user's choice is light / dark / SYSTEM,
        // and only the host knows which of those is set. Reading the OS theme directly (the only option
        // before this existed) is right for "system" and wrong the moment someone pins the opposite.
        // Re-read it when you build UI rather than caching: a preference change takes effect on the next open.
        bool IsDarkTheme { get; }

        // Write a line to the app's own diagnostic log, tagged with the calling module's id. Before this, a
        // module's only way to report anything was to make the pet SAY it, which is not a diagnostic channel.
        // Deliberately not behind a permission: it is strictly less capable than the storage a module already
        // has, and the alternative is modules inventing private log files nobody knows to look at. Cheap and
        // best-effort -- it never throws, and it is dropped when the log is unavailable.
        void Log(string moduleId, string message);

        // Ask the user to pick existing files. The HOST owns the dialog, so a module needs no UI framework
        // of its own (modules stay data + delegates). Returns the chosen full paths, or an empty list when
        // the user cancels. Extensions are bare, dot-less ("txt"); call from a PaneAction (UI thread).
        IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions);

        // Open an HTTPS link in the user's browser (e.g. "where do I download this optional dependency?").
        // Requires the calling module to declare ModulePermissions.Network — launching a browser is weaker
        // than the raw network access that flag already grants, but it IS user-visible, so it stays behind
        // a declared capability rather than being free to any module. The host validates the URL (HTTPS,
        // real host, no userinfo, length-bounded) and swallows failures. Returns false when refused.
        bool OpenLink(string moduleId, string httpsUrl);

        // ---- shared context (host 1.9.0+) ----
        // A tiny host-mediated key/value channel so one module can hand a fact to another WITHOUT a direct
        // reference — modules load in separate contexts and cannot call each other. The publisher owns its key
        // namespace by convention (dotted, e.g. "meeting.current"); the value is an opaque JSON string the host
        // never parses. Reading an unset key returns "". ContextChanged fires with the key that changed, on the
        // UI thread, so a reader reacts without polling. Live process state, not settings: it is unpersisted and
        // cleared on restart, and deliberately ungated (it is strictly weaker than the storage a module has).
        // A module that calls these must declare MinHostVersion 1.9.0 or the load gate refuses it.
        void PublishContext(string moduleId, string key, string valueJson);
        string ReadContext(string key);
        event Action<string> ContextChanged;

        // ---- contributions (register in Init) ----
        void AddTrayItems(IEnumerable<TrayItem> items);
        void AddOptionsPane(OptionsPane pane);
    }

    /// <summary>A DesktopAICompanion plugin. Implemented by exactly one public class per module DLL.</summary>
    public interface IModule
    {
        ModuleInfo Info { get; }
        void Init(IHost host);
        void Shutdown();
    }
}
