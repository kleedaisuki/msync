"""Compare the four checked msync workloads without rebuilding other languages."""

from __future__ import annotations

import argparse
import json
import math
import os
import sqlite3
import statistics
import subprocess
import time
from contextlib import closing
from dataclasses import dataclass
from pathlib import Path

from run import CASES, ROOT, SIZE, digest, prepare


@dataclass(frozen=True)
class Variant:
    compiler: Path
    probe: Path
    observer: Path | None


def invoke(command: list[str | Path], cwd: Path) -> str:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        command, cwd=cwd, text=True, capture_output=True, check=False, timeout=300
    )
    if result.returncode:
        raise RuntimeError(f"{result.args!r}: {result.stdout}\n{result.stderr}")
    return result.stdout


def build(compiler: Path, directory: Path) -> dict[str, str]:
    expected: dict[str, str] = prepare(compiler, directory)
    for case in expected:
        invoke(
            [
                compiler,
                "--no-config",
                "--no-telemetry",
                "bind-ir",
                directory / f"{case}.mso",
                "-o",
                directory / f"{case}.mexe",
            ],
            ROOT,
        )
    return expected


def measure(
    variant: Variant, image: Path, expected: str, warmups: int, samples: int
) -> list[float]:
    output: str = invoke(
        [
            variant.probe,
            "msync",
            str(SIZE),
            str(warmups),
            str(samples),
            "1",
            expected,
            image,
        ],
        ROOT,
    )
    result: list[float] = [float(line) for line in output.splitlines()]
    if len(result) != samples or not all(
        math.isfinite(sample) and sample > 0 for sample in result
    ):
        raise RuntimeError(f"Missing checked timing samples for {image}: {output!r}")
    return result


def profile(observer: Path, image: Path, database: Path) -> dict[str, object]:
    invoke(
        [
            observer,
            "--no-config",
            "--telemetry-db",
            database,
            "--telemetry-level",
            "profile",
            "run",
            image,
        ],
        ROOT,
    )
    groups: dict[str, dict[str, dict[str, int]]] = {}
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as db:
        sessions: list[tuple[str, str | None]] = db.execute(
            "SELECT status, exporter_error FROM sessions"
        ).fetchall()
        if sessions != [("complete", None)]:
            raise RuntimeError(f"Incomplete telemetry in {database}: {sessions!r}")
        executes: list[tuple[int, str]] = db.execute(
            "SELECT duration_ns, status FROM spans WHERE name='execute'"
        ).fetchall()
        if len(executes) != 1 or executes[0][1] != "ok":
            raise RuntimeError(
                f"Expected one successful execute interval: {executes!r}"
            )
        for name, label, key, value in db.execute(
            """SELECT r.name, COALESCE(a.value, ''), m.key, m.value
               FROM records r JOIN metrics m ON m.record_id=r.id
               LEFT JOIN attributes a ON a.record_id=r.id AND a.key=
               CASE r.name WHEN 'vm.function' THEN 'function.name'
                           WHEN 'vm.native' THEN 'native.name'
                           WHEN 'vm.opcode' THEN 'opcode' END
               WHERE r.name IN ('execute', 'vm.function', 'vm.native', 'vm.opcode')"""
        ):
            bucket: dict[str, int] = groups.setdefault(name, {}).setdefault(label, {})
            bucket[key] = bucket.get(key, 0) + int(value)
    if not groups.get("vm.opcode"):
        raise RuntimeError(f"Missing VM counters in {database}")
    return {"database": str(database), "execute_ns": executes[0][0], "metrics": groups}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compiler", type=Path, required=True)
    parser.add_argument("--candidate-compiler", type=Path)
    parser.add_argument("--before-probe", type=Path, required=True)
    parser.add_argument("--after-probe", type=Path, required=True)
    parser.add_argument("--before-cli", type=Path)
    parser.add_argument("--after-cli", type=Path)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--cpu", type=int)
    parser.add_argument("--pairs", type=int, default=6)
    parser.add_argument(
        "--cli-pairs",
        type=int,
        default=0,
        help="Also time fresh CLI executions with telemetry off",
    )
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--samples", type=int, default=1)
    parser.add_argument("--case", choices=CASES, action="append")
    args: argparse.Namespace = parser.parse_args()
    if args.pairs < 1 or args.samples < 1 or args.warmups < 0 or args.cli_pairs < 0:
        parser.error("expected positive pair/sample counts and nonnegative warmups")
    if args.cli_pairs and (args.before_cli is None or args.after_cli is None):
        parser.error("--cli-pairs requires both CLI executables")

    def optional(path: Path | None) -> Path | None:
        return path.resolve(strict=True) if path is not None else None

    compiler: Path = args.compiler.resolve(strict=True)
    variants: tuple[Variant, Variant] = (
        Variant(
            compiler, args.before_probe.resolve(strict=True), optional(args.before_cli)
        ),
        Variant(
            optional(args.candidate_compiler) or compiler,
            args.after_probe.resolve(strict=True),
            optional(args.after_cli),
        ),
    )
    work: Path = args.work.resolve()
    work.mkdir(parents=True)
    before: Path = work / "before"
    after: Path = work / "after" if args.candidate_compiler is not None else before
    expected: dict[str, str] = build(variants[0].compiler, before)
    if after != before and expected != build(variants[1].compiler, after):
        raise RuntimeError("The fixture oracles differ between compilers")
    dirs: tuple[Path, Path] = before, after
    if args.cpu is not None:
        os.sched_setaffinity(0, {args.cpu})
    cases: tuple[str, ...] = tuple(args.case or CASES)
    if "sha256" in cases:
        for case in ("sha-abc", "sha-boundary"):
            for variant, directory in zip(variants, dirs, strict=True):
                measure(variant, directory / f"{case}.mexe", expected[case], 0, 1)

    report: dict[str, object] = {
        "cpu_affinity": sorted(os.sched_getaffinity(0)),
        "warmups_per_process": args.warmups,
        "samples_per_process": args.samples,
        "variants": [
            {
                "probe": str(v.probe),
                "probe_sha256": digest(v.probe),
                "compiler": str(v.compiler),
                "compiler_sha256": digest(v.compiler),
                "observer": str(v.observer) if v.observer else None,
                "observer_sha256": digest(v.observer) if v.observer else None,
            }
            for v in variants
        ],
    }
    times: dict[str, list[tuple[list[float], list[float]]]] = {
        case: [] for case in cases
    }
    for pair in range(args.pairs):
        order: tuple[int, int] = (0, 1) if pair % 2 == 0 else (1, 0)
        for case in cases if pair % 2 == 0 else reversed(cases):
            samples: dict[int, list[float]] = {}
            for i in order:
                samples[i] = measure(
                    variants[i],
                    dirs[i] / f"{case}.mexe",
                    expected[case],
                    args.warmups,
                    args.samples,
                )
            times[case].append((samples[0], samples[1]))

    cli_times: dict[str, list[tuple[float, float]]] = {case: [] for case in cases}
    for pair in range(args.cli_pairs):
        order = (0, 1) if pair % 2 == 0 else (1, 0)
        for case in cases if pair % 2 == 0 else reversed(cases):
            wall: dict[int, float] = {}
            for i in order:
                observer = variants[i].observer
                assert observer is not None
                start: int = time.perf_counter_ns()
                invoke(
                    [
                        observer,
                        "--no-config",
                        "--no-telemetry",
                        "run",
                        dirs[i] / f"{case}.mexe",
                    ],
                    ROOT,
                )
                wall[i] = (time.perf_counter_ns() - start) / 1e6
            cli_times[case].append((wall[0], wall[1]))

    results: dict[str, object] = {}
    for case, pairs in times.items():
        images: list[Path] = [directory / f"{case}.mexe" for directory in dirs]
        medians: list[float] = [
            statistics.median(x for p in pairs for x in p[i]) for i in range(2)
        ]
        deltas: list[float] = [
            statistics.median(b) - statistics.median(a) for a, b in pairs
        ]
        results[case] = {
            "expected": expected[case],
            "images": [str(image) for image in images],
            "artifact_sha256": [digest(image) for image in images],
            "pairs_ms": pairs,
            "medians_ms": medians,
            "paired_deltas_ms": deltas,
            "paired_median_ms": statistics.median(deltas),
            "fresh_cli_pairs_ms": cli_times[case],
            "profiles": [
                profile(v.observer, images[i], work / f"{case}-{i}.sqlite")
                if v.observer
                else None
                for i, v in enumerate(variants)
            ],
        }
        print(f"{case}: {medians[0]:.3f} -> {medians[1]:.3f} ms", flush=True)
    report["cases"] = results
    (work / "result.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
