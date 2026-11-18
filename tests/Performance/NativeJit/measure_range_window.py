"""Compare actual Range windows using the same detached artifact in both arms."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import statistics
import subprocess
import time
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[3]
CLI: tuple[str, ...] = ("--no-config", "--no-telemetry", "--color", "never")


def freeze(binary: Path, work: Path) -> Path:
    source: Path = work / "window.msync"
    shutil.copyfile(Path(__file__).with_name("range-window.msync"), source)
    for step in (
        ("compile", "-O2", "window.msync", "-o", "window.mso"),
        ("bind-ir", "window.mso", "-o", "window.mexe"),
    ):
        subprocess.run([str(binary), *CLI, *step], cwd=work, check=True, timeout=30, capture_output=True)
    source.unlink()
    (work / "window.mso").unlink()
    return work / "window.mexe"


def measure(binary: Path, artifact: Path, count: int, expected: bytes) -> int:
    start: int = time.perf_counter_ns()
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), *CLI, "run", str(artifact)], cwd=artifact.parent, input=f"{count}\n".encode(),
        env={**os.environ, "MSYNC_JIT": "auto"}, capture_output=True, check=True, timeout=60,
    )
    elapsed: int = time.perf_counter_ns() - start
    if result.stdout != expected or result.stderr:
        raise AssertionError((binary, result.stdout, result.stderr))
    return elapsed


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--cpu", type=int)
    parser.add_argument("--count", type=int, default=1_500_000)
    parser.add_argument("--pairs", type=int, default=5)
    parser.add_argument("--output", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    if args.count < 1 or args.count > 50_000_000 or args.pairs < 1:
        parser.error("use a positive count within the exact Number sum domain and at least one pair")
    if args.cpu is not None:
        os.sched_setaffinity(0, {args.cpu})
    paths: tuple[Path, Path] = args.baseline.resolve(strict=True), args.candidate.resolve(strict=True)
    expected: bytes = f"{args.count * (2 * args.count - 1)}\n".encode()
    arms: tuple[list[int], list[int]] = ([], [])
    (ROOT / ".temp").mkdir(exist_ok=True)
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="range-window-bench-") as directory:
        artifact: Path = freeze(paths[1], Path(directory))
        for pair in range(args.pairs):
            order: tuple[int, int] = (0, 1) if pair % 2 == 0 else (1, 0)
            for arm in order:
                arms[arm].append(measure(paths[arm], artifact, args.count, expected))
        result: dict[str, object] = {
            "artifact_sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(),
            "binary_sha256": [hashlib.sha256(path.read_bytes()).hexdigest() for path in paths],
            "binaries": [str(path) for path in paths], "count": args.count, "cpu": args.cpu, "jit": "auto",
            "samples_ns": arms, "medians_ms": [statistics.median(samples) / 1e6 for samples in arms],
            "paired_median_percent": statistics.median((candidate / baseline - 1) * 100 for baseline, candidate in zip(*arms)),
        }
    args.output.write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
