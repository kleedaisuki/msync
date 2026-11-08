#!/usr/bin/env python3
"""Exercise a native Windows console using PyWinPTY's explicit ConPTY backend."""

from __future__ import annotations

import argparse
import hashlib
import importlib
import importlib.metadata
import json
import os
import platform
import re
import selectors
import socket
import statistics
import sys
import time
from collections.abc import Callable
from pathlib import Path
from typing import Protocol, cast

ROOT = Path(__file__).resolve().parents[2]


class PtyProcess(Protocol):
    fileobj: socket.socket

    @property
    def pid(self) -> int: ...

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
        backend: int | str,
    ) -> PtyProcess: ...


class Terminal:
    def __init__(self, binary: Path, directory: Path, *, color: bool = False) -> None:
        factory = cast(PtyFactory, importlib.import_module("winpty").PtyProcess)
        home = directory / "home"
        home.mkdir(exist_ok=True)
        environment = dict(os.environ)
        environment.update(
            HOME=str(home),
            USERPROFILE=str(home),
            APPDATA=str(home / ".config"),
            XDG_CONFIG_HOME=str(home / ".config"),
            TERM="xterm-256color",
            NO_COLOR="1",
        )
        if color:
            environment.pop("NO_COLOR", None)
        self.process = factory.spawn(
            [str(binary), "--no-config", "repl"],
            cwd=str(directory),
            env=environment,
            dimensions=(30, 120),
            # The upstream selector treats integer zero as unset before int().
            backend="0",
        )
        self.selector = selectors.DefaultSelector()
        self.selector.register(self.process.fileobj, selectors.EVENT_READ)
        self.raw = ""
        self.plain = ""
        self.escape = ""
        self.row = 1
        self.column = 1

    def send(self, value: str) -> None:
        self.process.write(value)

    def read(self, timeout: float) -> bool:
        if not self.selector.select(timeout):
            return False
        try:
            chunk = self.process.read(65536)
        except EOFError:
            return False
        self.raw += chunk
        for character in chunk:
            self.consume(character)
        return bool(chunk)

    def consume(self, character: str) -> None:
        if self.escape:
            self.escape += character
            if self.escape.startswith("\x1b["):
                if len(self.escape) > 2 and "@" <= character <= "~":
                    self.csi(self.escape[2:-1], character)
                    self.escape = ""
            elif self.escape.startswith("\x1b]"):
                if character == "\x07" or self.escape.endswith("\x1b\\"):
                    self.escape = ""
            elif len(self.escape) == 2:
                self.escape = ""
            return
        if character == "\x1b":
            self.escape = character
            return
        self.plain += character
        if character == "\r":
            self.column = 1
        elif character == "\n":
            self.row = min(30, self.row + 1)
        elif character == "\b":
            self.column = max(1, self.column - 1)
        elif character >= " ":
            self.column += 1
            if self.column > 120:
                self.column = 1
                self.row = min(30, self.row + 1)

    def csi(self, parameters: str, final: str) -> None:
        if parameters == "6" and final == "n":
            self.send(f"\x1b[{self.row};{self.column}R")
            return
        if parameters.startswith(("?", ">")):
            return
        if final not in "ABCDHfG":
            return
        values = [int(part or "1") for part in parameters.split(";")]
        count = values[0]
        if final == "A":
            self.row = max(1, self.row - count)
        elif final == "B":
            self.row = min(30, self.row + count)
        elif final == "C":
            self.column = min(120, self.column + count)
        elif final == "D":
            self.column = max(1, self.column - count)
        elif final in {"H", "f"}:
            self.row = values[0]
            self.column = values[1] if len(values) > 1 else 1
        elif final == "G":
            self.column = values[0]

    def until(self, predicate: Callable[[], bool], description: str) -> None:
        deadline = time.monotonic() + 20
        while not predicate():
            if not self.process.isalive():
                self.read(0)
                raise AssertionError(
                    f"Console exited {self.process.exitstatus}: {description}\n{self.plain}"
                )
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError(f"Console stalled: {description}\n{self.plain}")
            self.read(min(remaining, 0.1))

    def key(self, value: str) -> None:
        start = len(self.raw)
        text_start = len(self.plain)
        self.send(value)
        if all(character >= " " and character != "\x7f" for character in value):
            # Wait for the complete paint before sending Enter. Otherwise the
            # reader can legitimately batch the tail and Enter as pasted text.
            self.until(
                lambda: (
                    value in self.plain[text_start:].replace("\r", "").replace("\n", "")
                ),
                f"render text {value!r}",
            )
        else:
            self.until(lambda: len(self.raw) > start, f"render key {value!r}")

    def line(self, value: str, start: int = 0) -> int:
        expected = re.compile(r"\r?\n" + re.escape(value) + r"\r?\n")
        self.until(
            lambda: expected.search(self.plain, start) is not None,
            f"output line {value!r}",
        )
        match = expected.search(self.plain, start)
        assert match is not None
        return match.end()

    def prompt(self, start: int) -> None:
        self.until(lambda: "> " in self.plain[start:], "next prompt")

    def evaluate(self, source: str, expected: str) -> float:
        start = len(self.plain)
        self.key(source)
        began = time.perf_counter_ns()
        self.send("\r")
        boundary = self.line(expected, start)
        elapsed = (time.perf_counter_ns() - began) / 1_000_000
        self.prompt(boundary)
        return elapsed

    def exit(self) -> None:
        deadline = time.monotonic() + 10
        while self.process.isalive():
            if time.monotonic() >= deadline:
                raise AssertionError(f"Console did not exit after EOF\n{self.plain}")
            self.read(0.05)
        assert self.process.exitstatus == 0, self.process.exitstatus

    def finish(self, log: Path) -> None:
        try:
            if self.process.isalive():
                self.process.close(force=True)
        finally:
            log.write_text(self.raw, encoding="utf-8")
            self.selector.close()
            self.process.fileobj.close()


def executable(path: Path) -> str:
    return '$local::"' + str(path).replace('"', '""') + '"'


def run(binary: Path, directory: Path, phases: list[str]) -> dict[str, object]:
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "completion input.txt").write_text("PATH OK", encoding="utf-8")
    began = time.perf_counter_ns()
    terminal = Terminal(binary, directory)
    latencies: list[float] = []
    try:
        terminal.until(lambda: "> " in terminal.plain, "first prompt")
        first_prompt_ms = (time.perf_counter_ns() - began) / 1_000_000
        terminal.key('"EDIT_BAD"')
        terminal.key("\x1b[D")
        for _ in range(3):
            # ConPTY uses DEL for Backspace; BS selects Ctrl+Backspace.
            terminal.key("\x7f")
        terminal.key("GOOD")
        terminal.key("\x1b[F")
        terminal.send("\r")
        terminal.prompt(terminal.line("EDIT_GOOD"))
        phases.append("left/backspace/end editing")

        terminal.evaluate('"HISTORY_OK"', "HISTORY_OK")
        start = len(terminal.plain)
        terminal.key("\x1b[A")
        terminal.send("\r")
        terminal.prompt(terminal.line("HISTORY_OK", start))
        phases.append("up-arrow history")

        terminal.key("./comp")
        terminal.key("\t")
        terminal.key("\t")
        terminal.key(' -> ./completed; "PATH_DONE"')
        terminal.send("\r")
        terminal.prompt(terminal.line("PATH_DONE"))
        assert (directory / "completed").read_text() == "PATH OK"
        phases.append("Tab path completion and real transfer")

        terminal.key('"ABANDONED')
        cancelled = len(terminal.plain)
        terminal.key("\x03")
        terminal.prompt(cancelled)
        terminal.evaluate('"AFTER_CANCEL"', "AFTER_CANCEL")
        phases.append("Ctrl-C input cancellation")

        terminal.key("fn terminal_fn() { // } is only a comment")
        terminal.key("\r")
        terminal.key('"MULTILINE_OK"')
        terminal.key("\r")
        terminal.key("};")
        terminal.send("\r")
        terminal.until(lambda: "> " in terminal.plain[-80:], "multiline submission")
        terminal.evaluate("terminal_fn()", "MULTILINE_OK")
        phases.append("multiline braces and comments")

        terminal.key('"first')
        terminal.key("\r")
        terminal.key('second" -> multiline.txt;')
        submitted = len(terminal.plain)
        terminal.send("\r")
        literal = directory / "multiline.txt"
        terminal.until(literal.exists, "interactive multiline String publication")
        terminal.prompt(submitted)
        assert literal.read_bytes() == b"first\nsecond", literal.read_bytes()
        terminal.evaluate("terminal_fn()", "MULTILINE_OK")
        phases.append("ordinary Enter inserts LF String bytes and preserves session")

        (directory / "read-foreground.py").write_text(
            "from pathlib import Path\n"
            "Path('child-ready').write_text('ready')\n"
            "print('OWNED ' + input())\n",
            encoding="utf-8",
        )
        python = executable(Path(sys.executable))
        terminal.key(
            f'let child = {python}("./read-foreground.py"); '
            'child::stdout -> ./child-output; "CHILD_DONE"'
        )
        terminal.send("\r")
        terminal.until(
            lambda: (directory / "child-ready").exists(), "native child owns stdin"
        )
        terminal.send("NATIVE_STDIN\r")
        terminal.prompt(terminal.line("CHILD_DONE"))
        assert (directory / "child-output").read_bytes() == b"OWNED NATIVE_STDIN\r\n"
        phases.append("native child foreground input ownership")

        (directory / "wait-foreground.py").write_text(
            "from pathlib import Path\nimport time\n"
            "Path('work-ready').write_text('ready')\ntime.sleep(60)\n",
            encoding="utf-8",
        )
        terminal.key(f'let waiting = {python}("./wait-foreground.py");')
        terminal.send("\r")
        terminal.until(
            lambda: (directory / "work-ready").exists(), "native child started work"
        )
        start = len(terminal.plain)
        terminal.send("\x03")
        terminal.until(
            lambda: "operation cancelled" in terminal.plain[start:],
            "Ctrl-C cancels active work",
        )
        terminal.prompt(start)
        terminal.evaluate("terminal_fn()", "MULTILINE_OK")
        phases.append("Ctrl-C active child and persistent session")

        for index in range(5):
            marker = f"LATENCY_{index}"
            latencies.append(terminal.evaluate(f'"{marker}"', marker))
        terminal.send("\x04")
        terminal.exit()
        phases.append("Ctrl-D EOF")
    finally:
        terminal.finish(directory / "terminal.log")

    assert len(list((directory / "home").rglob("history-v08"))) == 1
    restored = Terminal(binary, directory)
    try:
        restored.until(lambda: "> " in restored.plain, "restarted prompt")
        restored.key("\x1b[A")
        restored.send("\r")
        restored.prompt(restored.line("LATENCY_4"))
        restored.send("\x04")
        restored.exit()
        phases.append("persisted history after restart")
    finally:
        restored.finish(directory / "restored-terminal.log")
    return {
        "first_prompt_ms": first_prompt_ms,
        "submission_ms": latencies,
        "submission_median_ms": statistics.median(latencies),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".temp/windows-console/results.json"
    )
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("This journey requires an actual native Windows ConPTY host.")
    binary = args.binary.resolve()
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    directory = output.parent / f"windows-console-{time.time_ns()}"
    phases: list[str] = []
    report: dict[str, object] = {
        "binary": str(binary),
        "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "platform": platform.platform(),
        "backend": "PyWinPTY ConPTY (backend=0)",
        "pywinpty": importlib.metadata.version("pywinpty"),
        "python": platform.python_version(),
        "image_os": os.environ.get("ImageOS"),
        "image_version": os.environ.get("ImageVersion"),
        "workspace": str(directory),
        "phases": phases,
        "passed": False,
    }
    try:
        report.update(run(binary, directory, phases))
        report["passed"] = True
        print(
            f"PASS: {len(phases)} actual Windows ConPTY editing/ownership/EOF journeys"
        )
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
