#!/usr/bin/env python3
"""Observe frozen native library-load probes with paired cold/warm compilations."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import shutil
import statistics
import subprocess
import sys
import tempfile
from collections.abc import Sequence
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
COMMON = ("none", "bool", "ranges_common")
ADDED = ("ranges", "cast", "functional")


@dataclass(frozen=True)
class Image:
    name: str
    executable: str
    source: str
    sha256: dict[str, str]


def freeze(name: str, binary: Path, source: str, directory: Path) -> Image:
    directory.mkdir(parents=True)
    hashes: dict[str, str] = {}
    for path in sorted(binary.parent.iterdir()):
        if path.is_file():
            copied = directory / path.name
            shutil.copy2(path, copied)
            hashes[path.name] = hashlib.sha256(copied.read_bytes()).hexdigest()
    return Image(name, str(directory / binary.name), source, hashes)


def distribution(values: Sequence[float]) -> dict[str, object]:
    ordered = sorted(values)
    position = (len(ordered) - 1) * 0.95
    first = int(position)
    last = min(first + 1, len(ordered) - 1)
    return {
        "samples": len(ordered),
        "p50": statistics.median(ordered),
        "p95": ordered[first] + (ordered[last] - ordered[first]) * (position - first),
        "min": ordered[0],
        "max": ordered[-1],
        "raw": list(values),
    }


def observe(image: Image, case: str, directory: Path) -> dict[str, Any]:
    directory.mkdir()
    command = [image.executable, case, "3"]
    result = subprocess.run(command, capture_output=True, check=False, timeout=60)
    (directory / "stdout.json").write_bytes(result.stdout)
    (directory / "stderr.txt").write_bytes(result.stderr)
    if result.returncode != 0:
        raise RuntimeError(
            f"{command!r} failed with {result.returncode}: "
            + result.stderr.decode("utf-8", errors="replace")
        )
    report: dict[str, Any] = json.loads(result.stdout)
    if report["workload"] != case or len(report["warm_o0_milliseconds"]) != 3:
        raise RuntimeError(f"Unexpected probe result: {report!r}")
    return report


def summaries(records: list[dict[str, Any]]) -> list[dict[str, object]]:
    groups: dict[tuple[str, str], list[dict[str, Any]]] = {}
    for record in records:
        groups.setdefault((record["image"], record["case"]), []).append(
            record["report"]
        )
    result: list[dict[str, object]] = []
    for (image, case), reports in groups.items():
        warm_times = [
            float(value)
            for report in reports
            for value in report["warm_o0_milliseconds"]
        ]
        warm_bytes = [
            float(value)
            for report in reports
            for value in report["warm_o0_allocated_bytes"]
        ]
        shapes = {
            (
                int(report["raw_functions"]),
                int(report["raw_constants"]),
                int(report["raw_instructions"]),
                int(report["raw_locals"]),
                tuple(report["raw_function_names"]),
            )
            for report in reports
        }
        if len(shapes) != 1:
            raise RuntimeError(
                f"Compilation shape changed within {image}/{case}: {shapes!r}"
            )
        result.append(
            {
                "image": image,
                "case": case,
                "cold_o0_milliseconds": distribution(
                    [float(report["cold_o0_milliseconds"]) for report in reports]
                ),
                "cold_o0_allocated_bytes": distribution(
                    [float(report["cold_o0_allocated_bytes"]) for report in reports]
                ),
                "warm_o0_milliseconds": distribution(warm_times),
                "warm_o0_allocated_bytes": distribution(warm_bytes),
                "raw_shape": list(next(iter(shapes))),
            }
        )
    return result


def save(path: Path, report: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(report, ensure_ascii=True, indent=2) + "\n", encoding="utf-8"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--baseline-source", required=True)
    parser.add_argument("--candidate-source", required=True)
    parser.add_argument("--case", action="append", choices=COMMON)
    parser.add_argument("--candidate-case", action="append", choices=ADDED)
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    scratch = ROOT / ".temp/stdlib-load"
    scratch.mkdir(parents=True, exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix="paired-", dir=scratch))
    images = [
        freeze(
            "baseline",
            options.baseline.resolve(),
            options.baseline_source,
            workspace / "baseline",
        ),
        freeze(
            "candidate",
            options.candidate.resolve(),
            options.candidate_source,
            workspace / "candidate",
        ),
    ]
    records: list[dict[str, Any]] = []
    report: dict[str, Any] = {
        "status": "running",
        "platform": platform.platform(),
        "python": {"path": sys.executable, "version": sys.version},
        "workspace": str(workspace),
        "images": [asdict(image) for image in images],
        "method": {
            "paired_processes": options.samples,
            "warm_samples_per_process": 3,
            "warmup_compilations": 3,
            "level": 0,
            "cases": options.case or COMMON,
            "candidate_only_cases": options.candidate_case or ADDED,
        },
        "records": records,
    }
    try:
        for case in options.case or COMMON:
            for sample in range(options.samples):
                order = images if sample % 2 == 0 else list(reversed(images))
                for image in order:
                    records.append(
                        {
                            "image": image.name,
                            "case": case,
                            "sample": sample,
                            "report": observe(
                                image, case, workspace / f"{case}-{sample}-{image.name}"
                            ),
                        }
                    )
                    save(options.output, report)
        for case in options.candidate_case or ADDED:
            for sample in range(options.samples):
                records.append(
                    {
                        "image": "candidate",
                        "case": case,
                        "sample": sample,
                        "report": observe(
                            images[1], case, workspace / f"{case}-{sample}-candidate"
                        ),
                    }
                )
                save(options.output, report)
        report["summaries"] = summaries(records)
        report["status"] = "complete"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = str(error)
        raise
    finally:
        save(options.output, report)
    print(json.dumps(report["summaries"], ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
