#!/usr/bin/env python3
"""Write compiler-tail inputs; fixture generation is never part of timing."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def bindings(size: int) -> str:
    lines = ["scope Routes { route: fn(file: Path) { ./root / file }, };"]
    lines.extend(f"let value_{index} = Routes::route(./input_{index});" for index in range(size))
    lines.append(f"value_{size - 1}")
    return "\n".join(lines) + "\n"


def scans(size: int, *, scope: bool) -> str:
    lines = ["let seed = $std::io::read_text(./seed.txt) as Number;"]
    if scope:
        lines.append(
            "let rows = $std::ranges::materialize(["
            "scope { x: seed, y: seed + 1 },"
            "scope { x: seed + 2, y: seed + 3 }]);"
        )
    else:
        lines.append("let rows = $std::ranges::materialize($std::ranges::iota(seed, seed + 1024));")
    lines.extend(f"let total_{index} = 0;" for index in range(size))
    for index in range(size):
        value = "value::x" if scope else "value"
        lines.append(
            f"for value in rows {{ total_{index} = total_{index} + {value} * {index + 1}; }};"
        )
    lines.append(" + ".join(f"total_{index}" for index in range(size)))
    return "\n".join(lines) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--bindings", nargs="+", type=int, default=[1024, 4096])
    parser.add_argument("--scans", nargs="+", type=int, default=[8, 32, 128])
    args = parser.parse_args()
    output: Path = args.output.resolve()
    binding_sizes: list[int] = args.bindings
    scan_sizes: list[int] = args.scans
    if any(size < 1 for size in (*binding_sizes, *scan_sizes)):
        parser.error("Input sizes must be positive")
    output.mkdir(parents=True, exist_ok=True)
    sources: list[tuple[str, int, str]] = [
        ("bindings", size, bindings(size)) for size in binding_sizes
    ]
    for scope in (False, True):
        sources.extend(
            ("scope_scans" if scope else "number_scans", size, scans(size, scope=scope))
            for size in scan_sizes
        )
    inventory: list[dict[str, str | int]] = []
    for name, size, source in sources:
        path = output / f"{name}-{size}.msync"
        contents = source.encode("utf-8")
        path.write_bytes(contents)
        inventory.append(
            {
                "case": name,
                "size": size,
                "path": str(path),
                "source_bytes": len(contents),
                "source_sha256": hashlib.sha256(contents).hexdigest(),
            }
        )
    (output / "index.json").write_text(json.dumps(inventory, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
