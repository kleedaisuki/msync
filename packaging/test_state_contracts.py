"""Selection contracts for native platform acceptance after backend retirement."""

from __future__ import annotations

import unittest

import state_contracts as contracts


class SelectionTests(unittest.TestCase):
    def test_default_contracts_are_required_existing_native_projects(self) -> None:
        selected: tuple[contracts.Contract, ...] = contracts.select(None)
        self.assertEqual(selected, contracts.CONTRACTS)
        self.assertEqual(len({item.name for item in selected}), len(selected))
        for item in selected:
            with self.subTest(contract=item.name):
                self.assertTrue((contracts.ROOT / "tests" / item.project).exists())

    def test_old_selection_intents_are_deduplicated_not_skipped(self) -> None:
        selected: tuple[contracts.Contract, ...] = contracts.select(
            ["cross-block-memory", "global-redundancy", "native-middle-end"]
        )
        self.assertEqual(
            [item.name for item in selected], ["native-optimizer", "native-middle-end"]
        )

    def test_deep_tail_behavior_retains_its_own_project(self) -> None:
        selected: tuple[contracts.Contract, ...] = contracts.select(
            ["tail-call-optimization"]
        )
        self.assertEqual(len(selected), 1)
        self.assertEqual(selected[0].project, "TailCallOptimization")

    def test_stdlib_selection_retains_demand_and_capture_journeys(self) -> None:
        self.assertEqual(
            {item.name for item in contracts.select(["stdlib-loading"])},
            {"stdlib-publication", "stdlib-demand", "stdlib-captures"},
        )

    def test_obsolete_binary_path_selection_retains_codec_and_path_oracles(
        self,
    ) -> None:
        self.assertEqual(
            {item.name for item in contracts.select(["format-one-paths"])},
            {"native-artifact", "current-paths"},
        )


if __name__ == "__main__":
    unittest.main()
