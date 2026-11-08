#!/usr/bin/env python3
"""Exercise actual PTY ghost-hint acceptance without changing Enter or child stdin."""

from __future__ import annotations

import argparse
import json
import os
import re
import tempfile
from pathlib import Path

from terminal_journey import Terminal

ROOT = Path(__file__).resolve().parents[2]


def run(binary: Path, directory: Path) -> dict[str, int]:
    terminal = Terminal(binary, directory, no_color=False)
    try:
        terminal.until(lambda: "> " in terminal.plain, "first colored prompt")
        start = len(terminal.plain)
        terminal.key(b"declt")
        terminal.until(
            lambda: "ype" in terminal.plain[start:], "visible keyword suffix"
        )
        terminal.send(b"\r")
        terminal.prompt(terminal.line("declt", start))

        start = len(terminal.plain)
        terminal.key(b"declt")
        terminal.until(
            lambda: "ype" in terminal.plain[start:],
            "keyword hint after prior submission",
        )
        terminal.key(b"\x1b[C")
        terminal.key(b"(./)")
        terminal.send(b"\r")
        terminal.prompt(terminal.line("Path", start))

        terminal.evaluate('"HINT_HISTORY_OK"', "HINT_HISTORY_OK")
        start = len(terminal.plain)
        terminal.key(b'"HINT_H')
        terminal.until(
            lambda: 'ISTORY_OK"' in terminal.plain[start:],
            "visible native history suffix",
        )
        terminal.key(b"\x1b[C")
        terminal.send(b"\r")
        terminal.prompt(terminal.line("HINT_HISTORY_OK", start))

        target = directory / "completion-target.txt"
        target.write_text("OLD", encoding="utf-8")
        terminal.evaluate(
            'let selected_target = ./completion-target.txt; "BINDING_READY"',
            "BINDING_READY",
        )
        start = len(terminal.plain)
        terminal.key(b'"TYPED" -> selected_tar')
        terminal.until(
            lambda: "get" in terminal.plain[start:], "visible inferred binding suffix"
        )
        terminal.send(b"\r")
        terminal.until(
            lambda: (directory / "selected_tar").exists(),
            "Enter submits only typed binding prefix",
        )
        terminal.prompt(terminal.line("true", start))
        assert target.read_text(encoding="utf-8") == "OLD"
        assert (directory / "selected_tar").read_text(encoding="utf-8") == "TYPED"
        start = len(terminal.plain)
        terminal.key(b'"ACCEPTED" -> selected_tar')
        terminal.until(
            lambda: "get" in terminal.plain[start:],
            "binding hint can be explicitly accepted",
        )
        terminal.key(b"\x1b[C")
        terminal.send(b"\r")
        terminal.until(
            lambda: target.read_text(encoding="utf-8") == "ACCEPTED",
            "RightArrow accepts inferred binding suffix",
        )
        terminal.prompt(terminal.line("true", start))
        raw_start = len(terminal.raw)
        terminal.key(b"let color_hint: Path = ~/.codex;")
        styled = bytes(terminal.raw[raw_start:])
        assert re.search(rb"\x1b\[[0-9;]*36mlet", styled), styled
        assert re.search(rb"\x1b\[[0-9;]*35mPath", styled), styled
        assert re.search(rb"\x1b\[[0-9;]*34m~", styled), styled
        cancelled = len(terminal.plain)
        terminal.key(b"\x03")
        terminal.prompt(cancelled)

        child = directory / "hint-child.py"
        child.write_text(
            "from pathlib import Path\n"
            "Path('child-ready').write_text('ready')\n"
            "print('OWNED ' + input())\n",
            encoding="utf-8",
        )
        terminal.key(
            b'let child = /usr/bin/python3("./hint-child.py"); child::stdout -> ./child-output; "CHILD_DONE"'
        )
        terminal.send(b"\r")
        terminal.until(
            lambda: (directory / "child-ready").exists(),
            "native child owns stdin after hinted prompt",
        )
        terminal.send(b"NATIVE_STDIN\n")
        terminal.prompt(terminal.line("CHILD_DONE"))
        assert (directory / "child-output").read_text(
            encoding="utf-8"
        ) == "OWNED NATIVE_STDIN\n"
        terminal.key(b"/quit")
        terminal.send(b"\r")
        terminal.process.wait(timeout=5)
        assert terminal.process.returncode == 0
    finally:
        terminal.finish(directory / "hint-terminal.raw")
    plain_directory = directory / "plain"
    plain_directory.mkdir()
    plain = Terminal(binary, plain_directory)
    try:
        plain.until(lambda: "> " in plain.plain, "NO_COLOR prompt")
        start = len(plain.plain)
        plain.key(b"declt")
        plain.send(b"\r")
        plain.prompt(plain.line("declt", start))
        assert "ype" not in plain.plain[start:], plain.plain[start:]
    finally:
        plain.finish(directory / "plain-terminal.raw")
    return {
        "ghost_keyword_acceptance": 1,
        "enter_preserves_typed_source": 1,
        "history_hint_acceptance": 1,
        "native_child_stdin_ownership": 1,
        "no_color_ghost_disabled": 1,
        "binding_hint_explicit_acceptance": 1,
        "lexical_color_roles": 3,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    arguments = parser.parse_args()
    assert os.name == "posix", "Use the actual ConPTY runner on Windows"
    parent = ROOT / ".temp/completion-v080"
    parent.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="hints-", dir=parent)).resolve()
    print(json.dumps(run(arguments.binary.resolve(), directory), indent=2))


if __name__ == "__main__":
    main()
