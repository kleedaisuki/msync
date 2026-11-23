#!/usr/bin/env python3
"""Run the maintained HPIR state contracts on the host OS."""

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
        "hpir-runtime",
        "HpirRuntime",
        "Physical calls, frame/heap ownership, cancellation and artifact ABI",
    ),
    Contract(
        "hpir-source",
        "HpirPipeline",
        "Source through detached binding, actual effects, layouts and interactive state",
    ),
)
BY_NAME: dict[str, Contract] = {contract.name: contract for contract in CONTRACTS}

# Compatibility names select the maintained behavior, never the old backend.
ALIASES: dict[str, tuple[str, ...]] = {
    **dict.fromkeys(
        (
            "native-artifact", "native-model", "scope-layout",
            "cross-block-memory", "aggregate-arguments", "format-one-paths",
        ),
        ("hpir-runtime", "hpir-source"),
    ),
    **dict.fromkeys(
        (
            "native-optimizer", "native-middle-end", "tail-call-optimization",
            "native-allocation", "native-execution", "native-preparation",
            "context-binding", "native-sessions", "current-captures", "current-paths",
            "stdlib-publication", "stdlib-demand", "stdlib-captures",
            "constant-interner", "optimization-graph", "predicate-optimization",
            "scope-scalarization", "loop-optimization", "local-slots",
            "global-redundancy", "memory-effects", "scheduler-retirement",
            "path-namespace", "stdlib-loading",
        ),
        ("hpir-source",),
    ),
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
        parser.error(f"Required HPIR contract projects are unavailable: {missing}")
    phases: list[Phase] = []
    report: dict[str, object] = {
        "platform": platform.platform(),
        "source_revision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
        "execution": "HPIR managed contracts on this OS; installed Native AOT behavior has its own acceptance.",
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
