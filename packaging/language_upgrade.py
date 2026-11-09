#!/usr/bin/env python3
"""Run changed cross-module language journeys against one immutable native image."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
import sys
from dataclasses import asdict
from pathlib import Path

from platform_journeys import ROOT, Phase, run_phase

JOURNEYS: tuple[tuple[str, str], ...] = (
    ("language", "language_upgrade_journey.py"),
    ("tooling", "language_upgrade_tooling_journey.py"),
    ("library", "stdlib_upgrade_journey.py"),
    ("skill", "skill_journey.py"),
)


def main() -> int:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".temp/platform-results/language-upgrade"
    )
    parser.add_argument(
        "--phase", action="append", choices=tuple(name for name, _ in JOURNEYS)
    )
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    output: Path = arguments.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    selected: set[str] = set(arguments.phase or (name for name, _ in JOURNEYS))
    report: dict[str, object] = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "fixture_revision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
        "platform": platform.platform(),
    }
    phases: list[Phase] = []
    for name, script in JOURNEYS:
        if name not in selected:
            continue
        phases.append(
            run_phase(
                name,
                [
                    sys.executable,
                    str(ROOT / "tests/Msync.EndToEnd" / script),
                    "--binary",
                    str(binary),
                ],
                output,
            )
        )
        report["phases"] = [asdict(phase) for phase in phases]
        (output / "results.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )
    return int(any(phase.returncode != 0 for phase in phases))


if __name__ == "__main__":
    raise SystemExit(main())
