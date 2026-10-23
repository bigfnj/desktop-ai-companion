"""Pure case-plan witnesses; no product input, builds, or target-file access."""

import argparse
import json
from pathlib import Path
import unittest
from unittest.mock import patch

from mutation_caseplan import CasePlanError, add_selection_arguments, make_plan


COMMIT = "a" * 40
ROOT = Path(__file__).resolve().parents[1]


def inventory(root=ROOT, count=37):
    return tuple(("synthetic case %02d" % index, str(root / "source" / ("case%d.cs" % index)),
                  ("old%d\n" % index).encode("utf-8"), ("new%d\n" % index).encode("utf-8"),
                  "--synthetic-probe") for index in range(count))


class CasePlanTests(unittest.TestCase):
    def test_complete_union_is_disjoint_and_balanced_for_supported_shard_counts(self):
        cases = inventory()
        whole = make_plan(cases, ROOT, COMMIT)
        for count in (1, 2, 8, 12, len(cases)):
            with self.subTest(count=count):
                plans = [make_plan(cases, ROOT, COMMIT, shard="%d/%d" % (index, count))
                         for index in range(1, count + 1)]
                indices = [index for plan in plans for index in plan.indices]
                self.assertEqual(len(indices), len(cases))
                self.assertEqual(len(set(indices)), len(cases))
                self.assertEqual(sorted(indices), list(whole.indices))
                self.assertEqual({plan.manifest["inventorySha256"] for plan in plans},
                                 {whole.manifest["inventorySha256"]})
                self.assertLessEqual(max(len(plan.cases) for plan in plans) - min(len(plan.cases) for plan in plans), 1)

    def test_inventory_digest_is_stable_across_isolated_worktree_paths(self):
        other = ROOT / "synthetic-other-worktree"
        first = make_plan(inventory(ROOT), ROOT, COMMIT)
        second = make_plan(inventory(other), other, COMMIT)
        self.assertEqual(first.manifest["inventorySha256"], second.manifest["inventorySha256"])

    def test_definition_or_order_change_changes_inventory_digest(self):
        cases = inventory()
        whole = make_plan(cases, ROOT, COMMIT)
        changed = list(cases)
        changed[0] = tuple(b"different mutation" if index == 3 else value
                           for index, value in enumerate(changed[0]))
        self.assertNotEqual(whole.manifest["inventorySha256"],
                            make_plan(changed, ROOT, COMMIT).manifest["inventorySha256"])
        self.assertNotEqual(whole.manifest["inventorySha256"],
                            make_plan(cases[::-1], ROOT, COMMIT).manifest["inventorySha256"])

    def test_shard_repeated_plan_is_stable_and_global_indices_are_emitted(self):
        first = make_plan(inventory(), ROOT, COMMIT, shard="2/8")
        second = make_plan(inventory(), ROOT, COMMIT, shard="2/8")
        self.assertEqual(first.json(), second.json())
        self.assertEqual(first.indices, (2, 10, 18, 26, 34))
        self.assertEqual([case["index"] for case in json.loads(first.json())["cases"]], list(first.indices))
        self.assertEqual(json.loads(first.summary())["selectedIndices"], list(first.indices))
        self.assertEqual(first.manifest["sourceCommit"], COMMIT)

    def test_duplicate_ids_are_rejected_including_case_only_difference(self):
        for names in (("same", "same"), ("same", "SAME")):
            with self.subTest(names=names):
                with self.assertRaisesRegex(CasePlanError, "duplicate case ID"):
                    make_plan(tuple((name, b"mutation") for name in names), ROOT, COMMIT)

    def test_invalid_shards_and_empty_selections_are_refused(self):
        for shard in ("0/8", "1/0", "9/8", "1/38", "1/2/3", "1", "x/2", " 1/2", "01/2", "-1/2", "1/" + "9" * 40):
            with self.subTest(shard=shard):
                with self.assertRaises(CasePlanError):
                    make_plan(inventory(), ROOT, COMMIT, shard=shard)
        for only in ("", " ", "not a real case"):
            with self.subTest(only=only):
                with self.assertRaises(CasePlanError):
                    make_plan(inventory(), ROOT, COMMIT, only=only)

    def test_empty_or_invalid_inventory_and_unverified_commit_are_refused(self):
        for cases in ((), (("", b"x"),), ((None, b"x"),)):
            with self.subTest(cases=cases):
                with self.assertRaises(CasePlanError):
                    make_plan(cases, ROOT, COMMIT)
        with self.assertRaises(CasePlanError):
            make_plan(inventory(), ROOT, "not-a-commit")

    def test_only_is_case_insensitive_and_cannot_combine_with_shard(self):
        plan = make_plan(inventory(), ROOT, COMMIT, only="CASE 02")
        self.assertEqual(plan.indices, (3,))
        with self.assertRaises(CasePlanError):
            make_plan(inventory(), ROOT, COMMIT, only="case", shard="1/2")
        parser = argparse.ArgumentParser()
        add_selection_arguments(parser)
        with patch("sys.stderr"):
            with self.assertRaises(SystemExit) as error:
                parser.parse_args(["--only=case", "--shard=1/2"])
        self.assertEqual(error.exception.code, 2)

    def test_selection_validation_never_opens_or_changes_target_files(self):
        cases = inventory()
        with patch("builtins.open", side_effect=AssertionError("no target read/write")):
            with patch("pathlib.Path.read_bytes", side_effect=AssertionError("no target read")):
                make_plan(cases, ROOT, COMMIT, shard="1/8")
                with self.assertRaises(CasePlanError):
                    make_plan(cases, ROOT, COMMIT, shard="0/8")


if __name__ == "__main__":
    unittest.main(verbosity=2)
