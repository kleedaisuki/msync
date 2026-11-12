"""Measure the fixed native simplification budget; correctness remains independent."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[1]
BASELINE: Path = ROOT / "docs/design/native-code-budget-v090.json"


def count(arguments: list[str]) -> dict[str, Any]:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        ["cloc", "--json", "--by-file", *arguments],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )
    if result.returncode:
        raise RuntimeError(f"cloc failed ({result.returncode}): {result.stderr}")
    report: dict[str, Any] = json.loads(result.stdout)
    return {name: row for name, row in report.items() if name not in {"header", "SUM"}}


def measure(baseline: dict[str, Any]) -> dict[str, Any]:
    files: dict[str, Any] = {
        **count(["src", "--include-ext=cs", "--exclude-dir=bin,obj"]),
        **count(["stdlib", "--include-ext=msync", "--force-lang=C#,msync"]),
    }
    code: int = sum(row["code"] for row in files.values())
    physical: int = sum(
        row["code"] + row["blank"] + row["comment"] for row in files.values()
    )
    fixed_changed: list[str] = [
        name
        for name in baseline["fixed_generated_payloads"]
        if not (ROOT / name).is_file()
        or hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
        != baseline["files"][name]["sha256"]
    ]
    targets: dict[str, int] = baseline["hard_target"]
    numerical: bool = (
        code <= targets["maximum_code_lines"]
        and physical <= targets["maximum_physical_lines"]
        and not fixed_changed
    )
    return {
        "status": "numerical-target-met" if numerical else "incomplete",
        "scope": baseline["scope"],
        "baseline_code_lines": baseline["totals"]["code_lines"],
        "current_code_lines": code,
        "net_code_lines_removed": baseline["totals"]["code_lines"] - code,
        "net_code_reduction_fraction": 1 - code / baseline["totals"]["code_lines"],
        "baseline_physical_lines": baseline["totals"]["physical_lines"],
        "current_physical_lines": physical,
        "maximum_code_lines": targets["maximum_code_lines"],
        "maximum_physical_lines": targets["maximum_physical_lines"],
        "fixed_generated_payloads_changed": fixed_changed,
        "added_product_files": sorted(set(files) - set(baseline["files"])),
        "deleted_product_files": sorted(set(baseline["files"]) - set(files)),
        "per_file": files,
        "acceptance": "Counts alone are insufficient: audit moved/encoded/generated/minified logic and require independent semantic user journeys. No feature/test removal credit.",
    }


def main() -> int:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report-only", action="store_true")
    options: argparse.Namespace = parser.parse_args()
    output: Path = options.output.resolve()
    if not output.is_relative_to(ROOT):
        parser.error("--output must remain inside the repository")
    if output.exists():
        parser.error("--output must be new; prior budget evidence is not overwritten")
    baseline: dict[str, Any] = json.loads(BASELINE.read_text(encoding="utf-8"))
    report: dict[str, Any] = measure(baseline)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(
        f"{report['status']}: {report['current_code_lines']} code lines "
        f"(maximum {report['maximum_code_lines']}), "
        f"net removed {report['net_code_lines_removed']}"
    )
    return 0 if options.report_only or report["status"] == "numerical-target-met" else 1


if __name__ == "__main__":
    raise SystemExit(main())
