using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>What pressing an option on a permission prompt would do.</summary>
    public enum OptionKind
    {
        /// <summary>Not in the table. One of these refuses the whole prompt.</summary>
        Unknown = 0,
        /// <summary>Approves THIS call and nothing else. The only class this module presses.</summary>
        ApproveOnce = 1,
        /// <summary>A session-wide or permanent grant. Never pressed.</summary>
        ApproveWider = 2,
        /// <summary>Alters the permission mode, sometimes persistently. Never pressed.</summary>
        ModeChange = 3,
        /// <summary>Declines the call.</summary>
        Reject = 4,
        /// <summary>"Other" / tell the agent something instead.</summary>
        FreeText = 5,
        /// <summary>
        /// "Yes, allow &lt;rules&gt; for all projects" -- a rule written to the USER settings,
        /// so it outlives the session and applies in every repo. The widest grant on offer.
        ///
        /// Its own kind rather than ApproveWider because the user can now opt into pressing
        /// exactly this one, and nothing else wider. Lumping it in with the session-scoped
        /// grants would make that choice impossible to express.
        /// </summary>
        ApproveAllProjects = 6,
        /// <summary>
        /// Codex's "Allow similar commands" -- a standing grant for commands it judges
        /// alike, narrower than all-projects and wider than one call.
        ///
        /// Its own kind for the same reason ApproveAllProjects is: the user can opt into
        /// exactly this and nothing else wider. Folding it into ApproveWider would make that
        /// choice impossible to express, and folding it into ApproveOnce would press a
        /// standing grant while claiming to approve one call.
        ///
        /// What "similar" MEANS is Codex's judgement, not ours, and that is the reason it is
        /// off by default: this module cannot state the blast radius of a grant whose scope is
        /// decided by the other agent.
        /// </summary>
        ApproveSimilar = 7,
    }

    /// <summary>
    /// Why a prompt was not pressed, so a caller can tell a FAULT from a prompt that simply
    /// is not this module's to answer.
    ///
    /// Added because those two were indistinguishable in the only thing the caller had: a
    /// reason STRING, all of which began "refused:". A plan prompt offers two mode changes
    /// and a decline, so it has no approve-once row and never will -- which is correct
    /// behaviour, not a malfunction, and describing it with the same word as "the capture
    /// misread the screen" sends the reader hunting a bug that is not there.
    /// </summary>
    public enum RefusalKind
    {
        /// <summary>A row was chosen. Nothing was refused.</summary>
        None = 0,
        /// <summary>Nothing was read off the prompt at all.</summary>
        NoOptions,
        /// <summary>At least one option nobody recognised. A FAULT: either the capture
        /// misread the screen or the agent shipped something new.</summary>
        Unrecognised,
        /// <summary>Every option was recognised and none of them approves one call. NOT a
        /// fault. The plan prompt is the ordinary example and there will be others.</summary>
        NothingToPress,
        /// <summary>More than one approve-once row, so an assumption about someone else's
        /// UI is wrong and the safe answer is to press nothing.</summary>
        Ambiguous,
    }

    /// <summary>The decision about one prompt: which row to press, or why not to touch it.</summary>
    public sealed class PromptDecision
    {
        /// <summary>Why nothing was pressed, as a value rather than as prose. Callers branch
        /// on this; <see cref="Reason"/> is for the log and is not a protocol.</summary>
        public RefusalKind Refusal = RefusalKind.None;

        /// <summary>Zero-based row to press, or -1 to press nothing.</summary>
        public int Index = -1;
        /// <summary>Why, written so it can say the run REFUSED rather than reporting success by omission.</summary>
        public string Reason;
        /// <summary>
        /// The TABLE ENTRY the chosen row matched -- "yes", "yes, allow ", and so on. Safe to
        /// log, because it comes from this file rather than from the screen.
        ///
        /// This used to hold the raw label, under a comment claiming the same safety, and the
        /// claim was false for the three prefix templates: "yes, allow " matches a row reading
        /// "Yes, allow Bash(npm test)", so the logged string carried the command. The
        /// diagnostic log is meant to be attachable to a public issue.
        /// </summary>
        public string Chosen;
        /// <summary>
        /// The row's text exactly as it was read off the screen. NEVER LOG THIS. It exists
        /// for one job: the click re-checks that the row still reads what it read, so the
        /// text has to travel with the index.
        /// </summary>
        public string ChosenRaw;
        /// <summary>
        /// Unrecognised option text, for showing the USER on screen when they ask why nothing
        /// was pressed. NEVER LOG THIS either -- an option nobody anticipated can say
        /// anything. Named so that logging it has to be a decision rather than an accident.
        /// </summary>
        public string UnsafeDetail;
        public bool WillPress { get { return Index >= 0; } }
    }

    /// <summary>
    /// Classifies the options on a coding agent's permission prompt, and picks the one row that
    /// approves exactly the call in front of it.
    ///
    /// THIS IS THE SAFETY MECHANISM, not a convenience. The Claude Code webview bundle ships TEN
    /// distinct strings beginning with "Yes", and only three of them mean "approve this one call":
    ///
    ///   approve this call   Yes | Yes, allow &lt;x&gt; | Yes, allow access to &lt;host&gt;
    ///   wider grant         Yes, allow all edits this session | Yes, and don't ask again
    ///   mode change         Yes, and auto-accept | Yes, and manually approve edits
    ///                       Yes, return to normal mode | Yes, set auto mode as my default
    ///
    /// So "just send Yes" is not implementable safely, and a prefix match would eventually press
    /// "set auto mode as my default". All four public tools that implement this were read at source
    /// level on 2026-09-16 and every one presses a wider grant than its README advertises:
    /// auto-accept-agent takes a bare `allow`, llm-auto-confirm does a blind Enter on the cursor
    /// row, antigravity-auto-accept calls agentAcceptAllInFile, and domyh puts AcceptAll at
    /// priority 1. That is the failure this class exists to not repeat.
    ///
    /// Three rules, each because the obvious implementation is wrong:
    ///
    ///   1. ALLOWLIST, NEVER DENYLIST. An unrecognised option refuses. A denylist fails OPEN the
    ///      day a new "Yes, ..." variant ships, and this bundle auto-updates underneath us.
    ///   2. EXACT MATCH for complete strings, PREFIX match only for templates. An entry ending in a
    ///      space is a template with a runtime value appended. Longest match wins.
    ///   3. ONE UNKNOWN OPTION POISONS THE WHOLE PROMPT. Either the capture misread the screen or
    ///      the bundle changed. Both mean do not touch it.
    ///
    /// The permission MODE is never ours to change: <see cref="Choose"/> only ever returns an
    /// approve-once row, and that is asserted and mutation-tested rather than left as an implicit
    /// consequence of the filter.
    ///
    /// Ported from docs/agentflow/agentflow_classifier.py, which carries the transcription of the
    /// bundle strings and an --audit mode that re-derives them from whatever is installed. The
    /// table below is verified against 2.1.274; it was transcribed from 2.1.273 and audited clean
    /// four releases later, which is what justifies maintaining it by hand.
    /// </summary>
    public static class PromptOptions
    {
        // A trailing space marks a TEMPLATE: the agent appends a runtime value ("Yes, allow access
        // to example.com"). Everything else must match exactly.
        private static readonly KeyValuePair<string, OptionKind>[] Known =
        {
            // -- approve this one call -------------------------------------------------
            Entry("yes", OptionKind.ApproveOnce),
            Entry("yes, allow ", OptionKind.ApproveOnce),
            Entry("yes, allow access to ", OptionKind.ApproveOnce),
            Entry("yes, allow access to", OptionKind.ApproveOnce),
            Entry("allow", OptionKind.ApproveOnce),
            // Codex. "allow once" is its own exact entry rather than relying on the bare
            // "allow" above, which is an EXACT entry and does not match it.
            Entry("allow once", OptionKind.ApproveOnce),

            // -- Codex: wider than one call, narrower than a project ------------------
            Entry("allow similar commands", OptionKind.ApproveSimilar),

            // -- wider than one call ---------------------------------------------------
            Entry("yes, allow all edits this session", OptionKind.ApproveWider),
            Entry("yes, and don't ask again", OptionKind.ApproveWider),

            // -- changes the permission mode -------------------------------------------
            Entry("yes, and auto-accept", OptionKind.ModeChange),
            // The OTHER branch of the same ternary, shipped in 2.1.280 and found by
            // agentflow_classifier.py --audit on the newer bundle:
            //     _0 = G ? (q === "auto" ? "Yes, and use auto mode" : "Yes, and auto-accept")
            // Until it was listed, every plan prompt in an auto-configured session offered
            // an option nothing recognised -- and one unknown option refuses the WHOLE
            // prompt. That changed the REASON given, not the outcome: this prompt's rows
            // are two mode changes and a decline, so there was never an approve-once row
            // to press. The cost was a log line blaming the screen capture for a prompt
            // that was simply not this module's to answer. Found by the audit, which is
            // what the audit is for.
            Entry("yes, and use auto mode", OptionKind.ModeChange),
            Entry("yes, and manually approve edits", OptionKind.ModeChange),
            Entry("yes, return to normal mode", OptionKind.ModeChange),
            Entry("yes, set auto mode as my default", OptionKind.ModeChange),

            // -- decline ---------------------------------------------------------------
            Entry("no", OptionKind.Reject),
            Entry("no, keep ", OptionKind.Reject),
            Entry("no, keep planning", OptionKind.Reject),
            // The reject row of the plan prompt when feedback has been typed -- the other
            // branch of `m5 = H ? "Send feedback and keep planning" : "No, keep planning"`.
            // Declines the plan, so Reject: it is the button paired with the approve row,
            // not the free-text escape hatch, which on this prompt is the textarea.
            Entry("send feedback and keep planning", OptionKind.Reject),
            Entry("deny", OptionKind.Reject),
            Entry("reject", OptionKind.Reject),
            Entry("cancel", OptionKind.Reject),

            // DELIBERATELY ABSENT: "Submit answers".
            //
            // It is a real button -- the approve row of the question prompt, `_0` when the
            // prompt is an AskUserQuestion -- and leaving it out is a decision, not an
            // oversight. Pressing it would submit whatever answers happen to be selected,
            // which is answering a question ON THE USER'S BEHALF rather than approving a
            // call they already asked for. No kind in this enum means "recognised, and
            // never ours to press", so the honest way to express that today is to leave it
            // Unknown and let the whole prompt refuse -- which is the behaviour we want.
            //
            // The cost is the wording: the refusal blames the capture or a new option,
            // when in fact the option is known and the answer is no. A NeverPress kind
            // would fix the sentence without changing a single press.
            // agentflow_classifier.py records the same decision so its audit stays green
            // for a stated reason rather than by accident.

            // -- type something instead ------------------------------------------------
            Entry("other", OptionKind.FreeText),
            Entry("no, and tell claude what to do differently", OptionKind.FreeText),
            Entry("no, and tell claude ", OptionKind.FreeText),
        };

        /// <summary>
        /// Where a "Yes, allow ..." option writes its rule, verbatim from the agent's own
        /// destination table (webview bundle 2.1.278):
        ///
        ///   localSettings   "this project (just you)"
        ///   userSettings    "all projects"
        ///   projectSettings "this project (shared)"
        ///   session         "this session"
        ///   cliArg          "startup options"
        ///
        /// The label is COMPOSED at runtime -- "Yes, allow " + the rules + " for " + one of
        /// these -- so none of the finished strings appears as a literal anywhere to be
        /// transcribed. That is why the table missed them: "yes, allow " is a template entry
        /// classed ApproveOnce, and every one of these rendered options starts with it.
        ///
        /// The cost was not theoretical. A real bash prompt offered "Yes" AND "Yes, allow
        /// python -c ... for all projects"; both matched the approve-once template, so the
        /// module saw two approve-once rows, called the prompt ambiguous and refused it.
        /// Every bash prompt behaved that way, which is most of them.
        /// </summary>
        private static readonly string[] RuleDestinations =
        {
            " for all projects",
            " for this project (just you)",
            " for this project (shared)",
            " for this session",
            " for startup options",
        };

        private const string AllProjectsSuffix = " for all projects";

        /// <summary>
        /// The rule-writing class of an option, or Unknown when it does not write one.
        ///
        /// Checked BEFORE the table, because the table would answer ApproveOnce for all of
        /// these and be wrong. A suffix is the right anchor: the middle of the string is the
        /// rule list, which is arbitrary user text and can contain anything at all.
        /// </summary>
        private static OptionKind RuleGrantKind(string normalized)
        {
            if (!normalized.StartsWith("yes, ", StringComparison.Ordinal))
                return OptionKind.Unknown;
            foreach (string destination in RuleDestinations)
            {
                if (!normalized.EndsWith(destination, StringComparison.Ordinal)) continue;
                return destination == AllProjectsSuffix
                    ? OptionKind.ApproveAllProjects
                    : OptionKind.ApproveWider;
            }
            return OptionKind.Unknown;
        }

        private static KeyValuePair<string, OptionKind> Entry(string text, OptionKind kind)
        {
            return new KeyValuePair<string, OptionKind>(text, kind);
        }

        /// <summary>A leading "1. " / "2) " / "> " is chrome the TUI draws, not part of the option.</summary>
        private static readonly Regex LeadingChrome =
            new Regex(@"^\s*(?:[>❯▶*\-]\s*)?(?:\(?\d{1,2}[.)\]]\s*)?", RegexOptions.Compiled);

        private static readonly Regex CollapseWhitespace = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Casefold, strip list chrome, collapse whitespace, unify the quote family.
        ///
        /// The quote fold is not cosmetic. A capture renders an apostrophe as U+2019 about as often
        /// as U+0027, and "don't ask again" is the exact string where that decides whether a
        /// PERMANENT GRANT is recognised or silently becomes Unknown. Normalizing to FormKC first
        /// does not help, because it leaves U+2019 alone, so the family is folded by hand.
        /// </summary>
        /// <summary>Keyboard glyphs Codex renders inside an option label: return, and the two
        /// arrow forms of it.</summary>
        private static readonly char[] HintGlyphs = { '\u23CE', '\u21B5', '\u2B90' };

        /// <summary>Trailing word hints, as whole tokens after a space.</summary>
        private static readonly string[] HintWords = { "esc", "enter", "return" };

        /// <summary>
        /// Remove a trailing keyboard hint, which Codex renders INSIDE the label rather than
        /// beside it: "Allow once \u23CE", "Deny Esc".
        ///
        /// Without this every Codex row classifies Unknown, and one unknown option refuses the
        /// whole prompt, so the agent would never be actionable at all.
        ///
        /// Deliberately narrow rather than "trim anything non-alphabetic": this is an allowlist
        /// file and the normaliser carries the same obligation as the table. A wide trim here
        /// would quietly widen every entry in it -- exactly the prefix-match failure the class
        /// comment above exists to warn about.
        /// </summary>
        internal static string StripKeyboardHint(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            string current = value.TrimEnd();
            for (int guard = 0; guard < 4; guard++)
            {
                string before = current;
                while (current.Length > 0 &&
                       Array.IndexOf(HintGlyphs, current[current.Length - 1]) >= 0)
                    current = current.Substring(0, current.Length - 1).TrimEnd();
                foreach (string word in HintWords)
                {
                    string suffix = " " + word;
                    if (current.Length > suffix.Length &&
                        current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        current = current.Substring(0, current.Length - suffix.Length).TrimEnd();
                        break;
                    }
                }
                if (string.Equals(current, before, StringComparison.Ordinal)) break;
            }
            return current;
        }

        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string value = text.Normalize(NormalizationForm.FormKC);
            value = value.Replace('’', '\'').Replace('ʼ', '\'').Replace('`', '\'');
            value = LeadingChrome.Replace(value, "");
            value = CollapseWhitespace.Replace(value, " ").Trim();
            // A trailing ellipsis is decoration. A trailing PERIOD is not stripped: no table entry
            // ends in one, and stripping it would widen the match surface for no gain.
            value = value.TrimEnd('…').TrimEnd();
            if (value.EndsWith("...", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 3).TrimEnd();
            value = StripKeyboardHint(value);
            return value.ToLowerInvariant();
        }

        /// <summary>The class of one option, and the table entry that matched (null when none did).</summary>
        public static OptionKind Classify(string text, out string matched)
        {
            matched = null;
            string observed = Normalize(text);
            if (observed.Length == 0) return OptionKind.Unknown;

            // Destination first. Every one of these also matches the "yes, allow " template,
            // and the template's answer is wrong for all of them.
            OptionKind rule = RuleGrantKind(observed);
            if (rule != OptionKind.Unknown)
            {
                matched = observed.EndsWith(AllProjectsSuffix, StringComparison.Ordinal)
                    ? AllProjectsSuffix.Trim()
                    : "a saved rule";
                return rule;
            }

            string bestEntry = null;
            OptionKind bestKind = OptionKind.Unknown;
            int bestLength = -1;
            foreach (KeyValuePair<string, OptionKind> pair in Known)
            {
                string entry = pair.Key;
                bool isTemplate = entry.EndsWith(" ", StringComparison.Ordinal);
                bool hit;
                if (isTemplate)
                {
                    // A rendered template is "<entry><runtime value>". Require something after the
                    // prefix: a bare "yes, allow" with nothing appended did not come from this
                    // template and must fall through to the exact entry.
                    hit = observed.StartsWith(entry, StringComparison.Ordinal)
                          && observed.Length > entry.Length;
                }
                else
                {
                    hit = string.Equals(observed, entry, StringComparison.Ordinal);
                }
                if (hit && entry.Length > bestLength)
                {
                    bestEntry = entry;
                    bestKind = pair.Value;
                    bestLength = entry.Length;
                }
            }
            if (bestEntry == null) return OptionKind.Unknown;
            matched = bestEntry;
            return bestKind;
        }

        /// <summary>
        /// Pick the row to press, or refuse with a reason that names the deciding condition.
        ///
        /// Never returns a wider grant or a mode change, and never guesses. A prompt offering no
        /// approve-once row, or more than one, is refused rather than resolved: "a prompt should
        /// offer exactly one" is an assumption about the agent's UI, and the safe response to it
        /// being wrong is to do nothing.
        /// </summary>
        /// <summary>
        /// As Choose(options), pressing only the one-call row. Kept so every existing caller
        /// and assertion keeps its meaning without being rewritten.
        /// </summary>
        public static PromptDecision Choose(IList<string> options)
        {
            return Choose(options, false);
        }

        /// <summary>
        /// Pick the row to press, or refuse with a reason that names the deciding condition.
        ///
        /// <paramref name="preferAllProjects"/> is the user's explicit choice, off unless they
        /// turn it on, and it is the ONLY way this function will ever return a row that writes
        /// a permission rule. It presses "Yes, allow ... for all projects" -- a rule saved to
        /// the user settings, which outlives the session and applies in every repository.
        ///
        /// It is deliberately narrow. It does not unlock the other four destinations, nor
        /// "don't ask again", nor anything that changes the permission mode; those stay
        /// unpressable whatever the setting says. The maintainer asked for this one, having
        /// been shown what it writes, and nothing else came with it.
        /// </summary>
        public static PromptDecision Choose(IList<string> options, bool preferAllProjects)
        {
            return Choose(options, preferAllProjects, false);
        }

        /// <summary>
        /// <paramref name="preferSimilar"/> is Codex's equivalent of preferAllProjects: the
        /// user opting into the wider row on purpose. Same shape, same guards, and the same
        /// refusal when the prompt offers more than one of them.
        /// </summary>
        public static PromptDecision Choose(IList<string> options, bool preferAllProjects,
                                            bool preferSimilar)
        {
            var decision = new PromptDecision();
            if (options == null || options.Count == 0)
            {
                decision.Refusal = RefusalKind.NoOptions;
                decision.Reason = "refused: no options were read off the prompt";
                return decision;
            }

            var kinds = new OptionKind[options.Count];
            var matchedKeys = new string[options.Count];
            var unknown = new List<string>();
            var approvals = new List<int>();
            var allProjects = new List<int>();
            var similar = new List<int>();
            int widerOrMode = 0;
            for (int i = 0; i < options.Count; i++)
            {
                string matched;
                kinds[i] = Classify(options[i], out matched);
                matchedKeys[i] = matched;
                switch (kinds[i])
                {
                    case OptionKind.Unknown: unknown.Add(options[i] ?? ""); break;
                    case OptionKind.ApproveOnce: approvals.Add(i); break;
                    case OptionKind.ApproveAllProjects: allProjects.Add(i); widerOrMode++; break;
                    case OptionKind.ApproveSimilar: similar.Add(i); widerOrMode++; break;
                    case OptionKind.ApproveWider:
                    case OptionKind.ModeChange: widerOrMode++; break;
                }
            }

            if (unknown.Count > 0)
            {
                var shown = new List<string>();
                for (int i = 0; i < unknown.Count && i < 3; i++) shown.Add(Quote(unknown[i]));
                // The text goes to UnsafeDetail, not into Reason. Reason is the line that gets
                // logged, and an option nobody anticipated is exactly the string least safe to
                // put in a file meant to be attachable to a public issue.
                decision.UnsafeDetail = string.Join("; ", shown.ToArray());
                decision.Refusal = RefusalKind.Unrecognised;
                decision.Reason = string.Format(CultureInfo.InvariantCulture,
                    "refused: {0} of {1} options unrecognised -- either the capture misread "
                    + "the prompt or the agent shipped a new option; not pressing anything",
                    unknown.Count, options.Count);
                return decision;
            }
            // The user's row, when they asked for it and the prompt offers exactly one. More
            // than one is refused for the same reason two approve-once rows are: "there is
            // exactly one" is an assumption about someone else's UI, and the safe answer to
            // it being wrong is to press nothing.
            //
            // ABOVE the approve-once guards, not below them. Below, a prompt offering the
            // all-projects row and NO plain "Yes" was refused for want of an approve-once row
            // that the user had explicitly said they did not need -- and worse, the assertion
            // meant to prove the setting does not leak to the OTHER destinations passed because
            // of that guard rather than because of the destination check. The mutation that
            // makes every destination all-projects survived, which is how it was found.
            if (preferAllProjects && allProjects.Count == 1)
            {
                decision.Index = allProjects[0];
                decision.ChosenRaw = options[allProjects[0]];
                decision.Chosen = matchedKeys[allProjects[0]];
                decision.Reason = string.Format(CultureInfo.InvariantCulture,
                    "pressing option {0}, which SAVES A RULE FOR ALL PROJECTS (you asked for "
                    + "this); declined the one-call row",
                    decision.Index + 1);
                return decision;
            }

            // Codex's wider row, on the same terms as the all-projects one above: only when
            // asked for, only when the prompt offers exactly one, and ABOVE the approve-once
            // guards so a prompt that offers the wider row and no narrow one is still
            // actionable for a user who said that is what they wanted.
            if (preferSimilar && similar.Count == 1)
            {
                decision.Index = similar[0];
                decision.ChosenRaw = options[similar[0]];
                decision.Chosen = matchedKeys[similar[0]];
                decision.Reason = string.Format(CultureInfo.InvariantCulture,
                    "pressing option {0}, which GRANTS SIMILAR COMMANDS for this session "
                    + "(you asked for this); declined the one-call row",
                    decision.Index + 1);
                return decision;
            }

            if (approvals.Count == 0)
            {
                // Recognised, and none of it ours. See RefusalKind.NothingToPress.
                decision.Refusal = RefusalKind.NothingToPress;
                decision.Reason = "refused: no approve-once option present on a prompt of "
                                  + options.Count.ToString(CultureInfo.InvariantCulture)
                                  + " recognised options";
                return decision;
            }
            if (approvals.Count > 1)
            {
                decision.Refusal = RefusalKind.Ambiguous;
                decision.Reason = string.Format(CultureInfo.InvariantCulture,
                    "refused: {0} approve-once options -- ambiguous, a prompt should offer exactly one",
                    approvals.Count);
                return decision;
            }


            decision.Index = approvals[0];
            decision.ChosenRaw = options[approvals[0]];
            decision.Chosen = matchedKeys[approvals[0]];
            decision.Reason = string.Format(CultureInfo.InvariantCulture,
                "pressing option {0}, recognised as {1} (approve-once); declined {2} wider or "
                + "mode option(s)",
                decision.Index + 1, Quote(decision.Chosen), widerOrMode);
            return decision;
        }

        private static string Quote(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }
    }
}
