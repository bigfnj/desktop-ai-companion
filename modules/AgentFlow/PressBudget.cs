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
    ///   Three identical presses in a row and it stands down. This is the one that matters --
    ///   it catches a broken click in thirty seconds rather than at whatever the hourly cap is.
    ///
    ///   RATE. A backstop for everything not imagined above. Ten presses in five minutes is far
    ///   more than a person generates by working, and far less than a loop generates in an hour.
    ///
    /// Standing down is STICKY until something changes: a different prompt clears the repeat
    /// counter, and only time clears the rate window. A guard that reset itself on the next tick
    /// would turn a death loop into a slightly slower death loop.
    /// </summary>
    internal sealed class PressBudget
    {
        /// <summary>Identical presses in a row before it stops. Three, not one: a prompt can
        /// legitimately recur (the same command asked twice), and two in a row is not yet a loop.</summary>
        internal const int MaxIdenticalPresses = 3;
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
        private string _lastSignature;
        private int _identical;

        /// <summary>
        /// May the module press this prompt? <paramref name="signature"/> identifies the prompt --
        /// the tool plus its option rows -- so "the same one again" is answerable.
        ///
        /// Records the press when it returns true. The caller does not get to decide whether a
        /// press counts, because the whole failure mode this guards against is a press that the
        /// caller believes did not happen.
        /// </summary>
        public bool TryPress(string signature, DateTime nowUtc, out string refusal)
        {
            refusal = null;

            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                if (_identical >= MaxIdenticalPresses)
                {
                    refusal = string.Format(CultureInfo.InvariantCulture,
                        "standing down: pressed the same prompt {0} times and it is still there, so "
                        + "the click is not taking. Switch auto-approve off and on to try again.",
                        _identical);
                    return false;
                }
            }
            else
            {
                _identical = 0;
                _lastSignature = signature;
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
            _identical++;
            return true;
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
        /// are different prompts without any click being read as proof of anything. The cost,
        /// accepted and stated: three identical retries of one command inside thirty seconds, each
        /// genuinely answered, look like one wedged card and stand the module down until the switch
        /// moves; the refusal says how, and the sweep that finds nothing between them clears it.
        /// </summary>
        public void NotePromptCleared()
        {
            _identical = 0;
            _lastSignature = null;
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
            _identical = 0;
            _lastSignature = null;
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
            var text = new System.Text.StringBuilder(toolName ?? "");
            if (options != null)
                foreach (string option in options) { text.Append('\u001F'); text.Append(option ?? ""); }
            if (!string.IsNullOrEmpty(fingerprint)) { text.Append('\u001F'); text.Append(fingerprint); }
            return text.ToString();
        }
    }
}
