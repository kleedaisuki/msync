#!/usr/bin/env python3
"""Read real source-free bytecode without executing, rebinding or modifying it."""

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


def text_range(lines: list[str], span: dict[str, Any]) -> str:
    start, end = span["start"], span["end"]
    assert 0 <= start["line"] <= end["line"] < len(lines), span
    for point in (start, end):
        assert (
            0
            <= point["character"]
            <= len(lines[point["line"]].encode("utf-16-le")) // 2
        ), (point, lines[point["line"]])
    if start["line"] != end["line"]:
        return ""
    row: int = start["line"]
    encoded = lines[row].encode("utf-16-le")
    first: int = start["character"]
    last: int = end["character"]
    return encoded[2 * first : 2 * last].decode("utf-16-le")


def validate_model(document: dict[str, Any], stage: str) -> dict[str, int]:
    assert document["schemaVersion"] == 1
    assert document["formatVersion"] == 5
    assert document["languageVersion"] == "0.8.0"
    assert document["stage"] == stage
    assert document["optimizationLevel"] == 0
    text: str = document["text"]
    assert "read-only disassembly" in text
    assert "does not bind, optimize or execute" in text
    assert "@c" in text and "@f" in text and "block @b" in text
    assert "fs.exists" in text and "Transfer" in text
    assert "arguments" in text and "captures" in text and "Path" in text
    assert "世界" in text and "\\n" in text
    assert "GetSingleton" in text and "SetSingleton" in text
    assert "declaration #" in text and "Remainder" in text
    lines = text.splitlines()
    symbols = 0

    def symbol(item: dict[str, Any]) -> None:
        nonlocal symbols
        symbols += 1
        text_range(lines, item["range"])
        assert text_range(lines, item["selectionRange"]), item
        assert (
            item["range"]["start"]["line"]
            <= item["selectionRange"]["start"]["line"]
            <= item["range"]["end"]["line"]
        ), item
        for child in item.get("children", []):
            symbol(child)

    for item in document["symbols"]:
        symbol(item)
    targets = 0
    sources = 0
    for item in document["references"]:
        spelling = text_range(lines, item["range"])
        assert spelling, item
        if "targetLine" in item:
            targets += 1
            target = lines[item["targetLine"]]
            assert target[item["targetCharacter"] :].startswith(spelling), (
                item,
                spelling,
                target,
            )
        else:
            sources += 1
            assert item["sourceStart"] >= 0 and item["sourceLength"] >= 0
            assert item["sourceName"]
    for item in document["hovers"]:
        assert text_range(lines, item["range"]) and item["markdown"]
    for opcode, explanation in (
        ("GetSingleton", "shared instance"),
        ("SetSingleton", "canonical session-owned singleton"),
        ("Remainder", "truncating remainder"),
    ):
        assert any(
            text_range(lines, item["range"]) == opcode
            and explanation in item["markdown"]
            for item in document["hovers"]
        ), (opcode, document["hovers"])
    assert targets > 0 and sources > 0
    if stage == "executable":
        assert "context {" in text and "cwd =" in text and "home =" in text
    else:
        assert "context {" not in text
    return {"symbols": symbols, "internal_references": targets, "source_spans": sources}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    parent = ROOT / ".temp" / "artifact-disassembly"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="查看 bytecode ", dir=parent) as directory:
        cwd = Path(directory)
        source = cwd / "source with spaces.msync"
        source.write_text(
            "scope Demo { copy: fn(file: Path, destination: Path) -> Bool { file -> destination } };\n"
            'let message = "hello ""Klee""\n世界";\n'
            "if exists ./missing { message -> ./must-not-exist; };\n"
            "Demo::copy(./missing, ./must-not-exist);\n"
            "fn state() { scope Shared { count: 7 }; Shared };\n"
            "let first = state(); let second = state();\n"
            "let remainder = first::count % 3;\n",
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
        for path, stage in [(relocatable, "relocatable"), (executable, "executable")]:
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
            assert plain == document["text"] and ANSI.search(color)
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
            ("truncated.mexe", relocatable.read_bytes()[:14], "disassemble"),
            (
                "future.mso",
                relocatable.read_bytes()[:8]
                + struct.pack("<H", 65535)
                + relocatable.read_bytes()[10:],
                "Unsupported msync bytecode format",
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
