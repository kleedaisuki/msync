#!/usr/bin/env python3
"""Separate ConPTY input preservation from native console output encoding."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import re
import subprocess
import sys
from collections.abc import Sequence
from io import TextIOWrapper
from pathlib import Path
from typing import cast

from windows_terminal_journey import Terminal

ROOT: Path = Path(__file__).resolve().parents[2]


def diagnostic_evaluate(
    terminal: Terminal,
    source: str,
    paint_suffix: str,
    output_pattern: str,
) -> tuple[str, str]:
    start = len(terminal.plain)
    terminal.send(source)
    terminal.until(
        lambda: (
            paint_suffix in terminal.plain[start:].replace("\r", "").replace("\n", "")
        ),
        f"diagnostic ASCII paint boundary {paint_suffix!r}",
    )
    paint = terminal.plain[start:]
    terminal.send("\r")
    expected = re.compile(r"\r?\n(" + output_pattern + r")\r?\n")
    terminal.until(
        lambda: expected.search(terminal.plain, start) is not None,
        "diagnostic native ASCII result",
    )
    matched = expected.search(terminal.plain, start)
    assert matched is not None
    terminal.prompt(matched.end())
    return matched.group(1), paint


def probe_case(
    binary: Path, directory: Path, command: Sequence[str] | None
) -> dict[str, object]:
    directory.mkdir(parents=True, exist_ok=True)
    work = directory / "work 空间"
    work.mkdir(exist_ok=True)
    (work / "marker.txt").write_text("CWD_CONTENT_OK", encoding="utf-8")
    terminal = Terminal(binary, directory, command=command)
    try:
        terminal.until(lambda: "> " in terminal.plain, "diagnostic native prompt")
        input_source = (
            'let sample = "空间"; '
            '$std::string::concat("INPUT_BYTES=", '
            "$std::string::utf8_size(sample) as String)"
        )
        measured, paint = diagnostic_evaluate(
            terminal,
            input_source,
            "$std::string::utf8_size(sample) as String)",
            r"INPUT_BYTES=[0-9]+",
        )
        cd_source = (
            'if exists ./"work 空间" { cd(./"work 空间"); "CD_OK" } '
            'else { "CD_MISSING" }'
        )
        changed, cd_paint = diagnostic_evaluate(
            terminal, cd_source, 'else { "CD_MISSING" }', r"CD_OK|CD_MISSING"
        )
        content: str | None = None
        if changed == "CD_OK":
            content, _ = diagnostic_evaluate(
                terminal, "cat(./marker.txt)", "cat(./marker.txt)", "CWD_CONTENT_OK"
            )
        terminal.key("/quit")
        terminal.send("\r")
        terminal.exit()
        return {
            "command": list(command)
            if command is not None
            else [str(binary), "--no-config", "repl"],
            "input_bytes": int(measured.split("=", 1)[1]),
            "expected_utf8_bytes": len("空间".encode()),
            "unicode_paint_preserved": "空间" in paint,
            "question_mark_paint": "??" in paint,
            "input_paint": paint,
            "cd_paint": cd_paint,
            "cd_result": changed,
            "cwd_content": content,
        }
    finally:
        terminal.finish(directory / "console.raw")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    if os.name != "nt":
        raise RuntimeError("This diagnostic requires the actual Windows ConPTY host.")
    binary = arguments.binary.resolve()
    output = arguments.output.resolve()
    work_root = output.parent / "console-encoding-work"
    work_root.mkdir(parents=True, exist_ok=True)
    native_arguments = [str(binary), "--no-config", "--no-telemetry", "repl"]
    utf8_command = [
        os.environ.get("COMSPEC", "cmd.exe"),
        "/d",
        "/c",
        "chcp 65001 >nul & " + subprocess.list2cmdline(native_arguments),
    ]
    result = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "platform": platform.platform(),
        "python": sys.version,
        "work_root": str(work_root),
        "method": "Same immutable Native image; direct ConPTY versus explicit console CP65001. ASCII paint suffix only in this causal diagnostic, never acceptance.",
        "default_console": probe_case(binary, work_root / "default", None),
        "utf8_console": probe_case(binary, work_root / "utf8", utf8_command),
    }
    encoded = json.dumps(result, indent=2, ensure_ascii=False)
    output.write_text(encoded + "\n", encoding="utf-8")
    cast(TextIOWrapper, sys.stdout).reconfigure(encoding="utf-8")
    print(encoded)


if __name__ == "__main__":
    main()
