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


EXAMPLES: tuple[Example, ...] = (
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
            'let object = scope { field?: "before", read: fn { field } }; let alias = object;',
            f'alias::field = null; {FAIL} object::field = "unreached";',
            'if object::read() is null { "prefix" } else { "stale" }',
            'alias::field = "after";',
            "object::read()",
        ),
        ("prefix", "after"),
    ),
    Example(
        "guarded-getter-contract",
        (
            'let object = scope { field?: "before", read: fn { if field is null { "empty" } else { field } } };',
            "object::field = null;",
            "object::read()",
            'object::field = "after";',
            "object::read()",
            "object::field = null;",
            "object::read()",
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
            'let object = scope { field?: "before", read: fn { match field { null => { "empty" }, _ => { field } } } };',
            "object::field = null;",
            "object::read()",
            'object::field = "after";',
            "object::read()",
        ),
        ("empty", "after"),
        errors=0,
    ),
    Example(
        "scope-method-write-prefix",
        (
            'let object = scope { field: "before", write: fn { field = "prefix"; }, read: fn { field } };',
            f'object::write(); {FAIL} object::field = "unreached";',
            "object::read()",
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
            'let object = scope { field: "before" };',
            f'object = scope {{ field?: "prefix" }}; {FAIL} object = scope {{ field: "later" }};',
            "object::field = null;",
            'if object::field is null { "prefix" } else { "stale" }',
        ),
        ("prefix",),
    ),
    Example(
        "constructor-instance-types",
        (
            "scope SomeType { new: fn { scope { field?: null } } };",
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
            "scope SomeType { new: fn { scope { field?: null } } };",
            'let object = SomeType::new(); object::field = "before";',
            f"object = SomeType::new(); {FAIL} object::field = true;",
            "object::field = false;",
            "object::field",
            'object::field = "rejected";',
        ),
        ("false",),
        errors=2,
    ),
    Example(
        "fresh-scope-prefix",
        (
            'let object = scope { old: "before" };',
            f'object = scope {{ field?: null }}; {FAIL} object = scope {{ later: "unreached" }};',
            'object::field = "after";',
            "object::field",
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
        timeout=30,
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
