#!/usr/bin/env python3
"""Exercise selected Core equivalence, artifact compatibility and namespace contracts."""

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
    arguments: tuple[str, ...] = ()


OPTIMIZER_CONTRACTS = (
    Contract(
        "optimization-graph", "OptimizationGraph/Msync.OptimizationGraph.Tests.csproj"
    ),
    Contract(
        "predicate-optimization", "PredicateOptimization/PredicateOptimization.csproj"
    ),
    Contract(
        "scope-scalarization",
        "ScopeScalarization/Msync.ScopeScalarization.Tests.csproj",
    ),
    Contract(
        "loop-optimization", "LoopOptimization/Msync.LoopOptimization.Tests.csproj"
    ),
    Contract(
        "tail-call-optimization",
        "TailCallOptimization/Msync.TailCallOptimization.Tests.csproj",
    ),
    Contract("local-slots", "LocalSlots/Msync.LocalSlots.Tests.csproj"),
    Contract(
        "global-redundancy", "GlobalRedundancy/Msync.GlobalRedundancy.Tests.csproj"
    ),
)
COMPATIBILITY_CONTRACTS = (
    Contract(
        "format-two-captures",
        "Msync.Runtime.Tests/Msync.Runtime.Tests.csproj",
        ("--mutable-compat",),
    ),
    Contract("format-one-paths", "PathProjection/Msync.PathProjection.Tests.csproj"),
    Contract(
        "path-namespace",
        "SchedulingGraph/Msync.SchedulingGraph.Tests.csproj",
        ("--namespace-only",),
    ),
)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".temp/platform-results/state-contracts"
    )
    parser.add_argument(
        "--contract",
        action="append",
        choices=[
            contract.name
            for contract in (*OPTIMIZER_CONTRACTS, *COMPATIBILITY_CONTRACTS)
        ],
    )
    arguments = parser.parse_args()
    output = arguments.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    available = [
        contract
        for contract in OPTIMIZER_CONTRACTS
        if (ROOT / "tests" / contract.project).is_file()
    ]
    contracts = [*available, *COMPATIBILITY_CONTRACTS]
    if arguments.contract:
        selected: set[str] = set(arguments.contract)
        contracts = [contract for contract in contracts if contract.name in selected]
    phases: list[Phase] = []
    report: dict[str, object] = {
        "platform": platform.platform(),
        "source_revision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
        "execution": "Managed structural/equivalence tests on the actual target OS; native CLI journeys are recorded separately.",
        "absent_optional_projects": [
            contract.project
            for contract in OPTIMIZER_CONTRACTS
            if contract not in available
        ],
    }
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
        (output / "state-contracts.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )
    return int(any(phase.returncode != 0 for phase in phases))


if __name__ == "__main__":
    raise SystemExit(main())
