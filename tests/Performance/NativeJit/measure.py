"""Compare fresh CLI source/object/bound runs with the product guest JIT on and off."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import statistics
import tempfile
from pathlib import Path
from time import perf_counter_ns

from journeys import HERE, ROOT, Input, invoke, merge, nested, recurrence

Samples = tuple[float, float]


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def compare(
    binary: Path,
    directory: Path,
    arguments: tuple[str, ...],
    example: Input,
    pairs: int,
    warmups: int,
) -> dict[str, object]:
    data: bytes = example.stdin()
    measured: list[Samples] = []
    for pair in range(-warmups, pairs):
        times: dict[bool, float] = {}
        order: tuple[bool, bool] = (False, True) if pair % 2 == 0 else (True, False)
        for native in order:
            start: int = perf_counter_ns()
            stdout: bytes = invoke(
                binary, directory, arguments, data=data, native=native
            )
            times[native] = (perf_counter_ns() - start) / 1_000_000
            example.check(stdout)
        if pair >= 0:
            measured.append((times[False], times[True]))
    return {
        "pairs_ms": measured,
        "off_p50_ms": statistics.median(off for off, _ in measured),
        "auto_p50_ms": statistics.median(auto for _, auto in measured),
        "paired_median_delta_ms": statistics.median(
            auto - off for off, auto in measured
        ),
        "faster_pairs": sum(auto < off for off, auto in measured),
    }


def measure(
    binary: Path,
    work: Path,
    fixture: str,
    iterations: int,
    width: int,
    pairs: int,
    warmups: int,
) -> dict[str, object]:
    sample = merge if fixture == "merge" else recurrence
    hot: Input = (
        nested(iterations, width, -1.125)
        if fixture == "nested"
        else sample(iterations, -1.125)
    )
    short: Input = (
        nested(3, width, -1.125) if fixture == "nested" else sample(3, -1.125)
    )
    reports: dict[str, object] = {}
    receipt: dict[str, object] = {
        "binary": str(binary),
        "binary_sha256": file_hash(binary),
        "platform": platform.platform(),
        "cpu_affinity": (
            sorted(os.sched_getaffinity(0))
            if hasattr(os, "sched_getaffinity")
            else None
        ),
        "fixture": fixture,
        "iterations": iterations,
        "width": width if fixture == "nested" else None,
        "pairs": pairs,
        "warmups": warmups,
        "routes": reports,
        "status": "running",
    }
    path: Path = work / "result.json"

    def route(name: str, cwd: Path, args: tuple[str, ...], input_file: Path) -> None:
        reports[name] = {
            "input_sha256": file_hash(input_file),
            "hot": compare(
                binary,
                cwd,
                ("--no-telemetry", *args),
                hot,
                pairs,
                warmups,
            ),
            "short": compare(
                binary,
                cwd,
                ("--no-telemetry", *args),
                short,
                pairs,
                warmups,
            ),
        }
        path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        print(f"PASS {name}", flush=True)

    try:
        source: Path = work / "program.msync"
        source.write_bytes((HERE / f"{fixture}.msync").read_bytes())
        route("source", work, (source.name,), source)
        isolated: Path = work / "artifact-only"
        isolated.mkdir()
        portable: Path = isolated / "program.mso"
        invoke(
            binary,
            work,
            ["--no-telemetry", "-O2", "compile", source.name, "-o", str(portable)],
        )
        source.unlink()
        route("object", isolated, ("run", portable.name), portable)
        bound: Path = isolated / "program.mexe"
        invoke(
            binary,
            isolated,
            ["--no-telemetry", "bind-ir", portable.name, "-o", bound.name],
        )
        route("bound", isolated, ("run", bound.name), bound)
        receipt["status"] = "complete"
    except BaseException as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return receipt


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument(
        "--fixture", choices=("recurrence", "nested", "merge"), default="recurrence"
    )
    parser.add_argument("--iterations", type=int)
    parser.add_argument("--width", type=int, default=19)
    parser.add_argument("--pairs", type=int, default=9)
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--cpu", type=int)
    parser.add_argument("--work", type=Path)
    args: argparse.Namespace = parser.parse_args()
    iterations: int = (
        args.iterations
        if args.iterations is not None
        else (60_001 if args.fixture == "nested" else 1_048_576)
    )
    if iterations < 1 or args.width < 1 or args.pairs < 1 or args.warmups < 0:
        parser.error(
            "iterations, width and pairs must be positive; warmups cannot be negative"
        )
    if args.cpu is not None:
        if not hasattr(os, "sched_setaffinity"):
            parser.error("--cpu requires a host with sched_setaffinity")
        os.sched_setaffinity(0, {args.cpu})
    binary: Path = args.binary.resolve(strict=True)
    temp: Path = ROOT / ".temp"
    temp.mkdir(exist_ok=True)
    work: Path = (
        args.work.resolve()
        if args.work
        else Path(tempfile.mkdtemp(prefix="native-jit-measure-", dir=temp))
    )
    if not work.is_relative_to(temp) or work.exists() and any(work.iterdir()):
        parser.error(
            "--work must be a new or empty directory under repository-root .temp"
        )
    work.mkdir(parents=True, exist_ok=True)
    print(
        json.dumps(
            measure(
                binary,
                work,
                args.fixture,
                iterations,
                args.width,
                args.pairs,
                args.warmups,
            ),
            indent=2,
        ),
        flush=True,
    )


if __name__ == "__main__":
    main()
