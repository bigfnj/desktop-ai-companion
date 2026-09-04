using System;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Canned one-liners for the poke-escalation "sass" tier (pokes 5-11). Deliberately a plain
    /// list so more can be slotted in later (or swapped for a bundled text resource) without
    /// touching the escalation logic. Never throws.
    /// </summary>
    internal static class PokeReactions
    {
        private static readonly Random _rng = new Random();
        private static int _last = -1;

        // --- Add / edit freely: one string per reaction. -------------------------
        private static readonly string[] Sass =
        {
            "Stop tickling me!",
            "Are you finished?",
            "I'm not a slot machine.",
            "Do you mind?",
            "Okay, okay, I'm awake!",
            "Personal space, please!",
            "That tickles!",
            "Hey! Quit poking.",
            "You again?",
            "I have feelings, you know.",
            "Enough already!",
            "Is this fun for you?",
            "Boop me one more time, I dare you.",
            "Rude.",
            "I felt that.",
            "Was that necessary?",
            "Do I poke YOU while you're working?",
            "I'm a sheep, not a stress ball.",
            "This is harassment, you know.",
            "Poke poke poke. Very original.",
            "Careful, I bruise like a peach.",
            "You've unlocked my villain arc.",
            "I'm going to start charging per poke.",
            "Keep it up and I'm unionizing.",
            "You must be so much fun at parties.",
            "Cut it out, human.",
            "Every poke shortens our friendship.",
            "I'm counting these, you know.",
            "One of us is going to regret this.",
            "Poke received. Filing a complaint.",
            "I have a bathtub to escape to, don't tempt me.",
            "My patience is not infinite.",
            "Ow. Ow. Still ow.",
            "That's assault on a fictional sheep.",
            "Try the keyboard instead. It likes attention.",
        };
        // -------------------------------------------------------------------------

        /// <summary>A random sass line (avoids repeating the previous one). "" if the list is empty.</summary>
        public static string RandomSass()
        {
            int n = Sass.Length;
            if (n == 0) return "";
            if (n == 1) return Sass[0];
            int i;
            do { i = _rng.Next(n); } while (i == _last);
            _last = i;
            return Sass[i];
        }
    }
}
