using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DesktopAICompanion.Shimeji;

namespace DesktopAICompanion
{
    /// <summary>
    /// policy:birth-cap's write-recorded limit and its birth decision (signed revision H :158, review readings K3 and K9):
    /// TickWorld.MaximumCompanions records every write after construction in a lossless run-length record, and Breed's
    /// loop decides each birth from one room whose operands are read once at the comparison. Each check is its own group,
    /// "Tick limit writes: ..." or "Tick birth decision: ...", so a mutation case names the one check its mutant must fail.
    /// </summary>
    internal static partial class Program
    {
        private static void RunTickPolicyGroups()
        {
            Run("Tick limit writes: none after construction", TestTickLimitWritesNone);
            Run("Tick limit writes: an equal-value 16 write is recorded", TestTickLimitWritesEqualValue);
            Run("Tick limit writes: run-length count 3 in one entry", TestTickLimitWritesRunLength);
            Run("Tick limit writes: order across value and writer changes", TestTickLimitWritesOrder);
            Run("Tick limit writes: explicit writer base 18 deduction 2", TestTickLimitWritesExplicitWriter);
            Run("Tick limit writes: an opaque writer marks base and deduction unavailable", TestTickLimitWritesOpaqueWriter);
            Run("Tick limit writes: a write before Start survives Start", TestTickLimitWritesSurviveStart);
            Run("Tick birth decision: operands equal the private counts", TestTickBirthDecisionOperands);
        }

        private static TickWorld TickBareWorld()
        {
            return new TickWorld(new TickDesk(), new TickTape(), new TickSwitches(), null);
        }

        /// <summary>The record as one line: "value writer @first xcount" per run, runs joined by "|".</summary>
        private static string TickLimitRuns(TickWorld world)
        {
            var text = new StringBuilder();
            foreach (TickLimitWrite run in world.LimitWrites.Runs)
            {
                if (text.Length > 0) text.Append('|');
                text.Append(run.Value.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(run.Writer.Name)
                    .Append(" @").Append(run.FirstWrite.ToString(CultureInfo.InvariantCulture))
                    .Append(" x").Append(run.Count.ToString(CultureInfo.InvariantCulture));
            }
            return text.Length == 0 ? "(none)" : text.ToString();
        }

        private static void TestTickLimitWritesNone()
        {
            TickWorld world = TickBareWorld();
            AssertEqual(16, world.MaximumCompanions, "The construction default is not 16.");
            AssertEqual(0L, world.LimitWrites.Count, "The construction default was recorded as a write; H :158 exempts it: " + TickLimitRuns(world));
            AssertEqual("(none)", TickLimitRuns(world), "A world nothing wrote holds a write run.");
            world.Start();
            AssertEqual(0L, world.LimitWrites.Count, "Start recorded a limit write: " + TickLimitRuns(world));
            // WITNESS: the same record says yes to one write, so "zero writes" is read from a record that can say otherwise.
            world.MaximumCompanions = 12;
            AssertEqual("12 direct @0 x1", TickLimitRuns(world), "WITNESS: one write after Start was not recorded.");
        }

        private static void TestTickLimitWritesEqualValue()
        {
            TickWorld world = TickBareWorld();
            world.MaximumCompanions = 16;
            AssertEqual(1L, world.LimitWrites.Count, "Writing 16 over the default 16 was not recorded (an equality short-circuit; H :158).");
            AssertEqual("16 direct @0 x1", TickLimitRuns(world), "The equal-value write's run is wrong.");
            AssertTrue(ReferenceEquals(TickLimitWriter.Direct, world.LimitWrites.Runs[0].Writer), "A plain assignment did not record the direct writer.");
        }

        private static void TestTickLimitWritesRunLength()
        {
            TickWorld world = TickBareWorld();
            // A fresh writer object each time, equal in every field: the run folds by value, not by reference.
            for (int i = 0; i < 3; i++) world.SetMaximumCompanions(12, TickLimitWriter.Opaque("budget", "a steady budget"));
            AssertEqual(3L, world.LimitWrites.Count, "Three writes did not count 3.");
            AssertEqual("12 budget @0 x3", TickLimitRuns(world), "Three equal writes by one writer are not one run counting 3.");
            AssertEqual(12, world.MaximumCompanions, "The written value was not applied.");
        }

        private static void TestTickLimitWritesOrder()
        {
            TickWorld world = TickBareWorld();
            TickLimitWriter budget = TickLimitWriter.Opaque("budget", "a steady budget");
            world.MaximumCompanions = 16;
            world.MaximumCompanions = 16;
            world.MaximumCompanions = 12;
            world.SetMaximumCompanions(12, budget);
            world.SetMaximumCompanions(12, budget);
            world.MaximumCompanions = 12;
            world.SetMaximumCompanions(16, TickLimitWriter.WithParts("budget", "a steady budget", 18, 2));
            AssertEqual(7L, world.LimitWrites.Count, "Seven writes did not count 7: " + TickLimitRuns(world));
            // A change of value, or of any writer field, starts a run; the same value by another writer is not folded.
            AssertEqual("16 direct @0 x2|12 direct @2 x1|12 budget @3 x2|12 direct @5 x1|16 budget @6 x1", TickLimitRuns(world),
                "The runs lost a write, its order, its value or its writer.");
            AssertTrue(world.LimitWrites.Runs[4].Writer.PartsKnown && !world.LimitWrites.Runs[2].Writer.PartsKnown,
                "The last run did not keep the writer that exposed its parts apart from the opaque writer of the same name.");
            AssertEqual(16, world.MaximumCompanions, "The last write was not the one applied.");
        }

        private static void TestTickLimitWritesExplicitWriter()
        {
            TickWorld world = TickBareWorld();
            world.SetMaximumCompanions(18 - 2, TickLimitWriter.WithParts("budget writer", "an installed budget", 18, 2));
            AssertEqual("16 budget writer @0 x1", TickLimitRuns(world), "The explicit writer's write was not recorded as written.");
            TickLimitWriter writer = world.LimitWrites.Runs[0].Writer;
            AssertTrue(writer.PartsKnown && writer.Base == 18 && writer.Deduction == 2,
                "The explicit writer's base and deduction were not recorded: " + writer);
            AssertEqual("an installed budget", writer.Context, "The explicit writer's context was not recorded.");
        }

        private static void TestTickLimitWritesOpaqueWriter()
        {
            TickWorld world = TickBareWorld();
            world.SetMaximumCompanions(14, TickLimitWriter.CompanionBudget);
            TickLimitWriter host = world.LimitWrites.Runs[0].Writer;
            AssertTrue(!host.PartsKnown && host.Base == 0 && host.Deduction == 0,
                "TickHost's CompanionBudget writer did not mark base and deduction unavailable: " + host);
            AssertEqual("TickHost.CompanionBudget", host.Name, "TickHost's writer is not named for CompanionBudget.");
            TickLimitWriter opaque = TickLimitWriter.Opaque("provider", "a provider object");
            AssertTrue(!opaque.PartsKnown, "An opaque writer claimed a base and deduction: " + opaque);
            world.SetMaximumCompanions(14, null);
            world.MaximumCompanions = 14;
            AssertEqual("14 TickHost.CompanionBudget @0 x1|14 direct @1 x2", TickLimitRuns(world),
                "A null writer or a plain assignment was not recorded as the direct writer.");
            AssertTrue(!TickLimitWriter.Direct.PartsKnown, "The direct writer claimed a base and deduction.");
        }

        private static void TestTickLimitWritesSurviveStart()
        {
            TickWorld world = TickBareWorld();
            world.MaximumCompanions = 12;
            world.Start();
            AssertEqual(1L, world.LimitWrites.Count, "Start cleared the write record's count: " + TickLimitRuns(world));
            AssertEqual("12 direct @0 x1", TickLimitRuns(world), "A write made before Start did not survive Start.");
            AssertEqual(12, world.MaximumCompanions, "Start reset the written limit.");
        }

        private static void TestTickBirthDecisionOperands()
        {
            // Breed with BornCount 3 at a cap of 3: m1 is active, so attempt 1 compares 1 + 0, attempt 2 compares 1 + 1
            // (the first birth is pending), and attempt 3 compares 1 + 2 and is refused.
            TickType alpha = TickType.Compile("Alpha", TickDoc(
                TRoute(TEdge(10, 1), TEdge(11, 0)),
                TBeh(10, "Throw", "Throw", 1, null),
                TBeh(11, "Stand", "Stand", 0, null),
                TAct(30, "Throw", "Embedded", "Breed",
                    new[] { TParam("BornX", "40"), TParam("BornY", "0"), TParam("BornMascot", "Alpha"), TParam("BornBehavior", "Stand"), TParam("BornCount", "3") },
                    TPose(0, 1), TPose(1, 1)),
                TAct(31, "Stand", "Stay", null, new[] { TParam("Duration", "50") }, TPose(0, 1))));
            var log = new TickLog();
            TickWorld world = TickWorldOf(log, null, alpha);
            world.MaximumCompanions = 3;
            var seen = new List<string>();
            var mismatches = new List<string>();
            world.BirthDecided = decision =>
            {
                // Read at the hook, which runs after the comparison and before the birth it decides: the private lists
                // still hold exactly what the comparison read.
                TickMembership members = world.SnapshotMembership();
                TickBirthRoom room = decision.Room;
                if (room.Limit != world.MaximumCompanions || room.Active != members.Active.Length || room.Pending != members.Pending.Length)
                    mismatches.Add("attempt " + decision.Attempt + ": room " + room.Limit + " vs " + room.Active + "+" + room.Pending
                        + ", private " + world.MaximumCompanions + " vs " + members.Active.Length + "+" + members.Pending.Length);
                seen.Add("t" + decision.Tick + " " + decision.ParentSid + " #" + decision.Attempt + " " + room.Limit + " vs "
                    + room.Active + "+" + room.Pending + (room.Admits ? " admit" : " refuse"));
            };
            world.CreateAtStart("m1", alpha, new TickPoint(640, 680), false, "Throw");
            world.StepOnce();
            world.StepOnce();
            AssertEqual(3, seen.Count, "Breed's loop did not hand the hook one decision per attempt: " + string.Join("; ", seen));
            AssertEqual("", string.Join("; ", mismatches), "A decision's operands are not the private counts at the comparison.");
            AssertEqual("t2 m1 #1 3 vs 1+0 admit|t2 m1 #2 3 vs 1+1 admit|t2 m1 #3 3 vs 1+2 refuse", string.Join("|", seen),
                "The decisions' identity, operands or results are not the comparison's.");
            AssertTrue(log.Events.Contains("m1 refused try3 cap") && world.Pending.Count == 2,
                "The refused decision did not refuse the birth: " + string.Join("; ", log.Events));
        }
    }
}
