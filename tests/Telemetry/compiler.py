#!/usr/bin/env python3
"""Compiler telemetry through real CLI compilation, replay, failures and exports."""

from __future__ import annotations

import argparse
import json
import sqlite3
import subprocess
from collections.abc import Callable
from pathlib import Path
from typing import Any
from uuid import uuid4

from journeys import invoke, records, require

ROOT: Path = Path(__file__).resolve().parents[2]
Row = dict[str, Any]

SOURCE: str = """fn update(flag: Bool, seed: Number) {
    let row = if flag { scope { n: seed, text: "left" } }
        else { scope { text: "right", n: seed + 1 } };
    let alias = row;
    alias::n = row::n + 2;
    row::n
};
let input? = $std::io::read_line();
let flag = if input? { false } else { input == "yes" };
let result = update(flag, 3) + update(flag, 4);
$std::io::println(result as String);
"""


def ownership(rows: list[Row]) -> None:
    spans: dict[str, Row] = {
        row["span_id"]: row for row in rows if row["kind"] == "span"
    }
    assert len(spans) == sum(row["kind"] == "span" for row in rows)
    for row in rows:
        if row["kind"] != "span":
            assert row["span_id"] in spans, row
            continue
        parent_id: str | None = row["parent_span_id"]
        if parent_id is None:
            continue
        parent: Row = spans[parent_id]
        assert parent_id != row["span_id"], row
        assert parent["start_unix_ns"] <= row["start_unix_ns"], (parent, row)
        assert (
            row["start_unix_ns"] + row["duration_ns"]
            <= parent["start_unix_ns"] + parent["duration_ns"]
        ), (parent, row)
        visited: set[str] = {row["span_id"]}
        while parent_id is not None:
            assert parent_id not in visited, (row, visited)
            visited.add(parent_id)
            parent_id = spans[parent_id]["parent_span_id"]


def compilation(binary: Path, fixture: Path) -> None:
    source: Path = fixture / "细腻.msync"
    source.write_text(SOURCE, encoding="utf-8")
    for level in range(4):
        unobserved: Path = fixture / f"plain-{level}.mso"
        observed: Path = fixture / f"observed-{level}.mso"
        trace: Path = fixture / f"compile-{level}.jsonl"
        require(
            invoke(
                binary,
                fixture,
                [
                    "--no-config",
                    "compile",
                    source.name,
                    f"-O{level}",
                    "-o",
                    str(unobserved),
                    "--no-telemetry",
                ],
            )
        )
        require(
            invoke(
                binary,
                fixture,
                [
                    "--no-config",
                    "compile",
                    source.name,
                    f"-O{level}",
                    "-o",
                    str(observed),
                    "--trace",
                    str(trace),
                    "--telemetry-level",
                    "diagnostics",
                ],
            )
        )
        # Standard-module layouts inherit process-randomized immutable-map order.
        # Same-process byte equivalence is checked by tests/CompilerTelemetry.
        baseline: subprocess.CompletedProcess[str] = invoke(
            binary,
            fixture,
            ["--no-config", "run-ir", str(unobserved), "--no-telemetry"],
            stdin="yes\n",
        )
        require(baseline)
        assert baseline.stdout.strip() == "11", baseline
        rows: list[Row] = records(trace)
        ownership(rows)
        names: set[str] = {row["name"] for row in rows}
        assert {
            "compiler.lex",
            "compiler.syntax",
            "compiler.bind",
            "compiler.bind.standard_demand",
            "compiler.bind.standard_module",
            "compiler.standard.document",
            "compiler.semantic.body",
            "compiler.semantic.instantiate",
            "compiler.flow.graph",
            "compiler.flow.solve",
            "compiler.hir.freeze",
            "compiler.hir.seal",
            "compiler.lower.layouts",
            "compiler.lower.function",
            "compiler.artifact.encode",
            "compiler.artifact.encode_section",
        } <= names, names
        instances: list[Row] = [
            row for row in rows if row["name"] == "compiler.semantic.instantiate"
        ]
        assert any(row["metrics"].get("cache.miss") == 1 for row in instances), (
            instances
        )
        assert any(row["metrics"].get("cache.hit") == 1 for row in instances), instances
        assert any(
            row["metrics"].get("transfers", 0) > 0
            for row in rows
            if row["name"] == "compiler.flow.solve"
        )
        assert any(
            row["attributes"].get("source.file", "").endswith(source.name)
            for row in instances
        )
        if level:
            passes: list[Row] = [
                row for row in rows if row["name"].startswith("optimizer.pass.")
            ]
            assert passes and all("function.id" in row["attributes"] for row in passes)
            assert any(
                int(row["attributes"]["optimizer.iteration"]) > 1 for row in passes
            ), passes
            assert any(
                row["metrics"].get("changed_invocations", 0) > 0 for row in passes
            ), passes
        else:
            assert not any(name.startswith("optimizer.pass.") for name in names)
        replay: Path = fixture / f"replay-{level}.jsonl"
        process: subprocess.CompletedProcess[str] = invoke(
            binary,
            fixture,
            [
                "--no-config",
                "run-ir",
                str(observed),
                "--trace",
                str(replay),
                "--telemetry-level",
                "diagnostics",
            ],
            stdin="yes\n",
        )
        require(process)
        assert process.stdout.strip() == "11", process
        replay_rows: list[Row] = records(replay)
        ownership(replay_rows)
        replay_names: set[str] = {row["name"] for row in replay_rows}
        assert {
            "compiler.artifact.decode",
            "compiler.artifact.decode_section",
            "compiler.verify",
            "compiler.verify.function",
            "compiler.verify.pointers",
            "compiler.verify.flow",
        } <= replay_names
        assert "compiler.parse" not in replay_names
    print(
        "PASS source O0–O3: unchanged replay, detailed ownership, cache/flow/pass counts and real replay"
    )


def failures(binary: Path, fixture: Path) -> None:
    cases: list[tuple[str, str, str]] = [
        ("lex", 'let value = "unfinished', "compiler.lex"),
        ("syntax", "let = 1;", "compiler.syntax"),
        ("bind", "let value = scope { n: 1, n: 2 };", "compiler.bind"),
        ("semantic", 'let value: Number = "not a number";', "compiler.semantic.body"),
    ]
    for name, text, phase in cases:
        source: Path = fixture / f"bad-{name}.msync"
        source.write_text(text, encoding="utf-8")
        trace: Path = fixture / f"bad-{name}.jsonl"
        process: subprocess.CompletedProcess[str] = invoke(
            binary,
            fixture,
            [
                "--no-config",
                "check",
                str(source),
                "--trace",
                str(trace),
                "--telemetry-level",
                "diagnostics",
            ],
        )
        assert process.returncode != 0 and process.stderr, process
        rows: list[Row] = records(trace)
        ownership(rows)
        failed: list[Row] = [
            row for row in rows if row["name"] == phase and row["status"] == "error"
        ]
        assert failed and all(row["error_chain"] for row in failed), (name, rows)
        assert all(row["attributes"].get("diagnostic.code") for row in failed), failed
        assert all(
            row["attributes"]["source.file"].endswith(source.name) for row in failed
        ), failed
        assert all(int(row["attributes"]["source.offset"]) >= 0 for row in failed), (
            failed
        )
        assert not any(row["name"] == "compiler.lower" for row in rows), rows
    print(
        "PASS lexical/syntax/binding/semantic failures: exact failed stage, causes and no fabricated later stages"
    )


def hir_and_sqlite(binary: Path, fixture: Path) -> None:
    source: Path = fixture / "细腻.msync"
    hir: Path = fixture / "typed.mhir"
    require(
        invoke(
            binary,
            fixture,
            [
                "--no-config",
                "compile",
                str(source),
                "--emit",
                "hir",
                "-o",
                str(hir),
                "--trace",
                str(fixture / "hir-write.jsonl"),
                "--telemetry-level",
                "profile",
            ],
        )
    )
    assert any(
        row["name"] == "compiler.hir.encode"
        for row in records(fixture / "hir-write.jsonl")
    )
    db: Path = fixture / "compiler.sqlite"
    process: subprocess.CompletedProcess[str] = invoke(
        binary,
        fixture,
        [
            "--no-config",
            "run-ir",
            str(hir),
            "--telemetry-db",
            str(db),
            "--telemetry-level",
            "diagnostics",
        ],
        stdin="no\n",
    )
    require(process)
    assert process.stdout.strip() == "13", process
    with sqlite3.connect(db) as connection:
        names: set[str] = {
            row[0] for row in connection.execute("SELECT name FROM records")
        }
        assert {
            "compiler.hir.decode",
            "compiler.hir.verify",
            "compiler.lower.function",
            "optimizer.function",
        } <= names, names
        assert "compiler.parse" not in names and "compiler.bind" not in names
        orphans: int = connection.execute("""SELECT count(*) FROM records child
            LEFT JOIN spans parent ON parent.trace_id=child.trace_id
              AND parent.span_id=CASE WHEN child.kind='event' THEN child.span_id ELSE child.parent_span_id END
            WHERE (child.kind='event' OR child.parent_span_id IS NOT NULL) AND parent.id IS NULL""").fetchone()[
            0
        ]
        assert orphans == 0, orphans
        assert connection.execute("SELECT status FROM sessions").fetchone() == (
            "complete",
        )
    print(
        "PASS HIR input/output and SQLite: detailed compiler rows, ownership and unchanged program output"
    )


def levels(binary: Path, fixture: Path) -> None:
    trace: Path = fixture / "phases.jsonl"
    require(
        invoke(
            binary,
            fixture,
            [
                "--no-config",
                "check",
                str(fixture / "细腻.msync"),
                "--trace",
                str(trace),
                "--telemetry-level",
                "phases",
            ],
        )
    )
    rows: list[Row] = records(trace)
    names: set[str] = {row["name"] for row in rows}
    assert {
        "compiler.parse",
        "compiler.bind",
        "compiler.analyze",
        "compiler.lower",
        "compiler.optimize",
    } <= names
    assert "compiler.lex" not in names and "compiler.semantic.instantiate" not in names
    ownership(rows)
    print("PASS phases level retains coarse stages without detailed compiler recording")


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser()
    parser.add_argument("binary", type=Path)
    parser.add_argument("--case", choices=["source", "failures", "hir", "levels"])
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    fixture: Path = ROOT / ".temp" / "compiler-telemetry" / uuid4().hex
    fixture.mkdir(parents=True)
    (fixture / "细腻.msync").write_text(SOURCE, encoding="utf-8")
    cases: dict[str, Callable[[Path, Path], None]] = {
        "source": compilation,
        "failures": failures,
        "hir": hir_and_sqlite,
        "levels": levels,
    }
    for name, journey in cases.items():
        if args.case is None or args.case == name:
            journey(binary, fixture)
    print(json.dumps({"fixture": str(fixture)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
