#!/usr/bin/env python3
"""Run maintained native state contracts, preserving named platform selections."""

from __future__ import annotations

import argparse
import json
import platform
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path

from platform_journeys import ROOT, Phase, run_phase


@dataclass(frozen=True)
class Contract:
    name: str
    project: str
    intent: str
    arguments: tuple[str, ...] = ()


CONTRACTS: tuple[Contract, ...] = (
    Contract(
        "native-model",
        "NativeModel",
        "All builtin storage, typed roots, physical layout and GC invariants",
    ),
    Contract(
        "native-artifact",
        "NativeArtifact",
        "Portable encoding, ABI, typed CFG and malformed input rejection",
    ),
    Contract(
        "native-optimizer",
        "NativeOptimization",
        "Constant identity, native memory facts, escape and exact scalar semantics",
    ),
    Contract(
        "native-middle-end",
        "NativeMiddleEnd",
        "General control relocation, dataflow, typed calls and captured-location inlining",
    ),
    Contract(
        "tail-call-optimization",
        "TailCallOptimization",
        "100000-step source recursion, parallel arguments and fresh captured locations",
    ),
    Contract(
        "native-allocation",
        "NativeAllocationV090",
        "Real logical allocation, alias freshness, frame placement and region bounds",
    ),
    Contract(
        "native-execution",
        "NativeExecution",
        "Real source/artifact execution, resources, suspension, scheduling and diagnostics",
    ),
    Contract(
        "native-preparation",
        "NativePreparation",
        "Context binding, selected transfer planning and no executable replan",
    ),
    Contract(
        "context-binding",
        "ContextBinding",
        "Frozen origins, residual branches, loop effects and post-publication canonical paths",
    ),
    Contract(
        "native-sessions",
        "NativeSessions",
        "Persistent roots, captures, failure reconciliation and editor isolation",
    ),
    Contract(
        "current-captures",
        "Msync.Runtime.Tests",
        "Shared source locations and actual persistent session execution",
        ("--mutable-state",),
    ),
    Contract(
        "current-paths",
        "PathProjection",
        "Symbolic/native spelling, endpoint identity and source-free path projection",
    ),
    Contract(
        "stdlib-publication",
        "StdlibLoading",
        "Selective source modules, mutable aliases and captured module/session identity",
    ),
    Contract(
        "stdlib-demand",
        "StdlibLoading",
        "Demand publication, shared family aliases and source-order dependencies",
        ("--demand-loading",),
    ),
    Contract(
        "stdlib-captures",
        "StdlibLoading",
        "Captured module replacement, retained aliases and persistent session identity",
        ("--captured-modules",),
    ),
)
BY_NAME: dict[str, Contract] = {contract.name: contract for contract in CONTRACTS}

# These selection names remain usable by platform jobs; retired implementation
# mechanisms are not revived merely to execute their old white-box assertions.
ALIASES: dict[str, tuple[str, ...]] = {
    "constant-interner": ("native-optimizer",),
    "optimization-graph": ("native-middle-end",),
    "predicate-optimization": ("native-middle-end", "native-execution"),
    "scope-scalarization": ("native-optimizer", "native-allocation"),
    "loop-optimization": ("native-middle-end", "native-execution"),
    "local-slots": ("native-middle-end", "native-sessions"),
    "global-redundancy": ("native-middle-end",),
    "format-one-paths": ("native-artifact", "current-paths"),
    "cross-block-memory": ("native-middle-end", "native-optimizer"),
    "memory-effects": ("native-execution", "native-preparation"),
    "scope-layout": ("native-model", "native-allocation"),
    "aggregate-arguments": ("native-middle-end", "native-sessions"),
    "scheduler-retirement": ("native-execution",),
    "path-namespace": ("native-execution", "native-preparation", "context-binding"),
    "stdlib-loading": ("stdlib-publication", "stdlib-demand", "stdlib-captures"),
}


def select(names: list[str] | None) -> tuple[Contract, ...]:
    if not names:
        return CONTRACTS
    selected: set[str] = set()
    for name in names:
        selected.update(ALIASES.get(name, (name,)))
    return tuple(contract for contract in CONTRACTS if contract.name in selected)


def main() -> int:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".temp/platform-results/state-contracts"
    )
    parser.add_argument("--contract", action="append", choices=(*BY_NAME, *ALIASES))
    parser.add_argument(
        "--list",
        action="store_true",
        help="Show maintained contracts and compatibility selections",
    )
    arguments: argparse.Namespace = parser.parse_args()
    if arguments.list:
        print(
            json.dumps(
                {
                    "contracts": [asdict(contract) for contract in CONTRACTS],
                    "aliases": ALIASES,
                },
                indent=2,
            )
        )
        return 0
    output: Path = arguments.output.resolve()
    if not output.is_relative_to(ROOT):
        parser.error("--output must be inside the repository")
    output.mkdir(parents=True, exist_ok=True)
    contracts: tuple[Contract, ...] = select(arguments.contract)
    missing: list[str] = [
        contract.project
        for contract in contracts
        if not (ROOT / "tests" / contract.project).exists()
    ]
    if missing:
        parser.error(f"Required native contract projects are unavailable: {missing}")
    phases: list[Phase] = []
    report: dict[str, object] = {
        "platform": platform.platform(),
        "source_revision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
        "execution": "Native model/IR managed contracts on this actual OS; native CLI and platform journeys remain independent acceptance.",
        "requested": arguments.contract or [contract.name for contract in contracts],
        "contracts": [asdict(contract) for contract in contracts],
        "compatibility_selections": {
            name: ALIASES[name] for name in arguments.contract or [] if name in ALIASES
        },
    }
    path: Path = output / "state-contracts.json"
    for contract in contracts:
        phases.append(
            run_phase(
                contract.name,
                [
                    "dotnet",
                    "run",
                    "--project",
                    str(ROOT / "tests" / contract.project),
                    "-c",
                    "Release",
                    "--artifacts-path",
                    str(ROOT / "artifacts/state-contracts"),
                    "-p:RestoreLockedMode=true",
                    "--",
                    *contract.arguments,
                ],
                output,
            )
        )
        report["phases"] = [asdict(phase) for phase in phases]
        report["status"] = (
            "failed" if any(phase.returncode != 0 for phase in phases) else "running"
        )
        path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    report["status"] = (
        "failed" if any(phase.returncode != 0 for phase in phases) else "complete"
    )
    path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return int(report["status"] != "complete")


if __name__ == "__main__":
    raise SystemExit(main())
