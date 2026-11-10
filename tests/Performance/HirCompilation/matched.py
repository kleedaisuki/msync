#!/usr/bin/env python3
"""Compare three identical sources on frozen released/candidate Compile hosts."""

from __future__ import annotations

import argparse
import json
import platform
import sys
import time
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from measure import SOURCES, distribution, execute, sha256


def image(host: Path) -> dict[str, Any]:
    return {
        "path": str(host),
        "host_sha256": sha256(host),
        "probe_sha256": sha256(host.with_suffix(".dll")),
        "assemblies": {
            path.name: sha256(path) for path in sorted(host.parent.glob("Msync.*.dll"))
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--baseline-reference", type=Path, required=True)
    parser.add_argument("--candidate-receipt", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--retention-only", action="store_true")
    args = parser.parse_args()
    work: Path = args.work.resolve()
    output: Path = args.output.resolve()
    work.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    hosts: dict[str, Path] = {
        "baseline": args.baseline.resolve(),
        "candidate": args.candidate.resolve(),
    }
    images = {name: image(host) for name, host in hosts.items()}
    reference: dict[str, Any] = json.loads(
        args.baseline_reference.read_text(encoding="utf-8")
    )
    producer: dict[str, Any] = json.loads(
        args.candidate_receipt.read_text(encoding="utf-8")
    )
    if (
        images["baseline"]["assemblies"]
        != reference["component"]["adjacent_assemblies"]
    ):
        raise RuntimeError(
            "Baseline assemblies differ from the accepted released image"
        )
    for name, expected in producer["assemblies"].items():
        if sha256(args.candidate_receipt.parent / name) != expected:
            raise RuntimeError(f"Producer image changed: {name}")
    for name, actual in images["candidate"]["assemblies"].items():
        if producer["assemblies"][name] != actual:
            raise RuntimeError(
                f"Candidate probe dependency differs from producer: {name}"
            )
    source_hashes: dict[str, str] = {}
    for name in ("none", "list", "tree"):
        source = work / f"{name}.msync"
        source.write_text(SOURCES[name], encoding="utf-8", newline="\n")
        source_hashes[name] = sha256(source)
        original = next(
            record for record in reference["records"] if record["case"] == name
        )
        if original["source_sha256"] != source_hashes[name]:
            raise RuntimeError(f"Source differs from the accepted baseline: {name}")
    observations: list[dict[str, Any]] = []
    report: dict[str, Any] = {
        "status": "prepared" if args.prepare_only else "running",
        "mode": "compile_retention" if args.retention_only else "compile_total",
        "started_utc": datetime.now(UTC).isoformat(),
        "os": platform.platform(),
        "python": sys.version,
        "images": images,
        "baseline_reference_sha256": sha256(args.baseline_reference),
        "candidate_receipt": producer,
        "tiered_compilation": False,
        "source_name": "hir-compilation.msync",
        "warmups_per_process": 3,
        "samples_per_process": None if args.retention_only else args.samples,
        "source_hashes": source_hashes,
        "records": observations,
    }
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if args.prepare_only:
        print(f"READY: {output}")
        return
    for index, (name, level) in enumerate(
        (name, level) for name in ("none", "list", "tree") for level in (0, 2)
    ):
        order = (
            ("baseline", "candidate") if index % 2 == 0 else ("candidate", "baseline")
        )
        pair: dict[str, Any] = {"case": name, "optimization": level, "order": order}
        observations.append(pair)
        for label in order:
            command = [str(hosts[label]), str(work / f"{name}.msync"), str(level)]
            command += (
                ["--retention-only"] if args.retention_only else [str(args.samples)]
            )
            started = time.perf_counter()
            result = execute(command, work)
            measured: dict[str, Any] = json.loads(result.stdout)
            if not args.retention_only:
                if measured["source_sha256"] != source_hashes[name]:
                    raise RuntimeError(f"Probe consumed a different source: {name}")
                measured["warm_ms_summary"] = distribution(measured["warm_ms"])
                measured["warm_allocation_summary"] = distribution(
                    measured["warm_allocated_bytes"]
                )
            pair[label] = measured
            pair[f"{label}_process_seconds"] = time.perf_counter() - started
            output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            print(f"{name} O{level} {label}: complete", flush=True)
    report["status"] = "complete"
    report["finished_utc"] = datetime.now(UTC).isoformat()
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
