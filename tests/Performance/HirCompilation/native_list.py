#!/usr/bin/env python3
"""Pair fresh Native default-O2 List checks without a benchmark matrix."""

from __future__ import annotations

import argparse
import json
import os
import platform
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

from measure import distribution, sha256


def observe(binary: Path, source: Path) -> float:
    environment: dict[str, str] = {**os.environ, "NO_COLOR": "1"}
    started: int = time.perf_counter_ns()
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "check", str(source)],
        cwd=source.parent,
        env=environment,
        capture_output=True,
        text=True,
        check=False,
    )
    elapsed: float = (time.perf_counter_ns() - started) / 1_000_000
    if result.returncode != 0:
        raise RuntimeError(f"{binary}: {result.stdout}{result.stderr}")
    return elapsed


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--candidate-receipt", type=Path, required=True)
    parser.add_argument("--pairs", type=int, default=15)
    parser.add_argument("--output", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    source: Path = args.source.resolve()
    binaries: dict[str, Path] = {
        "baseline": args.baseline.resolve(),
        "candidate": args.candidate.resolve(),
    }
    images: dict[str, dict[str, str]] = {
        name: {"path": str(binary), "sha256": sha256(binary)}
        for name, binary in binaries.items()
    }
    for binary in binaries.values():
        observe(binary, source)
    pairs: list[dict[str, object]] = []
    times: dict[str, list[float]] = {name: [] for name in binaries}
    differences: list[float] = []
    for index in range(args.pairs):
        order: tuple[str, str] = (
            ("baseline", "candidate") if index % 2 == 0 else ("candidate", "baseline")
        )
        values: dict[str, float] = {}
        for name in order:
            values[name] = observe(binaries[name], source)
            times[name].append(values[name])
        difference: float = values["candidate"] - values["baseline"]
        differences.append(difference)
        pairs.append({"order": order, "milliseconds": values, "delta_ms": difference})
    report: dict[str, Any] = {
        "scope": "one List source; fresh Native process; default O2 check",
        "source": str(source),
        "source_sha256": sha256(source),
        "images": images,
        "candidate_receipt_path": str(args.candidate_receipt.resolve()),
        "candidate_receipt_sha256": sha256(args.candidate_receipt),
        "candidate_receipt": json.loads(args.candidate_receipt.read_text()),
        "platform": platform.platform(),
        "python_executable": sys.executable,
        "python_version": sys.version,
        "command": ["BINARY", "check", str(source)],
        "environment_overrides": {"NO_COLOR": "1"},
        "untimed_launches_per_image": 1,
        "os_file_cache": "warm; no host cache flush",
        "pairs": pairs,
        "milliseconds": {name: distribution(values) for name, values in times.items()},
        "paired_delta_ms": distribution(differences),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("milliseconds", "paired_delta_ms")}))


if __name__ == "__main__":
    main()
