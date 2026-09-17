using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>What the permission rules say would happen to a call.</summary>
    public enum RuleVerdict
    {
        /// <summary>No rule spoke, or an `ask` rule matched. Either way the agent prompts.</summary>
        WouldPrompt = 0,
        /// <summary>An `allow` rule matched every part. The agent runs it without asking.</summary>
        WouldAllow = 1,
        /// <summary>A `deny` rule matched. The agent refuses; the user is not asked.</summary>
        WouldDeny = 2,
        /// <summary>Nothing addressable to evaluate (no command, no path).</summary>
        Undecidable = 3,
    }

    /// <summary>
    /// Claude Code's permission-rule matching, as documented, ported from the sibling
    /// permission-wildcarding project (src/permission-match.js) where it is the shared
    /// implementation behind both the wildcarding pass and its dashboard.
    ///
    /// Two rules here are easy to miss, and BOTH are load-bearing on a real machine. Measured
    /// against this box's own settings (user + managed, 702 rules):
    ///
    ///   1. `Tool(cmd:*)` is an equivalent spelling of `Tool(cmd *)`. **86 rules use the colon
    ///      form.** A matcher that knows only the space form treats all 86 as literals, so they
    ///      match nothing at all and the matcher reports a covered family as uncovered. The
    ///      measurement harness in docs/agentflow shipped with exactly that bug and it inflated
    ///      would-prompt.
    ///   2. A trailing `*` preceded by a space ALSO matches the BARE command: `Bash(ls *)` matches
    ///      `ls`, and `Bash(git log *)` matches `git log`. That holds only while the trailing `*` is
    ///      the rule's ONLY wildcard, so a rule carrying a second `*` does not get the
    ///      bare-command allowance. One rule on this box is in that second category, which is
    ///      exactly few enough to have been missed by inspection.
    ///
    /// Evaluation order is deny then ask then allow, and nothing-matched also prompts. That order
    /// is why a managed `ask` beats a user `allow`: the ask matches first and the allow is never
    /// reached.
    /// </summary>
    public static class PermissionRules
    {
        private static readonly Regex RuleShape =
            new Regex(@"^([A-Za-z_][A-Za-z0-9_]*)\(([\s\S]*)\)$", RegexOptions.Compiled);

        // Both documented rules above are specific to COMMAND specifiers, where a space separates a
        // prefix from its arguments. Other tools use their own specifier grammar -- a Read(**/x)
        // path glob, or a Skill(name:args) whose colon is a field separator rather than a wildcard
        // suffix -- so applying command semantics there would be an inference, and being wrong
        // means treating a rule as covering something it does not.
        private static readonly HashSet<string> CommandTools =
            new HashSet<string>(StringComparer.Ordinal) { "Bash", "PowerShell" };

        // Keyed on the exact rule string and holding a pure function of it, so nothing here can go
        // stale. Bounded because a module lives for the whole app session: a real allow list plus a
        // managed policy is a few hundred distinct strings, so reaching the cap means a caller is
        // synthesizing rules in a loop, which is a bug rather than a workload. Eviction is a
        // wholesale clear, since an LRU's bookkeeping would cost more than the compile it saves.
        private const int MatchCacheLimit = 5000;
        private static readonly Dictionary<string, string> NormalizedRules =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Regex> CompiledRules =
            new Dictionary<string, Regex>(StringComparer.Ordinal);
        private static readonly object CacheLock = new object();

        private static string EscapeLiteral(string value)
        {
            var builder = new StringBuilder(value.Length + 8);
            foreach (char ch in value)
            {
                // Every regex metacharacter EXCEPT '*', which the caller rewrites to '.*'.
                if (".+?^${}()|[]\\".IndexOf(ch) >= 0) builder.Append('\\');
                builder.Append(ch);
            }
            return builder.ToString();
        }

        /// <summary>Rewrites the `:*` spelling to the ` *` spelling for a command tool.</summary>
        public static string NormalizeRule(string rule)
        {
            string text = rule ?? string.Empty;
            lock (CacheLock)
            {
                string hit;
                if (NormalizedRules.TryGetValue(text, out hit)) return hit;
            }
            string value = NormalizeRuleUncached(text);
            lock (CacheLock)
            {
                if (NormalizedRules.Count >= MatchCacheLimit) NormalizedRules.Clear();
                NormalizedRules[text] = value;
            }
            return value;
        }

        private static string NormalizeRuleUncached(string text)
        {
            Match parsed = RuleShape.Match(text);
            if (!parsed.Success) return text;
            string tool = parsed.Groups[1].Value;
            string inner = parsed.Groups[2].Value;
            if (!CommandTools.Contains(tool)) return text;
            if (!inner.EndsWith(":*", StringComparison.Ordinal)) return text;
            return tool + "(" + inner.Substring(0, inner.Length - 2) + " *)";
        }

        /// <summary>The regex source a rule compiles to. Exposed so a test can assert it.</summary>
        public static string RuleRegexSource(string rule)
        {
            string normalized = NormalizeRule(rule);
            Match parsed = RuleShape.Match(normalized);
            if (parsed.Success)
            {
                string tool = parsed.Groups[1].Value;
                string inner = parsed.Groups[2].Value;
                int wildcards = 0;
                foreach (char ch in inner) if (ch == '*') wildcards++;
                if (CommandTools.Contains(tool) && wildcards == 1
                    && inner.EndsWith(" *", StringComparison.Ordinal))
                {
                    // ` .*` optional, which is what lets the prefix alone match too.
                    return EscapeLiteral(tool) + "\\("
                           + EscapeLiteral(inner.Substring(0, inner.Length - 2)) + "( .*)?\\)";
                }
            }
            return EscapeLiteral(normalized).Replace("*", ".*");
        }

        private static Regex CompiledRule(string rule)
        {
            lock (CacheLock)
            {
                Regex hit;
                if (CompiledRules.TryGetValue(rule, out hit)) return hit;
            }
            Regex value;
            try
            {
                value = new Regex("^" + RuleRegexSource(rule) + "$");
            }
            catch (ArgumentException)
            {
                // Unreachable today: EscapeLiteral escapes every metacharacter except '*', and '*'
                // is always rewritten. Kept so that if the escaping ever narrows, the failure is
                // cached rather than thrown on every call. Do NOT write a test asserting a rule
                // "cannot compile" -- there is no such input, so the assertion would pass whether
                // or not this guard exists.
                value = null;
            }
            lock (CacheLock)
            {
                if (CompiledRules.Count >= MatchCacheLimit) CompiledRules.Clear();
                CompiledRules[rule] = value;
            }
            return value;
        }

        /// <summary>Does <paramref name="rule"/> cover <paramref name="permission"/>?</summary>
        public static bool RuleMatches(string rule, string permission)
        {
            if (rule == null || permission == null) return false;
            string target = NormalizeRule(permission);
            if (NormalizeRule(rule) == target) return true;
            Regex pattern = CompiledRule(rule);
            return pattern != null && pattern.IsMatch(target);
        }

        /// <summary>
        /// Evaluate one concrete permission string (`Bash(git status)`) against a rule set.
        /// Order is deny, then ask, then allow; nothing matched prompts.
        /// </summary>
        public static RuleVerdict Evaluate(string permission, RuleSet rules)
        {
            if (rules == null) return RuleVerdict.WouldPrompt;
            foreach (string rule in rules.Deny)
                if (RuleMatches(rule, permission)) return RuleVerdict.WouldDeny;
            foreach (string rule in rules.Ask)
                if (RuleMatches(rule, permission)) return RuleVerdict.WouldPrompt;
            foreach (string rule in rules.Allow)
                if (RuleMatches(rule, permission)) return RuleVerdict.WouldAllow;
            return RuleVerdict.WouldPrompt;
        }

        /// <summary>
        /// Evaluate a whole tool call. For a command tool every top-level segment is evaluated
        /// separately and the MOST RESTRICTIVE answer wins, because that is what the agent does: a
        /// chain runs unprompted only when every part is allowed.
        /// </summary>
        public static RuleVerdict EvaluateCall(string tool, string command, RuleSet rules)
        {
            if (string.IsNullOrEmpty(tool)) return RuleVerdict.Undecidable;
            if (tool != "Bash" && tool != "PowerShell")
                return Evaluate(tool + "(" + (command ?? string.Empty) + ")", rules);
            if (string.IsNullOrEmpty(command) || command.Trim().Length == 0)
                return RuleVerdict.Undecidable;

            List<string> parts = CommandSplitter.Split(
                command, tool == "PowerShell" ? CommandSplitter.ShellPowerShell
                                              : CommandSplitter.ShellBash);
            if (parts.Count == 0) return RuleVerdict.Undecidable;

            bool sawDeny = false, sawPrompt = false, sawAllow = false;
            foreach (string part in parts)
            {
                string stripped = StripEnvPrefix(part);
                if (stripped.Length == 0) continue;
                switch (Evaluate(tool + "(" + stripped + ")", rules))
                {
                    case RuleVerdict.WouldDeny: sawDeny = true; break;
                    case RuleVerdict.WouldPrompt: sawPrompt = true; break;
                    case RuleVerdict.WouldAllow: sawAllow = true; break;
                }
            }
            if (sawDeny) return RuleVerdict.WouldDeny;
            if (sawPrompt) return RuleVerdict.WouldPrompt;
            if (sawAllow) return RuleVerdict.WouldAllow;
            return RuleVerdict.Undecidable;
        }

        private static readonly Regex EnvPrefix =
            new Regex(@"^(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)+", RegexOptions.Compiled);

        /// <summary>A leading `VAR=value` prefix is stripped before matching, as the agent does.</summary>
        public static string StripEnvPrefix(string command)
        {
            if (string.IsNullOrEmpty(command)) return string.Empty;
            return EnvPrefix.Replace(command.Trim(), string.Empty).Trim();
        }

        /// <summary>Introspection, so the cache bound can be asserted rather than assumed.</summary>
        public static void CacheStats(out int normalized, out int compiled, out int limit)
        {
            lock (CacheLock)
            {
                normalized = NormalizedRules.Count;
                compiled = CompiledRules.Count;
            }
            limit = MatchCacheLimit;
        }
    }

    /// <summary>The three rule tiers, already merged across the user and managed settings files.</summary>
    public sealed class RuleSet
    {
        public List<string> Deny = new List<string>();
        public List<string> Ask = new List<string>();
        public List<string> Allow = new List<string>();

        public int Count { get { return Deny.Count + Ask.Count + Allow.Count; } }
    }
}
