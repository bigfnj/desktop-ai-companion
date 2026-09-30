using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// PetGraph roots the magic animations the way the HOST binds them, so its reachability verdict on a
    /// hand-authored pet agrees with what the runtime will actually play.
    ///
    /// The loader (src/dotNet/Xml.cs) matches fall/drag/kill/sync exactly -- no Trim, no case folding, a
    /// later duplicate overwriting an earlier one -- and the runtime then resolves fall and drag by fallback
    /// (Animations.ResolveMagicAnimations: the lowest id whose name contains the word, else the lowest id).
    /// PetGraph used to root any trimmed case-insensitive match instead, so a "Kill" nothing pointed at was
    /// reported connected while the host never plays it, and a lone "Falling" that the host DOES bind as the
    /// fall was reported an orphan (F440). Built from in-memory DTOs: no XML, no sheet, no validator.
    /// </summary>
    public static class PetGraphSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();

            // 1: a capitalised 'Kill' with no inbound edge is a hidden orphan, not a root. The witness beside it
            //    is the exact lowercase 'kill', also with no inbound edge, which the host DOES bind.
            {
                XmlData.RootNode pet = Pet(
                    Anim(1, "stand", 1),
                    Anim(2, "fall", 1),
                    Anim(3, "drag", 2),
                    Anim(4, "Kill"),
                    Anim(5, "sync", 1),
                    Anim(6, "kill"));
                GraphReport g = PetGraph.Analyze(pet);
                if (!g.Unreachable.Contains(4))
                    failures.Add("'Kill' (capital K, no inbound edge) was not reported unreachable; the loader binds the exact "
                        + "lowercase name only, so the host never plays it and the orphan is hidden");
                if (g.Unreachable.Contains(6))
                    failures.Add("WITNESS: the exact 'kill' with no inbound edge was reported unreachable, but the host binds "
                        + "it as the kill entry point");
                if (g.Unreachable.Contains(2) || g.Unreachable.Contains(3) || g.Unreachable.Contains(5))
                    failures.Add("WITNESS: an exact fall/drag/sync was reported unreachable: " + Join(g.Unreachable));
            }

            // 2: a case variant BESIDE an exact match is an orphan: the exact one wins the binding.
            {
                XmlData.RootNode pet = Pet(
                    Anim(1, "stand", 1),
                    Anim(2, "fall", 1),
                    Anim(3, "Fall"),
                    Anim(4, "drag", 2),
                    Anim(5, "kill"),
                    Anim(6, "sync", 1));
                GraphReport g = PetGraph.Analyze(pet);
                if (!g.Unreachable.Contains(3))
                    failures.Add("'Fall' beside an exact 'fall' was not reported unreachable; the exact name takes the binding "
                        + "and the variant is never played");
                if (g.Unreachable.Contains(2))
                    failures.Add("WITNESS: the exact 'fall' was reported unreachable");
            }

            // 3: NO exact 'fall', but a name containing the word: the runtime binds it as the fall, so it is a
            //    root and not an orphan. Nothing else leads to it (drag returns to the hub here), so mutating
            //    the fallback away reports it unreachable as well as un-rooted.
            {
                XmlData.RootNode pet = Pet(
                    Anim(1, "stand", 1),
                    Anim(2, "Falling"),
                    Anim(3, "drag", 1),
                    Anim(4, "kill"),
                    Anim(5, "sync", 1));
                GraphReport g = PetGraph.Analyze(pet);
                if (g.Unreachable.Contains(2))
                    failures.Add("'Falling' with no exact 'fall' declared was reported unreachable, but the runtime's "
                        + "contains-the-word fallback binds it as the fall entry point");
                if (!g.Roots.Contains(2))
                    failures.Add("'Falling' is not a root although the runtime binds it as the fall");
            }

            // 4: no exact 'drag' and no name containing it: the runtime binds the LOWEST id, which is a root
            //    anyway here (the hub) -- so nothing else may be rooted for drag. A second, unrelated
            //    animation with no inbound edge stays an orphan.
            {
                XmlData.RootNode pet = Pet(
                    Anim(1, "stand", 1),
                    Anim(2, "fall", 1),
                    Anim(3, "loner"),
                    Anim(4, "kill"),
                    Anim(5, "sync", 1));
                GraphReport g = PetGraph.Analyze(pet);
                if (!g.Unreachable.Contains(3))
                    failures.Add("an animation with no inbound edge and no magic name was not reported unreachable (" + Join(g.Unreachable) + ")");
                if (g.Roots.Contains(3))
                    failures.Add("the drag fallback rooted 'loner' (id 3); with no name containing 'drag' the runtime binds the lowest id, 1");
            }

            // 5: two exact 'fall's: the loader's foreach leaves the LAST one bound, so the first is the orphan.
            //    drag leads to the LAST one, so the first has no inbound edge of any kind.
            {
                XmlData.RootNode pet = Pet(
                    Anim(1, "stand", 1),
                    Anim(2, "fall"),
                    Anim(3, "fall"),
                    Anim(4, "drag", 3),
                    Anim(5, "kill"),
                    Anim(6, "sync", 1));
                GraphReport g = PetGraph.Analyze(pet);
                if (!g.Unreachable.Contains(2) || g.Unreachable.Contains(3))
                    failures.Add("with two exact 'fall's the loader binds the LAST (id 3); expected id 2 unreachable and 3 a root, got unreachable "
                        + Join(g.Unreachable));
            }

            // 6: padding is not exact. " fall" (leading space) beside no exact 'fall' is bound by the CONTAINS
            //    fallback and so is a root -- the same answer the old trim gave, for the right reason -- while a
            //    padded " kill" has no fallback and is an orphan.
            {
                XmlData.RootNode pet = Pet(
                    Anim(1, "stand", 1),
                    Anim(2, " fall"),
                    Anim(3, "drag", 2),
                    Anim(4, " kill"),
                    Anim(5, "sync", 1));
                GraphReport g = PetGraph.Analyze(pet);
                if (g.Unreachable.Contains(2))
                    failures.Add("' fall' (padded) with no exact 'fall' was reported unreachable; the runtime's contains fallback binds it");
                if (!g.Unreachable.Contains(4))
                    failures.Add("' kill' (padded) was not reported unreachable; the loader matches 'kill' exactly and kill has no fallback");
            }

            var sb = new StringBuilder();
            sb.AppendLine("pet-graph self-test: magic entry points are rooted the way the host binds them");
            if (failures.Count == 0)
            {
                sb.Append("  exact names bind (last wins), 'Kill'/'Fall' variants and padded 'kill' are orphans, fall/drag fall back to a name containing the word, then the lowest id");
                detail = sb.ToString();
                return true;
            }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        /// <summary>A pet whose two spawns both land on animation 1, so 1 is a root by spawn and everything
        /// else is reached only through edges or through a magic binding.</summary>
        private static XmlData.RootNode Pet(params XmlData.AnimationNode[] animations)
        {
            return new XmlData.RootNode
            {
                Spawns = new XmlData.SpawnsNode
                {
                    Spawn = new[]
                    {
                        new XmlData.SpawnNode { Id = 1, Probability = 100, X = "0", Y = "0",
                            Next = new XmlData.NextNode { Value = 1, Probability = 100 } },
                    },
                },
                Animations = new XmlData.AnimationsNode { Animation = animations },
            };
        }

        /// <summary>An animation with the given id and name whose sequence leads to <paramref name="nextIds"/>
        /// (none for a terminal).</summary>
        private static XmlData.AnimationNode Anim(int id, string name, params int[] nextIds)
        {
            var next = new List<XmlData.NextNode>();
            foreach (int target in nextIds) next.Add(new XmlData.NextNode { Value = target, Probability = 100, OnlyFlag = "none" });
            return new XmlData.AnimationNode
            {
                Id = id,
                Name = name,
                Sequence = new XmlData.SequenceNode { RepeatFromFrame = 0, RepeatCount = "0", Frame = new[] { 0 }, Next = next.ToArray() },
            };
        }

        private static string Join(List<int> ids)
        {
            var parts = new List<string>();
            foreach (int id in ids) parts.Add(id.ToString());
            return "[" + string.Join(",", parts.ToArray()) + "]";
        }
    }
}
