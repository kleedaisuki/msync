#!/usr/bin/env python3
"""Compose product and sum families through actual source and binary programs."""

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
let product = $std::data::Pair;
let result = $std::data::Result;
let either = $std::data::Either;
let option = $std::data::Option;
let pair = product::new("Klee", 3);
pair::first -> ./first.txt;
(pair::second as String) -> ./second.txt;
let changed = product::bimap(pair, $std::string::to_uppercase,
    fn(value: Number) { value + 1 });
changed::first -> ./bimap-first.txt;
(changed::second as String) -> ./bimap-second.txt;
product::map_first(pair, $std::string::to_lowercase)::first
    -> ./map-first.txt;
(product::map_second(pair, fn(value: Number) { value * 2 })::second
    as String) -> ./map-second.txt;
let swapped = product::swap(pair);
(swapped::first as String) -> ./swap-first.txt;
swapped::second -> ./swap-second.txt;
product::fold(pair, fn(first: String, second: Number) {
    $std::string::concat(first, second as String)
}) -> ./product-fold.txt;
let curried = product::curry(fn(value: $std::concepts::Product) {
    $std::string::concat(value::first, value::second as String)
});
curried("Klee")(4) -> ./curried.txt;
let uncurried = product::uncurry(fn(first: String) {
    fn(second: Number) { $std::string::concat(first, second as String) }
});
uncurried(pair) -> ./uncurried.txt;
scope TextEquality {
    Self: String,
    equal: fn(left: String, right: String) {
        $std::string::to_lowercase(left) == $std::string::to_lowercase(right)
    },
};
scope NumberEquality {
    Self: Number,
    equal: fn(left: Number, right: Number) { left == right },
};
(product::equal(pair, product::new("KLEE", 3),
    TextEquality, NumberEquality) as String) -> ./policy-equal.txt;
let nullable_pair = product::new(null, true);
((nullable_pair::first is null) as String) -> ./nullable-first.txt;
(nullable_pair::second as String) -> ./nullable-second.txt;

let success = result::ok("Klee");
let failure = result::err("missing");
(result::is_ok(success) as String) -> ./is-ok.txt;
(result::is_err(failure) as String) -> ./is-err.txt;
result::fold(success, fn(value: String) { value },
    fn(error: String) { error }) -> ./result-fold.txt;
result::get_or(result::map(success, $std::string::to_uppercase), "fallback")
    -> ./result-map.txt;
result::fold(result::map_error(failure, $std::string::to_uppercase),
    fn(value: String) { value }, fn(error: String) { error })
    -> ./result-map-error.txt;
result::fold(result::bimap(failure, $std::string::to_lowercase,
    $std::string::to_uppercase), fn(value: String) { value },
    fn(error: String) { error }) -> ./result-bimap.txt;
result::get_or(result::and_then(success,
    fn(value: String) { result::ok($std::string::concat(value, "!")) }),
    "fallback") -> ./result-bind.txt;
result::get_or(result::or_else(failure,
    fn(error: String) { result::ok($std::string::concat("recovered:", error)) }),
    "fallback") -> ./result-recover.txt;
result::get_or(failure, "default") -> ./result-default.txt;
result::get_or_else(failure,
    fn(error: String) { $std::string::concat("lazy:", error) })
    -> ./result-default-lazy.txt;
option::get_or(result::to_option(success), "fallback")
    -> ./result-to-option.txt;
(option::is_none(result::to_option(failure)) as String)
    -> ./result-to-none.txt;
option::get_or(result::error_option(failure), "fallback")
    -> ./result-error-option.txt;
result::get_or(result::from_option(option::some("available"), "missing"),
    "fallback") -> ./result-from-option.txt;
result::fold(result::from_option(null, "absent"),
    fn(value: String) { value }, fn(error: String) { error })
    -> ./result-from-none.txt;
result::get_or(result::flatten(result::ok(result::ok("nested"))),
    "fallback") -> ./result-flatten.txt;
result::fold(result::zip(result::ok("joined"), result::ok(5)),
    fn(value: $std::concepts::Product) {
        $std::string::concat(value::first, value::second as String)
    }, fn(error: String) { error }) -> ./result-zip.txt;
(result::get_or(result::zip_with(result::ok(2), result::ok(3),
    fn(first: Number, second: Number) { first + second }), 0) as String)
    -> ./result-zip-with.txt;
let skipped = 0;
result::map(failure, fn(value: String) {
    skipped = skipped + 1; value
});
result::and_then(failure, fn(value: String) {
    skipped = skipped + 1; result::ok(value)
});
result::map_error(success, fn(error: String) {
    skipped = skipped + 1; error
});
result::or_else(success, fn(error: String) {
    skipped = skipped + 1; result::ok(error)
});
result::get_or_else(success, fn(error: String) {
    skipped = skipped + 1; error
});
(skipped as String) -> ./skipped.txt;
(result::fold(result::ok(null), fn(value?) { value is null },
    fn(error?) { false }) as String) -> ./result-null.txt;

let left = either::left("left");
let right = either::right(7);
(either::is_left(left) as String) -> ./is-left.txt;
(either::is_right(right) as String) -> ./is-right.txt;
either::fold(either::map_left(left, $std::string::to_uppercase),
    fn(value: String) { value }, fn(value: Number) { value as String })
    -> ./either-map-left.txt;
either::fold(either::map_right(right, fn(value: Number) { value + 1 }),
    fn(value: String) { value }, fn(value: Number) { value as String })
    -> ./either-map-right.txt;
either::fold(either::bimap(left, $std::string::to_uppercase,
    fn(value: Number) { value + 1 }), fn(value: String) { value },
    fn(value: Number) { value as String }) -> ./either-bimap.txt;
(either::is_right(either::swap(left)) as String) -> ./either-swap.txt;
(either::merge(right) as String) -> ./either-merge.txt;
"""

OUTPUTS: dict[str, bytes] = {
    "first.txt": b"Klee",
    "second.txt": b"3",
    "bimap-first.txt": b"KLEE",
    "bimap-second.txt": b"4",
    "map-first.txt": b"klee",
    "map-second.txt": b"6",
    "swap-first.txt": b"3",
    "swap-second.txt": b"Klee",
    "product-fold.txt": b"Klee3",
    "curried.txt": b"Klee4",
    "uncurried.txt": b"Klee3",
    "policy-equal.txt": b"true",
    "nullable-first.txt": b"true",
    "nullable-second.txt": b"true",
    "is-ok.txt": b"true",
    "is-err.txt": b"true",
    "result-fold.txt": b"Klee",
    "result-map.txt": b"KLEE",
    "result-map-error.txt": b"MISSING",
    "result-bimap.txt": b"MISSING",
    "result-bind.txt": b"Klee!",
    "result-recover.txt": b"recovered:missing",
    "result-default.txt": b"default",
    "result-default-lazy.txt": b"lazy:missing",
    "result-to-option.txt": b"Klee",
    "result-to-none.txt": b"true",
    "result-error-option.txt": b"missing",
    "result-from-option.txt": b"available",
    "result-from-none.txt": b"absent",
    "result-flatten.txt": b"nested",
    "result-zip.txt": b"joined5",
    "result-zip-with.txt": b"5",
    "skipped.txt": b"0",
    "result-null.txt": b"true",
    "is-left.txt": b"true",
    "is-right.txt": b"true",
    "either-map-left.txt": b"LEFT",
    "either-map-right.txt": b"8",
    "either-bimap.txt": b"LEFT",
    "either-swap.txt": b"true",
    "either-merge.txt": b"7",
}

BRANCH_PROGRAM: str = r"""
let result = $std::data::Result;
let selected = if exists ./selected.txt {
    result::ok("Klee")
} else { result::err(7) };
let visited = 0;
let unselected = 0;
let changed = result::map(selected, fn(value: String) {
    visited = visited + 1;
    $std::string::concat(value, "!")
});
result::fold(changed, fn(value: String) {
    unselected = unselected + 1;
    value
}, fn(error: Number) {
    unselected = unselected + 1;
    error as String
}) -> ./selected-value.txt;
(visited as String) -> ./visited.txt;
(unselected as String) -> ./fold-visits.txt;
"""

NEGATIVE: tuple[tuple[str, str, str], ...] = (
    ("missing-product-field", "$std::data::Pair::swap(scope { first: 1 });", "second"),
    (
        "wrong-product-callback",
        '$std::data::Pair::map_first($std::data::Pair::new("text", 1), fn(value: Number) { value + 1 });',
        "Number",
    ),
    (
        "missing-sum-handler",
        "$std::data::Result::fold($std::data::Result::ok(1), fn(value: Number) { value });",
        "argument",
    ),
    (
        "invalid-result-bind",
        "$std::data::Result::and_then($std::data::Result::ok(1), fn(value: Number) { value + 1 });",
        "Result",
    ),
    (
        "invalid-result-shape",
        "$std::data::Result::is_ok(scope { other: true });",
        "fold",
    ),
    (
        "dynamic-variant-handler-type",
        (
            'let value = if exists ./input.txt { $std::data::Result::ok("text") } '
            "else { $std::data::Result::err(7) }; "
            "$std::data::Result::fold(value, fn(item: Number) { item }, "
            "fn(error: Number) { error });"
        ),
        "Number",
    ),
)


def program(
    binary: Path,
    directory: Path,
    source_text: str,
    expected: dict[str, bytes],
    level: int,
    stage: str,
) -> None:
    source: Path = directory / "data 组合.msync"
    source.write_text(source_text, encoding="utf-8")
    if stage == "source":
        accept(
            invoke(binary, directory, "run", str(source), f"-O{level}"), "data source"
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
            "data compile",
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
                "data bind",
            )
            artifact = bound
        source.unlink()
        launch: Path = directory
        if stage == "executable":
            launch = directory / "different launch directory"
            launch.mkdir()
        accept(invoke(binary, launch, "run-ir", str(artifact)), "data binary")
    for name, contents in expected.items():
        actual: bytes = (directory / name).read_bytes()
        assert actual == contents, (level, stage, name, actual, contents)


def execute(binary: Path, parent: Path, levels: list[int]) -> int:
    executions: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage / "families"
            directory.mkdir(parents=True)
            program(binary, directory, PROGRAM, OUTPUTS, level, stage)
            executions += 1
            for selected in (False, True):
                directory = parent / f"O{level}" / stage / f"selected-{selected}"
                directory.mkdir(parents=True)
                if selected:
                    (directory / "selected.txt").write_text("present", encoding="utf-8")
                expected: dict[str, bytes] = {
                    "selected-value.txt": b"Klee!" if selected else b"7",
                    "visited.txt": b"1" if selected else b"0",
                    "fold-visits.txt": b"1",
                }
                program(binary, directory, BRANCH_PROGRAM, expected, level, stage)
                executions += 1
    return executions


def negative(binary: Path, parent: Path) -> int:
    directory: Path = parent / "negative"
    directory.mkdir()
    for name, text, expected in NEGATIVE:
        source: Path = directory / f"{name}.msync"
        source.write_text(text, encoding="utf-8")
        response: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "check", str(source)
        )
        assert response.returncode != 0, (name, response.stdout, response.stderr)
        assert (
            "error" in response.stderr.lower()
            and expected.lower() in response.stderr.lower()
        ), (name, response.stderr)
    return len(NEGATIVE)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", "--cli", type=Path, required=True)
    parser.add_argument("--level", type=int, choices=(0, 2, 3), action="append")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    levels: list[int] = arguments.level or [2]
    scratch: Path = ROOT / ".temp/standard-data"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=scratch) as name:
        directory: Path = Path(name)
        report: dict[str, object] = {
            "binary": str(binary),
            "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "companion_dlls": {
                path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(binary.parent.glob("*.dll"))
            },
            "managed_msync_components": [
                path.name
                for path in sorted(binary.parent.glob("*.dll"))
                if path.name.casefold()
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
            "family_outputs_per_execution": len(OUTPUTS),
            "dynamic_branch_outputs_per_execution": 3,
            "negative_diagnostics": negative(binary, directory),
        }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
