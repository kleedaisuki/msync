from __future__ import annotations

import random
import unittest
from typing import Any

from interactive import apply_delta, check_numeric_tokens


class SemanticTokenOracleTests(unittest.TestCase):
    def test_delta_matches_independent_splicing(self) -> None:
        randomizer = random.Random(1703)
        for _ in range(256):
            previous = [randomizer.randrange(1000) for _ in range(24)]
            prefix = [randomizer.randrange(1000) for _ in range(3)]
            suffix = [randomizer.randrange(1000) for _ in range(2)]
            delta: dict[str, Any] = {
                "edits": [
                    {"start": 2, "deleteCount": 4, "data": prefix},
                    {"start": 18, "deleteCount": 3, "data": suffix},
                ]
            }
            self.assertEqual(
                apply_delta(previous, delta),
                previous[:2] + prefix + previous[6:18] + suffix + previous[21:],
            )
            self.assertEqual(len(previous), 24)

    def test_empty_delta_and_full_fallback(self) -> None:
        self.assertEqual(apply_delta([1, 2, 3], {"edits": []}), [1, 2, 3])
        self.assertEqual(apply_delta([1], {"data": [4, 5]}), [4, 5])
        self.assertEqual(
            apply_delta([1, 2], {"edits": [{"start": 0, "deleteCount": 2}]}), []
        )

    def test_numeric_oracle_rejects_stale_tokens(self) -> None:
        legend = ["keyword", "variable", "operator", "number"]
        tokens = [0, 0, 3, 0, 0, 0, 4, 6, 1, 0, 0, 7, 1, 2, 0, 0, 2, 6, 3, 0]
        check_numeric_tokens(tokens, legend, 1, 0, "999999")
        with self.assertRaises(AssertionError):
            check_numeric_tokens(tokens, legend, 1, 0, "0")


if __name__ == "__main__":
    unittest.main()
