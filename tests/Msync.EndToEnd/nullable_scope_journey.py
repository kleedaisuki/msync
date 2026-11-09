#!/usr/bin/env python3
"""Preserve the user's annotated nullable Scope through real CLI, IR and LSP."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
from pathlib import Path
from typing import Any, cast

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client
from navigation_journey import position as lsp_position

ROOT = Path(__file__).resolve().parents[2]
PAYLOAD = b"nullable Scope\x00\xff\r\n"
HEADER = """scope SomeClass {
 new: fn -> Scope { scope { item: ~/.codex/AGENTS.md } },
 copy: fn(where: Connection, object: Scope) { object::item @ where -> ./ }
};
"""


def source(rebuilt: bool) -> str:
    tail = "scope { item: some::item }" if rebuilt else "some"
    return (
        HEADER
        + "let object? = {\n"
        + " let some = SomeClass::new();\n"
        + f" if exists some::item {{ {tail} }}\n"
        + "};\n"
        + "if not object? { SomeClass::copy($wsl, object) }\n"
    )


def invoke(binary: Path, directory: Path, arguments: list[str]) -> None:
    environment: dict[str, str] = dict(os.environ)
    environment["NO_COLOR"] = "1"
    result = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)


def exact_source_checks(binary: Path, directory: Path) -> int:
    count = 0
    for rebuilt in (False, True):
        program = directory / ("rebuilt.msync" if rebuilt else "direct.msync")
        program.write_text(source(rebuilt), encoding="utf-8")
        for level in (0, 2, 3):
            invoke(binary, directory, ["check", str(program), f"-O{level}"])
            count += 1
    return count


def editor_cases(binary: Path, directory: Path) -> int:
    client = Client(binary, cwd=directory)
    uri = (directory / "editing.msync").as_uri()
    try:
        client.request("initialize", {"capabilities": {}})
        client.send("initialized", {})
        notify(client, uri, 1, HEADER, opening=True)
        assert diagnostics(client, uri, 1)["diagnostics"] == [], (
            "an uninstantiated Scope parameter was incorrectly diagnosed",
            client.notifications,
        )
        for version, rebuilt in enumerate((False, True), 2):
            text = source(rebuilt)
            notify(client, uri, version, text)
            offset = text.index("object::item") + len("object::")
            position = lsp_position(text, offset + 1)
            parameters: dict[str, Any] = {
                "textDocument": {"uri": uri},
                "position": position,
            }
            hover, _ = client.request("textDocument/hover", parameters)
            assert diagnostics(client, uri, version)["diagnostics"] == [], text
            assert "item: Path" in hover["contents"]["value"], (text, hover)
            completion, _ = client.request("textDocument/completion", parameters)
            items = cast(list[dict[str, Any]], completion)
            members = [item for item in items if item["label"] == "item"]
            assert len(members) == 1 and "Path" in members[0]["detail"], (
                text,
                items,
            )
            assert members[0]["textEdit"] == {
                "range": {
                    "start": lsp_position(text, offset),
                    "end": lsp_position(text, offset + len("item")),
                },
                "newText": "item",
            }, members[0]
            definition, _ = client.request("textDocument/definition", parameters)
            declaration = text.rindex("item:") if rebuilt else text.index("item:")
            assert definition["uri"] == uri and definition["range"] == {
                "start": lsp_position(text, declaration),
                "end": lsp_position(text, declaration + len("item")),
            }, (text, definition)
        return 3
    finally:
        client.close()


def quote(text: str) -> str:
    return '"' + text.replace('"', '""') + '"'


def execution_cases(binary: Path, directory: Path) -> int:
    input_directory = directory / "input"
    input_directory.mkdir()
    present = input_directory / "AGENTS.md"
    present.write_bytes(PAYLOAD)
    missing = directory / "missing" / "AGENTS.md"
    count = 0
    for rebuilt in (False, True):
        for exists, item in ((False, missing), (True, present)):
            controlled = source(rebuilt).replace(
                "~/.codex/AGENTS.md", "(" + quote(item.as_posix()) + " @ $local)"
            )
            controlled = controlled.replace(
                "SomeClass::copy($wsl, object)", "SomeClass::copy($local, object)"
            )
            for level in (0, 2, 3):
                work = (
                    directory
                    / f"{'rebuilt' if rebuilt else 'direct'}-{exists}-O{level}"
                )
                work.mkdir()
                program = work / "example.msync"
                program.write_text(controlled, encoding="utf-8")
                relocatable = work / "example.mso"
                executable = work / "example.mexe"
                output = work / "AGENTS.md"
                invoke(binary, work, ["check", str(program), f"-O{level}"])
                invoke(
                    binary,
                    work,
                    ["compile", str(program), f"-O{level}", "-o", str(relocatable)],
                )
                invoke(
                    binary, work, ["bind-ir", str(relocatable), "-o", str(executable)]
                )
                assert not output.exists(), "checking/binding executed the transfer"
                for target in (program, relocatable, executable):
                    invoke(binary, work, ["run", str(target), f"-O{level}"])
                    if exists:
                        assert output.read_bytes() == PAYLOAD, (target, level, rebuilt)
                        output.unlink()
                    else:
                        assert not output.exists(), (target, "Null branch transferred")
                    assert present.read_bytes() == PAYLOAD, "the input was modified"
                    count += 1
    return count


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--lsp-only", action="store_true")
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    parent = ROOT / ".temp/nullable-scope-journey"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        directory = Path(temporary)
        checked = exact_source_checks(binary, directory)
        edited = editor_cases(binary, directory)
        executed = 0 if arguments.lsp_only else execution_cases(binary, directory)
    print(
        json.dumps(
            {
                "original_source_checks": checked,
                "lsp_document_versions": edited,
                "controlled_source_and_ir_executions": executed,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
