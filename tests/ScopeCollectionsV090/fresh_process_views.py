#!/usr/bin/env python3
"""Check that namespace read views do not make source acceptance process-dependent."""

from __future__ import annotations

import argparse
import json
import subprocess
import time
from pathlib import Path
from typing import Any

from journey import ROOT, payload_identity

SOURCE: str = """let Family = $std::containers::Set;
let tree = Family::new(Number);
Family::insert(tree, 1);
for value? in Family::range(tree, 0, 2) {};
let nullable = Family::new([null, 3, 1]);
Family::remove(nullable, null);
Family::size(tree)
"""
INVALID_WRITES: dict[str, str] = {
    "wrong-scalar": """let Model = decltype(object { count: 0 });
let checked = object { count: 1 } as Model;
checked::count = "wrong";
""",
    "missing-member": """let Model = decltype(object { count: 0 });
let checked = object { count: 1 } as Model;
checked::missing = 7;
""",
    "nullable-view": """let Model = decltype(object { count: 0 });
let checked? = if $std::fs::exists(./not-observed) { object { count: 1 } as Model } else { null };
checked::count = 7;
""",
}
EXPECTED_ERRORS: dict[str, str] = {
    "wrong-scalar": "E0203",
    "missing-member": "E0205",
    "nullable-view": "E0206",
}


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--processes", type=int, default=16)
    parser.add_argument("--work", type=Path)
    args: argparse.Namespace = parser.parse_args()
    if args.processes < 1:
        parser.error("--processes must be positive")
    binary: Path = args.binary.resolve(strict=True)
    work: Path = (
        args.work or ROOT / ".temp/scope-collections-v090" / f"views-{time.time_ns()}"
    ).resolve()
    if not work.is_relative_to(ROOT / ".temp") or work.exists() and any(work.iterdir()):
        parser.error("--work must be a new directory under repository-root .temp")
    work.mkdir(parents=True, exist_ok=True)
    source: Path = work / "views.msync"
    source.write_text(SOURCE, encoding="utf-8")
    report: dict[str, Any] = {
        "status": "running",
        "binary": str(binary),
        "payload_sha256": payload_identity(binary),
        "processes": args.processes,
        "checks": [],
        "rejected_writes": [],
    }
    try:
        for index in range(args.processes):
            command: list[str] = [
                str(binary),
                "--no-config",
                "--no-telemetry",
                "check",
                str(source),
            ]
            process: subprocess.CompletedProcess[str] = subprocess.run(
                command,
                cwd=work,
                text=True,
                encoding="utf-8",
                capture_output=True,
                timeout=180,
                check=False,
            )
            (work / f"{index}.stdout").write_text(process.stdout, encoding="utf-8")
            (work / f"{index}.stderr").write_text(process.stderr, encoding="utf-8")
            report["checks"].append(
                {"command": command, "exit_code": process.returncode}
            )
            (work / "result.json").write_text(json.dumps(report, indent=2) + "\n")
            if (
                process.returncode
                or process.stderr
                or process.stdout != f"{source}: valid\n"
            ):
                raise RuntimeError(f"Fresh process {index} failed; inspect {work}")
        for name, text in INVALID_WRITES.items():
            invalid: Path = work / f"{name}.msync"
            invalid.write_text(text, encoding="utf-8")
            process = subprocess.run(
                [str(binary), "--no-config", "--no-telemetry", "check", str(invalid)],
                cwd=work,
                text=True,
                encoding="utf-8",
                capture_output=True,
                timeout=180,
                check=False,
            )
            (work / f"{name}.stderr").write_text(process.stderr, encoding="utf-8")
            if (
                process.returncode != 1
                or f"error[{EXPECTED_ERRORS[name]}]" not in process.stderr
            ):
                raise RuntimeError(
                    f"Invalid checked write {name} was not diagnosed; inspect {work}"
                )
            report["rejected_writes"].append(name)
        if payload_identity(binary) != report["payload_sha256"]:
            raise RuntimeError(
                "Executable payload changed during fresh-process acceptance"
            )
        report["status"] = "complete"
        print(f"PASS {args.processes} independent namespace-view analyses")
    except BaseException as error:
        report.update(status="failed", error=str(error))
        raise
    finally:
        (work / "result.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
