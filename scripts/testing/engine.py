"""Resource-aware admission, supervised process trees, and exclusive measurements."""

from __future__ import annotations

import asyncio
import os
import signal
import sys
import time
import traceback
from pathlib import Path
from typing import Any

from . import compiler
from .model import ROOT, Task
from .resources import Scope
from .store import Store


def tail(path: Path) -> str:
    with path.open("rb") as stream:
        stream.seek(max(0, path.stat().st_size - 8192))
        return stream.read().decode("utf-8", errors="replace")


async def terminate(process: asyncio.subprocess.Process, group: Path) -> None:
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        await asyncio.wait_for(process.wait(), 1)
    except TimeoutError:
        pass
    # A grandchild can create a new session; cgroup ownership, not PGID, is authoritative.
    (group / "cgroup.kill").write_text("1", encoding="ascii")
    await process.wait()


async def execute(
    number: int,
    task: Task,
    binary: Path,
    directory: Path,
    scope: Scope,
    store: Store,
    stop: asyncio.Event,
) -> dict[str, Any]:
    directory.mkdir(parents=True)
    temporary: Path = directory / "tmp"
    temporary.mkdir()
    command: list[str] = [
        task.render(value, binary, directory) for value in task.command
    ]
    cwd: Path = Path(task.render(task.cwd, binary, directory))
    stdout: Path = directory / "stdout.log"
    stderr: Path = directory / "stderr.log"
    launch_error: Path = directory / "launch-error.json"
    environment: dict[str, str] = dict(
        os.environ, TMPDIR=str(temporary), TEMP=str(temporary), TMP=str(temporary)
    )
    environment.pop("MSYNC_JIT", None)
    environment.update(
        {key: task.render(value, binary, directory) for key, value in task.environment}
    )
    store.start(number, command, cwd, stdout, stderr)
    row: dict[str, Any] = {
        "name": task.name,
        "status": "infra_error",
        "command": command,
        "cwd": str(cwd),
        "stdout_path": str(stdout),
        "stderr_path": str(stderr),
    }
    group: Path = scope.task(number)
    stopped: asyncio.Task[bool] = asyncio.create_task(stop.wait())
    process: asyncio.subprocess.Process | None = None
    paused: list[Path] = []
    try:
        with stdout.open("wb") as output, stderr.open("wb") as errors:
            if task.exclusive and not task.uses_compiler:
                paused = compiler.park(scope.path.parent)
                store.event(
                    "compiler.parked",
                    {"groups": [str(path) for path in paused]},
                    number,
                )
            if task.uses_compiler:
                state: dict[str, Any] = compiler.ensure()
                row["compiler_before"] = state
                environment.update(compiler.environment(directory / "compiler.log"))
                store.event("compiler.attached", state, number)
            process = await asyncio.create_subprocess_exec(
                sys.executable,
                str(ROOT / "scripts/testing/child.py"),
                str(group),
                str(launch_error),
                *command,
                cwd=cwd,
                env=environment,
                stdout=output,
                stderr=errors,
                start_new_session=True,
            )
            row["pid"] = process.pid
            store.event(
                "process.started", {"pid": process.pid, "cgroup": str(group)}, number
            )
            waiting: asyncio.Task[int] = asyncio.create_task(process.wait())
            done, _ = await asyncio.wait(
                (waiting, stopped),
                timeout=task.timeout,
                return_when=asyncio.FIRST_COMPLETED,
            )
            if waiting in done:
                row["status"] = "passed" if process.returncode == 0 else "failed"
            else:
                row["status"] = "cancelled" if stop.is_set() else "timeout"
                row["error"] = (
                    "Cancelled by owner"
                    if stop.is_set()
                    else f"Task exceeded {task.timeout:g} seconds"
                )
                store.event(
                    "process.terminate",
                    {
                        "reason": row["status"],
                        "owned_processes": scope.processes(group),
                    },
                    number,
                )
                await terminate(process, group)
            await waiting
            row["exit_code"] = process.returncode
        if launch_error.exists():
            row.update(
                status="infra_error", error=launch_error.read_text(encoding="utf-8")
            )
    except Exception as error:  # noqa: BLE001 -- the supervision boundary preserves every concrete launch/setup failure
        row.update(
            status="infra_error",
            error=f"{type(error).__name__}: {error}",
            error_chain=traceback.format_exception(error),
        )
    finally:
        stopped.cancel()
        await asyncio.gather(stopped, return_exceptions=True)
        if process is not None and process.returncode is None:
            await terminate(process, group)
        try:
            await finish(number, task, binary, directory, group, scope, store, row)
        finally:
            compiler.resume(paused)
            if paused:
                store.event(
                    "compiler.resumed",
                    {"groups": [str(path) for path in paused]},
                    number,
                )

    return row


async def finish(
    number: int,
    task: Task,
    binary: Path,
    directory: Path,
    group: Path,
    scope: Scope,
    store: Store,
    row: dict[str, Any],
) -> None:
    if task.receipt is not None:
        receipt: Path = Path(task.render(task.receipt, binary, directory))
        if receipt.exists():
            row["receipt_path"] = str(receipt)
        elif row["status"] == "passed":
            row.update(
                status="infra_error",
                error=f"Task exited successfully without its receipt: {receipt}",
            )
    if task.uses_compiler:
        if row["status"] in ("timeout", "cancelled"):
            store.event(
                "compiler.cancel",
                {"reason": row["status"], "unit": compiler.unit()},
                number,
            )
            compiler.stop()
        row["compiler_after"] = compiler.snapshot()
        store.event("compiler.accounting", row["compiler_after"], number)
        compiler_log: Path = directory / "compiler.log"
        if compiler_log.exists():
            row["compiler_path"] = str(compiler_log)
    store.event("process.cleanup", {"owned_processes": scope.processes(group)}, number)
    row["resources"] = await scope.reap(group)
    row["finished_ns"] = time.perf_counter_ns()
    row["stdout_tail"] = (
        tail(directory / "stdout.log") if (directory / "stdout.log").exists() else ""
    )
    row["stderr_tail"] = (
        tail(directory / "stderr.log") if (directory / "stderr.log").exists() else ""
    )
    store.finish(number, row)
    store.event(
        "process.completed",
        {
            "status": row["status"],
            "exit_code": row.get("exit_code"),
            "resources": row["resources"],
            "error": row.get("error"),
            "error_chain": row.get("error_chain"),
        },
        number,
    )


Running = dict[asyncio.Task[dict[str, Any]], tuple[int, Task]]


def admission(
    task: Task, active: Running, cpus: int, memory_mib: int
) -> dict[str, Any]:
    running: list[Task] = [entry[1] for entry in active.values()]
    cpu_used: int = sum(item.cpu for item in running)
    memory_used: int = sum(item.memory_mib for item in running)
    held: set[str] = {lock for item in running for lock in item.locks}
    contested: list[str] = sorted(held.intersection(task.locks))
    reasons: list[str] = []
    if running and (task.exclusive or any(item.exclusive for item in running)):
        reasons.append("exclusive reservation")
    if cpu_used + task.cpu > cpus:
        reasons.append("CPU budget")
    if memory_used + task.memory_mib > memory_mib:
        reasons.append("memory budget")
    if contested:
        reasons.append("resource locks")
    return {
        "admitted": not reasons,
        "blocked_by": reasons,
        "contested_locks": contested,
        "cpu_used": cpu_used,
        "memory_used_mib": memory_used,
        "cpu_requested": task.cpu,
        "memory_requested_mib": task.memory_mib,
        "exclusive": task.exclusive,
    }


def admit(
    pending: list[tuple[int, Task]],
    active: Running,
    binary: Path,
    work: Path,
    scope: Scope,
    store: Store,
    stop: asyncio.Event,
    cpus: int,
    memory_mib: int,
    jobs: int,
) -> None:
    for number, task in pending[:]:
        if len(active) >= jobs:
            break
        decision: dict[str, Any] = admission(task, active, cpus, memory_mib)
        store.event(
            "scheduler.admit" if decision["admitted"] else "scheduler.blocked",
            decision,
            number,
        )
        if not decision["admitted"]:
            if task.exclusive:
                break
            continue
        future: asyncio.Task[dict[str, Any]] = asyncio.create_task(
            execute(
                number,
                task,
                binary,
                work / "tasks" / f"{number:03d}",
                scope,
                store,
                stop,
            )
        )
        active[future] = (number, task)
        pending.remove((number, task))


def cancel_pending(
    pending: list[tuple[int, Task]], store: Store
) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for number, task in pending:
        row: dict[str, Any] = {
            "name": task.name,
            "status": "cancelled",
            "finished_ns": time.perf_counter_ns(),
            "error": "Cancelled before admission",
        }
        store.finish(number, row)
        rows.append(row)
    pending.clear()
    return rows


async def schedule(
    tasks: list[Task],
    binary: Path,
    work: Path,
    scope: Scope,
    store: Store,
    stop: asyncio.Event,
    cpus: int,
    memory_mib: int,
    jobs: int,
    fail_fast: bool = False,
) -> list[dict[str, Any]]:
    pending: list[tuple[int, Task]] = list(enumerate(tasks))
    active: Running = {}
    rows: list[dict[str, Any]] = []
    try:
        while pending or active:
            if stop.is_set():
                rows.extend(cancel_pending(pending, store))
            admit(
                pending,
                active,
                binary,
                work,
                scope,
                store,
                stop,
                cpus,
                memory_mib,
                jobs,
            )
            if not active:
                continue
            completed, _ = await asyncio.wait(
                active, return_when=asyncio.FIRST_COMPLETED
            )
            for future in completed:
                _, task = active.pop(future)
                outcome: dict[str, Any] = future.result()
                rows.append(outcome)
                print(f"{outcome['status'].upper()} {task.name}", flush=True)
                if fail_fast and outcome["status"] != "passed":
                    stop.set()
    finally:
        if active:
            stop.set()
            await asyncio.gather(*active, return_exceptions=True)
    return rows
