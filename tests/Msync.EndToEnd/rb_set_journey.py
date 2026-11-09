#!/usr/bin/env python3
"""Exercise source red-black Set balancing through the real CLI and staged IR."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import random
import tempfile
from dataclasses import dataclass
from pathlib import Path

from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]
Operation = tuple[bool, int]


@dataclass(frozen=True)
class Node:
    key: int | None
    left: int
    right: int
    parent: int
    red: bool


def operations() -> list[Operation]:
    result: list[Operation] = [(True, value) for value in range(24)]
    generator: random.Random = random.Random(1741)
    result.extend(
        (generator.randrange(3) != 0, generator.randrange(48)) for _ in range(64)
    )
    result.extend((False, value) for value in range(47, -1, -1))
    result.extend((True, value) for value in range(12))
    return result


def program(commands: list[Operation]) -> str:
    literal: str = ",".join(
        f"scope {{ insert: {str(insert).lower()}, value: {value} }}"
        for insert, value in commands
    )
    return (
        r"""
let set_type = $std::containers::Set;
let buffer = $std::storage::Buffer;
fn numbers(values: Range<Number>) -> String {
    $std::string::join($std::ranges::transform(values,
        fn(value: Number) { value as String }), ",")
};
fn capture(tree, changed: Bool) -> String {
    let node_rows = $std::ranges::transform(buffer::snapshot(tree::nodes),
        fn(node) {
            let key = if $std::ranges::empty(node::payload) {
                "_"
            } else { $std::ranges::front(node::payload) as String };
            $std::string::join([
                key, node::left as String, node::right as String,
                node::parent as String, node::red as String
            ], ",")
        });
    $std::string::join([
        changed as String, tree::root as String, tree::count as String,
        numbers(set_type::snapshot(tree)),
        numbers(buffer::snapshot(tree::free)),
        $std::string::join(node_rows, "/")
    ], ";")
};
let tree = set_type::empty(0);
let trace = buffer::empty("");
for operation in [
"""
        + literal
        + r"""
] {
    let changed = if operation::insert {
        set_type::insert(tree, operation::value)
    } else { set_type::remove(tree, operation::value) };
    buffer::push(trace, capture(tree, changed));
};
$std::string::join(buffer::snapshot(trace), $std::environment::newline())
    -> ./trace.txt;
let between = set_type::range(tree, 3, 9);
numbers(between) -> ./range.txt;
let lower? = set_type::lower_bound(tree, 8);
if lower is not null { (lower::value as String) -> ./lower.txt; };
let first? = set_type::pop_first(tree);
let last? = set_type::pop_last(tree);
if first is not null and last is not null {
    $std::string::join([first::value as String, last::value as String], ",")
        -> ./endpoints.txt;
};
let added = set_type::extend(tree, [0, 11, 20, 20]);
(added as String) -> ./added.txt;
let selected? = set_type::get(tree, 20);
if selected is not null { (selected::value as String) -> ./get.txt; };
let retained = set_type::snapshot(tree);
set_type::clear(tree);
numbers(retained) -> ./retained.txt;
if set_type::is_empty(tree) and set_type::pop_first(tree) is null
    and set_type::pop_last(tree) is null {
    "empty" -> ./empty.txt;
};
let names = set_type::with_policy(["Klee", "albedo", "KLEE"], scope {
    Self: String,
    sample: "",
    less: fn(left: String, right: String) -> Bool {
        $std::string::to_lowercase(left) < $std::string::to_lowercase(right)
    },
});
let alias = names;
set_type::insert(alias, "Sucrose");
let prior = set_type::snapshot(names);
set_type::remove(names, "ALBEDO");
$std::string::join(prior, ",") -> ./policy-before.txt;
$std::string::join(set_type::snapshot(alias), ",") -> ./policy-after.txt;
if set_type::size(names) == 2 and set_type::contains(names, "klee") {
    "policy" -> ./policy.txt;
};
"""
    )


def integers(text: str) -> list[int]:
    return [] if not text else [int(value) for value in text.split(",")]


def validate_nodes(nodes: list[Node], root: int, step: int) -> set[int]:
    visited: set[int] = set()

    def walk(index: int, lower: int | None, upper: int | None, parent: int) -> int:
        if index == 0:
            return 1
        assert 0 < index < len(nodes) and index not in visited, (step, index)
        visited.add(index)
        node: Node = nodes[index]
        key: int | None = node.key
        assert key is not None and (lower is None or lower < key), (step, node)
        assert upper is None or key < upper, (step, node)
        assert node.parent == parent, (step, node, parent)
        if node.red:
            assert not nodes[node.left].red and not nodes[node.right].red, (step, node)
        left_height: int = walk(node.left, lower, key, index)
        right_height: int = walk(node.right, key, upper, index)
        assert left_height == right_height, (step, node, left_height, right_height)
        return left_height + (0 if node.red else 1)

    walk(root, None, None, 0)
    return visited


def check_trace(text: str, commands: list[Operation]) -> int:
    rows: list[str] = text.splitlines()
    assert len(rows) == len(commands), (len(rows), len(commands))
    model: set[int] = set()
    checks: int = 0
    for step, ((insert, value), row) in enumerate(zip(commands, rows, strict=True)):
        existed: bool = value in model
        changed: bool = not existed if insert else existed
        if insert:
            model.add(value)
        else:
            model.discard(value)
        fields: list[str] = row.split(";")
        assert len(fields) == 6, (step, row)
        actual_changed: str
        root_text: str
        count_text: str
        ordered: str
        free_text: str
        node_text: str
        actual_changed, root_text, count_text, ordered, free_text, node_text = fields
        root: int = int(root_text)
        free_indexes: list[int] = integers(free_text)
        free: set[int] = set(free_indexes)
        assert len(free) == len(free_indexes), (step, free_indexes)
        nodes: list[Node] = []
        for encoded in node_text.split("/"):
            node_fields: list[str] = encoded.split(",")
            assert len(node_fields) == 5, (step, encoded)
            nodes.append(
                Node(
                    None if node_fields[0] == "_" else int(node_fields[0]),
                    int(node_fields[1]),
                    int(node_fields[2]),
                    int(node_fields[3]),
                    node_fields[4] == "true",
                )
            )
        assert actual_changed == str(changed).lower(), (step, changed, actual_changed)
        assert int(count_text) == len(model), (step, count_text, model)
        assert integers(ordered) == sorted(model), (step, ordered, model)
        assert not nodes[0].red, (step, "red NIL")
        assert root == 0 or not nodes[root].red, (step, "red root")
        visited: set[int] = validate_nodes(nodes, root, step)
        assert len(visited) == len(model), (step, visited, model)
        assert all(nodes[index].key is None for index in free), (step, free)
        assert not visited.intersection(free), (step, visited, free)
        assert len(visited) + len(free) + 1 == len(nodes), (
            step,
            len(nodes),
            visited,
            free,
        )
        checks += 1
    return checks


def execute(binary: Path, parent: Path, levels: list[int]) -> dict[str, int]:
    commands: list[Operation] = operations()
    source_text: str = program(commands)
    completed: int = 0
    invariant_observations: int = 0
    outputs: dict[str, bytes] = {
        "range.txt": b"3,4,5,6,7,8",
        "lower.txt": b"8",
        "endpoints.txt": b"0,11",
        "added.txt": b"3",
        "get.txt": b"20",
        "retained.txt": b"0,1,2,3,4,5,6,7,8,9,10,11,20",
        "empty.txt": b"empty",
        "policy-before.txt": b"albedo,Klee,Sucrose",
        "policy-after.txt": b"Klee,Sucrose",
        "policy.txt": b"policy",
    }
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            source: Path = directory / "red black Set 体验.msync"
            source.write_text(source_text, encoding="utf-8")
            if stage == "source":
                accept(
                    invoke(binary, directory, "run", str(source), f"-O{level}"), stage
                )
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
                launch: Path = (
                    directory / "different launch"
                    if stage == "executable"
                    else directory
                )
                launch.mkdir(exist_ok=True)
                accept(invoke(binary, launch, "run-ir", str(artifact)), stage)
            invariant_observations += check_trace(
                (directory / "trace.txt").read_text(encoding="utf-8"), commands
            )
            for name, expected in outputs.items():
                actual: bytes = (directory / name).read_bytes()
                assert actual == expected, (level, stage, name, actual, expected)
            completed += 1
    return {
        "staged_executions": completed,
        "invariant_observations": invariant_observations,
        "outputs_per_execution": len(outputs) + 1,
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--level", type=int, choices=(0, 1, 2, 3), action="append")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    levels: list[int] = arguments.level or [2]
    scratch: Path = ROOT / ".temp/rb-set-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="execution-", dir=scratch) as temporary:
        report: dict[str, object] = {
            "family": "$std::containers::Set",
            "binary": str(binary),
            "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "managed_component_sha256": {
                path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(binary.parent.glob("*.dll"))
                if path.name.lower()
                in {
                    "msync.dll",
                    "msync.core.dll",
                    "msync.compiler.dll",
                    "msync.runtime.dll",
                }
            },
            "platform": platform.platform(),
            "levels": levels,
            **execute(binary, Path(temporary), levels),
        }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
