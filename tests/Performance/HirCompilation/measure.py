#!/usr/bin/env python3
"""Observe frozen Native CLI and managed compiler costs for the Typed HIR migration."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import statistics
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

SOURCES: dict[str, str] = {
    "none": "let value = true; value",
    "bool": "$std::bool::both(true, false)",
    "shell": "$std::shell::pwd()",
    "list": "let list = $std::containers::List::new([1, 2, 3]); $std::containers::List::size(list)",
    "traits": "$std::meta::is_convertible(Path, String)",
    "tree": (
        'let tree = $std::containers::BTree::new(0, "");\n'
        + "\n".join(
            f'$std::containers::BTree::insert(tree, {index}, "item-{index}");'
            for index in range(24)
        )
        + "\n$std::containers::BTree::get(tree, 17)"
    ),
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def distribution(values: list[float]) -> dict[str, object]:
    ordered = sorted(values)
    position = (len(ordered) - 1) * 0.95
    first = int(position)
    following = min(first + 1, len(ordered) - 1)
    return {
        "p50": statistics.median(ordered),
        "p95": ordered[first]
        + (ordered[following] - ordered[first]) * (position - first),
        "raw": values,
    }


def execute(command: list[str], directory: Path) -> subprocess.CompletedProcess[bytes]:
    result = subprocess.run(
        command,
        cwd=directory,
        capture_output=True,
        check=False,
        env={**os.environ, "NO_COLOR": "1", "DOTNET_TieredCompilation": "0"},
        timeout=180,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"{command!r} failed ({result.returncode}): "
            + result.stderr.decode("utf-8", errors="replace")
        )
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--component", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--source-revision", required=True)
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--case", action="append", choices=tuple(SOURCES))
    args = parser.parse_args()
    binary: Path | None = args.binary.resolve() if args.binary else None
    component: Path | None = args.component.resolve() if args.component else None
    work: Path = args.work.resolve()
    output: Path = args.output.resolve()
    work.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    selected: list[str] = args.case or list(SOURCES)
    records: list[dict[str, Any]] = []
    image: dict[str, Any] = {
        "source_revision": args.source_revision,
        "os": platform.platform(),
        "machine": platform.machine(),
        "python": sys.version,
        "status": "running",
        "records": records,
    }
    if binary is not None:
        image["native"] = {"path": str(binary), "sha256": sha256(binary)}
    if component is not None:
        image["component"] = {
            "path": str(component),
            "sha256": sha256(component),
            "adjacent_assemblies": {
                path.name: sha256(path)
                for path in sorted(component.parent.glob("Msync.*.dll"))
            },
        }
    for name in selected:
        source = work / f"{name}.msync"
        source.write_text(SOURCES[name], encoding="utf-8", newline="\n")
        for level in (0, 2):
            record: dict[str, Any] = {
                "case": name,
                "optimization": level,
                "source": SOURCES[name],
                "source_sha256": sha256(source),
            }
            if binary is not None:
                command = [
                    str(binary),
                    "--no-config",
                    "check",
                    str(source),
                    f"-O{level}",
                ]
                execute(command, work)
                timings: list[float] = []
                for _ in range(args.samples):
                    began = time.perf_counter_ns()
                    execute(command, work)
                    timings.append((time.perf_counter_ns() - began) / 1_000_000)
                record["native_fresh_process_ms"] = distribution(timings)
            if component is not None:
                report = execute(
                    [str(component), str(source), str(level), str(args.samples)], work
                )
                parsed: dict[str, Any] = json.loads(report.stdout)
                parsed["warm_ms_summary"] = distribution(parsed["warm_ms"])
                parsed["warm_allocation_summary"] = distribution(
                    parsed["warm_allocated_bytes"]
                )
                record["managed_component"] = parsed
            records.append(record)
            output.write_text(json.dumps(image, indent=2) + "\n", encoding="utf-8")
    image["status"] = "complete"
    output.write_text(json.dumps(image, indent=2) + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
