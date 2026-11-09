#!/usr/bin/env python3
"""Exercise native lexical snapshots and deterministic source-free artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def invoke(binary: Path, mode: str) -> bytes:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), mode], cwd=ROOT, capture_output=True, check=False
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"{mode} exited {result.returncode}:\n"
            + result.stderr.decode("utf-8", errors="replace")
        )
    return result.stdout


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument(
        "--output",
        type=Path,
        default=ROOT / ".temp/performance-v080/current/components/snapshots.json",
    )
    options = parser.parse_args()
    binary: Path = options.binary.resolve()
    before: str = hashlib.sha256(binary.read_bytes()).hexdigest()
    interner: bytes = invoke(binary, "--check-interner")
    snapshots: bytes = invoke(binary, "--check-snapshots")
    first: bytes = invoke(binary, "--emit-capture")
    second: bytes = invoke(binary, "--emit-capture")
    if first != second or not first.startswith(b"MSYNCBC\0"):
        raise AssertionError(
            "Fresh native processes emitted different capture artifacts"
        )
    if hashlib.sha256(binary.read_bytes()).hexdigest() != before:
        raise AssertionError("The tested native image changed during execution")
    output: Path = options.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    fixture: Path = Path(tempfile.mkdtemp(prefix="capture-", dir=output.parent))
    (fixture / "first.mso").write_bytes(first)
    (fixture / "second.mso").write_bytes(second)
    report: dict[str, str | int | bool] = {
        "binary": str(binary),
        "sha256": before,
        "passed": True,
        "interner": interner.decode("utf-8"),
        "snapshots": snapshots.decode("utf-8"),
        "artifact_sha256": hashlib.sha256(first).hexdigest(),
        "artifact_bytes": len(first),
        "fixture": str(fixture),
    }
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(interner.decode("utf-8").strip())
    print(snapshots.decode("utf-8").strip())
    print(f"Fresh-process byte identity passed: {len(first)} bytes; {output}")


if __name__ == "__main__":
    main()
