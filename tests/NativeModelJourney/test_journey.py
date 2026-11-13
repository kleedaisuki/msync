"""Check representation evidence cannot be confused with semantic-only success."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/Msync.EndToEnd"))

import native_model_journey as journey


class StructuralEvidenceTests(unittest.TestCase):
    def test_old_artifact_is_only_a_semantic_baseline(self) -> None:
        old: dict[str, Any] = {"text": "  0000: NewScope @l0\n  0001: Return\n"}
        result: dict[str, Any] = journey.inspect_native_ir(old, False)
        self.assertFalse(result["structurally_accepted"])
        self.assertEqual(result["legacy_object_operations"], ["NewScope"])
        with self.assertRaises(AssertionError):
            journey.inspect_native_ir(old, True)

    def test_marker_without_operations_is_not_evidence(self) -> None:
        with self.assertRaises(AssertionError):
            journey.inspect_native_ir({"representation": "native-v1"}, True)

    def test_structured_native_instructions_are_accepted(self) -> None:
        native: dict[str, Any] = {
            "representation": "native-v1",
            "functions": [
                {
                    "instructions": [
                        {"op": "Allocate"},
                        {"op": "Store"},
                        {"op": "Return"},
                    ]
                }
            ],
        }
        self.assertTrue(
            journey.inspect_native_ir(native, True)["structurally_accepted"]
        )

    def test_every_old_object_operation_is_rejected(self) -> None:
        for operation in journey.OLD_OBJECT_OPERATIONS:
            with self.subTest(operation=operation), self.assertRaises(AssertionError):
                journey.inspect_native_ir(
                    {
                        "representation": "native-v1",
                        "functions": [{"instructions": [{"op": operation}]}],
                    },
                    True,
                )

    def test_allocator_carriers_are_rejected(self) -> None:
        for declaration in (
            "type @t0 = Storage;",
            'const @c0: Function = native("arena.allocate");',
        ):
            with (
                self.subTest(declaration=declaration),
                self.assertRaises(AssertionError),
            ):
                journey.inspect_native_ir(
                    {
                        "representation": "native-v1",
                        "text": declaration + "\n  0000: Return\n",
                    },
                    True,
                )

    def test_all_builtins_and_runtime_effects_have_fixtures(self) -> None:
        cases: tuple[journey.Fixture, ...] = journey.fixtures()
        self.assertEqual(len(cases), 8)
        self.assertIn("Process", journey.BUILTINS)
        self.assertIn("Connection", journey.BUILTINS)
        self.assertEqual(
            next(case for case in cases if case.name == "lazy-stdin-demand").stdin,
            b"3\n7\n10\nnot-a-number\n",
        )
        child: journey.Fixture = next(
            case for case in cases if case.name == "process-return-and-suspension"
        )
        self.assertEqual(child.outputs, {"child-count.txt": b"1"})


if __name__ == "__main__":
    unittest.main()
