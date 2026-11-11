"""Measure real native path observations while unrelated copies await a producer."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import select
import statistics
import subprocess
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import IO


@dataclass(frozen=True)
class Sample:
    wall_ms: float
    process_cpu_ms: float


def build(command: list[str], directory: Path, copies: int, queries: int) -> None:
    directory.mkdir()
    (directory / "input.txt").write_bytes(b"observed")
    (directory / "wait.sh").write_text(
        "set -eu\nwhile [ ! -e gate ]; do sleep .01; done\nprintf held\n"
    )
    (directory / "case.msync").write_text(
        f"""let child = $std::process::spawn(/bin/sh, ["./wait.sh"]);
child::stdout -> ./held.txt;
for i in $std::ranges::iota(0, {copies}) {{
  ./held.txt -> $std::path::join(./outputs, i as String);
}};
$std::io::println("begin");
$std::io::flush();
let seen = 0;
for i in $std::ranges::iota(0, {queries}) {{
  if $std::fs::exists(./input.txt) {{ seen = seen + 1; }};
}};
$std::io::println(seen as String);
$std::io::flush();
acquire child;
"""
    )
    for args in (
        ["compile", "case.msync", "-o", "case.mso"],
        ["bind-ir", "case.mso", "-o", "case.mexe"],
    ):
        subprocess.run([*command, *args], cwd=directory, check=True, timeout=30)
    artifact: dict[str, object] = json.loads(
        subprocess.check_output(
            [*command, "disassemble", "case.mexe", "--json"],
            cwd=directory,
            timeout=10,
        )
    )
    assert not artifact["executionPlans"], artifact["executionPlans"]


def read_line(stream: IO[bytes]) -> bytes:
    assert select.select([stream], [], [], 30)[0], "No progress marker"
    return stream.readline().strip()


def process_cpu_ticks(pid: int) -> int:
    # Fields 14/15 are utime and stime; do not split the parenthesized command.
    tail: list[str] = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
    return int(tail[11]) + int(tail[12])


def sample(command: list[str], directory: Path, copies: int, queries: int) -> Sample:
    (directory / "gate").unlink(missing_ok=True)
    (directory / "held.txt").unlink(missing_ok=True)
    if (directory / "outputs").exists():
        for output in (directory / "outputs").iterdir():
            output.unlink()
    with subprocess.Popen(
        [*command, "run-ir", "case.mexe"],
        cwd=directory,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        bufsize=0,
    ) as child:
        assert child.stdout is not None
        try:
            assert read_line(child.stdout) == b"begin"
            assert not (directory / "held.txt").exists()
            start_ticks: int = process_cpu_ticks(child.pid)
            start_ns: int = time.perf_counter_ns()
            result: bytes = read_line(child.stdout)
            elapsed_ns: int = time.perf_counter_ns() - start_ns
            elapsed_ticks: int = process_cpu_ticks(child.pid) - start_ticks
            assert result == str(queries).encode(), result
            assert not (directory / "held.txt").exists()
            assert not (directory / "outputs/0").exists()
        finally:
            (directory / "gate").touch()
            stdout, stderr = child.communicate(timeout=45)
        assert child.returncode == 0, (stdout, stderr)
    assert (directory / "held.txt").read_bytes() == b"held"
    assert all(
        (directory / "outputs" / str(i)).read_bytes() == b"held" for i in range(copies)
    )
    return Sample(elapsed_ns / 1e6, 1000.0 * elapsed_ticks / os.sysconf("SC_CLK_TCK"))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--copies", type=int, nargs="+", default=[0, 1, 1024, 4096])
    parser.add_argument("--queries", type=int, default=4000)
    parser.add_argument("--rounds", type=int, default=3)
    args: argparse.Namespace = parser.parse_args()

    binary: Path = args.binary.resolve()
    command: list[str] = [str(binary), "--no-config", "--no-telemetry"]
    workspace: Path = args.workspace.resolve()
    workspace.mkdir(parents=True)
    cases: list[int] = args.copies
    queries: int = args.queries
    rounds: int = args.rounds
    directories: dict[int, Path] = {count: workspace / f"p{count}" for count in cases}
    for count, directory in directories.items():
        build(command, directory, count, queries)
    observations: dict[int, list[Sample]] = {count: [] for count in cases}
    for round_index in range(rounds):
        for count in cases if round_index % 2 == 0 else reversed(cases):
            observation: Sample = sample(command, directories[count], count, queries)
            observations[count].append(observation)
            print(count, observation, flush=True)
    receipt: dict[str, object] = {
        "binary_version": subprocess.check_output(
            [str(binary), "--version"], text=True
        ).strip(),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "cpu_affinity": sorted(os.sched_getaffinity(0)),
        "queries_per_sample": queries,
        "samples": {
            count: [asdict(value) for value in values]
            for count, values in observations.items()
        },
        "medians": {
            count: {
                "wall_ms": statistics.median(value.wall_ms for value in values),
                "process_cpu_ms": statistics.median(
                    value.process_cpu_ms for value in values
                ),
            }
            for count, values in observations.items()
        },
    }
    (workspace / "result.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
