#!/usr/bin/env python3
"""Exercise actual Linux terminal editing, history and foreground input ownership."""

from __future__ import annotations

import argparse
import codecs
import errno
import fcntl
import hashlib
import json
import math
import os
import pty
import re
import selectors
import signal
import statistics
import struct
import subprocess
import termios
import time
from collections.abc import Callable
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class Terminal:
    def __init__(self, binary: Path, directory: Path) -> None:
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 30, 120, 0, 0))
        environment = dict(os.environ)
        environment.update(
            HOME=str(directory / "home"),
            XDG_CONFIG_HOME=str(directory / "home/.config"),
            TERM="xterm-256color",
            NO_COLOR="1",
        )
        (directory / "home").mkdir(exist_ok=True)

        self.process: subprocess.Popen[bytes] = subprocess.Popen(
            ["setsid", "--ctty", str(binary), "--no-config", "repl"],
            cwd=directory,
            env=environment,
            stdin=slave,
            stdout=slave,
            stderr=slave,
        )
        os.close(slave)
        self.selector = selectors.DefaultSelector()
        self.selector.register(self.master, selectors.EVENT_READ)
        self.raw = bytearray()
        self.plain = ""
        self.escape = ""
        self.row = 1
        self.column = 1
        self.decoder: codecs.IncrementalDecoder = codecs.getincrementaldecoder("utf-8")(
            "replace"
        )

    def send(self, data: bytes) -> None:
        remaining = memoryview(data)
        while remaining:
            remaining = remaining[os.write(self.master, remaining) :]

    def read(self, timeout: float) -> bool:
        if not self.selector.select(timeout):
            return False
        try:
            chunk = os.read(self.master, 65536)
        except OSError as error:
            if error.errno == errno.EIO:
                return False
            raise
        if not chunk:
            return False
        self.raw.extend(chunk)
        for character in self.decoder.decode(chunk):
            self.consume(character)
        return True

    def consume(self, character: str) -> None:
        if self.escape:
            self.escape += character
            if self.escape.startswith("\x1b["):
                if len(self.escape) > 2 and "@" <= character <= "~":
                    self.csi(self.escape[2:-1], character)
                    self.escape = ""
            elif len(self.escape) == 2 and character != "[":
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
            self.send(f"\x1b[{self.row};{self.column}R".encode())
            return
        if parameters.startswith(("?", ">")):
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
        deadline = time.monotonic() + 15
        while not predicate():
            if self.process.poll() is not None:
                self.read(0)
                raise AssertionError(
                    f"Terminal exited {self.process.returncode}: {description}\n{self.plain}"
                )
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError(f"Terminal stalled: {description}\n{self.plain}")
            self.read(min(remaining, 0.1))

    def line(self, value: str, start: int = 0) -> int:
        expected = re.compile(r"\r?\n" + re.escape(value) + r"\r?\n")
        self.until(
            lambda: expected.search(self.plain, start) is not None,
            f"output line {value!r}",
        )
        match = expected.search(self.plain, start)
        assert match is not None
        return match.end()

    def key(self, value: bytes) -> None:
        start = len(self.raw)
        text_start = len(self.plain)
        self.send(value)
        if all(character >= 32 and character != 127 for character in value):
            expected = value.decode()
            self.until(
                lambda: expected in self.plain[text_start:], f"render text {expected!r}"
            )
        else:
            self.until(lambda: len(self.raw) > start, f"render key {value!r}")

    def prompt(self, start: int) -> None:
        self.until(lambda: "> " in self.plain[start:], "next prompt")

    def evaluate(self, source: str, expected: str) -> float:
        start = len(self.plain)
        self.key(source.encode())
        began = time.perf_counter_ns()
        self.send(b"\r")
        boundary = self.line(expected, start)
        elapsed = (time.perf_counter_ns() - began) / 1_000_000
        self.prompt(boundary)
        return elapsed

    def finish(self, log: Path) -> None:
        try:
            if self.process.poll() is None:
                self.send(b"\x04")
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(self.process.pid, signal.SIGKILL)
                    self.process.wait(timeout=5)
        finally:
            log.write_bytes(self.raw)
            self.selector.close()
            os.close(self.master)


def run(binary: Path, directory: Path) -> dict[str, object]:
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "completion input.txt").write_text("PATH OK", encoding="utf-8")
    began = time.perf_counter_ns()
    terminal = Terminal(binary, directory)
    latencies: list[float] = []
    try:
        terminal.until(lambda: "> " in terminal.plain, "first prompt")
        first_prompt_ms = (time.perf_counter_ns() - began) / 1_000_000
        terminal.key(b'"EDIT_BAD"')
        terminal.key(b"\x1b[D")
        for _ in range(3):
            terminal.key(b"\x7f")
        terminal.key(b"GOOD")
        terminal.key(b"\x1b[F")
        terminal.send(b"\r")
        terminal.prompt(terminal.line("EDIT_GOOD"))

        terminal.evaluate('"HISTORY_OK"', "HISTORY_OK")
        start = len(terminal.plain)
        terminal.key(b"\x1b[A")
        terminal.send(b"\r")
        terminal.prompt(terminal.line("HISTORY_OK", start))

        terminal.key(b"./comp")
        terminal.key(b"\t")
        terminal.key(b"\t")
        terminal.key(b' -> ./completed; "PATH_DONE"')
        terminal.send(b"\r")
        terminal.prompt(terminal.line("PATH_DONE"))
        assert (directory / "completed").read_text() == "PATH OK"

        terminal.key(b'"ABANDONED')
        cancelled = len(terminal.plain)
        terminal.key(b"\x03")
        terminal.prompt(cancelled)
        terminal.evaluate('"AFTER_CANCEL"', "AFTER_CANCEL")

        terminal.key(b"fn terminal_fn() { // } is only a comment")
        terminal.key(b"\r")
        terminal.key(b'"MULTILINE_OK"')
        terminal.key(b"\r")
        terminal.key(b"};")
        terminal.send(b"\r")
        terminal.until(lambda: "> " in terminal.plain[-40:], "multiline submission")
        terminal.evaluate("terminal_fn()", "MULTILINE_OK")

        child = directory / "read-foreground.py"
        child.write_text(
            "from pathlib import Path\n"
            "Path('child-ready').write_text('ready')\n"
            "print('OWNED ' + input())\n",
            encoding="utf-8",
        )
        terminal.key(
            b'let child = /usr/bin/python3("./read-foreground.py"); '
            b'child::stdout -> ./child-output; "CHILD_DONE"'
        )
        terminal.send(b"\r")
        terminal.until(
            lambda: (directory / "child-ready").exists(), "native child owns stdin"
        )
        terminal.send(b"NATIVE_STDIN\n")
        terminal.prompt(terminal.line("CHILD_DONE"))
        assert (directory / "child-output").read_text() == "OWNED NATIVE_STDIN\n"

        waiting = directory / "wait-foreground.py"
        waiting.write_text(
            "from pathlib import Path\nimport time\n"
            "Path('work-ready').write_text('ready')\ntime.sleep(60)\n",
            encoding="utf-8",
        )
        terminal.key(b'let waiting = /usr/bin/python3("./wait-foreground.py");')
        terminal.send(b"\r")
        terminal.until(
            lambda: (directory / "work-ready").exists(), "native child started work"
        )
        start = len(terminal.plain)
        terminal.send(b"\x03")
        terminal.until(
            lambda: "operation cancelled" in terminal.plain[start:],
            "Ctrl-C cancels active work",
        )
        terminal.prompt(start)
        terminal.evaluate("terminal_fn()", "MULTILINE_OK")
        for index in range(12):
            marker = f"LATENCY_{index}"
            latencies.append(terminal.evaluate(f'"{marker}"', marker))
        terminal.send(b"\x04")
        terminal.process.wait(timeout=5)
        assert terminal.process.returncode == 0
    finally:
        terminal.finish(directory / "terminal.log")
    histories = list((directory / "home").rglob("history-v08"))
    assert len(histories) == 1, (
        "Interactive history was not persisted in the user's home"
    )
    restored = Terminal(binary, directory)
    try:
        restored.until(lambda: "> " in restored.plain, "restarted prompt")
        restored.key(b"\x1b[A")
        restored.send(b"\r")
        restored.prompt(restored.line("LATENCY_11"))
        restored.send(b"\x04")
        restored.process.wait(timeout=5)
        assert restored.process.returncode == 0
    finally:
        restored.finish(directory / "restored-terminal.log")
    return {
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "journeys": [
            "cursor editing/backspace/end",
            "up-arrow history",
            "path Tab completion",
            "Ctrl-C input recovery",
            "multiline brace/comment handling",
            "native foreground child stdin ownership",
            "Ctrl-C active-work recovery",
            "persistent history after restart",
            "Ctrl-D EOF",
        ],
        "first_prompt_ms": first_prompt_ms,
        "submit_to_result_ms": latencies,
        "median_ms": statistics.median(latencies),
        "p95_ms": sorted(latencies)[math.ceil(len(latencies) * 0.95) - 1],
        "percentile_method": "nearest-rank",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    directory = ROOT / ".temp/terminal-journey" / str(time.time_ns())
    output = args.output or directory / "results.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    try:
        report = run(args.binary.resolve(), directory)
    except (AssertionError, subprocess.TimeoutExpired) as error:
        output.write_text(
            json.dumps({"error": str(error), "fixture": str(directory)}, indent=2)
            + "\n",
            encoding="utf-8",
        )
        raise
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
