"""End-to-end corpus runner: independently checked results, joint comparisons and SQLite."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import sqlite3
import statistics
import subprocess
from collections.abc import Sequence
from contextlib import closing
from dataclasses import dataclass
from pathlib import Path
from time import perf_counter_ns

import collections_workloads
import compute
import pipelines
import text_paths
from catalog import CORE_CASES, DIAGNOSTIC_CASES
from model import Case, Fixture

ROOT: Path = Path(__file__).resolve().parents[3]
SCALES: dict[str, tuple[int, int]] = {
    "smoke": (1, 64),
    "small": (1, 4),
    "standard": (1, 1),
    "stress": (4, 1),
}
CASES: tuple[Case, ...] = (
    *compute.cases(),
    *collections_workloads.cases(),
    *pipelines.cases(),
    *text_paths.cases(),
)
REPORT_VERSION: int = 2
ROUTES: dict[str, tuple[str, ...]] = {
    "bound": ("bound",),
    "object": ("object",),
    "source": ("source",),
    "both": ("bound", "source"),
    "all": ("bound", "object", "source"),
}
EXTENSION: dict[str, str] = {"bound": "mexe", "object": "mso", "source": "msync"}


@dataclass(frozen=True)
class Variant:
    name: str
    binary: Path
    original: Path | None = None


def payload(directory: Path) -> dict[str, str]:
    return {
        path.relative_to(directory).as_posix(): file_digest(path)
        for path in sorted(directory.rglob("*"))
        if path.is_file()
    }


def freeze(variant: Variant, directory: Path) -> Variant:
    destination: Path = directory / "images" / variant.name
    destination.mkdir(parents=True)
    manifests: set[str] = {
        f"{variant.binary.stem}.deps.json",
        f"{variant.binary.stem}.runtimeconfig.json",
    }
    files: list[Path] = [
        path
        for path in sorted(variant.binary.parent.iterdir())
        if path.is_file()
        and (
            path == variant.binary
            or path.suffix in {".dll", ".so", ".dylib"}
            or path.name in manifests
            or ".so." in path.name
        )
    ]
    for source in files:
        expected: str = file_digest(source)
        target: Path = destination / source.name
        shutil.copy2(source, target)
        if file_digest(target) != expected or file_digest(source) != expected:
            raise RuntimeError(f"Product payload changed while freezing: {source}")
    runtime: Path = variant.binary.parent / "runtimes"
    if runtime.is_dir():
        expected_runtime: dict[str, str] = payload(runtime)
        shutil.copytree(runtime, destination / "runtimes")
        if (
            payload(destination / "runtimes") != expected_runtime
            or payload(runtime) != expected_runtime
        ):
            raise RuntimeError(f"Runtime payload changed while freezing: {runtime}")
    return Variant(variant.name, destination / variant.binary.name, variant.binary)


@dataclass(frozen=True)
class Execution:
    elapsed_ms: float
    stdout: bytes


class InvocationFailure(RuntimeError):
    def __init__(self, message: str, details: dict[str, object]) -> None:
        super().__init__(message)
        self.details: dict[str, object] = details


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def invoke(
    variant: Variant,
    cwd: Path,
    arguments: Sequence[str],
    stdin: bytes = b"",
    database: Path | None = None,
    level: str = "profile",
) -> Execution:
    telemetry: list[str] = (
        ["--no-telemetry"]
        if database is None
        else [
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            level,
        ]
    )
    command: list[str] = [str(variant.binary), "--no-config", *telemetry, *arguments]
    started: int = perf_counter_ns()
    context: dict[str, object] = {"command": command, "cwd": str(cwd)}
    try:
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            command, cwd=cwd, input=stdin, capture_output=True, check=False, timeout=300
        )
    except subprocess.TimeoutExpired as error:
        context.update(
            timeout_seconds=error.timeout,
            stdout=(error.stdout or b"").decode("utf-8", errors="replace"),
            stderr=(error.stderr or b"").decode("utf-8", errors="replace"),
        )
        raise InvocationFailure(f"{command!r} in {cwd} timed out", context) from error
    except OSError as error:
        context.update(error_type=type(error).__name__, error=str(error))
        raise InvocationFailure(
            f"Cannot launch {command!r} in {cwd}: {error}", context
        ) from error
    elapsed: float = (perf_counter_ns() - started) / 1e6
    if result.returncode != 0:
        context.update(
            exit_code=result.returncode,
            stdout=result.stdout.decode("utf-8", errors="replace"),
            stderr=result.stderr.decode("utf-8", errors="replace"),
            elapsed_ms=elapsed,
        )
        raise InvocationFailure(
            f"{command!r} in {cwd} exited {result.returncode}: "
            f"stdout={result.stdout!r}; stderr={result.stderr!r}",
            context,
        )
    return Execution(elapsed, result.stdout)


def reset(directory: Path, fixture: Fixture) -> None:
    for output in fixture.outputs:
        path: Path = directory / output.path
        if output.initial is None:
            path.unlink(missing_ok=True)
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(output.initial)
    for path_name in fixture.absent:
        (directory / path_name).unlink(missing_ok=True)


def checked(
    variant: Variant,
    directory: Path,
    artifact: str,
    fixture: Fixture,
    stdin: bytes,
    database: Path | None = None,
    level: str = "profile",
) -> float:
    reset(directory, fixture)
    result: Execution = invoke(
        variant, directory, ["run", artifact], stdin, database, level
    )
    verify(directory, artifact, fixture, result.stdout)
    return result.elapsed_ms


def verify(
    directory: Path, artifact: str, fixture: Fixture, stdout_bytes: bytes
) -> None:
    stdout: str = stdout_bytes.decode("utf-8").replace("\r\n", "\n")
    if stdout != fixture.stdout:
        raise RuntimeError(
            f"{directory}/{artifact}: stdout "
            f"expected {fixture.stdout[:600]!r}, observed {stdout[:600]!r}"
        )
    for output in fixture.outputs:
        actual: str = file_digest(directory / output.path)
        if actual != output.sha256:
            raise RuntimeError(f"{directory}: wrong bytes at {output.path!r}: {actual}")
    for path in fixture.absent:
        if (directory / path).exists():
            raise RuntimeError(f"{directory}: unexpectedly produced {path!r}")
    for root in fixture.output_roots:
        expected_paths: set[str] = {
            item.path
            for item in fixture.outputs
            if Path(item.path).is_relative_to(root)
        }
        observed_paths: set[str] = {
            path.relative_to(directory).as_posix()
            for path in (directory / root).rglob("*")
            if path.is_file()
        }
        if observed_paths != expected_paths:
            raise RuntimeError(
                f"{directory}: output tree mismatch under {root!r}: "
                f"missing={sorted(expected_paths - observed_paths)}, "
                f"unexpected={sorted(observed_paths - expected_paths)}"
            )


def profile(path: Path) -> dict[str, object]:
    with closing(sqlite3.connect(path)) as db:
        status: list[tuple[str, str | None]] = db.execute(
            "SELECT status, exporter_error FROM sessions"
        ).fetchall()
        if status != [("complete", None)]:
            raise RuntimeError(f"{path}: exporter did not complete: {status}")
        sums: dict[str, dict[str, int]] = {}
        for group, key, value in db.execute(
            """SELECT r.name, m.key, m.value FROM records r JOIN metrics m ON m.record_id=r.id
               WHERE r.name IN ('execute', 'vm.function', 'vm.opcode', 'vm.native')"""
        ):
            bucket: dict[str, int] = sums.setdefault(group, {})
            bucket[key] = bucket.get(key, 0) + int(value)
        phases: dict[str, int] = dict(
            db.execute(
                """SELECT name,SUM(duration_ns) FROM spans WHERE name IN
               ('run','compile','bind','prepare','artifact.read','runtime.initialize','execute')
               GROUP BY name"""
            )
        )
        top: dict[str, dict[str, int]] = {}
        for name, attribute, metric in (
            ("vm.native", "native.name", "calls"),
            ("vm.opcode", "opcode", "hits"),
        ):
            top[name] = dict(
                db.execute(
                    """SELECT a.value,SUM(CAST(m.value AS INTEGER)) FROM records r
                   JOIN attributes a ON a.record_id=r.id AND a.key=?
                   JOIN metrics m ON m.record_id=r.id AND m.key=?
                   WHERE r.name=? GROUP BY a.value ORDER BY 2 DESC, a.value LIMIT 10""",
                    (attribute, metric, name),
                )
            )
    return {
        "database": str(path),
        "phase_duration_ns": phases,
        "metrics": sums,
        "top_native_calls": top["vm.native"],
        "top_opcodes": top["vm.opcode"],
    }


def manifest(directory: Path, fixture: Fixture, stdin: bytes) -> dict[str, object]:
    initial: set[str] = {output.path for output in fixture.outputs} | set(
        fixture.absent
    )
    files: dict[str, str] = {
        path.relative_to(directory).as_posix(): file_digest(path)
        for path in sorted(directory.rglob("*"))
        if path.is_file() and path.relative_to(directory).as_posix() not in initial
    }
    return {
        "source_sha256": digest(fixture.source.encode("utf-8")),
        "stdin_bytes": len(stdin),
        "stdin_sha256": digest(stdin),
        "stdout_sha256": digest(fixture.stdout.encode("utf-8")),
        "input_files": files,
        "expected_outputs": {output.path: output.sha256 for output in fixture.outputs},
        "expected_absent": fixture.absent,
    }


def prepare(
    variants: Sequence[Variant],
    directory: Path,
    fixture: Fixture,
    shared: bool,
    bind: bool,
) -> list[dict[str, object]]:
    prepared: list[dict[str, object]] = []
    for variant in variants[:1] if shared else variants:
        reset(directory, fixture)
        portable: str = f"{variant.name}.mso"
        executable: str = f"{variant.name}.mexe"
        compile_time: float = invoke(
            variant, directory, ["compile", "-O2", "workload.msync", "-o", portable]
        ).elapsed_ms
        row: dict[str, object] = {
            "producer": variant.name,
            "compile_ms": compile_time,
            "mso_sha256": file_digest(directory / portable),
        }
        if bind:
            row["bind_ms"] = invoke(
                variant, directory, ["bind-ir", portable, "-o", executable]
            ).elapsed_ms
            row["mexe_sha256"] = file_digest(directory / executable)
        prepared.append(row)
    return prepared


def summarize(samples: list[float]) -> dict[str, float | list[float]]:
    ordered: list[float] = sorted(samples)
    position: float = 0.95 * (len(ordered) - 1)
    lo: int = int(position)
    hi: int = min(lo + 1, len(ordered) - 1)
    p95: float = ordered[lo] + (position - lo) * (ordered[hi] - ordered[lo])
    return {"samples_ms": samples, "p50_ms": statistics.median(samples), "p95_ms": p95}


def measure_samples(
    variants: Sequence[Variant],
    directory: Path,
    fixture: Fixture,
    stdin: bytes,
    artifacts: dict[str, str],
    args: argparse.Namespace,
    phase: int,
    result: dict[str, object],
) -> dict[str, list[float]]:
    samples: dict[str, list[float]] = {variant.name: [] for variant in variants}
    timings: dict[str, object] = {
        name: {"samples_ms": values} for name, values in samples.items()
    }
    result["timings"] = timings
    for pair in range(-args.warmups, args.pairs):
        order: Sequence[Variant] = (
            variants if (pair + phase) % 2 == 0 else variants[::-1]
        )
        for variant in order:
            result["active"] = {
                "phase": "warmup" if pair < 0 else "timing",
                "pair": pair,
                "variant": variant.name,
                "artifact": artifacts[variant.name],
                "cwd": str(directory),
            }
            elapsed: float = checked(
                variant, directory, artifacts[variant.name], fixture, stdin
            )
            if pair >= 0:
                samples[variant.name].append(elapsed)
                timings[variant.name] = summarize(samples[variant.name])
    return samples


def measure_profiles(
    variants: Sequence[Variant],
    directory: Path,
    fixture: Fixture,
    stdin: bytes,
    artifacts: dict[str, str],
    level: str,
    result: dict[str, object],
) -> None:
    profiles: dict[str, dict[str, object]] = {}
    result["profiles"] = profiles
    for variant in variants:
        db: Path = (
            directory
            / f"{variant.name}-{Path(artifacts[variant.name]).suffix[1:]}.sqlite"
        )
        result["active"] = {
            "phase": "profile",
            "variant": variant.name,
            "artifact": artifacts[variant.name],
            "cwd": str(directory),
            "database": str(db),
        }
        checked(variant, directory, artifacts[variant.name], fixture, stdin, db, level)
        profiles[variant.name] = profile(db)


def measure(
    variants: Sequence[Variant],
    directory: Path,
    fixture: Fixture,
    stdin: bytes,
    artifacts: dict[str, str],
    args: argparse.Namespace,
    phase: int,
    result: dict[str, object],
) -> None:
    result["status"] = "running"
    try:
        samples: dict[str, list[float]] = measure_samples(
            variants,
            directory,
            fixture,
            stdin,
            artifacts,
            args,
            phase,
            result,
        )
        if "candidate" in samples:
            deltas: list[float] = [
                (right / left - 1.0) * 100.0
                for left, right in zip(
                    samples["baseline"], samples["candidate"], strict=True
                )
            ]
            result.update(
                paired_percent_change=deltas,
                paired_median_percent_change=statistics.median(deltas),
            )
        if args.profile != "off":
            measure_profiles(
                variants, directory, fixture, stdin, artifacts, args.profile, result
            )
    except BaseException as error:
        result.update(status="failed", error=str(error))
        if isinstance(error, InvocationFailure):
            result["invocation_failure"] = error.details
        raise
    result["status"] = "complete"
    result.pop("active", None)


def run(
    args: argparse.Namespace,
    variants: list[Variant],
    selected: tuple[Case, ...],
    scales: list[str],
    seeds: list[int],
    directory: Path,
) -> None:
    metadata: dict[str, object] = {
        "schema": REPORT_VERSION,
        "status": "running",
        "platform": platform.platform(),
        "python": platform.python_version(),
        "affinity": sorted(os.sched_getaffinity(0))
        if hasattr(os, "sched_getaffinity")
        else None,
        "variants": [
            {
                "name": v.name,
                "path": str(v.binary),
                "original_path": str(v.original or v.binary),
                "sha256": file_digest(v.binary),
                "payload_sha256": payload(v.binary.parent),
                "version": invoke(v, directory, ["--version"])
                .stdout.decode("utf-8")
                .strip(),
            }
            for v in variants
        ],
        "configuration": {
            "route": args.route,
            "pairs": args.pairs,
            "warmups": args.warmups,
            "profile": args.profile,
            "shared_artifact": args.same_artifact,
            "scales": scales,
            "seeds": seeds,
            "distribution": getattr(args, "distribution", "reference"),
            "msync_jit": os.environ.get("MSYNC_JIT", "auto"),
        },
    }
    rows: list[dict[str, object]] = []
    metadata["cases"] = rows
    path: Path = directory / "result.json"
    current: str = ""
    frozen_payloads: dict[str, dict[str, str]] = {
        variant.name: payload(variant.binary.parent) for variant in variants
    }
    try:
        distribution: str = getattr(args, "distribution", "reference")
        scenarios = [
            (case, "reference", "", case.generate)
            for case in selected
            if distribution != "contrast"
        ]
        if distribution != "reference":
            scenarios.extend(
                (case, label, label + "/", generate)
                for case in selected
                if case.contrast is not None
                for label, generate in (case.contrast,)
            )
        if not scenarios:
            raise ValueError(
                "The selected workloads do not have a contrast distribution"
            )
        for case, label, prefix, generate in scenarios:
            for scale in scales:
                numerator, denominator = SCALES[scale]
                size: int = max(1, case.default_size * numerator // denominator)
                for phase, seed in enumerate(seeds):
                    current = f"{case.name}/{prefix}{scale}/{seed}"
                    work: Path = directory / current
                    work.mkdir(parents=True)
                    fixture: Fixture = generate(work, size, seed)
                    stdin: bytes = fixture.stdin.encode("utf-8")
                    (work / "stdin.utf8").write_bytes(stdin)
                    (work / "expected.utf8").write_bytes(fixture.stdout.encode("utf-8"))
                    (work / "workload.msync").write_bytes(
                        fixture.source.encode("utf-8")
                    )
                    row: dict[str, object] = {
                        "case": case.name,
                        "family": case.family,
                        "role": "diagnostic"
                        if case.name in DIAGNOSTIC_CASES
                        else "core",
                        "scale": scale,
                        "size": size,
                        "seed": seed,
                        "distribution": label,
                        "input_characteristics": fixture.distribution,
                        "manifest": manifest(work, fixture, stdin),
                        "routes": {},
                    }
                    rows.append(row)
                    routes: dict[str, object] = {}
                    row["routes"] = routes
                    selected_routes: tuple[str, ...] = ROUTES[args.route]
                    if selected_routes != ("source",):
                        row["preparation"] = prepare(
                            variants,
                            work,
                            fixture,
                            args.same_artifact,
                            "bound" in selected_routes,
                        )
                    for route in selected_routes:
                        artifacts: dict[str, str] = {
                            variant.name: "workload.msync"
                            if route == "source"
                            else f"{'baseline' if args.same_artifact else variant.name}.{EXTENSION[route]}"
                            for variant in variants
                        }
                        route_result: dict[str, object] = {}
                        routes[route] = route_result
                        measure(
                            variants,
                            work,
                            fixture,
                            stdin,
                            artifacts,
                            args,
                            phase,
                            route_result,
                        )
                    path.write_text(
                        json.dumps(metadata, indent=2) + "\n", encoding="utf-8"
                    )
                    print(f"PASS {current} (size={size})", flush=True)
        metadata["status"] = "complete"
    except BaseException as error:
        metadata.update(status="failed", failure_case=current, error=str(error))
        if isinstance(error, InvocationFailure):
            metadata["invocation_failure"] = error.details
        raise
    finally:
        changed: list[str] = [
            variant.name
            for variant in variants
            if payload(variant.binary.parent) != frozen_payloads[variant.name]
        ]
        metadata["frozen_payloads_unchanged"] = not changed
        if changed:
            metadata.update(
                status="failed", error=f"Frozen product payload changed: {changed}"
            )
        path.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        if changed:
            raise RuntimeError(metadata["error"])


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--candidate", type=Path)
    parser.add_argument(
        "--case", action="append", choices=[case.name for case in CASES]
    )
    parser.add_argument(
        "--family", action="append", choices=sorted({case.family for case in CASES})
    )
    parser.add_argument("--scale", action="append", choices=SCALES)
    parser.add_argument("--seed", action="append", type=int)
    parser.add_argument(
        "--distribution", choices=("reference", "contrast", "both"), default="reference"
    )
    parser.add_argument("--pairs", type=int, default=5)
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument(
        "--profile", choices=("off", "profile", "instructions"), default="profile"
    )
    parser.add_argument("--route", choices=ROUTES, default="bound")
    parser.add_argument(
        "--same-artifact",
        action="store_true",
        help="Use the baseline-produced portable or bound image for both VMs",
    )
    parser.add_argument("--cpu", type=int)
    parser.add_argument("--work", type=Path)
    parser.add_argument("--list", action="store_true")
    parser.add_argument(
        "--include-diagnostics",
        action="store_true",
        help="Include explicitly retained engineering diagnostics as well as the core corpus",
    )
    args: argparse.Namespace = parser.parse_args()
    selected: tuple[Case, ...] = tuple(
        case
        for case in CASES
        if (not args.case or case.name in args.case)
        and (not args.family or case.family in args.family)
        and (
            args.list
            or args.case
            or args.family
            or args.include_diagnostics
            or case.name in CORE_CASES
        )
    )
    if args.list:
        for case in selected:
            contrast = f" [contrast: {case.contrast[0]}]" if case.contrast else ""
            print(
                f"{case.name:28} {'diagnostic' if case.name in DIAGNOSTIC_CASES else 'core':10} "
                f"{case.family:16} {case.default_size:6}  {case.description}{contrast}"
            )
        return
    if (
        args.baseline is None
        or args.work is None
        or args.pairs < 1
        or args.warmups < 0
        or not selected
    ):
        parser.error(
            "select a case and provide --baseline, --work, positive --pairs and nonnegative --warmups"
        )
    if args.same_artifact and (args.candidate is None or args.route == "source"):
        parser.error("--same-artifact requires a candidate and an artifact route")
    scales: list[str] = list(dict.fromkeys(args.scale or ["standard"]))
    seeds: list[int] = list(dict.fromkeys(args.seed or [17, 503]))
    directory: Path = args.work.resolve()
    if (
        not directory.is_relative_to(ROOT / ".temp")
        or directory.exists()
        and (not directory.is_dir() or any(directory.iterdir()))
    ):
        parser.error(
            "--work must be a new or empty directory under repository-root .temp"
        )
    directory.mkdir(parents=True, exist_ok=True)
    if args.cpu is not None:
        if not hasattr(os, "sched_setaffinity"):
            parser.error("--cpu requires a host with sched_setaffinity")
        os.sched_setaffinity(0, {args.cpu})
    variants: list[Variant] = [Variant("baseline", args.baseline.resolve(strict=True))]
    if args.candidate is not None:
        variants.append(Variant("candidate", args.candidate.resolve(strict=True)))
    variants = [freeze(variant, directory) for variant in variants]
    run(args, variants, selected, scales, seeds, directory)


if __name__ == "__main__":
    main()
