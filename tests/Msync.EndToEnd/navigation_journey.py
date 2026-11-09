#!/usr/bin/env python3
"""Navigate actual lexical declarations and structural outlines through stdio LSP."""

from __future__ import annotations

import argparse
import json
import tempfile
from pathlib import Path
from typing import Any

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]


def position(source: str, offset: int) -> dict[str, int]:
    before = source[:offset]
    return {
        "line": before.count("\n"),
        "character": len(before.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2,
    }


def definition_cases(binary: Path, directory: Path) -> int:
    client = Client(binary)
    uri = (directory / "definitions.msync").as_uri()
    cases: list[tuple[str, str | None]] = [
        ("let value = true; fn f(value: Path) { val§ue }; f(./);", "value: Path"),
        ("let .codex = true; fn f() { .co§dex }; f();", ".codex ="),
        ("scope Settings { file: ./ }; let alias = Settings; alias::fi§le;", "file:"),
        ("scope Settings { file: ./ }; let alias = Settings; ali§as::file;", "alias ="),
        ("let x = true; fn f() { x§ }; let x = false; f();", "x = true"),
        ("fn before() { la§ter }; let later = true; before();", None),
        ('let text = "😀"; fn f(.值: Path) { .§值 }; f(./);', ".值: Path"),
    ]
    try:
        client.request("initialize", {"capabilities": {}})
        for version, (marked, target) in enumerate(cases, 1):
            offset = marked.index("§")
            source = marked.replace("§", "")
            notify(client, uri, version, source, opening=version == 1)
            result, _ = client.request(
                "textDocument/definition",
                {"textDocument": {"uri": uri}, "position": position(source, offset)},
            )
            assert diagnostics(client, uri, version)["diagnostics"] == [], source
            if target is None:
                assert result is None, result
            else:
                assert result["uri"] == uri, result
                assert result["range"]["start"] == position(
                    source, source.index(target)
                ), (
                    source,
                    result,
                )
        source = "let fs = $std::fs; fs::exists(./);"
        notify(client, uri, len(cases) + 1, source)
        result, _ = client.request(
            "textDocument/definition",
            {
                "textDocument": {"uri": uri},
                "position": position(source, source.index("exists") + 2),
            },
        )
        assert result["uri"] == "msync-stdlib:/0.8.0/fs.msync" or result["uri"] == (
            "msync-stdlib:///0.8.0/fs.msync"
        ), result
        return len(cases) + 1
    finally:
        client.close()


def outline_cases(binary: Path, directory: Path, hierarchical: bool) -> int:
    client = Client(binary)
    uri = (directory / "outline.msync").as_uri()
    source = (
        'let text = "fn phantom() { true };"; # scope Ghost { };\n'
        "scope Actual { new: fn(file: Path) { let .codex = file; scope { leaf: .codex } } };"
    )
    try:
        client.request(
            "initialize",
            {
                "capabilities": {
                    "textDocument": {
                        "documentSymbol": {
                            "hierarchicalDocumentSymbolSupport": hierarchical
                        }
                    }
                }
            },
        )
        for version, text in enumerate((source, source[:-3]), 1):
            notify(client, uri, version, text, opening=version == 1)
            symbols, _ = client.request(
                "textDocument/documentSymbol", {"textDocument": {"uri": uri}}
            )
            if hierarchical:
                assert [symbol["name"] for symbol in symbols] == ["text", "Actual"], (
                    symbols
                )
                actual = symbols[1]
                assert [child["name"] for child in actual["children"]] == ["new"]
                operation = actual["children"][0]
                assert [child["name"] for child in operation["children"]] == [
                    "file",
                    ".codex",
                    "leaf",
                ]
                assert tuple(actual["range"]["end"].values()) > tuple(
                    actual["selectionRange"]["end"].values()
                )
                assert tuple(operation["range"]["end"].values()) > tuple(
                    operation["selectionRange"]["end"].values()
                )
            else:
                expected = ["text", "Actual", "new", "file", ".codex", "leaf"]
                assert [symbol["name"] for symbol in symbols] == expected, symbols
                assert all(symbol["location"]["uri"] == uri for symbol in symbols)
                assert symbols[2]["containerName"] == "Actual"
            assert all(symbol["name"] not in {"phantom", "Ghost"} for symbol in symbols)
        return 2
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments = parser.parse_args()
    parent = ROOT / ".temp/navigation-journey"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="actual-", dir=parent) as temporary:
        directory = Path(temporary)
        result: dict[str, Any] = {
            "lexical_definition_cases": definition_cases(
                arguments.binary.resolve(), directory
            ),
            "hierarchical_outline_revisions": outline_cases(
                arguments.binary.resolve(), directory, True
            ),
            "flat_outline_revisions": outline_cases(
                arguments.binary.resolve(), directory, False
            ),
        }
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
