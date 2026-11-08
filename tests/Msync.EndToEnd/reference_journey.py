#!/usr/bin/env python3
"""Follow printed function constraints and help options through the actual CLI."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def invoke(
    binary: Path, directory: Path, *arguments: str
) -> subprocess.CompletedProcess[str]:
    environment: dict[str, str] = dict(os.environ)
    environment["NO_COLOR"] = "1"
    return subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=directory,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )


def run(binary: Path, directory: Path) -> dict[str, int]:
    guide = invoke(binary, directory, "help", "syntax")
    assert guide.returncode == 0, guide.stderr
    for expected in (
        "-> Bool",
        "fn(file: Path) -> Path",
        "'-> Type'",
        "'-> Path?'",
        ".codex is a legal binding name",
    ):
        assert expected in guide.stdout, (expected, guide.stdout)
    type_section = guide.stdout.split("\nTYPES\n", 1)[1]
    for name in (
        "Bool",
        "String",
        "Path",
        "Function",
        "Scope",
        "Connection",
        "Process",
        "Null",
        "Number",
        "Type",
        "Range<T>",
        "Any",
    ):
        assert name in type_section, (name, type_section)
    functions = invoke(binary, directory, "skill", "language", "functions")
    assert functions.returncode == 0, functions.stderr
    assert "both named and anonymous functions" in functions.stdout
    assert "Return constraints apply to both block tails" in functions.stdout
    assert "fn locate(file: Path) -> Path?" in functions.stdout

    for arguments in (("--help",), ("help", "syntax"), ("skill",)):
        invalid = invoke(binary, directory, "--color", "purple", *arguments)
        assert invalid.returncode != 0 and not invalid.stdout, invalid
        assert "Unknown color policy 'purple'" in invalid.stderr, invalid.stderr
        assert "auto, always, or never" in invalid.stderr
    for policy in ("auto", "always", "never"):
        valid = invoke(binary, directory, "--color", policy, "help", "syntax")
        assert valid.returncode == 0 and valid.stdout == guide.stdout, valid

    payload = b"typed return path\x00\xff\r\n"
    (directory / "input.bin").write_bytes(payload)
    program = directory / "constrained-returns.msync"
    program.write_text(
        "fn named(value: String) -> String { value };\n"
        "let anonymous = fn(value: String) -> String { return value; };\n"
        'named("named") -> ./named-result;\n'
        'anonymous("anonymous") -> ./anonymous-result;\n'
        "fn locate(file: Path) -> Path? {\n"
        "    if exists file { file } else { null }\n"
        "};\n"
        "let selected? = locate(./input.bin);\n"
        "if selected is not null { selected -> ./nullable-result };\n"
        'fn as_path() -> Path { "./input.bin" };\n'
        "as_path() -> ./converted-result;\n",
        encoding="utf-8",
    )
    result = invoke(binary, directory, "check", str(program))
    assert result.returncode == 0, result.stderr
    assert not (directory / "named-result").exists()
    formatted = invoke(binary, directory, "fmt", str(program), "--stdout")
    assert formatted.returncode == 0 and "-> Path?" in formatted.stdout
    program.write_text(formatted.stdout, encoding="utf-8")
    result = invoke(binary, directory, "run", str(program))
    assert result.returncode == 0, result.stderr
    assert (directory / "named-result").read_bytes() == b"named"
    assert (directory / "anonymous-result").read_bytes() == b"anonymous"
    assert (directory / "nullable-result").read_bytes() == payload
    assert (directory / "converted-result").read_bytes() == payload

    invalid_program = directory / "mismatched-return.msync"
    invalid_program.write_text(
        'fn bad() -> Bool { "not Bool" }; bad();\n', encoding="utf-8"
    )
    mismatch = invoke(binary, directory, "check", str(invalid_program))
    assert mismatch.returncode != 0 and "E0203" in mismatch.stderr, mismatch
    assert "Bool" in mismatch.stderr and "String" in mismatch.stderr
    return {"reference_surfaces": 2, "color_policy_cases": 6, "return_contracts": 5}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments = parser.parse_args()
    scratch = ROOT / ".temp/reference-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="read-", dir=scratch) as temporary:
        report = run(arguments.binary.resolve(), Path(temporary))
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
