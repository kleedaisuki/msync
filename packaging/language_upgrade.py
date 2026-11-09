#!/usr/bin/env python3
"""Run changed cross-module language journeys against one immutable native image."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import subprocess
import sys
from dataclasses import asdict
from io import TextIOWrapper
from pathlib import Path
from typing import cast

from platform_journeys import ROOT, Phase, run_phase

END_TO_END: Path = ROOT / "tests/Msync.EndToEnd"
JOURNEYS: tuple[tuple[str, Path], ...] = (
    ("language", END_TO_END / "language_upgrade_journey.py"),
    ("tooling", END_TO_END / "language_upgrade_tooling_journey.py"),
    ("library", END_TO_END / "stdlib_upgrade_journey.py"),
    ("empty-collections", END_TO_END / "collections_empty_journey.py"),
    ("skill", END_TO_END / "skill_journey.py"),
    ("artifact", END_TO_END / "artifact_disassembly_journey.py"),
    ("path-display", END_TO_END / "path_display_journey.py"),
    ("option", END_TO_END / "standard_option_journey.py"),
    ("data", END_TO_END / "standard_data_journey.py"),
    ("concepts-tooling", END_TO_END / "standard_concepts_tooling_journey.py"),
    ("number-allocation", END_TO_END / "number_allocation_journey.py"),
    ("static-optimization", END_TO_END / "static_optimization_journey.py"),
    ("library-policy", END_TO_END / "stdlib_policy_journey.py"),
    ("command-policies", END_TO_END / "commands_policy_journey.py"),
    ("trees", END_TO_END / "ordered_structures_journey.py"),
    ("type-traits", END_TO_END / "type_traits_journey.py"),
    ("terminal-library", END_TO_END / "terminal_library_journey.py"),
    ("shell-repl", END_TO_END / "repl_shell_journey.py"),
    ("stdlib-cost", ROOT / "tests/Performance/stdlib_expansion.py"),
    ("console-encoding", END_TO_END / "windows_console_encoding_probe.py"),
    ("remote-library", END_TO_END / "stdlib_upgrade_journey.py"),
)
OPTIONAL_PHASES: frozenset[str] = frozenset({"console-encoding", "remote-library"})


def main() -> int:
    cast(TextIOWrapper, sys.stdout).reconfigure(encoding="utf-8")
    cast(TextIOWrapper, sys.stderr).reconfigure(encoding="utf-8")
    os.environ["PYTHONUTF8"] = "1"
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--ssh-fixture",
        action="store_true",
        help="Exercise selected class-library work through an owned real SSH endpoint",
    )
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
    selected: set[str] = set(
        arguments.phase or (name for name, _ in JOURNEYS if name not in OPTIONAL_PHASES)
    )
    if arguments.ssh_fixture and arguments.phase is None:
        selected.add("remote-library")
    report: dict[str, object] = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "companion_dlls": {
            str(path.name): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(binary.parent.glob("*.dll"))
        },
        "managed_msync_components": {
            path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(binary.parent.glob("*.dll"))
            if path.name
            in {
                "msync.dll",
                "Msync.Compiler.dll",
                "Msync.Core.dll",
                "Msync.Runtime.dll",
            }
        },
        "fixture_revision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
        "platform": platform.platform(),
    }
    phases: list[Phase] = []
    for name, script in JOURNEYS:
        if name not in selected:
            continue
        extra: list[str] = []
        if name == "remote-library":
            extra = ["--ssh-fixture", "--case", "ssh-file-and-command"]
        elif name in {"library-policy", "command-policies"}:
            extra = ["--levels", "2"]
        elif name == "stdlib-cost":
            extra = [
                "--samples",
                "3",
                "--terminal-completion",
                "list",
                "--output",
                str(output / "stdlib-cost.json"),
                "--work-root",
                str(output / "stdlib-cost-work"),
            ]
        elif name == "console-encoding":
            extra = ["--output", str(output / "console-encoding.json")]
        phases.append(
            run_phase(
                name,
                [
                    sys.executable,
                    str(script),
                    "--binary",
                    str(binary),
                    *extra,
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
