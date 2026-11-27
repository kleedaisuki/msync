"""Check real native-tier counters and unchanged results through the CLI exporter."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import subprocess
import uuid
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """let row = object { total: 0, text: "原生𐀀", file: ./origin, endpoint: $local };
for n in $std::ranges::iota(1, 257) { row::total = row::total + n; };
$std::io::println(row::total as String);
"""
KEYS: frozenset[str] = frozenset(
    {
        "tier.compilations",
        "tier.compilation_ns",
        "tier.code_bytes",
        "tier.entries",
        "tier.linked_entries",
        "tier.linked_operations",
        "tier.operations",
        "tier.direct_operations",
        "tier.raw_operations",
        "tier.guard_misses",
        "tier.deoptimizations",
        "tier.suspensions",
        "tier.recompilations",
        "tier.cancellation_polls",
        "tier.unavailable_compilations",
    }
)


def run(binary: Path, directory: Path, policy: str) -> dict[str, int]:
    directory.mkdir(parents=True)
    source: Path = directory / "tier.msync"
    source.write_text(SOURCE, encoding="utf-8")
    database: Path = directory / "tier.sqlite"
    environment: dict[str, str] = dict(os.environ)
    environment["MSYNC_JIT"] = policy
    environment["XDG_CONFIG_HOME"] = str(directory / "config")
    environment["HOME"] = str(directory / "home")
    arguments: list[str] = [
        str(binary),
        "--no-config",
        "--color",
        "never",
        "--telemetry-level",
        "phases",
        "--telemetry-db",
        str(database),
        source.name,
    ]
    process: subprocess.CompletedProcess[bytes] = subprocess.run(
        arguments,
        cwd=directory,
        env=environment,
        input=b"",
        capture_output=True,
        timeout=60,
        check=False,
    )
    (directory / "stdout.bin").write_bytes(process.stdout)
    (directory / "stderr.bin").write_bytes(process.stderr)
    assert process.returncode == 0, (process.returncode, process.stderr)
    assert process.stdout == b"32896\n", process.stdout
    assert process.stderr == b"", process.stderr
    with sqlite3.connect(database) as connection:
        counters: dict[str, int] = dict(
            connection.execute(
                """SELECT m.key,m.value_int FROM spans AS s
                   JOIN metrics AS m ON m.record_id=s.id
                   WHERE s.name='execute' AND m.key GLOB 'tier.*'"""
            )
        )
    assert counters.keys() == KEYS, counters
    assert all(value >= 0 for value in counters.values()), counters
    assert counters["tier.direct_operations"] <= counters["tier.operations"], counters
    assert counters["tier.raw_operations"] <= counters["tier.direct_operations"], (
        counters
    )
    if policy == "off":
        assert all(value == 0 for value in counters.values()), counters
    elif policy == "linked":
        assert counters["tier.compilations"] == 0, counters
        assert counters["tier.entries"] == 0, counters
        assert counters["tier.linked_entries"] > 0, counters
        assert counters["tier.linked_operations"] > 0, counters
    else:
        assert counters["tier.compilations"] > 0, counters
        assert counters["tier.code_bytes"] > 0, counters
        assert counters["tier.compilation_ns"] > 0, counters
        assert counters["tier.entries"] > 0, counters
        assert counters["tier.operations"] > 0, counters
        assert counters["tier.cancellation_polls"] > 0, counters
    return counters


def repl_deltas(binary: Path, directory: Path) -> list[dict[str, int]]:
    directory.mkdir(parents=True)
    database: Path = directory / "repl.sqlite"
    environment: dict[str, str] = dict(os.environ)
    environment["MSYNC_JIT"] = "force"
    program: str = (
        "let x=0; loop { if x>=256 { break; }; x=x+1; }; x\n"
        "let y=0; loop { if y>=256 { break; }; y=y+1; }; y\n/quit\n"
    )
    process: subprocess.CompletedProcess[bytes] = subprocess.run(
        [
            str(binary),
            "--no-config",
            "--color",
            "never",
            "--telemetry-level",
            "phases",
            "--telemetry-db",
            str(database),
            "repl",
        ],
        cwd=directory,
        env=environment,
        input=program.encode(),
        capture_output=True,
        timeout=60,
        check=False,
    )
    (directory / "stdout.bin").write_bytes(process.stdout)
    (directory / "stderr.bin").write_bytes(process.stderr)
    assert process.returncode == 0, process.stderr
    assert process.stdout.splitlines() == [b"256", b"256"], process.stdout
    with sqlite3.connect(database) as connection:
        records: list[int] = [
            row[0]
            for row in connection.execute(
                """SELECT e.id FROM spans AS e
                   JOIN spans AS p ON p.trace_id=e.trace_id AND p.span_id=e.parent_span_id
                   JOIN attributes AS a ON a.record_id=p.id
                   WHERE e.name='execute' AND p.name='repl.submission'
                     AND a.key='submission.kind' AND a.value='input' ORDER BY e.id"""
            )
        ]
        values: list[dict[str, int]] = [
            dict(
                connection.execute(
                    "SELECT key,value_int FROM metrics WHERE record_id=? AND key GLOB 'tier.*'",
                    (record,),
                )
            )
            for record in records
        ]
    assert len(values) == 2, values
    assert all(item.keys() == KEYS for item in values), values
    assert [item["tier.compilations"] for item in values] == [1, 1], values
    # Region partitioning can change entry counts, but identical submissions
    # must report equal deltas rather than cumulative session prefixes.
    assert values[0]["tier.entries"] > 0, values
    for key in (
        "tier.entries",
        "tier.operations",
        "tier.direct_operations",
        "tier.raw_operations",
        "tier.cancellation_polls",
    ):
        assert values[0][key] == values[1][key], (key, values)
    return values


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve(strict=True)
    directory: Path = ROOT / ".temp/native-tier-telemetry" / uuid.uuid4().hex
    directory.mkdir(parents=True)
    report: dict[str, Any] = {"status": "running", "binary": str(binary)}
    path: Path = directory / "result.json"
    try:
        for policy in ("off", "linked", "force"):
            report[policy] = run(binary, directory / policy, policy)
            path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        report["repl_deltas"] = repl_deltas(binary, directory / "repl")
        report["status"] = "complete"
    except Exception as error:
        report.update(status="failed", error=repr(error))
        raise
    finally:
        path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(path)
    print("PASS CLI native-tier counters: real native entry and JIT-off control")


if __name__ == "__main__":
    main()
