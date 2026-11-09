#!/usr/bin/env python3
"""Exercise native nullable Option composition through source and binary IR."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
import tempfile
from pathlib import Path

from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]
PROGRAM: str = r"""

let option = $std::data::Option;
let present = option::some(3);
(option::is_some(present) as String) -> ./present.txt;
(option::is_none(option::none()) as String) -> ./none.txt;
(option::get_or(option::map(present, fn(value: Number) { value + 1 }), 0)
    as String) -> ./map.txt;
(option::get_or(option::and_then(present,
    fn(value: Number) { option::some(value + 2) }), 0) as String)
    -> ./bind.txt;
let callback_calls = 0;
option::map(option::none(), fn(value: Number) {
    callback_calls = callback_calls + 1;
    value
});
option::and_then(option::none(), fn(value: Number) {
    callback_calls = callback_calls + 1;
    option::some(value)
});
option::filter(option::none(), fn(value: Number) {
    callback_calls = callback_calls + 1;
    true
});
option::get_or_else(present, fn { callback_calls = callback_calls + 1; 10 });
option::or_else(present, fn {
    callback_calls = callback_calls + 1;
    option::some(10)
});
(callback_calls as String) -> ./calls.txt;
(option::fold(present, fn(value: Number) { value as String },
    fn { "none" })) -> ./fold-some.txt;
(option::fold(option::none(), fn(value: Number) { value as String },
    fn { "none" })) -> ./fold-none.txt;
(option::get_or(option::none(), 11) as String)
    -> ./default.txt;
(option::get_or_else(option::none(), fn { 12 }) as String)
    -> ./default-lazy.txt;
(option::get_or(option::or_else(option::none(), fn { option::some(13) }), 0)
    as String) -> ./alternative.txt;
(option::is_some(option::filter(present, fn(value: Number) { value == 3 }))
    as String) -> ./filter-yes.txt;
(option::is_none(option::filter(present, fn(value: Number) { value == 4 }))
    as String) -> ./filter-no.txt;
(option::is_none(option::from_nullable(null)) as String)
    -> ./from-null.txt;
(option::get_or(option::from_nullable(14), 0) as String)
    -> ./from-value.txt;
(option::to_nullable(present) as String)
    -> ./to-value.txt;
((option::to_nullable(option::none()) is null) as String)
    -> ./to-null.txt;
let boxed_null = option::some(null);
(option::is_some(boxed_null) as String)
    -> ./boxed-null-some.txt;
(option::fold(boxed_null, fn(value?) { value is null }, fn { false })
    as String) -> ./boxed-null-value.txt;
(option::get_or(option::flatten(option::some(option::some(15))), 0) as String)
    -> ./flatten.txt;
(option::is_none(option::flatten(option::some(null))) as String)
    -> ./flatten-null.txt;
(option::get_or(option::zip_with(option::some(2), option::some(3),
    fn(left: Number, right: Number) { left + right }), 0) as String)
    -> ./zip-with.txt;
let paired? = option::zip(option::some("Klee"), option::some(16));
if paired is not null {
    paired::value::first -> ./zip-first.txt;
    (paired::value::second as String) -> ./zip-second.txt;
};
(option::is_none(option::zip(option::none(), option::some(1))) as String)
    -> ./zip-none.txt;
let maybe? = if exists ./input.txt {
    option::some(17)
} else { null };
(option::get_or(option::map(maybe, fn(value: Number) { value + 1 }), 0)
    as String) -> ./dynamic.txt;
"""

OUTPUTS: dict[str, bytes] = {
    "present.txt": b"true",
    "none.txt": b"true",
    "map.txt": b"4",
    "bind.txt": b"5",
    "calls.txt": b"0",
    "fold-some.txt": b"3",
    "fold-none.txt": b"none",
    "default.txt": b"11",
    "default-lazy.txt": b"12",
    "alternative.txt": b"13",
    "filter-yes.txt": b"true",
    "filter-no.txt": b"true",
    "from-null.txt": b"true",
    "from-value.txt": b"14",
    "to-value.txt": b"3",
    "to-null.txt": b"true",
    "boxed-null-some.txt": b"true",
    "boxed-null-value.txt": b"true",
    "flatten.txt": b"15",
    "flatten-null.txt": b"true",
    "zip-with.txt": b"5",
    "zip-first.txt": b"Klee",
    "zip-second.txt": b"16",
    "zip-none.txt": b"true",
    "dynamic.txt": b"18",
}

NEGATIVE: tuple[tuple[str, str, str], ...] = (
    (
        "bind-must-return-option",
        (
            "$std::data::Option::and_then($std::data::Option::some(3), "
            "fn(value: Number) { value + 1 });"
        ),
        "Option",
    ),
    (
        "flatten-needs-nested-option",
        "$std::data::Option::flatten($std::data::Option::some(3));",
        "Option",
    ),
    (
        "callback-payload-type",
        (
            '$std::data::Option::map($std::data::Option::some("text"), '
            "fn(value: Number) { value + 1 });"
        ),
        "Number",
    ),
    (
        "not-a-boxed-option",
        '$std::data::Option::is_some(scope { other: "missing payload" });',
        "value",
    ),
)


def execute(binary: Path, parent: Path, levels: list[int]) -> int:
    executions: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            (directory / "input.txt").write_text("present", encoding="utf-8")
            source: Path = directory / "option 体验.msync"
            source.write_text(PROGRAM, encoding="utf-8")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"),
                "Option check",
            )
            assert not any((directory / name).exists() for name in OUTPUTS)
            if stage == "source":
                result: subprocess.CompletedProcess[str] = invoke(
                    binary, directory, "run", str(source), f"-O{level}"
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
                    "Option compile",
                )
                if stage == "executable":
                    bound: Path = source.with_suffix(".mexe")
                    accept(
                        invoke(
                            binary,
                            directory,
                            "bind-ir",
                            str(artifact),
                            "-o",
                            str(bound),
                            "--source-file",
                            str(source),
                        ),
                        "Option bind",
                    )
                    artifact = bound
                source.unlink()
                launch: Path = directory / "different launch directory"
                if stage == "relocatable":
                    launch = directory
                launch.mkdir(exist_ok=True)
                result = invoke(binary, launch, "run-ir", str(artifact))
            accept(result, f"Option O{level} {stage}")
            for name, expected in OUTPUTS.items():
                actual: bytes = (directory / name).read_bytes()
                assert actual == expected, (level, stage, name, actual, expected)
            executions += 1
    return executions


def negative(binary: Path, parent: Path) -> int:
    directory: Path = parent / "negative"
    directory.mkdir()
    for name, text, expected in NEGATIVE:
        source: Path = directory / f"{name}.msync"
        source.write_text(text, encoding="utf-8")
        result: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "check", str(source)
        )
        assert result.returncode != 0, (name, result.stdout, result.stderr)
        assert expected in result.stderr and "error" in result.stderr.lower(), (
            name,
            result.stderr,
        )
    return len(NEGATIVE)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", "--cli", type=Path, required=True)
    parser.add_argument("--level", type=int, choices=(0, 1, 2, 3), action="append")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    levels: list[int] = arguments.level or [2]
    scratch: Path = ROOT / ".temp/standard-option"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=scratch) as name:
        directory: Path = Path(name)
        report: dict[str, object] = {
            "type_family": "$std::data::Option",
            "binary": str(binary),
            "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "companion_dlls": {
                str(path.name): hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(binary.parent.glob("*.dll"))
            },
            "managed_msync_components": [
                path.name
                for path in sorted(binary.parent.glob("*.dll"))
                if path.name.lower()
                in {
                    "msync.dll",
                    "msync.core.dll",
                    "msync.compiler.dll",
                    "msync.runtime.dll",
                }
            ],
            "platform": platform.platform(),
            "levels": levels,
            "staged_program_executions": execute(binary, directory, levels),
            "exact_outputs_per_execution": len(OUTPUTS),
            "negative_diagnostics": negative(binary, directory),
        }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
