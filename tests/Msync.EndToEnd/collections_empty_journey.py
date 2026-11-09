#!/usr/bin/env python3
"""Exercise typed empty collection families through real CLI stages."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = """
let List = $std::containers::List;
let EqualitySet = $std::containers::EqualitySet;
let Map = $std::containers::Map;
let Stack = $std::containers::Stack;
let Queue = $std::containers::Queue;
fn assert(condition: Bool) {
    if condition {
        "ok"
    } else {
        "FAILED"
    }
};

let list = List::empty(0);
let set = EqualitySet::empty(0);
let map = Map::empty("", 0);
let nullable = Map::empty_nullable("", 0);
let stack = Stack::empty(0);
let queue = Queue::empty(0);
let equal = fn(a: String, b: String) {
    $std::string::to_lowercase(a) == $std::string::to_lowercase(b)
};

let custom_set = EqualitySet::with_equality(
    $std::ranges::take([""], 0),
    scope {
        Self: String,
        equal: equal
    }
);
let custom_map = Map::with_equality(
    $std::ranges::take([scope {
        key: "",
        value: 0
    }], 0),
    scope {
        Self: String,
        equal: equal
    }
);
let before = $std::ranges::materialize(
    [
        assert(List::is_empty(list)),
        assert(EqualitySet::is_empty(set)),
        assert(Map::is_empty(map)),
        assert(Map::is_empty(nullable)),
        assert(Stack::is_empty(stack)),
        assert(Queue::is_empty(queue)),
        assert(EqualitySet::is_empty(custom_set)),
        assert(Map::is_empty(custom_map)),
        assert(Stack::peek(stack) is null),
        assert(Stack::pop(stack) is null),
        assert(Queue::peek(queue) is null),
        assert(Queue::pop(queue) is null),
        assert(List::get(list, 10) is null)
    ]
);
List::push(list, 1);
EqualitySet::insert(set, 1);
Map::insert(map, "a", 1);
Map::insert(nullable, "a", null);
Stack::push(stack, 1);
Queue::push(queue, 1);
EqualitySet::insert(custom_set, "ALPHA");
EqualitySet::insert(custom_set, "alpha");
Map::insert(custom_map, "ALPHA", 1);
Map::insert(custom_map, "alpha", 2);
let after = $std::ranges::materialize(
    [
        assert(List::size(list) == 1),
        assert(EqualitySet::size(set) == 1),
        assert(Map::get_or(map, "a", 0) == 1),
        assert(Map::contains_key(nullable, "a")),
        assert(Map::get_or(nullable, "a", 7) is null),
        assert($std::ranges::matches(Stack::pop(stack), 1)),
        assert($std::ranges::matches(Queue::pop(queue), 1)),
        assert(EqualitySet::size(custom_set) == 1),
        assert(Map::size(custom_map) == 1),
        assert(Map::get_or(custom_map, "AlPhA", 0) == 2)
    ]
);
$std::io::write_text(
    ./result.txt,
    $std::string::join($std::ranges::concat(before, after), "|")
);
"""
NEGATIVE: tuple[tuple[str, str], ...] = (
    (
        (
            "let list = $std::containers::List::empty(0); "
            '$std::containers::List::push(list, "bad");'
        ),
        "E0203",
    ),
    (
        (
            'let map = $std::containers::Map::empty("", 0); '
            '$std::containers::Map::insert(map, "a", null);'
        ),
        "E0203",
    ),
    (
        (
            "let policy = scope { Self: Number, "
            "equal: fn(a: String, b: String) { a == b } }; "
            "$std::containers::Map::with_equality([scope { key: 0, value: 0 }], policy);"
        ),
        "E0214",
    ),
)


def run(command: Sequence[str], cwd: Path) -> None:
    result = subprocess.run(
        command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", check=False
    )
    if result.returncode:
        raise RuntimeError(f"{list(command)!r}\n{result.stdout}\n{result.stderr}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    command = parser.add_mutually_exclusive_group(required=True)
    command.add_argument("--cli", nargs="+")
    command.add_argument("--binary", type=Path)
    parser.add_argument("--inline-source", action="store_true")
    args = parser.parse_args()
    cli: list[str] = [str(args.binary.resolve())] if args.binary else args.cli
    prefix = (
        (ROOT / "stdlib/containers.msync").read_text() if args.inline_source else ""
    )

    def code(source: str) -> str:
        return prefix + source.replace("$std::containers::", "") if prefix else source

    temp = ROOT / ".temp"
    temp.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="collections-empty-", dir=temp) as name:
        work = Path(name)
        source = work / "empty.msync"
        source.write_text(code(SOURCE), encoding="utf-8")
        portable = work / "empty.mso"
        executable = work / "empty.mexe"
        run(
            [*cli, "compile", str(source), "-O2", "-o", str(portable), "--no-config"],
            work,
        )
        run(
            [*cli, "bind-ir", str(portable), "-o", str(executable), "--no-config"], work
        )
        for artifact in (source, portable, executable):
            run([*cli, "run", str(artifact), "-O2", "--no-config"], work)
            actual = (work / "result.txt").read_text(encoding="utf-8")
            if actual.split("|") != ["ok"] * 23:
                raise AssertionError(f"{artifact.suffix}: {actual}")
        for invalid, diagnostic in NEGATIVE:
            source.write_text(code(invalid), encoding="utf-8")
            result = subprocess.run(
                [*cli, "check", str(source), "--no-config"],
                cwd=work,
                capture_output=True,
                text=True,
                encoding="utf-8",
                check=False,
            )
            if not result.returncode or f"error[{diagnostic}]" not in result.stderr:
                raise AssertionError(result.stderr)
    print(
        "PASS: 8 empty families, 23 observations through 3 O2 CLI stages; 3 fixed-schema/comparer rejections"
    )


if __name__ == "__main__":
    main()
