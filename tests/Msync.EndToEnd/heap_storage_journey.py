#!/usr/bin/env python3
"""Verify dense Heap storage and its native writer through real CLI routes."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import subprocess
import tempfile
from pathlib import Path


ROOT: Path = Path(__file__).resolve().parents[2]
COUNT: int = 2048
LIVE_COUNT: int = 1024
SWITCH_AT: int = 384
STRIDE: int = 40503
MODULUS: int = 65536
SOURCE: str = """
let Heap = $std::containers::Heap;
let queue = Heap::new(Number);
let key = 0;
for _ in $std::ranges::iota(0, COUNT) {
    key = (key + STRIDE) % MODULUS;
    Heap::push(queue, key);
};
let saved = Heap::snapshot(queue);
let alias = queue;
for _ in $std::ranges::iota(0, COUNT) {
    let value? = Heap::pop(alias);
    if value is not null { $std::io::println(value as String); };
};
$std::io::println(Heap::size(queue) as String);
$std::io::println($std::ranges::size(saved) as String);
for value in $std::ranges::sort(saved) { $std::io::println(value as String); };
""".replace("COUNT", str(COUNT)).replace("STRIDE", str(STRIDE)).replace(
    "MODULUS", str(MODULUS)
)
LIVE_SOURCE: str = """
let Heap = $std::containers::Heap;
let io = $std::io;
let order = scope {
    Self: Number,
    sample?: null,
    less: $std::containers::NaturalOrder::less,
};
let queue = Heap::with_policy(Number, order);
let alias = queue;
let positions = $std::ranges::iota(0, COUNT);
let key = 0;
for _ in positions {
    key = (key + STRIDE) % MODULUS;
    Heap::push(queue, key);
};
let saved = Heap::snapshot(queue);
let switch_text? = io::read_line();
if switch_text is not null {
    let switch_at = switch_text as Number;
    let comparisons = 0;
    for index in positions {
        if index == switch_at {
            order::less = fn(left?, right?) -> Bool {
                comparisons = comparisons + 1;
                if left is null { right is not null } else {
                    if right is null { false } else {
                        left < right or (left == left and right != right)
                    }
                }
            };
        };
        let value? = Heap::pop(alias);
        if value is not null { io::println(value as String); };
    };
    io::println("snapshot");
    for value in saved { io::println(value as String); };
    io::println("summary");
    io::println(Heap::size(queue) as String);
    io::println(comparisons as String);
};
""".replace("COUNT", str(LIVE_COUNT)).replace("STRIDE", str(STRIDE)).replace(
    "MODULUS", str(MODULUS)
)


def run(
    cli: Path,
    cwd: Path,
    args: list[str],
    profile: Path | None = None,
    jit: str = "auto",
    stdin: bytes = b"",
) -> bytes:
    options: list[str] = (
        ["--telemetry-db", str(profile), "--telemetry-level", "profile"]
        if profile is not None
        else ["--no-telemetry"]
    )
    process: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(cli), "--no-config", *options, *args],
        cwd=cwd,
        input=stdin,
        capture_output=True,
        env=dict(os.environ, MSYNC_JIT=jit),
        timeout=60,
    )
    assert process.returncode == 0 and not process.stderr, (
        args, process.returncode, process.stderr.decode(errors="replace")
    )
    return process.stdout


def expected_output() -> list[str]:
    keys: list[str] = [str(value) for value in sorted(
        index * STRIDE % MODULUS for index in range(1, COUNT + 1)
    )]
    return [*keys, "0", str(COUNT), *keys]


def compare(output: bytes) -> None:
    assert output.decode().splitlines() == expected_output()


def expected_live_output() -> list[str]:
    keys: list[int] = [index * STRIDE % MODULUS for index in range(1, LIVE_COUNT + 1)]
    rows: list[int] = []
    for key in keys:
        index: int = len(rows)
        rows.append(key)
        while index and key < rows[(index - 1) // 2]:
            parent: int = (index - 1) // 2
            rows[index] = rows[parent]
            index = parent
        rows[index] = key
    before: list[int] = rows.copy()
    comparisons: int = 0
    for index in range(LIVE_COUNT):
        tail: int = rows.pop()
        if not rows:
            break
        hole: int = 0
        while 2 * hole + 1 < len(rows):
            child: int = 2 * hole + 1
            if child + 1 < len(rows):
                comparisons += int(index >= SWITCH_AT)
                if rows[child + 1] < rows[child]:
                    child += 1
            comparisons += int(index >= SWITCH_AT)
            if rows[child] >= tail:
                break
            rows[hole] = rows[child]
            hole = child
        rows[hole] = tail
    return [
        *map(str, sorted(keys)), "snapshot", *map(str, before),
        "summary", "0", str(comparisons),
    ]


def worker_metrics(profile: Path) -> dict[str, int]:
    with sqlite3.connect(profile) as db:
        rows: list[tuple[str, int]] = db.execute(
            "select m.key, sum(m.value_int) from records r join metrics m"
            " on m.record_id=r.id where r.name='vm.function' and"
            " m.key in ('native_worker_entries','native_worker_stores','native_worker_guard_misses')"
            " group by m.key"
        ).fetchall()
    return dict(rows)


def journey(cli: Path, work: Path, require_native: bool) -> dict[str, object]:
    source: Path = work / "heap.msync"
    source.write_text(SOURCE, encoding="utf-8")
    compare(run(cli, work, ["run", str(source), "-O2"]))
    portable: Path = work / "heap.mso"
    executable: Path = work / "heap.mexe"
    run(cli, work, ["compile", str(source), "-O2", "-o", str(portable)])
    source.unlink()
    portable_text: bytes = run(cli, work, ["disassemble", str(portable), "--json"])
    assert b"storage.vector_set" in portable_text
    assert b"storage.buffer_" not in portable_text
    compare(run(cli, work, ["run-ir", str(portable)]))
    run(cli, work, ["bind-ir", str(portable), "-o", str(executable)])
    portable.unlink()
    detached: Path = work / "detached"
    detached.mkdir()
    profile: Path = work / "native.sqlite"
    compare(run(cli, detached, ["run-ir", str(executable)], profile=profile))
    metrics: dict[str, int] = worker_metrics(profile)
    if require_native:
        assert metrics.get("native_worker_entries", 0) > 0, metrics
        assert metrics.get("native_worker_stores", 0) > 0, metrics
    compare(run(cli, detached, ["run-ir", str(executable)], jit="off"))
    source.write_text(LIVE_SOURCE, encoding="utf-8")
    run(cli, work, ["compile", str(source), "-O2", "-o", str(portable)])
    source.unlink()
    run(cli, work, ["bind-ir", str(portable), "-o", str(executable)])
    portable.unlink()
    live_profile: Path = work / "live.sqlite"
    stdin: bytes = f"{SWITCH_AT}\n".encode()
    output: bytes = run(cli, detached, ["run-ir", str(executable)], live_profile, stdin=stdin)
    expected_live: list[str] = expected_live_output()
    assert output.decode().splitlines() == expected_live
    assert run(cli, detached, ["run-ir", str(executable)], jit="off", stdin=stdin) == output
    live_metrics: dict[str, int] = worker_metrics(live_profile)
    if require_native:
        assert live_metrics.get("native_worker_stores", 0) > 0, live_metrics
    return {
        "elements": COUNT,
        "routes": 4,
        "worker": metrics,
        "live_comparator": {
            "elements": LIVE_COUNT,
            "routes": 2,
            "comparisons": int(expected_live[-1]),
            "worker": live_metrics,
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--require-native", action="store_true")
    args: argparse.Namespace = parser.parse_args()
    scratch: Path = ROOT / ".temp/heap"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="dense-owner-", dir=scratch) as directory:
        report: dict[str, object] = journey(args.binary.resolve(), Path(directory), args.require_native)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
