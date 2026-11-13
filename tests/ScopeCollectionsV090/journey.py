#!/usr/bin/env python3
"""Exercise ordinary Scope collections through real source and artifact routes."""

from __future__ import annotations

import argparse
import hashlib
import heapq
import json
import os
import random
import subprocess
import time
from dataclasses import dataclass
from itertools import product
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
FAMILIES: tuple[str, ...] = ("Set", "Heap", "BTree", "BPlusTree")
ROUTES: tuple[str, ...] = ("source", "hir", "object", "bound")


@dataclass(frozen=True)
class Case:
    source: str
    stdin: str
    expected: str


def boolean(value: bool) -> str:
    return "true" if value else "false"


def payload_identity(binary: Path) -> dict[str, str]:
    root: Path = binary.parent
    files: list[Path] = [binary]
    files.extend(
        path
        for path in root.iterdir()
        if path != binary
        and path.is_file()
        and (
            path.suffix in (".dll", ".so", ".dylib")
            or path.name.endswith((".deps.json", ".runtimeconfig.json"))
        )
    )
    runtimes: Path = root / "runtimes"
    if runtimes.is_dir():
        files.extend(path for path in runtimes.rglob("*") if path.is_file())
    return {
        path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(files)
    }


def preamble(family: str, source_module: bool) -> str:
    declarations: str = (
        (ROOT / "stdlib/containers.msync").read_text(encoding="utf-8")
        if source_module
        else ""
    )
    selected: str = family if source_module else f"$std::containers::{family}"
    return (
        declarations
        + f"\nlet Family = {selected};\n"
        + """
fn show(value?) { if value is null { "null" } else { value as String } };
fn line(value?) { $std::io::println(show(value)); };
"""
    )


def set_case(seed: int, source_module: bool) -> Case:
    randomizer: random.Random = random.Random(seed)
    model: set[int] = set()
    operations: list[str] = []
    expected: list[str] = []
    keys: list[int] = list(range(96))
    randomizer.shuffle(keys)
    selected: list[tuple[bool, int]] = [(True, key) for key in keys]
    selected.extend(
        (randomizer.randrange(3) != 0, randomizer.randrange(128)) for _ in range(240)
    )
    selected.extend((False, key) for key in keys)
    for insert, key in selected:
        changed: bool = key not in model if insert else key in model
        if insert:
            model.add(key)
        else:
            model.discard(key)
        operations.append(f"{'i' if insert else 'r'} {key}")
        expected.extend((boolean(changed), str(len(model)), boolean(key in model)))
    snapshot: list[int] = sorted(model)
    expected.extend(("snapshot", *map(str, snapshot), "range"))
    expected.extend(str(key) for key in snapshot if 23 <= key < 71)
    lower: list[int] = [key for key in snapshot if key >= 47]
    expected.extend(("bound", str(lower[0]) if lower else "null", "saved"))
    expected.extend(map(str, snapshot))
    expected.extend(("empty", "true", "some-null", "true", "true", "false"))
    source: str = (
        preamble("Set", source_module)
        + """
let tree = Family::new(Number);
let alias = tree;
for record in $std::io::stdin_lines() {
    let fields = $std::string::split(record, " ");
    let key = $std::ranges::at(fields, 1) as Number;
    let changed = if $std::ranges::front(fields) == "i" {
        Family::insert(alias, key)
    } else { Family::remove(alias, key) };
    line(changed); line(Family::size(tree)); line(Family::contains(tree, key));
};
let saved = Family::snapshot(tree);
line("snapshot"); for value? in saved { line(value); };
line("range"); for value? in Family::range(tree, 23, 71) { line(value); };
line("bound");
let bound? = Family::lower_bound(tree, 47);
if bound is null { line(null); } else { line(bound::value); };
Family::clear(alias);
line("saved"); for value? in saved { line(value); };
line("empty"); line(Family::is_empty(tree));
let nullable = Family::new([null, 3, 1]);
let present? = Family::get(nullable, null);
line("some-null");
line(present is not null);
if present is not null { line(present::value is null); };
Family::remove(nullable, null);
line(Family::contains(nullable, null));
"""
    )
    return Case(source, "\n".join(operations) + "\n", "\n".join(expected) + "\n")


def tree_case(family: str, seed: int, source_module: bool) -> Case:
    randomizer: random.Random = random.Random(seed)
    model: dict[int, int] = {}
    operations: list[str] = []
    expected: list[str] = []
    keys: list[int] = list(range(260))
    randomizer.shuffle(keys)
    selected: list[tuple[bool, int, int]] = [(True, key, key * 3 + 1) for key in keys]
    selected.extend((False, key, 0) for key in keys[::2])
    selected.extend(
        (
            randomizer.randrange(3) != 0,
            randomizer.randrange(300),
            randomizer.randrange(10000),
        )
        for _ in range(240)
    )
    for insert, key, value in selected:
        changed: bool = key not in model if insert else key in model
        if insert:
            model[key] = value
        else:
            model.pop(key, None)
        operations.append(f"{'i' if insert else 'r'} {key} {value}")
        expected.extend(
            (
                boolean(changed),
                str(len(model)),
                boolean(key in model),
                str(model[key]) if key in model else "null",
                "true",
            )
        )
    entries: list[str] = [f"{key}:{model[key]}" for key in sorted(model)]
    expected.extend(("entries", *entries, "range"))
    expected.extend(f"{key}:{model[key]}" for key in sorted(model) if 23 <= key < 71)
    expected.extend(("saved", *entries, "empty", "true", "1", "true"))
    source: str = (
        preamble(family, source_module)
        + """
let tree = Family::of_types(Number, Number);
let alias = tree;
for record in $std::io::stdin_lines() {
    let fields = $std::string::split(record, " ");
    let key = $std::ranges::at(fields, 1) as Number;
    let value = $std::ranges::at(fields, 2) as Number;
    let changed = if $std::ranges::front(fields) == "i" {
        Family::insert(alias, key, value)
    } else { Family::remove(alias, key) };
    line(changed); line(Family::size(tree)); line(Family::contains(tree, key));
    let result? = Family::get(tree, key);
    if result is null { line(null); } else { line(result::value); };
    line(Family::validate(tree));
};
fn entry(value) { $std::string::concat($std::string::concat(show(value::key), ":"), show(value::value)) };
let saved = Family::entries(tree);
line("entries"); for value in saved { line(entry(value)); };
line("range"); for value in Family::range(tree, 23, 71) { line(entry(value)); };
Family::clear(alias);
line("saved"); for value in saved { line(entry(value)); };
line("empty"); line(Family::is_empty(tree)); line(Family::page_count(tree)); line(Family::validate(tree));
"""
    )
    return Case(source, "\n".join(operations) + "\n", "\n".join(expected) + "\n")


def heap_case(seed: int, source_module: bool) -> Case:
    randomizer: random.Random = random.Random(seed)
    initial: list[int] = [randomizer.randrange(1000) for _ in range(96)]
    model: list[int] = initial.copy()
    heapq.heapify(model)
    operations: list[str] = []
    expected: list[str] = []
    for _ in range(320):
        operation: str = randomizer.choice(("i", "p", "r", "q"))
        value: int = randomizer.randrange(1000)
        operations.append(f"{operation} {value}")
        result: int | None
        if operation == "i":
            heapq.heappush(model, value)
            result = None
        elif operation == "p":
            result = heapq.heappop(model) if model else None
        elif operation == "r":
            result = heapq.heapreplace(model, value) if model else None
            if result is None:
                heapq.heappush(model, value)
        else:
            result = heapq.heappushpop(model, value)
        expected.extend(
            (
                str(result) if result is not None else "null",
                str(len(model)),
                str(model[0]) if model else "null",
                "true",
            )
        )
    retained: list[int] = sorted(model)
    expected.extend(
        (
            "sorted",
            *map(str, retained),
            "drain",
            *map(str, retained),
            "saved",
            *map(str, retained),
            "empty",
            "true",
        )
    )
    source: str = (
        preamble("Heap", source_module)
        + "let queue = Family::new(["
        + ",".join(map(str, initial))
        + "]);\n"
        + """
let alias = queue;
for record in $std::io::stdin_lines() {
    let fields = $std::string::split(record, " ");
    let operation = $std::ranges::front(fields);
    let value = $std::ranges::at(fields, 1) as Number;
    let result? = if operation == "i" { Family::push(alias, value); null } else {
        if operation == "p" { Family::pop(alias) } else {
            if operation == "r" { Family::replace_top(alias, value) }
            else { Family::push_pop(alias, value) }
        }
    };
    line(result); line(Family::size(queue)); line(Family::peek(queue)); line(Family::validate(queue));
};
let saved = Family::snapshot(queue);
line("sorted"); for value? in Family::sorted(queue) { line(value); };
line("drain"); for value? in Family::drain(alias) { line(value); };
line("saved"); for value? in $std::ranges::sort(saved) { line(value); };
line("empty"); line(Family::is_empty(queue));
"""
    )
    return Case(source, "\n".join(operations) + "\n", "\n".join(expected) + "\n")


def with_policy_and_identity(case: Case, family: str) -> Case:
    source: str
    expected: str
    if family in ("Set", "Heap"):
        source = """
let record_key = scope { rank: 1, label: "before" };
let record_other = scope { rank: 2, label: "other" };
let record_order = scope {
    Self: decltype(record_key), sample: record_key,
    less: fn(left, right) -> Bool { left::rank < right::rank },
};
let records = Family::with_policy([record_other, record_key], record_order);
let record_alias = records;
let record_saved = Family::snapshot(records);
record_key::label = "after";
Family::clear(record_alias);
line("identity");
for record in $std::ranges::sort_with(record_saved, record_order::less) { line(record::label); };
line(Family::is_empty(records));
"""
        expected = "identity\nafter\nother\ntrue\n"
    else:
        source = """
let record_key = scope { rank: 1, label: "before" };
let record_other = scope { rank: 2, label: "other" };
let record_order = scope {
    Self: String, sample: "",
    less: fn(left: String, right: String) -> Bool {
        $std::string::to_lowercase(left) < $std::string::to_lowercase(right)
    },
};
let records = Family::with_policy("", record_key, record_order);
Family::insert(records, "Klee", record_key);
Family::insert(records, "ALBEDO", record_other);
let record_saved = Family::entries(records);
record_key::label = "after";
let record_selected? = Family::get(records, "klee");
line("identity");
if record_selected is not null {
    let selected_value? = record_selected::value;
    if selected_value is null { line(null); } else { line(selected_value::label); };
};
Family::clear(records);
for record in record_saved {
    let saved_value? = record::value;
    if saved_value is null { line(null); } else { line(saved_value::label); };
};
line(Family::is_empty(records));
"""
        expected = "identity\nafter\nother\nafter\ntrue\n"
    if family == "Set":
        source += """
let folded_order = scope {
    Self: String, sample: "",
    less: fn(left: String, right: String) -> Bool {
        $std::string::to_lowercase(left) < $std::string::to_lowercase(right)
    },
};
let folded = Family::with_policy(["Klee", "albedo", "KLEE"], folded_order);
Family::insert(folded, "Sucrose");
let folded_saved = Family::snapshot(folded);
Family::remove(folded, "ALBEDO");
line("folded");
for value in folded_saved { line(value); };
for value in Family::snapshot(folded) { line(value); };
let calls = 0;
folded_order::less = fn(left: String, right: String) -> Bool {
    calls = calls + 1;
    $std::string::to_lowercase(left) < $std::string::to_lowercase(right)
};
line(Family::contains(folded, "klee"));
line(calls > 0);
"""
        expected += "folded\nalbedo\nKlee\nSucrose\nKlee\nSucrose\ntrue\ntrue\n"
    if family == "Heap":
        rows: list[int] = list(range(64))
        calls: int = 0
        for iteration in range(64):
            last: int = rows.pop()
            if not rows:
                continue
            index: int = 0
            while 2 * index + 1 < len(rows):
                child: int = 2 * index + 1
                if child + 1 < len(rows):
                    calls += int(iteration >= 8)
                    if rows[child + 1] < rows[child]:
                        child += 1
                calls += int(iteration >= 8)
                if rows[child] >= last:
                    break
                rows[index] = rows[child]
                index = child
            rows[index] = last
        source += """
let live_order = scope {
    Self: Number, sample?: null,
    less: fn(left: Number, right: Number) -> Bool { left < right },
};
let live = Family::with_policy($std::ranges::iota(0, 64), live_order);
let live_saved = Family::snapshot(live);
line("live");
for _ in $std::ranges::iota(0, 8) { line(Family::pop(live)); };
let live_calls = 0;
live_order::less = fn(left: Number, right: Number) -> Bool {
    live_calls = live_calls + 1;
    left < right
};
for _ in $std::ranges::iota(8, 64) { line(Family::pop(live)); };
line(live_calls); line(Family::size(live)); line($std::ranges::size(live_saved));
"""
        expected += "live\n" + "\n".join(map(str, range(64))) + f"\n{calls}\n0\n64\n"
    return Case(case.source + source, case.stdin, case.expected + expected)


def invoke(binary: Path, directory: Path, arguments: list[str], stdin: str = "") -> str:
    process: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        input=stdin,
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
        timeout=300,
    )
    if process.returncode or process.stderr:
        raise RuntimeError(
            json.dumps(
                {
                    "command": process.args,
                    "cwd": str(directory),
                    "exit_code": process.returncode,
                    "stdout": process.stdout,
                    "stderr": process.stderr,
                }
            )
        )
    return process.stdout.replace("\r\n", "\n")


def execute(
    binary: Path, directory: Path, case: Case, route: str, optimization: int
) -> None:
    directory.mkdir(parents=True)
    source: Path = directory / "collections.msync"
    hir: Path = directory / "collections.mhir"
    portable: Path = directory / "collections.mso"
    executable: Path = directory / "collections.mexe"
    source.write_text(case.source, encoding="utf-8", newline="\n")
    (directory / "stdin.txt").write_text(case.stdin, encoding="utf-8")
    (directory / "expected.txt").write_text(case.expected, encoding="utf-8")
    selected: Path = source
    if route != "source":
        if route == "hir":
            invoke(
                binary,
                directory,
                ["compile", str(source), "--emit", "hir", "-o", str(hir)],
            )
            source.unlink()
            selected = hir
        else:
            invoke(
                binary,
                directory,
                ["compile", f"-O{optimization}", str(source), "-o", str(portable)],
            )
            source.unlink()
            selected = portable
        if route == "bound":
            invoke(binary, directory, ["bind-ir", str(portable), "-o", str(executable)])
            portable.unlink()
            selected = executable
    actual: str = invoke(
        binary, directory, ["run", f"-O{optimization}", str(selected)], case.stdin
    )
    (directory / "actual.txt").write_text(actual, encoding="utf-8")
    if actual != case.expected:
        actual_lines: list[str] = actual.splitlines()
        expected_lines: list[str] = case.expected.splitlines()
        mismatch: int = next(
            (i for i, (a, e) in enumerate(zip(actual_lines, expected_lines)) if a != e),
            min(len(actual_lines), len(expected_lines)),
        )
        raise AssertionError(
            f"Result differs at line {mismatch + 1}; inspect {directory}"
        )


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--family", choices=FAMILIES, action="append")
    parser.add_argument("--route", choices=ROUTES, action="append")
    parser.add_argument("--seed", type=int, action="append")
    parser.add_argument("--source-module", action="store_true")
    parser.add_argument("--optimization", choices=(0, 2), type=int, action="append")
    parser.add_argument("--work", type=Path)
    args: argparse.Namespace = parser.parse_args()
    directory: Path = (
        args.work or ROOT / ".temp/scope-collections-v090" / f"journey-{time.time_ns()}"
    ).resolve()
    if (
        not directory.is_relative_to(ROOT / ".temp")
        or directory.exists()
        and any(directory.iterdir())
    ):
        parser.error("--work must be a new directory under repository-root .temp")
    directory.mkdir(parents=True, exist_ok=True)
    binary: Path = args.binary.resolve(strict=True)
    report: dict[str, Any] = {
        "status": "running",
        "binary": str(binary),
        "source_module": args.source_module,
        "platform": os.name,
        "payload_sha256": payload_identity(binary),
        "checks": [],
    }
    try:
        for family in args.family or FAMILIES:
            for seed in args.seed or (17, 503):
                case: Case = (
                    set_case(seed, args.source_module)
                    if family == "Set"
                    else heap_case(seed, args.source_module)
                    if family == "Heap"
                    else tree_case(family, seed, args.source_module)
                )
                case = with_policy_and_identity(case, family)
                for optimization, route in product(
                    args.optimization or (0, 2), args.route or ROUTES
                ):
                    label: str = f"{family}/{seed}/O{optimization}/{route}"
                    report["active"] = label
                    (directory / "result.json").write_text(
                        json.dumps(report, indent=2) + "\n"
                    )
                    execute(
                        binary,
                        directory / family / str(seed) / f"O{optimization}" / route,
                        case,
                        route,
                        optimization,
                    )
                    report["checks"].append(label)
                    (directory / "result.json").write_text(
                        json.dumps(report, indent=2) + "\n"
                    )
                    print("PASS", label, flush=True)
        if payload_identity(binary) != report["payload_sha256"]:
            raise RuntimeError(
                "Executable payload changed during collection acceptance"
            )
        report.update(status="complete", active=None)
    except BaseException as error:
        report.update(status="failed", error=str(error))
        raise
    finally:
        (directory / "result.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
