#!/usr/bin/env python3
"""Score real scope-place behavior on source, object and source-free executable routes."""

from __future__ import annotations

import argparse
import os
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]
PROGRAM: str = """
let io = $std::io;
let flag = io::read_line() is null;
let row = if flag { object { alpha: 0, key: 11 } } else { object { key: 22, zed: 0 } };
let alias = row;
row::key = row::key + 3;
io::println(alias::key as String);
concept Keyed { key: Number };
fn update(value: Keyed) {
    let first = value::key;
    value = object { key: 29, extra: true };
    first + value::key
};
io::println(update(row) as String);
let captured = row;
fn read { captured::key };
io::println(read() as String);
io::println($std::ranges::front([row])::key as String);
let nested = object { sample: object { value: 19 } };
io::println(nested::sample::value as String);
let count = 0;
fn tick {
    global count;
    count = count + 1;
    count
};
fn statics {
    object Counter { first: tick(), second: tick() };
    Counter::second = Counter::second + 1;
    Counter::first * 10 + Counter::second
};
io::println(statics() as String);
io::println(statics() as String);
io::println(count as String);
"""


def run(binary: Path, folder: Path, args: list[str], input_data: bytes = b"") -> bytes:
    env: dict[str, str] = dict(os.environ)
    env["MSYNC_JIT"] = "off"
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [binary, "--no-config", "--no-telemetry", *args],
        cwd=folder,
        env=env,
        input=input_data,
        capture_output=True,
        timeout=60,
        check=False,
    )
    assert not result.returncode and not result.stderr, (
        result.args,
        result.returncode,
        result.stderr,
    )
    return result.stdout


def score(binary: Path, folder: Path, artifact: str, options: list[str]) -> None:
    for input_data, original in ((b"", 11), (b"other\n", 22)):
        value: int = original + 3
        expected: list[bytes] = [
            str(item).encode()
            for item in (value, value + 29, value, value, 19, 13, 14, 2)
        ]
        actual: bytes = run(binary, folder, [*options, "run", artifact], input_data)
        assert actual.splitlines() == expected, (artifact, expected, actual)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--level", type=int, action="append")
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    (ROOT / ".temp").mkdir(exist_ok=True)
    with TemporaryDirectory(prefix="typed-place-journey-", dir=ROOT / ".temp") as temp:
        folder: Path = Path(temp)
        for level in args.level or [0, 2]:
            source: Path = folder / "places.msync"
            source.write_text(PROGRAM, encoding="utf-8", newline="\n")
            score(binary, folder, source.name, [f"-O{level}"])
            run(
                binary,
                folder,
                [f"-O{level}", "compile", source.name, "-o", "places.mso"],
            )
            source.unlink()
            score(binary, folder, "places.mso", [])
            run(binary, folder, ["bind-ir", "places.mso", "-o", "places.mexe"])
            score(binary, folder, "places.mexe", [])
            print(f"PASS O{level}: source, detached .mso and detached .mexe")


if __name__ == "__main__":
    main()
