#!/usr/bin/env python3
"""Alternate frozen Native scheduler component images over real copied bytes."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import statistics
import subprocess
import sys
from collections.abc import Sequence
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]


def percentile(values: Sequence[float], quantile: float) -> float:
    ordered = sorted(values)
    position = (len(ordered) - 1) * quantile
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def summarize(runs: list[dict[str, Any]]) -> list[dict[str, object]]:
    groups: dict[tuple[str, str, int], list[dict[str, Any]]] = {}
    for run in runs:
        for item in run["report"]["Observations"]:
            if not item["Warmup"]:
                groups.setdefault((run["name"], item["Case"], item["Size"]), []).append(
                    item
                )
    results: list[dict[str, object]] = []
    for (name, case, size), observations in groups.items():
        result: dict[str, object] = {
            "image": name,
            "case": case,
            "size": size,
            "samples": len(observations),
        }
        for field in (
            "ConstructMilliseconds",
            "AdmissionMilliseconds",
            "ExecutionMilliseconds",
            "DrainMilliseconds",
            "ConstructAllocatedBytes",
            "ExecutionAllocatedBytes",
            "ResourceNodesVisited",
            "DependencyEdges",
            "NativeHazardWaits",
        ):
            values = [
                float(item[field]) for item in observations if item[field] is not None
            ]
            if values:
                result[field] = {
                    "p50": statistics.median(values),
                    "p95": percentile(values, 0.95),
                    "minimum": min(values),
                    "maximum": max(values),
                    "values": values,
                }
        results.append(result)
    return results


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--sizes", default="1,100,1000")
    parser.add_argument(
        "--cases",
        default="independent,observe_directory,chain,gated_literal,gated_shared",
    )
    parser.add_argument("--rounds", type=int, default=3)
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    output = options.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    raw = output.parent / f"{output.stem}-runs"
    raw.mkdir(exist_ok=True)
    fixtures = output.parent / f"{output.stem}-fixtures"
    images = [("before", options.before.resolve()), ("after", options.after.resolve())]
    runs: list[dict[str, Any]] = []
    report: dict[str, Any] = {
        "status": "running",
        "platform": platform.platform(),
        "python": {"executable": sys.executable, "version": sys.version},
        "images": [
            {
                "name": name,
                "path": str(image),
                "sha256": hashlib.sha256(image.read_bytes()).hexdigest(),
            }
            for name, image in images
        ],
        "method": "Alternating AB/BA process batches; per-batch warmups; "
        "fresh random 64-byte generation checked in every output outside timing. "
        "Executable typed modules are prepared without compile/bind in measurement. "
        "Admission is only separated when all transfers were admitted before first suspension; "
        "gated cases hold a real released child until the host observes that boundary. "
        "gated_shared initial source is stale, proving actual RAW prerequisites.",
        "rounds": options.rounds,
        "samples_per_batch": options.samples,
        "warmups_per_batch": options.warmups,
        "jobs": options.jobs,
        "runs": runs,
    }

    def save() -> None:
        report["summary"] = summarize(runs)
        output.write_text(
            json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )

    save()
    try:
        for iteration in range(options.rounds):
            for name, image in images if iteration % 2 == 0 else reversed(images):
                target = raw / f"{iteration}-{name}.json"
                command = [
                    str(image),
                    "--root",
                    str(fixtures),
                    "--output",
                    str(target),
                    "--sizes",
                    options.sizes,
                    "--cases",
                    options.cases,
                    "--samples",
                    str(options.samples),
                    "--warmups",
                    str(options.warmups),
                    "--jobs",
                    str(options.jobs),
                ]
                completed = subprocess.run(
                    command,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    check=True,
                )
                data: dict[str, Any] = json.loads(target.read_text(encoding="utf-8"))
                runs.append(
                    {
                        "name": name,
                        "round": iteration,
                        "report": data,
                        "stdout": completed.stdout,
                        "stderr": completed.stderr,
                    }
                )
                save()
                print(f"Completed round {iteration + 1}: {name}", flush=True)
        report["status"] = "complete"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = str(error)
        raise
    finally:
        save()


if __name__ == "__main__":
    main()
