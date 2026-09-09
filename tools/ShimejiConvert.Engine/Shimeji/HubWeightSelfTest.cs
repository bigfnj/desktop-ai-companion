using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DesktopAICompanion.Tools.ShimejiConvert.Emit;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Pins the hub selection weighting: the damping curve and the minimum-share floor.
    ///
    /// This exists because the shipped corpus proved the weighting can be badly wrong while every other check
    /// stays green. A pet whose rarest animation needs ~54 minutes of idling to appear once is still valid,
    /// still round-trips, and still fully reachable -- reachability says an animation CAN play, not that it
    /// ever does. 392 of 609 hub options across the 27 converted pets sat below 1%, the worst at 0.03%.
    ///
    /// The real corpus is re-audited separately; these are the invariants the code must hold for any input.
    /// </summary>
    public static class HubWeightSelfTest
    {
        public static bool Run(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            Action<string, bool> check = (name, condition) =>
            {
                sb.AppendLine("  " + (condition ? "ok   " : "FAIL ") + name);
                if (!condition) ok = false;
            };

            sb.AppendLine("hub-weight self-test: damping curve + budgeted minimum-share floor");

            // ---- the curve ----
            check("a frequency of 0 stays at the baseline",
                PetEmitter.HubWeightFromFrequency(0) == PetEmitter.HubBaseWeight);
            check("a negative frequency is treated as 0",
                PetEmitter.HubWeightFromFrequency(-5) == PetEmitter.HubBaseWeight);

            // Monotonic, so a character that walks a lot still walks a lot.
            bool monotonic = true;
            int previous = PetEmitter.HubWeightFromFrequency(0);
            for (int f = 1; f <= 2000; f++)
            {
                int current = PetEmitter.HubWeightFromFrequency(f);
                if (current < previous) { monotonic = false; break; }
                previous = current;
            }
            check("the curve never decreases as frequency rises", monotonic);

            // Damped: the shipped spread was 326x. Same inputs must now land far closer together.
            //
            // A LINEAR CURVE WAS TRIED HERE AND REVERTED, with assertions that the curve preserves a
            // category's frequency MASS. Do not re-add them. Measured against ground truth -- share of
            // animation PLAYS, resolving a composite behaviour to the leaves it actually runs -- linear
            // overshoots locomotion by 10 to 18 points on every skin measured, where sqrt sits within a
            // couple of points on most. The full table and the two ways the metric was got wrong first are
            // recorded on PetEmitter.HubWeightFromFrequency. The residual error was the reachability
            // FLOOR's, not the curve's, and is asserted further down.
            int low = PetEmitter.HubWeightFromFrequency(0);
            int high = PetEmitter.HubWeightFromFrequency(1100);
            check("a 1100-vs-0 frequency spread damps to under 40x", high < low * 40);
            check("but the busy action is still clearly favoured", high > low * 4);

            // ---- the floor ----
            // The pathological real shape: three dominant options and a long tail pinned at the baseline.
            var weights = new List<int> { 1104, 1104, 1004, 454, 49, 44, 34, 4, 4, 4, 4, 4, 4, 4 };
            var curved = weights.Select(w => PetEmitter.HubWeightFromFrequency(Math.Max(0, w - PetEmitter.HubBaseWeight))).ToList();
            var floored = new List<int>(curved);
            PetEmitter.ApplyMinimumShare(floored, -1, PetEmitter.HubMinimumSharePercent);

            double total = floored.Sum();
            double worst = floored.Min() * 100.0 / total;
            check("after the floor, no option is below the minimum share",
                worst >= PetEmitter.HubMinimumSharePercent - 0.01);
            check("the floor only ever raises a weight, never lowers one",
                !floored.Where((w, i) => w < curved[i]).Any());
            check("the busiest option still leads after the floor",
                floored[0] == floored.Max());

            // Idempotent: a second pass must be a no-op, or the migration could not be re-run safely.
            var twice = new List<int>(floored);
            PetEmitter.ApplyMinimumShare(twice, -1, PetEmitter.HubMinimumSharePercent);
            check("applying the floor twice changes nothing", twice.SequenceEqual(floored));

            // ---- the excluded hub self-edge ----
            // The hub's own re-selection stays at the baseline on purpose: it is every spoke's RETURN target,
            // so lifting it makes the pet loiter on the hub instead of getting on with the next action.
            var withHub = new List<int> { PetEmitter.HubBaseWeight, 60, 55, 40, 30, 20, 10, 6 };
            int hubIndex = 0;
            int hubBefore = withHub[hubIndex];
            PetEmitter.ApplyMinimumShare(withHub, hubIndex, PetEmitter.HubMinimumSharePercent);
            check("the excluded hub edge is left at its baseline", withHub[hubIndex] == hubBefore);
            check("every other edge still reached the floor",
                !withHub.Where((w, i) => i != hubIndex && w * 100.0 / withHub.Sum() < PetEmitter.HubMinimumSharePercent - 0.01).Any());

            // ---- degenerate inputs must not hang or throw ----
            PetEmitter.ApplyMinimumShare(null, -1, PetEmitter.HubMinimumSharePercent);
            var empty = new List<int>();
            PetEmitter.ApplyMinimumShare(empty, -1, PetEmitter.HubMinimumSharePercent);
            var zeros = new List<int> { 0, 0, 0 };
            PetEmitter.ApplyMinimumShare(zeros, -1, PetEmitter.HubMinimumSharePercent);
            check("null, empty and all-zero weight sets are handled", true);

            // ---- THE FLOOR MUST NOT EAT THE ORDERING ----
            // This used to assert only that a large set TERMINATED, and called the resulting even split
            // "the only sane answer". Terminating is not the job. A flat 1.5% per spoke costs
            // spokeCount * 1.5%, so at 63 spokes it demands 94.5% of the pool and the frequency curve is
            // left with nothing to say -- which is how uzi, the most expressively animated skin in the
            // library, ended up with 11.1% of its hub weight on locomotion against 30-37% for the small
            // skins, and stopped reaching the wall and ceiling animations it ships art for.
            //
            // So the property under test is that a busy action stays BUSY at any spoke count.
            var budgeted = new List<int>();
            for (int i = 0; i < 63; i++) budgeted.Add(PetEmitter.HubWeightFromFrequency(i < 5 ? 1100 : 0));
            var beforeFloor = new List<int>(budgeted);
            PetEmitter.ApplyMinimumShare(budgeted, -1, PetEmitter.HubMinimumSharePercent);
            double bTotal = budgeted.Sum();
            double busyShare = budgeted.Take(5).Sum() * 100.0 / bTotal;
            check("at 63 spokes the floor leaves the busy actions clearly ahead",
                budgeted[0] >= budgeted[62] * 3);
            check("...and they still hold a meaningful share of the pool", busyShare >= 20.0);
            // The budget is a TARGET, not an identity, and asserting equality here fails: `need` is an
            // integer ceiling of a fraction of the pool, so at 63 spokes a 0.556% target lands on 0.617%
            // and the tail consumes 38.3% rather than 35%. Rounding up is the right direction (a spoke
            // must never round to zero), so the property worth pinning is the one the budget exists to
            // buy: most of the pool is still free for the frequency curve to use. It was 5.5% free before.
            double floorConsumes = (budgeted.Count - 1) * (budgeted.Min() * 100.0 / bTotal);
            check("...while leaving most of the pool free for the frequency curve",
                floorConsumes <= 45.0);
            check("the floor still only raises weights at 63 spokes",
                !budgeted.Where((w, i) => w < beforeFloor[i]).Any());
            check("every spoke is still reachable, just on a smaller floor",
                budgeted.Min() > 0);

            // The budget is per-pool, so a small hub is untouched and needs no republish.
            check("at or below the crossover the requested floor is unchanged",
                Math.Abs(PetEmitter.EffectiveMinimumSharePercent(20, PetEmitter.HubMinimumSharePercent)
                         - PetEmitter.HubMinimumSharePercent) < 1e-9);
            check("above the crossover the floor shrinks instead of the ordering dying",
                PetEmitter.EffectiveMinimumSharePercent(63, PetEmitter.HubMinimumSharePercent)
                    < PetEmitter.HubMinimumSharePercent);

            // A set so large the budget itself is thin must still terminate rather than spin.
            var tooMany = new List<int>();
            for (int i = 0; i < 400; i++) tooMany.Add(i + 1);
            PetEmitter.ApplyMinimumShare(tooMany, -1, PetEmitter.HubMinimumSharePercent);
            check("an enormous spoke set terminates", tooMany.Count == 400 && tooMany.Min() > 0);

            check("the version marker advanced past the flat-weight one",
                !string.Equals(PetEmitter.ConvertedFormatVersion, PetEmitter.ConvertedFormatVersionFlatWeights, StringComparison.Ordinal));

            detail = sb.ToString().TrimEnd();
            return ok;
        }

    }
}
