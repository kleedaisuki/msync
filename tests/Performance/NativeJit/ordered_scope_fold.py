"""Verify the native ordered recurrence through source and source-free routes."""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from collections.abc import Sequence
from pathlib import Path

from journeys import Profile, invoke, profile


def verify(
    executable: Path,
    directory: Path,
    command: Sequence[str],
    stdin: bytes,
    expected: bytes,
    rows: int,
    queries: int,
    native_expected: bool,
    *,
    search_path: Path | None = None,
    exact_sites: bool = False,
) -> dict[str, dict[str, int]]:
    def check(args: Sequence[str], *, native: bool) -> None:
        output: bytes = invoke(
            executable,
            directory,
            [*args, *command],
            data=stdin,
            native=native,
            search_path=search_path,
        )
        if output != expected:
            raise AssertionError(f"{directory.name}: wrong stdout {output!r}")

    check(["--no-telemetry"], native=True)
    compiled_at: int = (512 + rows - 1) // rows
    chunks: int = max(0, queries - compiled_at + 1) * ((rows + 255) // 256)
    report: dict[str, dict[str, int]] = {}
    profiles: dict[str, Profile] = {}
    for native, name in ((True, "auto"), (False, "off")):
        database: Path = directory / f"{name}.sqlite"
        check(
            ["--telemetry-db", str(database), "--telemetry-level", "profile"],
            native=native,
        )
        sample: Profile = profile(database, "profile")
        expected_metrics: dict[str, int] = {
            "number_folds": queries,
            "number_fold_rows": queries * rows,
            "number_fold_misses": 0,
            "folded_literal_rows": queries * rows,
            "literal_factories": 0,
            "number_fold_native_chunks": chunks if native and native_expected else 0,
        }
        actual: dict[str, int] = {
            key: sample.metrics.get(key, -1) for key in expected_metrics
        }
        if sample.status != "ok" or actual != expected_metrics:
            raise AssertionError(
                f"{directory.name}/{name}: {actual=} {expected_metrics=}"
            )
        profiles[name] = sample
        report[name] = sample.metrics
    profiles["auto"].check_logical(profiles["off"], "profile")
    if exact_sites:
        database = directory / "instructions.sqlite"
        check(
            ["--telemetry-db", str(database), "--telemetry-level", "instructions"],
            native=True,
        )
        detailed: Profile = profile(database, "instructions")
        if detailed.metrics.get("number_folds") or detailed.metrics.get(
            "folded_literal_rows"
        ):
            raise AssertionError("detailed observation entered a fold")
        if detailed.metrics.get("literal_factories") != rows * queries:
            raise AssertionError("detailed observation skipped literal producers")
        profiles["auto"].check_logical(detailed, "profile")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--rows", type=int, default=129)
    parser.add_argument("--expect", choices=["native", "fallback"], default="native")
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    fixture: Path = args.fixture.resolve(strict=True)
    work: Path = args.work.resolve()
    work.mkdir(parents=True)
    source: bytes = (fixture / "workload.msync").read_bytes()
    stdin: bytes = (fixture / "stdin.utf8").read_bytes()
    expected: bytes = (fixture / "expected.utf8").read_bytes()
    queries: int = int(stdin.splitlines()[0])
    rows: int = args.rows
    native_expected: bool = args.expect == "native"
    prepared: Path = work / "prepare"
    prepared.mkdir()
    shutil.copyfile(fixture / "baseline.mso", prepared / "program.mso")
    invoke(
        binary,
        prepared,
        ["--no-telemetry", "bind-ir", "program.mso", "-o", "program.mexe"],
    )

    report: dict[str, dict[str, dict[str, int]]] = {}
    for route, extension in (("source", "msync"), ("object", "mso"), ("bound", "mexe")):
        directory: Path = work / route
        directory.mkdir()
        name: str = f"program.{extension}"
        if route == "source":
            (directory / name).write_bytes(source)
        else:
            shutil.copyfile(prepared / name, directory / name)
        report[route] = verify(
            binary,
            directory,
            ["-O2", "run", name],
            stdin,
            expected,
            rows,
            queries,
            native_expected,
            exact_sites=route == "bound",
        )
    if sys.platform == "linux":
        directory = work / "shebang"
        directory.mkdir()
        search_path: Path = directory / "bin"
        search_path.mkdir()
        (search_path / "msync").symlink_to(binary)
        script: Path = directory / "program.msync"
        script.write_bytes(b"#!/usr/bin/env msync\n" + source)
        script.chmod(0o755)
        report["shebang"] = verify(
            script,
            directory,
            ["-O2"],
            stdin,
            expected,
            rows,
            queries,
            native_expected,
            search_path=search_path,
        )
    for route, metrics in report.items():
        print(
            f"PASS ordered fold/{route}: {metrics['auto']['number_fold_native_chunks']} native chunks",
            flush=True,
        )
    (work / "result.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    main()
