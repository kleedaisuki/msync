#!/usr/bin/env python3
"""Observe endpoint display without changing native path projection or doing IO."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]


def invoke(
    binary: Path, directory: Path, arguments: list[str], source: str | None = None
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "--color", "never", *arguments],
        cwd=directory,
        input=source,
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
        timeout=30,
    )


def run(binary: Path, directory: Path) -> dict[str, Any]:
    result = invoke(
        binary,
        directory,
        ["repl"],
        "ssh://forge @ ./item\n"
        "$wsl @ ./item\n"
        "$local @ ./item\n"
        "($local @ ./item) as String\n"
        '(ssh://forge @ "dir with space") as String\n'
        'let invalid = ssh://forge @ "not-a-number";\n'
        "invalid as Number\n"
        "/quit\n",
    )
    assert result.returncode == 0, result
    lines = result.stdout.splitlines()
    assert len(lines) == 5, result.stdout
    assert lines[:2] == ["ssh://forge @ ./item", "wsl:// @ ./item"], lines
    assert lines[2] == lines[3] and " @ " not in lines[2], lines
    assert lines[4] == "dir with space", lines
    assert "Cannot cast Path 'ssh://forge @ not-a-number' to Number" in result.stderr
    assert "ssh://forge::" not in result.stderr

    source = directory / "display.msync"
    source.write_text(
        'let invalid = ssh://forge @ "not-a-number"; invalid as Number;\n',
        encoding="utf-8",
    )
    relocatable = directory / "display.mso"
    executable = directory / "display.mexe"
    compiled = invoke(
        binary, directory, ["compile", str(source), "-o", str(relocatable)]
    )
    assert compiled.returncode == 0, compiled.stderr
    bound = invoke(
        binary, directory, ["bind-ir", str(relocatable), "-o", str(executable)]
    )
    assert bound.returncode == 0, bound.stderr
    source.unlink()
    for artifact in (relocatable, executable):
        failed = invoke(binary, directory, ["run-ir", str(artifact)])
        assert failed.returncode == 1, failed
        assert (
            "Cannot cast Path 'ssh://forge @ not-a-number' to Number" in failed.stderr
        )
        inspected = invoke(binary, directory, ["disassemble", str(artifact), "--json"])
        assert inspected.returncode == 0, inspected.stderr
        document: dict[str, Any] = json.loads(inspected.stdout)
        assert 'connection(Ssh, "forge")' in document["text"]
        assert "ssh://forge::" not in document["text"]
        assert any(
            "connection @ path" in hover["markdown"]
            and "scope::member" in hover["markdown"]
            for hover in document["hovers"]
        )
    return {
        "repl_display": ["ssh", "wsl", "unqualified-local"],
        "native_string_projection_unchanged": True,
        "diagnostic_stages": ["repl", "mso", "mexe"],
        "artifact_provenance_and_hovers": True,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    scratch = ROOT / ".temp" / "path-display-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="display-", dir=scratch) as temporary:
        result = run(arguments.binary.resolve(), Path(temporary))
    encoded = json.dumps(result, indent=2)
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(encoded + "\n", encoding="utf-8")
    print(encoded)


if __name__ == "__main__":
    main()
