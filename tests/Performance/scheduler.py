#!/usr/bin/env python3
"""Measure real 0.8 CLI copy frontiers and dependency chains, not a scheduler mock.

Fixture preparation and exact verification are outside timing. Each invocation
overwrites its source with a new fixed-length generation token, so stale outputs
cannot pass. Warm overwrite samples alternate AB/BA; no cache eviction or shell
is involved. The timings include startup, compilation, scheduling, I/O and CLI
output capture. This is development infrastructure, never a product dependency.
P95 linearly interpolates sorted samples at index (n - 1) * 0.95 (type 7).
Seven samples give only a coarse tail estimate; sample count and maximum remain
alongside it, and the raw samples are retained.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import statistics
import subprocess
import sys
import time
from collections.abc import Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Image:
    name: str
    path: str
    sha256: str
    version: str


@dataclass(frozen=True)
class Sample:
    image: str
    case: str
    size: int
    iteration: int
    warmup: bool
    elapsed_ms: float
    generation: str
    stdout: str
    stderr: str


def image(name: str, path: Path) -> Image:
    path = path.resolve(strict=True)
    result = subprocess.run(
        [str(path), "--version"],
        capture_output=True,
        check=True,
        text=True,
        encoding="utf-8",
    )
    return Image(
        name,
        str(path),
        hashlib.sha256(path.read_bytes()).hexdigest(),
        result.stdout.strip(),
    )


def prepare(directory: Path, case: str, size: int) -> tuple[Path, list[Path]]:
    directory.mkdir(parents=True, exist_ok=True)
    outputs = directory / "outputs"
    outputs.mkdir(exist_ok=True)
    destinations = [outputs / f"item{index:06d}.bin" for index in range(size)]
    previous = "./input.bin"
    lines: list[str] = []
    for destination in destinations:
        target = f"./outputs/{destination.name}"
        lines.append(f"{previous} -> {target};")
        if case == "chain":
            previous = target
    script = directory / "workload.msync"
    script.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return script, destinations


def invoke(
    selected: Image,
    case: str,
    size: int,
    iteration: int,
    warmup: bool,
    script: Path,
    destinations: Sequence[Path],
    jobs: int,
    generation: int,
) -> Sample:
    token = f"{generation:064x}"
    payload = token.encode("ascii")
    (script.parent / "input.bin").write_bytes(payload)
    command = [selected.path, "run", str(script), "--jobs", str(jobs)]
    started = time.perf_counter_ns()
    result = subprocess.run(
        command,
        cwd=script.parent,
        capture_output=True,
        check=False,
    )
    elapsed = (time.perf_counter_ns() - started) / 1_000_000
    stdout = result.stdout.decode("utf-8", errors="replace")
    stderr = result.stderr.decode("utf-8", errors="replace")
    if result.returncode != 0:
        raise RuntimeError(
            f"{command!r} exited {result.returncode} after {elapsed:.3f} ms\n"
            f"stdout:\n{stdout}\nstderr:\n{stderr}"
        )
    for destination in destinations:
        actual = destination.read_bytes()
        if actual != payload:
            raise RuntimeError(
                f"{selected.name} {case}/{size}: {destination} has stale or "
                f"incorrect bytes: expected {payload!r}, found {actual!r}"
            )
    return Sample(
        selected.name, case, size, iteration, warmup, elapsed, token, stdout, stderr
    )


def percentile(values: Sequence[float], quantile: float) -> float:
    ordered = sorted(values)
    position = (len(ordered) - 1) * quantile
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def summaries(samples: Sequence[Sample]) -> list[dict[str, object]]:
    groups: dict[tuple[str, str, int], list[float]] = {}
    for sample in samples:
        if not sample.warmup:
            groups.setdefault((sample.image, sample.case, sample.size), []).append(
                sample.elapsed_ms
            )
    return [
        {
            "image": name,
            "case": case,
            "size": size,
            "samples": len(values),
            "median_ms": statistics.median(values),
            "p95_ms": percentile(values, 0.95),
            "minimum_ms": min(values),
            "maximum_ms": max(values),
            "milliseconds": values,
        }
        for (name, case, size), values in groups.items()
    ]


def save(path: Path, report: dict[str, object], samples: Sequence[Sample]) -> None:
    report["samples"] = [asdict(sample) for sample in samples]
    report["summary"] = summaries(samples)
    path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", type=Path)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--sizes", type=int, nargs="+", default=[1, 100, 1000, 5000])
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--warmups", type=int, default=2)
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--case", choices=["independent", "chain"], action="append")
    parser.add_argument(
        "--output",
        type=Path,
        required=True,
        help="JSON report path beneath repository .temp",
    )
    options = parser.parse_args()
    output = options.output.resolve()
    if not output.is_relative_to(ROOT / ".temp"):
        parser.error("--output must be beneath repository .temp")
    if min(options.sizes) < 1 or options.samples < 1 or options.warmups < 0:
        parser.error("sizes/samples must be positive; warmups must be nonnegative")
    output.parent.mkdir(parents=True, exist_ok=True)
    images: list[Image] = []
    if options.before is not None:
        images.append(image("before", options.before))
    images.append(image("after", options.after))
    samples: list[Sample] = []
    report: dict[str, object] = {
        "status": "running",
        "platform": platform.platform(),
        "python": {"executable": sys.executable, "version": sys.version},
        "images": [asdict(selected) for selected in images],
        "jobs": options.jobs,
        "warmups": options.warmups,
        "measurement_samples": options.samples,
        "percentile_method": "type 7: linear interpolation at sorted index (n - 1) * p; "
        "small sample tails are coarse estimates",
        "method": "fresh 64-byte source per invocation; warm overwrite; AB/BA; "
        "all output bytes verified outside timing; captured CLI output",
    }
    save(output, report, samples)
    generation = time.time_ns()
    try:
        for case in options.case or ["independent", "chain"]:
            for size in options.sizes:
                directory = output.parent / f"{output.stem}-fixtures" / case / str(size)
                script, destinations = prepare(directory, case, size)
                for iteration in range(options.warmups + options.samples):
                    order = images if iteration % 2 == 0 else list(reversed(images))
                    for selected in order:
                        generation += 1
                        sample = invoke(
                            selected,
                            case,
                            size,
                            iteration,
                            iteration < options.warmups,
                            script,
                            destinations,
                            options.jobs,
                            generation,
                        )
                        samples.append(sample)
                        print(
                            f"{selected.name} {case}/{size} "
                            f"{'warmup' if sample.warmup else 'sample'} "
                            f"{sample.elapsed_ms:.3f} ms",
                            flush=True,
                        )
                    save(output, report, samples)
        report["status"] = "complete"
    except Exception as error:
        report["status"] = "failed"
        report["failure"] = str(error)
        raise
    finally:
        save(output, report, samples)


if __name__ == "__main__":
    main()
