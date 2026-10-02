using System;
using System.Globalization;
using System.Text.Json.Nodes;
using DesktopAICompanion.ModuleKit;   // UnicodeTextProgress

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// One reading of the flag Remembrance publishes on the host's shared context while it runs a local model:
    /// whisper on the stop path or in its own buttons, or its Ollama summary. AI Brain stands down while the flag is
    /// set and fresh, so a remark cannot load a model beside, or evict, the one Remembrance is using (the owner's
    /// request of 2026-10-02). Module-to-module calls do not exist (docs/DESIGN-REGISTER.md), so this is the whole
    /// interface between the two modules: the contract both lanes were briefed with, as Addendum 1 amended it.
    ///
    ///   key   remembrance.busy
    ///   value {"phase":"transcribing"|"summarizing"|"validating","at":"&lt;UTC, ISO-8601 round-trip&gt;"}
    ///         while busy, where "at" is the time of the LATEST publish (Remembrance republishes at every phase
    ///         change and before every summary request), and "" when clear (ReadContext's own "nothing published").
    ///
    /// Pure: parsing and the freshness rule only. What the module does with a reading, and when it reads (at each
    /// decision, on the UI thread, never through a subscription), is in AiBrainModule.
    /// </summary>
    internal sealed class RemembranceBusyFlag
    {
        /// <summary>The shared-context key Remembrance publishes under.</summary>
        internal const string Key = "remembrance.busy";

        /// <summary>
        /// A flag whose "at" is older than this is stale and ignored, so one that is somehow never cleared cannot
        /// leave AI Brain off for good. Well past Remembrance's longest span between publishes, whisper's 6-hour cap;
        /// the brief's figure.
        /// </summary>
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(8);

        /// <summary>
        /// A value longer than this is malformed without being parsed. The real value is about 65 characters, and
        /// it is read on the UI thread at every decision, so a runaway publisher cannot make each read parse a
        /// megabyte.
        /// </summary>
        internal const int MaximumCharacters = 1024;

        /// <summary>The three phases the contract names. Closed on purpose: see Parse.</summary>
        private static readonly string[] Phases = { "transcribing", "summarizing", "validating" };

        /// <summary>
        /// The ISO-8601 forms accepted for "at": the round-trip "o" form with its seven fraction digits, the same with
        /// fewer or none (System.Text.Json trims trailing zeros when it writes a DateTime), and either with an
        /// explicit offset instead of Z. A zone is REQUIRED: the contract says UTC, and a time with no zone would be
        /// read as local by one module and as UTC by the other.
        /// </summary>
        private static readonly string[] AtFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd'T'HH:mm:sszzz",
        };

        private RemembranceBusyFlag(string phase, DateTime atUtc)
        {
            Phase = phase;
            AtUtc = atUtc;
        }

        /// <summary>"transcribing", "summarizing" or "validating".</summary>
        internal string Phase { get; private set; }

        /// <summary>When Remembrance last published the flag, in UTC.</summary>
        internal DateTime AtUtc { get; private set; }

        /// <summary>
        /// Fresh while "at" is within <see cref="StaleAfter"/> of <paramref name="nowUtc"/> in EITHER direction. The
        /// past bound is the contract's ("an at older than 8 h is stale"); the future bound is this reader's,
        /// because an "at" far in the future (a clock moved back after the publish, or a publisher's bug) would
        /// otherwise stay fresh for as long as it stays ahead, which is the "off for good" the bound exists to
        /// prevent. Either way the flag fails open, as a malformed one does.
        /// </summary>
        internal bool IsFreshAt(DateTime nowUtc)
        {
            return (nowUtc - AtUtc).Duration() <= StaleAfter;
        }

        /// <summary>
        /// Read a published value. Returns the flag when it is busy; null when it is clear ("" or whitespace, which is
        /// also what an absent key reads as, so a Remembrance that is not installed is simply clear); and null with
        /// <paramref name="malformed"/> set to a short reason for anything else, which the module counts as NOT busy
        /// (it fails open) and logs once. Never throws.
        ///
        /// An unrecognised phase is malformed, not "busy with an unknown phase". The contract names three, and a new
        /// one is a contract change that updates both modules together; reading it as busy would stand down for a
        /// value nobody defined, and the log line names the word so the mismatch is visible in the file SUPPORT.md
        /// asks for. Recorded under `#### feature/aibrain-standdown` in docs/DESIGN-REGISTER.md.
        /// </summary>
        internal static RemembranceBusyFlag Parse(string raw, out string malformed)
        {
            malformed = null;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw.Length > MaximumCharacters)
            {
                malformed = "the value is longer than " + MaximumCharacters.ToString(CultureInfo.InvariantCulture) + " characters";
                return null;
            }
            JsonObject root;
            try { root = JsonNode.Parse(raw) as JsonObject; }
            catch (Exception)
            {
                malformed = "the value is not JSON";
                return null;
            }
            if (root == null)
            {
                malformed = "the value is not a JSON object";
                return null;
            }
            string phase, at;
            try
            {
                phase = JsonRead.Str(root["phase"]);
                at = JsonRead.Str(root["at"]);
            }
            catch (Exception)
            {
                // A repeated property name surfaces here, when the object is first read.
                malformed = "the object could not be read";
                return null;
            }
            if (phase.Length == 0)
            {
                malformed = "it names no phase";
                return null;
            }
            if (Array.IndexOf(Phases, phase) < 0)
            {
                malformed = "its phase '" + Clip(phase) + "' is not one the contract names";
                return null;
            }
            DateTimeOffset parsed;
            if (at.Length == 0 ||
                !DateTimeOffset.TryParseExact(at, AtFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out parsed))
            {
                malformed = "its at '" + Clip(at) + "' is not an ISO-8601 UTC time";
                return null;
            }
            return new RemembranceBusyFlag(phase, parsed.UtcDateTime);
        }

        /// <summary>A published word as it may appear in a log line: bounded, so the value cannot flood it, and cut
        /// at a code point, as the module's other cuts are (RA-057).</summary>
        private static string Clip(string text)
        {
            const int Most = 40;
            return text.Length <= Most ? text : UnicodeTextProgress.TruncateAtCodePointBoundary(text, Most) + "...";
        }
    }
}
