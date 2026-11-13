"""Reuse the SDK compiler server as a supervised peer inside the shared budget."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import time
from pathlib import Path
from typing import Any

from .model import ROOT

SLICE: str = "msync-tests.slice"
TEMP: Path = ROOT / ".temp/test-execution/tmp"


def properties(unit: str) -> dict[str, str]:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [
            "systemctl",
            "--user",
            "show",
            unit,
            "-p",
            "MainPID",
            "-p",
            "ControlGroup",
            "-p",
            "MemoryMax",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode and not result.stdout:
        raise RuntimeError(result.stderr)
    return dict(
        line.split("=", 1) for line in result.stdout.splitlines() if "=" in line
    )


def configure(cpus: int, memory_mib: int) -> None:
    subprocess.run(
        [
            "systemctl",
            "--user",
            "set-property",
            "--runtime",
            SLICE,
            f"CPUQuota={cpus * 100}%",
            f"MemoryMax={memory_mib * 1024**2}",
            "MemorySwapMax=0",
        ],
        check=True,
    )


def initialize() -> None:
    if properties(SLICE).get("MemoryMax") == "infinity":
        configure(12, 24 * 1024)
    TEMP.mkdir(parents=True, exist_ok=True)


def version() -> str:
    return str(
        json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    )


def pipe() -> str:
    return f"msync-tests-{os.getuid()}-{version()}"


def unit() -> str:
    return "msync-tests-roslyn-" + version() + ".service"


def snapshot() -> dict[str, Any]:
    state: dict[str, str] = properties(unit())
    path: str = state.get("ControlGroup", "")
    return {
        "unit": unit(),
        "pid": int(state.get("MainPID", "0")),
        "pipe": pipe(),
        "cgroup": path,
        "resources": {
            name: (Path("/sys/fs/cgroup") / path.lstrip("/") / name).read_text().strip()
            for name in ("cpu.stat", "memory.peak", "memory.current")
        }
        if path
        else {},
    }


def ensure() -> dict[str, Any]:
    TEMP.mkdir(parents=True, exist_ok=True)
    state: dict[str, str] = properties(unit())
    reused: bool = int(state.get("MainPID", "0")) != 0
    if not reused:
        executable: str | None = shutil.which("dotnet")
        if executable is None:
            raise FileNotFoundError("dotnet is not on PATH")
        host: Path = Path(executable).resolve()
        server: Path = (
            host.parent / "sdk" / version() / "Roslyn/bincore/VBCSCompiler.dll"
        )
        log: Path = ROOT / "artifacts/test-execution/compiler" / version()
        log.mkdir(parents=True, exist_ok=True)
        subprocess.run(
            [
                "systemd-run",
                "--user",
                "--quiet",
                "--collect",
                "--unit",
                unit(),
                "--slice",
                SLICE,
                "-p",
                "Type=exec",
                "-p",
                f"StandardOutput=append:{log / 'server.log'}",
                "-p",
                f"StandardError=append:{log / 'server.log'}",
                "--setenv",
                f"TMPDIR={TEMP}",
                "--setenv",
                f"TEMP={TEMP}",
                "--setenv",
                f"TMP={TEMP}",
                "--setenv",
                f"RoslynCommandLineLogFile={log / 'roslyn.log'}",
                str(host),
                str(server),
                f"-pipename:{pipe()}",
            ],
            check=True,
        )
    # Roslyn NamedPipeUtil deliberately uses /tmp on Unix, independent of TMPDIR.
    socket: Path = Path("/tmp") / pipe()
    deadline: float = time.monotonic() + 10
    while not socket.exists():
        if (
            time.monotonic() >= deadline
            or int(properties(unit()).get("MainPID", "0")) == 0
        ):
            raise RuntimeError(
                f"Compiler server {unit()} did not open {socket}; inspect artifacts/test-execution/compiler/{version()}/server.log"
            )
        time.sleep(0.02)
    return snapshot() | {"reused": reused}


def environment(log: Path) -> dict[str, str]:
    return {
        "UseSharedCompilation": "true",
        "SharedCompilationId": pipe(),
        "RoslynCommandLineLogFile": str(log),
    }


def stop() -> None:
    subprocess.run(["systemctl", "--user", "stop", unit()], check=True)


def park(parent: Path) -> list[Path]:
    paused: list[Path] = []
    try:
        for path in parent.glob("msync-tests-roslyn-*.service"):
            (path / "cgroup.freeze").write_text("1", encoding="ascii")
            paused.append(path)
            while "frozen 0" in (path / "cgroup.events").read_text():
                time.sleep(0.01)
    except OSError:
        resume(paused)
        raise
    return paused


def resume(paths: list[Path]) -> None:
    for path in paths:
        (path / "cgroup.freeze").write_text("0", encoding="ascii")
