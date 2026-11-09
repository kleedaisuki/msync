#!/usr/bin/env python3
"""Compare matched native editor payloads through actual typed LSP workflows."""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import shutil
import statistics
import sys
import tempfile
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/Msync.EndToEnd"))

from lsp_latency import measure
from lsp_response_journey import run as response_journey

CASES = ("realistic", "flat128", "flat4000")
CASE_ROWS: dict[str, int] = {
    "flat80": 80,
    "flat128": 128,
    "flat400": 400,
    "flat4000": 4000,
}


def freeze(binary: Path, directory: Path) -> dict[str, Any]:
    directory.mkdir()
    hashes: dict[str, str] = {}
    for path in binary.parent.iterdir():
        if not path.is_file():
            continue
        destination = directory / path.name
        shutil.copy2(path, destination)
        hashes[path.name] = hashlib.sha256(destination.read_bytes()).hexdigest()
    return {"binary": str(directory / binary.name), "files_sha256": hashes}


def observe(binary: Path, case: str, workspace: Path) -> dict[str, Any]:
    if case == "realistic":
        workspace.mkdir()
        return response_journey(binary, workspace, negotiate=True)
    rows = CASE_ROWS[case]
    return measure(binary, rows, 16, resolve_documentation=True)


def distribution(values: list[float]) -> dict[str, Any]:
    p95 = (
        statistics.quantiles(values, n=100, method="inclusive")[94]
        if len(values) > 1
        else values[0]
    )
    return {
        "samples": len(values),
        "p50": statistics.median(values),
        "p95": p95,
        "min": min(values),
        "max": max(values),
        "raw": values,
    }


def summaries(records: list[dict[str, Any]]) -> dict[str, Any]:
    grouped: dict[str, dict[str, dict[str, list[float]]]] = {}
    for record in records:
        values = grouped.setdefault(record["case"], {}).setdefault(record["image"], {})
        for name, value in record["observation"].items():
            if name.endswith(("_ms", "_bytes")) and isinstance(value, (int, float)):
                values.setdefault(name, []).append(float(value))
    return {
        case: {
            image: {name: distribution(values) for name, values in metrics.items()}
            for image, metrics in images.items()
        }
        for case, images in grouped.items()
    }


def save(path: Path, report: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--baseline-source", default="unspecified")
    parser.add_argument("--candidate-source", default="unspecified")
    parser.add_argument("--case", action="append", choices=("realistic", *CASE_ROWS))
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--seed", type=int, default=807)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    scratch = ROOT / ".temp/editor-latency-comparison"
    scratch.mkdir(parents=True, exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="paired-", dir=scratch))
    images: dict[str, Any] = {
        "baseline": freeze(arguments.baseline.resolve(), root / "baseline"),
        "candidate": freeze(arguments.candidate.resolve(), root / "candidate"),
    }
    report: dict[str, Any] = {
        "status": "running",
        "samples_per_image": arguments.samples,
        "seed": arguments.seed,
        "baseline_source": arguments.baseline_source,
        "candidate_source": arguments.candidate_source,
        "images": images,
        "workspace": str(root),
        "records": [],
    }
    randomizer = random.Random(arguments.seed)
    records: list[dict[str, Any]] = report["records"]
    try:
        for case in arguments.case or CASES:
            for sample in range(arguments.samples):
                order = ["baseline", "candidate"]
                randomizer.shuffle(order)
                for image in order:
                    current: dict[str, Any] = {
                        "case": case,
                        "sample": sample,
                        "image": image,
                    }
                    report["active"] = current
                    observation = observe(
                        Path(images[image]["binary"]),
                        case,
                        root / f"{case}-{sample}-{image}",
                    )
                    records.append({**current, "observation": observation})
                    report["summary"] = summaries(records)
                    save(arguments.output, report)
        report["status"] = "complete"
        report.pop("active", None)
    except Exception as error:
        report["status"] = "failed"
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
        save(arguments.output, report)
        raise
    save(arguments.output, report)
    print(json.dumps(report["summary"], indent=2))


if __name__ == "__main__":
    main()
