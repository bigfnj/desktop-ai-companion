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
    }

    /// <summary>The decision about one prompt: which row to press, or why not to touch it.</summary>
    public sealed class PromptDecision
    {
        /// <summary>Zero-based row to press, or -1 to press nothing.</summary>
        public int Index = -1;
        /// <summary>Why, written so it can say the run REFUSED rather than reporting success by omission.</summary>
        public string Reason;
        /// <summary>The option text that was chosen. Safe to log: it is one of the table's strings.</summary>
        public string Chosen;
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

            // -- wider than one call ---------------------------------------------------
            Entry("yes, allow all edits this session", OptionKind.ApproveWider),
            Entry("yes, and don't ask again", OptionKind.ApproveWider),

            // -- changes the permission mode -------------------------------------------
            Entry("yes, and auto-accept", OptionKind.ModeChange),
            Entry("yes, and manually approve edits", OptionKind.ModeChange),
            Entry("yes, return to normal mode", OptionKind.ModeChange),
            Entry("yes, set auto mode as my default", OptionKind.ModeChange),

            // -- decline ---------------------------------------------------------------
            Entry("no", OptionKind.Reject),
            Entry("no, keep ", OptionKind.Reject),
            Entry("no, keep planning", OptionKind.Reject),
            Entry("deny", OptionKind.Reject),
            Entry("reject", OptionKind.Reject),
            Entry("cancel", OptionKind.Reject),

            // -- type something instead ------------------------------------------------
            Entry("other", OptionKind.FreeText),
            Entry("no, and tell claude what to do differently", OptionKind.FreeText),
            Entry("no, and tell claude ", OptionKind.FreeText),
        };

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
            return value.ToLowerInvariant();
        }

        /// <summary>The class of one option, and the table entry that matched (null when none did).</summary>
        public static OptionKind Classify(string text, out string matched)
        {
            matched = null;
            string observed = Normalize(text);
            if (observed.Length == 0) return OptionKind.Unknown;

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
        public static PromptDecision Choose(IList<string> options)
        {
            var decision = new PromptDecision();
            if (options == null || options.Count == 0)
            {
                decision.Reason = "refused: no options were read off the prompt";
                return decision;
            }

            var kinds = new OptionKind[options.Count];
            var unknown = new List<string>();
            var approvals = new List<int>();
            int widerOrMode = 0;
            for (int i = 0; i < options.Count; i++)
            {
                string matched;
                kinds[i] = Classify(options[i], out matched);
                switch (kinds[i])
                {
                    case OptionKind.Unknown: unknown.Add(options[i] ?? ""); break;
                    case OptionKind.ApproveOnce: approvals.Add(i); break;
                    case OptionKind.ApproveWider:
                    case OptionKind.ModeChange: widerOrMode++; break;
                }
            }

            if (unknown.Count > 0)
            {
                var shown = new List<string>();
                for (int i = 0; i < unknown.Count && i < 3; i++) shown.Add(Quote(unknown[i]));
                decision.Reason = string.Format(CultureInfo.InvariantCulture,
                    "refused: {0} of {1} options unrecognised ({2}) -- either the capture misread "
                    + "the prompt or the agent shipped a new option; not pressing anything",
                    unknown.Count, options.Count, string.Join("; ", shown.ToArray()));
                return decision;
            }
            if (approvals.Count == 0)
            {
                decision.Reason = "refused: no approve-once option present on a prompt of "
                                  + options.Count.ToString(CultureInfo.InvariantCulture)
                                  + " recognised options";
                return decision;
            }
            if (approvals.Count > 1)
            {
                decision.Reason = string.Format(CultureInfo.InvariantCulture,
                    "refused: {0} approve-once options -- ambiguous, a prompt should offer exactly one",
                    approvals.Count);
                return decision;
            }

            decision.Index = approvals[0];
            decision.Chosen = options[approvals[0]];
            decision.Reason = string.Format(CultureInfo.InvariantCulture,
                "pressing option {0} {1} (approve-once); declined {2} wider or mode option(s)",
                decision.Index + 1, Quote(decision.Chosen), widerOrMode);
            return decision;
        }

        private static string Quote(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }
    }
}
