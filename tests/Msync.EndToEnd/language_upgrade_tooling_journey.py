#!/usr/bin/env python3
"""Exercise current Number/concept/as/@ contracts through real LSP and REPL."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path
from typing import Any

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]


def position(text: str, offset: int) -> dict[str, int]:
    prefix = text[:offset]
    return {
        "line": prefix.count("\n"),
        "character": len(prefix.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2,
    }


def request_at(client: Client, method: str, uri: str, text: str, offset: int) -> Any:
    return client.request(
        method, {"textDocument": {"uri": uri}, "position": position(text, offset)}
    )[0]


def token_roles(text: str, data: list[int], legend: list[str]) -> dict[str, list[str]]:
    lines = text.splitlines()
    line = column = 0
    result: dict[str, list[str]] = {}
    for index in range(0, len(data), 5):
        delta, start, length, kind, _ = data[index : index + 5]
        line += delta
        column = start if delta else column + start
        word = (
            lines[line]
            .encode("utf-16-le")[column * 2 : (column + length) * 2]
            .decode("utf-16-le")
        )
        result.setdefault(word, []).append(legend[kind])
    return result


def protocol(binary: Path, directory: Path) -> dict[str, object]:
    (directory / "marker.txt").write_text("fixture", encoding="utf-8")
    uri = (directory / "tooling.msync").as_uri()
    text = """concept HasItem { item: Path, };
fn fetch(value: HasItem) -> Path { value::item };
scope Named { item: ./marker.txt, };
let result = fetch(Named);
let decimal = 1.25e-3;
let hex = 0xff;
let binary = 0b10;
let octal = 0o7;
let separated = 1_000;
let filename = ./123/0xff;
let dotfile = .5notes;
let zero = -0;
let arithmetic = 2/3+4%2;
let power = 2**3**2;
let converted = "42" as Number;
let qualified = ./marker.txt @ $local;
let trimmed = $std::string::trim(" x ");
"""
    client = Client(binary, cwd=directory)
    try:
        initialized, _ = client.request(
            "initialize",
            {
                "capabilities": {
                    "textDocument": {
                        "documentSymbol": {"hierarchicalDocumentSymbolSupport": True}
                    }
                }
            },
        )
        legend = initialized["capabilities"]["semanticTokensProvider"]["legend"][
            "tokenTypes"
        ]
        assert (
            "@"
            in initialized["capabilities"]["completionProvider"]["triggerCharacters"]
        )
        notify(client, uri, 1, text, opening=True)
        assert not diagnostics(client, uri, 1)["diagnostics"]
        tokens, _ = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        roles = token_roles(text, tokens["data"], legend)
        for word in ("1.25e-3", "0xff", "0b10", "0o7", "1_000", "0", "2", "3", "4"):
            assert "number" in roles.get(word, []), (word, roles)
        assert roles["0xff"] == ["number"], roles
        assert "123" not in roles, roles
        for word in ("HasItem", "Named", "std", "string"):
            assert roles.get(word) and set(roles[word]) == {"type"}, (word, roles)
        assert set(roles["value"]) == {"variable"}, roles
        assert roles["as"] == ["controlKeyword"]
        assert roles["@"] == ["operator"]
        assert set(roles["fn"]) == {"keyword"}
        assert set(roles["scope"]) == {"keyword"}
        hover = request_at(
            client,
            "textDocument/hover",
            uri,
            text,
            text.index("HasItem", text.index("fn fetch")),
        )
        assert (
            "HasItem" in hover["contents"]["value"]
            and "item" in hover["contents"]["value"]
        ), hover
        definition = request_at(
            client,
            "textDocument/definition",
            uri,
            text,
            text.index("HasItem", text.index("fn fetch")),
        )
        location = definition[0] if isinstance(definition, list) else definition
        assert location["uri"] == uri and location["range"]["start"]["line"] == 0, (
            definition
        )
        symbols, _ = client.request(
            "textDocument/documentSymbol", {"textDocument": {"uri": uri}}
        )
        assert any(
            item["name"] == "HasItem" and item["kind"] == 5 for item in symbols
        ), symbols
        partial = text + "fn check(value: Has"
        notify(client, uri, 2, partial)
        completions = request_at(
            client, "textDocument/completion", uri, partial, len(partial)
        )
        assert any(
            item["label"] == "HasItem" and item["kind"] == 7 for item in completions
        ), completions
        path_text = "let target = ./mar @ $local;"
        notify(client, uri, 3, path_text)
        completions = request_at(
            client, "textDocument/completion", uri, path_text, path_text.index(" @")
        )
        item = next(item for item in completions if item["label"] == "marker.txt")
        assert item["textEdit"]["newText"] == "marker.txt", item
        assert item["textEdit"]["range"]["end"]["character"] <= path_text.index(" @"), (
            item
        )
        unary_text = "let value = 2; -val"
        notify(client, uri, 4, unary_text)
        completions = request_at(
            client, "textDocument/completion", uri, unary_text, len(unary_text)
        )
        unary = next(item for item in completions if item["label"] == "value")
        assert unary["textEdit"]["newText"] == "value", unary
        assert (
            unary["textEdit"]["range"]["start"]["character"]
            == unary_text.index("-val") + 1
        ), unary
        return {
            "semantic_roles": roles,
            "concept_hover_definition_completion": True,
            "qualified_path_completion": item,
        }
    finally:
        client.close()


def repl(binary: Path, directory: Path) -> dict[str, object]:
    submissions = """let numeric = 1_000 + .5;
numeric
2**3**2
"42" as Number
concept HasItem { item: Path, };
fn fetch(value: HasItem) -> Path { value::item };
scope Named { item: ./marker.txt, };
fetch(Named)
./marker.txt @ $local
/quit
"""
    completed = subprocess.run(
        [str(binary), "--no-config", "repl"],
        input=submissions,
        text=True,
        encoding="utf-8",
        capture_output=True,
        cwd=directory,
        timeout=30,
        check=True,
    )
    assert not completed.stderr, completed.stderr
    assert (
        "1000.5" in completed.stdout
        and "42" in completed.stdout
        and "marker.txt" in completed.stdout
    ), completed.stdout
    return {"redirected_repl": True}


def terminal_palette(binary: Path, directory: Path) -> dict[str, object]:
    source = 'concept Numeric { value: Number, }; let numeric = 0xff + 1e-3; "42" as Number; ./marker.txt @ $local; 2**3**2'
    if os.name == "nt":
        from windows_terminal_journey import Terminal as WindowsTerminal

        windows = WindowsTerminal(binary, directory, color=True)
        try:
            windows.until(lambda: "> " in windows.plain, "upgraded palette prompt")
            start = len(windows.raw)
            windows.key(source)
            raw = windows.raw[start:]
            for pattern in (
                r"\x1b\[[0-9;]*35m0xff",
                r"\x1b\[[0-9;]*35m1e-3",
                r"\x1b\[[0-9;]*35mas",
                r"\x1b\[[0-9;]*36mconcept",
                r"\x1b\[[0-9;]*36m@",
                r"\x1b\[[0-9;]*36m\*\*",
            ):
                assert re.search(pattern, raw), raw
            windows.send("\r")
            windows.prompt(windows.line("512"))
            windows.key("fn read(item: Numeri")
            windows.key("\t")
            windows.key("\t")
            windows.key(') { item::value }; "CONCEPT_READY"')
            windows.send("\r")
            windows.prompt(windows.line("CONCEPT_READY"))
            windows.send("\x04")
            windows.exit()
        finally:
            windows.finish(directory / "upgrade-palette.raw")
    else:
        from terminal_journey import Terminal

        terminal = Terminal(binary, directory, no_color=False)
        try:
            terminal.until(lambda: "> " in terminal.plain, "upgraded palette prompt")
            start = len(terminal.raw)
            terminal.key(source.encode())
            raw_bytes = bytes(terminal.raw[start:])
            for pattern in (
                rb"\x1b\[[0-9;]*35m0xff",
                rb"\x1b\[[0-9;]*35m1e-3",
                rb"\x1b\[[0-9;]*35mas",
                rb"\x1b\[[0-9;]*36mconcept",
                rb"\x1b\[[0-9;]*36m@",
                rb"\x1b\[[0-9;]*36m\*\*",
            ):
                assert re.search(pattern, raw_bytes), raw_bytes
            terminal.send(b"\r")
            terminal.prompt(terminal.line("512"))
            terminal.key(b"fn read(item: Numeri")
            terminal.key(b"\t")
            terminal.key(b"\t")
            terminal.key(b') { item::value }; "CONCEPT_READY"')
            terminal.send(b"\r")
            terminal.prompt(terminal.line("CONCEPT_READY"))
            terminal.send(b"\x04")
            terminal.process.wait(timeout=5)
            assert terminal.process.returncode == 0
        finally:
            terminal.finish(directory / "upgrade-palette.raw")
    return {
        "real_terminal_number_cast_palette": True,
        "persistent_concept_type_completion": True,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    parent = ROOT / ".temp/language-upgrade-tooling"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        directory = Path(temporary)
        result = {
            **protocol(binary, directory),
            **repl(binary, directory),
            **terminal_palette(binary, directory),
        }
    encoded = json.dumps(result, indent=2, ensure_ascii=False)
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(encoded + "\n", encoding="utf-8")
    print(encoded)


if __name__ == "__main__":
    main()
