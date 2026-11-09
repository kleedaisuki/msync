#!/usr/bin/env python3
"""Exercise runtime Number chains and escaping objects through all CLI stages."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import tempfile
from pathlib import Path

from stdlib_experience_journey import accept, invoke

ROOT = Path(__file__).resolve().parents[2]

PROGRAM: str = r"""
let seed = $std::io::read_text(./seed.txt) as Number;
let total = seed;
for index in $std::ranges::iota(0, 512) {
    total = ((total + index) * 2 - index) / 2;
};
(total as String) -> ./total.txt;
let zero = seed - seed;
let negative_zero = -zero;
let nan = zero / zero;
let infinity = seed / zero;
if $std::math::signbit(negative_zero)
    and $std::math::signbit(negative_zero % seed)
    and nan != nan and not (nan <= zero) and not (nan > zero)
    and infinity > seed and negative_zero == zero {
    "ieee" -> ./ieee.txt;
};
fn Accumulator(initial: Number) {
    let stored = initial;
    scope {
        add: fn(amount: Number) {
            stored = stored + amount;
            stored
        },
        read: fn { stored }
    }
};
let objects = $std::ranges::materialize(
    $std::ranges::transform($std::ranges::iota(0, 64), Accumulator)
);
let accumulated = zero;
let aliases_valid = true;
for object in objects {
    let alias = object;
    let changed = alias::add(seed);
    aliases_valid = aliases_valid and object::read() == changed;
    accumulated = accumulated + changed;
};
(accumulated as String) -> ./objects.txt;
if aliases_valid { "shared-cells" -> ./aliases.txt; };
let first = Accumulator(seed);
let second = Accumulator(seed);
first::add(seed);
if first::read() == seed * 2
    and second::read() == seed {
    "fresh" -> ./fresh.txt;
};
let selected = fn(value: Number) { value * 2 + seed };
fn replace() {
    global selected;
    selected = fn(value: Number) { value * 3 + seed };
    seed
};
let original = selected(replace());
let replacement = selected(seed);
if original == 21 and replacement == 28 {
    "callee-before-arguments" -> ./callee.txt;
};
acquire;
"""

OUTPUTS: dict[str, bytes] = {
    "total.txt": b"65415",
    "ieee.txt": b"ieee",
    "objects.txt": b"2464",
    "aliases.txt": b"shared-cells",
    "fresh.txt": b"fresh",
    "callee.txt": b"callee-before-arguments",
}


def execute(binary: Path, parent: Path, levels: list[int]) -> int:
    executions: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            (directory / "seed.txt").write_text("7", encoding="utf-8")
            source: Path = directory / "number objects.msync"
            source.write_text(PROGRAM, encoding="utf-8")
            if stage == "source":
                accept(
                    invoke(binary, directory, "run", str(source), f"-O{level}"), stage
                )
            else:
                artifact: Path = source.with_suffix(".mso")
                accept(
                    invoke(
                        binary,
                        directory,
                        "compile",
                        str(source),
                        "-o",
                        str(artifact),
                        f"-O{level}",
                    ),
                    "compile",
                )
                if stage == "executable":
                    executable: Path = source.with_suffix(".mexe")
                    accept(
                        invoke(
                            binary,
                            directory,
                            "bind-ir",
                            str(artifact),
                            "-o",
                            str(executable),
                            "--source-file",
                            str(source),
                        ),
                        "bind",
                    )
                    artifact = executable
                source.unlink()
                launch: Path = (
                    directory / "other launch" if stage == "executable" else directory
                )
                launch.mkdir(exist_ok=True)
                accept(invoke(binary, launch, "run-ir", str(artifact)), stage)
            for name, expected in OUTPUTS.items():
                actual: bytes = (directory / name).read_bytes()
                assert actual == expected, (level, stage, name, actual, expected)
            executions += 1
    return executions


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--levels", type=int, choices=(0, 1, 2, 3), nargs="+", default=[0, 2, 3]
    )
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    scratch: Path = ROOT / ".temp/number-allocation-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="execution-", dir=scratch) as name:
        report: dict[str, object] = {
            "binary": str(binary),
            "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "platform": platform.platform(),
            "executions": execute(binary, Path(name), arguments.levels),
            "output_checks_per_execution": len(OUTPUTS),
        }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
