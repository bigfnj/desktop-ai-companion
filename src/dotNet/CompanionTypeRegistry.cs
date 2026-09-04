using System;
using System.Collections.Generic;

namespace DesktopAICompanion
{
    /// <summary>
    /// Registry of pet TYPES loaded "alongside" the active/default pet, so several different pets can be
    /// on screen at once. Each entry owns a validated (Xml, Animations) pair that every on-screen pet of
    /// that type shares; a reference count tracks how many pets use it, and the pair is disposed only
    /// when the last pet of that type closes. FormCompanion treats its Animations/Xml as borrowed refs and
    /// never disposes them, so ownership lives here.
    ///
    /// The active/default type is NOT held here — StartUp owns it directly (its xml/animations fields)
    /// and pins its lifetime — so only extra types are reference-counted. All access is on the UI thread
    /// (spawns from the tick/tray, decrements from FormClosed), so no locking is needed.
    /// </summary>
    internal sealed class CompanionTypeRegistry
    {
        internal sealed class Entry
        {
            public string Id;
            public Xml Xml;
            public Animations Animations;
            public int RefCount;
            /// <summary>A throwaway type staged from an XML string for a preview, not an installed pet. It is
            /// excluded from the on-screen mix, which is what keeps it out of settings.json and out of the
            /// tray's "Remove a pet" submenu (both derive from that one list).</summary>
            public bool IsTransient;
        }

        private readonly Dictionary<string, Entry> _byId =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        internal bool TryGet(string id, out Entry entry)
        {
            return _byId.TryGetValue(id ?? "", out entry);
        }

        /// <summary>
        /// Cache a freshly staged type. Reference count starts at 0 until a pet is spawned.
        ///
        /// Re-staging an id that is already registered displaces the old entry. If nothing references it
        /// (staged but never spawned) it is disposed here, because otherwise its pair leaks with no owner
        /// left to free it. If pets ARE still using it, it is deliberately left alive and owned by them:
        /// FormCompanion borrows its Xml/Animations and never disposes them, so freeing the pair now would pull
        /// the sprites out from under a live pet. Their FormClosed decrements it to zero as usual, and the
        /// identity check in <see cref="DisposeEntry"/> stops that from evicting THIS entry.
        /// </summary>
        internal Entry Add(string id, Xml xml, Animations animations, bool transient = false)
        {
            string key = id ?? "";
            Entry displaced;
            if (_byId.TryGetValue(key, out displaced) &&
                !ReferenceEquals(displaced.Xml, xml) &&
                displaced.RefCount <= 0)
                DisposePair(displaced);

            var entry = new Entry
            {
                Id = key,
                Xml = xml,
                Animations = animations,
                RefCount = 0,
                IsTransient = transient,
            };
            _byId[entry.Id] = entry;
            return entry;
        }

        internal void Increment(Entry entry)
        {
            if (entry != null) entry.RefCount++;
        }

        /// <summary>Release one pet's use of a type; dispose the shared pair when the last pet closes.</summary>
        internal void Decrement(Entry entry)
        {
            if (entry == null) return;
            entry.RefCount--;
            if (entry.RefCount <= 0)
                DisposeEntry(entry);
        }

        /// <summary>Drop a type that was staged but never spawned (e.g. the spawn slot was full).</summary>
        internal void DropIfUnused(Entry entry)
        {
            if (entry != null && entry.RefCount <= 0)
                DisposeEntry(entry);
        }

        internal IEnumerable<Entry> Entries { get { return _byId.Values; } }

        internal void DisposeAll()
        {
            var entries = new List<Entry>(_byId.Values);
            _byId.Clear();
            foreach (Entry entry in entries)
                DisposePair(entry);
        }

        private void DisposeEntry(Entry entry)
        {
            // Remove by IDENTITY, not by key. Removing by key alone was a real bug: once an id had been
            // re-staged, the OLD entry reaching zero references evicted the NEW entry from the map, so a
            // live pet's type vanished from the registry and the next spawn staged a third duplicate copy
            // of the same pet. Only drop the mapping when it still points at this exact entry.
            Entry current;
            if (_byId.TryGetValue(entry.Id ?? "", out current) && ReferenceEquals(current, entry))
                _byId.Remove(entry.Id ?? "");
            DisposePair(entry);
        }

        private static void DisposePair(Entry entry)
        {
            // Xml.Dispose/Animations.Dispose are idempotent, so a double-dispose is harmless.
            if (entry.Animations != null) { entry.Animations.Dispose(); entry.Animations = null; }
            if (entry.Xml != null) { entry.Xml.Dispose(); entry.Xml = null; }
        }
    }
}
