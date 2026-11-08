#!/usr/bin/env python3
"""Compare fresh-process first source reads in one Native AOT probe image."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import time
from dataclasses import asdict, dataclass
from pathlib import Path

from journeys import ROOT, percentile


@dataclass(frozen=True)
class Observation:
    bytes: int
    mode: str
    milliseconds: list[float]
    p50_ms: float
    p95_ms: float


def command(binary: Path, mode: str, source: Path) -> tuple[float, bytes]:
    started = time.perf_counter_ns()
    result = subprocess.run(
        [str(binary), f"--read-source-{mode}", str(source)],
        capture_output=True,
        check=False,
    )
    elapsed = (time.perf_counter_ns() - started) / 1_000_000
    if result.returncode != 0:
        raise RuntimeError(
            f"{binary} --read-source-{mode} {source} exited {result.returncode}:\n{result.stderr.decode('utf-8', errors='replace')}"
        )
    return elapsed, result.stdout


def fixture(root: Path, count: int) -> tuple[Path, dict[str, object]]:
    unit = "# 可莉𐐀 UTF-8 source\n".encode()
    payload = unit * (count // len(unit)) + b" " * (count % len(unit))
    source = root / f"source-{count}.msync"
    source.write_bytes(payload)
    text = payload.decode("utf-8")
    return source, {
        "path": str(source),
        "bytes": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
        "utf16_length": len(text.encode("utf-16-le")) // 2,
        "decoded_utf8_sha256": hashlib.sha256(text.encode("utf-8")).hexdigest().upper(),
    }


def measure(
    binary: Path, source: Path, metadata: dict[str, object], samples: int, warmups: int
) -> list[Observation]:
    _, verified = command(binary, "check", source)
    expected = f"{metadata['utf16_length']} {metadata['decoded_utf8_sha256']}"
    assert verified.decode().strip() == expected, (verified, expected)
    values: dict[str, list[float]] = {"sync": [], "async": []}
    for iteration in range(samples + warmups):
        order = ("sync", "async") if iteration % 2 == 0 else ("async", "sync")
        for mode in order:
            elapsed, output = command(binary, mode, source)
            assert output.decode().strip() == str(metadata["utf16_length"]), (
                mode,
                output,
            )
            if iteration >= warmups:
                values[mode].append(elapsed)
    observations = [
        Observation(
            source.stat().st_size,
            mode,
            times,
            percentile(times, 0.5),
            percentile(times, 0.95),
        )
        for mode, times in values.items()
    ]
    for item in observations:
        print(
            f"{item.bytes:7d} B {item.mode:5s} p50={item.p50_ms:.3f} ms p95={item.p95_ms:.3f} ms",
            flush=True,
        )
    assert hashlib.sha256(source.read_bytes()).hexdigest() == metadata["sha256"], (
        "Source changed during read comparison"
    )
    return observations


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--binary",
        type=Path,
        required=True,
        help="Published tests/Performance/Components native probe",
    )
    parser.add_argument("--samples", type=int, default=31)
    parser.add_argument("--warmups", type=int, default=3)
    parser.add_argument("--bytes", type=int, action="append")
    parser.add_argument(
        "--output",
        type=Path,
        default=ROOT / ".temp/performance-v080/current/source-read.json",
    )
    options = parser.parse_args()
    output = options.output.resolve()
    root = output.parent / f"source-read-{time.time_ns()}"
    root.mkdir(parents=True)
    origin = options.binary.resolve()
    binary = root / origin.name
    shutil.copy2(origin, binary)
    identity = hashlib.sha256(binary.read_bytes()).hexdigest()
    report: dict[str, object] = {
        "image": {
            "origin": str(origin),
            "frozen": str(binary),
            "sha256": identity,
            "bytes": binary.stat().st_size,
        },
        "environment": {
            "platform": platform.platform(),
            "logical_cpus": os.cpu_count(),
            "python": platform.python_version(),
        },
        "method": {
            "samples": options.samples,
            "warmups": options.warmups,
            "process": "one fresh native process per first read, same executable for both modes",
            "cache": "warm source/OS page cache; first BCL read in each process",
            "timing": "process creation to actual UTF-16 count response; SHA256/full-string equality checked separately",
            "order": "alternating sync/async per paired sample",
            "percentiles": "linearly interpolated empirical p50/p95",
        },
    }
    observations: list[Observation] = []
    sources: list[dict[str, object]] = []
    for count in options.bytes or (4096, 65536):
        source, metadata = fixture(root, count)
        sources.append(metadata)
        try:
            observations.extend(
                measure(binary, source, metadata, options.samples, options.warmups)
            )
        except (RuntimeError, AssertionError, OSError) as error:
            report["failure"] = {
                "source": metadata,
                "type": type(error).__name__,
                "message": str(error),
            }
            report["observations"] = [asdict(item) for item in observations]
            report["sources"] = sources
            output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            raise
        report["observations"] = [asdict(item) for item in observations]
        report["sources"] = sources
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    assert hashlib.sha256(binary.read_bytes()).hexdigest() == identity, (
        "Probe changed during measurement"
    )
    print(output)


if __name__ == "__main__":
    main()
