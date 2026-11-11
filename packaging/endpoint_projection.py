#!/usr/bin/env python3
"""Check endpoint path provenance without executing a remote operation."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = """let remote = ssh://unused-endpoint-projection;
let home = ~/.codex;
let relative = ./dir/file;
let home_text: String = remote @ home;
let relative_text: String = remote @ relative;
let direct_home: String = remote @ ~/.codex;
let direct_relative: String = remote @ ./dir/file;
home_text -> home.txt;
relative_text -> relative.txt;
direct_home -> direct-home.txt;
direct_relative -> direct-relative.txt;
"""
EXPECTED = {
    "home.txt": b"~/.codex",
    "relative.txt": b"./dir/file",
    "direct-home.txt": b"~/.codex",
    "direct-relative.txt": b"./dir/file",
}


def invoke(binary: Path, directory: Path, *arguments: str) -> None:
    result = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)


def run(binary: Path, directory: Path) -> None:
    source = directory / "projection.msync"
    source.write_text(SOURCE, encoding="utf-8")
    invoke(binary, directory, "check", source.name)
    invoke(binary, directory, "compile", source.name, "-o", "projection.mso")
    invoke(binary, directory, "bind-ir", "projection.mso", "-o", "projection.mexe")
    assert not any((directory / name).exists() for name in EXPECTED)
    for entry in (source.name, "projection.mso", "projection.mexe"):
        invoke(binary, directory, "run", entry)
        for name, expected in EXPECTED.items():
            path = directory / name
            assert path.read_bytes() == expected, (
                entry,
                name,
                path.read_bytes(),
                expected,
            )
            path.unlink()
        if source.exists():
            source.unlink()
    print(
        "PASS: source/.mso/.mexe home/relative endpoint projection before and after path joins"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    args = parser.parse_args()
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="endpoint-projection-", dir=temporary
    ) as name:
        run(args.binary.resolve(), Path(name))


if __name__ == "__main__":
    main()
