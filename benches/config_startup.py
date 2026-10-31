#!/usr/bin/env python3
"""Paired same-image configuration-discovery attribution, not an old/new benchmark."""

from __future__ import annotations

import csv
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from time import perf_counter_ns
from typing import TextIO, cast


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(binary: Path, root: Path, environment: dict[str, str], arguments: list[str]) -> float:
    started: int = perf_counter_ns()
    output: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), *arguments],
        cwd=root,
        env=environment,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        timeout=15,
        check=False,
    )
    elapsed_ms: float = (perf_counter_ns() - started) / 1_000_000
    if output.returncode:
        raise RuntimeError(output.stderr.decode("utf-8", errors="replace"))
    if (root / "output.bin").exists() or (root / ".temp").exists():
        raise RuntimeError("a static/pure attribution command initialized execution effects")
    return elapsed_ms


def percentile(samples: list[float], numerator: int) -> float:
    ordered: list[float] = sorted(samples)
    return ordered[(len(ordered) - 1) * numerator // 100]


def record_profiles(binary: Path, root: Path, environment: dict[str, str], output_root: Path) -> None:
    for disabled in (False, True):
        mode: str = "no-config" if disabled else "default"
        path: Path = output_root / f"config-startup-{mode}.jsonl"
        path.unlink(missing_ok=True)
        arguments: list[str] = ["check", "small.msync", "--trace", str(path)]
        if disabled:
            arguments.append("--no-config")
        run(binary, root, environment, arguments)
        records: list[dict[str, object]] = [
            cast(dict[str, object], json.loads(line))
            for line in path.read_text(encoding="utf-8").splitlines()
        ]
        session: dict[str, object] = next(
            record for record in records if record.get("name") == "session" and record.get("kind") == "span"
        )
        metrics: object = session.get("metrics")
        if not isinstance(metrics, dict):
            raise RuntimeError("session did not record typed startup metrics")
        selected: dict[str, object] = {
            str(key): value for key, value in metrics.items() if str(key).startswith("startup.")
        }
        print(f"profile,{mode},{json.dumps(selected, sort_keys=True)}")


def measure(binary: Path, output_path: Path, samples: int) -> None:
    repository: Path = Path(__file__).resolve().parents[1]
    temporary: Path = repository / ".temp"
    temporary.mkdir(exist_ok=True)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    initial_digest: str = digest(binary)
    with tempfile.TemporaryDirectory(prefix="config-startup-", dir=temporary) as owned:
        root: Path = Path(owned)
        (root / "source.bin").write_bytes(b"startup timing fixture")
        (root / "small.msync").write_text("source.bin -> output.bin\n", encoding="utf-8")
        (root / "pure.msync").write_text('ready = true\nlabel = "pure"\n', encoding="utf-8")
        environment: dict[str, str] = dict(os.environ)
        environment.update({
            "NO_COLOR": "1", "HOME": str(root / "home"), "USERPROFILE": str(root / "home"),
            "XDG_CONFIG_HOME": str(root / "config"), "APPDATA": str(root / "config"),
        })
        for name in ("MSYNC_TRACE", "MSYNC_STATE_DIR", "MSYNC_CONFIG"):
            environment.pop(name, None)
        stream: TextIO
        with output_path.open("w", encoding="utf-8", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(["kind", "case", "mode", "sample_or_count", "milliseconds", "p95_ms"])
            for case, arguments in (
                ("check/1", ["check", "small.msync"]),
                ("plan/1", ["plan", "small.msync"]),
                ("run/pure-bindings", ["run", "pure.msync"]),
            ):
                results: dict[str, list[float]] = {"default": [], "no-config": []}
                for iteration in range(samples + 5):
                    for disabled in ((False, True) if iteration % 2 == 0 else (True, False)):
                        mode: str = "no-config" if disabled else "default"
                        command: list[str] = [*arguments, *(["--no-config"] if disabled else [])]
                        elapsed: float = run(binary, root, environment, command)
                        if iteration >= 5:
                            results[mode].append(elapsed)
                            writer.writerow(["sample", case, mode, iteration - 5, f"{elapsed:.6f}", ""])
                for mode, durations in results.items():
                    writer.writerow(["summary", case, mode, len(durations), f"{percentile(durations, 50):.6f}", f"{percentile(durations, 95):.6f}"])
                    print(f"summary,{case},{mode},{len(durations)},{percentile(durations, 50):.6f},{percentile(durations, 95):.6f}")
        # Exporter setup is outside the timed controls; each profile is attribution, not a latency sample.
        record_profiles(binary, root, environment, output_path.parent)
    final_digest: str = digest(binary)
    if initial_digest != final_digest:
        raise RuntimeError("the measured executable changed")
    print(f"image_sha256,{initial_digest}")


def main() -> None:
    if len(sys.argv) not in (3, 4):
        raise SystemExit("usage: config_startup.py CANDIDATE OUTPUT_CSV [SAMPLES=30]")
    binary: Path = Path(sys.argv[1]).resolve(strict=True)
    output: Path = Path(sys.argv[2]).resolve()
    samples: int = int(sys.argv[3]) if len(sys.argv) == 4 else 30
    if samples < 5:
        raise SystemExit("at least five samples are required")
    measure(binary, output, samples)


if __name__ == "__main__":
    main()
