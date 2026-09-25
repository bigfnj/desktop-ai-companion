using System;
using System.Collections.Generic;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>Why the detector did or did not call a session blocked.</summary>
    public enum DetectionOutcome
    {
        /// <summary>Nothing outstanding; the agent is working or finished.</summary>
        Idle = 0,
        /// <summary>Outstanding, but not stalled long enough yet.</summary>
        Working = 1,
        /// <summary>Stalled, but the rules say this call would not have prompted.</summary>
        StalledButAllowed = 2,
        /// <summary>Stalled AND the rules say it would prompt. This is a notification.</summary>
        Blocked = 3,
        /// <summary>Not `default` mode: the rules do not predict prompts usefully, so stand down.</summary>
        StoodDownAutoMode = 4,
        /// <summary>The transcript parsed but held no tool calls at all -- adapter may be stale.</summary>
        AdapterSuspect = 5,
        /// <summary>Stalled, but the call carries nothing the permission rules can address.</summary>
        NotDecidable = 6,
    }

    /// <summary>One detector verdict about one session.</summary>
    public sealed class Detection
    {
        public AgentSession Session;
        public DetectionOutcome Outcome;
        public OutstandingCall Call;      // the stalled call, when there is one
        public double IdleSeconds;
        public RuleVerdict Verdict;
        public string Reason;             // short, loggable, never carries command text

        /// <summary>
        /// What the outcome WOULD have been if the mode gate had not stood down, and `Idle` when
        /// the gate never fired. Nothing in the notify path may read this: the stand-down governs
        /// unsolicited speech and `Outcome` is the only thing that decides whether the companion
        /// says anything.
        ///
        /// It exists so a user who PRESSES "Check now" gets a real answer instead of "2 sessions in
        /// auto mode", which is unfalsifiable from the outside. Asking is not being interrupted, so
        /// it does not need the precision an interruption needs.
        /// </summary>
        public DetectionOutcome WouldHaveBeen;

        /// <summary>The tool name, safe to speak and log. Never the command.</summary>
        public string ToolName { get { return Call != null ? Call.Tool : null; } }
    }

    /// <summary>
    /// Decides whether an agent is sitting blocked on a permission prompt.
    ///
    /// TWO SIGNALS, AND NEITHER WORKS ALONE. Both facts below are measured, not assumed, and they
    /// are what shaped this class:
    ///
    ///   1. A stall threshold on its own is NOT a detector. Over 27,967 paired calls, real prompts
    ///      waited a median 86.2s against 1.6s for ordinary completions -- yet a 20s threshold
    ///      still produced roughly 450 false alarms per real prompt, and missed half of them
    ///      anyway, because a slow build and a human-blocked call look identical in a transcript.
    ///   2. The permission-rule join answers a second, independent question about the same call:
    ///      would this have prompted at all? Measured recall against calls a rule actually blocked
    ///      is 93% (28/30), which is the axis that matters -- a miss means the companion stays
    ///      silent while the agent sits there.
    ///
    /// So the detector requires BOTH: outstanding, stalled past the threshold, and predicted to
    /// prompt. Either alone is unusable.
    ///
    /// AND IT STANDS DOWN IN AUTO MODE. In auto mode a model-side classifier sits in front of the
    /// rules, and precision collapses to about 0.4% -- roughly 250 false alarms per real prompt.
    /// Note what this does NOT claim: rules still cause prompts in auto mode (23 of 30 measured
    /// rule-caused denials were there) and the matcher still catches 91% of them. What fails is
    /// precision, not recall. Firing anyway would make the companion a liar 249 times out of 250,
    /// so it says so once and goes quiet -- the same idiom as refusing and explaining rather than
    /// quietly delivering something weaker.
    /// </summary>
    public static class BlockedDetector
    {
        /// <summary>Default stall threshold. Below the measured 86.2s median so a real prompt is
        /// caught well before a human would give up, above the 20.3s p90 of ordinary completions.</summary>
        public const double DefaultThresholdSeconds = 30.0;

        /// <summary>
        /// The ONLY mode in which the rule join is worth acting on.
        ///
        /// Keyed on `default` rather than on a list of modes to stand down in, and that is the
        /// correction of a real defect: this used to stand down for `auto` alone, so a session in
        /// `acceptEdits` fired. Measured precision by mode over 120 transcripts says they are the
        /// same population:
        ///
        ///   auto        6197 predictions, 23 real   0.37%
        ///   acceptEdits 1448 predictions,  6 real   0.41%
        ///   plan         291 predictions,  1 real   0.34%
        ///   default       20 predictions,  0 real   unmeasured, and the whole open question
        ///
        /// Roughly 250 false alarms per real prompt in all three. Only `default` is different, and
        /// an allow-list of one is also the safe shape: a mode nobody has measured yet defaults to
        /// standing down rather than to firing.
        /// </summary>
        public const string ModeDefault = "default";

        /// <summary>The Codex policy under which a session actually stops and asks.</summary>
        public const string CodexOnRequest = "on-request";

        /// <summary>
        /// How long a Codex call must sit before it is a person, not a slow command.
        ///
        /// MEASURED 2026-09-21 on this machine's corpus, and the control group is what makes
        /// it trustworthy: sessions running `approval_policy: never` CANNOT ask a human, so
        /// every gap in them is machine-only. Across 18,974 such calls the longest gap was
        /// 121.2 s, and the count over each threshold was:
        ///
        ///    30 s   946 false alarms (4.99%)
        ///   120 s    36            (0.19%)
        ///   180 s     0            (0.00%)
        ///
        /// In `on-request` sessions 5 of 171 calls exceeded 180 s, at 320, 351, 449, 1112 and
        /// 2922 seconds. The last three are 7, 18 and 49 minutes, which are a person.
        ///
        /// SIX TIMES Claude's 30 s, deliberately. Claude gets a second, independent question
        /// answered by the permission rules ("would this have prompted?"); Codex has no rule
        /// corpus, so the stall is the only signal and it has to carry the whole weight.
        /// Borrowing Claude's threshold would fire on 5% of calls that cannot prompt at all.
        ///
        /// One machine's corpus. A number this clean deserves re-measuring elsewhere before
        /// it is believed generally.
        /// </summary>
        public const double CodexStallSeconds = 180.0;

        /// <summary>Evaluate one session. Pure: no clock of its own, no IO, no host calls.</summary>
        public static Detection Evaluate(AgentSession session, RuleSet rules,
                                         double thresholdSeconds, DateTime nowUtc)
        {
            if (session == null) return null;
            var detection = new Detection
            {
                Session = session,
                IdleSeconds = session.IdleSeconds(nowUtc),
            };

            if (!session.SawAnyCall)
            {
                // Distinct from "nothing outstanding" on purpose. A transcript that parses but
                // yields no calls at all is how a renamed record type silently zeroes a whole
                // corpus, and it has happened once already in the sibling project. Saying
                // "everything is fine" would be the wrong answer to "I understood none of this".
                detection.Outcome = DetectionOutcome.AdapterSuspect;
                detection.Reason = "transcript parsed but contained no tool calls";
                return detection;
            }

            if (session.Outstanding.Count == 0)
            {
                detection.Outcome = DetectionOutcome.Idle;
                detection.Reason = "nothing outstanding";
                return detection;
            }

            // The OLDEST outstanding call is the one a human is looking at. A newer one cannot be
            // the blocker, because the agent issues them in order and waits for each.
            OutstandingCall oldest = null;
            foreach (OutstandingCall call in session.Outstanding)
            {
                if (oldest == null || call.StartedUtc < oldest.StartedUtc) oldest = call;
            }
            detection.Call = oldest;

            string mode = oldest.Mode ?? session.Mode;

            // CODEX DECIDES ON THE STALL ALONE, and that is not a shortcut. The rule join
            // exists to answer "would this call have prompted?", which Claude needs because
            // `default` mode still auto-allows whatever the user's rules cover. Codex answers
            // that question itself, per session, in approval_policy -- and it carries neither
            // Command nor Argument, so EvaluateCall would return Undecidable every time and a
            // Codex session could never reach Blocked however long it waited.
            if (string.Equals(session.Agent, TranscriptReader.AgentCodex,
                              StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(mode, CodexOnRequest, StringComparison.OrdinalIgnoreCase))
                {
                    detection.Outcome = DetectionOutcome.StoodDownAutoMode;
                    detection.Reason = "codex " + (string.IsNullOrEmpty(mode) ? "unknown" : mode)
                                       + ": this session never stops to ask, so nothing here is "
                                       + "waiting on you";
                    return detection;
                }
                if (detection.IdleSeconds < CodexStallSeconds)
                {
                    detection.Outcome = DetectionOutcome.Working;
                    detection.Reason = "outstanding for " + Format(detection.IdleSeconds)
                                       + ", under the " + Format(CodexStallSeconds) + " threshold";
                    return detection;
                }
                detection.Outcome = DetectionOutcome.Blocked;
                detection.Reason = "stalled " + Format(detection.IdleSeconds) + " on "
                                   + (oldest.Tool ?? "?")
                                   + " in a session that asks before it acts";
                return detection;
            }

            if (!string.Equals(mode, ModeDefault, StringComparison.OrdinalIgnoreCase))
            {
                // Stand down, but WORK OUT THE ANSWER ANYWAY and keep it in WouldHaveBeen.
                //
                // The stand-down governs unsolicited speech, and it should: measured over 36,497
                // auto-mode calls, acting here would be right 0.3% to 0.4% of the time, and
                // narrowing it to only the MANAGED ask/deny tier does not help (0.3% precision, 6%
                // recall -- that tier is 193 rules across four tools, not a needle).
                //
                // But a user who presses "Check now" has ASKED, and an answer to a direct question
                // does not need 90% precision the way an interruption does. Without this the pane
                // could only say "2 in auto mode", which is unfalsifiable from the outside and made
                // the module untestable on a machine that never leaves auto -- the maintainer's
                // machine, and the reason this exists.
                //
                // Nothing downstream may key on this: the notify path reads Outcome, and Outcome is
                // StoodDownAutoMode here whatever the shadow says.
                var shadow = new Detection
                {
                    Session = session,
                    Call = oldest,
                    IdleSeconds = detection.IdleSeconds,
                };
                Decide(shadow, oldest, rules, thresholdSeconds);
                detection.WouldHaveBeen = shadow.Outcome;
                detection.Verdict = shadow.Verdict;
                detection.Outcome = DetectionOutcome.StoodDownAutoMode;
                // The ~0.4% figure is a CLAUDE measurement, over Claude transcripts joined against
                // Claude permission rules, and that is all this line ever has to describe: a Codex
                // session cannot reach here, because the Codex block above returns on all three of its
                // paths. This used to carry a second, Codex-worded arm behind an isCodex test that was
                // always false -- a reason string no session could ever be given.
                string named = string.IsNullOrEmpty(mode) ? "unknown" : mode;
                detection.Reason =
                    named + " mode: rules predict prompts at ~0.4% precision outside default";
                if (shadow.Outcome == DetectionOutcome.Blocked)
                    detection.Reason += " (would have flagged it in default mode)";
                return detection;
            }

            Decide(detection, oldest, rules, thresholdSeconds);
            return detection;
        }

        /// <summary>
        /// Everything after the mode gate: threshold, then rule verdict. Extracted so the
        /// stand-down path can run it into a throwaway Detection and keep the answer without
        /// acting on it. One implementation, so the shadow answer cannot drift from the real one.
        /// </summary>
        private static void Decide(Detection detection, OutstandingCall oldest, RuleSet rules,
                                   double thresholdSeconds)
        {
            if (detection.IdleSeconds < thresholdSeconds)
            {
                detection.Outcome = DetectionOutcome.Working;
                detection.Reason = "outstanding for " + Format(detection.IdleSeconds)
                                   + ", under the " + Format(thresholdSeconds) + " threshold";
                return;
            }

            detection.Verdict = PermissionRules.EvaluateCall(
                oldest.Tool, oldest.Command, oldest.Argument, rules);
            if (detection.Verdict == RuleVerdict.WouldAllow)
            {
                detection.Outcome = DetectionOutcome.StalledButAllowed;
                detection.Reason = "stalled " + Format(detection.IdleSeconds)
                                   + " but the rules allow this call, so it is slow, not blocked";
                return;
            }
            if (detection.Verdict == RuleVerdict.Undecidable)
            {
                // The rules had nothing addressable to judge, so "would prompt" would have been
                // arithmetic rather than a finding. A long-running Agent call is the live example.
                detection.Outcome = DetectionOutcome.NotDecidable;
                detection.Reason = "stalled " + Format(detection.IdleSeconds) + " on "
                                   + (oldest.Tool ?? "?")
                                   + ", which carries nothing the permission rules can judge";
                return;
            }

            detection.Outcome = DetectionOutcome.Blocked;
            detection.Reason = "stalled " + Format(detection.IdleSeconds) + " on "
                               + (oldest.Tool ?? "?") + ", which the rules say would prompt";
            return;
        }

        /// <summary>
        /// What the rules APPROVED on the user's behalf, per session, since a given call id set was
        /// last counted.
        ///
        /// This is the other half of an approval module, and it was missing. The detector reports
        /// what it would have STOPPED; nothing reported what went through. On a machine with 517
        /// user allow rules plus a managed policy, running in auto mode, that is thousands of
        /// commands a day the user never sees -- and "never sees" is precisely what an audit trail
        /// is for. It answers a question the notify half cannot: not "am I blocked?" but "what has
        /// been run for me?"
        ///
        /// Keyed by the ROOT EXECUTABLE, never the command. `git`, `rg`, `dotnet` -- the same line
        /// AiBrain holds by logging endpoint HOSTS rather than URLs, and the same line the spoken
        /// half holds. A full command in the diagnostic log would put arguments, paths and
        /// occasionally a token into a file SUPPORT.md invites users to attach to an issue.
        ///
        /// `alreadyCounted` is consumed AND updated, so a call is counted once however many times
        /// the transcript is re-read. The caller owns pruning it.
        /// </summary>
        /// <param name="recent">
        /// Optional. When given, each approved call is appended here WITH ITS COMMAND, for the
        /// approvals card in the options pane and nothing else. Null for every other caller,
        /// which is the default and keeps the command text where it has always stayed.
        /// </param>
        public static Dictionary<string, int> ApprovedSince(AgentSession session, RuleSet rules,
                                                            HashSet<string> alreadyCounted,
                                                            IList<ApprovalEntry> recent)
        {
            var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (session == null || session.Completed == null) return tally;
            foreach (OutstandingCall call in session.Completed)
            {
                if (call == null || string.IsNullOrEmpty(call.Id)) continue;
                string key = session.SessionId + "/" + call.Id;
                if (alreadyCounted != null && !alreadyCounted.Add(key)) continue;
                RuleVerdict verdict = PermissionRules.EvaluateCall(
                    call.Tool, call.Command, call.Argument, rules);
                // WouldAllow only. A call that would have PROMPTED and completed anyway was either
                // answered by the user or auto-accepted by the agent's own mode, and neither is the
                // rules approving it -- claiming otherwise would inflate this into a count of
                // "everything that ran", which is not an approval record.
                if (verdict != RuleVerdict.WouldAllow) continue;
                string root = RootExecutable(call);
                if (recent != null)
                {
                    recent.Add(new ApprovalEntry
                    {
                        WhenLocal = DateTime.Now,
                        Root = root,
                        Command = call.Command ?? "",
                    });
                }
                int current;
                tally.TryGetValue(root, out current);
                tally[root] = current + 1;
            }
            return tally;
        }

        /// <summary>
        /// The executable a call runs, or the tool name when there is no command. Safe to log: it
        /// is one token with no arguments, no path and no user text.
        /// </summary>
        public static string RootExecutable(OutstandingCall call)
        {
            if (call == null) return "?";
            string command = call.Command;
            if (string.IsNullOrEmpty(command)) return call.Tool ?? "?";
            string stripped = PermissionRules.StripEnvPrefix(command);
            List<string> parts = CommandSplitter.Split(
                stripped, string.Equals(call.Tool, "PowerShell", StringComparison.OrdinalIgnoreCase)
                    ? CommandSplitter.ShellPowerShell : CommandSplitter.ShellBash);
            string first = parts.Count > 0 ? parts[0] : stripped;
            first = PermissionRules.StripEnvPrefix(first).Trim();
            if (first.Length == 0) return call.Tool ?? "?";
            int space = first.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
            if (space > 0) first = first.Substring(0, space);
            // A path would leak a directory layout, so keep the leaf only: /usr/bin/git -> git.
            int slash = first.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0 && slash < first.Length - 1) first = first.Substring(slash + 1);
            return first.Length == 0 ? (call.Tool ?? "?") : first;
        }

        /// <summary>One line for the diagnostic log: "approved 14: git x6, rg x5, dotnet x3".
        /// Highest count first, capped so one busy poll cannot write a paragraph.</summary>
        public static string DescribeApprovals(Dictionary<string, int> tally, int cap)
        {
            if (tally == null || tally.Count == 0) return null;
            var names = new List<string>(tally.Keys);
            names.Sort(delegate(string left, string right)
            {
                int byCount = tally[right].CompareTo(tally[left]);
                return byCount != 0 ? byCount : string.CompareOrdinal(left, right);
            });
            int total = 0;
            foreach (int count in tally.Values) total += count;
            var parts = new List<string>();
            for (int i = 0; i < names.Count && i < cap; i++)
                parts.Add(names[i] + " x" + tally[names[i]].ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            string more = names.Count > cap
                ? ", +" + (names.Count - cap).ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + " more"
                : "";
            return "approved " + total.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + " call(s) the rules allow without asking: " + string.Join(", ", parts.ToArray())
                   + more;
        }

        private static string Format(double seconds)
        {
            if (seconds < 90.0)
                return ((int)Math.Round(seconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
            int minutes = (int)Math.Round(seconds / 60.0);
            return minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
        }

        /// <summary>A short, human phrase for a blocked session. Never carries command text.</summary>
        public static string Describe(Detection detection)
        {
            if (detection == null || detection.Outcome != DetectionOutcome.Blocked) return null;
            string tool = detection.Call != null ? detection.Call.Tool : null;
            string where = ShortProject(detection.Session != null ? detection.Session.Cwd : null);
            string what = string.IsNullOrEmpty(tool) ? "something" : tool;
            string howLong = Format(detection.IdleSeconds);
            if (string.IsNullOrEmpty(where))
                return "Your agent has been waiting " + howLong + " for an answer about " + what + ".";
            return "Your agent in " + where + " has been waiting " + howLong
                   + " for an answer about " + what + ".";
        }

        /// <summary>
        /// The last path segment of a working directory, which is the project name a human uses.
        ///
        /// Deliberately NOT the full path. The whole path is personal data -- it routinely carries
        /// a customer or employer name -- and the speech bubble is on screen where anyone can see
        /// it. One segment is enough to tell two sessions apart, which is all this is for.
        /// </summary>
        public static string ShortProject(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return null;
            string trimmed = cwd.TrimEnd('\\', '/');
            if (trimmed.Length == 0) return null;
            int cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            string leaf = cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;
            return leaf.Length == 0 ? null : leaf;
        }
    }
}
