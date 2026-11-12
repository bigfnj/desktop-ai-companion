using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The tick host's half of packs (grimoire/06 section 9): it stages a member the world asks for through the app
    /// (<see cref="StageMember"/>), and it keeps every type a companion runs or draws as alive. A companion holds its
    /// current type and the type of the image it shows; after a Transform those differ for one step (the old type's last
    /// image stays until the new type's first pose), so both are held until the image moves on, and only then is the old
    /// one released. Pending births hold their type too, from the step that queued them.
    /// </summary>
    internal sealed partial class TickHost
    {
        private readonly Dictionary<TickType, TickHostType> _byType = new Dictionary<TickType, TickHostType>();
        private readonly Dictionary<TickMascot, Held> _holds = new Dictionary<TickMascot, Held>();
        private int _holdRound;

        private sealed class Held
        {
            public TickHostType Running, Drawn;
            public int Round;
        }

        /// <summary>Stage the member <c>key</c> of the referring type's pack and return it (not yet added), or null.
        /// Set by the app; null stages nothing, so only the world's own types resolve.</summary>
        public Func<TickType, string, TickHostType> StageMember { get; set; }

        /// <summary>A companion started to run or draw as this type: keep its sprites alive.</summary>
        public Action<TickHostType> RetainType { get; set; }

        /// <summary>No companion runs or draws as this type through this hold any more.</summary>
        public Action<TickHostType> ReleaseType { get; set; }

        private void AttachPacks()
        {
            _world.TypeResolver = ResolveMember;
            _world.UnresolvedReference = delegate (TickType from, string name)
            {
                _log("tick companion type " + from.Name + ": member '" + name + "' is not installed in pack '"
                     + (from.PackScope ?? "(none)") + "'; it keeps its own type there");
            };
            _world.ReferenceConflict = delegate (TickType kept, TickType ignored)
            {
                _log("tick companion types " + kept.Name + " and " + ignored.Name + " both claim member '" + kept.ReferenceKey
                     + "' of pack '" + (kept.PackScope ?? "(none)") + "'; " + kept.Name + " is used");
            };
        }

        private TickType ResolveMember(TickType from, string key)
        {
            if (StageMember == null) return null;
            TickHostType staged;
            try { staged = StageMember(from, key); }
            catch (Exception e)
            {
                _log("tick companion type " + from.Name + ": member '" + key + "' could not be staged: " + e.Message);
                return null;
            }
            if (staged == null) return null;
            AddType(staged);
            return staged.Type;
        }

        /// <summary>The host type a compiled type is drawn from, or null.</summary>
        public TickHostType HostTypeOf(TickType type)
        {
            TickHostType t;
            return type != null && _byType.TryGetValue(type, out t) ? t : null;
        }

        /// <summary>Forget every type drawn from this sheet (its owner is disposing it): it leaves the host's maps and the
        /// world's lookups, so a later reference stages it again. Companions still holding it keep it alive, so this
        /// runs only when none does.</summary>
        public void ForgetSprites(Xml sprites)
        {
            if (sprites == null) return;
            var gone = new List<TickHostType>();
            foreach (TickHostType t in _byType.Values) if (ReferenceEquals(t.Sprites, sprites)) gone.Add(t);
            foreach (TickHostType t in gone)
            {
                _byType.Remove(t.Type);
                TickHostType named;
                if (_typesByName.TryGetValue(t.Type.Name, out named) && ReferenceEquals(named, t)) _typesByName.Remove(t.Type.Name);
                _world.RemoveType(t.Type);
            }
        }

        /// <summary>
        /// Take every type <paramref name="ofType"/> selects out of the world's lookups (its name and its pack reference), so
        /// nothing new resolves it: the app's uninstall, whose type's folder is about to go (N-bd-w12-review-01). Without it
        /// the type stayed in the reference index until its registry entry was disposed, and that waits for the step's
        /// UpdateHolds to release the holds of the births the uninstall dropped and the windows it closed; a kept companion
        /// of another type whose Breed or Transform named the type at that step resolved it straight from the index, and
        /// its child's hold kept the entry alive, so the step after opened a window for the uninstalled type. A later
        /// reference now asks the stager, which finds the member gone from the pack index and falls back to the referring
        /// type, as for any member that is not installed. Only lookups change, as in ForgetSprites: the host's own maps keep
        /// the type, so a companion still running or drawing it keeps its sprites until its window closes and the entry's
        /// disposal forgets the rest. Marking the registry entry retired was the rejected alternative: the world's index
        /// answers before the stager or OpenBornTickPet is asked, so the type had to leave the index whatever else knew.
        ///
        /// Each is also marked retired (N-bd-w13-review-04), and Step gives no birth of it a window: a kept companion that
        /// had transformed into the type still runs it, and its Breed with an empty or unresolvable BornMascot falls back to
        /// its own type without asking the index. The count of host types retired.
        /// </summary>
        public int RetireTypes(Func<TickHostType, bool> ofType)
        {
            if (ofType == null) return 0;
            var retired = new List<TickHostType>();
            foreach (TickHostType t in _byType.Values) if (ofType(t)) retired.Add(t);
            foreach (TickHostType t in retired) t.Retired = true;
            foreach (TickHostType t in retired) _world.RemoveType(t.Type);
            return retired.Count;
        }

        /// <summary>The types a companion holds now: what it runs as, and what it shows (self-test).</summary>
        public TickHostType[] HeldBy(TickMascot mascot)
        {
            Held h;
            if (!_holds.TryGetValue(mascot, out h)) return new TickHostType[0];
            if (h.Drawn == null || ReferenceEquals(h.Drawn, h.Running)) return h.Running == null ? new TickHostType[0] : new[] { h.Running };
            return h.Running == null ? new[] { h.Drawn } : new[] { h.Running, h.Drawn };
        }

        private void UpdateHolds()
        {
            int round = ++_holdRound;
            foreach (TickMascot m in _world.Mascots) Hold(m, round);
            foreach (TickMascot m in _world.Pending) Hold(m, round);
            // A companion that left without a Disposed event (a pending birth removed before it activated) lets go here.
            List<TickMascot> left = null;
            foreach (KeyValuePair<TickMascot, Held> p in _holds)
                if (p.Value.Round != round && !p.Key.InManager) (left ?? (left = new List<TickMascot>())).Add(p.Key);
            if (left != null) foreach (TickMascot m in left) ReleaseHolds(m);
        }

        private void Hold(TickMascot m) { Hold(m, _holdRound); }

        private void Hold(TickMascot m, int round)
        {
            Held h;
            if (!_holds.TryGetValue(m, out h)) _holds[m] = h = new Held();
            h.Round = round;
            TickHostType running = HostTypeOf(m.Type);
            TickHostType drawn = m.PoseType == null ? running : HostTypeOf(m.PoseType);
            if (ReferenceEquals(running, h.Running) && ReferenceEquals(drawn, h.Drawn)) return;
            // Take the new holds before letting the old ones go, so a type that stays held never touches zero.
            if (running != null && !Holds(h, running)) Retain(running);
            if (drawn != null && !ReferenceEquals(drawn, running) && !Holds(h, drawn)) Retain(drawn);
            TickHostType oldRunning = h.Running, oldDrawn = h.Drawn;
            h.Running = running;
            h.Drawn = drawn;
            if (oldRunning != null && !Holds(h, oldRunning)) Release(oldRunning);
            if (oldDrawn != null && !ReferenceEquals(oldDrawn, oldRunning) && !Holds(h, oldDrawn)) Release(oldDrawn);
        }

        private static bool Holds(Held h, TickHostType t)
        {
            return ReferenceEquals(h.Running, t) || ReferenceEquals(h.Drawn, t);
        }

        private void ReleaseHolds(TickMascot m)
        {
            Held h;
            if (!_holds.TryGetValue(m, out h)) return;
            _holds.Remove(m);
            if (h.Running != null) Release(h.Running);
            if (h.Drawn != null && !ReferenceEquals(h.Drawn, h.Running)) Release(h.Drawn);
        }

        private void Retain(TickHostType t)
        {
            if (RetainType != null) RetainType(t);
        }

        private void Release(TickHostType t)
        {
            if (ReleaseType != null) ReleaseType(t);
        }
    }
}
