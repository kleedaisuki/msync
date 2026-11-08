#!/usr/bin/env python3
"""Compare frozen native optimizer probes with alternating equivalent phases.

Compile the same Components probe source against the baseline and candidate
product revisions. Each paired observation uses a fresh process, warms each API,
and retains compile/optimize/bind/run distributions separately. These cumulative
stages are not added together. Product assembly identities are in every report.
"""

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
CASES = ("calls", "scope_calls", "finite_loop", "branch_reuse")


@dataclass(frozen=True)
class Image:
    name: str
    path: str
    source: str
    payload_sha256: dict[str, str]


def freeze(name: str, binary: Path, source: str, directory: Path) -> Image:
    directory.mkdir(parents=True)
    hashes: dict[str, str] = {}
    for path in sorted(binary.parent.iterdir()):
        if path.is_file():
            destination = directory / path.name
            shutil.copy2(path, destination)
            hashes[path.name] = hashlib.sha256(destination.read_bytes()).hexdigest()
    return Image(name, str(directory / binary.name), source, hashes)


def distribution(values: Sequence[float]) -> dict[str, object]:
    ordered = sorted(values)
    position = (len(values) - 1) * 0.95
    lower = int(position)
    upper = min(lower + 1, len(values) - 1)
    return {
        "samples": len(values),
        "p50": statistics.median(values),
        "p95": ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower),
        "min": ordered[0],
        "max": ordered[-1],
        "raw": values,
    }


def summaries(records: list[dict[str, Any]]) -> list[dict[str, object]]:
    groups: dict[tuple[str, str, int, int, str], tuple[list[float], list[float]]] = {}
    for record in records:
        for observation in record["report"]["Observations"]:
            key = (
                record["image"],
                observation["Workload"],
                observation["Size"],
                observation["Level"],
                observation["Stage"],
            )
            times, allocations = groups.setdefault(key, ([], []))
            times.extend(observation["Milliseconds"])
            allocations.append(observation["AllocatedBytesPerOperation"])
    return [
        {
            "image": image,
            "case": case,
            "size": size,
            "level": level,
            "stage": stage,
            "milliseconds": distribution(times),
            "allocated_bytes_per_operation": distribution(allocations),
        }
        for (image, case, size, level, stage), (times, allocations) in groups.items()
    ]


def observe(
    image: Image,
    case: str,
    size: int,
    warmups: int,
    levels: Sequence[int],
    directory: Path,
) -> dict[str, Any]:
    directory.mkdir()
    output = directory / "components.json"
    command = [
        image.path,
        "--optimizer-pipeline",
        "--root",
        str(ROOT),
        "--cases",
        case,
        "--sizes",
        str(size),
        "--levels",
        ",".join(str(level) for level in levels),
        "--samples",
        "1",
        "--warmups",
        str(warmups),
        "--output",
        str(output),
    ]
    result = subprocess.run(command, capture_output=True, check=False)
    (directory / "stdout.txt").write_bytes(result.stdout)
    (directory / "stderr.txt").write_bytes(result.stderr)
    if result.returncode != 0:
        raise RuntimeError(
            f"{command!r} exited {result.returncode}\n"
            + result.stderr.decode("utf-8", errors="replace")
        )
    report: dict[str, Any] = json.loads(output.read_text(encoding="utf-8"))
    expected = {
        (level, stage) for level in levels for stage in ("relocatable", "executable")
    }
    actual = {(shape["Level"], shape["Stage"]) for shape in report["Shapes"]}
    if actual != expected:
        raise RuntimeError(f"Incomplete phase report: {actual} != {expected}")
    return report


def save(path: Path, report: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--baseline-source", required=True)
    parser.add_argument("--candidate-source", required=True)
    parser.add_argument(
        "--case", action="append", choices=(*CASES, "call_dag", "transfers")
    )
    parser.add_argument(
        "--candidate-case", action="append", choices=("mutable_scopes", "finite_sum")
    )
    parser.add_argument("--sizes", nargs="+", type=int, default=[1, 32, 128])
    parser.add_argument("--levels", nargs="+", type=int, default=[0, 1, 2, 3])
    parser.add_argument("--samples", type=int, default=15)
    parser.add_argument("--warmups", type=int, default=3)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    scratch = ROOT / ".temp/optimizer-performance"
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
        "python": {"executable": sys.executable, "version": sys.version},
        "workspace": str(workspace),
        "images": [asdict(image) for image in images],
        "method": {
            "samples_per_image": options.samples,
            "warmups_per_api": options.warmups,
            "order": "AB/BA fresh processes; warmed in-process phases",
            "p95": "type 7 linear interpolation",
            "correctness": "actual Path/Number oracle and codec roundtrip; "
            "transfer compiler cases validate typed artifacts without executing I/O",
        },
        "records": records,
    }
    cases = [(case, images) for case in options.case or CASES]
    cases.extend((case, [images[1]]) for case in options.candidate_case or [])
    try:
        for case, selected in cases:
            for size in options.sizes:
                for sample in range(options.samples):
                    order = selected if sample % 2 == 0 else list(reversed(selected))
                    for image in order:
                        active = {
                            "case": case,
                            "size": size,
                            "sample": sample,
                            "image": image.name,
                        }
                        report["active"] = active
                        save(options.output, report)
                        current = observe(
                            image,
                            case,
                            size,
                            options.warmups,
                            options.levels,
                            workspace / f"{case}-{size}-{sample}-{image.name}",
                        )
                        records.append({**active, "report": current})
                        report["summary"] = summaries(records)
                        save(options.output, report)
                print(f"Completed {case}/{size}", flush=True)
        report["status"] = "complete"
        report.pop("active", None)
    except Exception as error:
        report["status"] = "failed"
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
        save(options.output, report)
        raise
    save(options.output, report)


if __name__ == "__main__":
    main()
