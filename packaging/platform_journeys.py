#!/usr/bin/env python3
"""Exercise native platform contracts and retain actual host/capability evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import time
from collections.abc import Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LANGUAGE_CASES = (
    "block_tail_and_discard",
    "nullable_present_question_narrows_else",
    "nullable_absent_question_means_is_null",
    "nullable_is_not_narrows_then",
    "nullable_match_narrows_wildcard",
    "regex_nullable_present_narrows_path",
    "regex_relative_path_uses_raw_string",
    "regex_remote_path_keeps_raw_home_without_auth",
    "generic_type_dispatch_drops_incompatible_branches",
    "generic_functions",
    "function_callback",
    "recursive_function",
    "native_range_callback",
    "runtime_function_alternatives",
    "runtime_function_alternatives_absent",
    "scope_branch_member_union",
    "scope_range_member_projection",
    "artifact_context_relocation",
    "path_call_returns_real_process",
    "decltype_process_does_not_launch",
    "unselected_connection_has_no_effect",
    "full_unicode_case_regex_and_native_filename",
    "unicode_scalar_native_directory_order",
)


@dataclass(frozen=True)
class Phase:
    name: str
    arguments: list[str]
    returncode: int
    elapsed_seconds: float


def run_phase(name: str, arguments: Sequence[str], output: Path) -> Phase:
    print(f"BEGIN {name}", flush=True)
    started = time.perf_counter()
    result = subprocess.run(
        arguments,
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=180,
    )
    (output / f"{name}.stdout.log").write_text(result.stdout, encoding="utf-8")
    (output / f"{name}.stderr.log").write_text(result.stderr, encoding="utf-8")
    print(result.stdout, end="", flush=True)
    print(result.stderr, end="", file=sys.stderr, flush=True)
    print(f"END {name}: exit {result.returncode}", flush=True)
    return Phase(
        name, list(arguments), result.returncode, time.perf_counter() - started
    )


def capability(arguments: Sequence[str]) -> dict[str, object]:
    executable = shutil.which(arguments[0])
    if executable is None:
        return {"available": False}
    try:
        result = subprocess.run(
            [executable, *arguments[1:]],
            check=False,
            capture_output=True,
            timeout=15,
        )
    except subprocess.TimeoutExpired:
        return {"available": True, "path": executable, "query": "timed out"}

    def decode(data: bytes) -> str:
        encoding = "utf-16-le" if b"\0" in data else "utf-8"
        return data.decode(encoding, errors="replace").strip()

    return {
        "available": True,
        "path": executable,
        "returncode": result.returncode,
        "stdout": decode(result.stdout),
        "stderr": decode(result.stderr),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / ".temp/platform-results")
    args = parser.parse_args()
    binary = args.binary.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    language = [
        sys.executable,
        "tests/Acceptance/language_journeys.py",
        str(binary),
        "--artifacts",
        "--output",
        str(output / "language.json"),
    ]
    for name in LANGUAGE_CASES:
        language.extend(("--case", name))
    phases = [
        run_phase("language", language, output),
        run_phase(
            "cli",
            [
                sys.executable,
                "tests/Msync.EndToEnd/journeys.py",
                "--binary",
                str(binary),
            ],
            output,
        ),
        run_phase(
            "lsp",
            [
                sys.executable,
                "tests/Msync.EndToEnd/lsp_journey.py",
                "--binary",
                str(binary),
            ],
            output,
        ),
        run_phase(
            "configuration",
            [sys.executable, "tests/Configuration/cli_journeys.py", str(binary)],
            output,
        ),
        run_phase(
            "telemetry",
            [sys.executable, "tests/Telemetry/journeys.py", str(binary)],
            output,
        ),
    ]
    report: dict[str, object] = {
        "platform": platform.platform(),
        "machine": platform.machine(),
        "processor": platform.processor(),
        "python": platform.python_version(),
        "image_os": os.environ.get("ImageOS"),
        "image_version": os.environ.get("ImageVersion"),
        "runner_os": os.environ.get("RUNNER_OS"),
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "capabilities": {
            "ssh": capability(["ssh", "-V"]),
            "sshd": capability(["sshd", "-V"]),
            "wsl": capability(["wsl", "--list", "--quiet"]),
        },
        "phases": [asdict(phase) for phase in phases],
        "passed": all(phase.returncode == 0 for phase in phases),
    }
    (output / "platform.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
