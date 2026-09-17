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
        /// <summary>Auto mode: the rules do not predict prompts here, so stand down.</summary>
        StoodDownAutoMode = 4,
        /// <summary>The transcript parsed but held no tool calls at all -- adapter may be stale.</summary>
        AdapterSuspect = 5,
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

        public const string ModeAuto = "auto";

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
            if (string.Equals(mode, ModeAuto, StringComparison.OrdinalIgnoreCase))
            {
                detection.Outcome = DetectionOutcome.StoodDownAutoMode;
                detection.Reason = "auto mode: rules predict prompts at ~0.4% precision here";
                return detection;
            }

            if (detection.IdleSeconds < thresholdSeconds)
            {
                detection.Outcome = DetectionOutcome.Working;
                detection.Reason = "outstanding for " + Format(detection.IdleSeconds)
                                   + ", under the " + Format(thresholdSeconds) + " threshold";
                return detection;
            }

            detection.Verdict = PermissionRules.EvaluateCall(oldest.Tool, oldest.Command, rules);
            if (detection.Verdict == RuleVerdict.WouldAllow)
            {
                detection.Outcome = DetectionOutcome.StalledButAllowed;
                detection.Reason = "stalled " + Format(detection.IdleSeconds)
                                   + " but the rules allow this call, so it is slow, not blocked";
                return detection;
            }

            detection.Outcome = DetectionOutcome.Blocked;
            detection.Reason = "stalled " + Format(detection.IdleSeconds) + " on "
                               + (oldest.Tool ?? "?") + ", which the rules say would prompt";
            return detection;
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
