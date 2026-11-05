using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The app's side of packs for the tick host: it stages a member on demand through the app's validated staging path
    /// and its type registry, holds a registry reference for every type a companion runs or draws as, forgets a type
    /// when its registry entry is disposed, and decides how a main member starts. StartUp builds one with the real
    /// catalog; the window self-test builds one over a real registry with members built in code.
    /// </summary>
    internal sealed class TickPackBinding
    {
        private readonly TickHost _host;
        private readonly CompanionTypeRegistry _registry;
        private readonly Func<string, CompanionTypeRegistry.Entry> _stageType;
        private readonly Func<Xml, TickType> _typeOf;
        private readonly Func<CompanionPackIndex> _index;
        private readonly Func<string, bool> _arriveAsEgg;
        private readonly Action<string> _log;
        private readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="stageType">Stage (or reuse) an installed type by id into the registry, validated, or null.</param>
        /// <param name="typeOf">The tick type staged beside an Xml (TickStaging.Of in the app).</param>
        /// <param name="index">The pack index of the installed companions, built when asked.</param>
        /// <param name="arriveAsEgg">The user's "Arrive as an egg" for a main member's type id.</param>
        public TickPackBinding(TickHost host, CompanionTypeRegistry registry, Func<string, CompanionTypeRegistry.Entry> stageType,
            Func<Xml, TickType> typeOf, Func<CompanionPackIndex> index, Func<string, bool> arriveAsEgg, Action<string> log)
        {
            _host = host;
            _registry = registry;
            _stageType = stageType;
            _typeOf = typeOf;
            _index = index;
            _arriveAsEgg = arriveAsEgg;
            _log = log ?? delegate { };
            host.StageMember = Stage;
            host.RetainType = t => _registry.Increment(t.Owner as CompanionTypeRegistry.Entry);
            host.ReleaseType = t => _registry.Decrement(t.Owner as CompanionTypeRegistry.Entry);
            registry.EntryDisposing = entry => _host.ForgetSprites(entry.Xml);
        }

        /// <summary>A preview's members resolve only inside its own namespace (grimoire/06 section 9): its pack scope is
        /// the preview's id plus the pack id. A preview id holds a ':', which no pack id may (IsPackKey), so a preview
        /// scope never equals an installed pack and is never looked up in the index.</summary>
        public static void ScopeForPreview(TickType type, string previewId)
        {
            if (type != null && type.PackScope != null && !IsPreviewScope(type.PackScope))
                type.PackScope = previewId + "/" + type.PackScope;
        }

        public static bool IsPreviewScope(string scope) { return scope != null && scope.IndexOf(':') >= 0; }

        /// <summary>The host type for a staged registry entry: named by the entry's id, owned by the entry.</summary>
        public TickHostType Describe(CompanionTypeRegistry.Entry entry, TickType type)
        {
            if (string.IsNullOrEmpty(type.Name)) type.Name = entry.Id;
            return new TickHostType(entry.Id, type, entry.Xml, entry.Animations) { Owner = entry };
        }

        /// <summary>
        /// Stage <paramref name="key"/> of <paramref name="from"/>'s pack: the index names the installed type, the app's
        /// staging validates and compiles it, and the result must be that member of that pack, drawn in the same
        /// transparency mode as the referring type (a window keeps the mode it was made with). Anything else is refused,
        /// and the world falls back to the current type.
        /// </summary>
        private TickHostType Stage(TickType from, string key)
        {
            // A preview's scope holds a ':' and the index holds only pack ids, so it finds nothing for a preview.
            if (from.PackScope == null) return null;
            CompanionPackIndex index = _index();
            if (index == null) return null;
            foreach (string d in index.Duplicates) SayOnce(d);
            PackIndexEntry found = index.Find(from.PackScope, key);
            if (found == null) return null;
            CompanionTypeRegistry.Entry entry = _stageType(found.TypeId);
            if (entry == null || entry.Xml == null) return null;
            TickType type = _typeOf(entry.Xml);
            string refusal = null;
            if (type == null) refusal = "it is not a tick-clock companion";
            else if (!string.Equals(type.PackScope, from.PackScope, StringComparison.Ordinal) || !string.Equals(type.Member, key, StringComparison.Ordinal))
                refusal = "its header names member '" + type.Member + "' of pack '" + type.PackScope + "'";
            else
            {
                TickHostType asker = _host.HostTypeOf(from);
                if (asker != null && asker.Sprites != null && asker.Sprites.UsesAlpha != entry.Xml.UsesAlpha)
                    refusal = "its transparency differs from " + from.Name + "'s";
            }
            if (refusal != null)
            {
                SayOnce("pack '" + from.PackScope + "' member '" + key + "' (" + found.TypeId + ") was not used: " + refusal);
                _registry.DropIfUnused(entry);
                return null;
            }
            return Describe(entry, type);
        }

        /// <summary>
        /// How a new companion of <paramref name="main"/> (type id <paramref name="mainId"/>) starts: the name of the type
        /// to start as (already in the world) and the entry behaviour (null: the route chooses). A startup member that
        /// cannot be resolved starts the main member, chosen by the route.
        /// </summary>
        public string PlanStart(TickType main, string mainId, out string behaviour)
        {
            behaviour = null;
            if (main == null) return null;
            string role = null;
            if (!string.IsNullOrEmpty(main.StartupMember) && main.PackScope != null && !IsPreviewScope(main.PackScope))
            {
                CompanionPackIndex index = _index();
                PackIndexEntry e = index == null ? null : index.Find(main.PackScope, main.StartupMember);
                if (e != null) role = e.Facts.Role;
            }
            TickStartPlan plan = TickPack.StartPlan(main, role, _arriveAsEgg(mainId));
            if (plan.Member == null)
            {
                behaviour = plan.Behaviour;
                return main.Name;
            }
            TickType start = _host.World.TryResolveReference(main, plan.Member);
            if (start == null) return main.Name;
            behaviour = plan.Behaviour;
            return start.Name;
        }

        private void SayOnce(string line)
        {
            if (_said.Add(line)) _log(line);
        }
    }
}
