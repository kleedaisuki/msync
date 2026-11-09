"""Alternate source-matched allocation probes and retain their raw observations."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
from typing import Any


def command(image: Path) -> list[str]:
    return ["dotnet", str(image)] if image.suffix == ".dll" else [str(image)]


def identity(image: Path) -> dict[str, str]:
    result: dict[str, str] = {"image": str(image), "sha256": hashlib.sha256(image.read_bytes()).hexdigest()}
    for name in ("Msync.Core.dll", "Msync.Compiler.dll", "Msync.Runtime.dll"):
        dependency: Path = image.parent / name
        if dependency.exists():
            result[name] = hashlib.sha256(dependency.read_bytes()).hexdigest()
    return result


def quantile(samples: list[float], probability: float) -> float:
    ordered: list[float] = sorted(samples)
    position: float = (len(ordered) - 1) * probability
    lower: int = int(position)
    upper: int = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--control", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--pairs", default=3, type=int)
    parser.add_argument("--samples", default=9, type=int)
    parser.add_argument("--iterations", default=16, type=int)
    parser.add_argument("--size", default=256, type=int)
    parser.add_argument("--case", action="append", dest="cases")
    args: argparse.Namespace = parser.parse_args()
    output: Path = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    images: dict[str, Path] = {"control": args.control.resolve(), "candidate": args.candidate.resolve()}
    cases: list[str] = args.cases or ["direct", "swap", "captures", "objects", "paths", "literal"]
    summary: dict[str, Any] = {"images": {label: identity(image) for label, image in images.items()}, "cases": {}}
    for case in cases:
        results: dict[str, list[dict[str, Any]]] = {"control": [], "candidate": []}
        for pair in range(args.pairs):
            order: tuple[str, str] = ("control", "candidate") if pair % 2 == 0 else ("candidate", "control")
            for label in order:
                destination: Path = output / f"{case}-{pair}-{label}.json"
                invocation: list[str] = command(images[label]) + [
                    "--case", case, "--size", str(args.size), "--samples", str(args.samples),
                    "--iterations", str(args.iterations), "--output", str(destination),
                    "--root", str(output / "work"),
                ]
                subprocess.run(invocation, check=True)
                results[label].append(json.loads(destination.read_text()))
        row: dict[str, Any] = {}
        for label in images:
            times: list[float] = [time for result in results[label] for time in result["Milliseconds"]]
            row[label] = {
                "p50_ms": quantile(times, 0.5), "p95_ms": quantile(times, 0.95),
                "allocated_bytes": statistics.median(result["AllocatedBytesPerOperation"] for result in results[label]),
                "artifact_sha256": sorted({result["ArtifactSha256"] for result in results[label]}),
            }
        if row["control"]["artifact_sha256"] != row["candidate"]["artifact_sha256"]:
            raise RuntimeError(f"{case}: compared different executable bytecode")
        summary["cases"][case] = row
        print(case, row, flush=True)
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")


if __name__ == "__main__":
    main()
