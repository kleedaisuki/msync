#!/usr/bin/env python3
"""Use legal atom bindings and return constraints through the actual CLI and LSP."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from pathlib import Path
from typing import Any

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]
CARET = "⟦caret⟧"


def invoke(
    binary: Path, directory: Path, arguments: list[str]
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )


def position(text: str, offset: int) -> dict[str, int]:
    before = text[:offset]
    return {
        "line": before.count("\n"),
        "character": len(before.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2,
    }


def apply(text: str, edit: dict[str, Any]) -> str:
    def offset(point: dict[str, int]) -> int:
        lines = text.splitlines(keepends=True)
        return sum(map(len, lines[: point["line"]])) + len(
            lines[point["line"]]
            .encode("utf-16-le")[: point["character"] * 2]
            .decode("utf-16-le")
        )

    return (
        text[: offset(edit["range"]["start"])]
        + edit["newText"]
        + text[offset(edit["range"]["end"]) :]
    )


def runtime(binary: Path, directory: Path) -> dict[str, int]:
    program = directory / "bindings.msync"
    output = directory / "result"
    (directory / ".codex").write_text("FILESYSTEM_TRAP", encoding="utf-8")
    (directory / "input").write_text("ACTUAL_FILE_BYTES", encoding="utf-8")
    examples: list[tuple[str, str]] = [
        (
            'fn codex(file) { let .codex = "BINDING"; .codex }; codex(true) -> ./result;',
            "BINDING",
        ),
        (
            'let .codex = "CAPTURED"; let f = fn() -> String { .codex }; let .codex = "SHADOW"; f() -> ./result;',
            "CAPTURED",
        ),
        (
            'fn .copy-config(file: String) -> String { file }; .copy-config("DOT_FUNCTION") -> ./result;',
            "DOT_FUNCTION",
        ),
        ('let 名-称 = "UNICODE_BINDING"; 名-称 -> ./result;', "UNICODE_BINDING"),
        ('scope Config { .codex: "MEMBER" }; Config::.codex -> ./result;', "MEMBER"),
        (
            'fn file(value: String) -> Path { value }; file("./input") -> ./result;',
            "ACTUAL_FILE_BYTES",
        ),
        (
            'let file = fn(value: String) -> Path { return value; }; file("./input") -> ./result;',
            "ACTUAL_FILE_BYTES",
        ),
        (
            "fn choose(value: Bool) -> Path? { if value { ./input } else { null } }; let file? = choose(true); if file is not null { file -> ./result };",
            "ACTUAL_FILE_BYTES",
        ),
        (
            'fn .count-down(value: Number) -> Number { if value == 0 { 0 } else { .count-down(value - 1) } }; if .count-down(3) == 0 { "RECURSIVE" -> ./result };',
            "RECURSIVE",
        ),
    ]
    for source, expected in examples:
        output.unlink(missing_ok=True)
        program.write_text(source, encoding="utf-8")
        checked = invoke(binary, directory, ["check", str(program)])
        assert checked.returncode == 0, (source, checked.stderr)
        formatted = invoke(binary, directory, ["fmt", str(program), "--stdout"])
        assert formatted.returncode == 0, formatted.stderr
        program.write_bytes(formatted.stdout.encode("utf-8"))
        again = invoke(binary, directory, ["fmt", str(program), "--check"])
        assert again.returncode == 0, (source, again.stderr)
        executed = invoke(binary, directory, ["run", str(program)])
        assert executed.returncode == 0, (source, executed.stderr)
        assert output.read_text(encoding="utf-8") == expected, (
            source,
            output.read_text(),
        )
    bad = [
        "fn bad() -> Bool { ./input }; bad();",
        "let bad = fn() -> Bool { return ./input; }; bad();",
        "fn bad() -> Path { null }; bad();",
    ]
    for source in bad:
        program.write_text(source, encoding="utf-8")
        checked = invoke(binary, directory, ["check", str(program)])
        assert checked.returncode != 0 and "return type" in checked.stderr, (
            checked.stderr
        )
        assert (
            "declares return type" in checked.stderr and "annotation" in checked.stderr
        ), checked.stderr
    return {
        "actual_binding_and_return_results": len(examples),
        "return_constraint_diagnostics": len(bad),
    }


def editor(binary: Path, directory: Path) -> dict[str, int]:
    client = Client(binary, cwd=directory)
    uri = (directory / "editor.msync").as_uri()
    cases: list[tuple[str, str, str]] = [
        (
            "fn codex(file) { let .codex = ~/.codex; .co⟦caret⟧ }",
            ".codex",
            "fn codex(file) { let .codex = ~/.codex; .codex }",
        ),
        (
            "fn sample() { let copy-name = true; copy-na⟦caret⟧ }",
            "copy-name",
            "fn sample() { let copy-name = true; copy-name }",
        ),
        (
            "fn sample(file: Pa⟦caret⟧) -> Path { file }",
            "Path",
            "fn sample(file: Path) -> Path { file }",
        ),
        (
            "fn sample(file: Path) -> Pa⟦caret⟧ { file }",
            "Path",
            "fn sample(file: Path) -> Path { file }",
        ),
        (
            "let sample = fn(file: Path) -> ⟦caret⟧ { file };",
            "Path",
            "let sample = fn(file: Path) -> Path { file };",
        ),
        (
            "fn sample() -> Range<Pa⟦caret⟧> { [./input] }",
            "Path",
            "fn sample() -> Range<Path> { [./input] }",
        ),
        ("let value: Pa⟦caret⟧ = ./input;", "Path", "let value: Path = ./input;"),
    ]
    try:
        client.request("initialize", {"capabilities": {}})
        for version, (marked, label, expected) in enumerate(cases, 1):
            caret = marked.index(CARET)
            source = marked.replace(CARET, "")
            notify(client, uri, version, source, opening=version == 1)
            diagnostics(client, uri, version)
            items, _ = client.request(
                "textDocument/completion",
                {"textDocument": {"uri": uri}, "position": position(source, caret)},
            )
            selected = next((item for item in items if item["label"] == label), None)
            assert selected is not None, (marked, items)
            assert apply(source, selected["textEdit"]) == expected, (
                marked,
                selected,
                apply(source, selected["textEdit"]),
            )
            if label == "Path":
                names = {item["label"] for item in items}
                assert (
                    "Int" not in names and {"Path", "Type", "Any", "Number"} <= names
                ), names
        return {"legal_name_and_type_position_edits": len(cases)}
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments = parser.parse_args()
    parent = ROOT / ".temp/identifier-return-fix"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        directory = Path(temporary)
        print(
            json.dumps(
                {
                    **runtime(arguments.binary.resolve(), directory),
                    **editor(arguments.binary.resolve(), directory),
                },
                indent=2,
            )
        )


if __name__ == "__main__":
    main()
