#!/usr/bin/env python3
"""Validate submitted completion text and actual bytes through a real POSIX PTY."""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import shutil
import tempfile
from collections.abc import Callable
from pathlib import Path

from terminal_journey import Terminal

ROOT = Path(__file__).resolve().parents[2]


def submitted(directory: Path) -> list[str]:
    history = directory / "home/.msync/history-v08"
    return [
        base64.b64decode(line).decode("utf-8")
        for line in history.read_text(encoding="utf-8").splitlines()
        if line
    ]


def finish_submission(terminal: Terminal, suffix: str, marker: str, start: int) -> None:
    terminal.key(suffix.encode("utf-8"))
    terminal.send(b"\r")
    terminal.prompt(terminal.line(marker, start))


def complete(terminal: Terminal, prefix: str, selected_label: str | None = None) -> int:
    start = len(terminal.plain)
    terminal.key(prefix.encode("utf-8"))
    terminal.key(b"\t")
    if selected_label is not None:
        terminal.until(
            lambda: "│>" + selected_label in terminal.plain[start:],
            "selected completion " + selected_label,
        )
    terminal.key(b"\t")
    return start


def run_paths(binary: Path, directory: Path) -> dict[str, int]:
    fixtures = {
        "plain.txt": b"PLAIN CONTENT",
        "space name.txt": b"SPACE CONTENT",
        "some(name).txt": b"OPERATOR CONTENT",
    }
    for name, content in fixtures.items():
        (directory / name).write_bytes(content)
    terminal = Terminal(binary, directory)
    expected: list[str] = []
    try:
        terminal.until(lambda: "> " in terminal.plain, "path detail prompt")
        for index, (prefix, replacement, name) in enumerate(
            [
                ("./pl", "./plain.txt", "plain.txt"),
                ("./spa", './"space name.txt"', "space name.txt"),
                ("./som", './"some(name).txt"', "some(name).txt"),
            ]
        ):
            marker = f"PATH_DETAIL_{index}"
            suffix = f' -> ./result-{index}; "{marker}"'
            start = complete(terminal, prefix)
            finish_submission(terminal, suffix, marker, start)
            assert (directory / f"result-{index}").read_bytes() == fixtures[name]
            expected.append(replacement + suffix)

        marker = "MIDDLE_DETAIL"
        suffix = f' -> ./result-middle; "{marker}"'
        start = len(terminal.plain)
        terminal.key(b"./plZZ")
        terminal.key(b"\x1b[D\x1b[D")
        terminal.key(b"\t")
        terminal.key(b"\t")
        finish_submission(terminal, suffix, marker, start)
        assert (directory / "result-middle").read_bytes() == fixtures["plain.txt"]
        expected.append("./plain.txt" + suffix)

        start = complete(terminal, '"./spa')
        suffix = ' -> ./result-literal; "LITERAL_DETAIL"'
        finish_submission(terminal, suffix, "LITERAL_DETAIL", start)
        assert (directory / "result-literal").read_text(encoding="utf-8") == (
            "./space name.txt"
        )
        expected.append('"./space name.txt"' + suffix)
        terminal.key(b"/quit")
        terminal.send(b"\r")
        terminal.process.wait(timeout=5)
        assert terminal.process.returncode == 0
    finally:
        terminal.finish(directory / "paths.raw")
    history = submitted(directory)
    for source in expected:
        assert source in history, (source, history)
    assert all("$local" not in source for source in history), history
    return {"minimal_submitted_path_edits": len(expected)}


def run_names(binary: Path, directory: Path) -> dict[str, int]:
    (directory / "expected.txt").write_bytes(b"LEXICAL BINDING")
    (directory / ".codex").mkdir()
    (directory / ".codex/trap.txt").write_bytes(b"FILESYSTEM TRAP")
    terminal = Terminal(binary, directory, no_color=False)
    try:
        terminal.until(lambda: "> " in terminal.plain, "legal-name prompt")
        start = complete(
            terminal,
            "fn codex(file) { let .codex = ./expected.txt; .cod",
        )
        suffix = ' }; codex(true) -> ./dot-result; "DOT_NAME_DONE"'
        finish_submission(terminal, suffix, "DOT_NAME_DONE", start)
        assert (directory / "dot-result").read_bytes() == b"LEXICAL BINDING"
        terminal.evaluate('let .codex = ./expected.txt; "DOT_BOUND"', "DOT_BOUND")
        hint = len(terminal.plain)
        terminal.key(b".cod")
        terminal.until(lambda: "ex" in terminal.plain[hint:], "legal dot-name hint")
        terminal.key(b"\x1b[C")
        terminal.key(b' -> ./dot-hint-result; "DOT_HINT_DONE"')
        terminal.send(b"\r")
        terminal.prompt(terminal.line("DOT_HINT_DONE", hint))
        assert (directory / "dot-hint-result").read_bytes() == b"LEXICAL BINDING"
        terminal.key(b"/quit")
        terminal.send(b"\r")
        terminal.process.wait(timeout=5)
        assert terminal.process.returncode == 0
    finally:
        terminal.finish(directory / "names.raw")
    expected = (
        "fn codex(file) { let .codex = ./expected.txt; .codex"
        ' }; codex(true) -> ./dot-result; "DOT_NAME_DONE"'
    )
    assert expected in submitted(directory), submitted(directory)
    assert '.codex -> ./dot-hint-result; "DOT_HINT_DONE"' in submitted(directory)
    return {"legal_dot_name_semantic_completion": 1, "legal_dot_name_explicit_hint": 1}


def run_builtins(binary: Path, directory: Path) -> dict[str, int]:
    source = directory / "environment-source.txt"
    source.write_bytes(b"ENVIRONMENT CONTENT")
    variable = "MSYNC_TERMINAL_DETAIL_SOURCE"
    previous = os.environ.get(variable)
    previous_path = os.environ.get("PATH")
    programs = directory / "programs"
    programs.mkdir()
    executable = programs / "msync_detail_tool"
    shutil.copy2("/bin/echo", executable)
    os.environ["PATH"] = str(programs) + os.pathsep + (previous_path or "")
    os.environ[variable] = str(source)
    try:
        terminal = Terminal(binary, directory)
    finally:
        if previous is None:
            os.environ.pop(variable)
        else:
            os.environ[variable] = previous
        if previous_path is None:
            os.environ.pop("PATH")
        else:
            os.environ["PATH"] = previous_path
    expected: list[str] = []
    try:
        terminal.until(lambda: "> " in terminal.plain, "builtin detail prompt")
        for prefix, name, result in [
            ("en", "env", "Connection"),
            ("lo", "local", "Connection"),
            ("ws", "wsl", "Connection"),
            ("pw", "pwd", "Connection"),
            ("fi", "file", "Connection"),
            ("pa", "path", "Connection"),
            ("st", "std", "Scope"),
        ]:
            start = complete(terminal, "decltype($" + prefix)
            terminal.key(b")")
            terminal.send(b"\r")
            terminal.prompt(terminal.line(result, start))
            expected.append("decltype($" + name + ")")

        for prefix, suffix, marker, target in [
            (
                "$env::MSYNC_TERMINAL_DETAIL_S",
                ' -> ./env-result; "ENV_DETAIL_DONE"',
                "ENV_DETAIL_DONE",
                "env-result",
            ),
            (
                "environment::MSYNC_TERMINAL_DETAIL_S",
                ' -> ./env-alias-result; "ENV_ALIAS_DETAIL_DONE"',
                "ENV_ALIAS_DETAIL_DONE",
                "env-alias-result",
            ),
        ]:
            if prefix.startswith("environment"):
                terminal.evaluate('let environment = $env; "ENV_BOUND"', "ENV_BOUND")
            start = complete(terminal, prefix)
            finish_submission(terminal, suffix, marker, start)
            assert (directory / target).read_bytes() == b"ENVIRONMENT CONTENT"
            expected.append(prefix[:-1] + "SOURCE" + suffix)

        (directory / "pwdinput.txt").write_bytes(b"PWD CONTENT")
        for prefix, replacement, target, marker in [
            ("$pwd::pwdin", "$pwd::pwdinput.txt", "pwd-result", "PWD_DETAIL_DONE"),
            (
                "$local::pwdin",
                "$local::pwdinput.txt",
                "local-result",
                "LOCAL_DETAIL_DONE",
            ),
        ]:
            start = complete(terminal, prefix, "pwdinput.txt")
            suffix = f' -> ./{target}; "{marker}"'
            finish_submission(terminal, suffix, marker, start)
            assert (directory / target).read_bytes() == b"PWD CONTENT"
            expected.append(replacement + suffix)

        start = complete(terminal, "$path::msync_detail_t", "msync_detail_tool")
        terminal.key(b'("EXECUTABLE")::stdout -> ./path-result; "PATH_PROGRAM_DONE"')
        terminal.send(b"\r")
        terminal.prompt(terminal.line("PATH_PROGRAM_DONE", start))
        assert (directory / "path-result").read_bytes() == b"EXECUTABLE\n"
        expected.append(
            '$path::msync_detail_tool("EXECUTABLE")::stdout -> ./path-result; "PATH_PROGRAM_DONE"'
        )

        start = complete(terminal, "$std::ranges::sin")
        finish_submission(
            terminal, '(true); "RANGES_DETAIL_DONE"', "RANGES_DETAIL_DONE", start
        )
        expected.append('$std::ranges::single(true); "RANGES_DETAIL_DONE"')
        terminal.key(b"/quit")
        terminal.send(b"\r")
        terminal.process.wait(timeout=5)
        assert terminal.process.returncode == 0
    finally:
        terminal.finish(directory / "builtins.raw")
    history = submitted(directory)
    for text in expected:
        assert text in history, (text, history)
    assert all("$$" not in text for text in history), history
    assert all(
        not any(alias in text for alias in ("$view", "$views", "$ranges"))
        for text in history
    )
    return {"builtin_operator_and_namespace_edits": len(expected)}


def run_keywords(binary: Path, directory: Path) -> dict[str, int]:
    terminal = Terminal(binary, directory, no_color=False)
    try:
        terminal.until(lambda: "> " in terminal.plain, "keyword detail prompt")
        raw = len(terminal.raw)
        terminal.key(b"scope Named { construct: fn() { scope { value: true } } };")
        rendered = bytes(terminal.raw[raw:])
        assert re.search(rb"\x1b\[[0-9;]*36mscope", rendered), rendered
        assert re.search(rb"\x1b\[[0-9;]*36mfn", rendered), rendered
        cancelled = len(terminal.plain)
        terminal.key(b"\x03")
        terminal.prompt(cancelled)
        start = len(terminal.plain)
        terminal.key(b"declt")
        terminal.until(lambda: "ype" in terminal.plain[start:], "visible inert hint")
        terminal.send(b"\r")
        terminal.prompt(terminal.line("declt", start))
        start = len(terminal.plain)
        terminal.key(b"declt")
        terminal.until(lambda: "ype" in terminal.plain[start:], "visible accepted hint")
        terminal.key(b"\x1b[C")
        terminal.key(b"(./)")
        terminal.send(b"\r")
        terminal.prompt(terminal.line("Path", start))
    finally:
        terminal.finish(directory / "keywords.raw")
    return {"keyword_and_explicit_hint_contracts": 4}


def run_global(binary: Path, directory: Path) -> dict[str, int]:
    (directory / ".codex").mkdir()
    (directory / ".codex/trap.txt").write_bytes(b"FILESYSTEM TRAP")
    terminal = Terminal(binary, directory, no_color=False)
    source = "fn show() { global .codex; .codex }; show()"
    try:
        terminal.until(lambda: "> " in terminal.plain, "ancestor import prompt")
        terminal.evaluate(
            'let .codex = "GLOBAL_IMPORT_DONE"; "IMPORT_BOUND"', "IMPORT_BOUND"
        )
        start = complete(terminal, "fn show() { global .cod")
        finish_submission(terminal, "; .codex }; show()", "GLOBAL_IMPORT_DONE", start)
        terminal.key(b"/quit")
        terminal.send(b"\r")
        terminal.process.wait(timeout=5)
        assert terminal.process.returncode == 0
    finally:
        terminal.finish(directory / "global.raw")
    assert source in submitted(directory), submitted(directory)
    return {"global_import_semantic_terminal_completion": 1}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--case",
        choices=["paths", "names", "builtins", "keywords", "global"],
        action="append",
    )
    arguments = parser.parse_args()
    assert os.name == "posix", "Use the real ConPTY detail fixture on Windows"
    parent = ROOT / ".temp/terminal-details-v080"
    parent.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="journey-", dir=parent)).resolve()
    runners: dict[str, Callable[[Path, Path], dict[str, int]]] = {
        "paths": run_paths,
        "names": run_names,
        "builtins": run_builtins,
        "keywords": run_keywords,
        "global": run_global,
    }
    result: dict[str, int] = {}
    for name in arguments.case or list(runners):
        case = directory / name
        case.mkdir()
        result.update(runners[name](arguments.binary.resolve(), case))
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
