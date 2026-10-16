using System;
using System.Collections.Generic;
using Microsoft.Win32;

// Is Teams or Zoom using the microphone right now, and when did each last start and stop? Read from the record Windows
// keeps for its own "an app is using your microphone" indicator, so no audio is touched and nothing is hooked. Compiled
// into Remembrance (an end-of-meeting signal: the meeting app let go of the microphone) and Reminder (a "join the
// meeting" question withdraws itself once the user has joined), and run by both modules' self-tests, so each payload
// proves the copy it ships. Lane feature/meeting-end, 2026-10-15; the module declaring it carries
// ModulePermissions.MicrophoneUsage.
//
// THE RECORD. HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone holds one
// subkey per packaged app (its package family name, e.g. "MSTeams_8wekyb3d8bbwe") and, under NonPackaged, one per
// desktop exe (its full path with '#' for '\', e.g. "C:#Users#<user>#AppData#Roaming#Zoom#bin#Zoom.exe"). Each carries
// LastUsedTimeStart and LastUsedTimeStop as FILETIME QWORDs; Stop is 0 while the app holds the microphone. Only the
// newest session is kept per key. The names were read off a real machine on 2026-10-15: new Teams registers TWO
// packaged keys (MSTeams_* and its media host Microsoft.Teams.SlimCoreVdiHost.*), and Zoom one desktop key.
//
// UNDOCUMENTED BUT STABLE: Windows has written it since 1903 and the privacy indicator reads it. Policy can switch the
// recording off, in which case the key is absent and Read says so (Available false) rather than reporting "nothing in
// use", which would read as a meeting that ended. Nothing here throws to a caller.
namespace DesktopAICompanion.MeetingApps
{
    internal enum MeetingApp { None = 0, Teams = 1, Zoom = 2 }

    /// <summary>One subkey of the microphone record, as read.</summary>
    internal sealed class ConsentEntry
    {
        internal string Name = "";
        internal bool Packaged;
        internal long StartFileTime;
        internal long StopFileTime;
    }

    /// <summary>The seam the self-test fakes. Returns null when the record does not exist; may throw.</summary>
    internal interface IConsentStoreReader
    {
        IReadOnlyList<ConsentEntry> ReadMicrophone();
    }

    /// <summary>The real record. Classifies a subkey by NAME before opening it, so it opens only Teams' and Zoom's.</summary>
    internal sealed class RegistryConsentStoreReader : IConsentStoreReader
    {
        internal const string KeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        public IReadOnlyList<ConsentEntry> ReadMicrophone()
        {
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(KeyPath, false))
            {
                if (root == null) return null;
                var entries = new List<ConsentEntry>();
                Collect(root, true, entries);
                using (RegistryKey desktop = root.OpenSubKey("NonPackaged", false))
                    if (desktop != null) Collect(desktop, false, entries);
                return entries;
            }
        }

        private static void Collect(RegistryKey parent, bool packaged, List<ConsentEntry> into)
        {
            foreach (string name in parent.GetSubKeyNames())
            {
                if (packaged && string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                if (MeetingAppMicrophone.Classify(name, packaged) == MeetingApp.None) continue;
                using (RegistryKey app = parent.OpenSubKey(name, false))
                {
                    if (app == null) continue;
                    into.Add(new ConsentEntry
                    {
                        Name = name,
                        Packaged = packaged,
                        StartFileTime = AsLong(app.GetValue("LastUsedTimeStart")),
                        StopFileTime = AsLong(app.GetValue("LastUsedTimeStop")),
                    });
                }
            }
        }

        // REG_QWORD reads back as a long; anything else (absent, or a type a future Windows chooses) counts as 0.
        private static long AsLong(object value)
        {
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is ulong u) return u > long.MaxValue ? 0 : (long)u;
            return 0;
        }
    }

    /// <summary>One meeting app's use of the microphone, merged across every key that belongs to it.</summary>
    internal sealed class AppMicUse
    {
        internal MeetingApp App;
        internal bool InUse;
        internal DateTimeOffset? LastStartUtc;
        internal DateTimeOffset? LastStopUtc;
    }

    /// <summary>What Read saw. When Available is false nothing below it means anything: the record could not be read, which
    /// is NOT the same as "no meeting app is using the microphone".</summary>
    internal sealed class MicUseSnapshot
    {
        internal bool Available;
        internal string Unavailable = "";
        internal AppMicUse Teams = new AppMicUse { App = MeetingApp.Teams };
        internal AppMicUse Zoom = new AppMicUse { App = MeetingApp.Zoom };

        internal bool AnyInUse { get { return Available && (Teams.InUse || Zoom.InUse); } }

        /// <summary>The meeting app holding the microphone, Teams first when both do; None when neither or unavailable.</summary>
        internal MeetingApp InUseApp
        {
            get
            {
                if (!Available) return MeetingApp.None;
                if (Teams.InUse) return MeetingApp.Teams;
                return Zoom.InUse ? MeetingApp.Zoom : MeetingApp.None;
            }
        }

        internal AppMicUse For(MeetingApp app)
        {
            return app == MeetingApp.Teams ? Teams : (app == MeetingApp.Zoom ? Zoom : null);
        }
    }

    internal sealed class MeetingAppMicrophone
    {
        internal const string UnavailableNoRecord =
            "Windows keeps no record of microphone use here (it is turned off, or a policy turns it off)";

        private readonly IConsentStoreReader _reader;

        internal MeetingAppMicrophone(IConsentStoreReader reader = null)
        {
            _reader = reader ?? new RegistryConsentStoreReader();
        }

        /// <summary>Read the record now. A registry read of two or three small keys; never throws.</summary>
        internal MicUseSnapshot Read()
        {
            var snap = new MicUseSnapshot();
            IReadOnlyList<ConsentEntry> entries;
            try { entries = _reader.ReadMicrophone(); }
            catch (Exception ex)
            {
                // The type only: a message can carry a path, and this line can reach the diagnostics log.
                snap.Unavailable = "the microphone-use record could not be read (" + ex.GetType().Name + ")";
                return snap;
            }
            if (entries == null) { snap.Unavailable = UnavailableNoRecord; return snap; }
            snap.Available = true;
            foreach (ConsentEntry e in entries)
            {
                if (e == null) continue;
                AppMicUse use = snap.For(Classify(e.Name, e.Packaged));
                if (use == null) continue;
                if (e.StartFileTime > 0 && e.StopFileTime == 0) use.InUse = true;
                use.LastStartUtc = Later(use.LastStartUtc, FromFileTime(e.StartFileTime));
                use.LastStopUtc = Later(use.LastStopUtc, FromFileTime(e.StopFileTime));
            }
            return snap;
        }

        /// <summary>
        /// Which meeting app a subkey belongs to. Packaged: the package family name's prefix. Desktop: the exe's file name,
        /// the part after the last '#', compared whole -- so "NotZoom.exe" is not Zoom, and a path through a folder called
        /// Zoom is not either. Our own exe and everything else are None.
        /// </summary>
        internal static MeetingApp Classify(string keyName, bool packaged)
        {
            if (string.IsNullOrEmpty(keyName)) return MeetingApp.None;
            if (packaged)
            {
                if (keyName.StartsWith("MSTeams_", StringComparison.OrdinalIgnoreCase)) return MeetingApp.Teams;
                if (keyName.StartsWith("Microsoft.Teams.", StringComparison.OrdinalIgnoreCase)) return MeetingApp.Teams;
                return MeetingApp.None;
            }
            int hash = keyName.LastIndexOf('#');
            string exe = hash >= 0 ? keyName.Substring(hash + 1) : keyName;
            if (string.Equals(exe, "Teams.exe", StringComparison.OrdinalIgnoreCase)) return MeetingApp.Teams;      // classic Teams
            if (string.Equals(exe, "ms-teams.exe", StringComparison.OrdinalIgnoreCase)) return MeetingApp.Teams;   // new Teams, unpackaged
            if (string.Equals(exe, "Zoom.exe", StringComparison.OrdinalIgnoreCase)) return MeetingApp.Zoom;
            return MeetingApp.None;
        }

        internal static string DisplayName(MeetingApp app)
        {
            return app == MeetingApp.Teams ? "Teams" : (app == MeetingApp.Zoom ? "Zoom" : "");
        }

        internal static DateTimeOffset? FromFileTime(long fileTime)
        {
            if (fileTime <= 0) return null;
            try { return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
        {
            if (!a.HasValue) return b;
            if (!b.HasValue) return a;
            return a.Value >= b.Value ? a : b;
        }
    }
}
