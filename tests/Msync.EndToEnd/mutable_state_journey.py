#!/usr/bin/env python3
"""Observe mutable lexical storage and Scope aliases through actual CLI artifacts."""

from __future__ import annotations

import argparse
import json
import os
import random
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Example:
    name: str
    source: str
    expected: bytes


EXAMPLES: tuple[Example, ...] = (
    Example(
        "slot", 'let value = "before"; value = "after"; value -> ./result;', b"after"
    ),
    Example(
        "compact", 'let value="before"; value="after"; value -> ./result;', b"after"
    ),
    Example(
        "block",
        'let value = "before"; { value = "after"; }; value -> ./result;',
        b"after",
    ),
    Example(
        "shadow",
        'let value = "outer"; { let value = "inner"; value = "changed"; }; value -> ./result;',
        b"outer",
    ),
    Example(
        "union-type-change",
        'let value: Bool | String = true; value = "changed type"; value -> ./result;',
        b"changed type",
    ),
    Example(
        "scope-alias",
        'let object = scope { filename: "before" }; let alias = object; alias::filename = "after"; object::filename -> ./result;',
        b"after",
    ),
    Example(
        "nested-scope",
        'let object = scope { nested: scope { filename: "before" } }; let nested = object::nested; nested::filename = "after"; object::nested::filename -> ./result;',
        b"after",
    ),
    Example(
        "field-captured-read",
        'let object = scope { field: "before", read: fn { field }, write: fn { field = "after"; } }; object::field = "middle"; object::read() -> ./result;',
        b"middle",
    ),
    Example(
        "field-captured-write",
        'let object = scope { field: "before", read: fn { field }, write: fn { field = "after"; } }; object::write(); object::field -> ./result;',
        b"after",
    ),
    Example(
        "nullable-field",
        "scope SomeType { remote: ssh://forge, new: fn { scope { filename?: null } } }; let object = SomeType::new(); object::filename = ./input; object::filename -> ./result;",
        b"FILE\x00\xff\r\n",
    ),
    Example(
        "nullable-field-clear",
        'let object = scope { filename?: ./input }; object::filename = null; if object::filename is null { "cleared" -> ./result };',
        b"cleared",
    ),
    Example(
        "nullable-slot",
        'let value? = null; value = "after"; value -> ./result;',
        b"after",
    ),
    Example(
        "live-capture-read",
        'let value = "before"; let read = fn { value }; value = "after"; read() -> ./result;',
        b"after",
    ),
    Example(
        "live-capture-write",
        'let value = "before"; let write = fn { value = "after"; }; write(); value -> ./result;',
        b"after",
    ),
    Example(
        "capture-null-write",
        'let value? = "before"; let clear = fn { value = null; }; clear(); if value is null { "cleared" -> ./result };',
        b"cleared",
    ),
    Example(
        "capture-type-change",
        'let value: Bool | String = true; let read = fn { if value is String { value } else { "wrong" } }; value = "after"; read() -> ./result;',
        b"after",
    ),
    Example(
        "captured-callable",
        'let operation = fn { "before" }; let invoke = fn { operation() }; operation = fn { "after" }; invoke() -> ./result;',
        b"after",
    ),
    Example(
        "captured-shadow",
        'let value = "captured"; let read = fn { value }; let value = "shadow"; value = "changed"; read() -> ./result;',
        b"captured",
    ),
    Example(
        "argument-alias",
        'fn write(object) { object::filename = "after"; }; let object = scope { filename: "before" }; write(object); object::filename -> ./result;',
        b"after",
    ),
    Example(
        "instances",
        'fn make() { scope { filename?: null } }; let first = make(); let second = make(); first::filename = true; second::filename = ./input; if first::filename { "first" -> ./result };',
        b"first",
    ),
    Example(
        "constructor-captured-holes",
        'fn make() { scope { value?: null, set: fn(payload) { value = payload; }, get: fn { value } } }; let first = make(); let second = make(); first::set(true); second::set("text"); if first::get() { "first" -> ./result };',
        b"first",
    ),
    Example(
        "named-function",
        'fn operation() { "before" }; operation = fn { "after" }; operation() -> ./result;',
        b"after",
    ),
    Example(
        "named-scope",
        'scope Object { filename: "before" }; Object = scope { filename: "after" }; Object::filename -> ./result;',
        b"after",
    ),
    Example(
        "parameter",
        'fn operation(value: String) { value = "after"; value }; operation("before") -> ./result;',
        b"after",
    ),
    Example(
        "inferred-string-conversion",
        'let value = "before"; value = ./input; value -> ./result;',
        os.path.join(".", "input").encode("utf-8"),
    ),
    Example(
        "inferred-path-conversion",
        'let value = ./missing; value = "./input"; value -> ./result;',
        b"FILE\x00\xff\r\n",
    ),
    Example(
        "constraint-conversion",
        'let value: Path = ./missing; value = "./input"; value -> ./result;',
        b"FILE\x00\xff\r\n",
    ),
    Example(
        "assignment-value",
        'let value = "before"; let discarded? = (value = "after"); if discarded is null { value -> ./result };',
        b"after",
    ),
    Example(
        "conditional",
        'let value = "before"; if exists ./input { value = "after"; } else { value = "wrong"; }; value -> ./result;',
        b"after",
    ),
    Example(
        "match",
        'let value = "before"; match exists ./input { true => { value = "after"; }, false => { value = "wrong"; } }; value -> ./result;',
        b"after",
    ),
    Example(
        "for",
        'let value = "before"; for item in ["first", "last"] { value = item; }; value -> ./result;',
        b"last",
    ),
    Example(
        "loop-capture-freshness",
        'let first = fn { "unset" }; let last = fn { "unset" }; for item in ["first", "last"] { let snapshot = item; if item == "first" { first = fn { snapshot }; } else { last = fn { item }; }; }; if first() == "first" and last() == "last" { "fresh" -> ./result };',
        b"fresh",
    ),
    Example(
        "loop",
        'let value = "before"; loop { value = "after"; break; }; value -> ./result;',
        b"after",
    ),
)

INVALID: tuple[tuple[str, str, tuple[str, ...]], ...] = (
    (
        "inferred-slot-type",
        'let value = true; value = "text";',
        ("Bool", "String"),
    ),
    (
        "inferred-field-type",
        'let object = scope { value: true }; object::value = "text";',
        ("Bool", "String"),
    ),
    (
        "nullable-slot-branch-type",
        "let value? = null; if exists ./input { value = true; } else { value = ./input; };",
        ("Bool", "Path"),
    ),
    (
        "nullable-field-branch-type",
        "let object = scope { filename?: null }; if exists ./input { object::filename = true; } else { object::filename = ./input; };",
        ("Bool", "Path"),
    ),
    (
        "stale-capture-refinement",
        'let value? = "before"; let clear = fn { value = null; }; if value is not null { clear(); value -> ./result; };',
        ("Null",),
    ),
    (
        "stale-field-refinement",
        "let object = scope { value?: ./input }; let alias = object; if object::value is not null { alias::value = null; object::value -> ./result; };",
        ("Null",),
    ),
    (
        "field-null-initializer",
        "let object = scope { filename: null };",
        ("filename?", "Null"),
    ),
    (
        "field-null-store",
        "let object = scope { filename: ./input }; object::filename = null;",
        ("filename", "'?'", "Null"),
    ),
    (
        "slot-null-store",
        'let value = "text"; value = null;',
        ("?", "Null"),
    ),
    (
        "annotated-store",
        "let value: Bool = true; value = ./input;",
        ("Bool", "Path"),
    ),
    (
        "parameter-store",
        "fn bad(value: Bool) { value = ./input; }; bad(true);",
        ("Bool", "Path"),
    ),
    (
        "return-after-store",
        'fn bad() -> Bool { let value: Bool | String = true; value = "text"; value }; bad();',
        ("Bool", "String", "return"),
    ),
    (
        "physical-endpoint-store",
        '$local::./input = "not a lexical field";',
        ("Scope",),
    ),
)


def alias_properties(count: int) -> tuple[Example, ...]:
    examples: list[Example] = []
    for seed in range(count):
        rng = random.Random(0xC311 + seed)
        values: list[str] = ["first-initial", "second-initial"]
        selected = 0
        statements: list[str] = [
            'let first = scope { filename: "first-initial" };',
            'let second = scope { filename: "second-initial" };',
            "let alias = first;",
            "let read = fn { alias::filename };",
        ]
        for index in range(12):
            operation = rng.randrange(4)
            if operation == 0:
                selected = rng.randrange(2)
                statements.append(f"alias = {('first', 'second')[selected]};")
                continue
            payload = f"seed-{seed}-store-{index}"
            owner = selected if operation == 3 else operation - 1
            values[owner] = payload
            target = "alias" if operation == 3 else ("first", "second")[owner]
            statements.append(f'{target}::filename = "{payload}";')
        statements.append("read() -> ./result;")
        examples.append(
            Example(
                f"alias-property-{seed}",
                "\n".join(statements),
                values[selected].encode(),
            )
        )
    return tuple(examples)


def invoke(
    binary: Path, directory: Path, arguments: list[str]
) -> subprocess.CompletedProcess[str]:
    environment: dict[str, str] = dict(os.environ)
    environment["NO_COLOR"] = "1"
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )


def successful(binary: Path, directory: Path, arguments: list[str]) -> None:
    result = invoke(binary, directory, arguments)
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)


def run_example(binary: Path, directory: Path, example: Example) -> int:
    source = directory / f"{example.name}.msync"
    source.write_text(example.source + "\n", encoding="utf-8")
    result = directory / "result"
    relocatable = source.with_suffix(".mso")
    executable = source.with_suffix(".mexe")
    successful(binary, directory, ["check", str(source)])
    assert not result.exists(), f"check executed the {example.name} program"
    successful(binary, directory, ["compile", str(source), "-o", str(relocatable)])
    successful(binary, directory, ["bind-ir", str(relocatable), "-o", str(executable)])
    assert not result.exists(), f"compilation executed the {example.name} program"
    stages = (source, relocatable, executable)
    for target in stages:
        successful(binary, directory, ["run", str(target)])
        assert result.read_bytes() == example.expected, (
            example.name,
            target.suffix,
            example.expected,
            result.read_bytes(),
        )
        result.unlink()
    return len(stages)


def run_repl(binary: Path, directory: Path) -> int:
    submissions = [
        'let value = "before"; let read = fn { value };',
        'value = "after";',
        "read() -> ./repl-read;",
        "let object = scope { filename?: null };",
        "object::filename = ./input;",
        "object::filename -> ./repl-field;",
        'let operation = fn { "before" }; let invoke = fn { operation() };',
        'operation = fn { "after" };',
        "invoke() -> ./repl-callable;",
        "value = true;",
        "read() -> ./repl-after-error;",
        "/quit",
    ]
    result = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "repl"],
        input="\n".join(submissions) + "\n",
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )
    assert result.returncode == 0, (result.stdout, result.stderr)
    assert "Bool" in result.stderr and "String" in result.stderr, result.stderr
    expected: dict[str, bytes] = {
        "repl-read": b"after",
        "repl-field": b"FILE\x00\xff\r\n",
        "repl-callable": b"after",
        "repl-after-error": b"after",
    }
    for name, payload in expected.items():
        output = directory / name
        assert output.exists(), (name, result.stdout, result.stderr)
        assert output.read_bytes() == payload, (
            name,
            output.read_bytes(),
            result.stderr,
        )
    return len(expected)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--case", action="append", default=[], dest="cases")
    parser.add_argument("--skip-invalid", action="store_true")
    parser.add_argument("--properties", type=int, default=12)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    parent = ROOT / ".temp/mutable-state-journey"
    parent.mkdir(parents=True, exist_ok=True)
    stages = 0
    examples = 0
    invalid = 0
    repl_results = 0
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        directory = Path(temporary)
        (directory / "input").write_bytes(b"FILE\x00\xff\r\n")
        selected: set[str] = set(arguments.cases)
        names: set[str] = (
            {example.name for example in EXAMPLES}
            | {name for name, _, _ in INVALID}
            | {"repl"}
        )
        assert selected <= names, f"Unknown cases: {selected - names}"
        for example in EXAMPLES + (
            alias_properties(arguments.properties) if not selected else ()
        ):
            if selected and example.name not in selected:
                continue
            stages += run_example(binary, directory, example)
            examples += 1
            print(f"PASS {example.name}: source, .mso, .mexe", flush=True)
        for name, source, expected in INVALID:
            if arguments.skip_invalid or selected and name not in selected:
                continue
            program = directory / f"{name}.msync"
            program.write_text(source + "\n", encoding="utf-8")
            result = invoke(binary, directory, ["check", str(program)])
            assert result.returncode != 0, (name, "invalid mutable program accepted")
            for fragment in expected:
                assert fragment in result.stderr, (name, fragment, result.stderr)
            invalid += 1
            print(f"PASS {name}: actionable diagnostic", flush=True)
        if not selected or "repl" in selected:
            repl_results = run_repl(binary, directory)
            print(
                "PASS repl: accepted live state across submissions and rejected store",
                flush=True,
            )
    print(
        json.dumps(
            {
                "mutable_user_programs": examples,
                "source_and_artifact_executions": stages,
                "assignment_constraint_diagnostics": invalid,
                "repl_mutable_results": repl_results,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
