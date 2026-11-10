#!/usr/bin/env python3
"""Check affine scan schedules against real runtime-derived inputs."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import tempfile
from pathlib import Path

from object_locality_journey import ROOT, execute

PREFIX: str = "let seed = $std::io::read_text(./seed.txt) as Number;\n"
PROGRAMS: dict[str, tuple[str, dict[str, bytes]]] = {
    "scope-fields": (
        PREFIX
        + """
let rows = $std::ranges::materialize([
    scope { x: seed, y: seed + 1 },
    scope { x: seed + 2, y: seed + 3 },
    scope { x: seed + 4, y: seed + 5 },
    scope { x: seed + 6, y: seed + 7 },
]);
let first = 0;
let second = 0;
for row in rows { first = first + row::x; };
for row in rows { second = second + row::y; };
(first as String) -> ./first.txt;
(second as String) -> ./second.txt;
""",
        {"first.txt": b"40", "second.txt": b"44"},
    ),
    "scope-shared": (
        PREFIX
        + """
let shared = scope { x: seed, y: seed + 1 };
let rows = $std::ranges::materialize([shared, shared]);
let first = 0;
let second = 0;
for row in rows { first = first + row::x; };
for row in rows { second = second + row::y; };
(first as String) -> ./first.txt;
(second as String) -> ./second.txt;
""",
        {"first.txt": b"14", "second.txt": b"16"},
    ),
    "independent": (
        PREFIX
        + """
let values = $std::ranges::materialize($std::ranges::iota(0, 64));
let total = 0;
let squares = 0;
for value in values { total = total + value + seed; };
for value in values { squares = squares + (value + seed) * (value + seed); };
(total as String) -> ./total.txt;
(squares as String) -> ./squares.txt;
""",
        {"total.txt": b"2464", "squares.txt": b"116704"},
    ),
    "captured-dependence": (
        PREFIX
        + """
let unit = seed / seed;
let values = $std::ranges::materialize([unit, unit + 1, unit + 2, unit + 3]);
let total = 0;
fn run(items: Range<Number>) {
    global total;
    for value in items { total = total + value; };
    for value in items { total = total * 2 + value; };
};
run(values);
(total as String) -> ./total.txt;
""",
        {"total.txt": b"186"},
    ),
    "floating-order": (
        PREFIX
        + """
let unit = seed / seed;
let values = $std::ranges::materialize([
    unit * 10000000000000000, unit, -unit * 10000000000000000
]);
let first = 0;
let second = 0;
for value in values { first = first + value; };
for value in values { second = second + (value + unit); };
(first as String) -> ./first.txt;
(second as String) -> ./second.txt;
""",
        {"first.txt": b"0", "second.txt": b"2"},
    ),
    "opaque-observer": (
        PREFIX
        + """
let values = $std::ranges::materialize($std::ranges::iota(0, 4));
let first = 0;
let second = 0;
for value in values { first = first + value + seed; };
(first as String) -> ./between.txt;
for value in values { second = second + value + seed; };
(second as String) -> ./second.txt;
""",
        {"between.txt": b"34", "second.txt": b"34"},
    ),
    "lazy-effectful": (
        PREFIX
        + """
let calls = 0;
let values = $std::ranges::transform([seed, seed + 1], fn(value: Number) {
    calls = calls + 1;
    value
});
let total = 0;
let squares = 0;
for value in values { total = total + value; };
for value in values { squares = squares + value * value; };
(total as String) -> ./total.txt;
(squares as String) -> ./squares.txt;
(calls as String) -> ./calls.txt;
""",
        {"total.txt": b"15", "squares.txt": b"113", "calls.txt": b"4"},
    ),
    "indirect-walk": (
        PREFIX
        + """
let nodes = $std::storage::Buffer::new([
    scope { value: seed, next: 2 },
    scope { value: seed + 1, next: 0 },
    scope { value: seed + 2, next: -1 },
]);
let index = 1;
let total = 0;
loop {
    if index < 0 { break; };
    let node = $std::storage::Buffer::get(nodes, index);
    total = total + node::value;
    index = node::next;
};
(total as String) -> ./total.txt;
""",
        {"total.txt": b"24"},
    ),
}


def run_case(
    binary: Path, work: Path, name: str, route: str, level: int, require_fusion: bool
) -> dict[str, object]:
    work.mkdir(parents=True)
    (work / "seed.txt").write_text("7", encoding="utf-8")
    program, expected = PROGRAMS[name]
    source: Path = work / "scans.msync"
    source.write_text(program, encoding="utf-8", newline="\n")
    portable: Path = work / "scans.mso"
    execute(binary, work, ["compile", str(source), f"-O{level}", "-o", str(portable)])
    disassembly: dict[str, object] = json.loads(
        execute(binary, work, ["disassemble", str(portable), "--json"]).stdout
    )
    text = str(disassembly["text"])
    iterators: int = len(re.findall(r"\bIteratorNext\b", text))
    if require_fusion and name in {"independent", "scope-fields"}:
        assert iterators == 1, (
            "Expected a genuinely fused portable scan",
            iterators,
            text,
        )
    if route == "source":
        execute(binary, work, ["run", str(source), f"-O{level}"])
    else:
        artifact: Path = portable
        if route == "mexe":
            artifact = work / "scans.mexe"
            execute(binary, work, ["bind-ir", str(portable), "-o", str(artifact)])
        assert not any((work / name).exists() for name in expected), (
            "Preparation wrote an output."
        )
        source.unlink()
        execute(binary, work, ["run-ir", str(artifact)])
    for filename, value in expected.items():
        actual: bytes = (work / filename).read_bytes()
        assert actual == value, (name, route, filename, actual, value)
    return {
        "case": name,
        "route": route,
        "optimization": level,
        "iterators": iterators,
        "source_sha256": hashlib.sha256(program.encode()).hexdigest(),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--case", action="append", choices=tuple(PROGRAMS), default=[])
    parser.add_argument(
        "--route", action="append", choices=["source", "mso", "mexe"], default=[]
    )
    parser.add_argument("--level", type=int, default=2)
    parser.add_argument("--require-fusion", action="store_true")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    (ROOT / ".temp").mkdir(exist_ok=True)
    work: Path = Path(tempfile.mkdtemp(prefix="affine-scans-", dir=ROOT / ".temp"))
    observations: list[dict[str, object]] = [
        run_case(
            binary,
            work / f"{name}-{route}",
            name,
            route,
            arguments.level,
            arguments.require_fusion,
        )
        for name in (arguments.case or list(PROGRAMS))
        for route in (arguments.route or ["source", "mso", "mexe"])
    ]
    report: dict[str, object] = {
        "status": "passed",
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "assemblies": {
            path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(binary.parent.glob("Msync.*.dll"))
        },
        "work": str(work),
        "observations": observations,
    }
    output: Path = arguments.output or work / "report.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
