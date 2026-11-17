"""Exercise the same guest heap in REPL, interpreter, and JIT commands."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]

SETUP: tuple[str, ...] = (
    (
        "fn pair(seed: Number) { let first = scope { value: seed, next?: null }; "
        "let second = scope { value: seed + 1, next?: null }; "
        "first::next = second; second::next = first; first };"
    ),
    "let retained = pair(7);",
    (
        "fn batch() { let i = 0; let total = 0; loop { if i >= 4000 { break total; }; "
        "let node = pair(i); total = total + node::value; i = i + 1; } };"
    ),
)
BATCH: str = "batch() + retained::next::value"


def journey(binary: Path, work: Path) -> dict[str, object]:
    database: Path = work / "heap.sqlite"
    commands: list[str] = [
        *SETUP,
        *([BATCH] * 5),
        "retained::next::next::value",
        "/quit",
        "",
    ]
    execution: subprocess.CompletedProcess[str] = subprocess.run(
        [
            str(binary),
            "--no-config",
            "--color",
            "never",
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            "diagnostics",
            "repl",
        ],
        input="\n".join(commands),
        check=False,
        cwd=work,
        text=True,
        capture_output=True,
        timeout=120,
    )
    expected: list[str] = [str(4000 * 3999 // 2 + 8)] * 5 + ["7"]
    assert execution.returncode == 0 and not execution.stderr, execution.stderr
    assert execution.stdout.splitlines() == expected, execution.stdout
    events: dict[int, dict[str, int]] = {}
    with sqlite3.connect(database) as db:
        assert db.execute("SELECT status,exporter_error FROM sessions").fetchall() == [
            ("complete", None)
        ]
        for identity, metric, value in db.execute("""
            SELECT r.id,m.key,m.value_int FROM records r JOIN metrics m ON r.id=m.record_id
            WHERE r.name='heap.collect' ORDER BY r.id,m.key
        """):
            events.setdefault(identity, {})[metric] = value
        totals: dict[str, int] = dict(
            db.execute("""
            SELECT m.key,sum(m.value_int) FROM records r JOIN metrics m ON r.id=m.record_id
            WHERE r.name='vm.function' AND m.key LIKE 'native_heap%' GROUP BY m.key
        """)
        )
    sweeps: list[dict[str, int]] = list(events.values())
    assert len(sweeps) == 5 and all(
        row["heap.objects.reclaimed"] >= 8000 for row in sweeps
    ), sweeps
    assert len({row["heap.bytes.live"] for row in sweeps}) == 1, sweeps
    assert totals["native_heap_reused_objects"] >= 4 * 8000, totals
    if sys.maxsize > 2**32:
        assert totals["native_heap_edge_bytes"] > 0, totals
        assert totals["native_heap_descriptors_reused"] >= 4 * 8000, totals
        assert all(row["heap.raw_edges"] >= 2 for row in sweeps), sweeps
        assert all(row["heap.raw_objects_traced"] >= 2 for row in sweeps), sweeps
    return {"output": expected, "collections": sweeps, "allocations": totals}


def command_journey(binary: Path, work: Path) -> dict[str, object]:
    source: Path = work / "heap-command.msync"
    source.write_text(
        SETUP[0]
        + """
        fn batch(root: Scope, limit: Number) {
            let i = 0;
            let total = 0;
            loop {
                if i >= limit { break; };
                let node = pair(i);
                total = total + node::next::value;
                i = i + 1;
            };
            total + root::value
        };
        let retained = pair(7);
        fn captured(root: Scope) { fn { root::next::next::value } };
        let read = captured(pair(17));
        let snapshots = [pair(11), pair(13)];
        $std::io::println(batch(retained, 40000) as String);
        $std::io::println(retained::next::next::value as String);
        $std::io::println(read() as String);
        let range_total = 0;
        for item in snapshots { range_total = range_total + item::next::next::value; };
        $std::io::println(range_total as String);
        """,
        encoding="utf-8",
    )
    base: list[str] = [str(binary), "--no-config", "--color", "never"]
    artifact: Path = work / "heap-command.mexe"
    compiled: subprocess.CompletedProcess[str] = subprocess.run(
        [
            *base,
            "--no-telemetry",
            "compile",
            "-O2",
            source.name,
            "-o",
            "heap-command.mso",
        ],
        cwd=work,
        check=False,
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert compiled.returncode == 0, compiled.stderr
    subprocess.run(
        [*base, "--no-telemetry", "bind-ir", "heap-command.mso", "-o", artifact.name],
        cwd=work,
        check=True,
        capture_output=True,
        timeout=30,
    )
    receipts: dict[str, object] = {}
    expected: str = f"{40000 * 40001 // 2 + 7}\n7\n17\n24\n"
    for policy in ("off", "force"):
        database: Path = work / f"heap-command-{policy}.sqlite"
        result: subprocess.CompletedProcess[str] = subprocess.run(
            [
                *base,
                "--telemetry-db",
                str(database),
                "--telemetry-level",
                "diagnostics",
                "run",
                artifact.name,
            ],
            cwd=work,
            env=dict(os.environ, MSYNC_JIT=policy),
            check=True,
            capture_output=True,
            text=True,
            timeout=120,
        )
        assert result.stdout == expected and not result.stderr, (
            result.stdout,
            result.stderr,
        )
        with sqlite3.connect(database) as db:
            assert db.execute(
                "SELECT status,exporter_error FROM sessions"
            ).fetchall() == [("complete", None)]
            assert db.execute("""
                SELECT a.value FROM records r JOIN attributes a ON a.record_id=r.id
                WHERE r.name='execute' AND a.key='vm.driver'
                """).fetchall() == [("inline",)]
            counts: dict[str, tuple[int, int, int]] = {
                key: (events, minimum, total)
                for key, events, minimum, total in db.execute("""
                    SELECT m.key,count(*),min(m.value_int),sum(m.value_int)
                    FROM records r JOIN metrics m ON r.id=m.record_id
                    JOIN attributes a ON a.record_id=r.id AND a.key='heap.boundary' AND a.value='execution'
                    WHERE r.name='heap.collect' GROUP BY m.key
                """)
            }
            assert counts["heap.frames"][0] > 1 and counts["heap.frames"][1] >= 2, (
                counts
            )
            assert counts["heap.objects.reclaimed"][2] >= 75000, counts
            if sys.maxsize > 2**32:
                assert counts["heap.raw_edges"][1] >= 2, counts
                assert counts["heap.raw_environment_edges"][1] >= 1, counts
                assert counts["heap.raw_objects_traced"][1] >= 2, counts
            receipts[policy] = counts
    return receipts


def run(binary: Path, work: Path) -> dict[str, object]:
    return {"repl": journey(binary, work), "command": command_journey(binary, work)}


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    if args.output is None:
        with TemporaryDirectory(prefix="native-guest-heap-", dir=ROOT / ".temp") as tmp:
            run(args.binary.resolve(), Path(tmp))
    else:
        output: Path = args.output.resolve()
        result: dict[str, object] = run(args.binary.resolve(), output.parent)
        output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print("PASS: REPL and both command tiers reclaim cycles and retain live aliases")


if __name__ == "__main__":
    main()
