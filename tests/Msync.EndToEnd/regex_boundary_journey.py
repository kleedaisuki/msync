#!/usr/bin/env python3
"""Run compact regex operators, named captures and native artifacts end to end."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def command(binary: Path, cwd: Path, *arguments: str) -> None:
    result = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=cwd,
        capture_output=True,
        text=True,
        check=False,
        timeout=20,
    )
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)


def run(binary: Path, fixture: Path) -> None:
    source = fixture / "compact.msync"
    source.write_text(
        'match "foo" {<foo>=>{"compact"},_=>{"wrong"}} -> ./compact;\nmatch "value" {<(?<name>)>=>{"named"},_=>{"wrong"}} -> ./named;\nmatch "aa" {<(?<name>a)(?:\\k<name>)>=>{"backref"},_=>{"wrong"}} -> ./backref;\n./input/<.+>->./selected/;\n',
        encoding="utf-8",
    )
    (fixture / "input").mkdir()
    (fixture / "input/item.txt").write_text("selected", encoding="utf-8")
    command(binary, fixture, "check", str(source))
    command(binary, fixture, "compile", str(source), "-o", str(fixture / "compact.mso"))
    command(
        binary,
        fixture,
        "bind-ir",
        str(fixture / "compact.mso"),
        "-o",
        str(fixture / "compact.mexe"),
    )
    assert not (fixture / "selected").exists()
    for target in (source, fixture / "compact.mso", fixture / "compact.mexe"):
        command(binary, fixture, "run", str(target))
        assert (fixture / "compact").read_text() == "compact"
        assert (fixture / "named").read_text() == "named"
        assert (fixture / "backref").read_text() == "backref"
        assert (fixture / "selected/item.txt").read_text() == "selected"
        if target == source:
            source.unlink()
    print(
        "PASS: compact regex fat-arrow/transfer boundaries, empty named capture and named backreference in source/mso/mexe"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    args = parser.parse_args()
    root = ROOT / ".temp/product-latency"
    root.mkdir(parents=True, exist_ok=True)
    fixture = Path(tempfile.mkdtemp(prefix="regex-boundary-", dir=root))
    run(args.binary.resolve(), fixture)


if __name__ == "__main__":
    main()
