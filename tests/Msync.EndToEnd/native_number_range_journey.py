#!/usr/bin/env python3
"""Exercise numeric Range storage and iteration in a real source-free workflow."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sqlite3
import subprocess
from collections.abc import Sequence
from contextlib import closing
from pathlib import Path
from statistics import median
from tempfile import TemporaryDirectory
from time import perf_counter_ns

ROOT: Path = Path(__file__).resolve().parents[2]
PROGRAM: str = """
let io = $std::io;
let ranges = $std::ranges;
let input? = io::read_line();
if input is not null {
    let count = input as Number;
    let source = ranges::iota(0, count);
    let snapshot = ranges::materialize(source);
    let second = ranges::materialize(snapshot);
    let sum = 0;
    for value in source { sum = sum + value; };
    for value in second { sum = sum + value; };
    io::println(sum as String);
    io::println(ranges::size(source) as String);
    io::println(ranges::size(snapshot) as String);
    io::println(ranges::at(source, count - 1) as String);
    io::println(ranges::at(snapshot, count - 1) as String);

    let descending = ranges::materialize(ranges::sequence(2, -1, -0.5));
    let fraction = 0;
    for item in descending {
        if item == 0 { break; };
        fraction = fraction + item;
    };
    io::println(fraction as String);
    let zero = ranges::materialize(ranges::sequence(-0, 1, 0.25));
    io::println($std::math::signbit(ranges::at(zero, 0)) as String);
    for first in ranges::sequence(9007199254740992, 9007199254740994, 1) {
        io::println("first");
        break;
    };
};
"""

STALL: str = """
for item in $std::ranges::sequence(9007199254740992, 9007199254740994, 1) {
    $std::io::println("first");
};
"""


def invoke(
    binary: Path,
    work: Path,
    args: Sequence[str],
    *,
    data: bytes = b"",
) -> subprocess.CompletedProcess[bytes]:
    env: dict[str, str] = dict(os.environ)
    env["MSYNC_JIT"] = "off"
    return subprocess.run(
        [binary, "--no-config", *args],
        cwd=work,
        env=env,
        input=data,
        capture_output=True,
        timeout=90,
        check=False,
    )


def accept(result: subprocess.CompletedProcess[bytes], output: bytes = b"") -> None:
    assert result.returncode == 0, (result.args, result.stdout, result.stderr)
    assert result.stdout.splitlines() == output.splitlines(), (
        result.args,
        result.stdout,
        output,
    )
    assert not result.stderr, (result.args, result.stderr)


def receipts(path: Path) -> dict[str, int]:
    with closing(sqlite3.connect(path)) as connection:
        return dict(
            connection.execute(
                """SELECT m.key, SUM(m.value_int)
                   FROM records AS r JOIN metrics AS m ON m.record_id = r.id
                   WHERE (r.name = 'execute' AND m.key IN ('duration_ns', 'managed.allocated_bytes'))
                      OR (r.name = 'vm.function' AND m.key = 'number_boxes')
                   GROUP BY m.key"""
            )
        )


def exercise(
    builder: Path,
    hosts: dict[str, Path],
    root: Path,
    count: int,
    rounds: int,
) -> dict[str, object]:
    source: Path = root / "numeric.msync"
    source.write_text(PROGRAM, encoding="utf-8", newline="\n")
    build: Path = root / "build"
    build.mkdir()
    unit: Path = build / "numeric.msync"
    unit.write_bytes(source.read_bytes())
    accept(
        invoke(
            builder,
            build,
            ["--no-telemetry", "compile", unit.name, "-o", "numeric.mso"],
        )
    )
    unit.unlink()
    accept(
        invoke(
            builder,
            build,
            ["--no-telemetry", "bind-ir", "numeric.mso", "-o", "numeric.mexe"],
        )
    )
    assert not list(build.glob("*.msync"))

    data: bytes = f"{count}\n".encode()
    output: bytes = f"{count * (count - 1)}\n{count}\n{count}\n{count - 1}\n{count - 1}\n5\ntrue\nfirst\n".encode()
    all_metrics: dict[str, dict[str, dict[str, int]]] = {}
    for name, binary in hosts.items():
        routes: dict[str, dict[str, int]] = {}
        for route, working, filename in (
            ("source", root, source.name),
            ("mso", build, "numeric.mso"),
            ("mexe", build, "numeric.mexe"),
        ):
            database: Path = root / f"{name}-{route}.sqlite"
            start: int = perf_counter_ns()
            accept(
                invoke(
                    binary,
                    working,
                    [
                        "--telemetry-db",
                        str(database),
                        "--telemetry-level",
                        "profile",
                        "run",
                        filename,
                    ],
                    data=data,
                ),
                output,
            )
            routes[route] = {
                "profiled_process_ns": perf_counter_ns() - start,
                **receipts(database),
            }
        all_metrics[name] = routes

    wall: dict[str, list[int]] = {name: [] for name in hosts}
    labels: tuple[str, ...] = tuple(hosts)
    for index in range(rounds):
        for name in labels if index % 2 == 0 else labels[::-1]:
            start = perf_counter_ns()
            accept(
                invoke(
                    hosts[name],
                    build,
                    ["--no-telemetry", "run", "numeric.mexe"],
                    data=data,
                ),
                output,
            )
            wall[name].append(perf_counter_ns() - start)

    source.write_text(STALL, encoding="utf-8", newline="\n")
    result: subprocess.CompletedProcess[bytes] = invoke(
        hosts["candidate"], root, ["--no-telemetry", "run", source.name]
    )
    assert result.returncode != 0 and result.stdout.splitlines() == [b"first"], result
    assert (
        b"ranges.sequence step no longer advances the Number value" in result.stderr
    ), result
    return {
        "routes": all_metrics,
        "bound_wall_ns": wall,
        "bound_wall_median_ns": {
            name: median(samples) for name, samples in wall.items() if samples
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--count", type=int, default=250_000)
    parser.add_argument("--rounds", type=int, default=0)
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    if not 1 <= args.count <= 10_000_000:
        parser.error("--count must be in 1..10000000 for the exact binary64 sum oracle")
    if args.rounds < 0:
        parser.error("--rounds must be nonnegative")
    candidate: Path = args.binary.resolve(strict=True)
    hosts: dict[str, Path] = {"candidate": candidate}
    if args.baseline is not None:
        hosts = {"baseline": args.baseline.resolve(strict=True), **hosts}
    (ROOT / ".temp").mkdir(exist_ok=True)
    with TemporaryDirectory(prefix="numeric-range-", dir=ROOT / ".temp") as temporary:
        metrics: dict[str, object] = exercise(
            next(iter(hosts.values())), hosts, Path(temporary), args.count, args.rounds
        )
    report: str = json.dumps(
        {
            "count": args.count,
            "binary_sha256": {
                name: hashlib.sha256(binary.read_bytes()).hexdigest()
                for name, binary in hosts.items()
            },
            **metrics,
        },
        indent=2,
    )
    if args.output is not None:
        output: Path = args.output
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(report + "\n", encoding="utf-8")
    print(report)


if __name__ == "__main__":
    main()
