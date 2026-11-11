#!/usr/bin/env python3
"""Run the shipped console recipe with real redirected standard streams."""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import tempfile
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
INPUT: bytes = b"3\r\n1\n3\n\n2.5"
NEWLINE: bytes = os.linesep.encode("ascii")
OUTPUT: bytes = NEWLINE.join((b"1.00", b"2.50", b"3.00", b""))
ERROR: bytes = b"ignored blank line" + NEWLINE


def run(
    binary: Path, cwd: Path, *arguments: str, data: bytes = b""
) -> tuple[bytes, bytes]:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "--color", "never", *arguments],
        cwd=cwd,
        input=data,
        capture_output=True,
        timeout=90,
        check=False,
    )
    if result.returncode:
        raise AssertionError(
            (arguments, result.returncode, result.stdout, result.stderr)
        )
    return result.stdout, result.stderr


def verify(binary: Path, directory: Path) -> None:
    guide: bytes
    guide, _ = run(binary, directory, "skill", "library", "console")
    examples: list[str] = re.findall(
        r"^```msync\n(.*?)^```",
        guide.decode("utf-8").replace("\r\n", "\n"),
        re.MULTILINE | re.DOTALL,
    )
    if len(examples) != 1:
        raise AssertionError("The console topic must supply the runnable recipe.")
    source: Path = directory / "console.msync"
    source.write_text(examples[0], encoding="utf-8", newline="\n")

    def execute(*arguments: str) -> None:
        actual: tuple[bytes, bytes] = run(binary, directory, *arguments, data=INPUT)
        if actual != (OUTPUT, ERROR):
            raise AssertionError((arguments, actual, (OUTPUT, ERROR)))

    execute("run", source.name, "-O2")
    run(
        binary, directory, "compile", source.name, "--emit", "hir", "-o", "console.mhir"
    )
    source.unlink()
    execute("run", "console.mhir", "-O2")
    run(binary, directory, "compile", "console.mhir", "-O2", "-o", "console.mso")
    execute("run-ir", "console.mso")
    run(binary, directory, "bind-ir", "console.mso", "-o", "console.mexe")
    execute("run-ir", "console.mexe")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    temporary: Path = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="console-workflow-", dir=temporary) as work:
        verify(args.binary.resolve(), Path(work))
    print(
        "PASS: embedded console recipe, exact stdout and stderr, four execution stages"
    )


if __name__ == "__main__":
    main()
