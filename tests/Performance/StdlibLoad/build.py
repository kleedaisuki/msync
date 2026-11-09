#!/usr/bin/env python3
"""Publish a fixed stdlib probe against one exact product revision."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import platform
import shutil
import subprocess
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PROBE = Path(__file__).resolve().parent


def run(arguments: list[str], cwd: Path, output: Path) -> None:
    with output.open("wb") as stream:
        result = subprocess.run(
            arguments, cwd=cwd, stdout=stream, stderr=subprocess.STDOUT, check=False
        )
    if result.returncode != 0:
        raise RuntimeError(
            f"Command {arguments!r} exited {result.returncode}; see {output}"
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--rid", default="win-x64" if platform.system() == "Windows" else "linux-x64"
    )
    options = parser.parse_args()
    output = options.output.resolve()
    output.mkdir(parents=True)
    source = output / "source"
    revision = subprocess.check_output(
        ["git", "rev-parse", options.ref], cwd=ROOT, text=True
    ).strip()
    optional_projects: list[str] = []
    optimization_exists: subprocess.CompletedProcess[bytes] = subprocess.run(
        ["git", "cat-file", "-e", f"{revision}:src/Msync.Optimization"],
        cwd=ROOT,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    if optimization_exists.returncode == 0:
        optional_projects.append("src/Msync.Optimization")
    archive = subprocess.check_output(
        [
            "git",
            "archive",
            "--format=tar",
            revision,
            "src/Msync.Compiler",
            "src/Msync.Core",
            *optional_projects,
            "stdlib",
            "Directory.Build.props",
            "global.json",
        ],
        cwd=ROOT,
    )
    source.mkdir()
    with tarfile.open(fileobj=io.BytesIO(archive)) as contents:
        contents.extractall(source, filter="data")
    destination = source / "tests/Performance/StdlibLoad"
    destination.mkdir(parents=True)
    probe_hashes: dict[str, str] = {}
    for name in ("Program.cs", "Msync.StdlibLoad.csproj", "packages.lock.json"):
        path = PROBE / name
        if path.exists():
            shutil.copy2(path, destination / name)
            probe_hashes[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    sdk = subprocess.check_output(
        ["dotnet", "--version"], cwd=source, text=True
    ).strip()
    run(
        [
            "dotnet",
            "publish",
            str(destination / "Msync.StdlibLoad.csproj"),
            "-c",
            "Release",
            "-r",
            options.rid,
            "--artifacts-path",
            str(output / "build"),
            "-o",
            str(output / "native"),
            "-m:1",
            "-nr:false",
            f"-p:SourceRevisionId={revision}",
        ],
        source,
        output / "publish.log",
    )
    binary = (
        output
        / "native"
        / ("msync-stdlib-load.exe" if options.rid == "win-x64" else "msync-stdlib-load")
    )
    report: dict[str, object] = {
        "product_source": revision,
        "product_archive_sha256": hashlib.sha256(archive).hexdigest(),
        "probe_sha256": probe_hashes,
        "sdk": sdk,
        "rid": options.rid,
        "executable": str(binary),
        "executable_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
    }
    (output / "build.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
