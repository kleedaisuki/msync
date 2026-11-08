"""Focused release-container tests; executable behavior is in smoke.py."""

from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import build


class PackageTests(unittest.TestCase):
    def test_version_requires_editor_agreement(self) -> None:
        temporary = build.ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            root = Path(name)
            (root / "Directory.Build.props").write_text(
                "<Project><PropertyGroup><Version>0.8.0</Version></PropertyGroup></Project>",
                encoding="utf-8",
            )
            (root / "editors/vscode").mkdir(parents=True)
            manifest = root / "editors/vscode/package.json"
            manifest.write_text(json.dumps({"version": "0.8.0"}), encoding="utf-8")
            self.assertEqual(build.version(root), "0.8.0")
            manifest.write_text(json.dumps({"version": "0.7.3"}), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "does not match"):
                build.version(root)

    def test_archives_are_repeatable_and_keep_payload_paths(self) -> None:
        temporary = build.ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            root = Path(name)
            stage = root / "msync-0.8.0-linux-x86_64"
            (stage / "bin").mkdir(parents=True)
            (stage / "bin/msync").write_bytes(b"native payload")
            output = root / "dist"
            output.mkdir()
            for rid in build.RID_NAMES:
                first = build.archive_payload(
                    stage, output, rid, 1700000000
                ).read_bytes()
                second = build.archive_payload(
                    stage, output, rid, 1700000000
                ).read_bytes()
                self.assertEqual(first, second)
            files = sorted(output.iterdir())
            build.write_checksums(files, output / "SHA256SUMS")
            lines = (output / "SHA256SUMS").read_text().splitlines()
            self.assertEqual(len(lines), 2)
            for file, line in zip(files, lines, strict=True):
                self.assertEqual(
                    line,
                    f"{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.name}",
                )


if __name__ == "__main__":
    unittest.main()
