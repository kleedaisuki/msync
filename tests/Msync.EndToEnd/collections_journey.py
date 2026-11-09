#!/usr/bin/env python3
"""Exercise mutable source collections through the CLI's binary stages."""
from __future__ import annotations

import argparse
import subprocess
import tempfile
from pathlib import Path
from collections.abc import Sequence

ROOT = Path(__file__).resolve().parents[2]
SOURCE = r'''
fn assert(condition: Bool) {
    if condition { "ok" } else { "FAILED" }
};
let list = $std::collections::List([1, 2]);
let original = list::snapshot();
let alias = list;
alias::add(3);
let checks = $std::ranges::materialize([
    assert($std::ranges::equal(original, [1, 2])),
    assert($std::ranges::equal(list::snapshot(), [1, 2, 3])),
    assert(list::set(0, 9)),
    assert(list::insert(1, 8)),
    assert(list::remove_at(2)),
    assert(not list::set(20, 8)),
    assert(list::get_or(20, 7) == 7)
]);
list::reverse();
list::sort_with(fn(a: Number, b: Number) { a < b });
let removed = list::remove_where(fn(value: Number) { value > 7 });
let fresh = $std::collections::List([4]);
let set = $std::collections::Set([1, 2, 1]);
let set_before = set::snapshot();
set::union_with([2, 3]);
set::except_with([1]);
set::intersect_with([3, 4]);
let map = $std::collections::Map([
    scope { key: "a", value: 1 },
    scope { key: "b", value: 2 },
    scope { key: "a", value: 3 }
]);
let entries_before = map::entries();
map::put("b", 4);
let stack = $std::collections::Stack([1, 2]);
stack::push(3);
let queue = $std::collections::Queue([1, 2]);
queue::enqueue(3);
let insensitive = fn(left: String, right: String) {
    $std::string::to_lowercase(left) == $std::string::to_lowercase(right)
};
let words = $std::collections::SetWith(["alpha", "ALPHA", "beta"], insensitive);
let names = $std::collections::MapWith([
    scope { key: "alpha", value: 1 },
    scope { key: "ALPHA", value: 2 }
], insensitive);
let nullable = $std::collections::Map([
    scope { key: "a", value?: 1 }, scope { key: "b", value?: null }
]);
nullable::put("a", null);
let empty_stack = $std::collections::Stack($std::ranges::take([1], 0));
let empty_queue = $std::collections::Queue($std::ranges::take([1], 0));
let more = $std::ranges::materialize([
    assert(removed == 2),
    assert($std::ranges::equal(list::snapshot(), [3])),
    assert($std::ranges::equal(fresh::snapshot(), [4])),
    assert($std::ranges::equal(set_before, [1, 2])),
    assert($std::ranges::equal(set::snapshot(), [3])),
    assert(set::is_subset_of([2, 3])),
    assert(set::overlaps([3, 4])),
    assert(map::size() == 2),
    assert(map::get_or("a", 0) == 3),
    assert(map::get_or("b", 0) == 4),
    assert(map::get_or("missing", 7) == 7),
    assert(not map::try_add("a", 9)),
    assert(map::remove("a")),
    assert(not map::contains_key("a")),
    assert($std::ranges::size(entries_before) == 2),
    assert($std::ranges::matches(stack::pop(), 3)),
    assert($std::ranges::matches(stack::peek(), 2)),
    assert($std::ranges::matches(queue::dequeue(), 1)),
    assert($std::ranges::matches(queue::peek(), 2)),
    assert(words::size() == 2),
    assert(not words::add("BETA")),
    assert(words::contains("Alpha")),
    assert(names::size() == 1),
    assert(names::get_or("ALpHa", 0) == 2),
    assert(nullable::contains_key("a")),
    assert(nullable::get_or("a", 7) is null),
    assert(empty_stack::pop() is null),
    assert(empty_queue::dequeue() is null)
]);
$std::io::write_text(./result.txt,
    $std::string::join($std::ranges::concat(checks, more), "|"));
'''


NEGATIVE: tuple[str, ...] = (
    'let values = $std::collections::List([1]); values::add("wrong");',
    'let values = $std::collections::Set([1]); values::add("wrong");',
    'let values = $std::collections::Map([scope { key: "a", value: 1 }]); '
    'values::put(3, 2);',
)


def run(command: Sequence[str], cwd: Path) -> None:
    result = subprocess.run(command, cwd=cwd, text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError(f"{list(command)!r}\n{result.stdout}\n{result.stderr}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", nargs="+", required=True)
    args = parser.parse_args()
    cli: list[str] = args.cli
    temp = ROOT / ".temp"
    temp.mkdir(exist_ok=True)
    routes = 0
    with tempfile.TemporaryDirectory(prefix="collections-journey-", dir=temp) as name:
        work = Path(name)
        source = work / "journey.msync"
        source.write_text(SOURCE, encoding="utf-8")
        for level in (0, 2, 3):
            portable = work / f"journey-O{level}.mso"
            executable = work / f"journey-O{level}.mexe"
            run([*cli, "compile", str(source), f"-O{level}", "-o", str(portable), "--no-config"], work)
            run([*cli, "bind-ir", str(portable), "-o", str(executable), "--no-config"], work)
            for artifact in (source, portable, executable):
                run([*cli, "run", str(artifact), f"-O{level}", "--no-config"], work)
                actual = (work / "result.txt").read_text(encoding="utf-8")
                lines = actual.split("|")
                if len(lines) != 35 or any(line != "ok" for line in lines):
                    raise AssertionError(f"O{level} {artifact.suffix}: {actual}")
                routes += 1
        for index, invalid in enumerate(NEGATIVE):
            source.write_text(invalid, encoding="utf-8")
            result = subprocess.run(
                [*cli, "check", str(source), "--no-config", "--color", "never"],
                cwd=work, text=True, capture_output=True,
            )
            if result.returncode == 0 or "error[E0203]" not in result.stderr:
                raise AssertionError(f"fixed-schema case {index}: {result.stderr}")
    print(f"PASS: {routes} collection CLI/source/binary journeys, 35 observations each")


if __name__ == "__main__":
    main()
