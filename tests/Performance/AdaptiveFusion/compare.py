#!/usr/bin/env python3
"""Alternate frozen Native AOT fusion images; retain preparation and VM costs."""

from __future__ import annotations

import argparse
import hashlib
import json
import statistics
import subprocess
from pathlib import Path
from typing import TypedDict, cast


class Observation(TypedDict):
    Scenario: str
    Size: int
    Stage: str
    Sites: int
    Instructions: int
    Milliseconds: list[float]
    AllocatedBytes: float


class Report(TypedDict):
    Executable: str
    Sha256: str
    Observations: list[Observation]
    Checks: int


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    first = int(position)
    second = min(first + 1, len(ordered) - 1)
    return ordered[first] + (ordered[second] - ordered[first]) * (position - first)


def compare(
    control: Path,
    candidate: Path,
    output: Path,
    sizes: str,
    cases: str,
    pairs: int,
    samples: int,
) -> None:
    output.mkdir(parents=True, exist_ok=True)
    images = {"control": control.resolve(), "candidate": candidate.resolve()}
    results: dict[str, list[Report]] = {name: [] for name in images}
    for pair in range(pairs):
        order = ["control", "candidate"] if pair % 2 == 0 else ["candidate", "control"]
        for image in order:
            report_path = output / f"{pair}-{image}.json"
            subprocess.run(
                [
                    str(images[image]),
                    "--sizes",
                    sizes,
                    "--cases",
                    cases,
                    "--samples",
                    str(samples),
                    "--root",
                    str(output / "work"),
                    "--output",
                    str(report_path),
                ],
                check=True,
                stdout=(output / f"{pair}-{image}.log").open("w", encoding="utf-8"),
                stderr=subprocess.STDOUT,
                timeout=180,
            )
            results[image].append(cast(Report, json.loads(report_path.read_text())))
    combined: list[dict[str, object]] = []
    for image, reports in results.items():
        keys = sorted(
            {
                (row["Scenario"], row["Size"], row["Stage"])
                for report in reports
                for row in report["Observations"]
            }
        )
        for scenario, size, stage in keys:
            observations = [
                row
                for report in reports
                for row in report["Observations"]
                if (row["Scenario"], row["Size"], row["Stage"])
                == (scenario, size, stage)
            ]
            times = [
                time
                for observation in observations
                for time in observation["Milliseconds"]
            ]
            combined.append(
                {
                    "image": image,
                    "scenario": scenario,
                    "size": size,
                    "stage": stage,
                    "p50_ms": percentile(times, 0.5),
                    "p95_ms": percentile(times, 0.95),
                    "allocated_bytes": statistics.median(
                        row["AllocatedBytes"] for row in observations
                    ),
                    "eligible_static_sites": observations[0]["Sites"],
                    "canonical_instructions": observations[0]["Instructions"],
                    "raw_milliseconds": times,
                }
            )
    summary: dict[str, object] = {
        "images": {
            name: {
                "path": str(path),
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            }
            for name, path in images.items()
        },
        "pairs": pairs,
        "samples_per_process": samples,
        "observations": combined,
    }
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    for row in combined:
        print(
            f"{row['image']} {row['scenario']} n={row['size']} {row['stage']}: {float(cast(float, row['p50_ms'])):.6f} ms / {row['allocated_bytes']} B"
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--control", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sizes", default="1,32,4096")
    parser.add_argument("--cases", default="sum,captured,last,literal,copy")
    parser.add_argument("--pairs", type=int, default=3)
    parser.add_argument("--samples", type=int, default=7)
    args = parser.parse_args()
    compare(
        args.control,
        args.candidate,
        args.output.resolve(),
        args.sizes,
        args.cases,
        args.pairs,
        args.samples,
    )


if __name__ == "__main__":
    main()
