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
    parser.add_argument(
        "--native-origin",
        type=Path,
        help="Recorded artifact identity for a reused native image",
    )
    parser.add_argument(
        "--phase",
        action="append",
        choices=[
            "presentation",
            "lsp-analysis",
            "lsp-response",
            "formatter",
            "skill",
            "completion",
            "terminal-presentation",
            "hints",
            "authentication",
        ],
        help="Run only the named changed contract; may be repeated",
    )
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
        "fixture_revision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
    }
    if arguments.native_origin is not None:
        origin: dict[str, object] = json.loads(
            arguments.native_origin.read_text(encoding="utf-8")
        )
        assert origin["binary_sha256"] == report["binary_sha256"], (
            "Reused native image does not match its artifact identity"
        )
        report["native_origin"] = origin
        report["native_source_revision"] = str(origin["artifact_name"]).rsplit("-", 1)[
            -1
        ]
    else:
        report["native_source_revision"] = report["fixture_revision"]
    journeys = [
        ("presentation", "presentation_journey.py"),
        ("lsp-analysis", "lsp_analysis_journey.py"),
        ("lsp-response", "lsp_response_journey.py"),
        ("formatter", "formatter_journey.py"),
        ("skill", "skill_journey.py"),
        ("completion", "completion_journey.py"),
    ]
    if sys.platform != "win32":
        journeys.append(("terminal-presentation", "terminal_presentation_journey.py"))
        journeys.append(("hints", "hint_journey.py"))
        journeys.append(("authentication", "authentication_journey.py"))
    if arguments.phase:
        selected: set[str] = set(arguments.phase)
        unavailable = selected - {name for name, _ in journeys}
        if unavailable:
            parser.error(f"Phases unavailable on this host: {sorted(unavailable)}")
        journeys = [(name, script) for name, script in journeys if name in selected]
    for name, script in journeys:
        extra = (
            ["--ssh-fixture"]
            if name == "completion" and sys.platform == "linux"
            else []
        )
        phases.append(
            run_phase(
                name,
                [
                    sys.executable,
                    str(ROOT / "tests/Msync.EndToEnd" / script),
                    "--binary",
                    str(binary),
                    *extra,
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
