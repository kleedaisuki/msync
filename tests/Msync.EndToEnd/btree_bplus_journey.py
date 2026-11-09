#!/usr/bin/env python3
"""Exercise source-defined B/B+ tree pages against an independent map model."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

from stdlib_experience_journey import accept

ROOT: Path = Path(__file__).resolve().parents[2]


def invoke(
    binary: Path, directory: Path, *arguments: str
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
        timeout=180,
    )


@dataclass(frozen=True)
class Mutation:
    insert: bool
    key: int
    value: int
    changed: bool
    size: int
    present: bool
    current: int


def trace() -> tuple[list[Mutation], dict[int, int]]:
    randomizer: random.Random = random.Random(0xB7EE)
    model: dict[int, int] = {}
    mutations: list[Mutation] = []

    def add(insert: bool, key: int, value: int) -> None:
        changed: bool = key not in model if insert else key in model
        if insert:
            model[key] = value
        else:
            model.pop(key, None)
        mutations.append(
            Mutation(
                insert, key, value, changed, len(model), key in model, model.get(key, 0)
            )
        )

    keys: list[int] = list(range(260))
    randomizer.shuffle(keys)
    for key in keys:
        add(True, key, key * 3 + 1)
    for key in keys[::11]:
        add(True, key, key * 7 + 2)
    randomizer.shuffle(keys)
    for key in keys:
        add(False, key, 0)
    add(False, 500, 0)
    for key in range(80):
        add(True, key, key * 5 + 3)
    for _ in range(120):
        key: int = randomizer.randrange(100)
        add(randomizer.randrange(3) != 0, key, randomizer.randrange(10000))
    return mutations, model


def tree_program(family: str, definitions: str = "") -> tuple[str, dict[str, bytes]]:
    mutations, model = trace()
    records: list[str] = [
        "scope { insert: "
        + str(item.insert).lower()
        + ", key: "
        + str(item.key)
        + ", value: "
        + str(item.value)
        + ", changed: "
        + str(item.changed).lower()
        + ", size: "
        + str(item.size)
        + ", present: "
        + str(item.present).lower()
        + ", current: "
        + str(item.current)
        + " }"
        for item in mutations
    ]
    selected: str = family if definitions else "$std::containers::" + family
    program: str = (
        definitions
        + "\nlet Tree = "
        + selected
        + ";\n"
        + r"""
let tree = Tree::new(0, 0);
let alias = tree;
fn text(value?) { if value is null { "null" } else { value as String } };
let failures = 0;
let peak_height = 1;
let mutations = [
"""
        + ",\n".join(records)
        + r"""
];
for mutation in mutations {
    let changed = if mutation::insert {
        Tree::insert(alias, mutation::key, mutation::value)
    } else { Tree::remove(alias, mutation::key) };
    if changed != mutation::changed { failures = failures + 1; };
    if Tree::size(tree) != mutation::size { failures = failures + 1; };
    if not Tree::validate(tree) { failures = failures + 1; };
    let result? = Tree::get(tree, mutation::key);
    if mutation::present {
        if result is null { failures = failures + 1; } else {
            if result::value != mutation::current {
                failures = failures + 1;
            };
        };
    } else {
        if result is not null { failures = failures + 1; };
    };
    if Tree::height(tree) > peak_height {
        peak_height = Tree::height(tree);
    };
};
(failures as String) -> ./failures.txt;
((peak_height >= 3) as String) -> ./height.txt;
let snapshot = Tree::entries(tree);
$std::string::join($std::ranges::map(snapshot, fn(entry) {
    $std::string::concat($std::string::concat(text(entry::key), ":"),
        text(entry::value))
}), ",") -> ./entries.txt;
$std::string::join($std::ranges::map(Tree::range(tree, 23, 57), fn(entry) {
    $std::string::concat($std::string::concat(text(entry::key), ":"),
        text(entry::value))
}), ",") -> ./range.txt;
Tree::clear(tree);
((Tree::is_empty(alias) and Tree::validate(alias)
    and Tree::page_count(alias) == 1) as String) -> ./cleared.txt;
($std::ranges::size(snapshot) as String) -> ./snapshot.txt;
Tree::insert(tree, 999, 42);
let restored? = Tree::get(alias, 999);
if restored is not null {
    text(restored::value) -> ./fresh.txt;
};
let folded = Tree::with_policy("", 0, scope {
    sample: "",
    less: fn(left: String, right: String) {
        $std::string::to_lowercase(left) < $std::string::to_lowercase(right)
    },
});
Tree::insert(folded, "Alpha", 1);
let replaced = not Tree::insert(folded, "ALPHA", 9);
Tree::insert(folded, "beta", 2);
let folded_value? = Tree::get(folded, "alpha");
if folded_value is not null {
    ((replaced and folded_value::value == 9 and Tree::size(folded) == 2
        and Tree::validate(folded)) as String) -> ./equality-policy.txt;
};
let records = Tree::with_policy(scope { group: "", id: 0 }, 0, scope {
    sample: scope { group: "", id: 0 },
    less: fn(left: Scope, right: Scope) {
        if left::group == right::group { left::id < right::id } else {
            left::group < right::group
        }
    },
});
Tree::insert(records, scope { group: "b", id: 2 }, 22);
Tree::insert(records, scope { group: "a", id: 3 }, 13);
Tree::insert(records, scope { group: "a", id: 1 }, 11);
let selected_record? = Tree::get(records, scope { group: "a", id: 3 });
if selected_record is not null {
    ((selected_record::value == 13 and Tree::validate(records)) as String)
        -> ./record-policy.txt;
};
let ieee = Tree::new(0, 0);
let nan = 0 / 0;
Tree::insert(ieee, -0, 1);
let same_zero = not Tree::insert(ieee, 0, 2);
Tree::insert(ieee, nan, 3);
Tree::insert(ieee, 5, 4);
let nan_value? = Tree::get(ieee, nan);
if nan_value is not null {
    ((same_zero and Tree::size(ieee) == 3 and nan_value::value == 3
        and Tree::validate(ieee)) as String) -> ./ieee-policy.txt;
};
let churn = Tree::new(0, 0);
for key in $std::ranges::iota(0, 64) { Tree::insert(churn, key, key); };
let pages = $std::storage::Buffer::snapshot(churn::nodes);
let capacity = $std::storage::Buffer::size(churn::nodes);
for key in $std::ranges::iota(0, 64) { Tree::remove(churn, key); };
for key in $std::ranges::iota(0, 64) { Tree::insert(churn, key, key * 2); };
let reused = 0;
for page in pages {
    if $std::storage::Buffer::size(page::keys) > 0 { reused = reused + 1; };
};
((reused == Tree::page_count(churn)
    and capacity == $std::storage::Buffer::size(churn::nodes)
    and Tree::validate(churn)) as String) -> ./reused.txt;
"""
    )
    entries: str = ",".join(f"{key}:{model[key]}" for key in sorted(model))
    selected_entries: str = ",".join(
        f"{key}:{model[key]}" for key in sorted(model) if 23 <= key < 57
    )
    outputs: dict[str, bytes] = {
        "failures.txt": b"0",
        "height.txt": b"true",
        "entries.txt": entries.encode(),
        "range.txt": selected_entries.encode(),
        "cleared.txt": b"true",
        "snapshot.txt": str(len(model)).encode(),
        "fresh.txt": b"42",
        "equality-policy.txt": b"true",
        "record-policy.txt": b"true",
        "ieee-policy.txt": b"true",
        "reused.txt": b"true",
    }
    return program, outputs


def execute(
    binary: Path,
    directory: Path,
    program: str,
    outputs: dict[str, bytes],
    level: int,
    stage: str,
) -> None:
    directory.mkdir(parents=True)
    source: Path = directory / "ordered tree.msync"
    source.write_text(program, encoding="utf-8")
    if stage == "source":
        accept(invoke(binary, directory, "run", str(source), f"-O{level}"), stage)
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
            "compile",
        )
        if stage == "executable":
            executable: Path = source.with_suffix(".mexe")
            accept(
                invoke(
                    binary,
                    directory,
                    "bind-ir",
                    str(artifact),
                    "-o",
                    str(executable),
                    "--source-file",
                    str(source),
                ),
                "bind",
            )
            artifact = executable
        source.unlink()
        launch: Path = (
            directory / "different launch" if stage == "executable" else directory
        )
        launch.mkdir(exist_ok=True)
        accept(invoke(binary, launch, "run-ir", str(artifact)), stage)
    for name, expected in outputs.items():
        actual: bytes = (directory / name).read_bytes()
        assert actual == expected, (directory, name, actual, expected)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--levels", type=int, nargs="+", default=[2])
    parser.add_argument(
        "--stages",
        nargs="+",
        choices=("source", "relocatable", "executable"),
        default=["source", "relocatable", "executable"],
    )
    parser.add_argument(
        "--families",
        nargs="+",
        choices=("BTree", "BPlusTree"),
        default=["BTree", "BPlusTree"],
    )
    parser.add_argument("--definitions", type=Path)
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    definitions: str = (
        arguments.definitions.read_text(encoding="utf-8")
        if arguments.definitions
        else ""
    )
    scratch: Path = ROOT / ".temp/btree-bplus-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    runs: int = 0
    with tempfile.TemporaryDirectory(prefix="run-", dir=scratch) as name:
        for family in arguments.families:
            program, outputs = tree_program(family, definitions)
            for level in arguments.levels:
                for stage in arguments.stages:
                    execute(
                        binary,
                        Path(name) / family / f"O{level}" / stage,
                        program,
                        outputs,
                        level,
                        stage,
                    )
                    runs += 1
    report: dict[str, object] = {
        "binary": str(binary),
        "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "managed_assemblies": {
            name: hashlib.sha256((binary.parent / name).read_bytes()).hexdigest()
            for name in (
                "msync.dll",
                "Msync.Compiler.dll",
                "Msync.Runtime.dll",
                "Msync.Core.dll",
            )
            if (binary.parent / name).exists()
        },
        "runs": runs,
        "mutations_per_run": len(trace()[0]),
        "checks": "map model, occupancy, leaf depth, global ordering, leaf chain, page reuse, snapshots",
    }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
