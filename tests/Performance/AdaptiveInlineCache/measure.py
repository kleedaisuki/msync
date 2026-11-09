"""Alternate isolated Native AOT field-cache images and retain raw observations."""

from __future__ import annotations

import argparse
import hashlib
import json
import statistics
import subprocess
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[3]


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def quantile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def run(image: Path, output: Path, samples: int, iterations: int) -> dict[str, Any]:
    subprocess.run(
        [str(image), "--measure", str(output), str(samples), str(iterations)],
        cwd=ROOT,
        check=True,
        timeout=120,
    )
    return json.loads(output.read_text(encoding="utf-8"))  # type: ignore[no-any-return]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--control", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pairs", type=int, default=3)
    parser.add_argument("--samples", type=int, default=9)
    parser.add_argument("--iterations", type=int, default=4096)
    args = parser.parse_args()
    output: Path = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    images: dict[str, Path] = {
        "control": args.control.resolve(),
        "candidate": args.candidate.resolve(),
    }
    observations: dict[str, list[dict[str, Any]]] = {name: [] for name in images}
    for pair in range(args.pairs):
        order = ("control", "candidate") if pair % 2 == 0 else ("candidate", "control")
        for variant in order:
            observation = run(
                images[variant],
                output / f"{pair}-{variant}.json",
                args.samples,
                args.iterations,
            )
            observations[variant].append(observation)
    cases = observations["control"][0]["Shapes"]
    summary: list[dict[str, Any]] = []
    for case in cases:
        name: str = case["Name"]
        record: dict[str, Any] = {"case": name, "shape": case}
        for variant in images:
            times: list[float] = []
            allocations: list[int] = []
            for observation in observations[variant]:
                item = next(
                    row for row in observation["Measurements"] if row["Name"] == name
                )
                times.extend(item["Milliseconds"])
                allocations.append(item["AllocatedBytes"])
                shape = next(
                    row for row in observation["Shapes"] if row["Name"] == name
                )
                if shape != case:
                    raise RuntimeError(f"Different optimized bytecode shape for {name}")
            record[variant] = {
                "p50_ms": quantile(times, 0.5),
                "p95_ms": quantile(times, 0.95),
                "allocated_bytes_median": statistics.median(allocations),
                "samples": times,
            }
        summary.append(record)
    report: dict[str, Any] = {
        "images": {
            name: {"path": str(path), "sha256": digest(path)}
            for name, path in images.items()
        },
        "pairs": args.pairs,
        "samples_per_process": args.samples,
        "iterations": args.iterations,
        "summary": summary,
    }
    (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
