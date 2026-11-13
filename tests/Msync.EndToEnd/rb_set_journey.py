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


def payload_identity(binary: Path) -> dict[str, str]:
    files: list[Path] = [binary]
    files.extend(
        path
        for path in binary.parent.iterdir()
        if path != binary
        and path.is_file()
        and (
            path.suffix in (".dll", ".so", ".dylib")
            or ".so." in path.name
            or path.name.endswith((".deps.json", ".runtimeconfig.json"))
        )
    )
    runtimes: Path = binary.parent / "runtimes"
    if runtimes.is_dir():
        files.extend(path for path in runtimes.rglob("*") if path.is_file())
    return {
        path.relative_to(binary.parent).as_posix(): hashlib.sha256(
            path.read_bytes()
        ).hexdigest()
        for path in sorted(files)
    }


@dataclass(frozen=True)
class Node:
    key: int
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
fn numbers(values: Range<Number>) -> String {
    $std::string::join($std::ranges::transform(values,
        fn(value: Number) { value as String }), ",")
};
fn node_id(tree, node?) -> Number {
    if node is null { return 0; };
    let Node = decltype(tree::nil);
    let current = node as Node;
    if current::nil { return 0; };
    let key? = current::key;
    if key is null { 0 } else { key + 1 }
};
fn encode_rows(tree) -> String {
    let text = "";
    for key in set_type::snapshot(tree) {
        let Node = decltype(tree::nil);
        let node = tree::root as Node;
        loop {
            if node::nil { break; };
            let item? = node::key;
            if item is null { break; };
            if item == key {
                let row = $std::string::join([
                    node_id(tree, node) as String, item as String,
                    node_id(tree, node::left) as String, node_id(tree, node::right) as String,
                    node_id(tree, node::parent) as String, node::red as String,
                ], ",");
                text = $std::string::concat(text, $std::string::concat(row, "/"));
                break;
            };
            let next? = if key < item { node::left } else { node::right };
            if next is null { break; };
            node = next as Node;
        };
    };
    text
};
fn capture(tree, changed: Bool) -> String {
    let count = set_type::size(tree);
    $std::string::join([
        changed as String, node_id(tree, tree::root) as String, count as String,
        numbers(set_type::snapshot(tree)), tree::nil::red as String,
        encode_rows(tree)
    ], ";")
};
let tree = set_type::new(Number);
let trace = "";
for operation in [
"""
        + literal
        + r"""
] {
    let changed = if operation::insert {
        set_type::insert(tree, operation::value)
    } else { set_type::remove(tree, operation::value) };
    trace = $std::string::concat(trace,
        $std::string::concat(capture(tree, changed), $std::environment::newline()));
};
trace -> ./trace.txt;
let between = set_type::range(tree, 3, 9);
numbers(between) -> ./range.txt;
let lower? = set_type::lower_bound(tree, 8);
if lower is not null { (lower::value as String) -> ./lower.txt; };
let first? = set_type::pop_first(tree);
let last? = set_type::pop_last(tree);
if first is not null {
    (first::value as String) -> ./first.txt;
};
if last is not null {
    (last::value as String) -> ./last.txt;
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
let recycled = set_type::new(Number);
set_type::insert(recycled, 10);
let saved = set_type::snapshot(recycled);
set_type::remove(recycled, 10);
set_type::insert(recycled, 20);
if set_type::size(recycled) == 1 and set_type::contains(recycled, 20) {
    $std::string::join([numbers(saved), numbers(set_type::snapshot(recycled))], ";")
        -> ./recycled.txt;
};
set_type::clear(recycled);
if recycled::root::nil and not recycled::nil::red {
    numbers(set_type::snapshot(recycled)) -> ./cleared.txt;
};
let key = scope { rank: 1, label: "before" };
let another = scope { rank: 2, label: "other" };
let records = set_type::with_policy([key, another], scope {
    Self: decltype(key),
    sample: key,
    less: fn(left, right) -> Bool { left::rank < right::rank },
});
let captured = set_type::snapshot(records);
set_type::remove(records, key);
key::label = "after";
let captured_key = $std::ranges::front(captured);
if set_type::size(records) == 1 and set_type::contains(records, another) {
    captured_key::label -> ./scope-key.txt;
};
let paths = set_type::new([./bravo, ./alpha]);
$std::string::join($std::ranges::transform(set_type::snapshot(paths),
    fn(path: Path) { $std::path::filename(path) as String }), ",") -> ./path-keys.txt;
let negative_zero = $std::math::copysign(0, -1);
let special = set_type::new([negative_zero, 0, $std::math::nan, $std::math::nan]);
let special_keys = set_type::snapshot(special);
if set_type::size(special) == 2
    and $std::math::signbit($std::ranges::front(special_keys))
    and $std::math::is_nan($std::ranges::back(special_keys)) {
    "signed zero and NaN" -> ./ieee-keys.txt;
};
let nullable = set_type::new([null, 3]);
let present_null? = set_type::get(nullable, null);
if present_null is not null {
    if present_null::value is null and set_type::size(nullable) == 2 {
        "present null" -> ./nullable.txt;
    };
};
"""
    )


def integers(text: str) -> list[int]:
    return [] if not text else [int(value) for value in text.split(",")]


def validate_nodes(nodes: dict[int, Node], root: int, step: int) -> set[int]:
    visited: set[int] = set()

    def walk(index: int, lower: int | None, upper: int | None, parent: int) -> int:
        if index == 0:
            return 1
        assert index > 0 and index in nodes and index not in visited, (step, index)
        visited.add(index)
        node: Node = nodes[index]
        key: int = node.key
        assert lower is None or lower < key, (step, node)
        assert upper is None or key < upper, (step, node)
        assert node.parent == parent, (step, node, parent)
        if node.red:
            assert all(
                child == 0 or not nodes[child].red for child in (node.left, node.right)
            ), (step, node)
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
        nil_red: str
        node_text: str
        actual_changed, root_text, count_text, ordered, nil_red, node_text = fields
        root: int = int(root_text)
        nodes: dict[int, Node] = {}
        for encoded in filter(None, node_text.split("/")):
            node_fields: list[str] = encoded.split(",")
            assert len(node_fields) == 6 and node_fields[5] in ("true", "false"), (
                step,
                encoded,
            )
            index: int = int(node_fields[0])
            assert index not in nodes, (step, "repeated node handle", index)
            nodes[index] = Node(
                int(node_fields[1]),
                int(node_fields[2]),
                int(node_fields[3]),
                int(node_fields[4]),
                node_fields[5] == "true",
            )
        assert actual_changed == str(changed).lower(), (step, changed, actual_changed)
        assert int(count_text) == len(model), (step, count_text, model)
        assert integers(ordered) == sorted(model), (step, ordered, model)
        assert nil_red == "false", (step, "red NIL")
        assert root == 0 or not nodes[root].red, (step, "red root")
        visited: set[int] = validate_nodes(nodes, root, step)
        assert visited == nodes.keys(), (step, visited, nodes)
        assert sorted(node.key for node in nodes.values()) == sorted(model), (
            step,
            nodes,
            model,
        )
        checks += 1
    return checks


def execute(binary: Path, parent: Path, levels: list[int]) -> dict[str, object]:
    commands: list[Operation] = operations()
    source_text: str = program(commands)
    completed: int = 0
    invariant_observations: int = 0
    artifact_hashes: dict[str, str] = {}
    outputs: dict[str, bytes] = {
        "range.txt": b"3,4,5,6,7,8",
        "lower.txt": b"8",
        "first.txt": b"0",
        "last.txt": b"11",
        "added.txt": b"3",
        "get.txt": b"20",
        "retained.txt": b"0,1,2,3,4,5,6,7,8,9,10,11,20",
        "empty.txt": b"empty",
        "policy-before.txt": b"albedo,Klee,Sucrose",
        "policy-after.txt": b"Klee,Sucrose",
        "policy.txt": b"policy",
        "recycled.txt": b"10;20",
        "cleared.txt": b"",
        "scope-key.txt": b"after",
        "path-keys.txt": b"alpha,bravo",
        "ieee-keys.txt": b"signed zero and NaN",
        "nullable.txt": b"present null",
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
                    artifact.unlink()
                    artifact = bound
                artifact_hashes[f"O{level}/{stage}"] = hashlib.sha256(
                    artifact.read_bytes()
                ).hexdigest()
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
        "source_sha256": hashlib.sha256(source_text.encode("utf-8")).hexdigest(),
        "artifact_sha256": artifact_hashes,
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--level", type=int, choices=(0, 1, 2, 3), action="append")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    payload: dict[str, str] = payload_identity(binary)
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
                    "msync.hir.dll",
                    "msync.optimization.dll",
                    "msync.runtime.dll",
                }
            },
            "platform": platform.platform(),
            "levels": levels,
            "payload_sha256": payload,
            **execute(binary, Path(temporary), levels),
        }
        assert payload_identity(binary) == payload, (
            "CLI payload changed during validation"
        )
        report["payload_unchanged"] = True
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
