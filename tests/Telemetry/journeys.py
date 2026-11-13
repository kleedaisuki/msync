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
import sys
import time
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
        [
            "--no-config",
            "run",
            str(source),
            "--trace",
            str(destination),
            "--telemetry-level",
            "phases",
        ],
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
    assert any(
        row["name"] == "execute"
        and row["status"] == "error"
        and row["metrics"].get("scheduler.admitted_operations") == 1
        for row in found
    ), found
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
        totals: dict[str, int] = dict(
            connection.execute(
                """SELECT key,value_int FROM spans JOIN metrics ON id=record_id
               WHERE name='execute' AND key GLOB 'scheduler.*'"""
            )
        )
        assert totals["scheduler.admitted_operations"] == 1, totals
        assert totals["scheduler.admitted_transfers"] == 1, totals
        assert totals["scheduler.resource_nodes_visited"] > 0, totals
        assert totals["scheduler.submitted_plans"] == 1, totals
        assert totals["scheduler.submitted_batches"] == 1, totals
        assert totals["scheduler.admission_ns"] > 0, totals
        count: int = connection.execute("SELECT count(*) FROM records").fetchone()[0]
    count += scheduler_hazard(binary, fixture / "hazard")
    return Result("sqlite-profile-scheduler-causality-and-payload", count)


def scheduler_hazard(binary: Path, fixture: Path) -> int:
    fixture.mkdir()
    payload: str = "gated scheduler payload"
    (fixture / "source.txt").write_bytes(b"typed Path source bytes")
    (fixture / "producer.py").write_text(
        "from pathlib import Path\nimport sys\nimport time\n"
        "Path('child-ready').touch()\n"
        "while not Path('gate').exists():\n    time.sleep(0.01)\n"
        + f"sys.stdout.write({payload!r})\n",
        encoding="utf-8",
    )
    python: str = json.dumps(Path(sys.executable).resolve().as_posix())
    (fixture / "hazard.msync").write_text(
        f"""let child = $std::process::spawn({python}, ["producer.py"]);
child::stdout -> ./held.txt;
"independent bytes" -> ./independent.txt;
$std::io::write_text(./at-query, "entered");
if $std::fs::exists(./held.txt) {{ $std::io::println("visible"); }};
acquire child;
let path_child = $std::process::spawn({python}, ["-c", "print('source.txt', end='')"]);
(path_child::stdout as Path) -> ./from-path.txt;
acquire path_child;
""",
        encoding="utf-8",
    )
    database: Path = fixture / "hazard.sqlite"
    with subprocess.Popen(
        [
            str(binary),
            "--no-config",
            "--jobs",
            "2",
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            "diagnostics",
            "hazard.msync",
        ],
        cwd=fixture,
        text=True,
        encoding="utf-8",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    ) as process:
        try:
            assert wait_for_file(fixture / "child-ready", 10), process.poll()
            assert wait_for_file(fixture / "at-query", 10), process.poll()
            assert wait_for_file(fixture / "independent.txt", 5), process.poll()
            assert (fixture / "independent.txt").read_bytes() == b"independent bytes"
            assert not (fixture / "held.txt").exists() and process.poll() is None
            assert wait_for_scheduler_dependency(database), process.poll()
        finally:
            (fixture / "gate").touch()
            stdout, stderr = process.communicate(timeout=25)
    assert process.returncode == 0 and stdout.strip() == "visible", (stdout, stderr)
    assert (fixture / "held.txt").read_text(encoding="utf-8") == payload
    assert (fixture / "from-path.txt").read_bytes() == b"typed Path source bytes"
    with sqlite3.connect(database) as connection:
        totals: dict[str, int] = dict(
            connection.execute(
                """SELECT key,value_int FROM spans JOIN metrics ON id=record_id
            WHERE name='execute' AND key GLOB 'scheduler.*'"""
            )
        )
        for metric in (
            "admitted_operations",
            "admitted_transfers",
            "resource_nodes_visited",
            "dependency_edges",
            "native_hazard_waits",
            "admission_ns",
            "wait_ns",
        ):
            assert totals[f"scheduler.{metric}"] > 0, totals
        assert totals["scheduler.submitted_plans"] == 0, totals
        assert totals["scheduler.submitted_batches"] == 0, totals
        footprints: dict[str, int] = dict(
            connection.execute(
                """SELECT destination.value,reads.value_int FROM spans AS admission
            JOIN attributes AS destination ON destination.record_id=admission.id AND destination.key='transfer.destination'
            JOIN metrics AS reads ON reads.record_id=admission.id AND reads.key='resources.reads'
            WHERE admission.name='scheduler.admission'"""
            )
        )
        assert (
            next(
                reads for path, reads in footprints.items() if path.endswith("held.txt")
            )
            == 0
        ), footprints
        assert (
            next(
                reads
                for path, reads in footprints.items()
                if path.endswith("from-path.txt")
            )
            == 1
        ), footprints
        assert (
            connection.execute(
                """SELECT count(*) FROM spans AS waiting
            JOIN attributes AS reason ON reason.record_id=waiting.id AND reason.key='wait.reason'
            JOIN metrics AS pending ON pending.record_id=waiting.id AND pending.key='dependencies.pending'
            JOIN metrics AS suspended ON suspended.record_id=waiting.id AND suspended.key='await.suspended'
            JOIN events AS edge ON edge.trace_id=waiting.trace_id AND edge.span_id=waiting.span_id
            JOIN attributes AS producer ON producer.record_id=edge.id AND producer.key='dependency.span_id'
            JOIN spans AS admission ON admission.trace_id=edge.trace_id AND admission.span_id=producer.value
            JOIN attributes AS destination ON destination.record_id=admission.id AND destination.key='transfer.destination'
            WHERE waiting.name='scheduler.wait' AND reason.value='native.resources'
            AND pending.value_int>0 AND suspended.value_int=1 AND waiting.duration_ns>0
            AND edge.name='runtime.dependency' AND admission.name='scheduler.admission'
            AND destination.value LIKE '%held.txt'"""
            ).fetchone()[0]
            > 0
        )
        return int(connection.execute("SELECT count(*) FROM records").fetchone()[0])


def wait_for_scheduler_dependency(database: Path) -> bool:
    deadline: float = time.monotonic() + 10
    while time.monotonic() < deadline:
        with sqlite3.connect(database) as connection:
            found: int = connection.execute(
                """SELECT count(*) FROM events AS edge
                JOIN attributes AS producer ON producer.record_id=edge.id AND producer.key='dependency.span_id'
                JOIN spans AS admission ON admission.trace_id=edge.trace_id AND admission.span_id=producer.value
                JOIN attributes AS destination ON destination.record_id=admission.id AND destination.key='transfer.destination'
                WHERE edge.name='runtime.dependency' AND admission.name='scheduler.admission'
                AND destination.value LIKE '%held.txt'"""
            ).fetchone()[0]
        if found > 0:
            return True
        time.sleep(0.005)
    return False


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


def wait_for_file(path: Path, timeout: float) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if path.exists():
            return True
        time.sleep(0.005)
    return path.exists()


def diagnostics(binary: Path, fixture: Path) -> Result:
    output_token = "child-payload-is-not-trace-metadata"
    (fixture / "producer.py").write_text(
        """from pathlib import Path
import sys
import time

Path("child-ready").touch()
while not Path("gate").exists():
    time.sleep(0.01)
"""
        + f"sys.stdout.write({output_token!r})\n",
        encoding="utf-8",
    )
    (fixture / "input.txt").write_bytes(b"selected input")
    python = json.dumps(Path(sys.executable).resolve().as_posix(), ensure_ascii=False)
    (fixture / "case.msync").write_text(
        f"""let child = $std::process::spawn({python}, ["producer.py"]);
child::stdout -> ./held.txt;
let route? = $std::io::read_line();
if route? {{ ./never-src.txt -> ./unselected.txt; }} else {{
    if route == "copy" {{ ./input.txt -> ./independent.txt; }}
    else {{ ./never-src.txt -> ./unselected.txt; }};
}};
$std::io::write_text(./at-query, "entered");
let first = $std::ranges::front([1, 2]);
if $std::fs::exists(./held.txt) and first == 1 {{ $std::io::println("visible"); }};
acquire child;
""",
        encoding="utf-8",
    )
    config = fixture / "lossless.toml"
    config.write_text("[telemetry]\nqueue_capacity = 1\n", encoding="utf-8")
    for args in (
        ["compile", "-O0", "case.msync", "-o", "case.mso"],
        ["bind-ir", "case.mso", "-o", "case.mexe"],
    ):
        require(invoke(binary, fixture, ["--no-config", *args]))

    record_count = 0
    for route in ("msync", "mexe"):
        for filename in (
            "gate",
            "child-ready",
            "at-query",
            "held.txt",
            "independent.txt",
        ):
            (fixture / filename).unlink(missing_ok=True)
        database = fixture / f"{route}.sqlite"
        with subprocess.Popen(
            [
                str(binary),
                "--config",
                str(config),
                "--telemetry-db",
                str(database),
                "--telemetry-level",
                "diagnostics",
                "run",
                "-O0",
                f"case.{route}",
            ],
            cwd=fixture,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        ) as process:
            assert process.stdin is not None
            try:
                process.stdin.write("copy\n")
                process.stdin.flush()
                assert wait_for_file(fixture / "child-ready", 10), process.poll()
                assert wait_for_file(fixture / "at-query", 10), process.poll()
                assert wait_for_file(fixture / "independent.txt", 1), process.poll()
                assert not (fixture / "held.txt").exists()
                assert process.poll() is None
                time.sleep(0.10)
            finally:
                (fixture / "gate").touch()
                stdout, stderr = process.communicate(timeout=25)
        assert process.returncode == 0 and stdout.strip() == "visible", (stdout, stderr)
        assert (fixture / "held.txt").read_text(encoding="utf-8") == output_token
        assert (fixture / "independent.txt").read_bytes() == b"selected input"
        assert not (fixture / "unselected.txt").exists()
        with sqlite3.connect(database) as connection:
            assert connection.execute(
                "SELECT status, exporter_error FROM sessions"
            ).fetchall() == [("complete", None)]
            assert connection.execute(
                """SELECT value FROM attributes
                   WHERE key='telemetry.queue.mode'"""
            ).fetchone() == ("lossless_backpressure",)
            assert not connection.execute(
                """SELECT id FROM records AS r
                   WHERE (kind='event' OR parent_span_id IS NOT NULL)
                     AND NOT EXISTS (
                       SELECT 1 FROM spans AS p WHERE p.trace_id=r.trace_id
                       AND p.span_id=CASE r.kind WHEN 'event' THEN r.span_id ELSE r.parent_span_id END)"""
            ).fetchall()
            assert (
                connection.execute(
                    "SELECT count(*) FROM spans WHERE name='vm.await' AND duration_ns>0"
                ).fetchone()[0]
                > 0
            )
            assert (
                connection.execute(
                    """SELECT count(*) FROM spans WHERE name='vm.frame'"""
                ).fetchone()
                == connection.execute(
                    """SELECT sum(m.value_int) FROM events AS e JOIN metrics AS m ON m.record_id=e.id
                   WHERE e.name='vm.function' AND m.key='calls'"""
                ).fetchone()
            )
            assert (
                connection.execute(
                    """SELECT count(*) FROM events AS edge
                   JOIN attributes AS related ON related.record_id=edge.id
                     AND related.key='dependency.span_id'
                   JOIN spans AS dependency ON dependency.trace_id=edge.trace_id
                     AND dependency.span_id=related.value
                   JOIN spans AS owner ON owner.trace_id=edge.trace_id
                     AND owner.span_id=edge.span_id
                   JOIN attributes AS scope ON scope.record_id=owner.id
                     AND scope.key='dependency.scope' AND scope.value='relevant'
                   JOIN attributes AS destination ON destination.record_id=dependency.id
                     AND destination.key='transfer.destination' AND destination.value LIKE '%held.txt'
                   JOIN metrics AS suspended ON suspended.record_id=owner.id
                     AND suspended.key='await.suspended' AND suspended.value_int=1
                   WHERE edge.name='runtime.dependency' AND owner.name='scheduler.wait'
                     AND dependency.name='scheduler.admission'"""
                ).fetchone()[0]
                > 0
            )
            assert connection.execute(
                """SELECT m.value_int FROM spans AS s JOIN metrics AS m ON m.record_id=s.id
                   WHERE s.name='process.stdout' AND m.key='bytes.read' AND s.status='ok'"""
            ).fetchone() == (len(output_token),)
            assert connection.execute(
                """SELECT a.value FROM spans AS s JOIN attributes AS a ON a.record_id=s.id
                   WHERE s.name='process.exit_wait' AND a.key='wait.pending_on_entry'"""
            ).fetchone() == ("true",)
            assert connection.execute(
                """SELECT m.value_int FROM spans AS s JOIN metrics AS m ON m.record_id=s.id
                   WHERE s.name='file.copy' AND m.key=? AND s.status='ok'""",
                ("source.size_bytes" if os.name == "nt" else "bytes.written",),
            ).fetchone() == (len(b"selected input"),)
            assert connection.execute(
                """SELECT a.value FROM spans AS s JOIN attributes AS a ON a.record_id=s.id
                   WHERE s.name='vm.native' AND a.key='native.name' AND a.value='ranges.front'
                     AND s.status='ok' AND s.duration_ns>=0"""
            ).fetchone() == ("ranges.front",)
            assert (
                connection.execute(
                    """SELECT count(*) FROM spans AS s JOIN attributes AS a ON a.record_id=s.id
                   WHERE s.name GLOB 'transfer.*' AND a.value LIKE '%independent.txt'"""
                ).fetchone()[0]
                > 0
            )
            assert not connection.execute(
                """SELECT value FROM attributes
                   WHERE value LIKE '%unselected.txt%' OR value LIKE '%never-src.txt%'
                      OR value LIKE '%' || ? || '%'""",
                (output_token,),
            ).fetchall()
            record_count += connection.execute(
                "SELECT count(*) FROM records"
            ).fetchone()[0]
    return Result(
        "sqlite-lossless-timeline-selected-effects-and-real-dependency", record_count
    )


def diagnostics_exporter_failure(binary: Path, fixture: Path) -> Result:
    config = fixture / "tiny.toml"
    config.write_text(
        "[telemetry]\nqueue_capacity = 1\nmax_records_per_session = 1\n",
        encoding="utf-8",
    )
    source = fixture / "copy.msync"
    source.write_text('"still copied" -> ./copy.txt;\n', encoding="utf-8")
    database = fixture / "tiny.sqlite"
    run = invoke(
        binary,
        fixture,
        [
            "--config",
            str(config),
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            "diagnostics",
            source.name,
        ],
    )
    require(run)
    assert (fixture / "copy.txt").read_bytes() == b"still copied"
    assert "telemetry warning" in run.stderr and "record limit" in run.stderr, run
    with sqlite3.connect(database) as connection:
        status, error = connection.execute(
            "SELECT status,exporter_error FROM sessions"
        ).fetchone()
        assert status == "error" and "record limit" in error, (status, error)
    return Result("sqlite-lossless-exporter-failure-releases-producers")


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
            "diagnostics",
            "diagnostics-error",
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
        "diagnostics": diagnostics,
        "diagnostics-error": diagnostics_exporter_failure,
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
