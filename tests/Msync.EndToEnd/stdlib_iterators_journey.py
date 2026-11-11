#!/usr/bin/env python3
"""Run typed construction, explicit cursors and sorted algorithms across CLI stages."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Final

ROOT: Final = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Journey:
    name: str
    source: str
    expected: dict[str, str]


TYPED: Final = Journey(
    "typed-empty",
    """
let Set = $std::containers::Set;
let Map = $std::containers::Map;
let List = $std::containers::List;
let BTree = $std::containers::BTree;
let BPlusTree = $std::containers::BPlusTree;
let Buffer = $std::storage::Buffer;
let set = Set::new(Number);
(Set::size(set) as String) -> ./empty.txt;
Set::insert(set, 2);
Set::insert(set, 1);
Set::insert(set, 2);
($std::ranges::sum(Set::snapshot(set)) as String) -> ./set.txt;
let seeded = Set::new([3, 3, 6]);
($std::ranges::sum(Set::snapshot(seeded)) as String) -> ./seeded.txt;
let reverse_order = scope {
    Self: Number,
    sample?: null,
    less: fn(a: Number, b: Number) { a > b }
};
let reversed = Set::with_policy(Number, reverse_order);
Set::insert(reversed, 1);
Set::insert(reversed, 3);
($std::ranges::first(Set::snapshot(reversed)) as String) -> ./policy.txt;
let map = Map::of_types(String, Number);
Map::insert(map, "a", 3);
(Map::get_or(map, "a", 0) as String) -> ./map.txt;
let nullable = Map::of_types(String, $std::meta::add_null(Number));
Map::insert(nullable, "known-null", null);
((Map::get_or(nullable, "known-null", 5) is null) as String) -> ./nullable.txt;
let tree = BTree::of_types(Number, String);
BTree::insert(tree, 4, "b");
(BTree::validate(tree) as String) -> ./tree_valid.txt;
$std::ranges::front(BTree::entries(tree))::value -> ./tree.txt;
let plus = BPlusTree::of_types(Number, String);
BPlusTree::insert(plus, 2, "c");
(BPlusTree::validate(plus) as String) -> ./plus_valid.txt;
$std::ranges::front(BPlusTree::entries(plus))::value -> ./plus.txt;
let legacy = BPlusTree::new(7, "legacy");
BPlusTree::insert(legacy, 8, "value");
($std::ranges::matches(legacy::value_prototype, "legacy") as String)
    -> ./legacy.txt;
let items = Buffer::of_type(decltype(scope { label: "" }));
Buffer::push(items, scope { label: "shape" });
Buffer::get(items, 0)::label -> ./shape.txt;
let types = List::empty(Number);
List::push(types, String);
(List::get_or(types, 0, Number) as String) -> ./prototype.txt;
""",
    {
        "empty.txt": "0",
        "set.txt": "3",
        "seeded.txt": "9",
        "policy.txt": "3",
        "map.txt": "3",
        "nullable.txt": "true",
        "tree_valid.txt": "true",
        "tree.txt": "b",
        "plus_valid.txt": "true",
        "plus.txt": "c",
        "legacy.txt": "true",
        "shape.txt": "shape",
        "prototype.txt": "String",
    },
)

CURSORS: Final = Journey(
    "snapshot-cursors",
    """
let Cursor = $std::ranges::Cursor;
let Set = $std::containers::Set;
let set = Set::new([1, 4, 8]);
let at_begin = Set::cursor(set);
let other = Cursor::clone(at_begin);
Set::insert(set, 0);
Cursor::next(at_begin);
(Cursor::value(at_begin) as String) -> ./second.txt;
(Cursor::value(other) as String) -> ./clone.txt;
Cursor::advance(at_begin, 2);
(Cursor::has_value(at_begin) as String) -> ./end.txt;
Cursor::previous(at_begin);
(Cursor::value(at_begin) as String) -> ./previous.txt;
(Cursor::seek(at_begin, 0.5) as String) -> ./fraction.txt;
(Cursor::value(at_begin) as String) -> ./after_invalid.txt;
let nullable = Cursor::begin([null, 5]);
((Cursor::has_value(nullable) and Cursor::value(nullable) is null) as String)
    -> ./null_item.txt;
Cursor::next(nullable);
let item? = Cursor::value(nullable);
if item is not null { (item as String) -> ./not_null.txt; };
let bounds = $std::ranges::equal_range([1, 2, 2, 4], 2);
$std::string::join([(bounds::first as String), (bounds::last as String)], ":")
    -> ./bounds.txt;
($std::ranges::binary_search([1, 2, 4], 3) as String) -> ./miss.txt;
($std::ranges::binary_search([1, 2, 4], 4) as String) -> ./hit.txt;
($std::ranges::lower_bound([1, 2, 4], 8) as String) -> ./outside.txt;
($std::ranges::is_sorted([1, 4, 2]) as String) -> ./unsorted.txt;
""",
    {
        "second.txt": "4",
        "clone.txt": "1",
        "end.txt": "false",
        "previous.txt": "8",
        "fraction.txt": "false",
        "after_invalid.txt": "8",
        "null_item.txt": "true",
        "not_null.txt": "5",
        "bounds.txt": "1:3",
        "miss.txt": "false",
        "hit.txt": "true",
        "outside.txt": "3",
        "unsorted.txt": "false",
    },
)

ALGORITHMS: Final = Journey(
    "sorted-algorithms",
    """
let ranges = $std::ranges;
fn text(values: Range) {
    $std::string::join(ranges::map(values, fn(value) { value as String }), ",")
};
let left = [1, 1, 3, 5];
let right = [1, 2, 2, 5, 8];
text(ranges::merge(left, right)) -> ./merge.txt;
text(ranges::set_union(left, right)) -> ./union.txt;
text(ranges::set_intersection(left, right)) -> ./intersection.txt;
text(ranges::set_difference(left, right)) -> ./difference.txt;
text(ranges::set_symmetric_difference(left, right)) -> ./symmetric.txt;
text(ranges::merge(ranges::empty_of(Number), right)) -> ./empty.txt;
let counts = scope { left: 0, right: 0, compare: 0 };
let first = ranges::map([1, 3], fn(value: Number) {
    counts::left = counts::left + 1;
    value
});
let second = ranges::map([2, 4], fn(value: Number) {
    counts::right = counts::right + 1;
    value
});
let lazy = ranges::merge_with(first, second, fn(a: Number, b: Number) {
    counts::compare = counts::compare + 1;
    a < b
});
text([counts::left, counts::right, counts::compare]) -> ./cold.txt;
text(ranges::take(lazy, 0)) -> ./zero.txt;
text([counts::left, counts::right, counts::compare]) -> ./still_cold.txt;
(ranges::first(lazy) as String) -> ./first.txt;
text([counts::left, counts::right, counts::compare]) -> ./after_first.txt;
text(lazy) -> ./lazy.txt;
text([counts::left, counts::right, counts::compare]) -> ./after_all.txt;
let original = scope { key: 1, label: "original" };
let joined = ranges::merge_with(
    [original],
    [scope { key: 1, label: "later" }],
    fn(a, b) { a::key < b::key }
);
let member = ranges::first(joined);
member::label -> ./stable.txt;
member::label = "changed";
original::label -> ./alias.txt;
""",
    {
        "merge.txt": "1,1,1,2,2,3,5,5,8",
        "union.txt": "1,1,2,2,3,5,8",
        "intersection.txt": "1,5",
        "difference.txt": "1,3",
        "symmetric.txt": "1,2,2,3,8",
        "empty.txt": "1,2,2,5,8",
        "cold.txt": "0,0,0",
        "zero.txt": "",
        "still_cold.txt": "0,0,0",
        "first.txt": "1",
        "after_first.txt": "1,1,1",
        "lazy.txt": "1,2,3,4",
        "after_all.txt": "3,3,4",
        "stable.txt": "original",
        "alias.txt": "changed",
    },
)

TREE_PAGES: Final = Journey(
    "typed-tree-pages",
    """
let Tree = $std::containers::BPlusTree;
let tree = Tree::of_types(Number, String);
for key in $std::ranges::iota(0, 20) { Tree::insert(tree, key, "value"); };
((Tree::height(tree) == 2 and Tree::validate(tree)) as String) -> ./split.txt;
Tree::remove(tree, 0);
Tree::remove(tree, 1);
((Tree::size(tree) == 18 and Tree::validate(tree)) as String) -> ./erased.txt;
($std::ranges::size(Tree::range(tree, 3, 6)) as String) -> ./selected.txt;
Tree::clear(tree);
((Tree::is_empty(tree) and Tree::validate(tree)) as String) -> ./cleared.txt;
""",
    {
        "split.txt": "true",
        "erased.txt": "true",
        "selected.txt": "3",
        "cleared.txt": "true",
    },
)

JOURNEYS: Final = (TYPED, CURSORS, ALGORITHMS, TREE_PAGES)

NEGATIVE: Final[tuple[tuple[str, str], ...]] = (
    (
        'let Set = $std::containers::Set; let set = Set::new(Number); Set::insert(set, "wrong");',
        "E0203",
    ),
    (
        'let Map = $std::containers::Map; let map = Map::of_types(String, Number); Map::insert(map, "key", null);',
        "E0203",
    ),
    (
        "let choice = if exists ./missing { Number } else { String }; $std::ranges::empty_of(choice);",
        "E0220",
    ),
)


def invoke(binary: Path, work: Path, *arguments: str, diagnostic: str = "") -> None:
    result = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=work,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=120,
        check=False,
    )
    if diagnostic:
        if result.returncode != 0 and diagnostic in result.stderr:
            return
    elif result.returncode == 0:
        return
    raise AssertionError((arguments, result.returncode, result.stdout, result.stderr))


def verify(work: Path, expected: dict[str, str]) -> None:
    for name, value in expected.items():
        target: Path = work / name
        actual: bytes = target.read_bytes()
        assert actual == value.encode(), (name, value, actual)
        target.unlink()


def journey(binary: Path, work: Path, case: Journey, level: int) -> None:
    work.mkdir(parents=True)
    source: Path = work / "journey.msync"
    source.write_text(case.source, encoding="utf-8", newline="\n")
    invoke(binary, work, "run", source.name, f"-O{level}")
    verify(work, case.expected)
    invoke(binary, work, "compile", source.name, "--emit", "hir", "-o", "journey.mhir")
    source.unlink()
    invoke(binary, work, "compile", "journey.mhir", f"-O{level}", "-o", "journey.mso")
    invoke(binary, work, "run-ir", "journey.mso")
    verify(work, case.expected)
    invoke(binary, work, "bind-ir", "journey.mso", "-o", "journey.mexe")
    invoke(binary, work, "run-ir", "journey.mexe")
    verify(work, case.expected)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--level", type=int, choices=(0, 1, 2, 3), default=2)
    parser.add_argument(
        "--case", action="append", choices=tuple(case.name for case in JOURNEYS)
    )
    args = parser.parse_args()
    binary: Path = args.binary.resolve()
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="stdlib-iterator-", dir=scratch
    ) as directory:
        work = Path(directory)
        for case in JOURNEYS:
            if not args.case or case.name in args.case:
                journey(binary, work / case.name, case, args.level)
                print(
                    f"PASS: {case.name} through source and source-free HIR/bytecode at O{args.level}",
                    flush=True,
                )
        for index, (source, diagnostic) in enumerate(NEGATIVE):
            file: Path = work / f"negative-{index}.msync"
            file.write_text(source, encoding="utf-8", newline="\n")
            invoke(binary, work, "check", file.name, diagnostic=diagnostic)
        print(
            f"PASS: {len(NEGATIVE)} static witness and carrier rejections", flush=True
        )


if __name__ == "__main__":
    main()
