from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import statistics
from collections.abc import Sequence
from pathlib import Path
from typing import Any


def percentile(values: Sequence[float], probability: float) -> float:
    ordered = sorted(values)
    return ordered[math.ceil(probability * len(ordered)) - 1]


def interval(values: Sequence[float]) -> list[float]:
    return [percentile(values, 0.025), percentile(values, 0.975)]


def summarize(report: dict[str, Any], draws: int) -> dict[str, Any]:
    randomizer = random.Random(20261110)
    summary: dict[str, Any] = {}
    for case in report["counts"]:
        blocks: list[list[dict[str, Any]]] = [
            block["cases"][case] for block in report["blocks"]
        ]
        paired_medians: list[float] = []
        p99_differences: list[float] = []
        for _ in range(draws):
            pairs = [pair for _ in blocks for pair in randomizer.choice(blocks)]
            baseline = [pair["runs"]["baseline"]["milliseconds"] for pair in pairs]
            candidate = [pair["runs"]["candidate"]["milliseconds"] for pair in pairs]
            paired_medians.append(
                statistics.median(
                    right - left
                    for left, right in zip(baseline, candidate, strict=True)
                )
            )
            p99_differences.append(
                percentile(candidate, 0.99) - percentile(baseline, 0.99)
            )
        summary[case] = {
            "observed": report["summary"][case],
            "block_paired_median_deltas_ms": [
                statistics.median(
                    pair["runs"]["candidate"]["milliseconds"]
                    - pair["runs"]["baseline"]["milliseconds"]
                    for pair in block
                )
                for block in blocks
            ],
            "resampled_paired_median_delta_95pct_ms": interval(paired_medians),
            "resampled_marginal_p99_difference_95pct_ms": interval(p99_differences),
            "median_child_cpu_ms": {
                label: statistics.median(
                    sum(pair["runs"][label]["child_cpu"].values()) * 1000
                    for block in blocks
                    for pair in block
                )
                if blocks[0][0]["runs"][label]["child_cpu"] is not None
                else None
                for label in ("baseline", "candidate")
            },
        }
    return {
        "block_resampling": {
            "draws": draws,
            "random_seed": 20261110,
            "unit": "whole paired block; cases retain their original paired observations",
            "interpretation": (
                "Finite observed-window block bootstrap; not a guarantee of stationarity "
                "or independence from external shared-host load."
            ),
        },
        "summary": summary,
        "host_noop": {
            "command": report["host_noop"]["command"],
            "median_ms": statistics.median(
                sample["milliseconds"] for sample in report["host_noop"]["samples"]
            ),
        }
        if report["host_noop"]["command"] is not None
        else report["host_noop"],
    }


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Analyze an existing Native tail window"
    )
    parser.add_argument("report", type=Path)
    parser.add_argument("--draws", type=int, default=2000)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report: dict[str, Any] = json.loads(args.report.read_bytes())
    result = summarize(report, args.draws)
    result["report_sha256"] = hashlib.sha256(args.report.read_bytes()).hexdigest()
    result["analyzer_sha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(args.output)


if __name__ == "__main__":
    main()
