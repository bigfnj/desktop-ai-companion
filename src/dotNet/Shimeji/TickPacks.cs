using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// A type's place in a pack (grimoire/06 sections 2 and 9): the header's <c>&lt;pack id member role hidden/&gt;</c>
    /// and <c>&lt;startup member behaviour/&gt;</c>. <see cref="TickType.Name"/> stays the host's unique key for the type
    /// (an installed type id, or the parity run's image set); the engine matches a TransformMascot or BornMascot against
    /// <see cref="ReferenceKey"/> inside <see cref="PackScope"/> instead, so two packs that both have an "Egg" never meet.
    /// </summary>
    internal sealed partial class TickType
    {
        /// <summary>The namespace member keys resolve in: the pack id for an installed member, the preview's own
        /// namespace for a preview (the host sets that), null outside a pack.</summary>
        public string PackScope;

        /// <summary>The member key (unique in its pack, case-sensitive), or null outside a pack.</summary>
        public string Member;

        /// <summary>main, egg, form or helper; null outside a pack.</summary>
        public string Role;

        /// <summary>Kept out of the user's Add-a-companion list (helpers and egg stages).</summary>
        public bool Hidden;

        /// <summary>The source's initial image set and entry behaviour, declared on a main member only.</summary>
        public string StartupMember;
        public string StartupBehaviour;

        /// <summary>What a reference to this type names: its member key in a pack, its name outside one (the source's
        /// image-set names, which the parity run and a packless companion use).</summary>
        public string ReferenceKey { get { return Member ?? Name; } }
    }

    /// <summary>How a new companion of a main member starts: as which member, with which behaviour.</summary>
    internal struct TickStartPlan
    {
        /// <summary>The member to start as; null starts the main member itself.</summary>
        public string Member;

        /// <summary>The entry behaviour; null lets the route choose.</summary>
        public string Behaviour;
    }

    internal static class TickPack
    {
        public const string RoleMain = "main";
        public const string RoleEgg = "egg";

        /// <summary>Copy the header's pack and startup facts onto a type being compiled.</summary>
        public static void Read(TickType type, XmlData.HeaderNode header)
        {
            if (header == null) return;
            if (header.Pack != null)
            {
                type.PackScope = header.Pack.Id;
                type.Member = header.Pack.Member;
                type.Role = header.Pack.Role;
                type.Hidden = header.Pack.Hidden;
            }
            if (header.Startup != null)
            {
                type.StartupMember = header.Startup.Member;
                type.StartupBehaviour = header.Startup.Behaviour;
            }
        }

        /// <summary>The key a type is found by: its scope (or none) and its reference key. A pack id is letters, digits,
        /// '.', '_', '-' and space (the validator's IsPackKey) and a member key holds no control character (IsMemberKey),
        /// so the line feed between them cannot occur in either.</summary>
        public static string IndexKey(string scope, string referenceKey)
        {
            return (scope == null ? "-" : "p:" + scope) + "\n" + (referenceKey ?? "");
        }

        /// <summary>
        /// "Arrive as an egg" is offered only when the main member's startup names ANOTHER member whose role is egg
        /// (grimoire/06 section 2). <paramref name="startupRole"/> is that member's role as the pack index read it.
        /// </summary>
        public static bool OffersEggArrival(TickType main, string startupRole)
        {
            return main != null && !string.IsNullOrEmpty(main.StartupMember)
                && !string.Equals(main.StartupMember, main.ReferenceKey, StringComparison.Ordinal)
                && string.Equals(startupRole, RoleEgg, StringComparison.Ordinal);
        }

        /// <summary>
        /// How a new companion of <paramref name="main"/> starts. With a startup naming another member it starts as that
        /// member, entering the startup behaviour; when that member is an egg and the user turned "Arrive as an egg" off,
        /// the main member starts directly and the route chooses. A startup naming the main member itself only gives the
        /// entry behaviour. No startup: the main member, the route's choice (the source's createMascot).
        /// </summary>
        public static TickStartPlan StartPlan(TickType main, string startupRole, bool arriveAsEgg)
        {
            var plan = new TickStartPlan();
            if (main == null || string.IsNullOrEmpty(main.StartupMember)) return plan;
            if (string.Equals(main.StartupMember, main.ReferenceKey, StringComparison.Ordinal))
            {
                plan.Behaviour = main.StartupBehaviour;
                return plan;
            }
            if (OffersEggArrival(main, startupRole) && !arriveAsEgg) return plan;
            plan.Member = main.StartupMember;
            plan.Behaviour = main.StartupBehaviour;
            return plan;
        }
    }

    /// <summary>
    /// Cross-member references (grimoire/06 section 9). Transform's TransformMascot and Breed's BornMascot name a member
    /// key, resolved in this order:
    /// <list type="number">
    /// <item>an empty name names no type: the current one, silently (the source's default "");</item>
    /// <item>a type already in the world with the same scope and that key;</item>
    /// <item>a reference this type already failed to resolve stays the current type, with no second diagnostic;</item>
    /// <item>the host's <see cref="TypeResolver"/>, which may stage the member on demand from the same pack;</item>
    /// <item>otherwise the current type, as the source does (Transform.java:50, Breed.java:76), with ONE diagnostic per
    /// (type, reference) through <see cref="UnresolvedReference"/>.</item>
    /// </list>
    /// The diagnostic is not an observer event: the source says nothing when it falls back, so a parity trace must not
    /// gain an event the real runtime never has.
    /// </summary>
    internal sealed partial class TickWorld
    {
        private readonly Dictionary<string, TickType> _byReference = new Dictionary<string, TickType>(StringComparer.Ordinal);
        private readonly Dictionary<TickType, HashSet<string>> _unresolved = new Dictionary<TickType, HashSet<string>>();

        /// <summary>The host's way to stage a member the world does not have yet: (the referring type, the member key)
        /// to a type, already added through <see cref="AddType"/>, or null. Null: nothing beyond the world's types.</summary>
        public Func<TickType, string, TickType> TypeResolver { get; set; }

        /// <summary>Told once per (type, reference) that a reference fell back to the current type.</summary>
        public Action<TickType, string> UnresolvedReference { get; set; }

        /// <summary>Told when a type claims a (scope, key) another type already holds: the first one stays.</summary>
        public Action<TickType, TickType> ReferenceConflict { get; set; }

        private void IndexReference(TickType type)
        {
            string key = TickPack.IndexKey(type.PackScope, type.ReferenceKey);
            TickType held;
            // The same type staged again (same name) replaces itself; a different type with the same key does not take
            // the place of the one already running (the host logs it).
            if (_byReference.TryGetValue(key, out held) && !ReferenceEquals(held, type)
                && !string.Equals(held.Name, type.Name, StringComparison.Ordinal))
            {
                if (ReferenceConflict != null) ReferenceConflict(held, type);
                return;
            }
            _byReference[key] = type;
        }

        /// <summary>Forget a type (its frames were released): later references stage it afresh. Companions that hold it
        /// keep their reference; only lookups change.</summary>
        public void RemoveType(TickType type)
        {
            if (type == null) return;
            TickType held;
            if (_types.TryGetValue(type.Name, out held) && ReferenceEquals(held, type)) _types.Remove(type.Name);
            string key = TickPack.IndexKey(type.PackScope, type.ReferenceKey);
            if (_byReference.TryGetValue(key, out held) && ReferenceEquals(held, type)) _byReference.Remove(key);
            _unresolved.Remove(type);
        }

        /// <summary>The type <paramref name="name"/> names from <paramref name="from"/>, or <paramref name="from"/> itself
        /// when it names none (see the class summary for the order).</summary>
        public TickType ResolveReference(TickType from, string name)
        {
            return TryResolveReference(from, name) ?? from;
        }

        /// <summary>As <see cref="ResolveReference"/>, but null where that falls back (an empty name is not reported).</summary>
        public TickType TryResolveReference(TickType from, string name)
        {
            if (from == null || string.IsNullOrEmpty(name)) return null;
            TickType found;
            string key = TickPack.IndexKey(from.PackScope, name);
            if (_byReference.TryGetValue(key, out found)) return found;
            HashSet<string> failed;
            if (_unresolved.TryGetValue(from, out failed) && failed.Contains(name)) return null;
            if (TypeResolver != null)
            {
                // Whatever the host staged answers only if it now holds the asked-for (scope, key): a type of another
                // pack or another member is indexed under its own key and is not found here. The index decides which
                // type answers (the first one registered for the key).
                if (TypeResolver(from, name) != null && _byReference.TryGetValue(key, out found))
                    return found;
            }
            if (failed == null) _unresolved[from] = failed = new HashSet<string>(StringComparer.Ordinal);
            failed.Add(name);
            if (UnresolvedReference != null) UnresolvedReference(from, name);
            return null;
        }
    }
}
