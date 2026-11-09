#!/usr/bin/env python3
"""Exercise semantic ownership through the source and source-free IR boundaries."""

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
let values?: Range<Number> = [1, 2];
if values is not null {
    $std::string::join($std::ranges::map(values, fn(value: Number) {
        (value + 1) as String
    }), ",") -> ./guarded.txt;
};
let nullable?: Number = 1;
nullable = null;
((nullable is null) as String) -> ./nullable-write.txt;

let calls = 0;
fn delayed_values() {
    calls = calls + 1;
    [calls]
};
let delayed = delayed_values();
(calls as String) -> ./before-demand.txt;
($std::ranges::first(delayed) as String) -> ./first-demand.txt;
($std::ranges::first(delayed) as String) -> ./second-demand.txt;
(calls as String) -> ./after-demand.txt;

let action = fn(value: Bool) { "A" };
let replace = fn {
    action = fn(value: Bool) { "B" };
    false
};
action(replace()) -> ./callee-before-argument.txt;
action(false) -> ./callee-after-argument.txt;

fn late_values() {
    let value? = null;
    let captured = [fn { value }];
    value = "late";
    captured
};
let getter = $std::ranges::first(late_values());
let late? = getter();
if late is not null { late -> ./late-capture.txt; };

fn apply(operation: Function, value: Number) { operation(value) };
(apply(fn(first: Number) {
    apply(fn(second: Number) { first + second }, 2)
}, 3) as String) -> ./higher-order.txt;
fn count(state: Scope) {
    if state::remaining <= 0 { 0 }
    else { count(scope { remaining: state::remaining - 1 }) + 1 }
};
(count(scope { remaining: 8 }) as String) -> ./recursive.txt;

let original = scope { text: "before" };
let objects = $std::storage::Buffer::new([original]);
let alias = $std::storage::Buffer::get(objects, 0);
alias::text = "shared";
original::text -> ./indexed-alias.txt;
let numbers = $std::storage::Buffer::new([]);
let before = $std::storage::Buffer::snapshot(numbers);
$std::storage::Buffer::push(numbers, 7);
($std::ranges::size(before) as String) -> ./snapshot-before.txt;
($std::storage::Buffer::get(numbers, 0) as String) -> ./first-write.txt;
let present_null = $std::data::Option::some(null);
if $std::data::Option::is_some(present_null) and present_null::value is null {
    "present-null" -> ./present-null.txt;
};

fn named(value: String) {
    scope State { text: value };
    State
};
let first = named("first");
first::text = "singleton";
named("ignored")::text -> ./singleton.txt;
fn fresh(value: String) { scope { text: value } };
let changed = fresh("first");
changed::text = "changed";
fresh("fresh")::text -> ./fresh.txt;

let query_effects = 0;
fn effectful() {
    query_effects = query_effects + 1;
    "not executed" -> ./forbidden-query-effect.txt;
    3
};
($std::meta::is_invocable(effectful) as String) -> ./query-invocable.txt;
($std::meta::is_number($std::meta::invoke_result(effectful)) as String)
    -> ./query-result.txt;
(query_effects as String) -> ./query-effects.txt;
concept Numeric { operation: fn(value: Number) -> Number, };
fn require_numeric(value: Numeric) { 0 };
let mutable = scope { operation: fn(value: Number) { value + 1 } };
let invalid = fn {
    require_numeric(mutable);
    mutable::operation = fn(value: Number) { true };
};
let allowed = $std::meta::is_invocable(invalid);
mutable::operation = fn(value: Number) { true };
((not allowed) as String) -> ./failed-query-isolation.txt;
let unselected = ssh://hir-no-auth.invalid;
((decltype(~ @ unselected) is Path) as String) -> ./unevaluated-endpoint.txt;
acquire;
"""

OUTPUTS: dict[str, bytes] = {
    "guarded.txt": b"2,3",
    "nullable-write.txt": b"true",
    "before-demand.txt": b"0",
    "first-demand.txt": b"1",
    "second-demand.txt": b"1",
    "after-demand.txt": b"1",
    "callee-before-argument.txt": b"A",
    "callee-after-argument.txt": b"B",
    "late-capture.txt": b"late",
    "higher-order.txt": b"5",
    "recursive.txt": b"8",
    "indexed-alias.txt": b"shared",
    "snapshot-before.txt": b"0",
    "first-write.txt": b"7",
    "present-null.txt": b"present-null",
    "singleton.txt": b"singleton",
    "fresh.txt": b"fresh",
    "query-invocable.txt": b"true",
    "query-result.txt": b"true",
    "query-effects.txt": b"0",
    "failed-query-isolation.txt": b"true",
    "unevaluated-endpoint.txt": b"true",
}

NEGATIVE: tuple[tuple[str, str, tuple[str, ...]], ...] = (
    ("fixed-kind", 'let value = 1; value = "wrong";', ("Number", "String")),
    ("unhandled-null", "let value? = null; value -> ./wrong;", ("Null",)),
    ("field-null-admission", "scope Invalid { item: null };", ("?",)),
    (
        "invalidated-member-proof",
        """
let values = $std::storage::Buffer::new([
    scope { child?: scope { text: "ok" } }
]);
let first = $std::storage::Buffer::get(values, 0);
let alias = $std::storage::Buffer::get(values, 0);
if first::child is not null {
    alias::child = null;
    first::child::text;
};
""",
        ("Scope?",),
    ),
    (
        "unsafe-live-union",
        """
let values: Range<Number> | String = [1];
fn identity(value: Range<Number> | String) { value };
let delayed = identity(values);
values = "later";
$std::ranges::size(delayed);
""",
        ("Range", "String"),
    ),
)


def verify_outputs(directory: Path, label: str) -> None:
    for name, expected in OUTPUTS.items():
        actual: bytes = (directory / name).read_bytes()
        assert actual == expected, (label, name, actual, expected)
    assert not (directory / "forbidden-query-effect.txt").exists(), label


def execute(binary: Path, parent: Path, levels: list[int]) -> int:
    executions: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            source: Path = directory / "semantic 边界.msync"
            source.write_text(PROGRAM, encoding="utf-8", newline="\n")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"), "check"
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
                    "compile",
                )
                assert not any((directory / name).exists() for name in OUTPUTS)
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
                    assert not any((directory / name).exists() for name in OUTPUTS)
                source.unlink()
                launch: Path = directory
                if stage == "executable":
                    launch = directory / "different launch cwd"
                    launch.mkdir()
                result = invoke(binary, launch, "run-ir", str(artifact))
            accept(result, f"O{level} {stage}")
            verify_outputs(directory, f"O{level} {stage}")
            executions += 1
    return executions


def negative(binary: Path, parent: Path) -> int:
    directory: Path = parent / "negative"
    directory.mkdir()
    for name, program, fragments in NEGATIVE:
        source: Path = directory / f"{name}.msync"
        source.write_text(program, encoding="utf-8", newline="\n")
        result: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "check", str(source)
        )
        assert result.returncode != 0, (name, result.stdout, result.stderr)
        assert all(fragment in result.stderr for fragment in fragments), (
            name,
            result.stderr,
        )
    return len(NEGATIVE)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--levels", type=int, choices=(0, 1, 2, 3), nargs="+", default=[0, 2, 3]
    )
    parser.add_argument("--work", type=Path)
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    scratch: Path = ROOT / ".temp/hir-semantic-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    temporary: tempfile.TemporaryDirectory[str] | None = None
    if arguments.work is None:
        temporary = tempfile.TemporaryDirectory(prefix="case-", dir=scratch)
        work: Path = Path(temporary.name)
    else:
        work = arguments.work.resolve()
        work.mkdir(parents=True, exist_ok=True)
    try:
        report: dict[str, object] = {
            "binary": str(binary),
            "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "managed_product_components": {
                path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(binary.parent.glob("*.dll"))
                if path.stem.casefold() == "msync"
                or path.stem.casefold().startswith("msync.")
            },
            "source_sha256": hashlib.sha256(PROGRAM.encode("utf-8")).hexdigest(),
            "platform": platform.platform(),
            "levels": arguments.levels,
            "staged_executions": execute(binary, work, arguments.levels),
            "exact_outputs_per_execution": len(OUTPUTS),
            "forbidden_query_effect_absent": True,
            "negative_diagnostics": negative(binary, work),
        }
        rendered: str = json.dumps(report, indent=2) + "\n"
        if arguments.output is not None:
            arguments.output.parent.mkdir(parents=True, exist_ok=True)
            arguments.output.write_text(rendered, encoding="utf-8")
        print(rendered, end="")
    finally:
        if temporary is not None:
            temporary.cleanup()


if __name__ == "__main__":
    main()
