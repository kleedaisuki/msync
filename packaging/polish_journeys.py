#!/usr/bin/env python3
"""Run only the changed terminal, editor insight and formatting contracts."""

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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / ".temp/platform-results")
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    output = arguments.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    phases: list[Phase] = []
    report: dict[str, object] = {
        "platform": platform.platform(),
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "source_revision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
    }
    for name, script in (
        ("presentation", "presentation_journey.py"),
        ("formatter", "formatter_journey.py"),
        ("skill", "skill_journey.py"),
    ):
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
        (output / "polish.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )
    return int(any(phase.returncode != 0 for phase in phases))


if __name__ == "__main__":
    raise SystemExit(main())
