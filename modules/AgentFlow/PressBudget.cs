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

        private int _pressLimit = DefaultPressLimit;
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
        /// Called when a press was made and the prompt then went away, which is what success looks
        /// like. Clears the repeat counter so a busy hour of genuine prompts is not mistaken for a
        /// loop -- the rate cap still applies.
        ///
        /// ⚠ THIS EXISTED AND WAS CALLED IN ONLY ONE OF THE TWO PLACES IT HAD TO BE, which is why
        /// the guard it protects misfired on 2026-09-22. It ran when a whole sweep found nothing,
        /// and a busy session never has such a sweep -- so three DIFFERENT compound-command
        /// prompts in a row, which all carry the identical signature `Bash|Yes|No` because a
        /// compound command gets no wider-grant row to distinguish it, counted as one prompt
        /// pressed three times and latched the module off. It is now also called on a click the
        /// CDP layer confirmed as "clicked", which is the direct evidence that the prompt went
        /// away, so the repeat guard only fires when a click genuinely does not take -- which is
        /// what its message has always claimed.
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
            var text = new System.Text.StringBuilder(toolName ?? "");
            if (options != null)
                foreach (string option in options) { text.Append(''); text.Append(option ?? ""); }
            return text.ToString();
        }
    }
}
