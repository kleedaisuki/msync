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
    binary: Path, cwd: Path, arguments: list[str]
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


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument(
        "--case",
        choices=["jsonl", "sqlite", "failed", "warning", "disabled", "foreign"],
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
