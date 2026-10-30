"""Run with python -B -m unittest discover -s packaging."""
from __future__ import annotations

import hashlib
import tempfile
import unittest
from pathlib import Path

from licenses import Package, license_files, rust_attribution


class LicenseCollectionTests(unittest.TestCase):
    def setUp(self) -> None:
        scratch = Path(__file__).resolve().parent.parent / ".temp"
        scratch.mkdir(exist_ok=True)
        self.workspace = tempfile.TemporaryDirectory(dir=scratch, prefix="license-test-")
        self.addCleanup(self.workspace.cleanup)
        self.root = Path(self.workspace.name)

    def package(self, root: Path, explicit: str | None = None) -> Package:
        return Package(name="fixture", version="1.0.0", manifest_path=str(root / "Cargo.toml"),
                       license="MIT", license_file=explicit, source=None, authors=["fixture author"])

    def test_workspace_fixture_is_not_a_notice(self) -> None:
        (self.root / "LICENSE").write_text("actual license", encoding="utf-8")
        nested = self.root / ".temp"
        nested.mkdir()
        (nested / "LICENSE-unrelated").write_text("not shipped", encoding="utf-8")
        self.assertEqual(license_files(self.package(self.root), self.root), [self.root / "LICENSE"])

    def test_nested_license_and_explicit_grant_are_retained(self) -> None:
        dependency = self.root / "dependency"
        directory = dependency / "licenses"
        directory.mkdir(parents=True)
        (directory / "MIT.txt").write_text("MIT body", encoding="utf-8")
        (dependency / "grant.txt").write_text("explicit grant", encoding="utf-8")
        paths = license_files(self.package(dependency, "grant.txt"), self.root)
        self.assertEqual([path.relative_to(dependency).as_posix() for path in paths],
                         ["grant.txt", "licenses/MIT.txt"])

    def test_spdx_does_not_replace_actual_license_text(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "no actual license/notice text"):
            license_files(self.package(self.root), self.root)
        with self.assertRaisesRegex(RuntimeError, "declared license_file missing"):
            license_files(self.package(self.root, "MISSING"), self.root)

    def test_rust_html_and_license_are_copied_verbatim(self) -> None:
        documents = self.root / "toolchain/share/doc/rust"
        (documents / "licenses").mkdir(parents=True)
        report = b'<a href="licenses/MIT.txt">actual attribution</a>\r\n'
        text = b"MIT body\r\n"
        (documents / "COPYRIGHT-library.html").write_bytes(report)
        (documents / "licenses/MIT.txt").write_bytes(text)
        output = self.root / "output"
        rows = rust_attribution(self.root / "toolchain", output)
        self.assertEqual((output / "COPYRIGHT-library.html").read_bytes(), report)
        self.assertEqual((output / "licenses/MIT.txt").read_bytes(), text)
        self.assertIn(hashlib.sha256(report).hexdigest(), rows[0])
        self.assertEqual(rows, rust_attribution(self.root / "toolchain", output))

    def test_missing_toolchain_report_is_actionable(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "official matching rustc distribution"):
            rust_attribution(self.root / "custom-toolchain", self.root / "output")


if __name__ == "__main__":
    unittest.main()
