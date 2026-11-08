#!/usr/bin/env python3
"""Exercise stream-specific automatic color on real POSIX terminal descriptors."""

from __future__ import annotations

import argparse
import errno
import json
import os
import re
import selectors
import subprocess
import tempfile
import time
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ANSI = re.compile(r"\x1b\[[0-9;]*m")
CONTROL = re.compile(r"\x1b(?:\[[0-?]*[ -/]*[@-~]|[=>])")


@dataclass(frozen=True)
class TerminalResult:
    code: int
    terminal: str
    pipe: str


def invoke(
    binary: Path,
    arguments: Sequence[str],
    cwd: Path,
    environment: dict[str, str],
    *,
    error_terminal: bool = False,
) -> TerminalResult:
    import pty
    import termios

    master, slave = pty.openpty()
    termios.tcsetwinsize(slave, (30, 100))
    process = subprocess.Popen(
        [str(binary), "--no-config", *arguments],
        cwd=cwd,
        env=environment,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE if error_terminal else slave,
        stderr=slave if error_terminal else subprocess.PIPE,
    )
    os.close(slave)
    data = bytearray()
    deadline = time.monotonic() + 25
    try:
        with selectors.DefaultSelector() as ready:
            ready.register(master, selectors.EVENT_READ)
            while True:
                remaining = deadline - time.monotonic()
                assert remaining > 0, (arguments, bytes(data))
                if not ready.select(timeout=remaining):
                    raise AssertionError(("terminal output did not finish", arguments))
                try:
                    chunk = os.read(master, 65536)
                except OSError as error:
                    if error.errno == errno.EIO:
                        break
                    raise
                if not chunk:
                    break
                data.extend(chunk)
        stdout, stderr = process.communicate(timeout=5)
        piped = stdout if error_terminal else stderr
        assert piped is not None
        return TerminalResult(
            process.returncode,
            data.decode("utf-8").replace("\r\n", "\n"),
            piped.decode("utf-8"),
        )
    finally:
        os.close(master)
        if process.poll() is None:
            process.kill()
            process.wait()


def run(binary: Path, cwd: Path) -> dict[str, int]:
    environment = dict(os.environ)
    environment.pop("NO_COLOR", None)
    environment["TERM"] = "xterm-256color"
    help_output = invoke(binary, ["--help"], cwd, environment)
    assert help_output.code == 0 and ANSI.search(help_output.terminal), help_output
    assert "USAGE" in help_output.terminal and help_output.pipe == "", help_output

    guide = invoke(binary, ["help", "syntax"], cwd, environment)
    assert guide.code == 0 and "\x1b[1;36mlet\x1b[0m" in guide.terminal, guide
    assert "FUNCTIONS AND SCOPES" in guide.terminal and guide.pipe == "", guide

    for arguments, updates in (
        (["--color", "never", "--help"], {}),
        (["--color", "always", "--help"], {"NO_COLOR": "1"}),
        (["--help"], {"TERM": "dumb"}),
    ):
        plain = invoke(binary, arguments, cwd, {**environment, **updates})
        assert plain.code == 0 and not ANSI.search(plain.terminal), plain
        assert CONTROL.sub("", plain.terminal) == CONTROL.sub("", help_output.terminal), plain

    source = cwd / "nullable.msync"
    source.write_text("let value = null;\n", encoding="utf-8")
    diagnostic = invoke(
        binary, ["check", str(source)], cwd, environment, error_terminal=True
    )
    assert diagnostic.code == 1 and ANSI.search(diagnostic.terminal), diagnostic
    assert diagnostic.pipe == "", diagnostic
    rendered = CONTROL.sub("", diagnostic.terminal)
    assert "let value = null;" in rendered and "^^^^" in rendered, rendered
    assert "help:" in rendered and "let value? = null;" in rendered, rendered

    pipe_error = invoke(binary, ["check", str(source)], cwd, environment)
    assert pipe_error.code == 1 and CONTROL.sub("", pipe_error.terminal) == "", pipe_error
    assert not ANSI.search(pipe_error.pipe), pipe_error
    assert " ".join(pipe_error.pipe.split()) == " ".join(rendered.split()), (pipe_error, rendered)

    pipe_help = invoke(binary, ["--help"], cwd, environment, error_terminal=True)
    assert pipe_help.code == 0 and CONTROL.sub("", pipe_help.terminal) == "", pipe_help
    assert pipe_help.pipe == CONTROL.sub("", help_output.terminal), pipe_help

    reference = invoke(binary, ["skill", "language", "functions"], cwd, environment)
    assert reference.code == 0 and ANSI.search(reference.terminal), reference
    assert "# Functions and specialization" in ANSI.sub("", reference.terminal)
    return {"real_terminal_cases": 9}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    args = parser.parse_args()
    if os.name == "nt":
        raise SystemExit("Use the native ConPTY presentation journey on Windows.")
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="terminal-presentation-", dir=temporary) as name:
        print(json.dumps(run(args.binary.resolve(), Path(name)), indent=2))


if __name__ == "__main__":
    main()
