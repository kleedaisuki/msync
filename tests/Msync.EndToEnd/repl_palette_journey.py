#!/usr/bin/env python3
"""Check the restrained shared syntax palette and real terminal input rendering."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SGR = re.compile(r"\x1b\[([0-9;]*)m")


def styled_text(raw: str) -> tuple[str, list[str | None]]:
    text: list[str] = []
    styles: list[str | None] = []
    foreground: str | None = None
    offset = 0
    for match in SGR.finditer(raw):
        chunk = raw[offset : match.start()]
        text.append(chunk)
        styles.extend([foreground] * len(chunk))
        for code in match.group(1).split(";"):
            number = int(code or "0")
            if number in (0, 39):
                foreground = None
            elif 30 <= number <= 37 or 90 <= number <= 97:
                foreground = str(number)
        offset = match.end()
    text.append(raw[offset:])
    styles.extend([foreground] * (len(raw) - offset))
    return "".join(text), styles


def check_token(
    plain: str, styles: list[str | None], line: str, token: str, expected: str | None
) -> None:
    begin = plain.index(line)
    start = begin + line.index(token)
    assert styles[start : start + len(token)] == [expected] * len(token), (
        line,
        token,
        expected,
        styles[start : start + len(token)],
    )


def shared_guide(binary: Path) -> dict[str, int]:
    environment = dict(os.environ)
    environment.pop("NO_COLOR", None)
    arguments = [str(binary), "--color", "always", "help", "syntax"]
    result = subprocess.run(
        arguments,
        env=environment,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    plain, styles = styled_text(result.stdout)
    binding = "let source = ~/.codex;"
    check_token(plain, styles, binding, "let", "36")
    check_token(plain, styles, binding, "source", None)
    check_token(plain, styles, binding, "~/.codex", None)
    transfer = "source -> ~/backup;"
    check_token(plain, styles, transfer, "source", None)
    check_token(plain, styles, transfer, "->", "36")
    check_token(plain, styles, transfer, "~/backup", None)
    check_token(plain, styles, transfer, ";", None)
    for policy in (
        [str(binary), "--color", "never", "help", "syntax"],
        arguments,
    ):
        disabled = subprocess.run(
            policy,
            env={**environment, "NO_COLOR": "1"},
            check=True,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        assert "\x1b" not in disabled.stdout
        assert disabled.stdout == plain
    return {"shared_guide_token_roles": 7, "no_color_modes": 2}


def terminal_palette(binary: Path, directory: Path) -> dict[str, int]:
    from terminal_journey import Terminal

    terminal = Terminal(binary, directory, no_color=False)
    try:
        terminal.until(lambda: "> " in terminal.plain, "palette prompt")
        raw_start = len(terminal.raw)
        terminal.key(
            b'fn palette(file: Path) -> Path { file }; let plain = true; "PALETTE_READY"'
        )
        raw = bytes(terminal.raw[raw_start:])
        assert re.search(rb"\x1b\[[0-9;]*36mfn", raw), raw
        assert re.search(rb"\x1b\[[0-9;]*33mpalette", raw), raw
        assert re.search(rb"\x1b\[[0-9;]*35mPath", raw), raw
        assert re.search(rb"\x1b\[[0-9;]*32m", raw), raw
        assert not re.search(rb"\x1b\[[0-9;]*(?:34|94)m", raw), raw
        terminal.send(b"\r")
        terminal.prompt(terminal.line("PALETTE_READY"))
        terminal.evaluate("palette(./entry)", "./entry")
        terminal.key(b"/quit")
        terminal.send(b"\r")
        terminal.process.wait(timeout=5)
        assert terminal.process.returncode == 0
        return {"actual_pty_palette_roles": 5, "source_preserved": 1}
    finally:
        terminal.finish(directory / "palette-terminal.raw")


def windows_palette(binary: Path, directory: Path) -> dict[str, int]:
    from windows_terminal_journey import Terminal

    terminal = Terminal(binary, directory, color=True)
    try:
        terminal.until(lambda: "> " in terminal.plain, "palette ConPTY prompt")
        raw_start = len(terminal.raw)
        terminal.key(
            'fn palette(file: Path) -> Path { file }; let plain = true; "PALETTE_READY"'
        )
        raw = terminal.raw[raw_start:]
        assert re.search(r"\x1b\[[0-9;]*36mfn", raw), raw
        assert re.search(r"\x1b\[[0-9;]*33mpalette", raw), raw
        assert re.search(r"\x1b\[[0-9;]*35mPath", raw), raw
        assert re.search(r"\x1b\[[0-9;]*32m", raw), raw
        assert not re.search(r"\x1b\[[0-9;]*(?:34|94)m", raw), raw
        terminal.send("\r")
        terminal.prompt(terminal.line("PALETTE_READY"))
        terminal.send("\x04")
        terminal.exit()
        return {"actual_conpty_palette_roles": 5, "source_preserved": 1}
    finally:
        terminal.finish(directory / "palette-terminal.raw")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    result = shared_guide(binary)
    parent = ROOT / ".temp/repl-palette"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        if os.name == "posix":
            result.update(terminal_palette(binary, Path(temporary)))
        elif os.name == "nt":
            result.update(windows_palette(binary, Path(temporary)))
        from language_upgrade_tooling_journey import (
            terminal_palette as upgraded_palette,
        )

        upgraded_palette(binary, Path(temporary))
        result["upgraded_terminal_token_roles"] = 6
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
