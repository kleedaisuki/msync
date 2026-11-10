#!/usr/bin/env python3
"""Check prepared directory ordering and genuine self-overlap against real files."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import tempfile
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]


def execute(binary: Path, arguments: list[str], directory: Path) -> None:
    process = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=directory,
        check=False,
        capture_output=True,
        text=True,
        timeout=20,
    )
    if process.returncode != 0:
        raise AssertionError(
            f"{arguments}: exit {process.returncode}\n{process.stdout}\n{process.stderr}"
        )


def prepare_case(directory: Path, case: str) -> tuple[Path, str | int]:
    source = directory / "src"
    target = directory / "dest" / "src"
    target.mkdir(parents=True)
    if case == "member-order":
        (source / "foo").mkdir(parents=True)
        (source / "foo" / "foo.txt").write_text("inside", encoding="utf-8")
        (source / "foo.txt").write_text("sibling", encoding="utf-8")
        (target / "foo").symlink_to(".", target_is_directory=True)
        return target / "foo.txt", "sibling"
    if case == "metadata-order":
        outside = directory / "outside"
        outside.mkdir()
        for name, stamp in [
            ("a-dir", 1_600_000_000_111_111_111),
            ("b-dir", 1_700_000_000_222_222_222),
        ]:
            child = source / name
            child.mkdir(parents=True)
            (child / f"{name}.txt").write_text(name, encoding="utf-8")
            (target / name).symlink_to(outside, target_is_directory=True)
            os.utime(child, ns=(stamp, stamp))
        return outside, (source / "b-dir").stat().st_mtime_ns
    (source / "foo").mkdir(parents=True)
    (source / "foo" / "x.txt").write_text("x", encoding="utf-8")
    (target / "foo").symlink_to(source, target_is_directory=True)
    stamp = 1_750_000_000_333_333_333
    os.utime(source / "foo", ns=(1_600_000_000_111_111_111, 1_600_000_000_111_111_111))
    os.utime(source, ns=(stamp, stamp))
    return target, source.stat().st_mtime_ns


def run_case(binary: Path, directory: Path, case: str, route: str) -> dict[str, Any]:
    observed, expected = prepare_case(directory, case)
    source = directory / "main.msync"
    source.write_text("./src -> ./dest/;\n", encoding="utf-8")
    if route == "source":
        execute(binary, ["run", str(source)], directory)
    else:
        relocatable = directory / "main.mso"
        execute(
            binary, ["compile", str(source), "-O2", "-o", str(relocatable)], directory
        )
        program = relocatable
        if route == "mexe":
            program = directory / "main.mexe"
            execute(
                binary, ["bind-ir", str(relocatable), "-o", str(program)], directory
            )
        source.unlink()
        execute(binary, ["run-ir", str(program)], directory)
    actual = (
        observed.read_text(encoding="utf-8")
        if isinstance(expected, str)
        else observed.stat().st_mtime_ns
    )
    if actual != expected:
        raise AssertionError(f"{case}/{route}: expected {expected!r}, found {actual!r}")
    return {"case": case, "route": route, "expected": expected, "actual": actual}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    binary = args.binary.resolve()
    scratch = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    observations: list[dict[str, Any]] = []
    with tempfile.TemporaryDirectory(
        prefix="directory-plan-", dir=scratch
    ) as temporary:
        work = Path(temporary)
        for case in ["member-order", "metadata-order", "source-overlap"]:
            for route in ["source", "mso", "mexe"]:
                observations.append(
                    run_case(binary, work / f"{case}-{route}", case, route)
                )
    assemblies = {
        path.name: hashlib.sha256(path.read_bytes()).hexdigest()
        for path in [*binary.parent.glob("Msync.*.dll"), binary.with_suffix(".dll")]
        if path.is_file()
    }
    report = {
        "status": "passed",
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "assemblies": assemblies,
        "observations": observations,
    }
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(
        f"PASS directory plan order, metadata and overlap: {len(observations)} real-file routes"
    )


if __name__ == "__main__":
    main()
