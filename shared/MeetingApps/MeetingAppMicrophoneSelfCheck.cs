using System;
using System.Collections.Generic;

// MeetingAppMicrophone's own self-check, compiled into Remembrance and Reminder beside it and run by both modules'
// self-tests. Everything but the last check goes through FakeConsentStore, so nothing depends on what this machine's
// Teams or Zoom happens to be doing; the last one reads the real record and asserts only that the read cannot throw.
// Key names follow the shapes read off a real machine on 2026-10-15, with the user folder replaced by "someone".
namespace DesktopAICompanion.MeetingApps
{
    /// <summary>A consent record built in memory. Null entries = the key is absent; Throw = the read fails.</summary>
    internal sealed class FakeConsentStore : IConsentStoreReader
    {
        internal List<ConsentEntry> Entries = new List<ConsentEntry>();
        internal Exception Throw;
        internal int Reads;

        public IReadOnlyList<ConsentEntry> ReadMicrophone()
        {
            Reads++;
            if (Throw != null) throw Throw;
            return Entries;
        }

        internal FakeConsentStore Add(string name, bool packaged, DateTimeOffset? start, DateTimeOffset? stop)
        {
            Entries.Add(new ConsentEntry
            {
                Name = name,
                Packaged = packaged,
                StartFileTime = start.HasValue ? start.Value.UtcDateTime.ToFileTimeUtc() : 0,
                StopFileTime = stop.HasValue ? stop.Value.UtcDateTime.ToFileTimeUtc() : 0,
            });
            return this;
        }
    }

    internal static class MeetingAppMicrophoneSelfCheck
    {
        internal const string TeamsPackaged = "MSTeams_8wekyb3d8bbwe";
        internal const string TeamsMediaHost = "Microsoft.Teams.SlimCoreVdiHost.win-x64_8wekyb3d8bbwe";
        internal const string ZoomDesktop = "C:#Users#someone#AppData#Roaming#Zoom#bin#Zoom.exe";
        internal const string ClassicTeams = "C:#Users#someone#AppData#Local#Microsoft#Teams#current#Teams.exe";
        internal const string OurOwnExe = "C:#Users#someone#AppData#Local#Programs#Desktop AI Companion#DesktopAICompanion.exe";

        internal static void Run(Action<string, bool> check)
        {
            // ---- which keys are meeting apps ----
            check("meetingapps: new Teams' package key is Teams",
                MeetingAppMicrophone.Classify(TeamsPackaged, true) == MeetingApp.Teams);
            check("meetingapps: new Teams' media-host package key is Teams too (it holds the microphone during a call)",
                MeetingAppMicrophone.Classify(TeamsMediaHost, true) == MeetingApp.Teams);
            check("meetingapps: Zoom's desktop key is Zoom",
                MeetingAppMicrophone.Classify(ZoomDesktop, false) == MeetingApp.Zoom);
            check("meetingapps: classic Teams' desktop key is Teams",
                MeetingAppMicrophone.Classify(ClassicTeams, false) == MeetingApp.Teams);
            check("meetingapps: this app's own key is not a meeting app",
                MeetingAppMicrophone.Classify(OurOwnExe, false) == MeetingApp.None);
            check("meetingapps: the exe NAME decides, whole -- NotZoom.exe is not Zoom, and neither is an exe under a Zoom folder",
                MeetingAppMicrophone.Classify("C:#Tools#NotZoom.exe", false) == MeetingApp.None
                && MeetingAppMicrophone.Classify("C:#Zoom#bin#Recorder.exe", false) == MeetingApp.None);
            check("meetingapps: a package name in the desktop list, or a desktop name in the package list, is nothing",
                MeetingAppMicrophone.Classify(TeamsPackaged, false) == MeetingApp.None
                && MeetingAppMicrophone.Classify("Zoom.exe", true) == MeetingApp.None);
            check("meetingapps: other packaged apps are nothing",
                MeetingAppMicrophone.Classify("Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe", true) == MeetingApp.None);

            // ---- the in-use rule, and merging Teams' two keys ----
            DateTimeOffset t0 = new DateTimeOffset(2026, 10, 15, 16, 0, 0, TimeSpan.Zero);
            var holding = new FakeConsentStore()
                .Add(TeamsPackaged, true, t0.AddMinutes(-90), t0.AddMinutes(-80))   // the app itself, released
                .Add(TeamsMediaHost, true, t0.AddMinutes(-5), null)                // the media host, holding now
                .Add(ZoomDesktop, false, t0.AddDays(-1), t0.AddDays(-1).AddMinutes(33))
                .Add(OurOwnExe, false, t0.AddMinutes(-4), null);                   // ourselves, recording: ignored
            MicUseSnapshot s = new MeetingAppMicrophone(holding).Read();
            check("meetingapps: WITNESS the fake record was read once", holding.Reads == 1 && s.Available);
            check("meetingapps: Teams is in use when ANY of its keys has a start and no stop",
                s.Teams.InUse && s.InUseApp == MeetingApp.Teams && s.AnyInUse);
            check("meetingapps: Teams' last start is the newest across its keys",
                s.Teams.LastStartUtc.HasValue && s.Teams.LastStartUtc.Value == t0.AddMinutes(-5));
            check("meetingapps: a key with a stop after its start is not in use (Zoom, yesterday)",
                !s.Zoom.InUse && s.Zoom.LastStopUtc.HasValue && s.Zoom.LastStopUtc.Value == t0.AddDays(-1).AddMinutes(33));
            check("meetingapps: this app's own recording never counts as a meeting app holding the microphone",
                s.For(MeetingApp.None) == null);

            var released = new FakeConsentStore()
                .Add(TeamsPackaged, true, t0.AddMinutes(-60), t0.AddMinutes(-1))
                .Add(TeamsMediaHost, true, t0.AddMinutes(-60), t0.AddMinutes(-1))
                .Add(ZoomDesktop, false, t0.AddMinutes(-2), null);
            MicUseSnapshot r = new MeetingAppMicrophone(released).Read();
            check("meetingapps: Teams released and Zoom holding reads as Zoom in use",
                !r.Teams.InUse && r.Zoom.InUse && r.InUseApp == MeetingApp.Zoom);

            // ---- the record missing, or unreadable: never "nothing in use" ----
            var absent = new FakeConsentStore { Entries = null };
            MicUseSnapshot a = new MeetingAppMicrophone(absent).Read();
            check("meetingapps: an absent record is Unavailable with a reason, not an empty record",
                !a.Available && a.Unavailable == MeetingAppMicrophone.UnavailableNoRecord && !a.AnyInUse
                && a.InUseApp == MeetingApp.None);
            var failing = new FakeConsentStore { Throw = new UnauthorizedAccessException("C:\\secret\\path") };
            MicUseSnapshot f = null;
            bool threw = false;
            try { f = new MeetingAppMicrophone(failing).Read(); } catch { threw = true; }
            check("meetingapps: a read that throws is caught, Unavailable, and names only the exception TYPE",
                !threw && f != null && !f.Available && f.Unavailable.Contains("UnauthorizedAccessException")
                && f.Unavailable.IndexOf("secret", StringComparison.Ordinal) < 0);

            // ---- the FILETIME conversion ----
            check("meetingapps: a zero or negative FILETIME is no time at all",
                !MeetingAppMicrophone.FromFileTime(0).HasValue && !MeetingAppMicrophone.FromFileTime(-5).HasValue);
            check("meetingapps: a FILETIME converts to the same UTC instant",
                MeetingAppMicrophone.FromFileTime(t0.UtcDateTime.ToFileTimeUtc()) == t0);

            // ---- the real record: it may say anything, but reading it must not throw ----
            bool liveThrew = false;
            MicUseSnapshot live = null;
            try { live = new MeetingAppMicrophone().Read(); } catch { liveThrew = true; }
            check("meetingapps: reading this machine's real record does not throw (available: "
                + (live != null && live.Available ? "yes" : "no") + ")", !liveThrew && live != null);
        }
    }
}
