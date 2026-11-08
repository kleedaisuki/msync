#!/usr/bin/env python3
"""Distinguish a rendered prompt hint from accepted source in native ConPTY."""

from __future__ import annotations

import argparse
import json
import os
import re
import tempfile
from pathlib import Path

from windows_terminal_journey import ROOT, Terminal

GHOST = re.compile(r"\x1b\[(?:[0-9]+;)*90(?:;[0-9]+)*m")


def run(binary: Path, directory: Path, log: Path) -> list[str]:
    target = directory / "completion-target.txt"
    target.write_bytes(b"original destination")
    terminal = Terminal(binary, directory, color=True)
    phases: list[str] = []
    try:
        terminal.until(lambda: "> " in terminal.plain, "first colored prompt")
        terminal.key("let selected_target = ./completion-target.txt;")
        start = len(terminal.plain)
        terminal.send("\r")
        terminal.prompt(start)

        def hint(source: str) -> None:
            raw_start = len(terminal.raw)
            text_start = len(terminal.plain)
            terminal.key(source)
            terminal.until(
                lambda: (
                    "get" in terminal.plain[text_start:]
                    and GHOST.search(terminal.raw[raw_start:]) is not None
                ),
                "bright-black binding ghost suffix",
            )

        hint('"TYPED" -> selected_tar')
        start = len(terminal.plain)
        terminal.send("\r")
        typed = directory / "selected_tar"
        terminal.until(typed.exists, "only the typed destination was published")
        terminal.prompt(start)
        assert typed.read_bytes() == b"TYPED"
        assert target.read_bytes() == b"original destination"
        phases.append("ghost is not submitted source")
        typed.unlink()

        hint('"ACCEPTED" -> selected_tar')
        terminal.key("\x1b[C")
        start = len(terminal.plain)
        terminal.send("\r")
        terminal.until(
            lambda: target.read_bytes() == b"ACCEPTED",
            "RightArrow accepted the full binding suffix",
        )
        terminal.prompt(start)
        assert not typed.exists()
        phases.append("RightArrow accepts the ghost as source")
        terminal.send("\x04")
        terminal.exit()
        return phases
    finally:
        terminal.finish(log)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    if os.name != "nt":
        parser.error("An actual native Windows ConPTY host is required")
    output = arguments.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="windows-hint-", dir=temporary) as name:
        passed = run(
            arguments.binary.resolve(), Path(name), output.with_suffix(".console.log")
        )
    output.write_text(
        json.dumps({"backend": "ConPTY", "passed": passed}, indent=2) + "\n",
        encoding="utf-8",
    )
    print("PASS: rendered binding hint is inert until RightArrow acceptance")


if __name__ == "__main__":
    main()
