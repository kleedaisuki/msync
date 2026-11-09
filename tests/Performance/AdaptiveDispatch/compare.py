#!/usr/bin/env python3
"""Alternate frozen native no-clone/control images, retaining full observations."""

from __future__ import annotations

import argparse
import hashlib
import json
import statistics
import subprocess
import time
from pathlib import Path
from typing import Any


def distribution(values: list[float]) -> dict[str, object]:
    ordered = sorted(values)
    position = (len(values) - 1) * 0.95
    lower = int(position)
    upper = min(lower + 1, len(values) - 1)
    return {
        "p50": statistics.median(values),
        "p95": ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower),
        "raw": values,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--control", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--pairs", type=int, default=7)
    options = parser.parse_args()
    output: Path = options.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    work = output / "work"
    work.mkdir()
    images: dict[str, Path] = {
        "control": options.control.resolve(),
        "candidate": options.candidate.resolve(),
    }
    records: list[dict[str, Any]] = []
    for case, size in (
        ("direct", 1),
        ("direct", 256),
        ("swap", 256),
        ("captures", 256),
        ("literal", 1),
        ("io", 1),
    ):
        for pair in range(options.pairs):
            order = (
                ("control", "candidate") if pair % 2 == 0 else ("candidate", "control")
            )
            for image in order:
                destination = output / f"{case}-{size}-{pair}-{image}.json"
                start = time.perf_counter()
                process = subprocess.run(
                    [
                        str(images[image]),
                        "--case",
                        case,
                        "--size",
                        str(size),
                        "--samples",
                        "3",
                        "--iterations",
                        "8",
                        "--root",
                        str(work),
                        "--output",
                        str(destination),
                    ],
                    capture_output=True,
                    check=True,
                )
                elapsed = (time.perf_counter() - start) * 1000
                destination.with_suffix(".stdout").write_bytes(process.stdout)
                destination.with_suffix(".stderr").write_bytes(process.stderr)
                records.append(
                    {
                        "image": image,
                        "process_ms": elapsed,
                        "report": json.loads(destination.read_text()),
                    }
                )
    summaries: list[dict[str, object]] = []
    for image in images:
        for case, size in (
            ("direct", 1),
            ("direct", 256),
            ("swap", 256),
            ("captures", 256),
            ("literal", 1),
            ("io", 1),
        ):
            rows = [
                row
                for row in records
                if row["image"] == image
                and row["report"]["Case"] == case
                and row["report"]["Size"] == size
            ]
            summaries.append(
                {
                    "image": image,
                    "case": case,
                    "size": size,
                    "vm_ms": distribution(
                        [
                            float(sample)
                            for row in rows
                            for sample in row["report"]["Milliseconds"]
                        ]
                    ),
                    "alloc_bytes": distribution(
                        [
                            float(row["report"]["AllocatedBytesPerOperation"])
                            for row in rows
                        ]
                    ),
                    "process_ms": distribution(
                        [float(row["process_ms"]) for row in rows]
                    ),
                    "prepare_ms": distribution(
                        [float(row["report"]["PrepareMilliseconds"]) for row in rows]
                    ),
                    "first_run_ms": distribution(
                        [float(row["report"]["FirstRunMilliseconds"]) for row in rows]
                    ),
                    "shape": {
                        key: rows[0]["report"][key]
                        for key in (
                            "ArtifactSha256",
                            "Instructions",
                            "Calls",
                            "MappedCalls",
                            "DirectCalls",
                        )
                    },
                }
            )
    manifest = {
        "images": {
            name: {
                "path": str(path),
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            }
            for name, path in images.items()
        },
        "summaries": summaries,
        "records": records,
    }
    (output / "summary.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )
    for row in summaries:
        print(row["image"], row["case"], row["size"], row["vm_ms"], row["alloc_bytes"])


if __name__ == "__main__":
    main()
