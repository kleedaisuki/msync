#!/usr/bin/env python3
"""Run the four source-defined ordered containers through the real CLI stages."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
HERE: Path = Path(__file__).resolve().parent


@dataclass(frozen=True)
class Journey:
    name: str
    script: str
    repeated_level: bool = True


JOURNEYS: tuple[Journey, ...] = (
    Journey("paged-trees", "btree_bplus_journey.py", False),
    Journey("red-black-set", "rb_set_journey.py"),
    Journey("binary-heap", "heap_journey.py"),
)


def run_journey(
    journey: Journey, binary: Path, levels: list[int], directory: Path
) -> dict[str, Any]:
    report: Path = directory / f"{journey.name}.json"
    command: list[str] = [
        sys.executable,
        str(HERE / journey.script),
        "--binary",
        str(binary),
        "--output",
        str(report),
    ]
    if journey.repeated_level:
        for level in levels:
            command.extend(("--level", str(level)))
    else:
        command.extend(("--levels", *(str(level) for level in levels)))
    environment: dict[str, str] = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    result: subprocess.CompletedProcess[str] = subprocess.run(
        command,
        cwd=ROOT,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )
    (directory / f"{journey.name}.stdout").write_text(result.stdout, encoding="utf-8")
    (directory / f"{journey.name}.stderr").write_text(result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(
            f"{journey.name} failed ({result.returncode})\n"
            f"{result.stdout}\n{result.stderr}"
        )
    parsed: dict[str, Any] = json.loads(report.read_text(encoding="utf-8"))
    return {"journey": journey.name, **parsed}


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--level", type=int, choices=(0, 1, 2, 3), action="append")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    levels: list[int] = arguments.level or [2]
    scratch: Path = ROOT / ".temp/ordered-structures"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="native-journeys-", dir=scratch) as name:
        reports: list[dict[str, Any]] = [
            run_journey(journey, binary, levels, Path(name)) for journey in JOURNEYS
        ]
    report: dict[str, Any] = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "managed_component_sha256": {
            path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(binary.parent.glob("Msync.*.dll"))
        },
        "platform": platform.platform(),
        "levels": levels,
        "families": ["BTree", "BPlusTree", "Set", "Heap"],
        "reports": reports,
    }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
