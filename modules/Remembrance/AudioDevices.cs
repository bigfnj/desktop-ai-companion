using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>One selectable audio endpoint for the options dropdowns, by the friendly name the dropdown shows
    /// and the settings store. The default entry's name is a label ("System default output"), resolved live at
    /// record time. An endpoint id used to travel beside the name and nothing read it (F167): the design began
    /// with stable ids and shipped with names, which is also why two endpoints sharing one friendly name share
    /// one row.</summary>
    internal sealed class AudioDevice
    {
        public string Name;
    }

    /// <summary>Enumerates WASAPI render (for system/loopback capture) and capture (microphone) endpoints, and
    /// resolves a saved friendly name back to a device. All best-effort: enumeration never throws, and a name
    /// no longer present falls back to the system default.</summary>
    internal static class AudioDevices
    {
        public static List<AudioDevice> RenderDevices() { return Cached(DataFlow.Render, "System default output"); }
        public static List<AudioDevice> CaptureDevices() { return Cached(DataFlow.Capture, "System default microphone"); }

        /// <summary>
        /// One enumeration per flow per burst, instead of one per CALL.
        ///
        /// Opening the Remembrance options pane called each of these twice -- RefreshDynamicOptions builds
        /// the two dropdowns, then StatusLine calls both again purely to COUNT them -- so four WASAPI
        /// enumerations happened within milliseconds of each other, each constructing an
        /// MMDeviceEnumerator and reading FriendlyName off every endpoint's property store, on the UI
        /// thread.
        ///
        /// It also fixes a consistency bug that had nothing to do with speed: the dropdown options and the
        /// "devices: N output, M mic" count came from SEPARATE enumerations, so a device appearing or
        /// disappearing between the two produced a status line that disagreed with the list right above it.
        ///
        /// The window is deliberately short. Device lists change when hardware is plugged in, and this must
        /// not make a reopened pane show a stale list -- a pane rebuild takes milliseconds, so every call in
        /// one open shares a snapshot while the next open re-enumerates.
        /// </summary>
        private const int CacheWindowMilliseconds = 1500;
        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<DataFlow, List<AudioDevice>> _cache =
            new Dictionary<DataFlow, List<AudioDevice>>();
        private static readonly Dictionary<DataFlow, DateTime> _cacheStamp =
            new Dictionary<DataFlow, DateTime>();

        private static List<AudioDevice> Cached(DataFlow flow, string defaultLabel)
        {
            lock (_cacheLock)
            {
                DateTime stamp;
                List<AudioDevice> hit;
                if (_cacheStamp.TryGetValue(flow, out stamp) &&
                    (DateTime.UtcNow - stamp).TotalMilliseconds < CacheWindowMilliseconds &&
                    _cache.TryGetValue(flow, out hit))
                {
                    // A COPY. Callers hand these lists to DeviceOptions and to UI state; returning the
                    // cached instance would let one caller's mutation reach the next.
                    return new List<AudioDevice>(hit);
                }
            }

            List<AudioDevice> fresh = Enumerate(flow, defaultLabel);
            lock (_cacheLock)
            {
                _cache[flow] = fresh;
                _cacheStamp[flow] = DateTime.UtcNow;
            }
            return new List<AudioDevice>(fresh);
        }

        /// <summary>Drop the snapshot, so the next read re-enumerates. For a caller that has just changed
        /// the device set, or a test that must not see another test's devices.</summary>
        internal static void ForgetCachedDevices()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                _cacheStamp.Clear();
            }
        }

        /// <summary>How many real WASAPI walks have run in this process. For the self-test, which asserts that
        /// Init runs none of them (F178) and that a pane build runs some (the witness).</summary>
        internal static int EnumerationCount { get { return _enumerations; } }
        private static int _enumerations;

        private static List<AudioDevice> Enumerate(DataFlow flow, string defaultLabel)
        {
            System.Threading.Interlocked.Increment(ref _enumerations);
            var list = new List<AudioDevice> { new AudioDevice { Name = defaultLabel } };
            try
            {
                // The COLLECTION is disposable too, not just the devices in it. This runs on every options-pane
                // load (RemembranceModule.StatusLine counts the endpoints), so an undisposed collection per call
                // is a per-pane-open leak rather than a one-off.
                using (var en = new MMDeviceEnumerator())
                using (MMDeviceCollection endpoints = en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    foreach (MMDevice d in endpoints)
                    {
                        try { list.Add(new AudioDevice { Name = d.FriendlyName }); }
                        catch { }
                        finally { try { d.Dispose(); } catch { } }
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Resolve a saved friendly name to an MMDevice, or the system default when it is blank / a
        /// default-label / a device no longer present (the options dropdown stores the display name). The caller
        /// owns the returned device and must dispose it.</summary>
        public static MMDevice Resolve(DataFlow flow, string friendlyName)
        {
            var en = new MMDeviceEnumerator();
            try
            {
                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    // The whole collection is walked even after a hit, because returning from inside the loop
                    // stranded every endpoint the match had not reached yet -- and the collection itself is
                    // disposable, so it gets a using of its own. Exactly ONE device survives this block: the
                    // match handed to the caller.
                    using (MMDeviceCollection endpoints = en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    {
                        MMDevice match = null;
                        foreach (MMDevice d in endpoints)
                        {
                            bool isMatch = false;
                            if (match == null)
                            {
                                // FriendlyName reads the endpoint's property store and can throw; a throw here
                                // must not abandon the devices still to come.
                                try { isMatch = string.Equals(d.FriendlyName, friendlyName, StringComparison.Ordinal); }
                                catch { isMatch = false; }
                            }
                            if (isMatch) match = d;
                            else { try { d.Dispose(); } catch { } }
                        }
                        if (match != null) return match;
                    }
                }
                Role role = flow == DataFlow.Render ? Role.Multimedia : Role.Communications;
                return en.GetDefaultAudioEndpoint(flow, role);
            }
            finally { try { en.Dispose(); } catch { } }
        }
    }
}
