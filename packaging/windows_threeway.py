#!/usr/bin/env python3
"""Measure selected Windows journeys using recorded Rust and C# controls."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import TypedDict, cast

ROOT = Path(__file__).resolve().parents[1]
RUST_RUN = 42471357057
RUST_ARTIFACT = "native-win-x64-3a1275da6d2ca4cb589ea61c3e4af49ab25d8541"
RUST_RELATIVE = (
    ".temp/platform-results/fixture-1794133769143024300/images/rust/msync.exe"
)
RUST_SHA256 = "ccdfa5ef1dc3655147241ea55ad3cfd1197c1bc2e1463ac4e49434666aa83c05"
CONTROL_RUN = 42506951530
CONTROL_SOURCE = "2b434542e0bd0cc836df07a73386949de36ef41f"
CONTROL_SHA256 = "f255f77212e6437503b4358e13a73f1b2ff30c88910ae91bd515f08f22925502"


class ArtifactRecord(TypedDict):
    name: str
    expired: bool


class ArtifactList(TypedDict):
    artifacts: list[ArtifactRecord]


def execute(arguments: list[str]) -> None:
    subprocess.run(arguments, cwd=ROOT, check=True)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    if os.name != "nt":
        parser.error("This comparison requires an actual native Windows host")
    output = arguments.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    repository = os.environ["GITHUB_REPOSITORY"]
    response = subprocess.check_output(
        ["gh", "api", f"repos/{repository}/actions/runs/{RUST_RUN}/artifacts"],
        cwd=ROOT,
        text=True,
        encoding="utf-8",
    )
    listing = cast(ArtifactList, json.loads(response))
    available = any(
        record["name"] == RUST_ARTIFACT and not record["expired"]
        for record in listing["artifacts"]
    )
    if not available:
        status: dict[str, object] = {
            "status": "unavailable",
            "reason": "The recorded Windows Rust artifact is absent or expired",
            "rust_run": RUST_RUN,
            "rust_sha256": RUST_SHA256,
        }
        output.write_text(json.dumps(status, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(status))
        return
    baselines = ROOT / "artifacts/state-baselines"
    rust_directory = baselines / "rust-artifact"
    execute(
        [
            "gh",
            "run",
            "download",
            str(RUST_RUN),
            "--repo",
            repository,
            "--name",
            RUST_ARTIFACT,
            "--dir",
            str(rust_directory),
        ]
    )
    rust = rust_directory / RUST_RELATIVE
    assert digest(rust) == RUST_SHA256, "Recorded Rust control bytes changed"
    control_directory = baselines / "control"
    execute(
        [
            sys.executable,
            "packaging/performance_baseline.py",
            "--run",
            str(CONTROL_RUN),
            "--rid",
            "win-x64",
            "--output",
            str(control_directory),
        ]
    )
    control: dict[str, object] = json.loads(
        (control_directory / "baseline.json").read_text(encoding="utf-8")
    )
    assert str(control["artifact_name"]).endswith(CONTROL_SOURCE)
    assert control["binary_sha256"] == CONTROL_SHA256
    origin: dict[str, object] = {
        "rust_run": RUST_RUN,
        "rust_artifact": RUST_ARTIFACT,
        "rust_sha256": RUST_SHA256,
        "control": control,
        "candidate_sha256": digest(arguments.binary.resolve()),
    }
    output.with_name("windows-threeway-origins.json").write_text(
        json.dumps(origin, indent=2) + "\n", encoding="utf-8"
    )
    command = [
        sys.executable,
        "tests/Performance/journeys.py",
        "--rust",
        str(rust),
        "--control-aot",
        str(control["binary"]),
        "--aot",
        str(arguments.binary.resolve()),
    ]
    for case in (
        "check-values",
        "values-source",
        "values-object",
        "values-bound",
        "copy-file-source",
    ):
        command.extend(["--case", case])
    execute(
        [
            *command,
            "--files",
            "1",
            "--bytes",
            "4096",
            "--file-bytes",
            "4096",
            "--statements",
            "128",
            "--samples",
            "9",
            "--warmups",
            "3",
            "--output",
            str(output),
        ]
    )


if __name__ == "__main__":
    main()
