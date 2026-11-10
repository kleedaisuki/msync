#!/usr/bin/env python3
"""Exercise spatial Scope storage through real source and source-free artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import tempfile
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
ROUTES: tuple[str, ...] = ("source", "mso", "mexe")
PROGRAM: str = r"""
let seed = $std::io::read_text(./seed.txt) as Number;
fn make(index: Number) {
    let x = seed + index;
    scope {
        x: x, y: x * 2, z: x * 3, weight: x + 4,
        tag: index as String, path: ./seed.txt,
    }
};
let rows = $std::storage::Buffer::new([]);
for index in $std::ranges::iota(0, 64) {
    $std::storage::Buffer::push(rows, make(index));
};
let view = $std::storage::Buffer::snapshot(rows);
let total = 0;
for row in view {
    let alias = row;
    alias::x = alias::x + 1;
    total = total + row::x;
};
(total as String) -> ./scan.txt;
let first = $std::ranges::first(view);
$std::storage::Buffer::clear(rows);
$std::storage::Buffer::push(rows, make(999));
first::x = first::x + 100;
if $std::ranges::size(view) == 64
    and $std::storage::Buffer::size(rows) == 1
    and $std::ranges::first(view)::x == 108 {
    "snapshot-keeps-instances" -> ./snapshot.txt;
};
fn captured(initial: Number) {
    scope {
        x: initial, y: initial + 1, z: initial + 2, weight: initial + 3,
        read: fn { x },
        write: fn(value: Number) { x = value; },
    }
};
let object = captured(seed);
let alias = object;
alias::write(19);
object::x = 23;
if alias::read() == 23 { "shared-location" -> ./capture.txt; };
fn observe(initial: Number) {
    let local = scope {
        x: initial, y: initial + 1, z: initial + 2,
        read: fn { x }, write: fn(value: Number) { x = value; },
    };
    local::write(19);
    local::x = 23;
    scope { direct: local::x, captured: local::read() }
};
let observed = observe(seed);
if observed::direct == 23 and observed::captured == 23 {
    "active-frame-location" -> ./capture-frame.txt;
};
let wide_rows = $std::storage::Buffer::new([
    scope {
        x: seed + 1, y: seed + 2,
        a: "a", b: "b", c: "c", d: "d", e: "e", f: "f",
        g: "g", h: "h", i: "i", j: "j", k: "k", l: "l",
        m: "m", n: "n", o: "o", p: "p", path: ./seed.txt,
    }
]);
let wide = $std::storage::Buffer::get(wide_rows, 0);
if wide::x == 8 and wide::y == 9 and wide::p == "p" {
    "numeric-prefix-reference-tail" -> ./mixed-depth.txt;
};
let optional = scope { value?: seed, y: seed + 1, z: seed + 2 };
optional::value = null;
if optional::value is null { "null" -> ./nullable.txt; };
optional::value = 13;
if optional::value is not null { (optional::value as String) -> ./restored.txt; };
let mixed = scope {
    value: if exists ./number-branch { seed } else { "text" },
    y: seed + 1, z: seed + 2,
};
mixed::value = "reference";
mixed::value = seed + 2;
(mixed::value as String) -> ./union.txt;
let zero = seed - seed;
let nan = zero / zero;
let bits = scope { zero: -zero, nan: nan, y: seed, z: seed + 1 };
if $std::math::signbit(bits::zero) and bits::nan != bits::nan {
    "ieee" -> ./ieee.txt;
};
let unit = seed / seed;
let reduction = 0;
for value in [unit * 10000000000000000, unit, -unit * 10000000000000000] {
    reduction = reduction + value;
};
(reduction as String) -> ./ordered-reduction.txt;
fn named(value: Number) {
    scope Named { x: value, y: value + 1, z: value + 2 };
    Named
};
let singleton = named(seed);
singleton::x = 31;
if named(999)::x == 31 { "singleton" -> ./singleton.txt; };
let left = make(0);
let right = make(0);
left::x = 101;
if right::x == seed
    and not $std::ranges::contains([left], right)
    and $std::ranges::contains([left], left) {
    "fresh" -> ./identity.txt;
};
acquire;
"""

OUTPUTS: dict[str, bytes] = {
    "scan.txt": b"2528",
    "snapshot.txt": b"snapshot-keeps-instances",
    "capture.txt": b"shared-location",
    "capture-frame.txt": b"active-frame-location",
    "mixed-depth.txt": b"numeric-prefix-reference-tail",
    "nullable.txt": b"null",
    "restored.txt": b"13",
    "union.txt": b"9",
    "ieee.txt": b"ieee",
    "ordered-reduction.txt": b"0",
    "singleton.txt": b"singleton",
    "identity.txt": b"fresh",
}


def execute(
    binary: Path, work: Path, arguments: list[str]
) -> subprocess.CompletedProcess[str]:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=work,
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )
    if result.returncode != 0:
        raise AssertionError(
            f"{arguments}: exit {result.returncode}\n{result.stdout}\n{result.stderr}"
        )
    return result


def run_case(binary: Path, work: Path, route: str, level: int) -> dict[str, object]:
    work.mkdir(parents=True)
    (work / "seed.txt").write_text("7", encoding="utf-8")
    (work / "number-branch").write_text("selected", encoding="utf-8")
    source: Path = work / "objects.msync"
    source.write_text(PROGRAM, encoding="utf-8", newline="\n")
    if route == "source":
        execute(binary, work, ["run", str(source), f"-O{level}"])
    else:
        artifact: Path = work / "objects.mso"
        execute(
            binary, work, ["compile", str(source), f"-O{level}", "-o", str(artifact)]
        )
        if route == "mexe":
            executable: Path = work / "objects.mexe"
            execute(binary, work, ["bind-ir", str(artifact), "-o", str(executable)])
            artifact = executable
        assert not any((work / name).exists() for name in OUTPUTS), (
            "Preparation performed output writes."
        )
        source.unlink()
        execute(binary, work, ["run-ir", str(artifact)])
    observed: dict[str, str] = {}
    for name, expected in OUTPUTS.items():
        actual: bytes = (work / name).read_bytes()
        assert actual == expected, (route, level, name, actual, expected)
        observed[name] = actual.decode("utf-8")
    return {"route": route, "optimization": level, "outputs": observed}


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--route", action="append", choices=ROUTES, default=[])
    parser.add_argument("--level", action="append", type=int, default=[])
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    (ROOT / ".temp").mkdir(exist_ok=True)
    work: Path = Path(tempfile.mkdtemp(prefix="object-locality-", dir=ROOT / ".temp"))
    observations: list[dict[str, object]] = [
        run_case(binary, work / f"O{level}-{route}", route, level)
        for level in (arguments.level or [2])
        for route in (arguments.route or list(ROUTES))
    ]
    report: dict[str, object] = {
        "status": "passed",
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "source_sha256": hashlib.sha256(PROGRAM.encode()).hexdigest(),
        "assemblies": {
            path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(binary.parent.glob("Msync.*.dll"))
        },
        "work": str(work),
        "observations": observations,
    }
    output: Path = arguments.output or work / "report.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
