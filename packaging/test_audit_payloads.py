"""Executable standard sources retain their data bytes on every platform."""

from __future__ import annotations

import hashlib
import tempfile
import unittest
import zipfile
from pathlib import Path

from audit_payloads import ROOT, audit_native


class PayloadSourceTests(unittest.TestCase):
    def archive(self, directory: Path, payload: dict[str, bytes]) -> Path:
        manifest: bytes = "".join(
            f"{hashlib.sha256(data).hexdigest()}  {name}\n"
            for name, data in sorted(payload.items())
        ).encode()
        archive: Path = directory / "payload.zip"
        with zipfile.ZipFile(archive, "w") as output:
            for name, data in {**payload, "manifest.sha256": manifest}.items():
                output.writestr("msync-0.8.0-windows-x86_64/" + name, data)
        return archive

    def test_windows_standard_literal_is_byte_exact(self) -> None:
        temporary: Path = ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            directory: Path = Path(name)
            authored: bytes = b'let newline = "\n";\n'
            source: dict[str, bytes] = {"stdlib/text.msync": authored}
            payload: dict[str, bytes] = {
                "bin/msync.exe": b"native image",
                "share/msync/std/text.msync": authored,
            }
            self.assertEqual(
                audit_native(self.archive(directory, payload), source, True)[
                    "stdlib_sources"
                ],
                1,
            )
            payload["share/msync/std/text.msync"] = authored.replace(b"\n", b"\r\n")
            with self.assertRaisesRegex(AssertionError, "authored bytes"):
                audit_native(self.archive(directory, payload), source, True)

    def test_windows_vendored_prose_retains_normal_checkout_semantics(self) -> None:
        temporary: Path = ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            source: dict[str, bytes] = {"vendor/PrettyPrompt/README.md": b"upstream\n"}
            payload: dict[str, bytes] = {
                "bin/msync.exe": b"native image",
                "share/doc/msync/native-dependencies/prettyprompt-6.0.5/source/README.md": b"upstream\r\n",
            }
            self.assertEqual(
                audit_native(self.archive(Path(name), payload), source, True)[
                    "crlf_variants"
                ],
                1,
            )


if __name__ == "__main__":
    unittest.main()
