"""Exercise bound direct-source JIT on the independently checked policy stream."""

from __future__ import annotations

import argparse
import json
import shutil
import sqlite3
import sys
from collections.abc import Sequence
from contextlib import closing
from pathlib import Path
from typing import Literal

from journeys import Level, invoke, profile

Mode = Literal["direct", "fallback"]


def direct_by_function(database: Path) -> dict[tuple[str, str], tuple[int, int]]:
    with closing(sqlite3.connect(database)) as db:
        return {
            (image, function): (calls, direct)
            for image, function, calls, direct in db.execute(
                """SELECT i.value, f.value, c.value_int, j.value_int
                   FROM records r
                   JOIN attributes i ON i.record_id = r.id AND i.key = 'image.id'
                   JOIN attributes f ON f.record_id = r.id AND f.key = 'function.id'
                   JOIN metrics c ON c.record_id = r.id AND c.key = 'calls'
                   JOIN metrics j ON j.record_id = r.id AND j.key = 'direct_source_calls'
                   WHERE r.name = 'vm.function'"""
            )
        }


def check_route(
    host: Path,
    directory: Path,
    arguments: Sequence[str],
    stdin: bytes,
    stdout: bytes,
    mode: Mode,
    search_path: Path | None = None,
    check_sites: bool = False,
) -> dict[str, int]:
    def check(options: Sequence[str], *, native: bool) -> None:
        actual: bytes = invoke(
            host,
            directory,
            [*options, *arguments],
            data=stdin,
            native=native,
            search_path=search_path,
        )
        if actual != stdout:
            raise AssertionError(
                f"{directory.name}: expected {stdout!r}, got {actual!r}"
            )

    check(["--no-telemetry"], native=True)
    enabled: int = 0
    cold: int = 0
    levels: tuple[Level, ...] = (
        ("profile", "instructions") if check_sites else ("profile",)
    )
    for level in levels:
        auto_db: Path = directory / f"{level}-auto.sqlite"
        off_db: Path = directory / f"{level}-off.sqlite"
        check(["--telemetry-db", str(auto_db), "--telemetry-level", level], native=True)
        check(["--telemetry-db", str(off_db), "--telemetry-level", level], native=False)
        on, off = profile(auto_db, level), profile(off_db, level)
        if on.status != "ok" or off.status != "ok":
            raise AssertionError(
                f"{directory.name}: unexpected execute status {on.status}/{off.status}"
            )
        on.check_logical(off, level)
        if "direct_source_calls" not in on.metrics:
            raise AssertionError(
                f"{directory.name}: runtime did not export direct-source telemetry"
            )
        actual: dict[tuple[str, str], tuple[int, int]] = direct_by_function(auto_db)
        reference: dict[tuple[str, str], tuple[int, int]] = direct_by_function(off_db)
        if reference != {
            key: (calls + direct, 0) for key, (calls, direct) in actual.items()
        }:
            raise AssertionError(
                f"{directory.name}/{level}: canonical call totals changed"
            )
        selected: dict[tuple[str, str], tuple[int, int]] = {
            key: (calls, direct) for key, (calls, direct) in actual.items() if direct
        }
        if level == "profile" and mode == "direct":
            if len(selected) != 3 or on.metrics.get("jit_entries", 0) < sum(
                direct for _, direct in selected.values()
            ):
                raise AssertionError(
                    f"{directory.name}: missing native policy targets: {selected!r}"
                )
            if any(calls != 63 for calls, _ in selected.values()):
                raise AssertionError(
                    f"{directory.name}: unexpected warm-up history {selected!r}"
                )
            enabled = sum(direct for _, direct in selected.values())
            cold = sum(calls for calls, _ in selected.values())
        elif selected:
            raise AssertionError(
                f"{directory.name}/{level}: direct entry without admission"
            )
    return {"direct_source_calls": enabled, "warm_canonical_policy_calls": cold}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--expect", choices=["direct", "fallback"], default="direct")
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    fixture: Path = args.fixture.resolve(strict=True)
    work: Path = args.work.resolve()
    work.mkdir(parents=True)
    source: bytes = (fixture / "workload.msync").read_bytes()
    stdin: bytes = (fixture / "stdin.utf8").read_bytes()
    stdout: bytes = (fixture / "expected.utf8").read_bytes()
    build: Path = work / "build"
    build.mkdir()
    (build / "policy.msync").write_bytes(source)
    invoke(
        binary,
        build,
        ["--no-telemetry", "-O2", "compile", "policy.msync", "-o", "policy.mso"],
    )
    (build / "policy.msync").unlink()
    invoke(
        binary, build, ["--no-telemetry", "bind-ir", "policy.mso", "-o", "policy.mexe"]
    )
    report: dict[str, dict[str, int]] = {}
    for route, filename in (
        ("source", "policy.msync"),
        ("object", "policy.mso"),
        ("bound", "policy.mexe"),
    ):
        directory: Path = work / route
        directory.mkdir()
        if route == "source":
            (directory / filename).write_bytes(source)
        else:
            shutil.copyfile(build / filename, directory / filename)
        report[route] = check_route(
            binary,
            directory,
            ["-O2", "run", filename],
            stdin,
            stdout,
            args.expect,
            check_sites=route == "object",
        )
        print(f"PASS direct source/{route}: {report[route]}", flush=True)

    if sys.platform == "linux":
        directory = work / "shebang"
        directory.mkdir()
        search_path: Path = directory / "bin"
        search_path.mkdir()
        (search_path / "msync").symlink_to(binary)
        script: Path = directory / "policy.msync"
        script.write_bytes(b"#!/usr/bin/env msync\n" + source)
        script.chmod(0o755)
        report["shebang"] = check_route(
            script,
            directory,
            ["-O2"],
            stdin,
            stdout,
            args.expect,
            search_path=search_path,
        )
        print(f"PASS direct source/shebang: {report['shebang']}", flush=True)
    (work / "result.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    main()
