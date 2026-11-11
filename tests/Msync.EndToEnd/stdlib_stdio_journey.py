#!/usr/bin/env python3
"""Check borrowed standard text streams with actual CLI processes and a terminal."""

from __future__ import annotations

import argparse
import asyncio
import os
import subprocess
import tempfile
from collections.abc import Callable
from pathlib import Path
from typing import Protocol

ROOT: Path = Path(__file__).resolve().parents[2]
OPTIONS: tuple[str, ...] = ("--no-config", "--no-telemetry", "--color", "never")
SOURCE: str = """
let io = $std::io;
let concat = $std::string::concat;
let unused = $std::ranges::take(io::stdin_lines(), 0);
for line in unused { io::eprintln(concat("unexpected: ", line)); };
let empty? = io::read_line();
if empty is null { io::eprintln("wrong EOF"); }
else { io::println(concat("empty:", empty)); };
let lines = io::stdin_lines();
for line in $std::ranges::take(lines, 1) { io::println(concat("one:", line)); };
for line in $std::ranges::take(lines, 1) { io::println(concat("two:", line)); };
io::print("raw{}:");
io::print(io::read_stdin());
io::print("literal
");
if io::read_line() is null { io::println("eof"); };
io::eprint("warn{}:");
io::eprintln("星");
io::flush();
io::flush_error();
"""
INPUT: bytes = "\ufeff\r\nréflexion😀\r\n再读\nleft\r\nright\nlast".encode()
STDOUT: bytes = (
    os.linesep.join(("empty:", "one:réflexion😀", "two:再读", ""))
    + "raw{}:left\r\nright\nlastliteral\neof"
    + os.linesep
).encode()
STDERR: bytes = ("warn{}:星" + os.linesep).encode()


def command(binary: Path, *arguments: str) -> list[str]:
    return [str(binary), *OPTIONS, *arguments]


def run(
    binary: Path, directory: Path, *arguments: str, data: bytes = b""
) -> subprocess.CompletedProcess[bytes]:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        command(binary, *arguments),
        cwd=directory,
        input=data,
        capture_output=True,
        timeout=60,
        check=False,
    )
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)
    return result


def without_input(binary: Path, directory: Path, *arguments: str) -> None:
    # Keep the pipe open and empty: a mistaken planning-time read cannot see EOF.
    with (
        (directory / "preview.stdout").open("w+b") as output,
        (directory / "preview.stderr").open("w+b") as errors,
        subprocess.Popen(
            command(binary, *arguments),
            cwd=directory,
            stdin=subprocess.PIPE,
            stdout=output,
            stderr=errors,
        ) as process,
    ):
        try:
            process.wait(timeout=15)
        finally:
            if process.poll() is None:
                process.kill()
        output.seek(0)
        errors.seek(0)
        stdout: bytes = output.read()
        stderr: bytes = errors.read()
    assert process.returncode == 0 and not stderr, (arguments, stdout, stderr)
    assert b"raw{}:left" not in stdout and b"empty:\n" not in stdout


async def pipe_prompt(binary: Path, directory: Path) -> None:
    source: Path = directory / "prompt.msync"
    source.write_text(
        'let answer? = $std::io::prompt("name? ");\n'
        'if answer is not null { $std::io::println($std::string::concat("hello ", answer)); };\n',
        encoding="utf-8",
        newline="\n",
    )
    process: asyncio.subprocess.Process = await asyncio.create_subprocess_exec(
        *command(binary, "run", source.name),
        cwd=directory,
        stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    assert process.stdin is not None and process.stdout is not None
    try:
        prompt: bytes = await asyncio.wait_for(
            process.stdout.readexactly(6), timeout=15
        )
        assert prompt == b"name? ", prompt
        rest, errors = await asyncio.wait_for(
            process.communicate("小可莉🙂\n".encode()), timeout=15
        )
        assert process.returncode == 0 and not errors, (rest, errors)
        assert rest == ("hello 小可莉🙂" + os.linesep).encode(), rest
    finally:
        if process.returncode is None:
            process.kill()
            await process.wait()


class PromptTerminal(Protocol):
    plain: str

    def until(self, predicate: Callable[[], bool], description: str) -> None: ...
    def prompt(self, start: int) -> None: ...
    def evaluate(self, source: str, expected: str) -> float: ...


def exercise_prompt(
    terminal: PromptTerminal, key: Callable[[str], None], send: Callable[[str], None]
) -> None:
    terminal.prompt(0)
    key('let value? = $std::io::prompt($std::string::concat("DATA", "? "));')
    start: int = len(terminal.plain)
    send("\r")
    terminal.until(lambda: "DATA? " in terminal.plain[start:], "program prompt")
    send("实际输入🙂\ttext\r")
    terminal.prompt(start)
    terminal.evaluate(
        r'match value { <^实际输入🙂\ttext$> => { "got:ok" }, _ => { "got:bad" } }',
        "got:ok",
    )

    key('$std::io::prompt($std::string::concat("WAIT", "! "));')
    start = len(terminal.plain)
    send("\r")
    terminal.until(lambda: "WAIT! " in terminal.plain[start:], "blocked program read")
    cancelled: int = len(terminal.plain)
    send("\x03")
    terminal.until(
        lambda: "operation cancelled" in terminal.plain[cancelled:], "read cancellation"
    )
    terminal.prompt(cancelled)
    terminal.evaluate('"after-read-cancel"', "after-read-cancel")


def terminal_prompt(binary: Path, directory: Path) -> None:
    log: Path = directory / "stdio-pty.log"
    if os.name == "nt":
        from windows_terminal_journey import Terminal as WindowsTerminal

        windows = WindowsTerminal(binary, directory, command=command(binary, "repl"))
        try:
            exercise_prompt(windows, windows.key, windows.send)
            windows.send("\x04")
            windows.exit()
        finally:
            windows.finish(log)
        return

    from terminal_journey import Terminal

    terminal = Terminal(binary, directory, (*OPTIONS, "repl"))
    try:
        exercise_prompt(
            terminal,
            lambda value: terminal.key(value.encode()),
            lambda value: terminal.send(value.encode()),
        )
    finally:
        terminal.finish(log)
    assert terminal.process.returncode == 0, terminal.plain


def journey(binary: Path, directory: Path, tty: bool) -> None:
    source: Path = directory / "stdio.msync"
    source.write_text(SOURCE, encoding="utf-8", newline="\n")
    without_input(binary, directory, "check", source.name)
    without_input(binary, directory, "plan", source.name)
    without_input(binary, directory, "compile", source.name, "-o", "stdio.mso")
    without_input(binary, directory, "bind-ir", "stdio.mso", "-o", "stdio.mexe")
    for arguments in (
        ("run", source.name),
        ("run-ir", "stdio.mso"),
        ("run-ir", "stdio.mexe"),
    ):
        result = run(binary, directory, *arguments, data=INPUT)
        assert result.stdout == STDOUT, (arguments, result.stdout, STDOUT)
        assert result.stderr == STDERR, (arguments, result.stderr, STDERR)

    input_file: Path = directory / "stdin.txt"
    input_file.write_bytes(INPUT)
    with input_file.open("rb") as stream:
        redirected: subprocess.CompletedProcess[bytes] = subprocess.run(
            command(binary, "run", source.name),
            cwd=directory,
            stdin=stream,
            capture_output=True,
            timeout=30,
            check=False,
        )
    assert (
        redirected.returncode == 0
        and redirected.stdout == STDOUT
        and redirected.stderr == STDERR
    ), redirected

    repl_source: bytes = (
        "let line? = $std::io::read_line();\n"
        "input-only café🙂\n"
        "if line is not null { $std::io::println(line); };\n"
        "/quit\n"
    ).encode()
    result = run(binary, directory, "repl", data=repl_source)
    assert (
        result.stdout == ("input-only café🙂" + os.linesep).encode()
        and not result.stderr
    ), result
    result = run(
        binary,
        directory,
        "run",
        "-",
        data=b'if $std::io::read_line() is null { $std::io::println("eof"); };\n',
    )
    assert result.stdout == ("eof" + os.linesep).encode() and not result.stderr, result
    asyncio.run(pipe_prompt(binary, directory))
    if tty:
        terminal_prompt(binary, directory)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument(
        "--tty",
        action="store_true",
        help="also exercise PTY/ConPTY handoff and cancellation",
    )
    args: argparse.Namespace = parser.parse_args()
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="stdio-", dir=scratch) as path:
        journey(args.binary.resolve(), Path(path), args.tty)
    print(
        "PASS: redirected standard streams, lazy lines, EOF, prompt, and planning boundary"
        + (" + TTY handoff/Ctrl-C" if args.tty else "")
    )


if __name__ == "__main__":
    main()
