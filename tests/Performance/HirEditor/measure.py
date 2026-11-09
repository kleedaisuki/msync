#!/usr/bin/env python3
"""Compare matched managed editor analysis and context projection costs."""

from __future__ import annotations

import argparse
import json
import os
import platform
import statistics
import subprocess
import sys
from pathlib import Path
from typing import Any, cast


def observe(host: Path, bindings: int, samples: int) -> dict[str, Any]:
    environment: dict[str, str] = dict(os.environ)
    environment["DOTNET_TieredCompilation"] = "0"
    result: subprocess.CompletedProcess[str] = subprocess.run(
        ["dotnet", str(host), str(bindings), str(samples)],
        env=environment,
        text=True,
        capture_output=True,
        check=True,
    )
    return cast(dict[str, Any], json.loads(result.stdout))


def summarize(observation: dict[str, Any]) -> dict[str, float]:
    times: list[float] = cast(list[float], observation["warm_ms"])
    allocations: list[int] = cast(list[int], observation["warm_allocated_bytes"])
    return {
        "warm_median_ms": statistics.median(times),
        "warm_median_bytes": statistics.median(allocations),
        "cold_ms": float(observation["cold_ms"]),
        "cold_bytes": float(observation["cold_allocated_bytes"]),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-host", type=Path, required=True)
    parser.add_argument("--candidate-host", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--samples", type=int, default=9)
    arguments: argparse.Namespace = parser.parse_args()
    hosts: dict[str, Path] = {
        "baseline": arguments.baseline_host.resolve(),
        "candidate": arguments.candidate_host.resolve(),
    }
    records: list[dict[str, Any]] = []
    summary: dict[str, dict[str, dict[str, float]]] = {}
    for case, bindings in enumerate((0, 80, 400)):
        observations: dict[str, dict[str, Any]] = {}
        order: list[str] = ["baseline", "candidate"]
        if case % 2:
            order.reverse()
        for image in order:
            result: dict[str, Any] = observe(hosts[image], bindings, arguments.samples)
            observations[image] = result
            records.append({"image": image, **result})
        if (
            observations["baseline"]["source_sha256"]
            != observations["candidate"]["source_sha256"]
        ):
            raise ValueError("The paired editor workloads differ.")
        if (
            observations["baseline"]["visible_bindings"]
            != observations["candidate"]["visible_bindings"]
        ):
            raise ValueError("The paired editor contexts expose different bindings.")
        summary[str(bindings)] = {
            name: summarize(result) for name, result in observations.items()
        }
    report: dict[str, Any] = {
        "status": "complete",
        "runner": {
            "python": platform.python_version(),
            "executable": sys.executable,
            "platform": platform.platform(),
        },
        "records": records,
        "summary": summary,
    }
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
