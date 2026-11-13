"""Compare checked middle-end policies on one frozen Linux Native AOT CLI."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import resource
import shutil
import statistics
import subprocess
import time
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def invoke(
    binary: Path, artifact: Path, data: bytes, expected: bytes
) -> dict[str, float]:
    environment: dict[str, str] = dict(os.environ)
    environment["MSYNC_JIT"] = "off"
    before: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    started: int = time.perf_counter_ns()
    process: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "run", str(artifact)],
        input=data,
        capture_output=True,
        check=False,
        timeout=90,
        env=environment,
    )
    elapsed: float = (time.perf_counter_ns() - started) / 1_000_000
    after: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    if process.returncode != 0 or process.stdout != expected or process.stderr:
        raise AssertionError(
            (process.args, process.returncode, process.stdout, process.stderr)
        )
    return {
        "wall_ms": elapsed,
        "cpu_ms": 1000
        * (after.ru_utime + after.ru_stime - before.ru_utime - before.ru_stime),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--stdin", type=Path, required=True)
    parser.add_argument("--expected", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--pairs", type=int, default=7)
    options: argparse.Namespace = parser.parse_args()
    work: Path = options.work.resolve()
    if (
        not work.is_relative_to(ROOT / ".temp")
        or work == ROOT / ".temp"
        or work.exists()
    ):
        parser.error("--work must be a new child of repository-root .temp")
    if options.pairs < 1:
        parser.error("--pairs must be positive")
    source: Path = options.binary.resolve(strict=True)
    before_artifact: Path = options.before.resolve(strict=True)
    after_artifact: Path = options.after.resolve(strict=True)
    data: bytes = options.stdin.read_bytes()
    expected: bytes = options.expected.read_bytes()
    work.mkdir(parents=True)
    image: Path = work / "image"
    image.mkdir()
    for payload in source.parent.iterdir():
        if payload.is_file() and (
            payload == source
            or payload.suffix in {".so", ".dll"}
            or payload.name
            in {source.stem + ".deps.json", source.stem + ".runtimeconfig.json"}
        ):
            shutil.copy2(payload, image / payload.name)
    binary: Path = image / source.name
    artifacts: dict[str, Path] = {}
    for name, path in (("before", before_artifact), ("after", after_artifact)):
        artifacts[name] = work / f"{name}.mexe"
        shutil.copy2(path.resolve(strict=True), artifacts[name])
    hashes: dict[str, str] = {path.name: sha(path) for path in image.iterdir()}
    (work / "stdin.utf8").write_bytes(data)
    (work / "expected.utf8").write_bytes(expected)
    samples: dict[str, list[dict[str, float]]] = {"before": [], "after": []}
    for name, artifact in artifacts.items():
        invoke(binary, artifact, data, expected)
    for pair in range(options.pairs):
        for name in ("before", "after") if pair % 2 == 0 else ("after", "before"):
            samples[name].append(invoke(binary, artifacts[name], data, expected))
    if hashes != {path.name: sha(path) for path in image.iterdir()}:
        raise AssertionError("Frozen executable payload changed")
    report: dict[str, Any] = {
        "platform": platform.platform(),
        "affinity": sorted(os.sched_getaffinity(0)),
        "binary": str(source),
        "payload_sha256": hashes,
        "artifacts": {
            name: {"path": str(path), "sha256": sha(path)}
            for name, path in artifacts.items()
        },
        "stdin_sha256": hashlib.sha256(data).hexdigest(),
        "expected_stdout": expected.decode(),
        "boundary": "Fresh Native AOT CLI run of a bound artifact; no compile/prepare in timed window",
        "warmups": 1,
        "jit_policy": "off",
        "pairs": options.pairs,
        "samples": samples,
        "median": {
            name: {
                metric: statistics.median(sample[metric] for sample in values)
                for metric in ("wall_ms", "cpu_ms")
            }
            for name, values in samples.items()
        },
    }
    (work / "result.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(report["median"], indent=2))


if __name__ == "__main__":
    main()
