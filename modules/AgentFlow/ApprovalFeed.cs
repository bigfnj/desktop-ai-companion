using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// One approval, as it will be shown to the user on their own screen.
    ///
    /// Public only because <see cref="BlockedDetector.ApprovedSince"/> is public and takes a list
    /// of these; nothing outside this module references the assembly, so the distinction carries
    /// no meaning beyond matching the surface it appears in.
    /// </summary>
    public sealed class ApprovalEntry
    {
        public DateTime WhenLocal;
        /// <summary>Root executable or tool name. SAFE: one token, no arguments, no path.</summary>
        public string Root = "";
        /// <summary>
        /// The command as written. NEVER LOG, NEVER SPEAK, NEVER PERSIST.
        ///
        /// It exists for one place: the approvals card in the options pane, which is on the user's
        /// own screen and is the only surface in this module where the full text is appropriate.
        /// Reviewing approvals is the point of an approval module, and a list of bare executables
        /// cannot tell two `git` calls apart.
        /// </summary>
        public string Command = "";
    }

    /// <summary>
    /// The last few things the rules approved, held in memory so the pane can show them.
    ///
    /// THE WHOLE DESIGN IS THE CONTAINMENT. AgentFlow's standing rule is that command text never
    /// leaves TranscriptReader -- nothing logs it, speaks it or writes it anywhere -- and this is
    /// the first thing that keeps any. So the rules it holds itself to are narrow and asserted:
    ///
    ///   IN MEMORY ONLY. Never written to settings, never to the module's storage, never to the
    ///   diagnostic log. The ring dies with the process, which is the correct lifetime for
    ///   something whose only consumer is a window the user has open.
    ///
    ///   BOUNDED. Ten entries. Not a scrollback, not a history: the pane shows a recent handful
    ///   and the diagnostic log remains the durable record -- of executables, not commands.
    ///
    ///   THE SAFE HALF IS SEPARATE. <see cref="ApprovalEntry.Root"/> is what every existing
    ///   caller already logs. Nothing here widens what the log carries; the command lives beside
    ///   it in a field named so that logging it has to be a decision rather than an accident.
    /// </summary>
    internal sealed class ApprovalFeed
    {
        internal const int Cap = 10;

        private readonly List<ApprovalEntry> _entries = new List<ApprovalEntry>();

        public int Count { get { return _entries.Count; } }

        /// <summary>Newest first, which is the order a person reads a recent-activity list in.</summary>
        public IReadOnlyList<ApprovalEntry> Recent()
        {
            var copy = new List<ApprovalEntry>(_entries);
            copy.Reverse();
            return copy;
        }

        public void Record(DateTime whenLocal, string root, string command)
        {
            _entries.Add(new ApprovalEntry
            {
                WhenLocal = whenLocal,
                Root = string.IsNullOrEmpty(root) ? "?" : root,
                Command = command ?? "",
            });
            // From the front: the list is in time order, so only the oldest can fall off.
            while (_entries.Count > Cap) _entries.RemoveAt(0);
        }

        /// <summary>
        /// The card's text. One line per approval, newest first.
        ///
        /// Returns a sentence rather than an empty string when there is nothing yet, because an
        /// empty value renders as a blank area that reads like a broken pane rather than like a
        /// quiet one.
        /// </summary>
        public string Render()
        {
            if (_entries.Count == 0)
                return "Nothing approved yet this session.";

            var text = new System.Text.StringBuilder();
            foreach (ApprovalEntry entry in Recent())
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(entry.WhenLocal.ToString("HH:mm", CultureInfo.InvariantCulture));
                text.Append("  ");
                text.Append(entry.Root);
                if (entry.Command.Length > 0)
                {
                    text.Append("  ");
                    text.Append(OneLine(entry.Command));
                }
            }
            return text.ToString();
        }

        /// <summary>
        /// Flatten and cap a command for one row.
        ///
        /// A heredoc or a pasted script is hundreds of lines, and a TextBlock renders embedded
        /// newlines, so without this a single approval would push the other nine off the card and
        /// turn a summary into a wall. Truncation is marked, so a clipped command never reads as
        /// the whole thing.
        /// </summary>
        internal static string OneLine(string command)
        {
            if (string.IsNullOrEmpty(command)) return "";
            string flat = command.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            while (flat.IndexOf("  ", StringComparison.Ordinal) >= 0)
                flat = flat.Replace("  ", " ");
            flat = flat.Trim();
            const int Max = 70;
            return flat.Length <= Max ? flat : flat.Substring(0, Max - 1) + "…";
        }
    }
}
