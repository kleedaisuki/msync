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

from world_scaling import scaling_cases

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
EXTRA_SOURCES: dict[str, str] = {"arithmetic": "1 + 2"}


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
    parser.add_argument("--producer-receipt", type=Path)
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--retention-only", action="store_true")
    parser.add_argument("--world-scaling", action="store_true")
    parser.add_argument("--case", action="append", choices=(*SOURCES, *EXTRA_SOURCES))
    args = parser.parse_args()
    if args.world_scaling and args.case:
        parser.error("--world-scaling selects its own four cases; omit --case")
    binary: Path | None = args.binary.resolve() if args.binary else None
    component: Path | None = args.component.resolve() if args.component else None
    if args.retention_only and component is None:
        parser.error("--retention-only requires --component")
    work: Path = args.work.resolve()
    output: Path = args.output.resolve()
    work.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    scaling = scaling_cases() if args.world_scaling else {}
    sources = {name: case.source for name, case in scaling.items()} or {
        **SOURCES,
        **EXTRA_SOURCES,
    }
    selected: list[str] = args.case or list(scaling or SOURCES)
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
    if args.producer_receipt:
        receipt_path: Path = args.producer_receipt.resolve()
        receipt: dict[str, Any] = json.loads(receipt_path.read_text(encoding="utf-8"))
        verified = {
            name: sha256(receipt_path.parent / name) for name in receipt["assemblies"]
        }
        if verified != receipt["assemblies"]:
            raise RuntimeError("Producer receipt does not match its frozen assemblies")
        if component is not None:
            for name, actual in image["component"]["adjacent_assemblies"].items():
                if actual != verified[name]:
                    raise RuntimeError(f"Probe assembly differs from producer: {name}")
        image["producer"] = {"receipt_path": str(receipt_path), "receipt": receipt}
    for name in selected:
        source = work / f"{name}.msync"
        source.write_text(sources[name], encoding="utf-8", newline="\n")
        for level in (0, 2):
            record: dict[str, Any] = {
                "case": name,
                "optimization": level,
                "source": sources[name],
                "source_sha256": sha256(source),
            }
            if name in scaling:
                case = scaling[name]
                record["scaling"] = {
                    "unrelated_private_bindings": case.world_bindings,
                    "calls": case.calls,
                    "source_function_definitions": 1,
                    "argument_shape": "Scope{item:Number}",
                    "result_shape": "Scope{value:Scope{item:Number}}",
                    "consumed_result_indices": [0, case.calls // 2, case.calls - 1],
                }
            if binary is not None and not args.retention_only:
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
                component_args: list[str] = [
                    str(component),
                    str(source),
                    str(level),
                    str(args.samples),
                ]
                if args.retention_only:
                    component_args.append("--retention-only")
                report = execute(component_args, work)
                parsed: dict[str, Any] = json.loads(report.stdout)
                if not args.retention_only:
                    measurements = (
                        parsed["phases"]
                        if parsed["stage"] == "typed_hir_phases"
                        else [parsed]
                    )
                    for measurement in measurements:
                        measurement["warm_ms_summary"] = distribution(
                            measurement["warm_ms"]
                        )
                        measurement["warm_allocation_summary"] = distribution(
                            measurement["warm_allocated_bytes"]
                        )
                record["managed_component"] = parsed
            records.append(record)
            output.write_text(json.dumps(image, indent=2) + "\n", encoding="utf-8")
    image["status"] = "complete"
    output.write_text(json.dumps(image, indent=2) + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
