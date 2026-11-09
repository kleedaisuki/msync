#!/usr/bin/env python3
"""Exercise shared collection families and typed policies through the real CLI."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from pathlib import Path

from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]

PROGRAM: str = r"""
let calls = 0;
let storage = scope {
    snapshot: fn(values: Range) -> Range {
        calls = calls + 1;
        $std::ranges::materialize(values)
    },
};
let items = $std::containers::List::with-storage([1, 2], storage);
let alias = items;
let before = $std::containers::List::snapshot(items);
$std::containers::List::push(items, 3);
$std::containers::List::insert(items, 1, 9);
$std::containers::List::set(items, 0, 8);
$std::containers::List::remove(items, 2);
$std::containers::List::reverse(items);
$std::containers::List::sort-with(items, fn(a: Number, b: Number) { a < b });
$std::string::join($std::ranges::map(alias::items,
    fn(value: Number) { value as String }), ",") -> ./list.txt;
$std::string::join($std::ranges::map(before,
    fn(value: Number) { value as String }), ",") -> ./before.txt;
(calls as String) -> ./storage-calls.txt;
let separate = $std::containers::List::empty(0);
$std::containers::List::push(separate, 44);
($std::containers::List::get-or(separate, 0, 0) as String) -> ./fresh.txt;
scope FoldCase {
    Self: String,
    equal: fn(a: String, b: String) -> Bool {
        $std::string::to_lowercase(a) == $std::string::to_lowercase(b)
    },
};
let names = $std::containers::Set::with-equality(["Klee", "klee", "Alice"],
    FoldCase);
$std::containers::Set::insert(names, "ALICE");
$std::containers::Set::insert(names, "Bob");
$std::containers::Set::remove(names, "aLiCe");
$std::string::join($std::containers::Set::snapshot(names), ",") -> ./set.txt;
let records = $std::containers::Map::with-equality([
    scope { key: "Klee", value: 1 },
    scope { key: "Alice", value: 2 },
    scope { key: "KLEE", value: 3 },
], FoldCase);
$std::containers::Map::insert(records, "bob", 4);
($std::containers::Map::get-or(records, "klee", 0) as String) -> ./map.txt;
$std::string::join($std::containers::Map::keys(records), ",") -> ./keys.txt;
let map_before = $std::containers::Map::entries(records);
$std::containers::Map::remove(records, "ALICE");
($std::ranges::size(map_before) as String) -> ./map-before.txt;
let nullable = $std::containers::Map::empty-nullable("", 0);
$std::containers::Map::insert(nullable, "missing", null);
$std::containers::Map::insert(nullable, "present", 8);
if $std::containers::Map::contains-key(nullable, "missing")
    and $std::containers::Map::get(nullable, "missing") is null {
    "nullable" -> ./nullable.txt;
};
let lifo = $std::containers::Stack::empty(0);
if $std::containers::Stack::peek(lifo) is null {
    $std::containers::Stack::push(lifo, 7);
};
let popped? = $std::containers::Stack::pop(lifo);
if popped is not null { (popped as String) -> ./stack.txt; };
if $std::containers::Stack::peek(lifo) is null {
    "empty" -> ./stack-empty.txt;
};
let fifo = $std::containers::Queue::new([5, 6]);
$std::containers::Queue::push(fifo, 7);
let dequeued? = $std::containers::Queue::pop(fifo);
if dequeued is not null { (dequeued as String) -> ./queue.txt; };
let head? = $std::containers::Queue::peek(fifo);
if head is not null { (head as String) -> ./queue-peek.txt; };
let previous = $std::collections::List(["old"]);
previous::add("works");
$std::string::join(previous::snapshot(), ",") -> ./compatibility.txt;
acquire;
"""

OUTPUTS: dict[str, bytes] = {
    "list.txt": b"3,8,9",
    "before.txt": b"1,2",
    "storage-calls.txt": b"7",
    "fresh.txt": b"44",
    "set.txt": b"Klee,Bob",
    "map.txt": b"3",
    "keys.txt": b"Klee,Alice,bob",
    "map-before.txt": b"3",
    "nullable.txt": b"nullable",
    "stack.txt": b"7",
    "stack-empty.txt": b"empty",
    "queue.txt": b"5",
    "queue-peek.txt": b"6",
    "compatibility.txt": b"old,works",
}

NEGATIVE: tuple[tuple[str, str, tuple[str, ...]], ...] = (
    (
        "storage-return",
        (
            "let policy = scope { snapshot: fn(values: Range) { true } }; "
            "$std::containers::List::with-storage([1], policy);"
        ),
        ("snapshot", "Range"),
    ),
    (
        "equality-return",
        (
            "let policy = scope { Self: String, "
            "equal: fn(a: String, b: String) { 1 } }; "
            '$std::containers::Set::with-equality(["x"], policy);'
        ),
        ("equal", "Bool"),
    ),
    (
        "fixed-storage",
        (
            "let values = $std::containers::List::new([1]); "
            '$std::containers::List::push(values, "wrong");'
        ),
        ("Number", "String"),
    ),
)


def execute(binary: Path, parent: Path, levels: list[int]) -> tuple[int, int]:
    executions: int = 0
    observations: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            source: Path = directory / "collections.msync"
            source.write_text(PROGRAM, encoding="utf-8")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"), "check"
            )
            artifact: Path = source
            if stage != "source":
                artifact = source.with_suffix(".mso")
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
                        "bind",
                    )
                    artifact = bound
                source.unlink()
            result: subprocess.CompletedProcess[str] = invoke(
                binary, directory, "run", str(artifact), f"-O{level}"
            )
            accept(result, f"{stage} execution")
            for name, expected in OUTPUTS.items():
                actual: bytes = (directory / name).read_bytes()
                if actual != expected:
                    raise AssertionError(
                        f"O{level}/{stage}/{name}: {actual!r} != {expected!r}"
                    )
                observations += 1
            executions += 1
    return executions, observations


def reject(binary: Path, parent: Path) -> int:
    for name, program, fragments in NEGATIVE:
        source: Path = parent / f"{name}.msync"
        source.write_text(program, encoding="utf-8")
        result: subprocess.CompletedProcess[str] = invoke(
            binary, parent, "check", str(source)
        )
        diagnostic: str = result.stdout + result.stderr
        if result.returncode == 0 or not all(
            fragment in diagnostic for fragment in fragments
        ):
            raise AssertionError(f"Expected {name} rejection: {diagnostic}")
    return len(NEGATIVE)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--levels", default="0,2,3")
    parser.add_argument("--output", type=Path)
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve()
    levels: list[int] = [int(value) for value in options.levels.split(",")]
    temporary: Path = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="collections-policy-", dir=temporary
    ) as work:
        parent: Path = Path(work)
        executions: int
        observations: int
        executions, observations = execute(binary, parent, levels)
        negatives: int = reject(binary, parent)
    report: dict[str, object] = {
        "binary": str(binary),
        "staged_executions": executions,
        "byte_observations": observations,
        "negative_diagnostics": negatives,
    }
    payload: str = json.dumps(report, indent=2)
    if options.output is not None:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(payload + "\n", encoding="utf-8")
    print(payload)


if __name__ == "__main__":
    main()
