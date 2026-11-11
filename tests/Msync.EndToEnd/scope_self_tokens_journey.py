#!/usr/bin/env python3
"""Verify declaration-owned Scope roles in real file and library LSP documents."""

from __future__ import annotations

import argparse
import json
import tempfile
from pathlib import Path
from typing import Any

from language_upgrade_tooling_journey import position, request_at
from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """scope Tree {
    node: fn(value: Number) { value },
    unused: fn(value) { Tree::node(value) },
    shadow: fn { let Tree = scope { value: 1 }; Tree::value },
};
let inline = scope LocalTree {
    node: fn(value: Number) { value },
    unused: fn(value) { LocalTree::node(value) },
};
"""
LIBRARY: str = "msync-stdlib:///0.8.0/containers.msync"


def role_at(source: str, offset: int, data: list[int], legend: list[str]) -> str | None:
    expected: dict[str, int] = position(source, offset)
    line: int = 0
    character: int = 0
    for index in range(0, len(data), 5):
        delta_line, delta_start, length, role, _ = data[index : index + 5]
        line += delta_line
        character = delta_start if delta_line else character + delta_start
        if line == expected["line"] and character == expected["character"] and length:
            return legend[role]
    return None


def tokens(client: Client, uri: str) -> list[int]:
    result: dict[str, Any]
    result, _ = client.request(
        "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
    )
    return list(result["data"])


def verify(binary: Path, directory: Path) -> dict[str, object]:
    client = Client(binary, cwd=directory)
    try:
        initialized, _ = client.request("initialize", {"capabilities": {}})
        legend: list[str] = initialized["capabilities"]["semanticTokensProvider"][
            "legend"
        ]["tokenTypes"]
        uri: str = (directory / "self.msync").as_uri()
        notify(client, uri, 1, SOURCE, opening=True)
        assert diagnostics(client, uri, 1)["diagnostics"] == []
        checks: list[tuple[int, str]] = [
            (SOURCE.index("Tree"), "type"),
            (SOURCE.index("Tree::node"), "type"),
            (SOURCE.index("Tree::value"), "variable"),
            (SOURCE.index("LocalTree::node"), "type"),
        ]
        data: list[int] = tokens(client, uri)
        for offset, expected in checks:
            assert role_at(SOURCE, offset, data, legend) == expected, (offset, expected)
        selected: int = SOURCE.index("Tree::node")
        definition: list[dict[str, Any]] | dict[str, Any] = request_at(
            client, "textDocument/definition", uri, SOURCE, selected
        )
        target: dict[str, Any] = (
            definition[0] if isinstance(definition, list) else definition
        )
        assert target["uri"] == uri and target["range"]["start"] == position(
            SOURCE, SOURCE.index("Tree")
        ), target

        library_source: str
        library_source, _ = client.request("msync/stdlibSource", {"uri": LIBRARY})
        assert "scope BPlusTree {" in library_source
        notify(client, LIBRARY, 1, library_source, opening=True)
        diagnostics(client, LIBRARY, 1)
        library_data: list[int] = tokens(client, LIBRARY)
        tree: int = library_source.index("scope BPlusTree {")
        clear: int = library_source.index("clear: fn(self)", tree)
        member: int = library_source.index("BPlusTree::node", clear)
        assert role_at(library_source, member, library_data, legend) == "type", (
            "BPlusTree self reference"
        )
        return {
            "source_roles": len(checks),
            "definition": "lexical scope",
            "embedded_scope": "BPlusTree",
        }
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    temporary: Path = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="scope-self-tokens-", dir=temporary
    ) as work:
        report: dict[str, object] = verify(args.binary.resolve(), Path(work))
    if args.output is not None:
        output: Path = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("PASS: named-scope self-reference roles and lexical definition")


if __name__ == "__main__":
    main()
