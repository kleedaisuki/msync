#!/usr/bin/env python3
"""Exercise directory publication metadata through source and both artifact stages."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def invoke(binary: Path, arguments: Sequence[str], cwd: Path) -> None:
    result = subprocess.run(
        [str(binary), "--no-config", *arguments], cwd=cwd,
        text=True, encoding="utf-8", capture_output=True, check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(f"{arguments}: exit {result.returncode}\n{result.stdout}\n{result.stderr}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    binary: Path = args.binary.resolve()
    (ROOT / ".temp").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="directory-metadata-", dir=ROOT / ".temp") as temporary:
        root = Path(temporary)
        source = root / "input-tree"
        (source / "nested").mkdir(parents=True)
        (source / "nested" / "file").write_bytes(b"directory metadata and exact bytes\n")
        expected = {"": 1_700_000_000_123_456_700, "nested": 1_700_000_000_765_432_100}
        for relative, timestamp in expected.items():
            os.utime(source / relative, ns=(timestamp, timestamp))
        (root / "copy.msync").write_text("./input-tree -> ./output/;\n", encoding="utf-8")
        invoke(binary, ["compile", "copy.msync", "-o", "copy.mso"], root)
        invoke(binary, ["bind-ir", "copy.mso", "-o", "copy.mexe"], root)
        results: dict[str, dict[str, int]] = {}
        for stage in ("msync", "mso", "mexe"):
            invoke(binary, ["run", f"copy.{stage}"], root)
            target = root / "output" / "input-tree"
            observed = {relative: (target / relative).stat().st_mtime_ns for relative in expected}
            if observed != expected:
                raise AssertionError(f"{stage}: directory mtime mismatch: {observed} != {expected}")
            if (target / "nested" / "file").read_bytes() != b"directory metadata and exact bytes\n":
                raise AssertionError(f"{stage}: copied file bytes changed")
            results[stage] = observed
            shutil.rmtree(root / "output")
        if args.output is not None:
            output: Path = args.output
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
        print("Directory source/object/executable publication preserved nested bytes and directory mtimes.")


if __name__ == "__main__":
    main()
