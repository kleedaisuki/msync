#!/usr/bin/env python3
"""Exercise mutable source collections through the CLI's binary stages."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = r"""
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

let list = List::new([1, 2]);
let original = List::snapshot(list);
let alias = list;
List::push(alias, 3);
let checks = $std::ranges::materialize(
    [
        assert($std::ranges::equal(original, [1, 2])),
        assert($std::ranges::equal(List::snapshot(list), [1, 2, 3])),
        assert(List::set(list, 0, 9)),
        assert(List::insert(list, 1, 8)),
        assert(List::remove(list, 2)),
        assert(not List::set(list, 20, 8)),
        assert(List::get_or(list, 20, 7) == 7)
    ]
);
List::reverse(list);
List::sort_with(list, fn(a: Number, b: Number) {
    a < b
});
let removed = List::remove_where(list, fn(value: Number) {
    value > 7
});
let fresh = List::new([4]);
let set = EqualitySet::new([1, 2, 1]);
let set_before = EqualitySet::snapshot(set);
EqualitySet::union_with(set, [2, 3]);
EqualitySet::except_with(set, [1]);
EqualitySet::intersect_with(set, [3, 4]);
let map = Map::new(
    [
        scope {
            key: "a",
            value: 1
        },
        scope {
            key: "b",
            value: 2
        },
        scope {
            key: "a",
            value: 3
        }
    ]
);
let entries_before = Map::entries(map);
Map::insert(map, "b", 4);
let stack = Stack::new([1, 2]);
Stack::push(stack, 3);
let queue = Queue::new([1, 2]);
Queue::push(queue, 3);
let insensitive = fn(left: String, right: String) {
    $std::string::to_lowercase(left) == $std::string::to_lowercase(right)
};

let words = EqualitySet::with_equality(
    ["alpha", "ALPHA", "beta"],
    scope {
        Self: String,
        equal: insensitive
    }
);
let names = Map::with_equality(
    [
        scope {
            key: "alpha",
            value: 1
        },
        scope {
            key: "ALPHA",
            value: 2
        }
    ],
    scope {
        Self: String,
        equal: insensitive
    }
);
let nullable = Map::new(
    [
        scope {
            key: "a",
            value?: 1
        },
        scope {
            key: "b",
            value?: null
        }
    ]
);
Map::insert(nullable, "a", null);
let empty_stack = Stack::new($std::ranges::take([1], 0));
let empty_queue = Queue::new($std::ranges::take([1], 0));
let more = $std::ranges::materialize(
    [
        assert(removed == 2),
        assert($std::ranges::equal(List::snapshot(list), [3])),
        assert($std::ranges::equal(List::snapshot(fresh), [4])),
        assert($std::ranges::equal(set_before, [1, 2])),
        assert($std::ranges::equal(EqualitySet::snapshot(set), [3])),
        assert(EqualitySet::is_subset_of(set, [2, 3])),
        assert(EqualitySet::overlaps(set, [3, 4])),
        assert(Map::size(map) == 2),
        assert(Map::get_or(map, "a", 0) == 3),
        assert(Map::get_or(map, "b", 0) == 4),
        assert(Map::get_or(map, "missing", 7) == 7),
        assert(not Map::try_insert(map, "a", 9)),
        assert(Map::remove(map, "a")),
        assert(not Map::contains_key(map, "a")),
        assert($std::ranges::size(entries_before) == 2),
        assert($std::ranges::matches(Stack::pop(stack), 3)),
        assert($std::ranges::matches(Stack::peek(stack), 2)),
        assert($std::ranges::matches(Queue::pop(queue), 1)),
        assert($std::ranges::matches(Queue::peek(queue), 2)),
        assert(EqualitySet::size(words) == 2),
        assert(not EqualitySet::insert(words, "BETA")),
        assert(EqualitySet::contains(words, "Alpha")),
        assert(Map::size(names) == 1),
        assert(Map::get_or(names, "ALpHa", 0) == 2),
        assert(Map::contains_key(nullable, "a")),
        assert(Map::get_or(nullable, "a", 7) is null),
        assert(Stack::pop(empty_stack) is null),
        assert(Queue::pop(empty_queue) is null)
    ]
);
$std::io::write_text(
    ./result.txt,
    $std::string::join($std::ranges::concat(checks, more), "|")
);
"""


NEGATIVE: tuple[str, ...] = (
    'let values = $std::containers::List::new([1]); $std::containers::List::push(values, "wrong");',
    'let values = $std::containers::EqualitySet::new([1]); $std::containers::EqualitySet::insert(values, "wrong");',
    (
        'let values = $std::containers::Map::new([scope { key: "a", value: 1 }]); '
        "$std::containers::Map::insert(values, 3, 2);"
    ),
)


def run(command: Sequence[str], cwd: Path) -> None:
    result = subprocess.run(
        command, cwd=cwd, text=True, encoding="utf-8", capture_output=True, check=False
    )
    if result.returncode:
        raise RuntimeError(f"{list(command)!r}\n{result.stdout}\n{result.stderr}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", nargs="+", required=True)
    parser.add_argument("--level", type=int, choices=(0, 2, 3), action="append")
    args = parser.parse_args()
    cli: list[str] = args.cli
    temp = ROOT / ".temp"
    temp.mkdir(exist_ok=True)
    routes = 0
    with tempfile.TemporaryDirectory(prefix="collections-journey-", dir=temp) as name:
        work = Path(name)
        source = work / "journey.msync"
        source.write_text(SOURCE, encoding="utf-8")
        for level in args.level or (0, 2, 3):
            portable = work / f"journey-O{level}.mso"
            executable = work / f"journey-O{level}.mexe"
            run(
                [
                    *cli,
                    "compile",
                    str(source),
                    f"-O{level}",
                    "-o",
                    str(portable),
                    "--no-config",
                ],
                work,
            )
            run(
                [*cli, "bind-ir", str(portable), "-o", str(executable), "--no-config"],
                work,
            )
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
                cwd=work,
                text=True,
                encoding="utf-8",
                capture_output=True,
                check=False,
            )
            if result.returncode == 0 or "error[E0203]" not in result.stderr:
                raise AssertionError(f"fixed-schema case {index}: {result.stderr}")
    print(f"PASS: {routes} collection CLI/source/binary journeys, 35 observations each")


if __name__ == "__main__":
    main()
