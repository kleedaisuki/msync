#!/usr/bin/env python3
"""Check diagnostic trace causality against an owned real OpenSSH channel."""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from contextlib import closing
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "support"))
from ssh_fixture import Fixture, checked, lines, owned_run


def check_trace(database: Path, remote_source: Path, payload: bytes) -> int:
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as sql:
        assert sql.execute(
            "SELECT status, exporter_error FROM sessions"
        ).fetchall() == [("complete", None)]
        clients: list[tuple[int, str]] = sql.execute(
            """SELECT pid.value_int, endpoint.value FROM spans AS start
               JOIN metrics AS pid ON pid.record_id=start.id AND pid.key='transport.pid'
               JOIN attributes AS endpoint ON endpoint.record_id=start.id
                 AND endpoint.key='transport.endpoint'
               WHERE start.name='transport.start' AND start.status='ok'"""
        ).fetchall()
        assert len(clients) == 1 and clients[0][1] == "Perf_Fixture", clients
        pid: int = clients[0][0]
        assert sql.execute(
            """SELECT span.name FROM spans AS span JOIN metrics AS pid ON span.id=pid.record_id
               WHERE span.name IN ('transport.exit_wait','transport.session.close')
                 AND span.status='ok' AND pid.key='transport.pid' AND pid.value_int=?""",
            (pid,),
        ).fetchall()
        assert (
            sql.execute(
                """SELECT count(*) FROM attributes WHERE key='source.path' AND value=?""",
                (str(remote_source),),
            ).fetchone()[0]
            > 0
        )
        assert not sql.execute(
            """SELECT value FROM attributes WHERE instr(value,'unselected-source')>0
               OR instr(value,'unselected-output')>0 OR instr(value,?)>0""",
            (payload.decode(),),
        ).fetchall()
        assert sql.execute(
            """SELECT m.value_int FROM spans AS s JOIN metrics AS m ON s.id=m.record_id
               WHERE s.name='file.write' AND s.status='ok' AND m.key='bytes.written'""",
        ).fetchall() == [(len(payload),)]
        assert sql.execute(
            """SELECT count(*) FROM spans AS s
               JOIN spans AS parent ON s.trace_id=parent.trace_id AND s.parent_span_id=parent.span_id
               WHERE parent.name='file.write' AND s.name IN ('file.sync','file.publish')"""
        ).fetchone() == (2,)
    return pid


def run_traced(
    binary: Path, fixture: Fixture, work: Path, artifact: Path, stdin: bytes
) -> Path:
    database: Path = work / f"{artifact.stem}-{artifact.suffix[1:]}.sqlite"
    before: int = len(lines(fixture.calls))
    checked(
        owned_run(
            [
                str(binary),
                "--no-config",
                "--telemetry-db",
                str(database),
                "--telemetry-level",
                "diagnostics",
                "run",
                artifact.name,
            ],
            cwd=work,
            environment=fixture.environment(),
            source=stdin,
        )
    )
    assert lines(fixture.calls)[before:] == ["ssh"]
    return database


def check_owned_batch(binary: Path, fixture: Fixture, work: Path) -> None:
    uploads: dict[str, bytes] = {"upload-a": b"alpha transferred", "upload-b": b"beta"}
    for name, payload in uploads.items():
        (work / name).write_bytes(payload)
    target: Path = work / "remote"
    script: Path = work / "upload.msync"
    script.write_text(
        "let endpoint = ssh://Perf_Fixture;\n"
        f"[./upload-a, ./upload-b] -> (endpoint @ {json.dumps(str(target))});\n"
    )
    database: Path = run_traced(binary, fixture, work, script, b"")
    for name, payload in uploads.items():
        assert (target / name).read_bytes() == payload
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as sql:
        lifecycle: list[tuple[str, int]] = sql.execute(
            """SELECT name, value_int FROM spans JOIN metrics ON id=record_id
               WHERE name IN ('transport.start','transport.batch','transport.session.close')
                 AND status='ok' AND key='transport.pid' ORDER BY name"""
        ).fetchall()
        assert [name for name, _ in lifecycle] == [
            "transport.batch",
            "transport.session.close",
            "transport.start",
        ], lifecycle
        assert len({pid for _, pid in lifecycle}) == 1, lifecycle
        assert sql.execute(
            """SELECT value_int FROM spans JOIN metrics ON id=record_id
               WHERE name='transport.archive.write_entry' AND status='ok'
                 AND key='bytes.payload' ORDER BY value_int"""
        ).fetchall() == [(size,) for size in sorted(map(len, uploads.values()))]
        assert sql.execute(
            """SELECT count(*) FROM spans AS response
               JOIN spans AS batch ON response.trace_id=batch.trace_id
                 AND response.parent_span_id=batch.span_id
               WHERE response.name='transport.batch.response' AND response.status='ok'
                 AND batch.name='transport.batch'"""
        ).fetchone() == (len(uploads),)
    print(f"PASS prepared upload batch: two payloads, one SSH channel, {database}")


def run(binary: Path) -> None:
    with Fixture(ROOT / ".temp" / "ssh-diagnostics") as fixture:
        work: Path = fixture.root / "work"
        work.mkdir()
        source: Path = work / "remote-source"
        payload: bytes = b"payload bytes not copied into the timeline"
        source.write_bytes(payload)
        script: Path = work / "program.msync"
        script.write_text(
            "let choice? = $std::io::read_line();\n"
            "let endpoint = ssh://Perf_Fixture;\n"
            "if choice? {\n"
            '    (endpoint @ "unselected-source") -> ./unselected-output;\n'
            "} else {\n"
            f"    (endpoint @ {json.dumps(str(source))}) -> ./published;\n"
            "};\n"
        )
        environment: dict[str, str] = fixture.environment()
        baseline: int = len(lines(fixture.calls))
        for args in (
            ["compile", script.name, "-o", "program.mso"],
            ["bind-ir", "program.mso", "-o", "program.mexe"],
        ):
            checked(
                owned_run(
                    [str(binary), "--no-config", *args],
                    cwd=work,
                    environment=environment,
                )
            )
        assert len(lines(fixture.calls)) == baseline
        for artifact in (script, work / "program.mso", work / "program.mexe"):
            database: Path = run_traced(binary, fixture, work, artifact, b"selected\n")
            assert (work / "published").read_bytes() == payload
            assert not (work / "unselected-output").exists()
            pid: int = check_trace(database, source, payload)
            print(f"PASS {artifact.suffix}: one SSH channel, pid {pid}, {database}")
            (work / "published").unlink()
        check_owned_batch(binary, fixture, work)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    args: argparse.Namespace = parser.parse_args()
    run(args.binary.resolve(strict=True))


if __name__ == "__main__":
    main()
