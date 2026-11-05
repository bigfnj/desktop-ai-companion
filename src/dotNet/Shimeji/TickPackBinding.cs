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

        /// <summary>Stage a member of a preview's own namespace (a whole-pack preview, <see cref="TickPreviewPacks"/>): the
        /// preview scope and the member key to a transient registry entry whose type is scoped to that preview, or null. Null:
        /// a preview resolves nothing beyond the types already in the world, as before 1.10.0.</summary>
        public Func<string, string, CompanionTypeRegistry.Entry> StagePreviewMember { get; set; }

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
            if (from.PackScope == null) return null;
            CompanionTypeRegistry.Entry entry;
            string source;
            if (IsPreviewScope(from.PackScope))
            {
                // A preview's members come from its own namespace and from nowhere else: the installed index is never asked,
                // so an installed pack with the same id is never reached (grimoire/06 section 9). Its scope holds a ':' and the
                // index holds only pack ids, so asking it would find nothing anyway; not asking keeps that a rule rather than
                // a coincidence of the two key shapes.
                if (StagePreviewMember == null) return null;
                entry = StagePreviewMember(from.PackScope, key);
                source = "the preview";
            }
            else
            {
                CompanionPackIndex index = _index();
                if (index == null) return null;
                foreach (string d in index.Duplicates) SayOnce(d);
                PackIndexEntry found = index.Find(from.PackScope, key);
                if (found == null) return null;
                entry = _stageType(found.TypeId);
                source = found.TypeId;
            }
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
                SayOnce("pack '" + from.PackScope + "' member '" + key + "' (" + source + ") was not used: " + refusal);
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

    /// <summary>
    /// The namespaces of whole-pack previews (ICompanionPackPreview, host 1.10.0; grimoire/06 section 9). One per preview:
    /// the preview's id ("preview:" and a guid, so it holds a ':' no pack id may) with every member's validated document by
    /// its exact key. A preview's members are staged from here and only from here, under registry ids of the preview's own
    /// (the preview id, '/', the member's position), transient and scoped to the preview, so nothing here is ever indexed
    /// as installed and nothing installed is ever staged for a preview. A namespace closes when its handle is removed: a
    /// member not staged by then stages nothing, and a birth still pending gets no window.
    ///
    /// Beside the binding because it is the binding's preview half; StartUp holds one, and the window self-test drives one
    /// over a real registry and a real tick host.
    /// </summary>
    internal sealed class TickPreviewPacks
    {
        /// <summary>The most members one pack preview may name. A Shimeji pack has a handful; the cap bounds what one call
        /// can make the host parse on the UI thread.</summary>
        public const int MaximumMembers = 32;

        /// <summary>A validated member set, not yet opened under a preview id.</summary>
        internal sealed class Prepared
        {
            public string PackId;
            public string MainKey;
            /// <summary>Every member key in ordinal order: a member's position here is its registry id's suffix, because the
            /// registry ignores case and member keys do not ("Egg" and "egg" are two members).</summary>
            public readonly List<string> Keys = new List<string>();
            public readonly Dictionary<string, string> XmlByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            public string MainXml { get { string x; return MainKey != null && XmlByKey.TryGetValue(MainKey, out x) ? x : null; } }
        }

        private readonly Dictionary<string, Prepared> _open = new Dictionary<string, Prepared>(StringComparer.Ordinal);
        private readonly HashSet<string> _closed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Validate a whole pack for a preview, or null with the reason. Each member through the host's own parser and limits
        /// (CompanionXmlValidator, as SpawnPreview), then the set through the pack-level rules (PackValidator), plus what only
        /// a caller-keyed set can get wrong: a document filed under a key its header does not name, a member that is not a
        /// tick-clock companion, a main member whose role is not main. Copies the set, so the caller changing its dictionary
        /// afterwards changes nothing.
        /// </summary>
        public static Prepared Prepare(IReadOnlyDictionary<string, string> memberXmlByKey, string mainKey, out string error)
        {
            error = null;
            if (memberXmlByKey == null || memberXmlByKey.Count == 0) { error = "No pack members were supplied."; return null; }
            if (memberXmlByKey.Count > MaximumMembers)
            {
                error = "A pack preview takes at most " + MaximumMembers + " members; this one has " + memberXmlByKey.Count + ".";
                return null;
            }
            if (string.IsNullOrEmpty(mainKey)) { error = "Name the pack's main member."; return null; }

            var pack = new Prepared { MainKey = mainKey };
            foreach (KeyValuePair<string, string> pair in memberXmlByKey)
            {
                if (!ShimejiFormatValidator.IsMemberKey(pair.Key))
                {
                    error = "'" + pair.Key + "' is not a member key the host accepts (header/pack member's rule).";
                    return null;
                }
                if (pack.XmlByKey.ContainsKey(pair.Key)) { error = "Member '" + pair.Key + "' was supplied twice."; return null; }
                pack.XmlByKey.Add(pair.Key, pair.Value);
                pack.Keys.Add(pair.Key);
            }
            pack.Keys.Sort(StringComparer.Ordinal);
            if (!pack.XmlByKey.ContainsKey(mainKey))
            {
                error = "The main member '" + mainKey + "' is not one of the members supplied.";
                return null;
            }

            var members = new List<PackValidator.Member>();
            foreach (string key in pack.Keys)
            {
                string xml = pack.XmlByKey[key];
                if (string.IsNullOrWhiteSpace(xml)) { error = "Member '" + key + "' has no document."; return null; }
                if (System.Text.Encoding.UTF8.GetByteCount(xml) > CompanionXmlValidator.MaximumXmlBytes)
                {
                    error = "Member '" + key + "' is too large.";
                    return null;
                }
                XmlData.RootNode root;
                string why;
                if (!CompanionXmlValidator.TryParse(xml, out root, out why)) { error = "Member '" + key + "': " + why; return null; }
                if (!ShimejiFormatValidator.IsTickMode(root))
                {
                    error = "Member '" + key + "' is not a tick-clock companion, so it cannot take part in a pack.";
                    return null;
                }
                XmlData.PackNode declared = root.Header.Pack;
                if (declared == null) { error = "Member '" + key + "' declares no pack."; return null; }
                if (!string.Equals(declared.Member, key, StringComparison.Ordinal))
                {
                    error = "The document given as member '" + key + "' names member '" + declared.Member + "' in its header.";
                    return null;
                }
                members.Add(new PackValidator.Member("Member '" + key + "'", root));
            }
            PackValidator.Report report = PackValidator.Validate(members);
            if (!report.Ok) { error = report.Errors[0]; return null; }

            XmlData.PackNode main = null;
            foreach (PackValidator.Member m in members)
                if (string.Equals(m.Pack.Member, mainKey, StringComparison.Ordinal)) main = m.Pack;
            if (main == null || !string.Equals(main.Role, TickPack.RoleMain, StringComparison.Ordinal))
            {
                error = "'" + mainKey + "' is not the pack's main member: its header gives it the role '" + (main == null ? "" : main.Role) + "'.";
                return null;
            }
            pack.PackId = main.Id;
            return pack;
        }

        /// <summary>The scope a preview's members resolve in: the composition <see cref="TickPackBinding.ScopeForPreview"/>
        /// gives a type staged for it.</summary>
        public static string ScopeOf(string previewId, string packId) { return previewId + "/" + packId; }

        /// <summary>Whether a registry entry belongs to a preview: its main member (the preview's own id) or a member staged
        /// for it (the id, '/', a position).</summary>
        public static bool Owns(string previewId, string entryId)
        {
            if (string.IsNullOrEmpty(previewId) || string.IsNullOrEmpty(entryId)) return false;
            return string.Equals(entryId, previewId, StringComparison.OrdinalIgnoreCase)
                || entryId.StartsWith(previewId + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Open a prepared set under a preview id, before its main member spawns: the spawn resolves its startup
        /// member at once.</summary>
        public void Open(string previewId, Prepared pack)
        {
            if (string.IsNullOrEmpty(previewId) || pack == null) return;
            _open[previewId] = pack;
        }

        /// <summary>Close a preview's namespace: it stages nothing more, and a pending birth of it gets no window.</summary>
        public void Close(string previewId)
        {
            if (string.IsNullOrEmpty(previewId)) return;
            if (_open.Remove(previewId)) _closed.Add(previewId);
        }

        public bool IsOpen(string previewId) { return previewId != null && _open.ContainsKey(previewId); }

        /// <summary>Whether a window may open for a companion of this registry entry: false only for a member of a whole-pack
        /// preview that has been closed. A single preview, and every installed type, is never refused here.</summary>
        public bool MayOpenWindow(CompanionTypeRegistry.Entry entry)
        {
            if (entry == null || !entry.IsTransient || string.IsNullOrEmpty(entry.Id)) return true;
            int slash = entry.Id.IndexOf('/');
            string previewId = slash < 0 ? entry.Id : entry.Id.Substring(0, slash);
            return !_closed.Contains(previewId);
        }

        /// <summary>
        /// Stage member <paramref name="key"/> of the open preview whose scope is <paramref name="scope"/>, through the app's
        /// validated staging path at <paramref name="scaleFactor"/>, as a transient registry entry whose tick type is scoped
        /// to that preview; or reuse the entry already staged. Null when the scope is not an open preview's, the preview has
        /// no such member, or staging refused it (said through <paramref name="log"/>).
        /// </summary>
        public CompanionTypeRegistry.Entry Stage(CompanionTypeRegistry registry, string scope, string key, double scaleFactor, Action<string> log)
        {
            int slash = scope == null ? -1 : scope.IndexOf('/');
            if (registry == null || slash <= 0 || key == null) return null;
            string previewId = scope.Substring(0, slash);
            Prepared pack;
            if (!_open.TryGetValue(previewId, out pack) || !string.Equals(scope, ScopeOf(previewId, pack.PackId), StringComparison.Ordinal))
                return null;
            int position = pack.Keys.IndexOf(key);
            if (position < 0) return null;
            string id = previewId + "/" + position.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CompanionTypeRegistry.Entry existing;
            if (registry.TryGet(id, out existing)) return existing;
            Xml xml;
            Animations animations;
            string error;
            if (!StartUp.TryStageRuntime(pack.XmlByKey[key], scaleFactor, out xml, out animations, out error))
            {
                if (log != null) log("preview member '" + key + "' could not be staged: " + error);
                return null;
            }
            animations.PetTypeId = id;
            TickPackBinding.ScopeForPreview(TickStaging.Of(xml), previewId);
            return registry.Add(id, xml, animations, true);
        }
    }
}
