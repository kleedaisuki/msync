"""Compare checked Native CLI journeys; use SQLite profiles to explain the delta."""

from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
import statistics
import subprocess
import time
from contextlib import closing
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import cast

ROOT: Path = Path(__file__).resolve().parents[2]
ROUTES: dict[str, str] = {
    "source": "case.msync",
    "object": "case.mso",
    "bound": "case.mexe",
}


@dataclass(frozen=True)
class Image:
    name: str
    binary: Path
    level: int
    compiler: Path

    def invoke(
        self,
        cwd: Path,
        arguments: list[str],
        stdin: bytes = b"",
        telemetry: tuple[str, ...] = ("--no-telemetry",),
        *,
        prepare: bool = False,
    ) -> subprocess.CompletedProcess[bytes]:
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            [
                str(self.compiler if prepare else self.binary),
                "--no-config",
                f"-O{self.level}",
                *telemetry,
                *arguments,
            ],
            cwd=cwd,
            input=stdin,
            capture_output=True,
            check=False,
            timeout=180,
        )
        if result.returncode:
            raise RuntimeError(
                f"{result.args!r} exited {result.returncode} in {cwd}: "
                f"stdout={result.stdout!r}; stderr={result.stderr!r}"
            )
        return result


@dataclass(frozen=True)
class ProfileRow:
    name: str
    rows: int
    duration_ns: int
    metrics: dict[str, int]
    by_name: dict[str, dict[str, int]]


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def prepare(image: Image, source: bytes, workspace: Path) -> Path:
    directory: Path = workspace / image.name
    directory.mkdir()
    (directory / ROUTES["source"]).write_bytes(source)
    image.invoke(
        directory, ["compile", ROUTES["source"], "-o", ROUTES["object"]], prepare=True
    )
    image.invoke(
        directory, ["bind-ir", ROUTES["object"], "-o", ROUTES["bound"]], prepare=True
    )
    return directory


def require_result(result: subprocess.CompletedProcess[bytes], expected: bytes) -> None:
    if result.stdout != expected:
        raise RuntimeError(
            f"{result.args!r}: expected {expected!r}, got {result.stdout!r}; "
            f"stderr={result.stderr!r}"
        )


def profile_rows(database: Path) -> list[ProfileRow]:
    totals: dict[str, dict[str, int]] = {}
    dimensions: dict[str, dict[str, dict[str, int]]] = {}
    with closing(
        sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)
    ) as connection:
        sessions: list[tuple[str, str | None]] = connection.execute(
            "SELECT status, exporter_error FROM sessions"
        ).fetchall()
        if len(sessions) != 1 or sessions[0] != ("complete", None):
            raise RuntimeError(f"Incomplete telemetry in {database}: {sessions!r}")
        rows: list[tuple[str, int, int]] = connection.execute(
            """SELECT name, count(*), COALESCE(sum(duration_ns), 0)
               FROM records GROUP BY name ORDER BY name"""
        ).fetchall()
        # The TEXT column preserves the sink's full unsigned integer domain.
        for name, key, value, label in connection.execute(
            """SELECT r.name, m.key, m.value, a.value FROM metrics m
               JOIN records r ON r.id = m.record_id
               LEFT JOIN attributes a ON a.record_id = r.id AND a.key =
                   CASE r.name WHEN 'vm.opcode' THEN 'opcode'
                               WHEN 'vm.native' THEN 'native.name' END"""
        ):
            count: int = int(value)
            group: dict[str, int] = totals.setdefault(name, {})
            group[key] = group.get(key, 0) + count
            if label is not None:
                selected: dict[str, int] = dimensions.setdefault(name, {}).setdefault(
                    label, {}
                )
                selected[key] = selected.get(key, 0) + count
    if not any(name == "execute" for name, _, _ in rows) or not any(
        name == "vm.opcode" for name, _, _ in rows
    ):
        raise RuntimeError(f"Missing execute span or VM opcode census in {database}")
    return [
        ProfileRow(
            name, count, duration, totals.get(name, {}), dimensions.get(name, {})
        )
        for name, count, duration in rows
    ]


def compare(
    images: tuple[Image, Image],
    route: str,
    repetitions: int,
    warmups: int,
    stdin: bytes,
    expected: bytes,
    source: bytes,
    workspace: Path,
    level: str | None,
    same_artifact: bool,
) -> dict[str, object]:
    if same_artifact:
        shared: Path = prepare(images[0], source, workspace)
        directories: dict[str, Path] = dict.fromkeys(
            (image.name for image in images), shared
        )
    else:
        directories = {
            image.name: prepare(image, source, workspace) for image in images
        }
    arguments: list[str] = ["run", ROUTES[route]]
    measurements: dict[str, list[float]] = {image.name: [] for image in images}
    for pair in range(warmups + repetitions):
        order: tuple[Image, Image] = images if pair % 2 == 0 else images[::-1]
        for image in order:
            start: int = time.perf_counter_ns()
            result = image.invoke(directories[image.name], arguments, stdin)
            elapsed_ms: float = (time.perf_counter_ns() - start) / 1e6
            require_result(result, expected)
            if pair >= warmups:
                measurements[image.name].append(elapsed_ms)

    cases: dict[str, object] = {}
    for image in images:
        directory: Path = directories[image.name]
        database: Path = workspace / f"{image.name}.sqlite"
        samples: list[float] = measurements[image.name]
        record: dict[str, object] = {
            "binary": str(image.binary),
            "binary_sha256": digest(image.binary.read_bytes()),
            "compiler": str(image.compiler),
            "compiler_sha256": digest(image.compiler.read_bytes()),
            "optimization_level": image.level,
            "artifact_sha256": digest((directory / ROUTES[route]).read_bytes()),
            "median_ms": statistics.median(samples),
            "samples_ms": samples,
        }
        if level is not None:
            result = image.invoke(
                directory,
                arguments,
                stdin,
                ("--telemetry-db", str(database), "--telemetry-level", level),
            )
            require_result(result, expected)
            record["profile_sqlite"] = str(database)
            record["profile"] = [asdict(row) for row in profile_rows(database)]
        cases[image.name] = record
    paired: list[float] = [
        candidate - baseline
        for baseline, candidate in zip(
            measurements[images[0].name], measurements[images[1].name], strict=True
        )
    ]
    return {
        "route": route,
        "same_artifact": same_artifact,
        "telemetry_level": level,
        "profile_span_duration_semantics": "inclusive sums; not exclusive CPU or wall time",
        "stdin_sha256": digest(stdin),
        "expected_stdout_sha256": digest(expected),
        "source_sha256": digest(source),
        "images": cases,
        "paired_candidate_minus_baseline_ms": paired,
        "paired_median_ms": statistics.median(paired),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--baseline-compiler", type=Path)
    parser.add_argument("--candidate-compiler", type=Path)
    parser.add_argument("--baseline-level", type=int, choices=range(4), default=2)
    parser.add_argument("--candidate-level", type=int, choices=range(4), default=2)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--stdin", type=Path)
    parser.add_argument("--expected", type=Path, required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--route", choices=ROUTES, default="bound")
    parser.add_argument("--rounds", type=int, default=9)
    parser.add_argument("--warmups", type=int, default=2)
    parser.add_argument(
        "--telemetry-level",
        choices=("profile", "instructions", "diagnostics"),
        default="profile",
    )
    parser.add_argument(
        "--timing-only",
        action="store_true",
        help="Accept binaries without profile support",
    )
    parser.add_argument(
        "--same-artifact",
        action="store_true",
        help="Run both binaries on one baseline-built artifact and working directory",
    )
    args: argparse.Namespace = parser.parse_args()
    if args.rounds < 1 or args.warmups < 0:
        parser.error("expected at least one round and nonnegative warmups")
    if args.same_artifact and (
        args.route == "source" or args.baseline_level != args.candidate_level
    ):
        parser.error(
            "--same-artifact requires an artifact route and matching optimization levels"
        )
    if args.route == "source" and (
        args.baseline_compiler is not None or args.candidate_compiler is not None
    ):
        parser.error("a separate compiler requires an artifact route")

    workspace: Path = args.workspace.resolve()
    workspace.mkdir(parents=True)
    images: tuple[Image, Image] = (
        Image(
            "baseline",
            args.baseline.resolve(),
            args.baseline_level,
            (args.baseline_compiler or args.baseline).resolve(),
        ),
        Image(
            "candidate",
            args.candidate.resolve(),
            args.candidate_level,
            (args.candidate_compiler or args.candidate).resolve(),
        ),
    )
    route: str = args.route
    stdin_path: Path | None = args.stdin
    result: dict[str, object] = compare(
        images,
        route,
        args.rounds,
        args.warmups,
        stdin_path.read_bytes() if stdin_path is not None else b"",
        cast(Path, args.expected).read_bytes(),
        cast(Path, args.source).read_bytes(),
        workspace,
        None if args.timing_only else args.telemetry_level,
        args.same_artifact,
    )
    (workspace / "result.json").write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
