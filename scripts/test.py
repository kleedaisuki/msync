#!/usr/bin/env python3
"""Run focused real-process journeys concurrently against one immutable image."""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import signal
import sqlite3
import sys
import time
import traceback
import uuid
from dataclasses import replace
from pathlib import Path
from typing import Any

from testing import build, compiler
from testing.catalog import DEFAULTS, catalog
from testing.engine import execute, schedule
from testing.image import digest, freeze
from testing.model import ROOT, Task
from testing.resources import Lease, Scope, launch_scope
from testing.store import Store


def positive(value: str) -> int:
    result: int = int(value)
    if result < 1:
        raise argparse.ArgumentTypeError("expected a positive integer")
    return result


def checkpoint(work: Path, report: dict[str, Any]) -> None:
    pending: Path = work / "result.pending"
    pending.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    pending.replace(work / "result.json")


def selection(args: argparse.Namespace) -> tuple[list[Task], Path | None]:
    binary: Path | None = args.binary
    if args.prepare_only:
        return [], binary.resolve(strict=True) if binary is not None else None
    if args.replay is not None:
        with sqlite3.connect(
            args.replay.resolve().as_uri() + "?mode=ro", uri=True
        ) as db:
            metadata: dict[str, Any] = json.loads(
                db.execute("SELECT metadata_json FROM run").fetchone()[0]
            )
            binary = binary or Path(metadata["binary"])
            records: list[tuple[str, str, str]] = db.execute(
                "SELECT name,spec_json,status FROM tasks WHERE id>=0 ORDER BY id"
            ).fetchall()
            tasks: list[Task] = [
                Task.from_dict(json.loads(spec))
                for name, spec, status in records
                if name in args.journey or (not args.journey and status != "passed")
            ]
    elif args.plan is not None:
        content: dict[str, Any] = json.loads(args.plan.read_text(encoding="utf-8"))
        tasks = [Task.from_dict(spec) for spec in content["tasks"]]
        if args.journey:
            tasks = [task for task in tasks if task.name in args.journey]
    else:
        entries: dict[str, Task] = catalog()
        tasks = [
            entries[name]
            for name in dict.fromkeys(
                args.journey or (() if args.project else DEFAULTS)
            )
        ]
    for project in args.project:
        project = project.resolve(strict=True)
        if "FrozenAssemblies" not in project.read_text(encoding="utf-8"):
            raise ValueError(
                f"{project}: use a project with an explicit FrozenAssemblies contract"
            )
        tasks.append(
            Task(
                "project:" + project.stem,
                (
                    sys.executable,
                    "{root}/scripts/testing/project.py",
                    "--project",
                    str(project),
                    "--assemblies",
                    "{assemblies}",
                    "--work",
                    "{work}/project",
                ),
                cpu=2,
                memory_mib=1536,
                timeout=600,
                receipt="{work}/project/project.json",
                uses_compiler=True,
            )
        )
    if args.journey and set(args.journey) - {task.name for task in tasks}:
        raise ValueError(
            f"Unknown task selection: {set(args.journey) - {task.name for task in tasks}}"
        )
    if not tasks:
        raise ValueError("No tasks selected; replay defaults to non-passing tasks")
    if len({task.name for task in tasks}) != len(tasks):
        raise ValueError("Task names must be unique")
    for task in tasks:
        if not task.command or task.cpu < 1 or task.memory_mib < 1 or task.timeout <= 0:
            raise ValueError(f"Invalid task resource/command contract: {task.name}")
        if task.memory_mib > args.memory_mib and not task.exclusive:
            raise ValueError(
                f"{task.name} requests {task.memory_mib} MiB; budget is {args.memory_mib} MiB"
            )
    tasks = [
        replace(task, cpu=args.cpus, memory_mib=args.memory_mib)
        if task.exclusive
        else replace(task, cpu=min(task.cpu, args.cpus))
        for task in tasks
    ]
    return tasks, binary.resolve(strict=True) if binary is not None else None


async def prepare(
    binary: Path | None,
    work: Path,
    scope: Scope,
    store: Store,
    stop: asyncio.Event,
    report: dict[str, Any],
    cpus: int,
) -> Path:
    if binary is None:
        binary = await build_image(work, scope, store, stop, report, cpus)
    store.event("image.freeze.started", {"original": str(binary)})
    started: int = time.perf_counter_ns()
    frozen, manifest = freeze(binary, work / "image")
    report.update(
        binary=str(frozen), original_binary=str(binary), payload_sha256=manifest
    )
    store.event(
        "image.freeze.completed",
        {
            "elapsed_ns": time.perf_counter_ns() - started,
            "files": len(manifest),
            "sha256": manifest,
        },
    )
    return frozen


async def build_image(
    work: Path,
    scope: Scope,
    store: Store,
    stop: asyncio.Event,
    report: dict[str, Any],
    cpus: int,
) -> Path:
    builder: Task = build.command(cpus)
    store.register([builder], start=-1)
    store.event("build.incremental.started", {"directory": str(build.BASE)})
    outcome: dict[str, Any] = await execute(
        -1, builder, ROOT / "msync", work / "build", scope, store, stop
    )
    report["build"] = outcome
    if outcome["status"] != "passed":
        raise RuntimeError(
            f"Product build {outcome['status']}; see {outcome['stderr_path']}"
        )
    store.event("build.incremental.completed", {"directory": str(build.BASE)})
    return build.BASE / "bin/Msync.Cli/debug/msync"


async def run(args: argparse.Namespace, tasks: list[Task], binary: Path | None) -> int:
    work: Path = args.work
    work.mkdir(parents=True, exist_ok=True)
    with (work / ".run-owner").open("x", encoding="utf-8") as owner:
        owner.write(f"pid={os.getpid()}\n")
    scope: Scope = Scope(args.cpus, args.memory_mib)
    stop: asyncio.Event = asyncio.Event()
    loop: asyncio.AbstractEventLoop = asyncio.get_running_loop()
    report: dict[str, Any] = {
        "schema": 1,
        "status": "running",
        "started_unix_ns": time.time_ns(),
        "scope": scope.metadata,
        "work": str(work),
        "tasks": [],
        "jobs": args.jobs,
        "harness_sha256": {
            str(path.relative_to(ROOT)): digest(path)
            for path in [
                Path(__file__).resolve(),
                *(ROOT / "scripts/testing").glob("*.py"),
            ]
        },
        "selection": [task.describe() for task in tasks],
        "environment": {
            "python": sys.version,
            "platform": sys.platform,
            "jit": "auto unless explicitly overridden by task",
        },
        "measurement": "Functional task execution and resource accounting, not product latency or throughput. Benchmark tasks reserve the suite exclusively.",
    }

    def signal_stop(signum: signal.Signals) -> None:
        report["cancel_signal"] = signum.name
        stop.set()

    for signum in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(signum, signal_stop, signum)
    store: Store = Store(work / "results.sqlite", report)
    store.register(tasks)
    checkpoint(work, report)
    try:
        with Lease() as lease:
            store.event("resource.lease.wait", {})
            admitted: bool = await lease.acquire(stop)
            if not admitted:
                raise InterruptedError(
                    "Cancelled while waiting for the repository resource lease"
                )
            compiler.configure(args.cpus, args.memory_mib)
            report["shared_resources_before"] = scope.snapshot(scope.path.parent)
            store.event(
                "resource.lease.acquired", {"shared_budget": str(scope.path.parent)}
            )
            frozen: Path = await prepare(
                binary, work, scope, store, stop, report, args.cpus
            )
            store.update("running", report)
            checkpoint(work, report)
            report["tasks"] = await schedule(
                tasks,
                frozen,
                work,
                scope,
                store,
                stop,
                args.cpus,
                args.memory_mib,
                args.jobs,
                args.fail_fast,
            )
            report["status"] = (
                "cancelled"
                if report.get("cancel_signal")
                else (
                    "passed"
                    if all(row["status"] == "passed" for row in report["tasks"])
                    else "failed"
                )
            )
    except InterruptedError as error:
        report.update(status="cancelled", error=str(error))
    except Exception as error:  # noqa: BLE001 -- infrastructure failures need a durable run receipt
        report.update(
            status="cancelled" if report.get("cancel_signal") else "infra_error",
            error=f"{type(error).__name__}: {error}",
        )
        report["error_chain"] = traceback.format_exception(error)
        store.event(
            "run.failed", {"error": report["error"], "chain": report["error_chain"]}
        )
        print(report["error"], file=sys.stderr)
    finally:
        report.update(
            finished_unix_ns=time.time_ns(),
            resources=scope.snapshot(),
            shared_resources=scope.snapshot(scope.path.parent),
        )
        for number, task in enumerate(tasks):
            store.db.execute(
                "UPDATE tasks SET status='cancelled',finished_ns=?,error='Run ended before admission' WHERE id=? AND status='queued'",
                (time.perf_counter_ns(), number),
            )
        store.db.commit()
        store.update(report["status"], report)
        store.close()
        checkpoint(work, report)
        print(work / "results.sqlite", flush=True)
    return (
        130
        if report["status"] == "cancelled"
        else (0 if report["status"] == "passed" else 1)
    )


def main() -> int:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--binary",
        type=Path,
        help="Existing apphost; otherwise use the shared incremental MSBuild tree",
    )
    parser.add_argument(
        "--journey", "--task", dest="journey", action="append", default=[]
    )
    parser.add_argument(
        "--plan",
        type=Path,
        help="JSON task descriptions using argv, not shell commands",
    )
    parser.add_argument(
        "--replay",
        type=Path,
        help="Replay non-passing tasks from a previous results.sqlite",
    )
    parser.add_argument("--work", type=Path)
    parser.add_argument("--cpus", type=positive, default=12)
    parser.add_argument("--memory-mib", type=positive, default=24 * 1024)
    parser.add_argument("--jobs", type=positive, default=12)
    parser.add_argument(
        "--fail-fast",
        action="store_true",
        help="Cancel owned active and queued tasks after the first non-passing outcome",
    )
    parser.add_argument(
        "--project",
        action="append",
        type=Path,
        default=[],
        help="C# executable contract project supporting FrozenAssemblies (managed image required)",
    )
    parser.add_argument(
        "--prepare-only",
        action="store_true",
        help="Build or freeze the image without running journeys",
    )
    parser.add_argument("--list", action="store_true")
    args: argparse.Namespace = parser.parse_args()
    if args.list:
        for task in catalog().values():
            print(
                f"{task.name:15} cpu={task.cpu:2} memory={task.memory_mib:5} MiB exclusive={task.exclusive}"
            )
        return 0
    try:
        if args.prepare_only and (
            args.plan or args.replay or args.journey or args.project
        ):
            raise ValueError("--prepare-only does not select test tasks")
        if args.plan is not None and args.replay is not None:
            raise ValueError("--plan and --replay are mutually exclusive")
        if args.cpus > 12 or args.memory_mib > 24 * 1024:
            raise ValueError("The shared budget cannot exceed 12 CPUs / 24 GiB")
        if sys.platform != "linux":
            raise ValueError(
                "Bounded concurrent execution requires Linux cgroup v2 and a systemd user manager"
            )
        args.cpus = min(args.cpus, len(os.sched_getaffinity(0)))
        args.work = (
            args.work or ROOT / ".temp/test-execution" / uuid.uuid4().hex
        ).resolve()
        if not args.work.is_relative_to(ROOT / ".temp") or args.work == ROOT / ".temp":
            raise ValueError("--work must be a child of repository-root .temp")
        if args.work.exists() and any(args.work.iterdir()):
            raise ValueError("--work must be new or empty")
        tasks, binary = selection(args)
        scoped: int | None = launch_scope(args.cpus, args.memory_mib)
        if scoped is not None:
            return scoped
        return asyncio.run(run(args, tasks, binary))
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.error(str(error))
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
