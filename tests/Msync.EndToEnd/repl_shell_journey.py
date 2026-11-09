#!/usr/bin/env python3
"""Exercise direct shell functions and session-owned REPL paths through real input."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from io import TextIOWrapper
from pathlib import Path
from typing import cast

ROOT: Path = Path(__file__).resolve().parents[2]


def fixtures(directory: Path) -> Path:
    work = directory / "work 空间"
    work.mkdir(exist_ok=True)
    (directory / "root.txt").write_text("root-value", encoding="utf-8")
    (work / "inside.txt").write_text("inside-value", encoding="utf-8")
    (work / "completion_target.txt").write_text("completion-value", encoding="utf-8")
    (work / "line_end.txt").write_text("line-end\n", encoding="utf-8")
    return work


def redirected(binary: Path, directory: Path) -> dict[str, object]:
    work = fixtures(directory)
    source = """"ROOT"
pwd()
let anchored = ./root.txt;
fn read_global(path: Path) -> String { global cat; cat(path) };
cd(./"work 空间");
"WORK"
pwd()
read_global(anchored)
cat(./inside.txt)
cat(./line_end.txt)
"LS_BEGIN"
ls()
"LS_END"
[null, 1, "two"]
scope { name: "Klee", value: 7 }
head(sort(uniq(["b", "a", "a", "c"])), 2)
let effects = 0;
$std::ranges::map(["lazy-a", "lazy-b"], fn(value: String) { effects = effects + 1; value })
effects
wc(./inside.txt)
"created" -> ./created.txt;
cd(./missing);
"AFTER_MISSING"
pwd()
/cd ..
/pwd
let echo = fn(value: String) { $std::string::concat("user:", value) };
echo("override")
/quit
"""
    started = time.perf_counter_ns()
    completed = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "repl"],
        input=source,
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
        check=False,
    )
    elapsed_ms = (time.perf_counter_ns() - started) / 1_000_000
    assert completed.returncode == 0, (completed.stdout, completed.stderr)
    output = completed.stdout.splitlines()
    begin = output.index("LS_BEGIN")
    end = output.index("LS_END")
    listed = {Path(line).name for line in output[begin + 1 : end]}
    assert listed == {"inside.txt", "completion_target.txt", "line_end.txt"}, output
    assert output[:begin] == [
        "ROOT",
        str(directory),
        "WORK",
        str(work),
        "root-value",
        "inside-value",
        "line-end",
    ], (output, completed.stderr)
    assert output[end + 1 :] == [
        "null",
        "1",
        "two",
        'scope { name: "Klee", value: 7 }',
        "a",
        "b",
        "lazy-a",
        "lazy-b",
        "2",
        "scope { lines: 0, words: 1, characters: 12, bytes: 12 }",
        "AFTER_MISSING",
        str(work),
        str(directory),
        "user:override",
    ], (output, completed.stderr)
    assert "missing" in completed.stderr, completed.stderr
    assert (work / "created.txt").read_text(encoding="utf-8") == "created"
    assert not (directory / "created.txt").exists()
    return {
        "direct_shell_functions": True,
        "session_cd_and_legacy_command": True,
        "captured_path_origin_preserved": True,
        "ordinary_global_capture_and_shadowing": True,
        "streamed_null_and_scalar_items": True,
        "lazy_effects_accepted_after_display": True,
        "scope_field_values": True,
        "native_directory_error_and_recovery": True,
        "redirected_journey_ms": elapsed_ms,
    }


def terminal(binary: Path, directory: Path) -> dict[str, object]:
    fixtures(directory)
    if os.name == "nt":
        from windows_terminal_journey import Terminal as WindowsTerminal

        console = WindowsTerminal(binary, directory, color=False)
        try:
            console.until(lambda: "> " in console.plain, "shell prompt")
            console.evaluate('cd(./"work 空间"); "ENTERED"', "ENTERED")
            console.key("cat(./completion_targ")
            console.key("\t")
            console.key("\t")
            console.key(")")
            console.send("\r")
            console.prompt(console.line("completion-value"))
            start = len(console.plain)
            console.key('$std::ranges::repeat("STREAM_ABORT", 1000000000000)')
            console.send("\r")
            boundary = console.line("STREAM_ABORT", start)
            console.send("\x03")
            console.prompt(boundary)
            console.evaluate('echo("STILL_READY")', "STILL_READY")
            console.send("\x04")
            console.exit()
        finally:
            console.finish(directory / "repl-shell-console.raw")
    else:
        from terminal_journey import Terminal

        unix = Terminal(binary, directory)
        try:
            unix.until(lambda: "> " in unix.plain, "shell prompt")
            unix.evaluate('cd(./"work 空间"); "ENTERED"', "ENTERED")
            unix.key(b"cat(./completion_targ")
            unix.key(b"\t")
            unix.key(b"\t")
            unix.key(b")")
            unix.send(b"\r")
            unix.prompt(unix.line("completion-value"))
            start = len(unix.plain)
            unix.key(b'$std::ranges::repeat("STREAM_ABORT", 1000000000000)')
            unix.send(b"\r")
            boundary = unix.line("STREAM_ABORT", start)
            unix.send(b"\x03")
            unix.prompt(boundary)
            unix.evaluate('echo("STILL_READY")', "STILL_READY")
            unix.send(b"\x04")
            unix.process.wait(timeout=5)
            assert unix.process.returncode == 0
        finally:
            unix.finish(directory / "repl-shell-console.raw")
    return {
        "real_terminal_cd_path_completion": True,
        "real_terminal_streaming_ctrl_c": True,
        "prompt_recovers_after_cancel": True,
    }


def process_display(binary: Path, directory: Path) -> dict[str, object]:
    executable = '"' + sys.executable.replace('"', '""') + '"'
    source = f"""let child = spawn({executable}, ["-c", "import sys; value: str = sys.stdin.read()"]);
child
$std::process::close_stdin(child);
wait(child)
/quit
"""
    completed = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "repl"],
        input=source,
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
        check=False,
    )
    output = completed.stdout.splitlines()
    assert completed.returncode == 0 and not completed.stderr, (
        completed.stdout,
        completed.stderr,
    )
    assert len(output) == 2 and re.fullmatch(
        r"Process\(pending, pid=[1-9][0-9]*\)", output[0]
    ), output
    assert output[1] == "Process(exit=0)", output
    return {"live_process_display": output[0], "completed_process_display": output[1]}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    parent = ROOT / ".temp/repl-shell"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        directory = Path(temporary)
        redirected_root = directory / "redirected"
        terminal_root = directory / "terminal"
        redirected_root.mkdir()
        terminal_root.mkdir()
        result = {
            **redirected(binary, redirected_root),
            **process_display(binary, directory),
            **terminal(binary, terminal_root),
        }
    encoded = json.dumps(result, indent=2, ensure_ascii=False)
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(encoded + "\n", encoding="utf-8")
    cast(TextIOWrapper, sys.stdout).reconfigure(encoding="utf-8")
    print(encoded)


if __name__ == "__main__":
    main()
