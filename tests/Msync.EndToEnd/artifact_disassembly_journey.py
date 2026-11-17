#!/usr/bin/env python3
"""Read real source-free native IR without executing, rebinding or modifying it."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import tempfile
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
ANSI = re.compile(r"\x1b\[[0-9;]*m")


def invoke(
    binary: Path, cwd: Path, arguments: list[str], data: bytes | None = None
) -> subprocess.CompletedProcess[bytes]:
    environment = dict(os.environ)
    environment.pop("NO_COLOR", None)
    return subprocess.run(
        [str(binary), *arguments],
        cwd=cwd,
        input=data,
        capture_output=True,
        env=environment,
        check=False,
        timeout=30,
    )


def success(binary: Path, cwd: Path, arguments: list[str]) -> bytes:
    result = invoke(binary, cwd, arguments)
    assert result.returncode == 0, (arguments, result.stderr.decode("utf-8"))
    return result.stdout


def validate_model(document: dict[str, Any], stage: str) -> dict[str, int]:
    assert document["representation"] == "native-v1", document["representation"]
    assert document["formatVersion"] == 2
    assert document["languageVersion"] == "0.8.0"
    assert document["stage"] == stage
    assert document["optimizationLevel"] == 0
    functions: list[dict[str, Any]] = document["functions"]
    layouts: list[dict[str, Any]] = document["layouts"]
    types: list[dict[str, Any]] = document["types"]
    instructions: list[dict[str, Any]] = [
        instruction
        for function in functions
        for instruction in function["instructions"]
    ]
    opcodes: set[str] = {instruction["op"] for instruction in instructions}
    assert {"Allocate", "Load", "Store", "Binary"} <= opcodes, opcodes
    assert {"Path", "Scope", "Number"} <= {value["kind"] for value in types}
    assert any(value["text"] == 'hello "Klee"\n世界' for value in document["constants"])
    assert any(
        field["name"] == "count" for layout in layouts for field in layout["fields"]
    )
    for function in functions:
        assert function["signature"]["result"] in range(len(types)), function[
            "signature"
        ]
    for instruction in instructions:
        if instruction["op"] == "Allocate":
            assert instruction["a"] in range(len(layouts)), instruction
        if instruction["op"] == "AddressOffset":
            layout: dict[str, Any] = layouts[instruction["b"]]
            assert instruction["c"] in range(len(layout["fields"])), instruction
    spans: list[dict[str, Any]] = [
        instruction["source"] for instruction in instructions if "source" in instruction
    ]
    assert spans and all(
        span["file"] and span["start"] >= 0 and span["length"] >= 0 for span in spans
    )
    if stage == "Executable":
        assert (
            document["context"]["workingDirectory"]
            and document["context"]["homeDirectory"]
        )
    else:
        assert "context" not in document
        assert "Transfer" in opcodes
    return {
        "functions": len(functions),
        "layouts": len(layouts),
        "source_spans": len(spans),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    parent = ROOT / ".temp" / "artifact-disassembly"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="查看 native IR ", dir=parent) as directory:
        cwd = Path(directory)
        source = cwd / "source with spaces.msync"
        source.write_text(
            "scope Demo { copy: fn(file: Path, destination: Path) -> Bool { file -> destination } };\n"
            'let message = "hello ""Klee""\n世界";\n'
            "if exists ./missing { message -> ./must-not-exist; };\n"
            "Demo::copy(./missing, ./must-not-exist);\n"
            "fn state() { scope Shared { count: 7 }; Shared };\n"
            "let first = state(); let second = state();\n"
            "let local = scope { count: 2 };\n"
            "local::count = 9;\n"
            "scope Counter { value: 3 };\n"
            "Counter::value = 8;\n"
            "let remainder = first::count % (local::count + Counter::value);\n",
            encoding="utf-8",
        )
        relocatable = cwd / "readable.mso"
        executable = cwd / "readable.mexe"
        success(
            binary,
            cwd,
            ["--no-config", "compile", str(source), "-O0", "-o", str(relocatable)],
        )
        success(
            binary,
            cwd,
            ["--no-config", "bind-ir", str(relocatable), "-o", str(executable)],
        )
        source.unlink()
        # An explicitly selected malformed configuration must not enter runtime setup.
        broken_config = cwd / "broken.toml"
        broken_config.write_text("broken = [", encoding="utf-8")
        report: dict[str, Any] = {}
        for path, stage in [(relocatable, "Relocatable"), (executable, "Executable")]:
            before = hashlib.sha256(path.read_bytes()).hexdigest()
            raw = success(
                binary,
                cwd,
                [
                    "--config",
                    str(broken_config),
                    "--color",
                    "always",
                    "disassemble",
                    str(path),
                    "--json",
                ],
            )
            assert b"\x1b" not in raw
            document: dict[str, Any] = json.loads(raw)
            report[stage] = validate_model(document, stage)
            plain = success(
                binary, cwd, ["--color", "never", "disassemble", str(path)]
            ).decode("utf-8")
            color = success(
                binary, cwd, ["--color", "always", "disassemble", str(path)]
            ).decode("utf-8")
            assert plain.startswith(f"native-v1 {stage} entry=") and ANSI.search(color)
            assert ANSI.sub("", color) == plain
            assert hashlib.sha256(path.read_bytes()).hexdigest() == before
            stdin = invoke(
                binary, cwd, ["disassemble", "-", "--json"], path.read_bytes()
            )
            assert stdin.returncode == 0, stdin.stderr
            stdin_document: dict[str, Any] = json.loads(stdin.stdout)
            assert stdin_document["stage"] == stage
            disguised = cwd / "artifact without binary extension.dat"
            disguised.write_bytes(path.read_bytes())
            success(binary, cwd, ["disassemble", str(disguised), "--json"])
        invalid: tuple[tuple[str, bytes, str], ...] = (
            ("not-bytecode.mso", b"plain text", "Not an msync"),
            (
                "truncated.mexe",
                relocatable.read_bytes()[:14],
                "Native IR artifact at byte",
            ),
            (
                "future.mso",
                relocatable.read_bytes()[:8]
                + struct.pack("<H", 65535)
                + relocatable.read_bytes()[10:],
                "Unsupported native IR format",
            ),
        )
        for name, data, expected in invalid:
            path = cwd / name
            path.write_bytes(data)
            result = invoke(binary, cwd, ["disassemble", str(path), "--json"])
            assert result.returncode != 0 and result.stdout == b"", result
            assert expected in result.stderr.decode("utf-8"), result.stderr
            assert path.read_bytes() == data
        assert not (cwd / "must-not-exist").exists()
        report["malformed_artifacts"] = len(invalid)
        report["no_user_effects"] = True
        print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
