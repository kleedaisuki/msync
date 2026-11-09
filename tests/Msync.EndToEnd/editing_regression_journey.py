#!/usr/bin/env python3
"""Exercise outline and minimal completion edits while a document is unfinished."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
from pathlib import Path
from typing import Any, cast

from completion_journey import CARET, apply_edit, lsp_position, source_offset
from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]


def complete(
    client: Client, uri: str, version: int, marked: str
) -> tuple[str, list[dict[str, Any]]]:
    caret = marked.index(CARET)
    source = marked.replace(CARET, "")
    notify(client, uri, version, source, opening=version == 1)
    result, _ = client.request(
        "textDocument/completion",
        {"textDocument": {"uri": uri}, "position": lsp_position(source, caret)},
    )
    return source, cast(list[dict[str, Any]], result)


def selected_edit(
    source: str, items: list[dict[str, Any]], expected: str
) -> dict[str, Any]:
    selected = [
        item
        for item in items
        if item.get("textEdit") and apply_edit(source, item["textEdit"]) == expected
    ]
    assert len(selected) == 1, (source, expected, items)
    return selected[0]


def symbol_mode(
    binary: Path, directory: Path, converter: bool, hierarchy: bool | None
) -> int:
    client = Client(binary, cwd=directory)
    uri = (directory / "outline.msync").as_uri()
    sources = [
        (
            "scope SomeScope { new: fn() { scope { item: $wsl } } };\n"
            "let object = SomeScope::new();\nlet space = object::item;\n"
        ),
        "scope SomeScope {\n    new: fn() {\n        scope { item: $wsl }\n    },\n",
        "fn unfinished(item: Path) {\n    let local = item;\n",
        "let still_editing =\n",
        "fn\n",
    ]
    arrays: list[list[dict[str, Any]]] = []
    try:
        capabilities: dict[str, Any] = {}
        if hierarchy is not None:
            capabilities = {
                "textDocument": {
                    "documentSymbol": {"hierarchicalDocumentSymbolSupport": hierarchy}
                }
            }
        client.request("initialize", {"capabilities": capabilities})
        for version, source in enumerate(sources, start=1):
            notify(client, uri, version, source, opening=version == 1)
            # An outline request may arrive before the background checker completes.
            early, _ = client.request(
                "textDocument/documentSymbol", {"textDocument": {"uri": uri}}
            )
            arrays.append(cast(list[dict[str, Any]], early))
            diagnostics(client, uri, version)
            checked, _ = client.request(
                "textDocument/documentSymbol", {"textDocument": {"uri": uri}}
            )
            arrays.append(cast(list[dict[str, Any]], checked))
        if converter:
            result = subprocess.run(
                ["node", str(ROOT / "editors/vscode/test/protocol-symbols.cjs")],
                input=json.dumps(arrays),
                capture_output=True,
                text=True,
                check=False,
                timeout=20,
            )
            assert result.returncode == 0, (result.stdout, result.stderr, arrays)
        for values in arrays:
            assert isinstance(values, list), values
            for value in values:
                assert value.get("detail", "") is not None, value
                if hierarchy:
                    assert (
                        "selectionRange" in value
                        and "range" in value
                        and "location" not in value
                    ), value
                else:
                    assert (
                        "selectionRange" not in value
                        and "uri" in value["location"]
                        and "range" in value["location"]
                    ), value
        return len(arrays)
    finally:
        client.close()


def symbol_responses(binary: Path, directory: Path, converter: bool) -> dict[str, int]:
    count = sum(
        symbol_mode(binary, directory, converter, hierarchy)
        for hierarchy in (True, False, None)
    )
    return {
        "editing_outline_responses": count,
        "symbol_capability_modes": 3,
        "actual_client_converter": int(converter),
    }


def check_source(binary: Path, directory: Path, source: str) -> None:
    path = directory / "completed-edit.msync"
    path.write_text(source, encoding="utf-8")
    checked = subprocess.run(
        [str(binary), "--no-config", "check", str(path)],
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=15,
    )
    assert checked.returncode == 0, (source, checked.stdout, checked.stderr)


def minimal_paths(binary: Path, directory: Path) -> dict[str, int]:
    workspace = directory / "paths"
    workspace.mkdir()
    (workspace / "plain.txt").write_text("plain", encoding="utf-8")
    (workspace / "normal-folder").mkdir()
    (workspace / "Program Files (x86)").mkdir()
    (workspace / "two words.txt").write_text("spaces", encoding="utf-8")
    (workspace / "semi;colon.txt").write_text("operator", encoding="utf-8")
    client = Client(binary, cwd=workspace)
    uri = (workspace / "editing.msync").as_uri()
    absolute = (workspace / "plain.txt").as_posix()
    cases = [
        (
            "let file = " + absolute[:-3] + CARET + ";\n",
            "let file = " + absolute + ";\n",
        ),
        ("let file = ./pl" + CARET + ";\n", "let file = ./plain.txt;\n"),
        ("let file = ./normal-f" + CARET + ";\n", "let file = ./normal-folder/;\n"),
        (
            "let file = ./pl" + CARET + " @ $local;\n",
            "let file = ./plain.txt @ $local;\n",
        ),
        ('let file = "./pl' + CARET + '";\n', 'let file = "./plain.txt";\n'),
        ('"payload" -> ./pl' + CARET + ";\n", '"payload" -> ./plain.txt;\n'),
    ]
    try:
        client.request("initialize", {"capabilities": {}})
        for version, (marked, expected) in enumerate(cases, start=1):
            source, items = complete(client, uri, version, marked)
            candidate = selected_edit(source, items, expected)
            check_source(binary, workspace, expected)
            typed = marked[: marked.index(CARET)]
            leaf_start = max(typed.rfind("/"), typed.rfind("\\")) + 1
            assert (
                source_offset(source, candidate["textEdit"]["range"]["start"])
                >= leaf_start
            ), candidate
            if "$local" not in marked:
                assert "$local" not in candidate["textEdit"]["newText"], candidate
        source, items = complete(
            client, uri, len(cases) + 1, "let file = ./two w" + CARET + ";\n"
        )
        spaces = [
            item
            for item in items
            if "two words.txt" in item.get("textEdit", {}).get("newText", "")
        ]
        assert len(spaces) == 1, items
        final = apply_edit(source, spaces[0]["textEdit"])
        assert '"' in spaces[0]["textEdit"]["newText"] and "$local" not in final, final
        check_source(binary, workspace, final)
        source, items = complete(
            client, uri, len(cases) + 2, "let file = ./semi" + CARET + ";\n"
        )
        operator = [
            item
            for item in items
            if "semi;colon.txt" in item.get("textEdit", {}).get("newText", "")
        ]
        assert len(operator) == 1 and '"' in operator[0]["textEdit"]["newText"], items
        check_source(binary, workspace, apply_edit(source, operator[0]["textEdit"]))
        if os.name == "nt":
            for version, prefix in enumerate(
                [
                    "\\",
                    "\\\\",
                    "\\\\msync-incomplete-server",
                    "\\\\msync-incomplete-server\\",
                    "C:",
                    "C:\\",
                    "D:/",
                ],
                start=len(cases) + 3,
            ):
                complete(client, uri, version, "let file = " + prefix + CARET + ";\n")
        return {
            "minimal_literal_path_edits": len(cases),
            "necessary_quoted_candidates": 2,
            "partial_windows_roots": 7 if os.name == "nt" else 0,
        }
    finally:
        client.close()


def builtin_edits(binary: Path, directory: Path) -> dict[str, int]:
    environment = dict(os.environ)
    environment["MSYNC_COMPLETION_ALPHA"] = "owned-alpha"
    environment["MSYNC_COMPLETION_BETA"] = "owned-beta"
    executable_directory = directory / "bin"
    executable_directory.mkdir()
    executable_name = (
        "msync_completion_tool.exe" if os.name == "nt" else "msync_completion_tool"
    )
    executable = executable_directory / executable_name
    executable.write_bytes(b"fixture executable catalog entry")
    if os.name != "nt":
        executable.chmod(0o755)
    environment["PATH"] = (
        str(executable_directory) + os.pathsep + environment.get("PATH", "")
    )
    document_directory = directory / "documents"
    document_directory.mkdir()
    (directory / "pwd-physical.txt").write_text("working directory", encoding="utf-8")
    (document_directory / "file-physical.txt").write_text(
        "document directory", encoding="utf-8"
    )
    (directory / "file-physical-trap.txt").write_text(
        "wrong directory", encoding="utf-8"
    )
    client = Client(binary, cwd=directory, environment=environment)
    uri = (document_directory / "builtins.msync").as_uri()
    cases = [
        ("let endpoint = $" + CARET + ";\n", "let endpoint = $local;\n"),
        ("let endpoint = $ws" + CARET + ";\n", "let endpoint = $wsl;\n"),
        (
            "let value = $env::MSYNC_COMPLETION_AL" + CARET + ";\n",
            "let value = $env::MSYNC_COMPLETION_ALPHA;\n",
        ),
        (
            "fn query() { $env::MSYNC_COMPLETION_BE" + CARET + " }\n",
            "fn query() { $env::MSYNC_COMPLETION_BETA }\n",
        ),
        (
            "let environment = $env;\nenvironment::MSYNC_COMPLETION_AL" + CARET + "\n",
            "let environment = $env;\nenvironment::MSYNC_COMPLETION_ALPHA\n",
        ),
    ]
    cases.extend(
        (
            "let endpoint = $" + name[:2] + CARET + ";\n",
            "let endpoint = $" + name + ";\n",
        )
        for name in ("std", "pwd", "file", "env", "path")
    )
    cases.extend(
        [
            (
                "let helper = $std::ranges::ta" + CARET + ";\n",
                "let helper = $std::ranges::take;\n",
            ),
            (
                "let tool = $path::msync_completion_to" + CARET + ";\n",
                "let tool = $path::" + executable_name + ";\n",
            ),
            ("let current = $pwd::pw" + CARET + ";\n", "let current = $pwd::pwd;\n"),
            ("let source = $file::th" + CARET + ";\n", "let source = $file::this;\n"),
            ("let parent = $file::pa" + CARET + ";\n", "let parent = $file::parent;\n"),
            (
                "let current = ./pwd-ph" + CARET + " @ $pwd;\n",
                "let current = ./pwd-physical.txt @ $pwd;\n",
            ),
            (
                "let source = ./file-ph" + CARET + " @ $file;\n",
                "let source = ./file-physical.txt @ $file;\n",
            ),
        ]
    )
    try:
        client.request("initialize", {"capabilities": {}})
        for version, (marked, expected) in enumerate(cases, start=1):
            source, items = complete(client, uri, version, marked)
            if "$std::ranges::ta" in source:
                # Ordinary identifier completion may use VS Code's word range;
                # '$' and filesystem prefixes require the explicit edits above.
                selected = [item for item in items if item["label"] == "take"]
                assert len(selected) == 1 and selected[0]["kind"] == 3, items
                if "textEdit" in selected[0]:
                    edited = apply_edit(source, selected[0]["textEdit"])
                else:
                    caret = marked.index(CARET)
                    edited = (
                        source[: caret - 2]
                        + selected[0].get("insertText", "take")
                        + source[caret:]
                    )
                assert edited == expected, (edited, expected)
            else:
                selected_edit(source, items, expected)
            assert "$$" not in expected
        return {"builtin_prefix_and_environment_edits": len(cases)}
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--converter",
        action="store_true",
        help="Run the installed production client converter; npm dependencies must exist",
    )
    parser.add_argument(
        "--case", choices=["symbols", "paths", "builtins"], action="append"
    )
    arguments = parser.parse_args()
    parent = ROOT / ".temp/editing-regression"
    parent.mkdir(parents=True, exist_ok=True)
    result: dict[str, int] = {}
    with tempfile.TemporaryDirectory(dir=parent, prefix="journey-") as temporary:
        directory = Path(temporary)
        cases: list[str] = arguments.case or ["symbols", "paths", "builtins"]
        if "symbols" in cases:
            result.update(
                symbol_responses(
                    arguments.binary.resolve(), directory, arguments.converter
                )
            )
        if "paths" in cases:
            result.update(minimal_paths(arguments.binary.resolve(), directory))
        if "builtins" in cases:
            result.update(builtin_edits(arguments.binary.resolve(), directory))
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
