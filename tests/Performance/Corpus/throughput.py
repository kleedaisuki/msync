#!/usr/bin/env python3
"""Closed-loop concurrent application journeys with a 12 CPU / 24 GiB scope."""

from __future__ import annotations

import argparse
import json
import os
import platform
import resource
import shutil
import subprocess
import sys
import threading
from concurrent.futures import ThreadPoolExecutor
from dataclasses import asdict, dataclass
from itertools import product
from pathlib import Path
from time import perf_counter_ns
from typing import Any

import run as corpus
from model import Case, Fixture
from throughput_store import Store

CPU_LIMIT: int = 12
MEMORY_LIMIT: int = 24 * 1024**3
DEFAULT_CASES: tuple[str, ...] = (
    "numeric-record-stream",
    "bounded-sensor-pipeline",
    "local-manifest-copy",
)


@dataclass(frozen=True)
class Lane:
    number: int
    directory: Path
    fixture: Fixture


def bounded_scope() -> dict[str, Any] | int:
    if not sys.platform.startswith("linux") or not hasattr(os, "sched_setaffinity"):
        raise RuntimeError(
            "Throughput resource enforcement requires Linux cgroup v2 and systemd user scopes"
        )
    cpus: list[int] = sorted(os.sched_getaffinity(0))[:CPU_LIMIT]
    os.sched_setaffinity(0, cpus)
    if os.environ.get("MSYNC_BENCHMARK_SCOPE") != "1":
        environment: dict[str, str] = dict(os.environ, MSYNC_BENCHMARK_SCOPE="1")
        return subprocess.run(
            [
                "systemd-run",
                "--user",
                "--scope",
                "--quiet",
                "--collect",
                "-p",
                f"MemoryMax={MEMORY_LIMIT}",
                "-p",
                "MemorySwapMax=0",
                "-p",
                f"CPUQuota={CPU_LIMIT * 100}%",
                sys.executable,
                str(Path(__file__).resolve()),
                *sys.argv[1:],
            ],
            env=environment,
            check=False,
        ).returncode
    group: str = next(
        line[3:]
        for line in Path("/proc/self/cgroup").read_text().splitlines()
        if line.startswith("0::")
    )
    directory: Path = Path("/sys/fs/cgroup") / group.lstrip("/")
    memory: str = (directory / "memory.max").read_text().strip()
    quota, period = (directory / "cpu.max").read_text().split()
    if (
        memory == "max"
        or int(memory) > MEMORY_LIMIT
        or quota == "max"
        or int(quota) > CPU_LIMIT * int(period)
    ):
        raise RuntimeError(
            f"Resource scope was not applied: {directory}, memory.max={memory}, cpu.max={quota} {period}"
        )
    return {
        "cpus": cpus,
        "memory_max_bytes": int(memory),
        "cpu_quota": int(quota),
        "cpu_period": int(period),
        "cgroup": str(directory),
    }


def resource_snapshot(scope: dict[str, Any]) -> dict[str, str]:
    directory: Path = Path(scope["cgroup"])
    return {
        name: (directory / name).read_text().strip()
        for name in ("memory.peak", "memory.events", "cpu.stat")
    }


def request(
    variant: corpus.Variant,
    lane: Lane,
    artifact: str,
    number: int,
    database: Path | None = None,
    level: str = "profile",
) -> dict[str, Any]:
    command: list[str] = [str(variant.binary), "--no-config"]
    command += (
        ["--no-telemetry"]
        if database is None
        else ["--telemetry-db", str(database), "--telemetry-level", level]
    )
    command += ["run", artifact]
    started: int = perf_counter_ns()
    row: dict[str, Any] = {
        "request": number,
        "worker": lane.number,
        "started_ns": started,
        "cwd": str(lane.directory),
        "command": command,
        "invocation_ms": None,
        "reset_ns": None,
        "verify_ns": None,
        "error": None,
        "status": "failed",
    }
    try:
        corpus.reset(lane.directory, lane.fixture)
        row["reset_ns"] = perf_counter_ns() - started
        execution: corpus.Execution = corpus.invoke(
            variant,
            lane.directory,
            ["run", artifact],
            lane.fixture.stdin.encode("utf-8"),
            database,
            level,
        )
        row["invocation_ms"] = execution.elapsed_ms
        verify_started: int = perf_counter_ns()
        corpus.verify(lane.directory, artifact, lane.fixture, execution.stdout)
        row["verify_ns"] = perf_counter_ns() - verify_started
        row["status"] = "complete"
    except Exception as error:  # noqa: BLE001 — persist per-request failures without discarding sibling results
        row["error"] = {"message": str(error), "type": type(error).__name__}
        if isinstance(error, corpus.InvocationFailure):
            row["error"]["invocation"] = error.details
            row["invocation_ms"] = error.details.get("elapsed_ms")
    row["finished_ns"] = perf_counter_ns()
    return row


def batch(
    variant: corpus.Variant, lanes: list[Lane], artifact: str, requests: int
) -> tuple[int, float, float, list[dict[str, Any]]]:
    ready: threading.Barrier = threading.Barrier(len(lanes) + 1)
    release: threading.Event = threading.Event()

    def worker(lane: Lane) -> list[dict[str, Any]]:
        ready.wait()
        release.wait()
        return [
            request(variant, lane, artifact, number)
            for number in range(lane.number, requests, len(lanes))
        ]

    with ThreadPoolExecutor(max_workers=len(lanes)) as executor:
        futures = [executor.submit(worker, lane) for lane in lanes]
        ready.wait()
        before: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
        started: int = perf_counter_ns()
        release.set()
        samples: list[dict[str, Any]] = [
            sample for future in futures for sample in future.result()
        ]
        finished: int = max(sample["finished_ns"] for sample in samples)
        after: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    for sample in samples:
        sample["started_ns"] -= started
        sample["finished_ns"] -= started
    return (
        finished - started,
        after.ru_utime - before.ru_utime,
        after.ru_stime - before.ru_stime,
        sorted(samples, key=lambda sample: sample["request"]),
    )


def checkpoint(path: Path, report: dict[str, Any]) -> None:
    pending: Path = path.with_suffix(".pending")
    pending.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    pending.replace(path)


def measure_level(
    args: argparse.Namespace,
    store: Store,
    report: dict[str, Any],
    variant: corpus.Variant,
    template: Path,
    fixture: Fixture,
    artifact: str,
    concurrency: int,
    directory: Path,
) -> None:
    report["active"]["concurrency"] = concurrency
    checkpoint(directory / "result.json", report)
    lane_root: Path = directory / "lanes"
    lanes: list[Lane] = []
    for number in range(concurrency):
        destination: Path = lane_root / str(number)
        shutil.copytree(template, destination)
        if report["active"]["route"] == "bound":
            # Bound Path values capture the binding directory; they cannot be relocated.
            binding: corpus.Execution = corpus.invoke(
                variant,
                destination,
                ["bind-ir", f"{variant.name}.mso", "-o", artifact],
            )
            report["lane_preparation"].append(
                {
                    **report["active"],
                    "worker": number,
                    "bind_ms": binding.elapsed_ms,
                    "artifact_sha256": corpus.file_digest(destination / artifact),
                }
            )
        lanes.append(Lane(number, destination, fixture))
    for _ in range(args.warmups):
        _, _, _, warm = batch(variant, lanes, artifact, concurrency)
        if any(sample["status"] != "complete" for sample in warm):
            report["warmup_failure"] = warm
            raise RuntimeError("Warmup failed; see warmup_failure in result.json")
    for repetition in range(args.samples):
        elapsed, user, system, samples = batch(
            variant, lanes, artifact, max(args.requests, concurrency)
        )
        succeeded: int = sum(sample["status"] == "complete" for sample in samples)
        row: dict[str, Any] = {
            **report["active"],
            "repetition": repetition,
            "requested": len(samples),
            "succeeded": succeeded,
            "failed": len(samples) - succeeded,
            "elapsed_ns": elapsed,
            "child_user_seconds": user,
            "child_system_seconds": system,
            "status": "complete" if succeeded == len(samples) else "failed",
        }
        good: list[float] = [
            sample["invocation_ms"]
            for sample in samples
            if sample["status"] == "complete"
        ]
        row["latency"] = corpus.summarize(good) if good else None
        for quantile, label in ((0.5, "p50"), (0.95, "p95"), (0.99, "p99")):
            row[f"{label}_ms"] = percentile(good, quantile) if good else None
        row["id"] = store.batch(row, samples)
        row["successful_requests_per_second"] = succeeded * 1e9 / elapsed
        report["batches"].append(row)
        checkpoint(directory / "result.json", report)
        print(
            f"{variant.name}/{report['active']['workload']}/{report['active']['route']} C={concurrency} sample={repetition}: {succeeded}/{len(samples)}, {row['successful_requests_per_second']:.2f} requests/s",
            flush=True,
        )
        if row["failed"]:
            raise RuntimeError(
                f"Batch {row['id']} failed; raw errors retained in benchmark.sqlite samples"
            )
    shutil.rmtree(lane_root)


def percentile(values: list[float], quantile: float) -> float:
    ordered: list[float] = sorted(values)
    position: float = quantile * (len(ordered) - 1)
    lower: int = int(position)
    upper: int = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (position - lower) * (ordered[upper] - ordered[lower])


def record_profile(
    args: argparse.Namespace,
    store: Store,
    report: dict[str, Any],
    variant: corpus.Variant,
    template: Path,
    fixture: Fixture,
    artifact: str,
    case: Case,
    route: str,
    seed: int,
) -> None:
    path: Path = template / f"{route}.sqlite"
    result: dict[str, Any] = request(
        variant, Lane(0, template, fixture), artifact, 0, path, args.profile
    )
    report["profile_attempt"] = result
    if path.exists():
        store.profile(path, variant.name, case.name, route, seed)
    if result["status"] != "complete":
        raise RuntimeError(f"Profile failed: {result['error']}")
    if not path.exists():
        raise RuntimeError(
            f"Product did not create requested telemetry database: {path}"
        )
    report["profiles"].append(
        {
            "image": variant.name,
            "workload": case.name,
            "route": route,
            "seed": seed,
            "database": str(path),
        }
    )
    report.pop("profile_attempt")


def exercise_fixture(
    args: argparse.Namespace,
    store: Store,
    report: dict[str, Any],
    variant: corpus.Variant,
    case: Case,
    seed: int,
    directory: Path,
) -> None:
    numerator, denominator = corpus.SCALES[args.scale]
    size: int = max(1, case.default_size * numerator // denominator)
    template: Path = directory / "fixtures" / case.name / str(seed) / variant.name
    template.mkdir(parents=True)
    fixture: Fixture = case.generate(template, size, seed)
    (template / "workload.msync").write_text(fixture.source, encoding="utf-8")
    report["fixtures"].append(
        {
            "image": variant.name,
            "workload": case.name,
            "seed": seed,
            "size": size,
            "manifest": corpus.manifest(
                template, fixture, fixture.stdin.encode("utf-8")
            ),
        }
    )
    routes: tuple[str, ...] = corpus.ROUTES[args.route]
    if routes != ("source",):
        report["preparation"].append(
            {
                "image": variant.name,
                "workload": case.name,
                "seed": seed,
                "artifacts": corpus.prepare(
                    [variant], template, fixture, False, "bound" in routes
                ),
            }
        )
    for route in routes:
        artifact: str = (
            "workload.msync"
            if route == "source"
            else f"{variant.name}.{corpus.EXTENSION[route]}"
        )
        report["active"] = {
            "image": variant.name,
            "workload": case.name,
            "route": route,
            "seed": seed,
        }
        for concurrency in dict.fromkeys(args.concurrency or [1, 4, 12, 24]):
            measure_level(
                args,
                store,
                report,
                variant,
                template,
                fixture,
                artifact,
                concurrency,
                directory,
            )
    if args.profile != "off":
        for route in routes:
            artifact = (
                "workload.msync"
                if route == "source"
                else f"{variant.name}.{corpus.EXTENSION[route]}"
            )
            record_profile(
                args,
                store,
                report,
                variant,
                template,
                fixture,
                artifact,
                case,
                route,
                seed,
            )


def exercise(
    args: argparse.Namespace,
    store: Store,
    report: dict[str, Any],
    variants: list[corpus.Variant],
    directory: Path,
) -> None:
    selected: list[Case] = [
        case for case in corpus.CASES if case.name in (args.case or DEFAULT_CASES)
    ]
    for case, seed, variant in product(
        selected, dict.fromkeys(args.seed or [17]), variants
    ):
        exercise_fixture(args, store, report, variant, case, seed, directory)


def positive(value: str) -> int:
    result: int = int(value)
    if result < 1:
        raise argparse.ArgumentTypeError("expected a positive integer")
    return result


def main() -> int:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--work", required=True, type=Path)
    parser.add_argument(
        "--case", action="append", choices=[case.name for case in corpus.CASES]
    )
    parser.add_argument("--seed", action="append", type=int)
    parser.add_argument("--scale", choices=corpus.SCALES, default="standard")
    parser.add_argument("--route", choices=corpus.ROUTES, default="bound")
    parser.add_argument("--samples", type=positive, default=3)
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--concurrency", action="append", type=positive)
    parser.add_argument("--requests", type=positive, default=48)
    parser.add_argument(
        "--jit", choices=("auto", "off", "linked", "force"), default="auto"
    )
    parser.add_argument(
        "--profile", choices=("off", "profile", "instructions"), default="off"
    )
    args: argparse.Namespace = parser.parse_args()
    directory: Path = args.work.resolve()
    if args.warmups < 0:
        parser.error("--warmups must be nonnegative")
    if (
        not directory.is_relative_to(corpus.ROOT / ".temp")
        or directory == corpus.ROOT / ".temp"
    ):
        parser.error("--work must be a child of repository-root .temp")
    if directory.exists() and any(directory.iterdir()):
        parser.error("--work must be new or empty")
    scope: dict[str, Any] | int = bounded_scope()
    if isinstance(scope, int):
        return scope
    directory.mkdir(parents=True, exist_ok=True)
    with (directory / ".run-owner").open("x", encoding="utf-8") as owner:
        owner.write(f"pid={os.getpid()}\n")
    report: dict[str, Any] = {
        "schema": 1,
        "status": "running",
        "platform": platform.platform(),
        "resource_scope": scope,
        "configuration": vars(args)
        | {
            "binary": str(args.binary),
            "baseline": str(args.baseline) if args.baseline else None,
            "work": str(directory),
        },
        "measurement": "Closed-loop isolated CLI processes; throughput window includes reset/oracle between requests. Invocation latency excludes reset/oracle. Telemetry profiles are separate serial runs. No open-loop arrival or server-throughput claim.",
        "fixtures": [],
        "images": [],
        "batches": [],
        "profiles": [],
        "preparation": [],
        "lane_preparation": [],
    }
    store: Store = Store(directory / "benchmark.sqlite", report)
    checkpoint(directory / "result.json", report)
    try:
        variants: list[corpus.Variant] = []
        if args.baseline is not None:
            variants.append(
                corpus.Variant("baseline", args.baseline.resolve(strict=True))
            )
        variants.append(corpus.Variant("current", args.binary.resolve(strict=True)))
        variants = [corpus.freeze(variant, directory) for variant in variants]
        report["images"] = [
            {
                **asdict(variant),
                "binary": str(variant.binary),
                "original": str(variant.original),
                "payload_sha256": corpus.payload(variant.binary.parent),
            }
            for variant in variants
        ]
        os.environ.pop("MSYNC_JIT", None)
        if args.jit != "auto":
            os.environ["MSYNC_JIT"] = args.jit
        report["jit"] = args.jit
        exercise(args, store, report, variants, directory)
        report.pop("active", None)
        report["status"] = "complete"
    except BaseException as error:  # noqa: BLE001 — checkpoint the run, including interrupts
        report.update(
            status="failed", error=str(error), error_type=type(error).__name__
        )
        if isinstance(error, corpus.InvocationFailure):
            report["invocation_failure"] = error.details
        print(f"FAILED: {error}", file=sys.stderr, flush=True)
    finally:
        report["resource_usage"] = resource_snapshot(scope)
        store.update(report["status"], report)
        store.close()
        checkpoint(directory / "result.json", report)
        print(directory / "benchmark.sqlite", flush=True)
    return 0 if report["status"] == "complete" else 1


if __name__ == "__main__":
    raise SystemExit(main())
