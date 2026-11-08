#!/usr/bin/env python3
"""Build exact-source Native AOT optimization preferences without editing product settings."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import platform
import subprocess
import sys
import tarfile
import time
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def command(arguments: Sequence[str], cwd: Path, log: Path) -> None:
    print(" ".join(arguments), flush=True)
    with log.open("wb") as output:
        result = subprocess.run(
            arguments, cwd=cwd, stdout=output, stderr=subprocess.STDOUT, check=False
        )
    if result.returncode != 0:
        raise RuntimeError(
            f"Command exited {result.returncode}; full diagnostics: {log}"
        )


def snapshot(revision: str, destination: Path) -> str:
    commit = subprocess.check_output(
        ["git", "rev-parse", revision], cwd=ROOT, text=True
    ).strip()
    payload = subprocess.check_output(
        [
            "git",
            "archive",
            "--format=tar",
            commit,
            "src",
            "stdlib",
            "Directory.Build.props",
            "global.json",
        ],
        cwd=ROOT,
    )
    destination.mkdir(parents=True)
    with tarfile.open(fileobj=io.BytesIO(payload)) as archive:
        archive.extractall(destination, filter="data")
    return commit


def build(source: Path, root: Path, rid: str, preference: str, commit: str) -> Path:
    destination = root / preference / "publish"
    arguments = [
        "dotnet",
        "publish",
        "src/Msync.Cli/Msync.Cli.csproj",
        "-c",
        "Release",
        "-r",
        rid,
        "--self-contained",
        "true",
        "-p:RestoreLockedMode=true",
        f"-p:SourceRevisionId={commit}",
        "--artifacts-path",
        str(root / preference / "build"),
        "-o",
        str(destination),
        "-m:1",
        "-nr:false",
        "--nologo",
    ]
    if preference != "blended":
        arguments.append(f"-p:OptimizationPreference={preference}")
    command(arguments, source, root / f"{preference}.build.log")
    binary = destination / ("msync.exe" if rid == "win-x64" else "msync")
    subprocess.run([str(binary), "--version"], check=True)
    return binary


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", default="HEAD")
    parser.add_argument(
        "--rid",
        choices=("linux-x64", "win-x64"),
        default="win-x64" if platform.system() == "Windows" else "linux-x64",
    )
    parser.add_argument(
        "--preference", choices=("blended", "Size", "Speed"), action="append"
    )
    parser.add_argument("--samples", type=int, default=31)
    parser.add_argument(
        "--measure",
        action="store_true",
        help="After publishing, run paired startup/check/file CLI journeys against the blended image",
    )
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    root = (
        options.output.resolve()
        if options.output
        else ROOT / f".temp/performance-v080/preferences/run-{time.time_ns()}"
    )
    root.mkdir(parents=True)
    source = root / "source"
    commit = snapshot(options.ref, source)
    preferences: list[str] = options.preference or ["blended", "Size", "Speed"]
    if options.measure and "blended" not in preferences:
        preferences.insert(0, "blended")
    images: dict[str, Path] = {}
    report: dict[str, object] = {
        "source_commit": commit,
        "rid": options.rid,
        "sdk": subprocess.check_output(
            ["dotnet", "--version"], cwd=source, text=True
        ).strip(),
        "images": {},
    }
    for preference in preferences:
        images[preference] = build(source, root, options.rid, preference, commit)
        report["images"] = {
            name: {
                "path": str(path),
                "bytes": path.stat().st_size,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            }
            for name, path in images.items()
        }
        (root / "builds.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )
    if options.measure:
        baseline = images["blended"]
        for preference, binary in images.items():
            if preference == "blended":
                continue
            arguments = [
                sys.executable,
                str(ROOT / "tests/Performance/journeys.py"),
                "--previous-aot",
                str(baseline),
                "--aot",
                str(binary),
                "--samples",
                str(options.samples),
                "--statements",
                "1024",
                "--files",
                "1",
                "--output",
                str(root / f"{preference}.comparison.json"),
            ]
            for case in ("startup", "check-small", "check-batch", "copy-file-source"):
                arguments.extend(["--case", case])
            command(arguments, ROOT, root / f"{preference}.comparison.log")
    print(root)


if __name__ == "__main__":
    main()
