using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// How often this module is allowed to press anything, and when it must stop.
    ///
    /// BACKLOG.md asked for this by name before the answering half existed: "a death-loop guard
    /// belongs in whichever version first presses anything." This is that guard, and it is
    /// separate from NotifyBudget on purpose. That one stops the companion repeating itself out
    /// loud, and the worst case of getting it wrong is an annoying pet. This one stops the module
    /// approving things without end, and the worst case of getting it wrong is a machine that
    /// approved four hundred calls overnight because a click stopped landing.
    ///
    /// Two limits, because there are two different runaways:
    ///
    ///   REPEAT. The same prompt pressed over and over is the loop that actually happens: the
    ///   click reports success, the UI does not advance, and the next poll sees the same thing.
    ///   Five presses of the same card and that card is left alone. This is the one that matters --
    ///   it catches a broken click within five checks rather than at whatever the rate cap is.
    ///
    ///   RATE. A backstop for everything not imagined above, set by the user, 9999 in five minutes
    ///   unless they ask for less.
    ///
    /// Standing down is STICKY until something changes: the card going away clears its count, and
    /// only time clears the rate window. A guard that reset itself on the next tick would turn a
    /// death loop into a slightly slower death loop.
    ///
    /// <para>
    /// THE COUNT IS PER PANEL AND CARD (1.11.0, BUG-018). It was one counter for the whole module,
    /// keyed on the last signature pressed, which is right only while one prompt is on screen at a
    /// time. With a stuck card in one panel and a working prompt in another the two signatures
    /// alternated: after three presses the stuck card was refused, the sweep went on and pressed the
    /// other panel, that different signature reset the counter, and the next sweep pressed the stuck
    /// card again as a new prompt, three ticks in four for as long as it stood. Pressing every panel in
    /// one sweep would have made that every tick. So each (target, signature) keeps its own count, and
    /// a count is dropped only by a sweep that READ its panel and did not find its card there
    /// (<see cref="EndSweep"/>), or when its panel is no longer listed at all. A panel the sweep could
    /// not read keeps its counts: not seeing a card is not the same as seeing it gone.
    /// </para>
    /// </summary>
    internal sealed class PressBudget
    {
        /// <summary>
        /// Presses of one card before that card is left alone. Five since 1.11.0 (three before): the
        /// owner's call of 2026-10-21, made beside the check interval becoming a setting. The count is
        /// of presses, not seconds, so at a one-second interval three presses was three seconds of a
        /// card that may only have been slow to leave after a click it took. Two more clicks on the
        /// approve-once row of a card that really is stuck are the price, and they press nothing new.
        /// </summary>
        internal const int MaxIdenticalPresses = 5;
        /// <summary>
        /// The rate cap's range and its DEFAULT, which is the top of that range.
        ///
        /// It was a hard-coded 10 per five minutes, and on 2026-09-22 that stood the module down
        /// in the middle of the owner's ordinary work: two agent sessions running side by side
        /// reach ten presses in five minutes easily, and the log said so while the prompts sat
        /// there. The owner's ruling was "it should be unlimited, it is either on or off -- if you
        /// want a budget it should be another option where the user can put an approval limit".
        ///
        /// So the default is the MAXIMUM. Out of the box, on means on; a user who wants a backstop
        /// dials it down, rather than discovering one they never asked for. The cap is still real
        /// code rather than removed, because "approve at most N while I am away" is a reasonable
        /// thing to want and there is now a way to ask for it.
        /// </summary>
        internal const int MinPressLimit = 1;
        internal const int MaxPressLimit = 9999;
        internal const int DefaultPressLimit = MaxPressLimit;

        internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

        // VOLATILE, and it is the only member of this class the UI thread touches. The list and the
        // repeat state below are the poll worker's alone; the limit is written by SavePaneValues on
        // the UI thread so a user who has just raised it -- the thing one does the moment it has
        // stood the module down -- does not wait a tick. An aligned int store is atomic either way;
        // volatile is what makes the worker's next TryPress read the new value rather than a cached
        // one, and it is the word that makes the cross-thread write a stated fact rather than an
        // accident the field's doc used to deny (F028).
        private volatile int _pressLimit = DefaultPressLimit;
        private readonly List<DateTime> _presses = new List<DateTime>();

        /// <summary>One card's presses, and the panel it is on, so <see cref="EndSweep"/> can tell a card
        /// that went away from one whose panel was not read.</summary>
        private sealed class Repeat
        {
            public string Target;
            public int Count;
        }

        // Keyed by Key(target, signature). Poll-worker only, like the list above.
        private readonly Dictionary<string, Repeat> _repeats = new Dictionary<string, Repeat>(StringComparer.Ordinal);
        // The keys seen since the last EndSweep, pressed or not (NoteSeen): a stood-down card the sweep read is still there.
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// May the module press this prompt? <paramref name="signature"/> identifies the prompt --
        /// the tool plus its option rows -- so "the same one again" is answerable.
        ///
        /// Records the press when it returns true. The caller does not get to decide whether a
        /// press counts, because the whole failure mode this guards against is a press that the
        /// caller believes did not happen.
        ///
        /// This form has no panel, so every prompt it is handed shares one, and a sweep's
        /// <see cref="EndSweep"/> drops its counts. The sweep presses through the form with a target.
        /// </summary>
        public bool TryPress(string signature, DateTime nowUtc, out string refusal)
        {
            return TryPress(null, signature, nowUtc, out refusal);
        }

        /// <summary>As above, for the card <paramref name="signature"/> on the panel <paramref name="target"/>
        /// (the CDP target id). Two panels showing alike-signed cards are two counts.</summary>
        public bool TryPress(string target, string signature, DateTime nowUtc, out string refusal)
        {
            refusal = null;
            // Not marked seen here: SweepPass.Handle marks every card it reads, pressed or not, before this runs.
            string key = Key(target, signature);

            Repeat repeat;
            if (_repeats.TryGetValue(key, out repeat) && repeat.Count >= MaxIdenticalPresses)
            {
                refusal = string.Format(CultureInfo.InvariantCulture,
                    "standing down: pressed the same prompt {0} times and it is still there, so "
                    + "the click is not taking. Switch auto-approve off and on to try again.",
                    repeat.Count);
                return false;
            }

            Prune(nowUtc);
            if (_presses.Count >= _pressLimit)
            {
                refusal = string.Format(CultureInfo.InvariantCulture,
                    "standing down: {0} presses in the last {1} minutes reaches the approval limit "
                    + "you set. Raise it in the AgentFlow options, or switch auto-approve off and "
                    + "on to start a fresh window.",
                    _presses.Count, (int)Window.TotalMinutes);
                return false;
            }

            _presses.Add(nowUtc);
            if (repeat == null) _repeats[key] = repeat = new Repeat { Target = target ?? "" };
            repeat.Count++;
            return true;
        }

        /// <summary>
        /// The sweep READ this card and did not press it (a refusal, a disabled row, a switch that moved), so
        /// it is still on screen and its count must survive <see cref="EndSweep"/>. A card that stands down
        /// at five and then reads disabled for one sweep would otherwise come back with a clean count.
        /// </summary>
        public void NoteSeen(string target, string signature)
        {
            _seen.Add(Key(target, signature));
        }

        /// <summary>
        /// The end of one sweep. <paramref name="listed"/> is every agent panel the port listed and
        /// <paramref name="read"/> the ones whose page was actually read (no prompt, or a prompt handed on).
        /// A count is dropped when its panel was read and its card was not seen, which is the card going
        /// away, or when its panel is no longer listed, which is the panel closing. A panel that was listed
        /// and NOT read keeps every count it has: an attach that failed says nothing about the card.
        ///
        /// This is what <see cref="NotePromptCleared"/> did for the whole module when a sweep found nothing,
        /// done per card, so a stuck card in one panel is still the stuck card while another panel works.
        /// </summary>
        public void EndSweep(ICollection<string> listed, ICollection<string> read)
        {
            var gone = new List<string>();
            foreach (KeyValuePair<string, Repeat> entry in _repeats)
            {
                string target = entry.Value.Target;
                bool stillListed = listed != null && listed.Contains(target);
                bool wasRead = read != null && read.Contains(target);
                if (!stillListed || (wasRead && !_seen.Contains(entry.Key))) gone.Add(entry.Key);
            }
            foreach (string key in gone) _repeats.Remove(key);
            _seen.Clear();
        }

        /// <summary>How many cards hold a count, for the assertions: a bound nothing observes is not a bound.</summary>
        internal int RepeatsHeld { get { return _repeats.Count; } }

        /// <summary>The card's key: its panel and its signature, joined by U+001E, which no signature
        /// carries (its parts are joined by U+001F) and no CDP target id contains.</summary>
        private static string Key(string target, string signature)
        {
            return (target ?? "") + "\u001E" + (signature ?? "");
        }

        /// <summary>
        /// Called when a whole sweep found nothing to press: the prompt is gone, which is what success
        /// looks like, so the repeat counter has nothing left to be suspicious about. The rate cap
        /// still applies.
        ///
        /// NOT called on a confirmed click any more (RA-049). From 1.4.0 Decide also called this
        /// whenever the CDP layer answered 'clicked', on the reasoning that a click that ran is
        /// evidence the prompt went away. It is not: the loop this guard exists for is precisely the
        /// click that RUNS and the card that STAYS, and clearing on 'clicked' made that case
        /// unreachable -- a wedged card was re-clicked and logged every ten seconds with no
        /// stand-down, and the guard fired only for clicks that never ran at all. What 1.4.0 was
        /// really fixing (2026-09-22) was three DIFFERENT compound-command prompts signing alike,
        /// `Bash|Yes|No`, no wider-grant row to tell them apart, latching the module off. That is
        /// the SIGNATURE's job: it now carries the card's fingerprint (RA-023), so different cards
        /// are different prompts without any click being read as proof of anything. The cost that
        /// was accepted here, "three identical retries of one command inside thirty seconds", was
        /// the wrong size (BUG-017): an Edit card shows its path and nothing else, so EVERY run of
        /// edits to one file signed alike, and a subagent's seven edits to one script stood the
        /// module down at the fourth with every press having landed. The card's ELEMENT is now in
        /// the signature too (PromptView.CardId), so the cost is gone for Claude Code, where each
        /// request renders in its own element. Codex cards carry an element id too since 1.11.0.
        ///
        /// Since 1.11.0 the sweep calls <see cref="EndSweep"/> instead, which does this per card; this
        /// clears every card at once and is kept for the assertions that model "the card went away".
        /// </summary>
        public void NotePromptCleared()
        {
            _repeats.Clear();
            _seen.Clear();
        }

        /// <summary>
        /// How many presses are allowed in a five-minute window. Clamped, so a settings file
        /// holding nonsense cannot switch the cap off or invert it.
        /// </summary>
        public void SetPressLimit(int limit)
        {
            _pressLimit = limit < MinPressLimit ? MinPressLimit
                        : (limit > MaxPressLimit ? MaxPressLimit : limit);
        }

        /// <summary>What the cap currently is, for the pane and the assertions.</summary>
        public int PressLimit { get { return _pressLimit; } }

        /// <summary>Forget everything. The switch moving is the user saying "try again".</summary>
        public void Reset()
        {
            _presses.Clear();
            _repeats.Clear();
            _seen.Clear();
        }


        private void Prune(DateTime nowUtc)
        {
            // From the front, because the list is in time order and only the head can expire.
            while (_presses.Count > 0 && nowUtc - _presses[0] > Window) _presses.RemoveAt(0);
        }

        /// <summary>
        /// A prompt's identity for repeat detection: the tool and every option row.
        ///
        /// The OPTIONS are part of it, not just the tool. Two different Bash calls produce prompts
        /// whose approve row reads the same but whose wider-grant row names a different rule, so
        /// keying on the tool alone would read a normal run of shell commands as one prompt
        /// repeating and stand down in the middle of ordinary work.
        /// </summary>
        public static string Signature(string toolName, IList<string> options)
        {
            return Signature(toolName, options, null);
        }

        /// <summary>
        /// As above, plus the CARD's identity (<see cref="PromptView.Fingerprint"/>), which is what tells
        /// two prompts whose labels sign alike apart (RA-023): every compound-command prompt signs
        /// `Bash|Yes|No`, and until the card itself was part of the signature, three of them in a row
        /// were one prompt pressed three times. Empty when the reader had none, in which case the
        /// signature is the label-only one it always was.
        /// </summary>
        public static string Signature(string toolName, IList<string> options, string fingerprint)
        {
            return Signature(toolName, options, fingerprint, null);
        }

        /// <summary>
        /// As above, plus the card ELEMENT's identity (<see cref="PromptView.CardId"/>, BUG-017): two cards
        /// whose text is identical are two prompts when they are two elements, and one prompt pressed again
        /// only while the same element is still there. Text AND element, so either one differing is a
        /// different prompt. Empty when the reader minted none (every Codex read), which leaves the
        /// signature exactly the text-only one above. Marked with '#', so a card with an id and no
        /// fingerprint can never sign the same as a card with that fingerprint and no id.
        /// </summary>
        public static string Signature(string toolName, IList<string> options, string fingerprint, string cardId)
        {
            var text = new System.Text.StringBuilder(toolName ?? "");
            if (options != null)
                foreach (string option in options) { text.Append('\u001F'); text.Append(option ?? ""); }
            if (!string.IsNullOrEmpty(fingerprint)) { text.Append('\u001F'); text.Append(fingerprint); }
            if (!string.IsNullOrEmpty(cardId)) { text.Append('\u001F'); text.Append('#'); text.Append(cardId); }
            return text.ToString();
        }
    }
}
