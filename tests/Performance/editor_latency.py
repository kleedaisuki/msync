#!/usr/bin/env python3
"""Compare matched native editor payloads through actual typed LSP workflows."""

from __future__ import annotations

import argparse
import json
import random
import statistics
import sys
import tempfile
import traceback
from pathlib import Path
from typing import Any

from journeys import freeze as freeze_image

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/Msync.EndToEnd"))

from lsp_latency import measure
from lsp_response_journey import run as response_journey

CASES = ("realistic", "flat400")
CASE_ROWS: dict[str, int] = {
    "flat80": 80,
    "flat128": 128,
    "flat400": 400,
    "flat4000": 4000,
}


def freeze(binary: Path, directory: Path) -> dict[str, Any]:
    image = freeze_image(binary, directory.name, directory.parent)
    return {
        "binary": image.executable,
        "files_sha256": image.payload_sha256,
        "version": image.version,
        "sha256": image.sha256,
    }


def observe(binary: Path, case: str, workspace: Path) -> dict[str, Any]:
    workspace.mkdir()
    if case == "realistic":
        return response_journey(
            binary,
            workspace,
            negotiate=True,
            server_arguments=("--no-config", "--no-telemetry"),
        )
    rows = CASE_ROWS[case]
    return measure(
        binary,
        rows,
        16,
        resolve_documentation=True,
        workspace=workspace,
        server_arguments=("--no-config", "--no-telemetry"),
    )


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
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--candidate", type=Path)
    parser.add_argument("--baseline-source", default="unspecified")
    parser.add_argument("--candidate-source", default="unspecified")
    parser.add_argument("--case", action="append", choices=("realistic", *CASE_ROWS))
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--seed", type=int, default=807)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    if arguments.samples <= 0:
        parser.error("--samples must be positive")
    if arguments.binary is not None and arguments.candidate is None:
        selected: list[tuple[str, Path]] = (
            [("baseline", arguments.baseline)] if arguments.baseline is not None else []
        ) + [("candidate", arguments.binary)]
    elif (
        arguments.binary is None
        and arguments.baseline is not None
        and arguments.candidate is not None
    ):
        selected = [
            ("baseline", arguments.baseline),
            ("candidate", arguments.candidate),
        ]
    else:
        parser.error(
            "use --binary CURRENT [--baseline BEFORE], or --baseline BEFORE --candidate CURRENT"
        )
    scratch = ROOT / ".temp/editor-latency-comparison"
    scratch.mkdir(parents=True, exist_ok=True)
    root = Path(
        tempfile.mkdtemp(
            prefix="single-" if len(selected) == 1 else "paired-", dir=scratch
        )
    )
    images: dict[str, Any] = {}
    report: dict[str, Any] = {
        "status": "running",
        "samples_per_image": arguments.samples,
        "seed": arguments.seed,
        "baseline_source": arguments.baseline_source,
        "candidate_source": arguments.candidate_source,
        "images": images,
        "workspace": str(root),
        "order": "single image"
        if len(selected) == 1
        else "seeded randomized within each pair",
        "measurement": "fresh LSP session per observation; individual request and complete edit-burst clocks remain distinct",
        "records": [],
    }
    randomizer = random.Random(arguments.seed)
    records: list[dict[str, Any]] = report["records"]
    try:
        save(arguments.output, report)
        for name, path in selected:
            report["active"] = {"stage": "freeze", "image": name, "source": str(path)}
            save(arguments.output, report)
            images[name] = freeze(path.resolve(), root / name)
        for case in arguments.case or CASES:
            for sample in range(arguments.samples):
                order = list(images)
                randomizer.shuffle(order)
                for image in order:
                    current: dict[str, Any] = {
                        "case": case,
                        "sample": sample,
                        "image": image,
                    }
                    report["active"] = current
                    save(arguments.output, report)
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
    except BaseException as error:
        report["status"] = "failed"
        report["failure"] = {
            "type": type(error).__name__,
            "message": str(error),
            "traceback": traceback.format_exc(),
        }
        raise
    finally:
        save(arguments.output, report)
    print(json.dumps(report["summary"], indent=2))


if __name__ == "__main__":
    main()
