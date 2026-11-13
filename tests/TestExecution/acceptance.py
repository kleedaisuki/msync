"""Exercise actual scheduling, native failure, logs, replay and detached-tree cancellation."""

from __future__ import annotations

import argparse
import json
import signal
import sqlite3
import subprocess
import sys
import time
from contextlib import closing
from pathlib import Path
from typing import Any

from fixture import PAYLOAD

ROOT: Path = Path(__file__).resolve().parents[2]
FIXTURE: str = "{root}/tests/TestExecution/fixture.py"


def task(name: str, mode: str = "wait", **options: Any) -> dict[str, Any]:
    return {
        "name": name,
        "command": [sys.executable, FIXTURE, mode],
        "memory_mib": 128,
        **options,
    }


def plan(directory: Path, name: str, tasks: list[dict[str, Any]]) -> Path:
    path: Path = directory / (name + ".json")
    path.write_text(json.dumps({"tasks": tasks}), encoding="utf-8")
    return path


def command(binary: Path, work: Path, *arguments: str) -> list[str]:
    return [
        sys.executable,
        str(ROOT / "scripts/test.py"),
        "--binary",
        str(binary),
        "--work",
        str(work),
        *arguments,
    ]


def rows(work: Path) -> dict[str, tuple[int, int, str]]:
    with closing(sqlite3.connect(work / "results.sqlite")) as db:
        assert db.execute("PRAGMA foreign_key_check").fetchall() == []
        return {
            name: (start, finish, status)
            for name, start, finish, status in db.execute(
                "SELECT name,started_ns,finished_ns,status FROM tasks"
            )
        }


def gone(pid: int) -> None:
    path: Path = Path(f"/proc/{pid}/stat")
    assert not path.exists() or path.read_text().split(") ")[1][0] == "Z", (
        f"Owned process survived: {pid}"
    )


def wait_for(path: Path, process: subprocess.Popen[bytes]) -> None:
    deadline: float = time.monotonic() + 20
    while not path.exists():
        assert process.poll() is None, f"Controller exited before {path}"
        assert time.monotonic() < deadline, f"No readiness marker at {path}"
        time.sleep(0.02)


def mixed(binary: Path, directory: Path) -> dict[str, Any]:
    definitions: list[dict[str, Any]] = [
        task("stream-a", "stream"),
        task("stream-b", "stream"),
        task("lock-a", locks=["shared-fixture"]),
        task("lock-b", locks=["shared-fixture"]),
        task("measurement", exclusive=True),
        task("after-measurement"),
        task(
            "guest-error",
            "guest-error",
            command=[sys.executable, FIXTURE, "guest-error", "--binary", "{binary}"],
        ),
        task("launch-error", command=[str(directory / "missing-program")]),
        task("timeout-tree", "tree", timeout=2),
    ]
    work: Path = directory / "mixed"
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        command(
            binary, work, "--plan", str(plan(directory, "mixed-plan", definitions))
        ),
        check=False,
    )
    assert result.returncode == 1
    observed: dict[str, tuple[int, int, str]] = rows(work)
    assert (
        observed["stream-a"][0] < observed["stream-b"][1]
        and observed["stream-b"][0] < observed["stream-a"][1]
    )
    assert observed["lock-a"][1] <= observed["lock-b"][0]
    for name, (start, finish, _) in observed.items():
        if name != "measurement":
            exclusive_start, exclusive_finish, _ = observed["measurement"]
            assert finish <= exclusive_start or start >= exclusive_finish, name
    assert observed["guest-error"][2] == "failed"
    assert observed["launch-error"][2] == "infra_error"
    assert observed["timeout-tree"][2] == "timeout"
    gone(int((work / "tasks/008/descendant.pid").read_text()))
    with closing(sqlite3.connect(work / "results.sqlite")) as db:
        for name in ("stream-a", "stream-b"):
            identity: int = db.execute(
                "SELECT id FROM tasks WHERE name=?", (name,)
            ).fetchone()[0]
            content: bytes = b"".join(
                item[0]
                for item in db.execute(
                    "SELECT data FROM output_chunks WHERE task_id=? AND stream='stdout' ORDER BY offset",
                    (identity,),
                )
            )
            assert content == PAYLOAD
        assert "invalid.msync" in (work / "tasks/006/stderr.log").read_text()

    replay: Path = directory / "replay"
    result = subprocess.run(
        command(
            binary,
            replay,
            "--replay",
            str(work / "results.sqlite"),
            "--task",
            "stream-a",
        ),
        check=False,
    )
    assert result.returncode == 0
    assert set(rows(replay)) == {"stream-a"}
    assert (replay / "tasks/000/stdout.log").read_bytes() == PAYLOAD
    assert rows(work)["guest-error"][2] == "failed"
    fail_fast_work: Path = directory / "fail-fast"
    fail_fast_plan: Path = plan(
        directory, "fail-fast-plan", [definitions[6], task("not-started")]
    )
    result = subprocess.run(
        command(
            binary,
            fail_fast_work,
            "--plan",
            str(fail_fast_plan),
            "--jobs",
            "1",
            "--fail-fast",
        ),
        check=False,
    )
    assert result.returncode == 1
    assert rows(fail_fast_work)["guest-error"][2] == "failed"
    assert rows(fail_fast_work)["not-started"][2] == "cancelled"
    return {
        "mixed_outcomes": {name: value[2] for name, value in observed.items()},
        "lossless_stdout_bytes_per_stream": len(PAYLOAD),
        "replay": "passed",
        "fail_fast_preserves_failure_exit": True,
    }


def cancellation(binary: Path, directory: Path) -> dict[str, Any]:
    work: Path = directory / "cancelled"
    first_plan: Path = plan(
        directory,
        "cancel-plan",
        [task("active-tree", "tree", timeout=120), task("queued")],
    )
    second_work: Path = directory / "lease-waiter"
    second_plan: Path = plan(directory, "lease-plan", [task("after-lease")])
    with (directory / "cancel-driver.log").open("wb") as output:
        first: subprocess.Popen[bytes] = subprocess.Popen(
            command(binary, work, "--plan", str(first_plan), "--jobs", "1"),
            stdout=output,
            stderr=output,
        )
        second: subprocess.Popen[bytes] | None = None
        try:
            marker: Path = work / "tasks/000/descendant.pid"
            wait_for(marker, first)
            second = subprocess.Popen(
                command(binary, second_work, "--plan", str(second_plan)),
                stdout=output,
                stderr=output,
            )
            wait_for(second_work / "results.sqlite", second)
            deadline: float = time.monotonic() + 10
            while True:
                with closing(sqlite3.connect(second_work / "results.sqlite")) as db:
                    if db.execute(
                        "SELECT 1 FROM phases WHERE name='resource.lease.wait'"
                    ).fetchone():
                        break
                assert time.monotonic() < deadline
                time.sleep(0.02)
            first.send_signal(signal.SIGTERM)
            assert first.wait(timeout=15) == 130
            assert second.wait(timeout=15) == 0
            assert all(value[2] == "cancelled" for value in rows(work).values())
            gone(int(marker.read_text()))
            with closing(sqlite3.connect(work / "results.sqlite")) as db:
                released: int = db.execute(
                    "SELECT MAX(timestamp_ns) FROM phases WHERE name='process.completed'"
                ).fetchone()[0]
            with closing(sqlite3.connect(second_work / "results.sqlite")) as db:
                acquired: int = db.execute(
                    "SELECT timestamp_ns FROM phases WHERE name='resource.lease.acquired'"
                ).fetchone()[0]
            assert acquired >= released
        finally:
            for process in (first, second):
                if process is not None and process.poll() is None:
                    process.terminate()
                    process.wait(timeout=15)
    return {
        "active_and_queued_cancelled": True,
        "detached_descendant_reaped": True,
        "global_lease_serializes_controllers": True,
    }


def receipts(binary: Path, directory: Path) -> dict[str, Any]:
    work: Path = directory / "receipt-contract"
    descriptions: list[dict[str, Any]] = [
        task("receipt-present", "stream", receipt="{work}/report.json"),
        task("receipt-missing", receipt="{work}/missing.json"),
    ]
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        command(
            binary, work, "--plan", str(plan(directory, "receipt-plan", descriptions))
        ),
        check=False,
    )
    assert result.returncode == 1
    observed: dict[str, tuple[int, int, str]] = rows(work)
    assert observed["receipt-present"][2] == "passed"
    assert observed["receipt-missing"][2] == "infra_error"
    with closing(sqlite3.connect(work / "results.sqlite")) as db:
        identity, path = db.execute(
            "SELECT id,receipt_path FROM tasks WHERE name='receipt-present'"
        ).fetchone()
        exported: bytes = b"".join(
            item[0]
            for item in db.execute(
                "SELECT data FROM output_chunks WHERE task_id=? AND stream='receipt' ORDER BY offset",
                (identity,),
            )
        )
        assert exported == Path(path).read_bytes()
    return {"receipt_bytes_lossless": True, "missing_receipt_is_not_success": True}


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument(
        "--case", choices=("all", "receipts", "controls"), default="all"
    )
    args: argparse.Namespace = parser.parse_args()
    directory: Path = args.work.resolve()
    assert directory.is_relative_to(ROOT / ".temp")
    directory.mkdir(parents=True)
    result: dict[str, Any] = {}
    if args.case in ("all", "controls"):
        result.update(mixed(args.binary.resolve(), directory))
        result.update(cancellation(args.binary.resolve(), directory))
    if args.case in ("all", "receipts"):
        result.update(receipts(args.binary.resolve(), directory))
    (directory / "acceptance.json").write_text(
        json.dumps(result, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
