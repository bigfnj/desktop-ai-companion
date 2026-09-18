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
        internal const int MaxPressesPerWindow = 10;
        internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

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
            if (_presses.Count >= MaxPressesPerWindow)
            {
                refusal = string.Format(CultureInfo.InvariantCulture,
                    "standing down: {0} presses in the last {1} minutes is more than this is willing "
                    + "to do unattended", _presses.Count, (int)Window.TotalMinutes);
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
        /// </summary>
        public void NotePromptCleared()
        {
            _identical = 0;
            _lastSignature = null;
        }

        /// <summary>Forget everything. The switch moving is the user saying "try again".</summary>
        public void Reset()
        {
            _presses.Clear();
            _identical = 0;
            _lastSignature = null;
        }

        /// <summary>Presses still inside the window. Exposed so the pane can say where it stands.</summary>
        public int RecentPresses(DateTime nowUtc)
        {
            Prune(nowUtc);
            return _presses.Count;
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
