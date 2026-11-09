#!/usr/bin/env python3
"""Exercise parser-owned REPL continuation and executed-prefix recovery via real processes."""

from __future__ import annotations

import argparse
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def quoted(value: str) -> str:
    return '"' + value.replace('"', '""') + '"'


def invoke(
    binary: Path, cwd: Path, source: str, *, arguments: tuple[str, ...] = ()
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "repl", *arguments],
        cwd=cwd,
        input=source,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=20,
        check=False,
    )


def run(binary: Path, cwd: Path) -> None:
    source = 'fn comment() { # } " [ (\n    "comment-ok"\n};\ncomment()\nfn selector() {\n    match ./input { <[}"]+> => { "wrong" }, _ => { "regex-ok" } }\n};\nselector()\nfn multiline() { "first\nsecond" };\nmultiline()\nlet invalid = ;\n"recovered"\n/quit\n'
    result = invoke(binary, cwd, source)
    assert result.returncode == 0, result.stderr
    assert result.stdout.splitlines() == [
        "comment-ok",
        "regex-ok",
        "first",
        "second",
        "recovered",
    ], (result.stdout, result.stderr)
    assert "Expected an expression" in result.stderr, result.stderr
    incomplete = invoke(binary, cwd, "fn unfinished() { # }\n")
    assert incomplete.returncode == 1, (
        incomplete.returncode,
        incomplete.stdout,
        incomplete.stderr,
    )
    assert "Expected '}'" in incomplete.stderr
    child = quoted(str(Path(sys.executable).resolve()))
    failure = f'let waiting = ({child})("-c", "print(123)");'
    recovery = (
        "\n".join(
            [
                'let waiting = "prior";',
                "let optional? = null;",
                'let kept = "prefix"; '
                + failure
                + ' let never_initialized = "not-run";',
                "waiting",
                "kept",
                'if optional? { "real-null" } else { "wrong" }',
                'let waiting = "new-prefix"; ' + failure,
                "waiting",
                "/quit",
            ]
        )
        + "\n"
    )
    result = invoke(binary, cwd, recovery, arguments=("--spool-limit", "0"))
    assert result.returncode == 0, result.stderr
    assert result.stdout.splitlines() == [
        "prior",
        "prefix",
        "real-null",
        "new-prefix",
    ], (result.stdout, result.stderr)
    assert result.stderr.count("spool limit") == 2, result.stderr
    (cwd / "input.txt").write_text("captured origin", encoding="utf-8")
    (cwd / "sub").mkdir()
    relocation = invoke(
        binary,
        cwd,
        "let origin = ./input.txt;\n/cd sub\norigin -> ./copied.txt;\n/pwd\n/quit\n",
    )
    assert relocation.returncode == 0, relocation.stderr
    assert (cwd / "sub/copied.txt").read_text(encoding="utf-8") == "captured origin"
    assert str(cwd / "sub") in relocation.stdout
    print(
        "PASS: source-owned continuation, complete-error recovery, incomplete EOF, initialized prefix/shadow/null recovery, captured paths across /cd"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    args = parser.parse_args()
    directory = ROOT / ".temp/product-latency"
    directory.mkdir(parents=True, exist_ok=True)
    fixture = Path(tempfile.mkdtemp(prefix="submission-", dir=directory))
    run(args.binary.resolve(), fixture)


if __name__ == "__main__":
    main()
