"""Check that copy timing cannot be certified by a previous successful output."""

from __future__ import annotations

import os
import shutil
import tempfile
import unittest
from pathlib import Path

import journeys


class CopyObservationTests(unittest.TestCase):
    temporary: tempfile.TemporaryDirectory[str]
    directory: Path

    def setUp(self) -> None:
        (journeys.ROOT / ".temp").mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(
            prefix="benchmark-copy-check-", dir=journeys.ROOT / ".temp"
        )
        self.directory = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_replacement_reset_preserves_source_size_and_requires_new_bytes(
        self,
    ) -> None:
        original: Path = self.directory / "input.bin"
        target: Path = self.directory / "output.bin"
        for size in (0, 1, 7, 16, 33, 4096, 65536):
            with self.subTest(size=size):
                original.write_bytes(bytes(index % 256 for index in range(size)))
                shutil.copy2(original, target)
                journeys.verify("copy-file-source", self.directory, 1)
                journeys.reset_sample("copy-file-source", self.directory, 3)
                self.assertEqual(original.stat().st_size, size)
                self.assertTrue(target.exists())
                with self.assertRaises(AssertionError):
                    journeys.verify("copy-file-source", self.directory, 1)
                shutil.copy2(original, target)
                journeys.verify("copy-file-source", self.directory, 1)

    @unittest.skipUnless(
        os.name == "posix" and shutil.which("true"),
        "real no-op executable requires POSIX true",
    )
    def test_real_noop_cannot_reuse_successful_file_or_tree(self) -> None:
        executable: str | None = shutil.which("true")
        assert executable is not None
        image: journeys.Image = journeys.Image(
            "noop", executable, "not a product", "", 0, {}
        )
        for case in ("copy-file-source", "copy-tree-source"):
            with self.subTest(case=case):
                if case == "copy-file-source":
                    original: Path = self.directory / "input.bin"
                    original.write_bytes(b"previous successful result")
                    shutil.copy2(original, self.directory / "output.bin")
                else:
                    tree: Path = self.directory / "input-tree/sub"
                    tree.mkdir(parents=True)
                    (tree / "one.bin").write_bytes(b"previous successful result")
                    shutil.copytree(
                        self.directory / "input-tree",
                        self.directory / "output-tree/input-tree",
                    )
                journeys.verify(case, self.directory, 1)
                with self.assertRaises(journeys.SamplingFailure) as failure:
                    journeys.sample_case(
                        [image], {"noop": self.directory}, case, 0, 1, 1, 1
                    )
                self.assertIn(
                    "Copied bytes differ", str(failure.exception.details["message"])
                )
                self.assertEqual(
                    failure.exception.details["partial_samples_ms"], {"noop": []}
                )

    def test_tree_oracle_rejects_unexpected_non_bin_file(self) -> None:
        source: Path = self.directory / "input-tree"
        source.mkdir()
        (source / "one.bin").write_bytes(b"payload")
        target: Path = self.directory / "output-tree/input-tree"
        shutil.copytree(source, target)
        (target / "unexpected.txt").write_bytes(b"not in the manifest")
        with self.assertRaises(AssertionError):
            journeys.verify("copy-tree-source", self.directory, 1)


if __name__ == "__main__":
    unittest.main()
