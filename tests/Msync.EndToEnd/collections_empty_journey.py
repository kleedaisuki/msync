#!/usr/bin/env python3
"""Exercise prototype-based empty collection factories through real CLI stages."""
from __future__ import annotations

import argparse
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = '''
fn assert(condition: Bool) {
    if condition { "ok" } else { "FAILED" }
};
let list = $std::collections::EmptyList(0);
let set = $std::collections::EmptySet(0);
let map = $std::collections::EmptyMap("", 0);
let nullable = $std::collections::EmptyNullableMap("", 0);
let stack = $std::collections::EmptyStack(0);
let queue = $std::collections::EmptyQueue(0);
let equal = fn(a: String, b: String) {
    $std::string::to_lowercase(a) == $std::string::to_lowercase(b)
};
let custom_set = $std::collections::EmptySetWith("", equal);
let custom_map = $std::collections::EmptyMapWith("", 0, equal);
let before = $std::ranges::materialize([
    assert(list::is_empty()), assert(set::is_empty()), assert(map::is_empty()),
    assert(nullable::is_empty()), assert(stack::is_empty()),
    assert(queue::is_empty()), assert(custom_set::is_empty()),
    assert(custom_map::is_empty()), assert(stack::peek() is null),
    assert(stack::pop() is null), assert(queue::peek() is null),
    assert(queue::dequeue() is null), assert(list::get(10) is null)
]);
list::add(1);
set::add(1);
map::put("a", 1);
nullable::put("a", null);
stack::push(1);
queue::enqueue(1);
custom_set::add("ALPHA");
custom_set::add("alpha");
custom_map::put("ALPHA", 1);
custom_map::put("alpha", 2);
let after = $std::ranges::materialize([
    assert(list::size() == 1), assert(set::size() == 1),
    assert(map::get_or("a", 0) == 1),
    assert(nullable::contains_key("a")),
    assert(nullable::get_or("a", 7) is null),
    assert($std::ranges::matches(stack::pop(), 1)),
    assert($std::ranges::matches(queue::dequeue(), 1)),
    assert(custom_set::size() == 1), assert(custom_map::size() == 1),
    assert(custom_map::get_or("AlPhA", 0) == 2)
]);
$std::io::write_text(./result.txt,
    $std::string::join($std::ranges::concat(before, after), "|"));
'''
NEGATIVE: tuple[str, ...] = (
    'let list = $std::collections::EmptyList(0); list::add("bad");',
    'let map = $std::collections::EmptyMap("", 0); map::put("a", null);',
    'let map = $std::collections::EmptyMapWith(0, 0, '
    'fn(a: String, b: String) { a == b }); map::put(1, 2);',
)


def run(command: Sequence[str], cwd: Path) -> None:
    result = subprocess.run(command, cwd=cwd, capture_output=True, text=True)
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
    prefix = (ROOT / "stdlib/collections.msync").read_text() if args.inline_source else ""

    def code(source: str) -> str:
        return prefix + source.replace("$std::collections::", "") if prefix else source

    with tempfile.TemporaryDirectory(prefix="collections-empty-", dir=ROOT / ".temp") as name:
        work = Path(name)
        source = work / "empty.msync"
        source.write_text(code(SOURCE), encoding="utf-8")
        portable = work / "empty.mso"
        executable = work / "empty.mexe"
        run([*cli, "compile", str(source), "-O2", "-o", str(portable), "--no-config"], work)
        run([*cli, "bind-ir", str(portable), "-o", str(executable), "--no-config"], work)
        for artifact in (source, portable, executable):
            run([*cli, "run", str(artifact), "-O2", "--no-config"], work)
            actual = (work / "result.txt").read_text(encoding="utf-8")
            if actual.split("|") != ["ok"] * 23:
                raise AssertionError(f"{artifact.suffix}: {actual}")
        for invalid in NEGATIVE:
            source.write_text(code(invalid), encoding="utf-8")
            result = subprocess.run([*cli, "check", str(source), "--no-config"],
                                    cwd=work, capture_output=True, text=True)
            if not result.returncode or "error[E0203]" not in result.stderr:
                raise AssertionError(result.stderr)
    print("PASS: 8 empty factories, 23 observations through 3 O2 CLI stages; 3 fixed-schema/comparer rejections")


if __name__ == "__main__":
    main()
