#!/usr/bin/env python3
"""Exercise CLI composition, source formatting, and non-replaying REPL state."""

from __future__ import annotations

import argparse
import json
import sqlite3
import subprocess
import tempfile
from collections.abc import Sequence
from contextlib import closing
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def invoke(
    binary: Path, arguments: Sequence[str], cwd: Path, *, code: int = 0
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=cwd,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    assert result.returncode == code, (arguments, result.stdout, result.stderr)
    return result


def compile_exports(binary: Path, cwd: Path) -> None:
    cwd.mkdir()
    help_text = invoke(binary, ["--color", "never", "--help"], cwd).stdout
    assert "compile FILE [--prepare]" in help_text
    assert "bind-ir" not in help_text and "run-ir" not in help_text
    inputs = cwd / "inputs"
    inputs.mkdir()
    (inputs / "first.pick").write_text("first", encoding="utf-8")
    source = cwd / "sync.msync"
    source.write_text(r"./inputs/<.+\.pick> -> ./output/;" + "\n", encoding="utf-8")
    result = invoke(
        binary,
        ["--telemetry-db", "prepare.sqlite", "compile", source.name, "--prepare"],
        cwd,
    )
    assert result.stdout == "Wrote sync.mexe (prepared program).\n"
    assert not (cwd / "sync.mso").exists()
    assert not (cwd / "output").exists(), "Preparing an artifact ran a transfer"
    with closing(sqlite3.connect(str(cwd / "prepare.sqlite"))) as db:
        phases = {name for (name,) in db.execute("SELECT name FROM spans")}
        assert {"prepare", "artifact.write"} <= phases
        assert not {"runtime", "execute"} & phases
        values = set(db.execute("SELECT key, value FROM attributes"))
        assert ("artifact.export_kind", "executable") in values
        assert ("artifact.output_mode", "default") in values

    result = invoke(binary, ["compile", source.name], cwd)
    assert result.stdout == "Wrote sync.mso (portable program).\n"
    result = invoke(binary, ["compile", source.name, "--emit", "hir"], cwd)
    assert result.stdout == "Wrote sync.mhir (typed HIR).\n"
    assert (cwd / "sync.mhir").is_file()
    assert not (cwd / "output").exists()
    (cwd / "wrong-stage.mso").write_bytes((cwd / "sync.mexe").read_bytes())
    for args, guidance in (
        (["compile", source.name, "-o", "unexpected.mexe"], "--prepare"),
        (["compile", source.name, "-o", "unexpected.mhir"], "--emit hir"),
        (["compile", source.name, "--prepare", "--emit", "hir"], "not both"),
        (["compile", "sync.mexe", "-o", "unexpected.mso"], "already prepared"),
        (
            ["compile", "wrong-stage.mso", "--prepare", "-o", "unexpected.mexe"],
            "already prepared",
        ),
        (["compile", "sync.mso"], "overwrite its input"),
        (["compile", "-"], "requires '-o"),
    ):
        assert guidance in invoke(binary, args, cwd, code=1).stderr
    assert not list(cwd.glob("unexpected.*"))

    for flags, stage in (([], "Relocatable"), (["--prepare"], "Executable")):
        compiled = subprocess.run(
            [str(binary), "--no-config", "compile", source.name, *flags, "-o", "-"],
            cwd=cwd,
            check=True,
            capture_output=True,
            timeout=30,
        )
        assert compiled.stdout.startswith(b"MSYNCIR\0")
        viewed = subprocess.run(
            [str(binary), "--no-config", "disassemble", "-", "--json"],
            cwd=cwd,
            input=compiled.stdout,
            check=True,
            capture_output=True,
            timeout=30,
        )
        assert json.loads(viewed.stdout)["stage"] == stage

    source.unlink()
    (inputs / "later.pick").write_text("later", encoding="utf-8")
    invoke(binary, ["run", "sync.mexe"], cwd)
    output = cwd / "output"
    assert {f.name: f.read_text(encoding="utf-8") for f in output.iterdir()} == {
        "first.pick": "first"
    }
    invoke(binary, ["run", "sync.mso"], cwd)
    assert {f.name: f.read_text(encoding="utf-8") for f in output.iterdir()} == {
        "first.pick": "first",
        "later.pick": "later",
    }
    print("PASS: default export, direct prepare, source-free run, and binary stdout")


def repl(binary: Path, cwd: Path) -> None:
    process = subprocess.Popen(
        [str(binary), "--no-config", "repl"],
        cwd=cwd,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    assert process.stdin is not None and process.stdout is not None
    try:
        process.stdin.write('let target = repl-output; "first" -> target; "ready"\n')
        process.stdin.flush()
        assert process.stdout.readline().strip() == "ready"
        assert (cwd / "repl-output").read_text() == "first"
        (cwd / "repl-output").unlink()
        process.stdin.write("fn identity(value) { value };\nidentity(target)\n/quit\n")
        process.stdin.flush()
        stdout, stderr = process.communicate(timeout=30)
        assert process.returncode == 0, stderr
        assert stdout.strip() == "repl-output", (stdout, stderr)
        assert not (cwd / "repl-output").exists(), "An earlier transfer was replayed"
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()


def run(binary: Path, cwd: Path) -> None:
    source = cwd / "source.msync"
    source.write_text(
        "scope sample { item: input.txt, copy: fn(a, b) { a -> b }, };\n"
        "let value: Path = sample::item;\n"
        "sample::copy(value, output.txt);\n",
        encoding="utf-8",
    )
    (cwd / "input.txt").write_text("literal 可莉\n", encoding="utf-8")
    invoke(binary, ["check", str(source)], cwd)
    assert not (cwd / "output.txt").exists()
    invoke(binary, ["run", str(source)], cwd)
    assert (cwd / "output.txt").read_bytes() == (cwd / "input.txt").read_bytes()
    literal = cwd / "literal.msync"
    literal.write_text(
        '"  first  \n second \\n " -> formatted.txt;\n', encoding="utf-8"
    )
    invoke(binary, ["fmt", str(literal)], cwd)
    first = literal.read_bytes()
    invoke(binary, ["fmt", str(literal), "--check"], cwd)
    invoke(binary, ["fmt", str(literal)], cwd)
    assert literal.read_bytes() == first
    invoke(binary, ["run", str(literal)], cwd)
    assert (cwd / "formatted.txt").read_text() == "  first  \n second \\n "
    compile_exports(binary, cwd / "exports")
    repl(binary, cwd)
    print(
        "PASS: global options, scope functions, lossless formatting, persistent non-replaying REPL"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--export-only", action="store_true")
    args = parser.parse_args()
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="cli-journeys-", dir=temporary) as name:
        directory = Path(name)
        if args.export_only:
            compile_exports(args.binary.resolve(), directory / "exports")
        else:
            run(args.binary.resolve(), directory)


if __name__ == "__main__":
    main()
