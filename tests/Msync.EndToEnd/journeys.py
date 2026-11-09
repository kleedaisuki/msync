#!/usr/bin/env python3
"""Exercise CLI composition, source formatting, and non-replaying REPL state."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def invoke(
    binary: Path, arguments: Sequence[str], cwd: Path
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
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)
    return result


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
    artifact = subprocess.run(
        [str(binary), "--no-config", "compile", str(source), "-o", "-"],
        cwd=cwd,
        check=True,
        capture_output=True,
        timeout=30,
    )
    assert artifact.stdout.startswith(b"MSYNCBC\0"), (
        "Binary stdout contains text or a banner"
    )
    repl(binary, cwd)
    print(
        "PASS: global options, scope functions, lossless formatting, binary stdout, persistent non-replaying REPL"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    args = parser.parse_args()
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="cli-journeys-", dir=temporary) as name:
        directory = Path(name)
        run(args.binary.resolve(), directory)


if __name__ == "__main__":
    main()
