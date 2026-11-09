#!/usr/bin/env python3
"""Freeze two product snapshots; apply only the experimental call-target patch."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import shutil
import subprocess
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PRODUCT = ("src", "stdlib", "vendor", "Directory.Build.props", "global.json")
INPUTS = (
    "AdaptiveDispatch.csproj",
    "Program.cs",
    "packages.lock.json",
    "packages.linux-x64.lock.json",
)


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def prepare(ref: str, destination: Path) -> None:
    archive = subprocess.run(
        ["git", "archive", ref, *PRODUCT], cwd=ROOT, capture_output=True, check=True
    ).stdout
    revision = subprocess.run(
        ["git", "rev-parse", ref], cwd=ROOT, capture_output=True, check=True, text=True
    ).stdout.strip()
    destination.mkdir(parents=True, exist_ok=False)
    patch = Path(__file__).with_name("no-clone.patch").read_bytes()
    for name in ("control", "candidate"):
        source = destination / name
        source.mkdir()
        with tarfile.open(fileobj=io.BytesIO(archive)) as files:
            files.extractall(source, filter="data")
        probe = source / "tests/Performance/AdaptiveDispatch"
        probe.mkdir(parents=True)
        for filename in INPUTS:
            shutil.copy2(Path(__file__).with_name(filename), probe / filename)
        if name == "candidate":
            subprocess.run(
                ["git", "apply", "--unidiff-zero", "-"],
                cwd=source,
                input=patch,
                check=True,
            )
    manifest = {
        "product_revision": revision,
        "archive_sha256": digest(archive),
        "patch_sha256": digest(patch),
        "probe_sha256": {
            filename: digest(Path(__file__).with_name(filename).read_bytes())
            for filename in INPUTS
        },
        "variants": {
            name: str(destination / name) for name in ("control", "candidate")
        },
    }
    (destination / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", required=True)
    parser.add_argument("--destination", required=True, type=Path)
    options = parser.parse_args()
    prepare(options.ref, options.destination.resolve())


if __name__ == "__main__":
    main()
