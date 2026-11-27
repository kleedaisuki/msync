#!/usr/bin/env python3
"""Observe accepted mutable state after failed real REPL submissions."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FAIL = "$std::fs::canonical(./missing-file);"


@dataclass(frozen=True)
class Example:
    name: str
    submissions: tuple[str, ...]
    expected: tuple[str, ...]
    errors: int = 1
    timeout: float = 30


EXAMPLES: tuple[Example, ...] = (
    Example(
        "ieee-feedback",
        (
            "let number = 0; fn invert() { global number; number = -0; };",
            "invert();",
            "1 / number < 0",
            "number = 0;",
            "1 / number > 0",
            "let record = object { value: 0, set: fn { value = -0; }, read: fn { 1 / value } };",
            "record::set(); record::read() < 0",
            "let alias = record; alias::value = 0; record::read() > 0",
            "alias::value = -0; record::read() < 0",
            "let copy = record::value;",
            "alias::value = 0; 1 / copy < 0",
            "record::read() > 0",
            "let nan = 0 / 0; fn hold() { nan };",
            "hold() != hold()",
        ),
        ("true",) * 8,
        errors=0,
    ),
    Example(
        "aged-state-snapshots",
        (
            'let value? = "before"; fn read() { value }; let record = object { field?: "before" }; let alias = record;',
            *(
                f'let aged_{index} = object {{ text: "row", path: ./item, flag: true }};'
                for index in range(1, 1025)
            ),
            'decltype({ value = "query-only"; value })',
            "read()",
            "value = null; read() is null",
            f'value = "prefix"; record::field = "prefix"; {FAIL} value = "unreached";',
            "read()",
            "alias::field",
            "aged_1::text",
            "aged_1024::flag",
            "value = true;",
            "read()",
            "let value = 0; read()",
            "value",
        ),
        (
            "String",
            "before",
            "true",
            "prefix",
            "prefix",
            "row",
            "true",
            "prefix",
            "prefix",
            "0",
        ),
        errors=2,
        timeout=120,
    ),
    Example(
        "planned-repl-io",
        (
            '"one" -> ./shared;',
            "$std::io::read_text(./shared)",
            '"two" -> ./shared;',
            "$std::io::read_text(./shared)",
            "let destination = ./first-output;",
            '"one" -> destination;',
            "destination = ./second-output;",
            '"two" -> destination;',
            "$std::io::read_text(./first-output)",
            "$std::io::read_text(./second-output)",
        ),
        ("one", "two", "one", "two"),
        errors=0,
    ),
    Example(
        "nullable-prefix",
        (
            'let value? = "before";',
            f'value = null; {FAIL} value = "unreached";',
            'if value is null { "prefix" } else { "stale" }',
        ),
        ("prefix",),
    ),
    Example(
        "callable-prefix",
        (
            'let action = fn { "before" };',
            f'action = fn {{ "prefix" }}; {FAIL} action = fn {{ "unreached" }};',
            "action()",
        ),
        ("prefix",),
    ),
    Example(
        "captured-callable-prefix",
        (
            'let captured = "before"; let action = fn { "old" };',
            f'action = fn {{ captured }}; {FAIL} action = fn {{ "unreached" }};',
            'captured = "after";',
            "action()",
        ),
        ("after",),
    ),
    Example(
        "scope-alias-null-prefix",
        (
            'let record = object { field?: "before", read: fn { field } }; let alias = record;',
            f'alias::field = null; {FAIL} record::field = "unreached";',
            'if record::read() is null { "prefix" } else { "stale" }',
            'alias::field = "after";',
            "record::read()",
        ),
        ("prefix", "after"),
    ),
    Example(
        "guarded-getter-contract",
        (
            'let record = object { field?: "before", read: fn { if field is null { "empty" } else { field } } };',
            "record::field = null;",
            "record::read()",
            'record::field = "after";',
            "record::read()",
            "record::field = null;",
            "record::read()",
        ),
        ("empty", "after", "empty"),
        errors=0,
    ),
    Example(
        "scalar-getter-contract",
        (
            'let field? = "before"; fn read() { field };',
            "field = null;",
            "read() is null",
            'field = "after";',
            "read()",
        ),
        ("true", "after"),
        errors=0,
    ),
    Example(
        "match-getter-contract",
        (
            'let record = object { field?: "before", read: fn { match field { null => { "empty" }, _ => { field } } } };',
            "record::field = null;",
            "record::read()",
            'record::field = "after";',
            "record::read()",
        ),
        ("empty", "after"),
        errors=0,
    ),
    Example(
        "scope-method-write-prefix",
        (
            'let record = object { field: "before", write: fn { field = "prefix"; }, read: fn { field } };',
            f'record::write(); {FAIL} record::field = "unreached";',
            "record::read()",
        ),
        ("prefix",),
    ),
    Example(
        "new-declaration-prefix",
        (
            f'let completed = "prefix"; {FAIL} let unfinished = "unreached";',
            "completed",
            "decltype(unfinished)",
        ),
        ("prefix", "Path"),
    ),
    Example(
        "compile-rejection",
        (
            'let value = "before";',
            'let hidden = "unexecuted"; let invalid = null;',
            "decltype(hidden)",
            "value",
        ),
        ("Path", "before"),
    ),
    Example(
        "fixed-type-after-failure",
        (
            'let value? = "before";',
            f'value = null; {FAIL} value = "unreached";',
            "value = true;",
            'value = "after";',
            "value",
        ),
        ("after",),
        errors=2,
    ),
    Example(
        "accepted-capture-location",
        (
            'let value = "before"; let action = fn { value };',
            'value = "after";',
            'let value = "shadow";',
            f'{FAIL} value = "unreached";',
            "action()",
            "value",
        ),
        ("after", "shadow"),
    ),
    Example(
        "same-fields-different-nullability",
        (
            'let record = object { field: "before" };',
            f'record = object {{ field?: "prefix" }}; {FAIL} record = object {{ field: "later" }};',
            "record::field = null;",
            'if record::field is null { "prefix" } else { "stale" }',
        ),
        ("prefix",),
    ),
    Example(
        "constructor-instance-types",
        (
            "object SomeType { new: fn { object { field?: null } } };",
            'let first = SomeType::new(); first::field = "before";',
            "let second = SomeType::new(); second::field = true;",
            f'first::field = "prefix"; second::field = false; {FAIL} first::field = "unreached"; second::field = true;',
            "first::field",
            "second::field",
            "first::field = true;",
            'second::field = "rejected";',
            'first::field = "after";',
            "second::field = true;",
            "first::field",
            "second::field",
        ),
        ("prefix", "false", "after", "true"),
        errors=3,
    ),
    Example(
        "fresh-constructor-type-prefix",
        (
            "object SomeType { new: fn { object { field?: null } } };",
            'let record = SomeType::new(); record::field = "before";',
            f"record = SomeType::new(); {FAIL} record::field = true;",
            "record::field = false;",
            "record::field",
            'record::field = "rejected";',
        ),
        ("false",),
        errors=2,
    ),
    Example(
        "fresh-scope-prefix",
        (
            'let record = object { old: "before" };',
            f'record = object {{ field?: null }}; {FAIL} record = object {{ later: "unreached" }};',
            'record::field = "after";',
            "record::field",
        ),
        ("after",),
    ),
)


def run(binary: Path, example: Example, work: Path) -> None:
    source = "\n".join((*example.submissions, "/quit", ""))
    result = subprocess.run(
        [str(binary), "repl", "--color", "never"],
        cwd=work,
        input=source,
        text=True,
        capture_output=True,
        timeout=example.timeout,
        check=False,
    )
    actual = tuple(result.stdout.splitlines())
    if result.returncode != 0 or actual != example.expected:
        raise AssertionError(
            f"{example.name}: expected {example.expected}, got {actual}; "
            f"exit={result.returncode}\n{result.stderr}"
        )
    errors = sum(line.startswith("error") for line in result.stderr.splitlines())
    if errors != example.errors:
        raise AssertionError(
            f"{example.name}: expected {example.errors} selected errors, got {errors}\n"
            f"{result.stderr}"
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--case", action="append", default=[], dest="cases")
    args = parser.parse_args()
    binary = args.binary.resolve()
    selected: set[str] = set(args.cases)
    unknown: set[str] = selected - {example.name for example in EXAMPLES}
    if unknown:
        raise ValueError(f"Unknown cases: {sorted(unknown)}")
    examples: tuple[Example, ...] = tuple(
        example for example in EXAMPLES if not selected or example.name in selected
    )
    temp = ROOT / ".temp"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="repl-state-", dir=temp) as directory:
        work = Path(directory)
        for example in examples:
            run(binary, example, work)
    print(f"PASS: {len(examples)} real REPL mutable prefix/recovery journeys")


if __name__ == "__main__":
    main()
