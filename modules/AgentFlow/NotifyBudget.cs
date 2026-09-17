using System;
using System.Collections.Generic;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// Decides whether a notification is allowed to happen, and it is the only thing standing
    /// between a working detector and a companion that will not shut up.
    ///
    /// Three independent guards, each because one specific thing goes wrong without it:
    ///
    ///   ONE-SHOT PER CALL. A blocked call stays blocked -- that is the point -- so the detector
    ///   reports it on every single poll until the human answers. Without this the companion
    ///   repeats itself every few seconds. Keyed on session id plus call id, so the SAME prompt
    ///   never speaks twice and a genuinely new prompt in the same session still does.
    ///
    ///   COOLDOWN. Several sessions can block at once (3 to 5 concurrent agents is normal on this
    ///   machine), and five distinct one-shots all firing in the same second is still five bubbles
    ///   in a row. A global cooldown spaces them.
    ///
    ///   DEATH-LOOP GUARD. A sliding window with a cap, then a pause. The idea is taken from
    ///   nockasdd/domyh-auto-accept, which is the only tool in the field to have one, but NOT its
    ///   implementation: there the cooldown clears all state and auto-resumes, so a persistent loop
    ///   simply cycles forever rather than stopping. Here the pause is entered explicitly and the
    ///   window is not cleared by it, so a loop that keeps firing keeps the guard closed.
    ///
    /// Why a read-only module needs this at all: AgentFlow only speaks, it never presses anything,
    /// so it cannot get into the retry loop that guard was written for. It can still get into the
    /// NOTIFICATION equivalent -- an agent that blocks, is answered, blocks again on the same
    /// wrong thing, forever -- and the cost there is the user turning the module off, which is a
    /// silent failure that looks like the feature not working.
    /// </summary>
    public sealed class NotifyBudget
    {
        /// <summary>Minimum gap between any two notifications.</summary>
        public const double DefaultCooldownSeconds = 120.0;
        /// <summary>Window the cap is measured over.</summary>
        public const double DefaultWindowSeconds = 600.0;
        /// <summary>Notifications allowed inside one window before the guard closes.</summary>
        public const int DefaultMaxPerWindow = 5;
        /// <summary>How long the guard stays closed once tripped.</summary>
        public const double DefaultPauseSeconds = 1800.0;

        // NOT readonly, and that is the whole fix for "saving the pane re-arms the one-shot".
        // SavePaneValues used to replace the budget object so a changed cooldown took effect
        // immediately, which threw away _announced with it -- so pressing Save let a prompt that
        // had already been announced be announced again. The one-shot is the guard that stops the
        // companion repeating itself on every poll, so discarding it undoes the module's single
        // most important piece of restraint, and the pane is the one place a user goes when they
        // find it too chatty.
        private double _cooldownSeconds;
        private readonly double _windowSeconds;
        private readonly int _maxPerWindow;
        private readonly double _pauseSeconds;

        private readonly HashSet<string> _announced = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<DateTime> _window = new List<DateTime>();
        private DateTime _lastNotifyUtc = DateTime.MinValue;
        private DateTime _pausedUntilUtc = DateTime.MinValue;

        public NotifyBudget()
            : this(DefaultCooldownSeconds, DefaultWindowSeconds, DefaultMaxPerWindow,
                   DefaultPauseSeconds)
        {
        }

        public NotifyBudget(double cooldownSeconds, double windowSeconds, int maxPerWindow,
                            double pauseSeconds)
        {
            _cooldownSeconds = cooldownSeconds;
            _windowSeconds = windowSeconds;
            _maxPerWindow = maxPerWindow;
            _pauseSeconds = pauseSeconds;
        }

        /// <summary>True while the death-loop guard is holding notifications back.</summary>
        public bool IsPaused(DateTime nowUtc) { return nowUtc < _pausedUntilUtc; }

        /// <summary>
        /// Change the cooldown on the LIVE budget, keeping every one-shot, the window and any
        /// active pause. The alternative -- constructing a replacement -- is what the pane used to
        /// do, and it silently re-armed prompts that had already been announced.
        /// </summary>
        public void SetCooldownSeconds(double cooldownSeconds)
        {
            _cooldownSeconds = cooldownSeconds;
        }

        /// <summary>The cooldown in force, so a test can assert the pane's value reached here.</summary>
        public double CooldownSeconds { get { return _cooldownSeconds; } }

        /// <summary>Has this detection already been announced? Exposed so the one-shot can be
        /// asserted across a settings save rather than assumed to survive it.</summary>
        public bool WasAnnounced(Detection detection)
        {
            return detection != null && _announced.Contains(KeyFor(detection));
        }

        /// <summary>Notifications inside the current window. Exposed so a test can assert the cap
        /// rather than assume it -- a bound nothing observes is not a bound.</summary>
        public int WindowCount(DateTime nowUtc)
        {
            Trim(nowUtc);
            return _window.Count;
        }

        /// <summary>
        /// May this detection be announced? Asking does not consume anything; a caller that decides
        /// to speak must call <see cref="Record"/>. Split in two so a caller can log the refusal
        /// reason without the act of logging changing the budget.
        /// </summary>
        public bool ShouldAnnounce(Detection detection, DateTime nowUtc, out string refusal)
        {
            refusal = null;
            if (detection == null || detection.Outcome != DetectionOutcome.Blocked)
            {
                refusal = "not blocked";
                return false;
            }
            if (IsPaused(nowUtc))
            {
                refusal = "death-loop guard is paused until "
                          + _pausedUntilUtc.ToString("HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture) + "Z";
                return false;
            }
            string key = KeyFor(detection);
            if (_announced.Contains(key))
            {
                refusal = "already announced this prompt";
                return false;
            }
            if (_lastNotifyUtc != DateTime.MinValue
                && (nowUtc - _lastNotifyUtc).TotalSeconds < _cooldownSeconds)
            {
                refusal = "within the cooldown";
                return false;
            }
            Trim(nowUtc);
            if (_window.Count >= _maxPerWindow)
            {
                refusal = "hit the per-window cap";
                return false;
            }
            return true;
        }

        /// <summary>Consume the budget for a notification that was actually delivered.</summary>
        public void Record(Detection detection, DateTime nowUtc)
        {
            if (detection == null) return;
            _announced.Add(KeyFor(detection));
            _lastNotifyUtc = nowUtc;
            Trim(nowUtc);
            _window.Add(nowUtc);
            if (_window.Count >= _maxPerWindow)
            {
                // Enter the pause explicitly, and do NOT clear the window doing it. Clearing is
                // what makes domyh's version cycle: it forgets why it paused, resumes clean, and
                // trips again immediately, forever. Keeping the timestamps means a genuine loop
                // stays shut off until it actually goes quiet.
                _pausedUntilUtc = nowUtc.AddSeconds(_pauseSeconds);
            }
        }

        /// <summary>Forget that a call was announced, once it is no longer outstanding.</summary>
        public void Forget(string sessionId, string callId)
        {
            if (sessionId == null || callId == null) return;
            _announced.Remove(sessionId + "/" + callId);
        }

        /// <summary>Drop one-shot keys for calls that are no longer outstanding anywhere.</summary>
        public void Retain(IEnumerable<string> liveKeys)
        {
            if (liveKeys == null) return;
            var live = new HashSet<string>(liveKeys, StringComparer.Ordinal);
            var stale = new List<string>();
            foreach (string key in _announced) if (!live.Contains(key)) stale.Add(key);
            foreach (string key in stale) _announced.Remove(key);
        }

        /// <summary>Session plus call, so the same prompt cannot speak twice and a new one can.</summary>
        public static string KeyFor(Detection detection)
        {
            string session = detection != null && detection.Session != null
                ? detection.Session.SessionId : "?";
            string call = detection != null && detection.Call != null ? detection.Call.Id : "?";
            return session + "/" + call;
        }

        private void Trim(DateTime nowUtc)
        {
            DateTime cutoff = nowUtc.AddSeconds(-_windowSeconds);
            while (_window.Count > 0 && _window[0] < cutoff) _window.RemoveAt(0);
        }
    }
}
