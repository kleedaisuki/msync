#!/usr/bin/env python3
"""Observe automatic terminal color through an actual native Windows console."""

from __future__ import annotations

import argparse
import importlib
import json
import os
import re
import selectors
import tempfile
import time
from pathlib import Path
from typing import cast

from windows_terminal_journey import ROOT, PtyFactory

SGR = re.compile(r"\x1b\[[0-9;]*m")
COLORED = re.compile(r"\x1b\[(?:[0-9]+;)*(?:3[0-7]|9[0-7])(?:;[0-9]+)*m")


def console(binary: Path, directory: Path, arguments: list[str], no_color: bool) -> str:
    factory = cast(PtyFactory, importlib.import_module("winpty").PtyProcess)
    environment = dict(os.environ)
    environment.pop("NO_COLOR", None)
    environment["TERM"] = "xterm-256color"
    if no_color:
        environment["NO_COLOR"] = "1"
    process = factory.spawn(
        [str(binary), "--no-config", *arguments],
        cwd=str(directory),
        env=environment,
        dimensions=(40, 120),
        backend="0",
    )
    selector = selectors.DefaultSelector()
    selector.register(process.fileobj, selectors.EVENT_READ)
    chunks: list[str] = []
    deadline = time.monotonic() + 20
    try:
        while True:
            ready = selector.select(0.05)
            if ready:
                try:
                    chunks.append(process.read(65536))
                except EOFError:
                    break
            elif not process.isalive():
                break
            if time.monotonic() >= deadline:
                raise AssertionError("Finite console command did not terminate")
    finally:
        if process.isalive():
            process.close(force=True)
        selector.close()
        process.fileobj.close()
    return "".join(chunks)


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
    cases = (
        ("automatic-syntax", ["help", "syntax"], False, True, "scope"),
        (
            "disabled-syntax",
            ["--color", "never", "help", "syntax"],
            False,
            False,
            "scope",
        ),
        (
            "no-color-syntax",
            ["--color", "always", "help", "syntax"],
            True,
            False,
            "scope",
        ),
        ("automatic-diagnostic", ["check", "invalid.msync"], False, True, "help:"),
        (
            "plain-diagnostic",
            ["--color", "never", "check", "invalid.msync"],
            False,
            False,
            "help:",
        ),
    )
    results: list[str] = []
    with tempfile.TemporaryDirectory(
        prefix="windows-presentation-", dir=temporary
    ) as name:
        directory = Path(name)
        (directory / "invalid.msync").write_text(
            "let value = null;\n", encoding="utf-8"
        )
        for case, command, no_color, expected_color, fragment in cases:
            text = console(arguments.binary.resolve(), directory, command, no_color)
            (output.parent / f"{case}.console.log").write_text(text, encoding="utf-8")
            assert bool(COLORED.search(text)) == expected_color, (case, text)
            assert fragment in SGR.sub("", text), (case, text)
            results.append(case)
    output.write_text(
        json.dumps({"backend": "ConPTY", "passed": results}, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"PASS: {len(results)} actual Windows console presentation contracts")


if __name__ == "__main__":
    main()
