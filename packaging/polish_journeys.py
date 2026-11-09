#!/usr/bin/env python3
"""Run only the changed terminal, editor insight and formatting contracts."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
import sys
from dataclasses import asdict
from pathlib import Path

from platform_journeys import ROOT, Phase, run_phase


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--native-origin",
        type=Path,
        help="Recorded artifact identity for a reused native image",
    )
    parser.add_argument(
        "--phase",
        action="append",
        choices=[
            "presentation",
            "lsp-analysis",
            "lsp-response",
            "formatter",
            "skill",
            "completion",
            "editing-regression",
            "reference",
            "reference-options",
            "identifier-return",
            "mutable-state",
            "mutable-getters",
            "scheduling",
            "repl-state",
            "linear-effect",
            "memory-layout",
            "memory-nullable-payloads",
            "nullable-scope-user",
            "global-import",
            "global-terminal",
            "call-order",
            "scheduler-frontier-source",
            "scheduler-frontier-executable",
            "artifact-view",
            "artifact-disassembly",
            "navigation",
            "repl-palette",
            "terminal-detail",
            "terminal-presentation",
            "hints",
            "authentication",
        ],
        help="Run only the named changed contract; may be repeated",
    )
    parser.add_argument("--output", type=Path, default=ROOT / ".temp/platform-results")
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    output = arguments.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    phases: list[Phase] = []
    report: dict[str, object] = {
        "platform": platform.platform(),
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "fixture_revision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
    }
    if arguments.native_origin is not None:
        origin: dict[str, object] = json.loads(
            arguments.native_origin.read_text(encoding="utf-8")
        )
        assert origin["binary_sha256"] == report["binary_sha256"], (
            "Reused native image does not match its artifact identity"
        )
        report["native_origin"] = origin
        report["native_source_revision"] = str(origin["artifact_name"]).rsplit("-", 1)[
            -1
        ]
    else:
        report["native_source_revision"] = report["fixture_revision"]
    journeys = [
        ("presentation", "presentation_journey.py"),
        ("lsp-analysis", "lsp_analysis_journey.py"),
        ("lsp-response", "lsp_response_journey.py"),
        ("formatter", "formatter_journey.py"),
        ("skill", "skill_journey.py"),
        ("completion", "completion_journey.py"),
        ("editing-regression", "editing_regression_journey.py"),
        ("reference", "reference_journey.py"),
        ("reference-options", "presentation_journey.py"),
        ("identifier-return", "identifier_return_journey.py"),
        ("mutable-state", "mutable_state_journey.py"),
        ("mutable-getters", "mutable_state_journey.py"),
        ("scheduling", "scheduling_journey.py"),
        ("repl-state", "repl_state_journey.py"),
        ("linear-effect", "linear_effect_journey.py"),
        ("memory-layout", "memory_layout_journey.py"),
        ("memory-nullable-payloads", "mutable_state_journey.py"),
        ("nullable-scope-user", "nullable_scope_journey.py"),
        ("global-import", "global_journey.py"),
        ("call-order", "call_order_journey.py"),
        ("scheduler-frontier-source", "scheduling_journey.py"),
        ("scheduler-frontier-executable", "scheduling_journey.py"),
        ("artifact-view", "artifact_view_journey.py"),
        ("artifact-disassembly", "artifact_disassembly_journey.py"),
        ("navigation", "navigation_journey.py"),
        ("repl-palette", "repl_palette_journey.py"),
    ]
    if sys.platform != "win32":
        journeys.append(("terminal-presentation", "terminal_presentation_journey.py"))
        journeys.append(("hints", "hint_journey.py"))
        journeys.append(("terminal-detail", "terminal_detail_journey.py"))
        journeys.append(("global-terminal", "terminal_detail_journey.py"))
        journeys.append(("authentication", "authentication_journey.py"))
    if arguments.phase:
        selected: set[str] = set(arguments.phase)
        unavailable = selected - {name for name, _ in journeys}
        if unavailable:
            parser.error(f"Phases unavailable on this host: {sorted(unavailable)}")
        journeys = [(name, script) for name, script in journeys if name in selected]
    for name, script in journeys:
        extra: list[str] = []
        if name == "completion" and sys.platform == "linux":
            extra.append("--ssh-fixture")
        if name == "editing-regression":
            extra.append("--converter")
        if name == "scheduling":
            extra.append("--executable")
        if name.startswith("scheduler-frontier-"):
            for case in (
                "selected",
                "owner",
                "inherited-acquire",
                "after-region",
                "acquire-break",
                "deferred-dataflow",
                "native-independent",
            ):
                extra.extend(["--case", case])
            if name == "scheduler-frontier-executable":
                extra.append("--executable")
        if name == "reference-options":
            extra.append("--reference-only")
        if name == "global-terminal":
            extra.extend(["--case", "global"])
        if name == "mutable-getters":
            for case in (
                "constructor-captured-holes",
                "constraint-conversion",
                "deferred-null-captures",
            ):
                extra.extend(["--case", case])
            extra.extend(["--properties", "0"])
        if name == "memory-nullable-payloads":
            for case in (
                "nullable-function-conditional",
                "nullable-native-function",
                "nullable-function-alternatives",
            ):
                extra.extend(["--case", case])
            extra.extend(["--properties", "0"])
        phases.append(
            run_phase(
                name,
                [
                    sys.executable,
                    str(ROOT / "tests/Msync.EndToEnd" / script),
                    "--binary",
                    str(binary),
                    *extra,
                ],
                output,
            )
        )
        report["phases"] = [asdict(phase) for phase in phases]
        (output / "polish.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )
    return int(any(phase.returncode != 0 for phase in phases))


if __name__ == "__main__":
    raise SystemExit(main())
