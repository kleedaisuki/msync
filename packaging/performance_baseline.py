#!/usr/bin/env python3
"""Fetch one recorded native CI image for a same-runner before/after comparison."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import tarfile
import zipfile
from pathlib import Path
from typing import Any, cast

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", required=True, type=int)
    parser.add_argument("--rid", required=True, choices=("linux-x64", "win-x64"))
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()
    repository = os.environ["GITHUB_REPOSITORY"]
    result = subprocess.run(
        ["gh", "api", f"repos/{repository}/actions/runs/{args.run}/artifacts"],
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    document = cast(dict[str, Any], json.loads(result.stdout))
    artifacts = [
        record
        for record in document["artifacts"]
        if record["name"].startswith(f"native-{args.rid}-")
    ]
    if len(artifacts) != 1:
        raise ValueError(
            f"Run {args.run} does not identify one recorded {args.rid} artifact: {artifacts}"
        )
    artifact = artifacts[0]
    output = args.output.resolve()
    download = output / "download"
    download.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "gh",
            "run",
            "download",
            str(args.run),
            "--repo",
            repository,
            "--name",
            artifact["name"],
            "--dir",
            str(download),
        ],
        check=True,
        cwd=ROOT,
    )
    suffix = "-windows-x86_64.zip" if args.rid == "win-x64" else "-linux-x86_64.tar.gz"
    packages = list(download.rglob(f"msync-*{suffix}"))
    if len(packages) != 1:
        raise ValueError(
            f"Recorded artifact does not identify one native portable package: {packages}"
        )
    package = packages[0]
    extracted = output / "installed"
    extracted.mkdir(parents=True, exist_ok=True)
    if package.suffix == ".zip":
        with zipfile.ZipFile(package) as archive:
            archive.extractall(extracted)
    else:
        with tarfile.open(package) as archive:
            archive.extractall(extracted, filter="data")
    executable = "msync.exe" if args.rid == "win-x64" else "msync"
    binaries = list(extracted.glob(f"msync-*/bin/{executable}"))
    if len(binaries) != 1:
        raise ValueError(
            f"Recorded package does not identify one native executable: {binaries}"
        )
    binary = binaries[0]
    metadata = {
        "run_id": args.run,
        "artifact_id": artifact["id"],
        "artifact_name": artifact["name"],
        "package_sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
    }
    (output / "baseline.json").write_text(
        json.dumps(metadata, indent=2) + "\n", encoding="utf-8"
    )
    if args.github_output is not None:
        with args.github_output.open("a", encoding="utf-8") as stream:
            stream.write(f"binary={binary}\n")
    print(json.dumps(metadata, indent=2))


if __name__ == "__main__":
    main()
