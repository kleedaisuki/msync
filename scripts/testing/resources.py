"""One repository-wide admission owner and kernel-enforced process-tree budgets."""

from __future__ import annotations

import asyncio
import fcntl
import os
import signal
import subprocess
import sys
from pathlib import Path
from types import FrameType, TracebackType
from typing import IO, Any, Self

from . import compiler
from .model import ROOT


class Scope:
    def __init__(self, cpus: int, memory_mib: int) -> None:
        group: str = next(
            line[3:]
            for line in Path("/proc/self/cgroup").read_text().splitlines()
            if line.startswith("0::")
        )
        self.path: Path = Path("/sys/fs/cgroup") / group.lstrip("/")
        maximum: str = (self.path / "memory.max").read_text().strip()
        quota, period = (self.path / "cpu.max").read_text().split()
        if (
            maximum == "max"
            or int(maximum) > memory_mib * 1024**2
            or quota == "max"
            or int(quota) > cpus * int(period)
        ):
            raise RuntimeError(f"Test resource scope not applied: {self.path}")
        controller: Path = self.path / "controller"
        controller.mkdir()
        (controller / "cgroup.procs").write_text(str(os.getpid()), encoding="ascii")
        (self.path / "cgroup.subtree_control").write_text(
            "+cpu +memory", encoding="ascii"
        )
        self.metadata: dict[str, Any] = {
            "path": str(self.path),
            "cpu_quota": int(quota),
            "cpu_period": int(period),
            "memory_max_bytes": int(maximum),
            "affinity": sorted(os.sched_getaffinity(0)),
            "shared_budget": str(self.path.parent),
        }

    def task(self, number: int) -> Path:
        path: Path = self.path / f"task-{number}"
        path.mkdir()
        (path / "memory.max").write_text(
            str(self.metadata["memory_max_bytes"]), encoding="ascii"
        )
        (path / "cpu.max").write_text(
            f"{self.metadata['cpu_quota']} {self.metadata['cpu_period']}",
            encoding="ascii",
        )
        return path

    def snapshot(self, path: Path | None = None) -> dict[str, str]:
        directory: Path = path or self.path
        return {
            name: (directory / name).read_text().strip()
            for name in ("memory.peak", "memory.events", "cpu.stat")
        }

    def processes(self, path: Path) -> list[int]:
        return [
            int(pid)
            for file in path.rglob("cgroup.procs")
            for pid in file.read_text().split()
        ]

    async def reap(self, path: Path) -> dict[str, str]:
        (path / "cgroup.kill").write_text("1", encoding="ascii")
        while "populated 1" in (path / "cgroup.events").read_text():
            await asyncio.sleep(0.01)
        metrics: dict[str, str] = self.snapshot(path)
        for child in sorted(path.rglob("*"), reverse=True):
            if child.is_dir():
                child.rmdir()
        path.rmdir()
        return metrics


def launch_scope(cpus: int, memory_mib: int) -> int | None:
    if sys.platform != "linux":
        raise RuntimeError(
            "Bounded concurrent test execution requires Linux cgroup v2 and a systemd user manager"
        )
    os.sched_setaffinity(0, sorted(os.sched_getaffinity(0))[:cpus])
    if os.environ.get("MSYNC_TEST_SCOPE") == "1":
        return None
    compiler.initialize()
    environment: dict[str, str] = dict(
        os.environ,
        MSYNC_TEST_SCOPE="1",
        TMPDIR=str(compiler.TEMP),
        TEMP=str(compiler.TEMP),
        TMP=str(compiler.TEMP),
    )
    process: subprocess.Popen[bytes] = subprocess.Popen(
        [
            "systemd-run",
            "--user",
            "--scope",
            "--quiet",
            "--collect",
            "--slice",
            compiler.SLICE,
            "-p",
            "Delegate=yes",
            "-p",
            f"MemoryMax={memory_mib * 1024**2}",
            "-p",
            "MemorySwapMax=0",
            "-p",
            f"CPUQuota={cpus * 100}%",
            sys.executable,
            str(ROOT / "scripts/test.py"),
            *sys.argv[1:],
        ],
        env=environment,
        start_new_session=True,
    )

    def forward(signum: int, frame: FrameType | None) -> None:
        try:
            os.killpg(process.pid, signum)
        except ProcessLookupError:
            pass

    previous: dict[signal.Signals, Any] = {
        kind: signal.signal(kind, forward) for kind in (signal.SIGINT, signal.SIGTERM)
    }
    try:
        return process.wait()
    finally:
        for kind, handler in previous.items():
            signal.signal(kind, handler)


class Lease:
    def __init__(self) -> None:
        directory: Path = ROOT / ".temp/test-execution"
        directory.mkdir(parents=True, exist_ok=True)
        self.file: IO[str] = (directory / "resource.lock").open("a+", encoding="utf-8")

    async def acquire(self, stop: asyncio.Event) -> bool:
        while not stop.is_set():
            try:
                fcntl.flock(self.file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                self.file.seek(0)
                self.file.truncate()
                self.file.write(f"pid={os.getpid()}\n")
                self.file.flush()
                return True
            except BlockingIOError:
                await asyncio.sleep(0.1)
        return False

    def __enter__(self) -> Self:
        return self

    def __exit__(
        self,
        kind: type[BaseException] | None,
        value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        self.file.close()
