#!/usr/bin/env python3
"""Use closed-type traits and unevaluated metadata through actual CLI stages."""

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
let meta = $std::meta;
let categories = [
    meta::is_null(Null), meta::is_bool(Bool), meta::is_number(Number),
    meta::is_string(String), meta::is_path(Path), meta::is_range(Range),
    meta::is_function(Function), meta::is_scope(Scope),
    meta::is_process(Process), meta::is_connection(Connection),
    meta::is_type(Type), meta::is_never(meta::remove_null(Null)),
    meta::is_union(Number | Null), meta::is_scalar(String | Path),
    meta::is_nullable(Number | Null),
];
($std::ranges::all_of(categories, fn(value: Bool) { value }) as String)
    -> ./categories.txt;
let relations = [
    meta::is_same(Number, Number), meta::accepts(Path | String, Path),
    meta::is_assignable(String, Path), meta::is_convertible(Path, String),
    not meta::is_convertible(Number, String), meta::is_castable(String, Number),
    not meta::is_castable(String, Process),
    not meta::is_convertible(Range<Number>, Range<String>),
];
($std::ranges::all_of(relations, fn(value: Bool) { value }) as String)
    -> ./relations.txt;
meta::type_name(meta::add_null(Number)) -> ./nullable.txt;
meta::type_name(meta::remove_null(Number | Null)) -> ./nonnull.txt;
meta::type_name(meta::common_type(Path, String)) -> ./common.txt;
meta::type_name(meta::range_of(Number)) -> ./range.txt;
let element? = meta::element_type(Range<String>);
if element is not null { meta::type_name(element) -> ./element.txt; };
((meta::element_type(Number) is null) as String) -> ./nonrange.txt;
(meta::range_rank(Range<Range<Number>>) as String) -> ./rank.txt;
($std::ranges::size(meta::alternatives(Any)) as String)
    -> ./alternatives.txt;

fn adjust(value) {
    if meta::is_number(decltype(value)) {
        value + 1
    } else {
        $std::string::concat(value, "!")
    }
};
(adjust(4) as String) -> ./generic-number.txt;
adjust("Klee") -> ./generic-string.txt;
fn apply_policy(policy: Scope, value: Number) {
    if meta::has_member(policy, "apply") { policy::apply(value) }
    else { value }
};
(apply_policy(scope { apply: fn(value: Number) { value * 2 } }, 6)
    as String) -> ./policy.txt;
(apply_policy(scope { note: "identity" }, 6) as String)
    -> ./default-policy.txt;

let calls = 0;
fn touched(value: Number) -> Number {
    calls = calls + 1;
    "must not execute" -> ./forbidden-call.txt;
    value + 1
};
(meta::is_invocable(touched, Number) as String)
    -> ./invocable.txt;
(meta::is_invocable_r(Number, touched, Number) as String)
    -> ./invocable-result.txt;
(meta::is_invocable_r(String, touched, Number) as String)
    -> ./explicit-only-result.txt;
(meta::is_invocable(touched, String) as String)
    -> ./wrong-argument.txt;
meta::type_name(meta::invoke_result(touched, Number))
    -> ./result.txt;
(meta::parameter_count(touched) as String) -> ./arity.txt;
(meta::is_variadic($std::ranges::of) as String) -> ./variadic.txt;
let parameters? = meta::parameter_types(touched);
if parameters is not null {
    $std::string::join($std::ranges::map(parameters,
        fn(type: Type) { meta::type_name(type) }), "|")
        -> ./parameters.txt;
};
(meta::is_invocable({
    calls = calls + 1;
    touched
}, {
    "must not execute" -> ./forbidden-argument.txt;
    3
}) as String) -> ./unevaluated-arguments.txt;
(meta::is_invocable(ssh://never-login.invalid @ ./never-start-this, String)
    as String) -> ./unevaluated-process.txt;
(meta::has_member({
    "must not execute" -> ./forbidden-member.txt;
    scope { path: ~ }
}, "path") as String) -> ./has-member.txt;
let member? = meta::member_type(scope { path: ~ }, "path");
if member is not null { meta::type_name(member) -> ./member-type.txt; };
$std::string::join(meta::member_names(scope { beta: 2, alpha: 1 }), "|")
    -> ./members.txt;
(meta::member_count(scope { beta: 2, alpha: 1 }) as String)
    -> ./member-count.txt;
(calls as String) -> ./calls.txt;
acquire;
"""

OUTPUTS: dict[str, bytes] = {
    "categories.txt": b"true",
    "relations.txt": b"true",
    "nullable.txt": b"Number?",
    "nonnull.txt": b"Number",
    "common.txt": b"String | Path",
    "range.txt": b"Range<Number>",
    "element.txt": b"String",
    "nonrange.txt": b"true",
    "rank.txt": b"2",
    "alternatives.txt": b"11",
    "generic-number.txt": b"5",
    "generic-string.txt": b"Klee!",
    "policy.txt": b"12",
    "default-policy.txt": b"6",
    "invocable.txt": b"true",
    "invocable-result.txt": b"true",
    "explicit-only-result.txt": b"false",
    "wrong-argument.txt": b"false",
    "result.txt": b"Number",
    "arity.txt": b"1",
    "variadic.txt": b"true",
    "parameters.txt": b"Number",
    "unevaluated-arguments.txt": b"true",
    "unevaluated-process.txt": b"true",
    "has-member.txt": b"true",
    "member-type.txt": b"Path",
    "members.txt": b"alpha|beta",
    "member-count.txt": b"2",
    "calls.txt": b"0",
}
FORBIDDEN: tuple[str, ...] = (
    "forbidden-call.txt",
    "forbidden-argument.txt",
    "forbidden-member.txt",
)
NEGATIVE: tuple[tuple[str, str, str], ...] = (
    ("value-is-not-type", "$std::meta::is_number(3);", "Type"),
    (
        "invalid-invocation-result",
        "$std::meta::invoke_result(1, Number);",
        "invocation",
    ),
    (
        "dynamic-member-name",
        "$std::meta::has_member(scope { value: 1 }, $std::io::read_text(./name.txt));",
        "statically",
    ),
    (
        "wrong-query-arity",
        "$std::meta::is_invocable_r(Number);",
        "arguments",
    ),
)


def observations(directory: Path) -> None:
    for name, expected in OUTPUTS.items():
        actual: bytes = (directory / name).read_bytes()
        assert actual == expected, (name, actual, expected)
    assert not any((directory / name).exists() for name in FORBIDDEN)


def execute(binary: Path, parent: Path, levels: list[int]) -> int:
    executions: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            source: Path = directory / "type traits 体验.msync"
            source.write_text(PROGRAM, encoding="utf-8")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"),
                "trait check",
            )
            assert not any((directory / name).exists() for name in OUTPUTS)
            assert not any((directory / name).exists() for name in FORBIDDEN)
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
                    "trait compile",
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
                        "trait bind",
                    )
                    artifact = bound
                assert not any((directory / name).exists() for name in FORBIDDEN)
                source.unlink()
                launch: Path = directory
                if stage == "executable":
                    launch = directory / "different launch directory"
                    launch.mkdir()
                result = invoke(binary, launch, "run-ir", str(artifact))
            accept(result, f"traits O{level} {stage}")
            observations(directory)
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
    scratch: Path = ROOT / ".temp/type-traits-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=scratch) as name:
        directory: Path = Path(name)
        report: dict[str, object] = {
            "namespace": "$std::meta",
            "binary": str(binary),
            "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "companion_dlls": {
                path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(binary.parent.glob("*.dll"))
            },
            "platform": platform.platform(),
            "levels": levels,
            "staged_program_executions": execute(binary, directory, levels),
            "exact_outputs_per_execution": len(OUTPUTS),
            "forbidden_effects_per_execution": len(FORBIDDEN),
            "negative_diagnostics": negative(binary, directory),
        }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
