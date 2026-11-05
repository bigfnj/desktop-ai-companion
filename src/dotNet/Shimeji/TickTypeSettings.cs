using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The user's stored choices for tick companions, per companion type (the app keeps them in its settings document;
    /// a test keeps them in memory). Keyed by the type's id, which is the source's image set: the source keys each of
    /// these by the companion's CURRENT image set (Main.java:1382-1389, and DisabledBehaviours from :1420), so a
    /// transformed companion reads its new type's choices.
    /// </summary>
    internal interface ITickSwitchStore
    {
        /// <summary>The user's choice for one switch of one type, or null while the user has not chosen (the switch's
        /// default applies).</summary>
        bool? Get(string typeId, string key);
    }

    /// <summary>
    /// The per-type switches of a tick companion and how each is decided (grimoire/06 section 2 `&lt;toggles&gt;`, and
    /// the plan's design contract 10): Breeding, Transients and Transformation default on, as the source's; window actions
    /// (<see cref="TickWindowFamily.Setting"/>) default OFF, the owner's decision, so a window action plays its animation
    /// and moves nothing until the user lets the type move windows (policy:window-off-plays); and one switch per header
    /// toggle, defaulting to the toggle's own `default`. A key this list does not name is off. Throwing, the source's own
    /// window switch, is not a user switch and is not decided here.
    /// </summary>
    internal static class TickTypeSwitches
    {
        public const string Breeding = "Breeding";
        public const string Transients = "Transients";
        public const string Transformation = "Transformation";
        public const string WindowActions = TickWindowFamily.Setting;

        /// <summary>A header toggle's key: the prefix and the toggle's id.</summary>
        public const string TogglePrefix = "toggle:";

        public static string ToggleKey(string toggleId) { return TogglePrefix + toggleId; }

        /// <summary>The default of a named per-type switch, or null when <paramref name="key"/> is not one.</summary>
        public static bool? DefaultOf(string key)
        {
            switch (key)
            {
                case Breeding:
                case Transients:
                case Transformation: return true;
                case WindowActions: return false;
                default: return null;
            }
        }

        /// <summary>ITickSettings.Get for a named per-type switch: the user's choice for the type, else the default. Any
        /// key this class does not name is OFF, so a misspelt or new switch never turns a behaviour on.</summary>
        public static bool Get(ITickSwitchStore store, string typeId, string key)
        {
            bool? fallback = DefaultOf(key);
            if (!fallback.HasValue) return false;
            return Stored(store, typeId, key) ?? fallback.Value;
        }

        /// <summary>Configuration.isBehaviorEnabled: a behaviour with no toggle is always enabled; one with a toggle is
        /// what the user chose for that toggle in <paramref name="typeId"/>'s settings, else the toggle's default.</summary>
        public static bool IsBehaviourEnabled(ITickSwitchStore store, string typeId, TickBehaviourDef behaviour)
        {
            if (behaviour == null || string.IsNullOrEmpty(behaviour.Toggle)) return true;
            return Stored(store, typeId, ToggleKey(behaviour.Toggle)) ?? behaviour.ToggleDefault;
        }

        /// <summary>A pane row's current state: the user's choice, else the row's default.</summary>
        public static bool IsOn(ITickSwitchStore store, string typeId, TickSettingRow row)
        {
            return Stored(store, typeId, row.Key) ?? row.DefaultOn;
        }

        private static bool? Stored(ITickSwitchStore store, string typeId, string key)
        {
            if (store == null || string.IsNullOrEmpty(typeId)) return null;
            return store.Get(typeId, key);
        }
    }

    /// <summary>One switch the Pets pane offers for a tick companion type.</summary>
    internal sealed class TickSettingRow
    {
        public string Key;
        public string Label;
        public string Tooltip;
        public bool DefaultOn;
    }

    /// <summary>
    /// Which switches a tick companion type is offered, decided from its compiled definition: only what the type uses.
    /// Breeding and Transients when an action that runs breeds, with BornTransient deciding which of the two governs it
    /// (TickBreedDelegate.IsEnabled, the source's Breed.Delegate.isEnabled); Transformation when one runs Transform;
    /// "Let it move windows" when one runs a window-family class; then one row per header toggle. An action counts only
    /// when something runs it (a behaviour, or a composite's child), and its parameters are read as each of those
    /// references gives them, because a reference's own parameters replace the action's (ActionBuilder.createActions).
    /// A scripted value could be either, so it offers both rows.
    /// </summary>
    internal static class TickSettingRows
    {
        /// <summary>The classes that breed every time they run (Breed.java and the moving breeds that share its
        /// delegate).</summary>
        private static readonly string[] BreedClasses = { "Breed", "BreedMove", "BreedJump", "BreedFall" };

        /// <summary>The classes that breed when their Characteristics name Breed (ComplexMove.java:71-77,
        /// ComplexJump.java:80-86).</summary>
        private static readonly string[] ComplexClasses = { "ComplexMove", "ComplexJump" };

        public static List<TickSettingRow> For(TickType type)
        {
            var rows = new List<TickSettingRow>();
            if (type == null) return rows;
            bool breeding = false, transients = false, transform = false, windows = false;
            foreach (TickActDef act in type.Actions.Values)
            {
                if (string.IsNullOrEmpty(act.Class)) continue;
                List<Dictionary<string, TickParamDef>> uses = UsesOf(type, act);
                if (uses.Count == 0) continue;
                if (act.Class == "Transform") transform = true;
                else if (IsWindowClass(act.Class)) windows = true;
                bool always = Array.IndexOf(BreedClasses, act.Class) >= 0, complex = Array.IndexOf(ComplexClasses, act.Class) >= 0;
                if (!always && !complex) continue;
                foreach (Dictionary<string, TickParamDef> binds in uses)
                {
                    if (complex && !MayBreed(Read(act, binds, "Characteristics"))) continue;
                    TickParamDef transient = Read(act, binds, "BornTransient");
                    // Absent, BornTransient reads false (EvalBool's fallback): Breeding governs the birth.
                    if (transient == null) { breeding = true; continue; }
                    if (transient.Program == null && transient.Literal.Kind == ScriptValueKind.Boolean)
                    {
                        if (transient.Literal.IsTruthy) transients = true;
                        else breeding = true;
                        continue;
                    }
                    breeding = true;
                    transients = true;
                }
            }
            if (breeding)
                rows.Add(new TickSettingRow
                {
                    Key = TickTypeSwitches.Breeding, Label = "Breeding", DefaultOn = true,
                    Tooltip = "lets it make more of itself; the ones it makes leave when the app closes",
                });
            if (transients)
                rows.Add(new TickSettingRow
                {
                    Key = TickTypeSwitches.Transients, Label = "Transients", DefaultOn = true,
                    Tooltip = "lets it make short-lived companions, such as things it throws",
                });
            if (transform)
                rows.Add(new TickSettingRow
                {
                    Key = TickTypeSwitches.Transformation, Label = "Transformation", DefaultOn = true,
                    Tooltip = "lets it turn into another form",
                });
            if (windows)
                rows.Add(new TickSettingRow
                {
                    Key = TickTypeSwitches.WindowActions, Label = "Let it move windows", DefaultOn = false,
                    Tooltip = "lets it carry, throw or minimise a window it stands on; off, it only acts it out",
                });
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (TickToggleDef t in type.Toggles)
                if (t != null && !string.IsNullOrEmpty(t.Id) && seen.Add(t.Id))
                    rows.Add(new TickSettingRow
                    {
                        Key = TickTypeSwitches.ToggleKey(t.Id), Label = string.IsNullOrEmpty(t.Label) ? t.Id : t.Label,
                        DefaultOn = t.Default, Tooltip = "one of this companion's own behaviours",
                    });
            return rows;
        }

        /// <summary>Each reference that runs <paramref name="act"/>, as the parameters it passes: a behaviour that runs
        /// it (its binds), or a composite child that names it (that child's binds).</summary>
        private static List<Dictionary<string, TickParamDef>> UsesOf(TickType type, TickActDef act)
        {
            var uses = new List<Dictionary<string, TickParamDef>>();
            foreach (TickBehaviourDef b in type.BehavioursById.Values)
                if (b != null && b.Runs == act.Name) uses.Add(b.Binds);
            foreach (TickActDef parent in type.Actions.Values)
                foreach (TickChildDef c in parent.Children)
                    if (c != null && (ReferenceEquals(c.Target, act) || (c.Target == null && c.Animation == act.Name))) uses.Add(c.Binds);
            return uses;
        }

        /// <summary>A parameter as one reference gives it: the reference's own, else the action's, else absent.</summary>
        private static TickParamDef Read(TickActDef act, Dictionary<string, TickParamDef> binds, string name)
        {
            TickParamDef p;
            if (binds != null && binds.TryGetValue(name, out p)) return p;
            return act.Params.TryGetValue(name, out p) ? p : null;
        }

        /// <summary>Characteristics, split on commas and compared exactly (TickSocialFamily.ReadCharacteristics): absent
        /// is the empty string, which names nothing; a script may name Breed.</summary>
        private static bool MayBreed(TickParamDef characteristics)
        {
            if (characteristics == null) return false;
            if (characteristics.Program != null) return true;
            if (characteristics.Literal.Kind != ScriptValueKind.String) return false;
            return Array.IndexOf(characteristics.Literal.Text.Split(','), "Breed") >= 0;
        }

        private static readonly TickWorld Probe = new TickWorld(null, null, null, null);

        /// <summary>Whether the window family constructs this class, asked of the family itself (as
        /// TickWorld.RunsEmbeddedClass asks), so a class the family gains is offered without a list kept here. A
        /// constructor that refuses the empty probe still claimed the class.</summary>
        private static bool IsWindowClass(string embeddedClass)
        {
            var def = new TickActDef { Name = "probe", Type = "Embedded", Class = embeddedClass };
            try
            {
                return TickWindowFamily.TryCreate(Probe, null, def, new Dictionary<string, TickParamDef>(StringComparer.Ordinal)) != null;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
