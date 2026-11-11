#!/usr/bin/env python3
"""Exercise immutable semantic generations through the real language server."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import tempfile
from io import TextIOWrapper
from pathlib import Path
from typing import Any, cast

from completion_journey import apply_edit
from language_upgrade_tooling_journey import position, request_at, token_roles
from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]


def hover_text(client: Client, uri: str, source: str, offset: int) -> str:
    result: dict[str, Any] = request_at(
        client, "textDocument/hover", uri, source, offset
    )
    return str(result["contents"]["value"])


def labels(client: Client, uri: str, source: str, offset: int) -> set[str]:
    result: list[dict[str, Any]] = request_at(
        client, "textDocument/completion", uri, source, offset
    )
    return {str(item["label"]) for item in result}


def standard_projection(client: Client, uri: str) -> dict[str, bool]:
    source = "let selected = $std::containers::Set;"
    notify(client, uri, 10, source)
    assert diagnostics(client, uri, 10)["diagnostics"] == []
    offset = source.index("Set")
    choices: list[dict[str, Any]] = request_at(
        client, "textDocument/completion", uri, source, offset
    )
    sibling = next(item for item in choices if item["label"] == "List")
    assert sibling["kind"] == 7 and sibling["detail"] == "scope List", sibling
    resolved, _ = client.request("completionItem/resolve", sibling)
    assert resolved["documentation"]["value"], resolved
    assert apply_edit(source, sibling["textEdit"]) == source.replace("Set", "List")
    definitions: dict[str, Any] | list[dict[str, Any]] = request_at(
        client, "textDocument/definition", uri, source, offset
    )
    target = definitions[0] if isinstance(definitions, list) else definitions
    assert target["uri"].startswith("msync-stdlib:"), target
    content, _ = client.request("msync/stdlibSource", {"uri": target["uri"]})
    assert "scope Set {" in content

    native = "$std::ranges::single(1);"
    notify(client, uri, 11, native)
    assert diagnostics(client, uri, 11)["diagnostics"] == []
    choices = request_at(
        client, "textDocument/completion", uri, native, native.index("single")
    )
    omitted = next(item for item in choices if item["label"] == "empty_of")
    assert omitted["kind"] == 3 and "element: Type" in omitted["detail"], omitted
    resolved, _ = client.request("completionItem/resolve", omitted)
    assert "ranges.empty_of" in resolved["documentation"]["value"], resolved

    for version, draft in (
        (12, "$std::containers::"),
        (13, "$std::containers::Lis;"),
        (14, "let collections = $std::containers; collections::"),
    ):
        notify(client, uri, version, draft)
        problems = diagnostics(client, uri, version)["diagnostics"]
        if version == 13:
            assert problems and "Lis" in problems[0]["message"], problems
        assert {"List", "Set"} <= labels(client, uri, draft, len(draft.rstrip(";")))

    shadow = (
        "let std = scope { containers: scope { Only: true } }; $std::containers::Only;"
    )
    notify(client, uri, 15, shadow)
    assert diagnostics(client, uri, 15)["diagnostics"] == []
    assert labels(client, uri, shadow, shadow.rindex("Only")) == {"Only"}
    return {
        "projected_standard_scope_completion_and_definition": True,
        "projected_standard_native_documentation": True,
        "incomplete_escaped_and_shadowed_standard_receivers": True,
    }


def protocol(binary: Path, directory: Path) -> dict[str, object]:
    uri = (directory / "语义 generations.msync").as_uri()
    marker = directory / "analysis-must-not-write.txt"
    source = """concept Named { item: typename Item, };
scope Factory {
    make: fn(value) { scope { item: value } },
};
fn declared_but_not_called(value) {
    let local = scope { path: ./marker.txt };
    local::path
};
let first = Factory::make(42);
let second = Factory::make("Klee");
let ignored = "analysis is not execution" -> ./analysis-must-not-write.txt;
let result_type = $std::meta::invoke_result(fn(value: Number) { value + 1 }, Number);
first::item;
second::item;
"""
    client = Client(binary, cwd=directory)
    try:
        initialized, _ = client.request(
            "initialize",
            {
                "capabilities": {
                    "textDocument": {
                        "documentSymbol": {"hierarchicalDocumentSymbolSupport": True},
                        "completion": {
                            "completionItem": {
                                "resolveSupport": {"properties": ["documentation"]}
                            }
                        },
                    }
                }
            },
        )
        legend: list[str] = initialized["capabilities"]["semanticTokensProvider"][
            "legend"
        ]["tokenTypes"]
        notify(client, uri, 1, source, opening=True)
        assert diagnostics(client, uri, 1)["diagnostics"] == []
        first = source.index("first::item") + len("first::")
        second = source.index("second::item") + len("second::")
        assert "Number" in hover_text(client, uri, source, first)
        assert "String" in hover_text(client, uri, source, second)
        assert not marker.exists(), "Semantic analysis executed a transfer"

        tokens, _ = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        roles = token_roles(source, tokens["data"], legend)
        assert set(roles["Factory"]) == {"type"}, roles
        assert set(roles["fn"]) == {"keyword"}, roles
        assert set(roles["scope"]) == {"keyword"}, roles
        assert set(roles["typename"]) == {"keyword"}, roles
        symbols, _ = client.request(
            "textDocument/documentSymbol", {"textDocument": {"uri": uri}}
        )
        factory = next(item for item in symbols if item["name"] == "Factory")
        assert factory["kind"] == 5 and factory["selectionRange"], factory
        assert any(item["name"] == "make" for item in factory["children"]), factory

        partial = """fn unfinished(argument) {
    let local = scope { item: 7 };

    local::"""
        notify(client, uri, 2, partial)
        assert diagnostics(client, uri, 2)["diagnostics"]
        assert "item" in labels(client, uri, partial, len(partial))
        assert "argument" in labels(client, uri, partial, partial.index("\n\n") + 1)

        latest = 'let last_string = "current";\nlast_'
        notify(client, uri, 3, "let last_number = 9;\nlast_")
        notify(client, uri, 4, latest)
        diagnostics(client, uri, 4)
        current = labels(client, uri, latest, len(latest))
        assert "last_string" in current and "last_number" not in current, current

        nullable = """fn read(value?) {
    if value is not null {
        value::item
    }
};
let candidate? = scope { item: 3 };
read(candidate);
"""
        notify(client, uri, 5, nullable)
        assert diagnostics(client, uri, 5)["diagnostics"] == []
        assert "Number" in hover_text(
            client, uri, nullable, nullable.index("value::item") + len("value::")
        )

        invalid = "let value: Number = true;"
        notify(client, uri, 6, invalid)
        rejected = diagnostics(client, uri, 6)["diagnostics"]
        assert rejected and rejected[0]["range"]["start"]["line"] == 0, rejected
        assert "Number" in rejected[0]["message"], rejected
        repaired = '"😀"; let café = true;\ncafé'
        notify(client, uri, 7, repaired)
        assert diagnostics(client, uri, 7)["diagnostics"] == []
        use = repaired.rindex("café")
        definition = request_at(client, "textDocument/definition", uri, repaired, use)
        target = definition[0] if isinstance(definition, list) else definition
        assert target["uri"] == uri, target
        assert target["range"]["start"] == position(repaired, repaired.index("café")), (
            target
        )

        standard = "$std::concepts::Product"
        notify(client, uri, 8, f"fn accept(value: {standard}) {{ value::first }};")
        standard_source = f"fn accept(value: {standard}) {{ value::first }};"
        assert diagnostics(client, uri, 8)["diagnostics"] == []
        definition = request_at(
            client,
            "textDocument/definition",
            uri,
            standard_source,
            standard_source.index("Product"),
        )
        target = definition[0] if isinstance(definition, list) else definition
        assert target["uri"].startswith("msync-stdlib:"), target
        content, _ = client.request("msync/stdlibSource", {"uri": target["uri"]})
        assert "concept Product" in content and "typename First" in content

        typed = "fn inspect(value: $std::concepts::Pro) { value };"
        notify(client, uri, 9, typed)
        diagnostics(client, uri, 9)
        caret = typed.index(")")
        choices: list[dict[str, Any]] = request_at(
            client, "textDocument/completion", uri, typed, caret
        )
        product = next(item for item in choices if item["label"] == "Product")
        completed = apply_edit(typed, product["textEdit"])
        assert "$std::concepts::Product)" in completed, completed
        assert "$$" not in completed, completed

        projected = standard_projection(client, uri)
        client.send("textDocument/didClose", {"textDocument": {"uri": uri}})
        assert diagnostics(client, uri, None)["diagnostics"] == []
        reopened = "let reopened = false;\nreopened"
        notify(client, uri, 1, reopened, opening=True)
        assert diagnostics(client, uri, 1)["diagnostics"] == []
        assert "Bool" in hover_text(client, uri, reopened, reopened.rindex("reopened"))
        assert "last_string" not in labels(client, uri, reopened, len(reopened))
        assert not marker.exists(), "Editor queries executed source effects"
        return {
            "generic_instance_types": True,
            "source_owned_symbols": True,
            "incomplete_generic_context": True,
            "latest_generation_only": True,
            "nullable_flow": True,
            "error_recovery": True,
            "utf16_definitions": True,
            "standard_virtual_definition": True,
            "qualified_type_edit": True,
            "close_reopen_identity": True,
            "analysis_did_not_execute": True,
            **projected,
        }
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    parent = ROOT / ".temp/hir-editor-journey"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="protocol-", dir=parent) as temporary:
        directory = Path(temporary)
        results = protocol(binary, directory)
    report: dict[str, object] = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "protocol": results,
    }
    encoded = json.dumps(report, indent=2, ensure_ascii=False)
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(encoded + "\n", encoding="utf-8")
    if sys.stdout.encoding and sys.stdout.encoding.lower().replace("-", "") != "utf8":
        cast(TextIOWrapper, sys.stdout).reconfigure(encoding="utf-8")
    print(encoded)


if __name__ == "__main__":
    main()
