"""Deterministic native WSL interruption checks for the owned fixture endpoint."""

from __future__ import annotations

import importlib
import json
import os
import selectors
import socket
import subprocess
import time
import uuid
from collections.abc import Callable, Iterator
from pathlib import Path
from typing import Protocol, cast


class CommandLike(Protocol):
    @property
    def returncode(self) -> int | None: ...
    @property
    def stdout(self) -> str: ...
    @property
    def stderr(self) -> str: ...


class RunnerLike(Protocol):
    def run(
        self,
        arguments: list[str],
        *,
        directory: Path | None = None,
        environment: dict[str, str] | None = None,
        check: bool = True,
        timeout: int = 120,
    ) -> CommandLike: ...


class PtyLike(Protocol):
    fileobj: socket.socket

    @property
    def exitstatus(self) -> int | None: ...
    def read(self, size: int = 1024) -> str: ...
    def write(self, value: str) -> int: ...
    def isalive(self) -> bool: ...
    def close(self, force: bool = False) -> None: ...


class PtyFactory(Protocol):
    def spawn(
        self,
        argv: list[str],
        *,
        cwd: str,
        env: dict[str, str],
        dimensions: tuple[int, int],
        backend: str,
    ) -> PtyLike: ...


SETUP = """set -eu
control=$HOME/.codex/control/$1
mkdir -p "$control"
mkfifo "$control/ready" "$control/gate"
cat > "$control/checkpoint" <<'SCRIPT'
#!/bin/sh
set -eu
here=${0%/*}
if mkdir "$here/entered" 2>/dev/null; then
    printf 'ready\\n' > "$here/ready"
    IFS= read -r release < "$here/gate"
fi
SCRIPT
chmod 755 "$control/checkpoint"
printf '%s' "$control"
"""


def shell(wsl: str, distro: str, program: str, *arguments: str) -> list[str]:
    return [
        wsl,
        "--distribution",
        distro,
        "--exec",
        "sh",
        "-c",
        program,
        "sh",
        *arguments,
    ]


def setup(
    runner: RunnerLike,
    wsl: str,
    distro: str,
    name: str,
    environment: dict[str, str],
) -> tuple[str, dict[str, str]]:
    result = runner.run(shell(wsl, distro, SETUP, name))
    control = result.stdout
    invocation = dict(environment)
    invocation["TAR_OPTIONS"] = (
        f"--checkpoint=8 --checkpoint-action=exec={control}/checkpoint"
    )
    inherited = invocation.get("WSLENV", "")
    invocation["WSLENV"] = (inherited + ":" if inherited else "") + "TAR_OPTIONS/u"
    forwarded = runner.run(
        shell(wsl, distro, "printf '%s' \"$TAR_OPTIONS\""),
        environment=invocation,
    )
    assert forwarded.stdout == invocation["TAR_OPTIONS"], forwarded.stdout
    return control, invocation


def wait_checkpoint(runner: RunnerLike, wsl: str, distro: str, control: str) -> None:
    result = runner.run(
        shell(
            wsl, distro, 'IFS= read -r ready < "$1/ready"; printf %s "$ready"', control
        ),
        timeout=30,
    )
    assert result.stdout == "ready", result.stdout


def progress(
    directory: Path, alive: Callable[[], bool], pump: Callable[[], None]
) -> tuple[str, int]:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        pump()
        for stage in directory.glob(".msync-*"):
            if stage.is_file() and stage.stat().st_size > 0:
                return stage.name, stage.stat().st_size
        if not alive():
            raise AssertionError(
                "Native transfer exited before writing positive staged bytes"
            )
    raise AssertionError(
        "Native transfer did not expose positive staged bytes at the checkpoint"
    )


def preserved(directory: Path, previous: bytes) -> None:
    assert (directory / "target").read_bytes() == previous
    assert not list(directory.glob(".msync-*")), (
        "Native owner retained an ordinary file stage"
    )


def release(runner: RunnerLike, wsl: str, distro: str, control: str) -> None:
    runner.run(
        shell(
            wsl,
            distro,
            'timeout 2 sh -c \'printf "release\\n" > "$1/gate"\' sh "$1"',
            control,
        ),
        check=False,
        timeout=5,
    )


def cancellation(
    runner: RunnerLike,
    binary: Path,
    wsl: str,
    distro: str,
    directory: Path,
    environment: dict[str, str],
) -> dict[str, object]:
    name = "cancel-" + uuid.uuid4().hex
    control, invocation = setup(runner, wsl, distro, name, environment)
    previous = b"previous cancellation target"
    (directory / "target").write_bytes(previous)
    script = directory / "cancel.msync"
    script.write_text(
        f"let remote = wsl://{distro}; ~/.codex/large.bin @ remote -> ./target;\n",
        encoding="utf-8",
    )
    factory = cast(PtyFactory, importlib.import_module("winpty").PtyProcess)
    process = factory.spawn(
        [str(binary), "--no-config", "run", str(script)],
        cwd=str(directory),
        env=invocation,
        dimensions=(30, 120),
        backend="0",
    )
    raw: list[str] = []
    with selectors.DefaultSelector() as selector:
        selector.register(process.fileobj, selectors.EVENT_READ)

        def pump() -> None:
            if selector.select(0):
                try:
                    raw.append(process.read(65536))
                except EOFError:
                    pass

        try:
            wait_checkpoint(runner, wsl, distro, control)
            stage, count = progress(directory, process.isalive, pump)
            process.write("\x03")
            deadline = time.monotonic() + 30
            while process.isalive() and time.monotonic() < deadline:
                if selector.select(0.1):
                    pump()
            assert not process.isalive(), "Ctrl-C did not finish the native transfer"
            pump()
            assert process.exitstatus not in (None, 0), (process.exitstatus, raw)
            preserved(directory, previous)
            return {
                "case": "positive-progress-cancel",
                "stage": stage,
                "progress_bytes": count,
                "exit": process.exitstatus,
                "old_target_preserved": True,
                "stage_retired": True,
                "console": "".join(raw),
            }
        finally:
            release(runner, wsl, distro, control)
            (directory / "console.txt").write_text("".join(raw), encoding="utf-8")
            process.close(force=process.isalive())


def endpoint_failure(
    runner: RunnerLike,
    binary: Path,
    wsl: str,
    distro: str,
    directory: Path,
    environment: dict[str, str],
) -> dict[str, object]:
    name = "disconnect-" + uuid.uuid4().hex
    control, invocation = setup(runner, wsl, distro, name, environment)
    previous = b"previous disconnected target"
    (directory / "target").write_bytes(previous)
    script = directory / "disconnect.msync"
    script.write_text(
        f"let remote = wsl://{distro}; ~/.codex/large.bin @ remote -> ./target;\n",
        encoding="utf-8",
    )
    process = subprocess.Popen(
        [str(binary), "--no-config", "run", str(script)],
        cwd=directory,
        env=invocation,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        wait_checkpoint(runner, wsl, distro, control)
        stage, count = progress(directory, lambda: process.poll() is None, lambda: None)
        runner.run([wsl, "--terminate", distro])
        stdout, stderr = process.communicate(timeout=30)
        (directory / "stdout.log").write_bytes(stdout)
        (directory / "stderr.log").write_bytes(stderr)
        assert process.returncode != 0, (stdout, stderr)
        preserved(directory, previous)
        return {
            "case": "positive-progress-endpoint-failure",
            "stage": stage,
            "progress_bytes": count,
            "exit": process.returncode,
            "old_target_preserved": True,
            "stage_retired": True,
            "stdout": stdout.decode("utf-8", errors="replace"),
            "stderr": stderr.decode("utf-8", errors="replace"),
        }
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()


def run_control_journeys(
    runner: RunnerLike,
    binary: Path,
    wsl: str,
    distro: str,
    workspace: Path,
    environment: dict[str, str],
) -> Iterator[dict[str, object]]:
    assert os.name == "nt"
    controls = workspace / "diagnostics/controls"
    controls.mkdir(parents=True, exist_ok=True)
    for name, operation in (("cancel", cancellation), ("disconnect", endpoint_failure)):
        directory = controls / name
        directory.mkdir()
        result = operation(runner, binary, wsl, distro, directory, environment)
        (directory / "result.json").write_text(
            json.dumps(result, indent=2) + "\n", encoding="utf-8"
        )
        yield result
    sibling = runner.run(shell(wsl, distro, 'cat "$HOME/.codex/sibling"'))
    assert sibling.stdout == "keep remote sibling", sibling.stdout
