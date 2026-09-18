using System;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// What AgentFlow does about a waiting prompt. One choice, not a pile of switches.
    ///
    /// It replaces two booleans that could contradict each other. <c>enabled</c> was the master
    /// switch and <c>autoApprove</c> sat underneath it, so "approve but do not watch" was
    /// expressible and meant nothing, and a user reading the pane had to work out that one
    /// checkbox silently governed the other. Four named modes say the same thing without the
    /// puzzle.
    ///
    /// AUTO-APPROVE STILL SPEAKS, and that is deliberate rather than an oversight in the
    /// exclusivity. It speaks about the prompts it REFUSED -- an option it does not recognise, a
    /// prompt with no approve-once row, a stand-down after too many presses. Those are precisely
    /// the moments the module exists for: a prompt it will not touch is one that sits there
    /// forever, and silence would recreate the failure the notify half was written to prevent.
    /// What it stops doing is narrating the ones it handled.
    /// </summary>
    internal static class AgentMode
    {
        /// <summary>Press the approve-once row; speak only about what it would not press.</summary>
        public const string AutoApprove = "autoApprove";
        /// <summary>Watch and say something. Presses nothing. The published 1.0.x behaviour.</summary>
        public const string Notify = "notify";
        /// <summary>Watch and write the audit line. Never speaks, never presses.</summary>
        public const string Log = "log";
        /// <summary>Do nothing at all: no scan, no audit, no speech.</summary>
        public const string Off = "off";

        public static string[] All { get { return new[] { AutoApprove, Notify, Log, Off }; } }

        public static bool IsKnown(string mode)
        {
            return mode == AutoApprove || mode == Notify || mode == Log || mode == Off;
        }

        /// <summary>
        /// The stored mode, or the one the old pair of booleans meant.
        ///
        /// Split out as a pure function for the same reason Fortunes split
        /// <c>MigrateContentLevel</c>: a migration is exactly the code that runs once, on someone
        /// else's machine, months later, and it is untestable if it can only be reached through a
        /// settings store.
        ///
        /// AgentFlow is PUBLISHED, so these legacy keys are on real installs and this is the only
        /// thing standing between an upgrade and a module that silently reverts to defaults. A
        /// recognised new value always wins; the booleans are consulted only when there is no new
        /// value at all.
        ///
        /// The mapping keeps what the user evidently chose, and never widens it:
        ///   enabled=false            -> Off        (it was doing nothing; it keeps doing nothing)
        ///   autoApprove=true         -> AutoApprove
        ///   enabled=true, no approve -> Notify     (the published default behaviour)
        ///
        /// Note what is NOT reachable by migration: Log. It is a new mode with no legacy
        /// equivalent, so nobody is silently moved into it.
        /// </summary>
        public static string Migrate(string stored, bool legacyEnabled, bool legacyAutoApprove)
        {
            if (IsKnown(stored)) return stored;
            if (!legacyEnabled) return Off;
            return legacyAutoApprove ? AutoApprove : Notify;
        }

        /// <summary>Does this mode read transcripts at all? Off is the only one that does not.</summary>
        public static bool Scans(string mode) { return mode != Off; }

        /// <summary>Does this mode press anything?</summary>
        public static bool Presses(string mode) { return mode == AutoApprove; }

        /// <summary>
        /// Does this mode speak about a blocked prompt it did not handle?
        ///
        /// True for AutoApprove as well as Notify, per the note at the top of this file. Log is
        /// the mode for someone who wants the audit trail and no pet commentary.
        /// </summary>
        public static bool Speaks(string mode)
        {
            return mode == Notify || mode == AutoApprove;
        }

        /// <summary>The plain-language labels, in the order they should appear.</summary>
        public static string[] Displays()
        {
            return new[]
            {
                "Approve prompts for me (one call at a time)",
                "Tell me when a prompt is waiting",
                "Just keep a log of what was approved",
                "Off",
            };
        }

        public static string ToDisplay(string mode)
        {
            string[] modes = All, displays = Displays();
            for (int i = 0; i < modes.Length; i++) if (modes[i] == mode) return displays[i];
            return displays[1];
        }

        public static string FromDisplay(string display)
        {
            string[] modes = All, displays = Displays();
            for (int i = 0; i < displays.Length; i++) if (displays[i] == display) return modes[i];
            return Notify;
        }
    }
}
