"""Build and execute a selected C# contract project against frozen product DLLs."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


def main() -> int:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--assemblies", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=True)
    phases: list[dict[str, Any]] = []
    name: str = (
        ET.parse(args.project).getroot().findtext(".//AssemblyName")
        or args.project.stem
    )
    commands: tuple[list[str], ...] = (
        [
            "dotnet",
            "build",
            str(args.project),
            f"-p:FrozenAssemblies={args.assemblies}",
            "--artifacts-path",
            str(args.work / "build"),
            "-m:1",
            "-nr:false",
            "-p:UseSharedCompilation=true",
            f"-p:SharedCompilationId={os.environ['SharedCompilationId']}",
            "--nologo",
            f"-p:NuGetLockFilePath={args.work / 'packages.lock.json'}",
        ],
        [
            "dotnet",
            str(
                args.work / "build/bin" / args.project.stem / "debug" / (name + ".dll")
            ),
        ],
    )
    for command in commands:
        started: int = time.perf_counter_ns()
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            command, check=False
        )
        phases.append(
            {
                "command": command,
                "elapsed_ns": time.perf_counter_ns() - started,
                "exit_code": result.returncode,
            }
        )
        (args.work / "project.json").write_text(
            json.dumps({"phases": phases}, indent=2) + "\n", encoding="utf-8"
        )
        if result.returncode:
            return result.returncode
        if command[1] == "build":
            output: Path = args.work / "build/bin" / args.project.stem / "debug"
            for product in args.assemblies.glob("Msync.*.dll"):
                copied: Path = output / product.name
                if copied.exists():
                    with copied.open("rb") as actual, product.open("rb") as expected:
                        if (
                            hashlib.file_digest(actual, "sha256").digest()
                            != hashlib.file_digest(expected, "sha256").digest()
                        ):
                            raise RuntimeError(
                                f"Test project did not use the frozen product: {copied}"
                            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
