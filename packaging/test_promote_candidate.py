"""An accepted candidate is published as the same bytes, not rebuilt outputs."""

from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from typing import Any
from unittest.mock import patch

from promote_candidate import ARTIFACTS, ROOT, stage, validate_candidate, verify_payload


class CandidateTests(unittest.TestCase):
    def candidate(
        self,
    ) -> tuple[dict[str, Any], list[dict[str, Any]], list[dict[str, Any]]]:
        run: dict[str, Any] = {
            "head_sha": "tested",
            "status": "completed",
            "conclusion": "success",
            "path": ".github/workflows/release.yml",
        }
        jobs: list[dict[str, Any]] = [
            {
                "name": "native (ubuntu-22.04, linux-x64, linux-x86_64)",
                "conclusion": "success",
            },
            {
                "name": "native (windows-latest, win-x64, windows-x86_64)",
                "conclusion": "success",
            },
            {"name": "editor", "conclusion": "success"},
        ]
        artifacts: list[dict[str, Any]] = [
            {"name": name, "expired": False} for name in ARTIFACTS
        ]
        return run, jobs, artifacts

    def test_successful_matching_candidate_is_selected(self) -> None:
        run, jobs, artifacts = self.candidate()
        validate_candidate(run, jobs, artifacts, "tested")

    def test_wrong_source_or_incomplete_acceptance_is_not_promoted(self) -> None:
        run, jobs, artifacts = self.candidate()
        with self.assertRaisesRegex(ValueError, "current main source"):
            validate_candidate(run, jobs, artifacts, "different")
        jobs[1]["conclusion"] = "failure"
        with self.assertRaisesRegex(ValueError, "acceptance did not succeed"):
            validate_candidate(run, jobs, artifacts, "tested")

    def test_missing_or_expired_payload_is_not_promoted(self) -> None:
        run, jobs, artifacts = self.candidate()
        artifacts.pop()
        with self.assertRaisesRegex(ValueError, "artifacts are missing"):
            validate_candidate(run, jobs, artifacts, "tested")
        run, jobs, artifacts = self.candidate()
        artifacts[0]["expired"] = True
        with self.assertRaisesRegex(ValueError, "artifacts are missing"):
            validate_candidate(run, jobs, artifacts, "tested")

    def test_matching_receipts_stage_exact_payload_bytes(self) -> None:
        temporary: Path = ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            root: Path = Path(name)
            names: dict[str, tuple[str, ...]] = {
                "linux-x86_64": ("msync-0.8.1-linux-x86_64.tar.gz",),
                "windows-x86_64": (
                    "msync-0.8.1-windows-x86_64.zip",
                    "msync-0.8.1-windows-x86_64-setup.exe",
                ),
                "vscode": ("msync-language-0.8.1.vsix",),
            }
            originals: dict[str, bytes] = {}
            for platform, files in names.items():
                payloads: Path = root / "download" / f"release-{platform}"
                payloads.mkdir(parents=True)
                recorded: list[dict[str, object]] = []
                members: list[str] = []
                for filename in files:
                    path: Path = payloads / filename
                    if filename.endswith(".vsix"):
                        with zipfile.ZipFile(path, "w") as archive:
                            archive.writestr(
                                "extension/package.json",
                                json.dumps({"version": "0.8.1"}),
                            )
                        members = ["extension/package.json"]
                    else:
                        path.write_bytes(filename.encode())
                    data: bytes = path.read_bytes()
                    originals[filename] = data
                    recorded.append(
                        {
                            "name": filename,
                            "size": len(data),
                            "sha256": hashlib.sha256(data).hexdigest(),
                        }
                    )
                receipts: Path = root / "download" / f"acceptance-{platform}"
                receipts.mkdir()
                (receipts / "payload.json").write_text(
                    json.dumps(
                        {
                            "version": "0.8.1",
                            "source": "tested",
                            "assets": recorded,
                            "members": members,
                        }
                    ),
                    encoding="utf-8",
                )
            with (
                patch("promote_candidate.source_files", return_value={}),
                patch(
                    "promote_candidate.audit_native", return_value={"checked": True}
                ) as native,
            ):
                report = stage(root / "download", root / "dist", "0.8.1", "tested")
            self.assertEqual(native.call_count, 2)
            self.assertEqual(len(report["assets"]), 4)  # type: ignore[arg-type]
            self.assertEqual(
                {path.name: path.read_bytes() for path in (root / "dist").iterdir()},
                originals,
            )
            receipt: dict[str, Any] = json.loads(
                (root / "download/acceptance-vscode/payload.json").read_text()
            )
            with self.assertRaisesRegex(ValueError, "version/source"):
                verify_payload(
                    root / "dist/msync-language-0.8.1.vsix", receipt, "0.8.2", "tested"
                )
            path = root / "dist/msync-language-0.8.1.vsix"
            path.write_bytes(b"another build")
            with self.assertRaisesRegex(ValueError, "differs"):
                verify_payload(path, receipt, "0.8.1", "tested")


if __name__ == "__main__":
    unittest.main()
