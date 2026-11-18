#!/usr/bin/env python3
"""Bind an executable, detach it, and check inlined locations and diagnostics."""

from __future__ import annotations

import argparse
import os
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]

LOCATIONS: str = """\
./origin.txt -> ./planned.txt;
let state = 3;
fn step(delta: Number) -> Number {
    state = state + delta;
    state
};
fn less(left: Number, right: Number) -> Bool { left < right };
let first = step(2);
./origin.txt -> ./dest.txt;
let second = step(4);
$std::io::println(first as String);
$std::io::println(second as String);
$std::io::println(state as String);
$std::io::println(less(first, second) as String);
"""
DIAGNOSTIC: str = """\
fn decode(flag: Bool) -> Number {
    let token = if flag { "not-a-number" } else { "1" };
    token as Number
};
let line? = $std::io::read_line();
$std::io::println(decode(if line is null { false } else { line == "bad" }) as String);
"""


def invoke(
    binary: Path, work: Path, *arguments: str, data: str = "", status: int = 0
) -> str:
    run: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "--color", "never", *arguments],
        cwd=work, check=False,
        input=data,
        text=True,
        capture_output=True,
        timeout=30,
        env=dict(os.environ, MSYNC_JIT="off"),
    )
    assert run.returncode == status, (arguments, run.returncode, run.stdout, run.stderr)
    return run.stdout if status == 0 else run.stderr


def entry(disassembly: str) -> str:
    return disassembly.split("\nfn 0 ", 1)[1].split("\nfn ", 1)[0]


def bind(binary: Path, work: Path, source: str, calls: int) -> str:
    work.mkdir()
    (work / "main.msync").write_text(source, encoding="utf-8")
    payload: bytes = b"location-preserving transfer\x00\xff\n"
    (work / "origin.txt").write_bytes(payload)
    invoke(binary, work, "compile", "-O2", "main.msync", "-o", "main.mso")
    original: str = entry(invoke(binary, work, "disassemble", "main.mso"))
    assert original.count(" = Call a=") == calls, original
    invoke(binary, work, "bind-ir", "main.mso", "-o", "main.mexe")
    bound: str = entry(invoke(binary, work, "disassemble", "main.mexe"))
    assert " = Call a=" not in bound, bound
    assert not (work / "dest.txt").exists()
    assert not (work / "planned.txt").exists()
    (work / "main.msync").unlink()
    (work / "main.mso").unlink()
    return bound


def verify(binary: Path, work: Path) -> None:
    alias: Path = work / "alias"
    bound: str = bind(binary, alias, LOCATIONS, 3)
    assert bound.count(" BindLocation ") == 2, bound
    assert " ExecutePlan " in bound, bound
    assert invoke(binary, alias, "run", "main.mexe") == "5\n9\n9\ntrue\n"
    assert (alias / "dest.txt").read_bytes() == (alias / "origin.txt").read_bytes()
    assert (alias / "planned.txt").read_bytes() == (alias / "origin.txt").read_bytes()

    failure: Path = work / "failure"
    bind(binary, failure, DIAGNOSTIC, 1)
    message: str = invoke(binary, failure, "run", "main.mexe", data="bad\n", status=1)
    span: str = "token as Number"
    offset: int = DIAGNOSTIC.index(span)
    assert f"main.msync (source offset {offset}..{offset + len(span)})" in message, message


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    with TemporaryDirectory(prefix="executable-inlining-", dir=ROOT / ".temp") as directory:
        verify(binary, Path(directory))
    print("PASS: detached .mexe inlining retains mutable locations and callee source spans")


if __name__ == "__main__":
    main()
