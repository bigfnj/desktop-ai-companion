using System;
using System.Collections.Generic;
using System.Xml;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// Which animations a given pet actually has.
    ///
    /// WHY THIS IS PER-PET AND NOT ONE FIXED LIST. Measured across the 53 companions this repo
    /// ships: 709 distinct animation names, and the set present on EVERY pet is EMPTY. The four
    /// names that come closest -- fall, drag, kill, sync, on 49 to 52 of 53 -- are the engine's
    /// reserved lifecycle animations, so a "safe" dropdown built from them would be offering to
    /// play the dying animation. The smallest pet ships eight animations in total, which caps any
    /// intersection at eight before you start.
    ///
    /// That also condemns the list this module shipped with. `boing, jump, run` reaches 34 of 53
    /// pets and `boing` exists on exactly ONE, so "Play an animation" has been a silent no-op on
    /// nineteen pets since it was written -- silent because PlayAnimationAll is documented to try
    /// each candidate and do nothing when a pet defines none of them.
    ///
    /// So the user picks a pet and then one of THAT pet's animations, which is the only way the
    /// choice can be honest. The names come from the pet's own XML, through
    /// ICompanionManager.TryReadTypeXml, which searches the writable library, then the bundled
    /// pets, then the built-in default.
    /// </summary>
    internal static class PetAnimations
    {
        /// <summary>
        /// The value stored when the user has not chosen a specific pet.
        ///
        /// It is not a pet id, and it cannot collide with one: a type id is a folder name.
        /// </summary>
        internal const string AnyPet = "(any pet)";

        /// <summary>
        /// The fallback candidates used for "any pet", in preference order.
        ///
        /// Chosen by COVERAGE rather than by taste, which is the opposite of how the old list was
        /// chosen: walk 34/53, sit 32/53, turn 31/53, stand 30/53. It still will not reach every
        /// pet -- nothing can -- but it reaches roughly twice as many as `boing` did, and every
        /// name in it is an ordinary visible animation rather than an engine lifecycle one.
        /// </summary>
        internal static IReadOnlyList<string> AnyPetCandidates
        {
            get { return new[] { "walk", "sit", "turn", "stand", "jump", "run" }; }
        }

        /// <summary>
        /// Animation names declared by one pet's XML, de-duplicated and sorted.
        ///
        /// Duplicates are real: the seven sheep recolours each declare walk_top_corner twice, and
        /// the engine takes the first match, so showing it twice would be offering the user a
        /// choice that does not exist. Comparison is ordinal-ignore-case because the host's own
        /// lookup (FormCompanion.TryPlayAnimation) is case-insensitive, while the engine's magic
        /// -name binding is case-sensitive -- a difference that does not matter here but is worth
        /// not accidentally depending on.
        /// </summary>
        internal static List<string> FromXml(string animationsXml)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(animationsXml)) return names;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var document = new XmlDocument();
                // No DTD, no resolver: this is a file from a downloaded pet pack, and an XML
                // parser that will fetch an external entity is a document that can read the disk.
                document.XmlResolver = null;
                document.LoadXml(animationsXml);
                XmlNodeList nodes = document.GetElementsByTagName("animation");
                foreach (XmlNode node in nodes)
                {
                    XmlNode nameNode = null;
                    foreach (XmlNode child in node.ChildNodes)
                    {
                        if (string.Equals(child.Name, "name", StringComparison.OrdinalIgnoreCase))
                        {
                            nameNode = child;
                            break;
                        }
                    }
                    if (nameNode == null) continue;
                    string name = (nameNode.InnerText ?? "").Trim();
                    // The schema allows an EMPTY name (it validates length only, 0 to 128), so a
                    // blank entry is legal XML and would render as a blank dropdown row.
                    if (name.Length == 0) continue;
                    if (!seen.Add(name)) continue;
                    names.Add(name);
                }
            }
            catch (XmlException)
            {
                // A pet with unreadable XML contributes nothing rather than taking the pane down.
                return names;
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// The ordered candidate list for a stored animation choice.
        ///
        /// The chosen name leads, because the user picked it off a pet's own list; the coverage
        /// list follows (the convention AiBrain, Reminder and StartUp all use) so a pet that has
        /// since been swapped degrades instead of doing nothing at all.
        ///
        /// TAKES NO PET. It used to, and never read it, which made the whole thing look like it
        /// honoured the pane's pet dropdown while the only consumer handed the result to
        /// PlayAnimationAll -- every pet, by contract. Choosing a pet could not change what
        /// happened. Which pet to play on is now the caller's business, where it is visible.
        /// </summary>
        internal static IReadOnlyList<string> Candidates(string animation)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(animation) && animation != AnyPet) list.Add(animation);
            foreach (string fallback in AnyPetCandidates)
                if (!list.Contains(fallback)) list.Add(fallback);
            return list;
        }
    }
}
