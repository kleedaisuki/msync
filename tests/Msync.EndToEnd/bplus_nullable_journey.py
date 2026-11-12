#!/usr/bin/env python3
"""Check B+ nullable keys and values after splits, range publication and removal."""

from __future__ import annotations

import argparse
import tempfile
from pathlib import Path

from btree_bplus_journey import ROOT, execute

SOURCE: str = """let Tree = $std::containers::BPlusTree;
fn optional(flag: Bool) -> Number | Null { if flag { null } else { 0 } };
let carrier = decltype(optional(true));
let tree = Tree::of_types(carrier, carrier);
Tree::insert(tree, null, null);
for i in $std::ranges::sequence(0, 35, 1) { Tree::insert(tree, i, i); };
let answer? = Tree::get(tree, null);
if answer is not null {
    ((answer::value is null and Tree::height(tree) > 1) as String) -> ./found.txt;
};
let snapshot = Tree::range(tree, null, 4);
(Tree::validate(tree) as String) -> ./before.txt;
(Tree::remove(tree, null) as String) -> ./removed.txt;
((Tree::get(tree, null) is null and Tree::validate(tree)) as String) -> ./after.txt;
let keys = $std::ranges::transform(snapshot, fn(entry) {
    if entry::key is null { "null" } else { entry::key as String }
});
$std::string::join(keys, ",") -> ./range.txt;
"""

OUTPUTS: dict[str, bytes] = {
    "found.txt": b"true",
    "before.txt": b"true",
    "removed.txt": b"true",
    "after.txt": b"true",
    "range.txt": b"null,0,1,2,3",
}


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve(strict=True)
    directory: Path = ROOT / ".temp"
    directory.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="bplus-nullable-", dir=directory) as work:
        for stage in ("source", "relocatable", "executable"):
            execute(binary, Path(work) / stage, SOURCE, OUTPUTS, 2, stage)
            print(f"PASS BPlusTree nullable keys and snapshot: {stage}", flush=True)


if __name__ == "__main__":
    main()
