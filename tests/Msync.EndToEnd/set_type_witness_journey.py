#!/usr/bin/env python3
"""Verify typed empty containers and independently selected Range construction."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """
let Set = $std::containers::Set;
fn expect(condition: Bool, label: String) {
    if not condition { $std::io::println(label); };
};

let numbers = Set::new(Number);
expect(Set::size(numbers) == 0, "new Number is empty");
let empty_snapshot = Set::snapshot(numbers);
expect($std::ranges::size(empty_snapshot) == 0, "empty snapshot contains no keys");
expect(decltype($std::ranges::front(empty_snapshot)) is Number, "empty snapshot retains its carrier");
expect(Set::insert(numbers, 3), "first Number insert");
expect(not Set::insert(numbers, 3), "Number uniqueness");
expect(Set::contains(numbers, 3), "Number lookup");
let paths = Set::new(Path);
expect(Set::insert(paths, "./klee" as Path), "first Path insert");
expect(Set::contains(paths, "./klee" as Path), "Path lookup");

let prototype = Set::empty(0);
expect(Set::is_empty(prototype), "prototype construction is empty");
expect(Set::insert(prototype, 5), "prototype carrier is retained");
let typed_range = Set::new($std::ranges::empty_of(Number));
expect(Set::is_empty(typed_range), "actual empty Range is empty");
expect(Set::insert(typed_range, 6), "empty Range carrier is retained");

let default_visits = 0;
let pending = $std::ranges::transform([1, 2], fn(value: Number) {
    global default_visits;
    default_visits = default_visits + 1;
    value
});
let from_default_range = Set::new(pending);
expect(default_visits == 2, "default constructor consumes deferred input once");
expect(Set::size(from_default_range) == 2, "default deferred members are retained");

let visits = 0;
let calls = 0;
let compared_early = false;
let order = object {
    Self: String,
    sample: "",
    less: fn(left: String, right: String) {
        global visits;
        global calls;
        global compared_early;
        calls = calls + 1;
        if visits != 3 { compared_early = true; };
        $std::string::to_lowercase(left) < $std::string::to_lowercase(right)
    },
};
let empty_policy = Set::with_policy(String, order);
expect(Set::is_empty(empty_policy), "custom witness is empty");
expect(calls == 0, "empty witness must not call the comparator");

let input = $std::ranges::transform(["Klee", "klee", "Albedo"], fn(value: String) {
    global visits;
    visits = visits + 1;
    value
});
let from_range = Set::with_policy(input, order);
expect(visits == 3, "deferred input must be consumed once");
expect(not compared_early, "input must be snapshotted before comparisons");
expect(Set::size(from_range) == 2, "custom comparator preserves uniqueness");
expect(Set::contains(from_range, "ALBEDO"), "custom comparator lookup");
expect(Set::insert(empty_policy, "Klee"), "custom empty carrier is retained");

let before = calls;
order::less = fn(left: String, right: String) {
    global calls;
    calls = calls + 10;
    $std::string::to_lowercase(left) < $std::string::to_lowercase(right)
};
expect(Set::contains(empty_policy, "KLEE"), "replacement comparator lookup");
expect(calls - before == 20, "Set observes the current shared comparator");

$std::io::println("ok");
"""

HEAP_SOURCE: str = """
let Heap = $std::containers::Heap;
fn expect(condition: Bool, label: String) {
    if not condition { $std::io::println(label); };
};

let numbers = Heap::new(Number);
expect(Heap::size(numbers) == 0, "Number witness is empty");
Heap::push(numbers, 7);
expect(Heap::size(numbers) == 1, "Number witness retains its carrier");

let from_empty_range = Heap::new($std::ranges::empty_of(Number));
expect(Heap::size(from_empty_range) == 0, "real empty Range is empty");
Heap::push(from_empty_range, 2);
expect(Heap::size(from_empty_range) == 1, "empty Range retains its carrier");

let visits = 0;
let comparisons = 0;
let compared_early = false;
let order = object {
    Self: Number,
    sample: 0,
    less: fn(left: Number, right: Number) {
        global visits;
        global comparisons;
        global compared_early;
        comparisons = comparisons + 1;
        if visits != 3 { compared_early = true; };
        left > right
    },
};
let custom_empty = Heap::with_policy(Number, order);
expect(Heap::size(custom_empty) == 0 and comparisons == 0, "custom witness skips heapification");

let pending = $std::ranges::transform([3, 9, 4], fn(value: Number) {
    global visits;
    visits = visits + 1;
    value
});
let heap = Heap::with_policy(pending, order);
expect(visits == 3 and not compared_early, "real Range is consumed once before comparisons");
expect(Heap::size(heap) == 3 and comparisons > 0, "real Range is heapified");
let selected? = Heap::pop(heap);
if selected? { $std::io::println("heap is missing its first value"); }
else { expect(selected == 9, "custom ordering selects the largest value"); };
expect(Heap::size(heap) == 2, "pop removes exactly one value");
$std::io::println("ok");
"""


def invoke(cli: Path, cwd: Path, args: list[str | Path], expected: str = "") -> None:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [cli, "--no-config", "--no-telemetry", *args],
        cwd=cwd,
        stdin=subprocess.DEVNULL,
        capture_output=True,
        check=False,
        text=True,
        encoding="utf-8",
        timeout=45,
    )
    if result.returncode or result.stdout != expected or result.stderr:
        raise AssertionError((args, result.returncode, result.stdout, result.stderr))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--heap", action="store_true")
    args = parser.parse_args()
    cli: Path = args.binary.resolve(strict=True)
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    family: str = "Heap" if args.heap else "Set"
    with tempfile.TemporaryDirectory(
        prefix=f"{family.lower()}-type-witness-", dir=scratch
    ) as name:
        work: Path = Path(name)
        source: Path = work / "witness.msync"
        portable: Path = work / "witness.mso"
        executable: Path = work / "witness.mexe"
        source.write_text(HEAP_SOURCE if args.heap else SOURCE, encoding="utf-8")
        invoke(cli, work, ["run", source], "ok\n")
        invoke(cli, work, ["compile", "-O2", source, "-o", portable])
        source.unlink()
        invoke(cli, work, ["run", portable], "ok\n")
        invoke(cli, work, ["bind-ir", portable, "-o", executable])
        portable.unlink()
        launch: Path = work / "launch"
        launch.mkdir()
        invoke(cli, launch, ["run", executable], "ok\n")
    print(f"{family} Type witness: source, isolated object, isolated executable")


if __name__ == "__main__":
    main()
