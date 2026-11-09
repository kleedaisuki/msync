#!/usr/bin/env python3
"""Observe explicit ancestral imports through CLI bytecode, live REPL and LSP."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, cast

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client
from navigation_journey import position

ROOT = Path(__file__).resolve().parents[2]
INPUT = b"global Path\x00\xff\r\n"


@dataclass(frozen=True)
class Program:
    name: str
    source: str
    outputs: tuple[tuple[str, bytes], ...]


PROGRAMS: tuple[Program, ...] = (
    Program(
        "live-cell",
        'let value = "before"; fn read() { global value; value }; fn write() { global value; value = "after"; }; write(); let value = "shadow"; read() -> ./result; value -> ./shadow;',
        (("result", b"after"), ("shadow", b"shadow")),
    ),
    Program(
        "ancestry",
        'let root = "root"; let nearest = "outside"; fn outer() { let nearest = "parent"; fn middle() { fn inner() { global root, nearest; $std::string::concat($std::string::concat(root, "/"), nearest) }; inner() }; middle() }; outer() -> ./result;',
        (("result", b"root/parent"),),
    ),
    Program(
        "escaped-cell",
        'fn build() { let count = 0; fn step() { global count; count = count + 1; count }; step }; let next = build(); next(); if next() == 2 { "two" -> ./result; };',
        (("result", b"two"),),
    ),
    Program(
        "scope-field",
        'scope State { value: "before", write: fn { global value; value = "after"; }, read: fn { global value; value } }; let alias = State; alias::write(); State::read() -> ./result;',
        (("result", b"after"),),
    ),
    Program(
        "scope-rebind",
        'let object = scope { file: "before" }; fn read() { global object; object::file }; object = scope { file: "after" }; read() -> ./result;',
        (("result", b"after"),),
    ),
    Program(
        "prior-callable",
        'fn action() { "old" }; fn consume() { global action; action() }; fn action() { "new" }; consume() -> ./result;',
        (("result", b"old"),),
    ),
    Program(
        "native-callable",
        'let trim = $std::string::trim; fn consume() { global trim; trim("  native  ") }; consume() -> ./result;',
        (("result", b"native"),),
    ),
    Program(
        "nullable-cell",
        'let file? = null; fn set() { global file; file = ./input; }; fn clear() { global file; file = null; }; set(); if file is not null { file -> ./result; }; clear(); if file? { "cleared" -> ./cleared; };',
        (("result", INPUT), ("cleared", b"cleared")),
    ),
    Program(
        "repeat-shadow",
        'let value = "root"; fn read() { global value, value; { let value = "local"; value -> ./local; }; global value; value }; read() -> ./result;',
        (("result", b"root"), ("local", b"local")),
    ),
    Program(
        "legal-names",
        'let .codex = "dot"; let copy-config = "hyphen"; let 名称 = "unicode"; fn read() { global .codex, copy-config, 名称; $std::string::concat($std::string::concat(.codex, "/"), $std::string::concat(copy-config, $std::string::concat("/", 名称))) }; read() -> ./result;',
        (("result", b"dot/hyphen/unicode"),),
    ),
)

INVALID: tuple[tuple[str, str, tuple[str, ...]], ...] = (
    ("top-level", 'let value = "root"; global value;', ("E0214", "inside an fn")),
    (
        "forward",
        'fn read() { global later; later }; let later = "late"; read();',
        ("E0214", "before this function"),
    ),
    (
        "caller-local",
        'fn read() { global caller; caller }; fn use() { let caller = "local"; read() }; use();',
        ("E0214", "Caller locals"),
    ),
    (
        "parameter",
        'let value = "root"; fn read(value: String) { global value; value }; read("local");',
        ("E0214", "conflicts", "parameter"),
    ),
    (
        "local",
        'let value = "root"; fn read() { let value = "local"; global value; value }; read();',
        ("E0214", "conflicts", "local"),
    ),
    (
        "fixed-type",
        'let value = true; fn write() { global value; value = "wrong"; }; write();',
        ("Bool", "String"),
    ),
    (
        "null-admission",
        'let value = "root"; fn write() { global value; value = null; }; write();',
        ("Null", "?"),
    ),
    (
        "field-refinement",
        "scope State { value?: ./input, clear: fn { global value; value = null; } }; if State::value is not null { State::clear(); State::value -> ./result; };",
        ("Null", "Handle"),
    ),
)


def invoke(
    binary: Path, cwd: Path, arguments: list[str], source: str | None = None
) -> subprocess.CompletedProcess[str]:
    environment: dict[str, str] = dict(os.environ)
    environment["NO_COLOR"] = "1"
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=cwd,
        input=source,
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=environment,
        check=False,
        timeout=30,
    )


def successful(binary: Path, cwd: Path, arguments: list[str]) -> None:
    result = invoke(binary, cwd, arguments)
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)


def programs(binary: Path, root: Path, selected: set[str]) -> int:
    executions = 0
    for case in PROGRAMS:
        if selected and case.name not in selected:
            continue
        for level in (0, 2, 3):
            cwd = root / f"{case.name}-O{level}"
            cwd.mkdir()
            (cwd / "input").write_bytes(INPUT)
            source = cwd / "example.msync"
            source.write_text(case.source, encoding="utf-8")
            relocatable, executable = source.with_suffix(".mso"), source.with_suffix(
                ".mexe"
            )
            successful(
                binary,
                cwd,
                ["compile", str(source), f"-O{level}", "-o", str(relocatable)],
            )
            successful(
                binary, cwd, ["bind-ir", str(relocatable), "-o", str(executable)]
            )
            assert all(
                not (cwd / name).exists() for name, _ in case.outputs
            ), "compilation executed an import or body"
            for target in (source, relocatable, executable):
                successful(binary, cwd, ["run", str(target), f"-O{level}"])
                for name, expected in case.outputs:
                    output = cwd / name
                    assert output.read_bytes() == expected, (
                        case.name,
                        level,
                        target,
                        name,
                    )
                    output.unlink()
                executions += 1
    return executions


def invalid(binary: Path, root: Path, selected: set[str]) -> int:
    for name, text, fragments in INVALID:
        if selected and name not in selected:
            continue
        path = root / f"invalid-{name}.msync"
        path.write_text(text, encoding="utf-8")
        result = invoke(binary, root, ["check", str(path)])
        assert result.returncode != 0, (name, "invalid import accepted")
        for fragment in fragments:
            assert fragment in result.stderr, (name, fragment, result.stderr)
    return len(INVALID)


def repl(binary: Path, root: Path) -> int:
    submissions = (
        'let value = "before"; fn read() { global value; value };',
        'value = "after";',
        "read()",
        'let value = "shadow";',
        "read()",
        "value",
        'let other? = null; fn write() { global other; other = "written"; };',
        "write();",
        "other",
        "fn wrong() { global missing; };",
        "read()",
        "/quit",
        "",
    )
    result = invoke(binary, root, ["repl"], "\n".join(submissions))
    assert result.returncode == 0 and result.stdout.splitlines() == [
        "after",
        "after",
        "shadow",
        "written",
        "after",
    ], (result.stdout, result.stderr)
    assert result.stderr.count("error[E0214]") == 1, result.stderr
    return 5


def editor(binary: Path, root: Path) -> int:
    client = Client(binary, cwd=root)
    uri = (root / "editing.msync").as_uri()
    text = 'let root = scope { item: "root" }; fn copy() { "copy" }; fn read(parameter: String) { let local = "local"; global ro; root::item };'
    try:
        client.request("initialize", {"capabilities": {}})
        notify(client, uri, 1, text, opening=True)
        caret = text.index("global ro") + len("global ro")
        response, _ = client.request(
            "textDocument/completion",
            {"textDocument": {"uri": uri}, "position": position(text, caret)},
        )
        items = cast(list[dict[str, Any]], response)
        names = {item["label"] for item in items}
        assert (
            "root" in names
            and "parameter" not in names
            and "local" not in names
            and "$env" not in names
        ), items
        corrected = text.replace("global ro;", "global root;")
        notify(client, uri, 2, corrected)
        offset = corrected.index("global root") + len("global ")
        parameters: dict[str, Any] = {
            "textDocument": {"uri": uri},
            "position": position(corrected, offset + 1),
        }
        hover, _ = client.request("textDocument/hover", parameters)
        assert diagnostics(client, uri, 2)["diagnostics"] == [], corrected
        assert (
            "root: Scope" in hover["contents"]["value"]
            and "item" in hover["contents"]["value"]
        ), hover
        definition, _ = client.request("textDocument/definition", parameters)
        assert definition["uri"] == uri and definition["range"]["start"] == position(
            corrected, corrected.index("root")
        ), definition
        return 3
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--editor-only", action="store_true")
    parser.add_argument("--case", action="append", default=[], dest="cases")
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    selected: set[str] = set(arguments.cases)
    parent = ROOT / ".temp/global-journey"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        root = Path(temporary)
        count = 0 if arguments.editor_only else programs(binary, root, selected)
        rejected = 0 if arguments.editor_only else invalid(binary, root, selected)
        live = (
            0
            if arguments.editor_only or selected and "repl" not in selected
            else repl(binary, root)
        )
        editing = 0 if selected and "editor" not in selected else editor(binary, root)
    print(
        json.dumps(
            {
                "source_and_ir_executions": count,
                "global_diagnostics": rejected,
                "repl_values": live,
                "lsp_import_contracts": editing,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
