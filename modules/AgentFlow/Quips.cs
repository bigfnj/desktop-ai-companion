using System;
using System.Collections.Generic;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// What the companion says when a prompt has been waiting. Thirty-six of them, because one
    /// line repeated is how a desktop pet stops being charming.
    ///
    /// EVERY QUIP STILL CARRIES THE FACTS. The alternative design -- flavour text plus a separate
    /// factual line -- was rejected: it doubles the bubble length for no gain, and the facts are
    /// the reason the notice exists. So a quip is a template over the three things that were
    /// already in the old single line: which project, which tool, how long.
    ///
    /// PRIVACY IS INHERITED, NOT RE-EARNED. {project} is filled from
    /// <see cref="BlockedDetector.ShortProject"/>, which returns the LAST PATH SEGMENT only,
    /// because a working directory routinely carries a customer or an employer name and a speech
    /// bubble is on screen where anyone can read it. {tool} is a tool name. Neither is ever the
    /// command, and there is no placeholder that could carry one.
    ///
    /// NOT EVERY QUIP CAN ALWAYS BE USED. The project folder is unknown for some sessions and the
    /// tool name for others, so each quip declares what it needs and the picker only draws from
    /// the ones it can fill. Fourteen need neither, which is the floor: there is always a pool.
    /// </summary>
    internal sealed class Quip
    {
        public readonly string Text;
        public readonly bool NeedsProject;
        public readonly bool NeedsTool;

        public Quip(string text, bool needsProject, bool needsTool)
        {
            Text = text;
            NeedsProject = needsProject;
            NeedsTool = needsTool;
        }
    }

    internal sealed class QuipPicker
    {
        internal const string ProjectToken = "{project}";
        internal const string ToolToken = "{tool}";
        internal const string DurationToken = "{duration}";

        /// <summary>
        /// The pool. Order is not meaningful; the picker draws uniformly from what it can fill.
        ///
        /// Kept deliberately short per line. A speech bubble is not a paragraph, and the budget
        /// that decides WHETHER to speak already exists (NotifyBudget) -- this only decides what.
        /// </summary>
        private static readonly Quip[] All =
        {
            // -- need the project folder ------------------------------------------------
            new Quip("Your agent in {project} is waiting on you. {duration} now.", true, false),
            new Quip("Something in {project} wants a yes or a no.", true, false),
            new Quip("{project} has been paused {duration} waiting for you.", true, false),
            new Quip("There is a decision waiting in {project}.", true, false),
            new Quip("Pending: one prompt in {project}.", true, false),
            new Quip("{project} is stuck on a permission question.", true, false),
            new Quip("That is {duration} of nothing happening in {project}.", true, false),
            new Quip("{project} is waiting. No rush, but it is waiting.", true, false),
            new Quip("Something needs approving over in {project}.", true, false),
            new Quip("The cursor is blinking at a prompt in {project}.", true, false),
            new Quip("A question went unanswered {duration} ago in {project}.", true, false),
            new Quip("{project}: one pending approval.", true, false),
            new Quip("{duration} waiting. {project} would like an answer.", true, false),
            new Quip("There is a prompt with your name on it in {project}.", true, false),
            new Quip("Approval pending in {project}, {duration} and counting.", true, false),
            new Quip("Nothing is broken. Something is just waiting in {project}.", true, false),

            // -- need the project AND the tool -------------------------------------------
            new Quip("{tool} is holding the door open in {project}.", true, true),
            new Quip("Nudge: {project} needs an answer about {tool}.", true, true),
            new Quip("{tool} needs the go-ahead in {project}.", true, true),
            new Quip("Permission needed in {project}: {tool}.", true, true),

            // -- need the tool only --------------------------------------------------------
            new Quip("Your agent would like a word about {tool}.", false, true),
            new Quip("Held up on {tool} for {duration}.", false, true),
            new Quip("{tool} is parked, waiting on you.", false, true),

            // -- need neither. The floor: these are always available. ----------------------
            new Quip("Still waiting, {duration} in.", false, false),
            new Quip("There is a prompt sitting there. {duration}.", false, false),
            new Quip("Your agent stopped to ask permission, {duration} ago.", false, false),
            new Quip("A prompt is up and nobody has pressed anything.", false, false),
            new Quip("Your agent asked a question {duration} ago and is still holding.", false, false),
            new Quip("Waiting on a human. That would be you.", false, false),
            new Quip("Idle {duration}, and it is not the agent's fault.", false, false),
            new Quip("Your agent is being polite and waiting. {duration}.", false, false),
            new Quip("One prompt, unanswered, {duration} old.", false, false),
            new Quip("Your agent hit a gate {duration} ago.", false, false),
            new Quip("Your agent is waiting for a yes.", false, false),
            new Quip("Your agent paused for permission and has not moved since.", false, false),
            new Quip("Your agent asked. It is still asking.", false, false),
        };

        internal static int Count { get { return All.Length; } }

        private readonly Random _random;
        private string _last;

        /// <summary>Seeded on purpose, so a test can assert the picker rather than hope.</summary>
        public QuipPicker(int seed)
        {
            _random = new Random(seed);
        }

        public QuipPicker() : this(Environment.TickCount) { }

        /// <summary>
        /// One line, with everything filled in. Never returns the line it returned last time.
        ///
        /// The no-repeat rule is not politeness. The notify budget lets one notice through every
        /// couple of minutes, so consecutive draws are the ones a user actually hears back to
        /// back, and hearing the same sentence twice running is the whole difference between a pet
        /// and an alarm. With fourteen quips in the smallest pool it costs nothing.
        /// </summary>
        public string Next(string project, string tool, string duration)
        {
            bool hasProject = !string.IsNullOrEmpty(project);
            bool hasTool = !string.IsNullOrEmpty(tool);

            var eligible = new List<Quip>();
            foreach (Quip quip in All)
            {
                if (quip.NeedsProject && !hasProject) continue;
                if (quip.NeedsTool && !hasTool) continue;
                eligible.Add(quip);
            }
            if (eligible.Count == 0) return null;

            // Drop the previous line, unless doing so would leave nothing. The guard matters: a
            // pool can be one entry wide if the table is ever edited down, and an empty pool here
            // would mean silence rather than a repeat -- the wrong trade for a notice.
            var choices = new List<Quip>();
            foreach (Quip quip in eligible)
                if (!string.Equals(quip.Text, _last, StringComparison.Ordinal)) choices.Add(quip);
            if (choices.Count == 0) choices = eligible;

            Quip picked = choices[_random.Next(choices.Count)];
            _last = picked.Text;
            return Fill(picked.Text, project, tool, duration);
        }

        internal static string Fill(string text, string project, string tool, string duration)
        {
            if (text == null) return "";
            return text
                .Replace(ProjectToken, project ?? "")
                .Replace(ToolToken, tool ?? "")
                .Replace(DurationToken, duration ?? "");
        }

        /// <summary>
        /// Every quip, for the self-test. Exposed because the properties worth asserting are
        /// properties of the TABLE -- no quip may carry a placeholder it did not declare, and the
        /// unconditional pool must not be empty -- and neither is observable through Next().
        /// </summary>
        internal static IReadOnlyList<Quip> Table { get { return All; } }
    }
}
