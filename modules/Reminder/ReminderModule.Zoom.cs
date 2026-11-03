using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;
using DesktopAICompanion.ModuleKit;

namespace DesktopAICompanion.ReminderModule
{
    // The Zoom slot (1.3.0; the owner, 2026-11-03: "replace calendar 5 with a dedicated Zoom slot").
    //
    // Slot 5 keeps its id ("cal5"), so its settings keys and its fired marks keep their prefix, but its card is "Zoom"
    // and its feed is Zoom or Off. It reads the scheduled meetings the user HOSTS from Zoom's API after a browser
    // sign-in (ZoomSignIn, ZoomTokenStore, ZoomMeetingsSource). Meetings someone else hosts arrive as calendar invites
    // in the other slots, and a meeting on both is announced once, from the calendar (AggregateCalendarSource's twins,
    // MirrorTwinFired below). A Calendar 5 configured before 1.3.0 is moved to the first free slot at the first start,
    // or parked with a note when all four are taken (MigrateZoomSlot).
    public sealed partial class ReminderModule
    {
        private const int ZoomSlot = MaxSlots;
        internal const string ZoomClientIdKey = "zoomClientId";
        // "Also read meetings I'm invited to": GET /users/me/upcoming_meetings beside the hosted list. Off by default, so a
        // sign-in set up for hosted meetings only is never refused on a scope it was not granted (ZoomMeetingsSource).
        internal const string ZoomInvitedKey = "zoomInvited";
        private const string ZoomStatusKey = "zoomStatus";
        private const string ZoomAboutKey = "zoomAbout";
        internal const string ZoomMigratedKey = "migratedZoomSlot";
        internal const string ZoomSlotNoteKey = "zoomSlotNote";
        internal const string ZoomParkedPrefix = "cal5.parked.";

        // The per-slot keys a feed is made of, each with the value its getter falls back to when the key is unset, for
        // the move out of slot 5 (SlotFeedKeys). The style keys and defaults are SpeechStyleSettings'.
        private static readonly KeyValuePair<string, string>[] SlotFeedKeyDefaults =
        {
            new KeyValuePair<string, string>("type", SourceOff),
            new KeyValuePair<string, string>("label", ""),
            new KeyValuePair<string, string>("url", ""),
            new KeyValuePair<string, string>("file", ""),
            new KeyValuePair<string, string>("companion", ""),
            new KeyValuePair<string, string>("chimeOn", "true"),
            new KeyValuePair<string, string>("chime", ""),
            new KeyValuePair<string, string>(SpeechStyleSettings.FontKey, "Default"),
            new KeyValuePair<string, string>(SpeechStyleSettings.SizeKey, "9"),
            new KeyValuePair<string, string>(SpeechStyleSettings.BoldKey, "false"),
            new KeyValuePair<string, string>(SpeechStyleSettings.ItalicKey, "false"),
            new KeyValuePair<string, string>(SpeechStyleSettings.UnderlineKey, "false"),
            new KeyValuePair<string, string>(SpeechStyleSettings.ColorKey, "Default"),
        };

        internal const string ZoomAbout =
            "Reads the Zoom meetings you HOST, straight from Zoom. Meetings other people invite you to come from your " +
            "calendar slots; a meeting that is on both is announced once, from the calendar. If your invites reach Zoom " +
            "but no calendar here (a connected calendar in Zoom), tick \"Also read meetings I'm invited to\".";

        private ZoomTokenStore _zoomTokens;
        private CancellationTokenSource _zoomSignInCts;
        private string _zoomSignInClientId;   // the client ID the pending sign-in is for

        /// <summary>The self-test's seam for the code exchange, so a sign-in runs end to end against the loopback
        /// listener without reaching zoom.us. Null in the shipped module.</summary>
        internal Func<string, string, string, string, ZoomSignIn.Tokens> ZoomExchangeForSelfTest;

        /// <summary>The self-test's seam for the Zoom slot's HTTP, read when the slot's source is built, so a module
        /// signed in by the self-test never calls api.zoom.us. Null in the shipped module.</summary>
        internal ZoomMeetingsSource.Getter ZoomGetForSelfTest;

        /// <summary>The card a slot's fields and buttons sit in: "Zoom" for slot 5, "Calendar N" for the rest. Also the
        /// name "Test this reminder" speaks when the slot has none of its own.</summary>
        internal static string SlotGroup(int i)
        {
            return i == ZoomSlot ? "Zoom" : "Calendar " + i.ToString(CultureInfo.InvariantCulture);
        }

        private static string ZoomFolder(IHost host)
        {
            try
            {
                ModulePaths paths = ModulePaths.FromStorage(host == null ? null : host.GetStorage(Id), Id);
                return paths.HasRoot ? paths.Root : null;
            }
            catch { return null; }
        }

        // --- the card ---------------------------------------------------------------------------------

        private IEnumerable<SettingField> ZoomSlotFields(string g)
        {
            string onlyWhenZoom = SlotKey(ZoomSlot, "type") + "=" + SourceZoom;
            yield return new SettingField { Id = SlotKey(ZoomSlot, "type"), Label = "Feed type", Kind = SettingKind.Enum, Options = new[] { SourceOff, SourceZoom }, Group = g };
            yield return new SettingField { Id = SlotKey(ZoomSlot, ZoomAboutKey), Label = "What this reads", Kind = SettingKind.Info, Group = g };
            yield return new SettingField { Id = SlotKey(ZoomSlot, "label"), Label = "Name (spoken with the reminder; blank is fine, the bubble says \"(Zoom)\")", Kind = SettingKind.Text, Group = g };
            yield return new SettingField
            {
                Id = SlotKey(ZoomSlot, ZoomClientIdKey),
                Label = "Zoom app client ID (the PUBLIC client ID of a Zoom app you make; see ZOOM.md)",
                Kind = SettingKind.Text,
                Group = g,
                EnabledWhen = onlyWhenZoom,
            };
            yield return new SettingField
            {
                Id = SlotKey(ZoomSlot, ZoomInvitedKey),
                Label = "Also read meetings I'm invited to (the next 24 hours; the app needs one more scope, see ZOOM.md)",
                Kind = SettingKind.Bool,
                Group = g,
                EnabledWhen = onlyWhenZoom,
            };
            yield return new SettingField { Id = SlotKey(ZoomSlot, ZoomStatusKey), Label = "Zoom sign-in", Kind = SettingKind.Info, Group = g };
        }

        private IEnumerable<PaneAction> ZoomSlotActions(string g)
        {
            yield return new PaneAction
            {
                Label = "Sign in to Zoom…",
                Group = g,
                ReloadPaneAfter = true,
                // The on-screen values, so a client ID typed and not yet applied is caught rather than signed in
                // under the saved one.
                InvokeWithPendingAsync = pending => SignInToZoomAsync(pending),
                InvokeAsync = () => SignInToZoomAsync(null),
            };
            yield return new PaneAction
            {
                Label = "Sign out of Zoom",
                Group = g,
                ReloadPaneAfter = true,
                InvokeAsync = () => Task.FromResult(SignOutOfZoom()),
            };
        }

        private string ZoomSlotType()
        {
            return string.Equals(_settings.Get(SlotKey(ZoomSlot, "type"), SourceOff), SourceZoom, StringComparison.Ordinal) ? SourceZoom : SourceOff;
        }

        private static string CoerceZoomSlotType(string value)
        {
            return string.Equals((value ?? "").Trim(), SourceZoom, StringComparison.Ordinal) ? SourceZoom : SourceOff;
        }

        private void AddZoomLoadValues(Dictionary<string, string> values)
        {
            values[SlotKey(ZoomSlot, ZoomAboutKey)] = ZoomAbout;
            values[SlotKey(ZoomSlot, ZoomClientIdKey)] = _settings.Get(SlotKey(ZoomSlot, ZoomClientIdKey), "");
            values[SlotKey(ZoomSlot, ZoomInvitedKey)] = _settings.GetBool(SlotKey(ZoomSlot, ZoomInvitedKey), false) ? "true" : "false";
            values[SlotKey(ZoomSlot, ZoomStatusKey)] = ZoomStatusText();
        }

        private void SaveZoomValues(IReadOnlyDictionary<string, string> values)
        {
            string v;
            if (values.TryGetValue(SlotKey(ZoomSlot, ZoomClientIdKey), out v))
                _settings.Set(SlotKey(ZoomSlot, ZoomClientIdKey), (v ?? "").Trim());
            bool invited;
            if (values.TryGetValue(SlotKey(ZoomSlot, ZoomInvitedKey), out v) && bool.TryParse(v, out invited))
                _settings.Set(SlotKey(ZoomSlot, ZoomInvitedKey), invited ? "true" : "false");
            // The host greys only the button it is awaiting, so the card can be edited and applied while a sign-in is in
            // the browser. One begun for a client ID the card no longer names, or with the slot now Off, is abandoned:
            // its listener closes now, and SignInToZoomAsync's own check refuses it if the browser already answered.
            if (_zoomSignInCts != null && !SignInStillWanted(_zoomSignInClientId)) CancelZoomSignIn();
        }

        /// <summary>Whether a sign-in begun for <paramref name="clientId"/> is still what the saved card asks for.</summary>
        private bool SignInStillWanted(string clientId)
        {
            string saved;
            return string.Equals(ZoomSlotType(), SourceZoom, StringComparison.Ordinal)
                && ZoomSignIn.CheckClientId(_settings.Get(SlotKey(ZoomSlot, ZoomClientIdKey), ""), out saved) == null
                && string.Equals(saved, clientId, StringComparison.Ordinal);
        }

        /// <summary>The card's sign-in line: what is missing, in the order the user has to do it, or the signed-in state
        /// with what the slot last read. A parked Calendar 5 (MigrateZoomSlot) leads, until the user turns Zoom on.</summary>
        internal string ZoomStatusText()
        {
            string note = _settings.Get(ZoomSlotNoteKey, "");
            string lead = string.IsNullOrWhiteSpace(note) ? "" : note.Trim() + " ";
            if (!string.Equals(ZoomSlotType(), SourceZoom, StringComparison.Ordinal))
                return lead + "Off. Choose Zoom as the feed type, paste the app's client ID, Apply, then press Sign in to Zoom.";
            string clientId;
            string problem = ZoomSignIn.CheckClientId(_settings.Get(SlotKey(ZoomSlot, ZoomClientIdKey), ""), out clientId);
            if (problem != null) return "✗ " + problem + " ZOOM.md says how to make the Zoom app.";
            if (_zoomTokens == null || !_zoomTokens.HasFolder) return "✗ This module has no data folder to keep a Zoom sign-in in.";
            string signedIn = _zoomTokens.SignedInClientId;
            if (signedIn == null)
                return _zoomTokens.Unreadable
                    ? "✗ The saved Zoom sign-in could not be opened (another Windows user's, or damaged). Press Sign in to Zoom."
                    : "Not signed in. Press Sign in to Zoom.";
            if (!string.Equals(signedIn, clientId, StringComparison.Ordinal))
                return "✗ Signed in with a different client ID from this one. Press Sign in to Zoom.";
            CalendarSnapshot snap = _lastSnapshot;
            bool healthy;
            if (snap == null || snap.SlotHealthy == null || !snap.SlotHealthy.TryGetValue(SlotId(ZoomSlot), out healthy))
                return "✓ Signed in. Not read yet.";
            int shown = snap.Events == null ? 0 : snap.Events.Count(e => e != null && e.SourceId == SlotId(ZoomSlot));
            int merged = snap.Twins == null ? 0 : snap.Twins.Keys.Count(k => SlotOf(k) == SlotId(ZoomSlot));
            string count = shown.ToString(CultureInfo.InvariantCulture) + " meeting(s) in the next 48 hours"
                + (merged > 0 ? " (and " + merged.ToString(CultureInfo.InvariantCulture) + " also on a calendar, announced from there)" : "");
            return healthy ? "✓ Signed in. " + count + "." : "⚠ Signed in. " + count + "; Feed status above says what went wrong.";
        }

        // --- signing in and out ------------------------------------------------------------------------

        private async Task<string> SignInToZoomAsync(IReadOnlyDictionary<string, string> pending)
        {
            try
            {
                if (pending != null)
                {
                    string shownType, shownId;
                    if (pending.TryGetValue(SlotKey(ZoomSlot, "type"), out shownType) && CoerceZoomSlotType(shownType) != ZoomSlotType())
                        return "✗ Apply first: the feed type on screen is not the saved one.";
                    if (pending.TryGetValue(SlotKey(ZoomSlot, ZoomClientIdKey), out shownId)
                        && !string.Equals((shownId ?? "").Trim(), _settings.Get(SlotKey(ZoomSlot, ZoomClientIdKey), "").Trim(), StringComparison.Ordinal))
                        return "✗ Apply first: the client ID on screen is not the saved one.";
                }
                if (!string.Equals(ZoomSlotType(), SourceZoom, StringComparison.Ordinal))
                    return "✗ Choose Zoom as this card's feed type and Apply, then sign in.";
                string clientId;
                string problem = ZoomSignIn.CheckClientId(_settings.Get(SlotKey(ZoomSlot, ZoomClientIdKey), ""), out clientId);
                if (problem != null) return "✗ " + problem + " Paste it, Apply, then sign in.";
                if (_zoomTokens == null || !_zoomTokens.HasFolder) return "✗ This module has no data folder to keep a Zoom sign-in in.";
                if (_zoomSignInCts != null) return "A Zoom sign-in is already waiting in the browser. Finish it there, or wait for it to time out.";

                // Read now, before the browser: a Sign out pressed while the browser is open moves it, and Save then
                // refuses this sign-in's tokens rather than writing them over the sign-out.
                int startedAt = _zoomTokens.Generation;
                var cts = new CancellationTokenSource();
                _zoomSignInCts = cts;
                _zoomSignInClientId = clientId;
                try
                {
                    ZoomSignIn.Authorization auth = await ZoomSignIn.AuthorizeAsync(clientId,
                        url => TryOpenUrl(url, "zoom sign-in"), ZoomSignIn.Timeout, cts.Token);
                    if (auth.Error != null) return "✗ " + auth.Error;
                    Func<string, string, string, string, ZoomSignIn.Tokens> exchange = ZoomExchangeForSelfTest ?? ZoomSignIn.ExchangeCode;
                    ZoomSignIn.Tokens tokens = await Task.Run(() => exchange(clientId, auth.Code, auth.RedirectUri, auth.Verifier));
                    if (_shutDown) return "✗ The app closed during the sign-in.";
                    // Cancelling stops the listener, not an exchange already under way: a Sign out, an Apply naming
                    // another client ID, or the slot switched Off while the code was being traded is checked here,
                    // before anything is kept.
                    if (cts.IsCancellationRequested || !SignInStillWanted(clientId))
                        return "✗ The Zoom card changed during the sign-in, so it was not kept. Press Sign in to Zoom again.";
                    if (tokens == null || tokens.Error != null) return "✗ " + (tokens == null ? "Zoom gave no answer." : tokens.Error);
                    string error;
                    if (!_zoomTokens.Save(clientId, tokens, startedAt, out error)) return "✗ " + error;
                    try { _host.Log(Id, "zoom: signed in"); } catch { }
                    // The new sign-in is a new refresh key, so this tick's Fetch kicks the first read at once.
                    CheckDue();
                    return "✓ Signed in to Zoom. Your meetings appear in a few seconds; press Check now to see them sooner.";
                }
                finally
                {
                    if (ReferenceEquals(_zoomSignInCts, cts)) { _zoomSignInCts = null; _zoomSignInClientId = null; }
                    cts.Dispose();
                }
            }
            catch (Exception ex)
            {
                return "✗ The Zoom sign-in failed (" + ex.GetType().Name + ").";
            }
        }

        private string SignOutOfZoom()
        {
            if (_zoomTokens == null) return "Not signed in to Zoom.";
            // A sign-in still in the browser is abandoned too: it would otherwise land a moment later and undo this.
            CancelZoomSignIn();
            string words;
            bool removed = _zoomTokens.SignOut(out words);
            // The user's data, so the delete is logged either way (the delete-logging rule).
            try { _host.Log(Id, removed ? "zoom: signed out, saved sign-in removed" : "zoom: sign-out failed, saved sign-in not removed"); } catch { }
            CheckDue();
            return words;
        }

        private void CancelZoomSignIn()
        {
            CancellationTokenSource cts = _zoomSignInCts;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
        }

        // --- moving a pre-1.3.0 Calendar 5 out ----------------------------------------------------------

        /// <summary>The slot's setting suffixes with their defaults; the key is SlotKey(i, suffix).</summary>
        private static IEnumerable<KeyValuePair<string, string>> SlotFeedKeys()
        {
            return SlotFeedKeyDefaults;
        }

        /// <summary>What a slot's setting reads as: its saved value, or the default its getter would answer.</summary>
        private string EffectiveSlotValue(int i, KeyValuePair<string, string> key)
        {
            return _settings.Get(SlotKey(i, key.Key), key.Value);
        }

        /// <summary>
        /// Slot 5 was a calendar like the other four until 1.3.0. A user who had one there keeps it: it moves, every
        /// key and its fired marks with it, to the first slot among 1-4 whose feed is Off, and slot 5 starts as Zoom's,
        /// switched off. With all four taken it cannot move: its keys are copied under cal5.parked.* (nothing is
        /// deleted), slot 5 is switched off, and a note says so on the Zoom card and in the log until the user turns
        /// Zoom on. Runs once, keyed on a marker, BEFORE LoadFired reads the fired marks it rewrites; the marker is
        /// set in the same settings dictionary as the move, so the move cannot run twice in one process, and a save
        /// that fails leaves the disk as it was, so the next start runs the same move again from the same state.
        /// </summary>
        private void MigrateZoomSlot()
        {
            if (string.Equals(_settings.Get(ZoomMigratedKey, ""), "1", StringComparison.Ordinal)) return;
            string old = _settings.Get(SlotKey(ZoomSlot, "type"), "");
            bool hadFeed = old.Length > 0 && !string.Equals(old, SourceOff, StringComparison.Ordinal)
                && !string.Equals(old, SourceZoom, StringComparison.Ordinal);
            if (hadFeed)
            {
                int free = 0;
                for (int i = 1; i < ZoomSlot && free == 0; i++)
                {
                    string type = _settings.Get(SlotKey(i, "type"), "");
                    if (type.Length == 0 || string.Equals(type, SourceOff, StringComparison.Ordinal)) free = i;
                }
                if (free > 0)
                {
                    // The source's EFFECTIVE configuration, every key: an Off slot is not a blank one, and a key the old
                    // Calendar 5 never set (its chime on by default, say) must not inherit what an earlier calendar
                    // left in the destination (chime off).
                    foreach (KeyValuePair<string, string> key in SlotFeedKeys())
                        _settings.Set(SlotKey(free, key.Key), EffectiveSlotValue(ZoomSlot, key));
                    _settings.Set(ZoomSlotNoteKey, "");
                    RewriteFiredSlot(SlotId(ZoomSlot), SlotId(free));
                    try { _host.Log(Id, "calendar 5 (" + old + ") moved to calendar " + free.ToString(CultureInfo.InvariantCulture) + ": slot 5 is the Zoom slot from 1.3.0"); } catch { }
                }
                else
                {
                    foreach (KeyValuePair<string, string> key in SlotFeedKeys())
                        _settings.Set(ZoomParkedPrefix + key.Key, EffectiveSlotValue(ZoomSlot, key));
                    _settings.Set(ZoomSlotNoteKey, "Calendar 5 (" + old + ") was switched off to make room for Zoom, because calendars 1-4 are all in use; its settings are kept under cal5.parked in the settings file.");
                    try { _host.Log(Id, "calendar 5 (" + old + ") parked: calendars 1-4 are in use and slot 5 is the Zoom slot from 1.3.0"); } catch { }
                }
                // Slot 5 starts as Zoom's, off, with nothing of the old calendar left in its live keys: every key back
                // to the value its getter would answer unset.
                foreach (KeyValuePair<string, string> key in SlotFeedKeys())
                    _settings.Set(SlotKey(ZoomSlot, key.Key), key.Value);
            }
            _settings.Set(ZoomMigratedKey, "1");
            SaveSettings("calendar 5 becoming the Zoom slot");
        }

        /// <summary>Re-prefix the persisted fired marks of one slot ("cal5|uid@15" -> "cal2|uid@15"), so a moved calendar
        /// does not announce again what it already announced. Works on the settings string, before LoadFired.</summary>
        private void RewriteFiredSlot(string fromSlot, string toSlot)
        {
            string raw = _settings.Get("fired", "");
            if (string.IsNullOrEmpty(raw)) return;
            var lines = new List<string>();
            foreach (string id in raw.Split('\n'))
            {
                string trimmed = id.Trim();
                if (trimmed.Length == 0) continue;
                lines.Add(string.Equals(SlotOf(trimmed), fromSlot, StringComparison.Ordinal)
                    ? toSlot + trimmed.Substring(fromSlot.Length)
                    : trimmed);
            }
            _settings.Set("fired", string.Join("\n", lines));
        }

        // --- one meeting, two copies ------------------------------------------------------------------

        /// <summary>
        /// The id the Zoom slot gives (or would give) each calendar meeting that carries a Zoom join link: calendar
        /// event id -> "cal5|meetingNumber@startMinuteUtc", the same spelling <see cref="ZoomMeetingsSource.EventId"/>
        /// gives the Zoom copy. Deterministic, so it needs no Zoom read: after a restart a calendar can load before the
        /// Zoom slot has, and the mark the Zoom copy fired before the restart must still count for the calendar copy.
        /// Empty unless the Zoom slot is configured, so a mark is never mirrored to a slot that is
        /// not there, where the prune would drop it and the next tick put it back.
        /// </summary>
        internal static Dictionary<string, string> ZoomAliasPairs(CalendarSnapshot snap)
        {
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            string zoomSlot = SlotId(ZoomSlot);
            if (snap == null || snap.Events == null || snap.SlotHealthy == null || !snap.SlotHealthy.ContainsKey(zoomSlot)) return pairs;
            foreach (CalendarEvent e in snap.Events)
            {
                if (e == null || e.Id == null || string.Equals(e.SourceId, zoomSlot, StringComparison.Ordinal)) continue;
                string number = MeetingLinkDetector.ZoomMeetingId(e);
                if (number == null) continue;
                string alias = zoomSlot + "|" + ZoomMeetingsSource.EventId(number, e.Start);
                if (!string.Equals(alias, e.Id, StringComparison.Ordinal)) pairs[e.Id] = alias;
            }
            return pairs;
        }

        /// <summary>
        /// For each pair of copies of one meeting -- a twin the aggregate left out beside the calendar copy it kept, and
        /// every calendar meeting with a Zoom link beside the id the Zoom slot gives it (<see cref="ZoomAliasPairs"/>) --
        /// copy every fired mark either holds to the other, whatever its suffix (a lead's "@15", the join question's
        /// "@join"). Without it the copies merging after the Zoom copy fired would announce the calendar copy again,
        /// parting after the calendar copy fired would announce the Zoom copy again, and a restart where the calendar
        /// loads first would announce it again too. CheckDue runs it before the due check, after each announcement
        /// (so a second copy due in the same tick is skipped), and again after spending, before the save. True when
        /// anything was added.
        ///
        /// The rule this makes, while the Zoom card is on: one Zoom meeting is announced once, whichever slots carry
        /// it, from the first slot in order that has it due. Two calendars carrying the same Zoom invite are joined
        /// through the alias they share. Chosen over keeping them independent (as they were before 1.3.0, and still
        /// are with the Zoom card off) because independence through the alias depended on load order: two copies
        /// loaded together both announced, while one that loaded a tick later did not.
        ///
        /// Not covered: a meeting whose calendar copy carries no Zoom link with a meeting number (a personal-room link,
        /// or an invite with the link stripped) is two meetings as far as this can tell, and both copies announce.
        /// </summary>
        internal static bool MirrorTwinFired(CalendarSnapshot snap, HashSet<string> fired)
        {
            if (snap == null || fired == null || fired.Count == 0) return false;
            var partner = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (snap.Twins != null)
                foreach (KeyValuePair<string, string> pair in snap.Twins)
                {
                    AddPartner(partner, pair.Key, pair.Value);
                    AddPartner(partner, pair.Value, pair.Key);
                }
            foreach (KeyValuePair<string, string> pair in ZoomAliasPairs(snap))
            {
                AddPartner(partner, pair.Key, pair.Value);
                AddPartner(partner, pair.Value, pair.Key);
            }
            if (partner.Count == 0) return false;
            // Until nothing new: copies are joined through their shared alias (calendar 1 -> the Zoom id -> calendar 2),
            // so one pass would carry a mark only half way.
            bool any = false;
            for (int pass = 0; pass < 8 && MirrorOnce(partner, fired); pass++) any = true;
            return any;
        }

        private static bool MirrorOnce(Dictionary<string, List<string>> partner, HashSet<string> fired)
        {
            var add = new List<string>();
            foreach (string id in fired)
            {
                string eventId = ReminderScheduler.EventIdOf(id);
                List<string> others;
                if (eventId == null || eventId.Length == id.Length || !partner.TryGetValue(eventId, out others)) continue;
                string suffix = id.Substring(eventId.Length);
                foreach (string other in others) add.Add(other + suffix);
            }
            bool changed = false;
            foreach (string id in add) if (fired.Add(id)) changed = true;
            return changed;
        }

        private static void AddPartner(Dictionary<string, List<string>> partner, string from, string to)
        {
            List<string> list;
            if (!partner.TryGetValue(from, out list)) partner[from] = list = new List<string>();
            if (!list.Contains(to)) list.Add(to);
        }
    }
}
