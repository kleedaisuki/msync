"""Same-artifact canonical/AOT/native recurrence comparisons."""

from __future__ import annotations

import argparse
import itertools
import json
import os
import resource
import statistics
import subprocess
import time
from dataclasses import asdict, dataclass
from pathlib import Path


@dataclass(frozen=True)
class Arm:
    label: str
    binary: Path
    native: bool


@dataclass(frozen=True)
class Sample:
    wall_ms: float
    cpu_ms: float


def run(arm: Arm, directory: Path, stdin: bytes, expected: bytes) -> Sample:
    environment: dict[str, str] = dict(os.environ)
    environment.pop("MSYNC_JIT", None)
    if not arm.native:
        environment["MSYNC_JIT"] = "off"
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    start: int = time.perf_counter_ns()
    proc: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(arm.binary), "--no-config", "--no-telemetry", "run", "baseline.mexe"],
        input=stdin,
        capture_output=True,
        check=True,
        cwd=directory,
        env=environment,
        timeout=180,
    )
    elapsed: float = (time.perf_counter_ns() - start) / 1e6
    after = resource.getrusage(resource.RUSAGE_CHILDREN)
    cpu: float = (
        after.ru_utime - before.ru_utime + after.ru_stime - before.ru_stime
    ) * 1000
    if proc.stdout != expected:
        raise AssertionError(
            f"{arm.label}/{directory}: {proc.stdout!r} != {expected!r}"
        )
    return Sample(elapsed, cpu)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, action="append", required=True)
    parser.add_argument("--result", type=Path, required=True)
    parser.add_argument("--cpu", type=int, default=10)
    parser.add_argument("--rounds", type=int, default=12)
    args: argparse.Namespace = parser.parse_args()
    os.sched_setaffinity(0, {args.cpu})
    candidate: Path = args.candidate.resolve(strict=True)
    arms: tuple[Arm, ...] = (
        Arm("canonical", args.baseline.resolve(strict=True), True),
        Arm("aot", candidate, False),
        Arm("native", candidate, True),
    )
    orders: list[tuple[Arm, ...]] = list(itertools.permutations(arms))
    report: dict[str, object] = {}
    for fixture in args.fixture:
        directory: Path = fixture.resolve(strict=True)
        stdin: bytes = (directory / "stdin.utf8").read_bytes()
        expected: bytes = (directory / "expected.utf8").read_bytes()
        for arm in arms:
            run(arm, directory, stdin, expected)
        blocks: list[dict[str, Sample]] = []
        for round_number in range(args.rounds):
            blocks.append(
                {
                    arm.label: run(arm, directory, stdin, expected)
                    for arm in orders[round_number % len(orders)]
                }
            )
        pairs: tuple[tuple[str, str], ...] = (
            ("aot", "canonical"),
            ("native", "canonical"),
            ("native", "aot"),
        )
        changes: dict[str, float] = {
            f"{left}_vs_{right}_cpu_percent": statistics.median(
                (block[left].cpu_ms / block[right].cpu_ms - 1) * 100 for block in blocks
            )
            for left, right in pairs
        }
        report[str(directory)] = {
            "changes": changes,
            "blocks": [
                {label: asdict(sample) for label, sample in block.items()}
                for block in blocks
            ],
        }
        print(directory, changes, flush=True)
        args.result.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
