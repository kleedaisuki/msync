#!/usr/bin/env python3
"""Exercise source sugar through the CLI, formatter, and detached artifacts."""

from __future__ import annotations

import argparse
import os
import subprocess
from collections.abc import Sequence
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """concept HasPath { path: String, };
concept HasCounter { count?: Number, };
let path = "report";
let record = { :path, count?: 1 } satisfies (HasPath and HasCounter);
let missing: Number or Null = null;
let narrowed: (Number or String or Null) and not (String or Null) = 12;
$std::io::println(narrowed as String);
let seen = { state: 0 };
let get = fn() {
    seen::state = seen::state * 10 + 1;
    missing
};
let fallback = fn() => 7;
$std::io::println((get() ?? fallback()) as String);
let required = fn(record: Scope?) {
    $std::io::println((record?::count ?? 0) as String);
};
required(record);
required(null);
let properties = if true { path: "branch" } else { path: "other" };
$std::io::println(properties::path);
record::count ??= 22;
missing ??= 9;
$std::io::println((record::count ?? 0) as String);
$std::io::println((missing ?? 0) as String);
$std::io::println(record::path);
let capture = fn(n: Number) {
    seen::state = seen::state * 10 + n;
    n
};
let difference = fn(left: Number, right: Number) => left - right;
$std::io::println(difference(right: capture(2), left: capture(3)) as String);
$std::io::println(seen::state as String);
let data = [for n in 1..5 if n > 2 => capture(n)];
$std::io::println(seen::state as String);
$std::io::println($std::ranges::first(data) as String);
$std::io::println(seen::state as String);
fn sum(values: Range<Number>) -> Number {
    let total = 0;
    for n in values { total = total + n; };
    total
};
$std::io::println(sum(1..4) as String);
$std::io::println(sum(4..1) as String);
let limit = 9007199254740992;
$std::io::println(sum(limit..limit) as String);
"""
EXPECTED: list[str] = [
    "12", "7", "1", "0", "branch", "1", "9", "report", "1", "123", "123", "3", "1233",
    "10", "0", "9007199254740992",
]


def invoke(binary: Path, cwd: Path, args: Sequence[str], policy: str = "off") -> str:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [binary, "--no-config", "--no-telemetry", *args],
        cwd=cwd,
        env={**os.environ, "MSYNC_JIT": policy},
        capture_output=True,
        check=False,
        text=True,
        timeout=30,
    )
    assert result.returncode == 0, (args, policy, result.stderr)
    return result.stdout


def journey(binary: Path, work: Path) -> None:
    unit: Path = work / "sugar.msync"
    unit.write_text(SOURCE, encoding="utf-8")
    formatted: str = invoke(binary, work, ["fmt", unit.name, "--stdout"])
    unit.write_text(formatted, encoding="utf-8")
    assert invoke(binary, work, ["fmt", unit.name, "--stdout"]) == formatted
    assert invoke(binary, work, ["run", unit.name]).splitlines() == EXPECTED
    invoke(binary, work, ["compile", unit.name, "-o", "sugar.mso"])
    unit.unlink()
    invoke(binary, work, ["bind-ir", "sugar.mso", "-o", "sugar.mexe"])
    for policy in ("off", "force"):
        for artifact in ("sugar.mso", "sugar.mexe"):
            assert invoke(binary, work, ["run", artifact], policy).splitlines() == EXPECTED


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(prefix="source-sugar-", dir=ROOT / ".temp") as directory:
        journey(args.binary.resolve(), Path(directory))
    print("Source sugar: formatted source, portable and bound artifact journeys passed.")


if __name__ == "__main__":
    main()
