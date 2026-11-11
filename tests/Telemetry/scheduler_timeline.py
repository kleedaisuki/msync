"""Check detailed scheduler causality against real, process-gated transfers."""

from __future__ import annotations

import argparse
import json
import sqlite3
import subprocess
import time
from collections.abc import Callable
from contextlib import closing
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]
HELD: str = """
let held = $std::process::spawn(/bin/sh, ["./wait.sh"]);
held::stdout -> ./held.txt;
$std::fs::copy_file(./input.txt, ./early.txt);
"""


def command(binary: Path, work: Path, *args: str) -> list[str]:
    return [str(binary), "--no-config", *args]


def run(binary: Path, work: Path, *args: str) -> str:
    return subprocess.run(
        command(binary, work, *args),
        cwd=work,
        text=True,
        check=True,
        capture_output=True,
        timeout=30,
    ).stdout


def wait_for(condition: Callable[[], bool]) -> bool:
    deadline: float = time.monotonic() + 10
    while time.monotonic() < deadline:
        if condition():
            return True
        time.sleep(0.01)
    return condition()


def database(work: Path) -> sqlite3.Connection:
    return sqlite3.connect((work / "trace.sqlite").as_uri() + "?mode=ro", uri=True)


def wait_recorded(work: Path, policy: str) -> bool:
    with closing(database(work)) as connection:
        if policy == "relevant":
            return bool(
                connection.execute(
                    """SELECT EXISTS(
                   SELECT 1 FROM events AS edge
                   JOIN attributes AS kind ON kind.record_id=edge.id
                   JOIN attributes AS producer ON producer.record_id=edge.id
                   JOIN spans AS admission ON admission.trace_id=edge.trace_id AND admission.span_id=producer.value
                   JOIN attributes AS dest ON dest.record_id=admission.id
                   WHERE edge.name='runtime.dependency'
                     AND kind.key='dependency.kind' AND kind.value='relevant'
                     AND producer.key='dependency.span_id' AND admission.name='scheduler.admission'
                     AND dest.key='transfer.destination' AND dest.value LIKE '%held.txt')"""
                ).fetchone()[0]
            )
        return bool(
            connection.execute(
                """SELECT EXISTS(SELECT 1 FROM events AS e JOIN attributes AS a ON a.record_id=e.id
               WHERE e.name='runtime.dependency' AND a.key='dependency.kind' AND a.value=?)""",
                (policy,),
            ).fetchone()[0]
        )


def gated(binary: Path, work: Path, *, fallback: bool) -> dict[str, object]:
    work.mkdir()
    (work / "input.txt").write_bytes(b"independent")
    (work / "wait.sh").write_text(
        "set -eu\nwhile [ ! -e gate ]; do sleep .01; done\nprintf held\n"
    )
    policy: str = "fallback" if fallback else "relevant"
    if fallback:
        (work / "result").mkdir()
        output: Path = work / "result/input.txt"
        source: str = (
            HELD
            + """
let destination = $std::process::spawn(/bin/sh, ["-c", "printf ./result/"]);
[./input.txt] -> (destination::stdout as Path);
"""
        )
    else:
        output = work / "result.txt"
        source = HELD + "$std::fs::copy_file(./held.txt, ./result.txt);\n"
    (work / "case.msync").write_text(source + "acquire held;\n")
    with subprocess.Popen(
        command(
            binary,
            work,
            "--telemetry-db",
            "trace.sqlite",
            "--telemetry-level",
            "diagnostics",
            "case.msync",
        ),
        cwd=work,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    ) as process:
        try:
            assert wait_for((work / "early.txt").exists), (work, process.poll())
            assert (work / "early.txt").read_bytes() == b"independent"
            assert wait_for(lambda: wait_recorded(work, policy)), (work, process.poll())
            assert not output.exists() and not (work / "held.txt").exists()
        finally:
            (work / "gate").touch()
            stdout, stderr = process.communicate(timeout=20)
        assert process.returncode == 0, (work, stdout, stderr)
    assert output.read_bytes() == (b"independent" if fallback else b"held")
    with closing(database(work)) as connection:
        waits: list[tuple[str, int, int]] = connection.execute(
            """SELECT reason.value, pending.value_int, s.duration_ns
               FROM spans AS s
               JOIN attributes AS scope ON scope.record_id=s.id AND scope.key='dependency.scope'
               JOIN attributes AS reason ON reason.record_id=s.id AND reason.key='wait.reason'
               JOIN metrics AS pending ON pending.record_id=s.id AND pending.key='dependencies.pending'
               JOIN metrics AS suspended ON suspended.record_id=s.id AND suspended.key='await.suspended'
               WHERE s.name='scheduler.wait' AND scope.value=? AND suspended.value_int=1""",
            (policy,),
        ).fetchall()
        assert waits and all(
            pending > 0 and duration > 0 for _, pending, duration in waits
        ), waits
        totals: dict[str, int] = dict(
            connection.execute(
                """SELECT m.key, m.value_int FROM spans AS s JOIN metrics AS m ON m.record_id=s.id
               WHERE s.name='execute' AND m.key IN ('scheduler.admission_ns',
                   'scheduler.cohort_admission_ns', 'scheduler.wait_ns')"""
            )
        )
        assert totals["scheduler.admission_ns"] > 0 and totals["scheduler.wait_ns"] > 0
        assert (totals["scheduler.cohort_admission_ns"] > 0) == fallback
        assert connection.execute(
            """SELECT EXISTS(
               SELECT 1 FROM spans AS s
               JOIN metrics AS p ON p.record_id=s.id AND p.key='pending.candidates'
               JOIN metrics AS d ON d.record_id=s.id AND d.key='dependencies.matched'
               WHERE s.name='scheduler.native_dependencies' AND p.value_int>0 AND d.value_int=0)"""
        ).fetchone() == (1,)
        if fallback:
            assert connection.execute(
                """SELECT a.value FROM spans AS s JOIN attributes AS a ON a.record_id=s.id
                   WHERE s.name='scheduler.cohort' AND a.key='cohort.strategy'"""
            ).fetchone() == ("drain-fallback",)
        else:
            assert connection.execute(
                """SELECT EXISTS(SELECT 1 FROM spans AS s JOIN attributes AS a ON a.record_id=s.id
                   WHERE s.name='scheduler.native_dependencies' AND a.key GLOB 'resource.publication.*.path'
                     AND a.value LIKE '%held.txt')"""
            ).fetchone() == (1,)
    return {"policy": policy, "suspended_waits": waits}


def prepared(binary: Path, work: Path) -> dict[str, object]:
    work.mkdir()
    (work / "input.txt").write_bytes(b"planned")
    (work / "case.msync").write_text("./input.txt -> ./result.txt;\n")
    run(binary, work, "compile", "case.msync", "-o", "case.mso")
    run(binary, work, "bind-ir", "case.mso", "-o", "case.mexe")
    payload: dict[str, object] = json.loads(
        run(binary, work, "disassemble", "case.mexe", "--json")
    )
    assert payload["executionPlans"], payload
    run(
        binary,
        work,
        "--telemetry-db",
        "trace.sqlite",
        "--telemetry-level",
        "diagnostics",
        "case.mexe",
    )
    assert (work / "result.txt").read_bytes() == b"planned"
    with closing(database(work)) as connection:
        records: list[tuple[str, str]] = connection.execute(
            """SELECT child.name, parent.name FROM spans AS child
               JOIN spans AS parent ON parent.trace_id=child.trace_id AND parent.span_id=child.parent_span_id
               WHERE child.name='scheduler.plan_batch'
                  OR (child.name='scheduler.wait' AND parent.name='scheduler.plan_batch')"""
        ).fetchall()
    assert ("scheduler.plan_batch", "scheduler.plan") in records, records
    assert ("scheduler.wait", "scheduler.plan_batch") in records, records
    return {"plan_causality": records}


def exercise(binary: Path, work: Path) -> list[dict[str, object]]:
    return [
        gated(binary, work / "relevant", fallback=False),
        gated(binary, work / "fallback", fallback=True),
        prepared(binary, work / "prepared"),
    ]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument(
        "--workspace", type=Path, help="Retain the SQLite traces in a new directory"
    )
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    workspace: Path | None = args.workspace
    if workspace is not None:
        work: Path = workspace.resolve()
        work.mkdir(parents=True)
        results = exercise(binary, work)
    else:
        scratch: Path = ROOT / ".temp"
        scratch.mkdir(exist_ok=True)
        with TemporaryDirectory(prefix="scheduler-timeline-", dir=scratch) as name:
            results = exercise(binary, Path(name))
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
