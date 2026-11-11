#!/usr/bin/env python3
"""Real-process JSONL/SQLite observer compatibility journeys."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Result:
    name: str
    records: int = 0


def invoke(
    binary: Path, cwd: Path, arguments: list[str], *, stdin: str | None = None
) -> subprocess.CompletedProcess[str]:
    environment: dict[str, str] = dict(os.environ)
    (cwd / "home").mkdir(exist_ok=True)
    environment["XDG_CONFIG_HOME"] = str(cwd / "config-home")
    environment["HOME"] = str(cwd / "home")
    process: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), *arguments],
        cwd=cwd,
        env=environment,
        text=True,
        encoding="utf-8",
        input=stdin,
        capture_output=True,
        timeout=30,
        check=False,
    )
    return process


def require(process: subprocess.CompletedProcess[str], code: int = 0) -> None:
    assert process.returncode == code, (
        process.args,
        process.returncode,
        process.stdout,
        process.stderr,
    )


def records(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def jsonl(binary: Path, fixture: Path) -> Result:
    source: Path = fixture / "jsonl.msync"
    source.write_text('"hello, 可莉" -> ./jsonl-output;\n', encoding="utf-8")
    destination: Path = fixture / "trace.jsonl"
    process = invoke(
        binary,
        fixture,
        ["--no-config", "run", str(source), "--trace", str(destination)],
    )
    require(process)
    assert (fixture / "jsonl-output").read_bytes() == "hello, 可莉".encode()
    found = records(destination)
    assert any(
        row["kind"] == "span" and row["name"] == "run" and row["status"] == "ok"
        for row in found
    ), found
    assert any(
        row["name"] in {"copy", "transfer"} and row["status"] == "ok" for row in found
    ), found
    assert all(
        row["schema_version"] == 1 and len(row["trace_id"]) == 32 for row in found
    )
    assert len({row["trace_id"] for row in found}) == 1
    return Result("jsonl-runtime-bytes", len(found))


def sqlite(binary: Path, fixture: Path) -> Result:
    source = fixture / "sqlite.msync"
    source.write_text('"sqlite" -> ./sqlite-output;\n', encoding="utf-8")
    destination = fixture / "trace.sqlite"
    for _ in range(2):
        require(
            invoke(
                binary,
                fixture,
                ["--no-config", "run", str(source), "--telemetry-db", str(destination)],
            )
        )
    with sqlite3.connect(destination) as connection:
        assert connection.execute("PRAGMA application_id").fetchone() == (0x4D53594E,)
        assert connection.execute("PRAGMA user_version").fetchone() == (1,)
        sessions: list[tuple[str, int, str | None]] = connection.execute(
            "SELECT status,ended_unix_ns,exporter_error FROM sessions"
        ).fetchall()
        assert len(sessions) == 2 and all(
            row[0] == "complete" and row[1] > 0 and row[2] is None for row in sessions
        ), sessions
        count: int = connection.execute("SELECT count(*) FROM records").fetchone()[0]
        assert connection.execute(
            "SELECT count(*) FROM spans WHERE name='run' AND status='ok'"
        ).fetchone() == (2,)
        assert (
            connection.execute(
                "SELECT count(*) FROM records WHERE name IN ('copy','transfer')"
            ).fetchone()[0]
            >= 2
        )
        assert (
            connection.execute(
                "SELECT count(*) FROM attributes WHERE key='destination.path'"
            ).fetchone()[0]
            >= 2
        )
    return Result("sqlite-native-library-append", count)


def failed_copy(binary: Path, fixture: Path) -> Result:
    source = fixture / "failed.msync"
    source.write_text("./missing-file -> ./not-created;\n", encoding="utf-8")
    destination = fixture / "failed.jsonl"
    process = invoke(
        binary,
        fixture,
        ["--no-config", "run", str(source), "--trace", str(destination)],
    )
    require(process, 1)
    assert not (fixture / "not-created").exists()
    found = records(destination)
    assert any(row["name"] == "run" and row["status"] == "error" for row in found), (
        found
    )
    assert any(
        row["name"] in {"copy", "transfer"}
        and row["status"] == "error"
        and row["error_chain"]
        for row in found
    ), found
    assert "missing-file" in process.stderr
    return Result("failed-copy-cause-and-outcome", len(found))


def observer_failure(binary: Path, fixture: Path) -> Result:
    source = fixture / "warning.msync"
    source.write_text('"survives" -> ./warning-output;\n', encoding="utf-8")
    destination = fixture / "missing-parent" / "trace.sqlite"
    process = invoke(
        binary,
        fixture,
        ["--no-config", "run", str(source), "--telemetry-db", str(destination)],
    )
    require(process)
    assert (fixture / "warning-output").read_text() == "survives"
    assert (
        "telemetry warning" in process.stderr and str(destination) in process.stderr
    ), process.stderr
    assert not destination.parent.exists()
    return Result("observer-open-failure-preserves-work")


def no_admission(binary: Path, fixture: Path) -> Result:
    destination = fixture / "never-created" / "trace.sqlite"
    configuration = fixture / "zero.toml"
    configuration.write_text(
        '[telemetry]\nenabled = true\nbackend = "sqlite"\n'
        f"path = {json.dumps(str(destination))}\nsample_per_million = 0\n",
        encoding="utf-8",
    )
    source = fixture / "zero.msync"
    source.write_text('"zero" -> ./zero-output;\n', encoding="utf-8")
    require(
        invoke(binary, fixture, ["run", str(source), "--config", str(configuration)])
    )
    assert (fixture / "zero-output").read_text() == "zero"
    assert not destination.parent.exists()
    configuration.write_text(
        configuration.read_text().replace(
            "sample_per_million = 0", "sample_per_million = 1000000"
        )
    )
    require(invoke(binary, fixture, ["config", "--config", str(configuration)]))
    assert not destination.parent.exists()
    require(
        invoke(
            binary,
            fixture,
            ["run", str(source), "--config", str(configuration), "--no-telemetry"],
        )
    )
    assert not destination.parent.exists()
    require(
        invoke(
            binary,
            fixture,
            ["--no-config", "--telemetry-level", "instructions", source.name],
        )
    )
    assert not (fixture / "home" / ".msync").exists()
    return Result("zero-sampling-inspection-disabled-no-admission")


def foreign_database(binary: Path, fixture: Path) -> Result:
    destination = fixture / "foreign.sqlite"
    with sqlite3.connect(destination) as connection:
        connection.execute("CREATE TABLE unrelated(value TEXT)")
        connection.execute("INSERT INTO unrelated VALUES('preserved')")
    source = fixture / "foreign.msync"
    source.write_text('"work" -> ./foreign-output;\n', encoding="utf-8")
    process = invoke(
        binary,
        fixture,
        ["--no-config", "run", str(source), "--telemetry-db", str(destination)],
    )
    require(process)
    assert "telemetry warning" in process.stderr
    with sqlite3.connect(destination) as connection:
        assert connection.execute("SELECT value FROM unrelated").fetchall() == [
            ("preserved",)
        ]
        assert connection.execute("PRAGMA application_id").fetchone() == (0,)
        assert (
            connection.execute(
                "SELECT name FROM sqlite_schema WHERE name='sessions'"
            ).fetchall()
            == []
        )
    return Result("foreign-database-not-adopted")


def profile(binary: Path, fixture: Path) -> Result:
    source = fixture / "compute.msync"
    source.write_text(
        """let input? = $std::io::read_line();
let count = if input? { 0 } else { input as Number };
let i = 0;
let result = 0;
loop {
    if i >= count { break; };
    result = result + i;
    i = i + 1;
};
$std::io::println(result as String);
""",
        encoding="utf-8",
    )
    database = fixture / "profile.sqlite"
    flags = ["--no-config", "--telemetry-db", str(database)]
    for args in (
        ["compile", "-O0", source.name, "-o", "compute.mso"],
        ["compile", source.name, "--emit", "hir", "-o", "compute.mhir"],
        ["bind-ir", "compute.mso", "-o", "compute.mexe"],
    ):
        require(invoke(binary, fixture, [*flags, "--telemetry-level", "phases", *args]))
    require(
        invoke(
            binary,
            fixture,
            [
                *flags,
                "--telemetry-level",
                "profile",
                "compile",
                "-O2",
                source.name,
                "-o",
                "optimized.mso",
            ],
        )
    )
    routes = [
        ("source", source.name, "instructions"),
        ("hir", "compute.mhir", "phases"),
        ("object", "compute.mso", "profile"),
        ("executable", "compute.mexe", "instructions"),
        ("executable", "compute.mexe", "summary"),
    ]
    for _, file, level in routes:
        run = invoke(
            binary,
            fixture,
            [*flags, "--telemetry-level", level, "run", "-O0", file],
            stdin="37\n",
        )
        require(run)
        assert run.stdout.strip() == "666" and not run.stderr, run

    with sqlite3.connect(database) as connection:
        assert connection.execute("PRAGMA user_version").fetchone() == (1,)
        assert connection.execute(
            "SELECT count(*) FROM sessions WHERE status='complete' AND exporter_error IS NULL"
        ).fetchone() == (len(routes) + 4,)
        assert (
            connection.execute(
                """SELECT count(*) FROM events AS e
               JOIN spans AS p ON p.trace_id=e.trace_id AND p.span_id=e.span_id
               JOIN metrics AS m ON m.record_id=e.id AND m.key='invocations'
               WHERE e.name GLOB 'optimizer.*' AND p.name='compile' AND m.value_int>0"""
            ).fetchone()[0]
            > 0
        )
        assert not connection.execute(
            """SELECT child.id FROM records AS child
               LEFT JOIN spans AS parent ON child.trace_id=parent.trace_id
                 AND CASE WHEN child.kind='event' THEN child.span_id
                          ELSE child.parent_span_id END=parent.span_id
               WHERE (child.kind='event' OR child.parent_span_id IS NOT NULL)
                 AND parent.id IS NULL"""
        ).fetchall()
        for route, _, level in routes:
            rows: list[tuple[str]] = connection.execute(
                """SELECT r.trace_id FROM spans AS r
                   JOIN attributes AS route ON r.id=route.record_id AND route.key='input.kind'
                   JOIN attributes AS level ON r.id=level.record_id AND level.key='telemetry.level'
                   JOIN attributes AS build ON r.id=build.record_id AND build.key='implementation.build'
                   WHERE r.parent_span_id IS NULL AND r.name='run' AND r.status='ok'
                     AND route.value=? AND level.value=? AND build.value<>''""",
                (route, level),
            ).fetchall()
            assert len(rows) == 1, (route, level, rows)
            trace = rows[0][0]
            names: set[str] = {
                name
                for (name,) in connection.execute(
                    "SELECT name FROM records WHERE trace_id=?", (trace,)
                )
            }
            assert {"run", "runtime", "runtime.initialize", "execute"} <= names
            assert ("artifact.read" in names) == (route != "source"), names
            assert ("compiler.parse" in names) == (route == "source"), names
            assert ("compiler.bind" in names) == (route == "source"), names
            assert ("compiler.analyze" in names) == (route == "source"), names
            assert ("compiler.lower" in names) == (route in {"source", "hir"}), names
            assert ("compiler.optimize" in names) == (route in {"source", "hir"}), names
            assert ("bind" in names) == (route != "executable"), names
            assert ("prepare" in names) == (route != "executable"), names
            assert ("vm.opcode" in names) == (level in {"profile", "instructions"})
            assert ("vm.instruction" in names) == (level == "instructions")
            assert ("vm.edge" in names) == (level == "instructions")
            if level in {"profile", "instructions"}:
                assert (
                    connection.execute(
                        """SELECT value_int FROM spans JOIN metrics ON id=record_id
                       WHERE trace_id=? AND name='execute' AND key='managed.allocated_bytes'""",
                        (trace,),
                    ).fetchone()[0]
                    > 0
                )
                counts: dict[str, int] = dict(
                    connection.execute(
                        """SELECT e.name,sum(m.value_int)
                           FROM events AS e JOIN metrics AS m ON m.record_id=e.id
                           JOIN spans AS s ON e.span_id=s.span_id AND e.trace_id=s.trace_id
                           WHERE e.trace_id=? AND s.name='execute'
                             AND ((e.name='vm.function' AND m.key='instructions')
                               OR (e.name IN ('vm.opcode','vm.instruction') AND m.key='hits'))
                           GROUP BY e.name""",
                        (trace,),
                    )
                )
                assert counts["vm.function"] == counts["vm.opcode"] > 37, counts
                if level == "instructions":
                    assert counts["vm.instruction"] == counts["vm.function"], counts
            assert connection.execute(
                """SELECT count(*) FROM spans JOIN metrics ON id=record_id
                   WHERE trace_id=? AND parent_span_id IS NULL AND key GLOB 'startup.*'""",
                (trace,),
            ).fetchone() == (3,)
        count: int = connection.execute("SELECT count(*) FROM records").fetchone()[0]
    return Result("sqlite-profile-source-hir-object-executable", count)


def scheduler_profile(binary: Path, fixture: Path) -> Result:
    source = fixture / "sync.msync"
    source.write_text('"payload" -> ./copied.txt;\n', encoding="utf-8")
    database = fixture / "scheduler.sqlite"
    require(
        invoke(
            binary,
            fixture,
            [
                "--no-config",
                "--telemetry-db",
                str(database),
                "--telemetry-level",
                "phases",
                source.name,
            ],
        )
    )
    assert (fixture / "copied.txt").read_bytes() == b"payload"
    with sqlite3.connect(database) as connection:
        assert (
            connection.execute(
                """SELECT count(*) FROM records AS child
               JOIN spans AS parent ON parent.trace_id=child.trace_id
                 AND CASE WHEN child.kind='event' THEN child.span_id
                          ELSE child.parent_span_id END=parent.span_id
               WHERE child.name IN ('copy','transfer','transfer-batch')
                 AND child.status='ok' AND parent.name='runtime'"""
            ).fetchone()[0]
            > 0
        )
        assert (
            connection.execute(
                """SELECT value_int FROM spans JOIN metrics ON id=record_id
               WHERE name='execute' AND key='scheduler.admitted_operations'"""
            ).fetchone()[0]
            > 0
        )
        count: int = connection.execute("SELECT count(*) FROM records").fetchone()[0]
    return Result("sqlite-profile-scheduler-causality-and-payload", count)


def repl_profile(binary: Path, fixture: Path) -> Result:
    database = fixture / "repl.sqlite"
    config = fixture / "repl.toml"
    config.write_text(
        '[telemetry]\nenabled = true\nlevel = "profile"\npath = "repl.sqlite"\n',
        encoding="utf-8",
    )
    run = invoke(
        binary,
        fixture,
        ["repl", "--config", str(config)],
        stdin="let x = 1;\nx + 2\n/quit\n",
    )
    require(run)
    assert run.stdout.splitlines() == ["3"], run
    with sqlite3.connect(database) as connection:
        assert connection.execute(
            """SELECT count(*) FROM spans AS s JOIN attributes AS a ON s.id=a.record_id
               WHERE s.name='repl.submission' AND a.key='submission.kind' AND a.value='input'
                 AND s.status='ok'"""
        ).fetchone() == (2,)
        assert connection.execute(
            """SELECT count(DISTINCT s.span_id)
               FROM spans AS s JOIN events AS e ON s.trace_id=e.trace_id AND s.span_id=e.span_id
               JOIN spans AS submission ON submission.trace_id=s.trace_id AND submission.span_id=s.parent_span_id
               JOIN attributes AS a ON submission.id=a.record_id
               WHERE s.name='execute' AND e.name='vm.function'
                 AND a.key='submission.kind' AND a.value='input'"""
        ).fetchone() == (2,)
        count: int = connection.execute("SELECT count(*) FROM records").fetchone()[0]
    return Result("repl-profile-submission-ownership", count)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument(
        "--case",
        choices=[
            "jsonl",
            "sqlite",
            "failed",
            "warning",
            "disabled",
            "foreign",
            "profile",
            "repl",
            "scheduler",
        ],
    )
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve(strict=True)
    stamp: str = hashlib.sha256(binary.read_bytes()).hexdigest()[:12]
    fixture: Path = ROOT / ".temp" / "telemetry-v080" / stamp
    fixture.mkdir(parents=True, exist_ok=True)
    cases = {
        "jsonl": jsonl,
        "sqlite": sqlite,
        "failed": failed_copy,
        "warning": observer_failure,
        "disabled": no_admission,
        "foreign": foreign_database,
        "profile": profile,
        "repl": repl_profile,
        "scheduler": scheduler_profile,
    }
    results: list[dict[str, str | int]] = []
    for name, case in cases.items():
        if options.case is not None and name != options.case:
            continue
        work: Path = fixture / name
        if work.exists():
            shutil.rmtree(work)
        work.mkdir()
        result: Result = case(binary, work)
        results.append({"name": result.name, "records": result.records})
        print(f"PASS {result.name} ({result.records} records)")
    (fixture / "results.json").write_text(
        json.dumps(
            {"binary": str(binary), "sha256": stamp, "journeys": results}, indent=2
        )
        + "\n"
    )


if __name__ == "__main__":
    main()
