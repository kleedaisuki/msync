#!/usr/bin/env python3
"""Orchestrate checked latency and concurrent throughput journeys."""

from __future__ import annotations

import argparse
import hashlib
import json
import logging
import os
import platform
import subprocess
import sys
import uuid
from collections.abc import Sequence
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from Corpus.catalog import CORE_CASES, DIAGNOSTIC_CASES
from journeys import Image, freeze

ROOT: Path = Path(__file__).resolve().parents[2]
HERE: Path = Path(__file__).resolve().parent
LOGGER: logging.Logger = logging.getLogger(__name__)
GROUPS: dict[str, str] = {
    "cli": "Fresh CLI startup, check, ordinary file and many-small-file copy",
    "io": "64 MiB ordinary-file replacement from source and portable object",
    "applications": "Mixed application corpus with independent result/file oracles",
    "interactive": "Persistent REPL and real stdio LSP requests, including edits",
    "editor": "Fresh-process typed editor journey and moderate-document edit bursts",
    "session": "Actual colored PTY: fixed-state and growing-export session age",
    "throughput": "Concurrent application processes, bounded 12 CPU / 24 GiB, detailed SQLite",
}
CLI_CASES: tuple[str, ...] = (
    "startup",
    "check-small",
    "check-batch",
    "copy-file-source",
    "copy-file-object",
    "copy-tree-source",
    "copy-tree-object",
)
INTERACTIVE_CASES: tuple[str, ...] = (
    "repl-copy",
    "repl-scalar",
    "lsp-initialize",
    "lsp-hover",
    "lsp-tokens",
    "lsp-edit",
    "lsp-delta",
)


@dataclass(frozen=True)
class Configuration:
    binary: Path
    baseline: Path | None
    work: Path
    groups: tuple[str, ...]
    samples: int
    warmups: int
    seeds: tuple[int, ...]
    scale: str
    route: str
    workloads: tuple[str, ...]
    submissions: int
    window: int
    cpu: int | None
    profile: str
    jit: str
    concurrency: tuple[int, ...] = ()
    requests: int = 48


@dataclass(frozen=True)
class Command:
    name: str
    arguments: tuple[str, ...]
    receipt: Path


def positive(value: str) -> int:
    result: int = int(value)
    if result < 1:
        raise argparse.ArgumentTypeError("expected a positive integer")
    return result


def nonnegative(value: str) -> int:
    result: int = int(value)
    if result < 0:
        raise argparse.ArgumentTypeError("expected a nonnegative integer")
    return result


def parser() -> argparse.ArgumentParser:
    result: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--binary", type=Path, help="Current Native AOT CLI")
    result.add_argument(
        "--baseline", type=Path, help="Optional previous Native AOT CLI"
    )
    result.add_argument("--group", choices=GROUPS, action="append")
    result.add_argument(
        "--work", type=Path, help="New or empty repository-root .temp directory"
    )
    result.add_argument("--samples", type=positive, default=7)
    result.add_argument("--warmups", type=nonnegative, default=1)
    result.add_argument("--seed", type=int, action="append")
    result.add_argument(
        "--scale", choices=("smoke", "small", "standard", "stress"), default="standard"
    )
    result.add_argument(
        "--route", choices=("source", "object", "bound", "both", "all"), default="both"
    )
    result.add_argument(
        "--workload", choices=(*CORE_CASES, *DIAGNOSTIC_CASES), action="append"
    )
    result.add_argument(
        "--concurrency",
        type=positive,
        action="append",
        help="Throughput worker count; repeat for a scaling sweep (default: 1,4,12,24)",
    )
    result.add_argument(
        "--requests",
        type=positive,
        default=48,
        help="Throughput requests per repetition; at least one per worker",
    )
    result.add_argument("--submissions", type=positive, default=256)
    result.add_argument("--window", type=positive, default=64)
    result.add_argument("--cpu", type=nonnegative)
    result.add_argument(
        "--profile", choices=("off", "profile", "instructions"), default="off"
    )
    result.add_argument(
        "--jit",
        choices=("auto", "off", "linked", "force"),
        default="auto",
        help="Guest tier policy (linked/force are diagnostics); ambient MSYNC_JIT is not inherited",
    )
    result.add_argument(
        "--list",
        action="store_true",
        help="List groups and core/diagnostic workloads without running",
    )
    return result


def configuration(arguments: argparse.Namespace) -> Configuration:
    if arguments.binary is None:
        raise ValueError("--binary is required unless --list is selected")
    work: Path = arguments.work or (
        ROOT
        / ".temp/benchmarks"
        / f"{datetime.now(UTC):%Y%m%dT%H%M%SZ}-{uuid.uuid4().hex[:8]}"
    )
    work = work.resolve()
    temporary: Path = (ROOT / ".temp").resolve()
    if (
        not temporary.is_relative_to(ROOT)
        or not work.is_relative_to(temporary)
        or work == temporary
    ):
        raise ValueError("--work must be a child directory under repository-root .temp")
    if work.exists() and (not work.is_dir() or any(work.iterdir())):
        raise ValueError(
            "--work must be new or empty; previous evidence is never overwritten"
        )
    if arguments.cpu is not None:
        if not hasattr(os, "sched_setaffinity"):
            raise ValueError("--cpu requires a host with sched_setaffinity")
        if arguments.cpu not in os.sched_getaffinity(0):
            raise ValueError(
                "--cpu must belong to the process's available CPU affinity"
            )
    return Configuration(
        arguments.binary.resolve(strict=True),
        arguments.baseline.resolve(strict=True)
        if arguments.baseline is not None
        else None,
        work,
        tuple(dict.fromkeys(arguments.group or GROUPS)),
        arguments.samples,
        arguments.warmups,
        tuple(dict.fromkeys(arguments.seed or (17,))),
        arguments.scale,
        arguments.route,
        tuple(dict.fromkeys(arguments.workload or ())),
        arguments.submissions,
        arguments.window,
        arguments.cpu,
        arguments.profile,
        arguments.jit,
        tuple(dict.fromkeys(arguments.concurrency or ())),
        arguments.requests,
    )


def native_arguments(images: Sequence[Image]) -> tuple[str, ...]:
    current: Image = images[-1]
    return (
        ("--binary", current.executable, "--baseline", images[0].executable)
        if len(images) == 2
        else ("--binary", current.executable)
    )


def selected_cases(names: Sequence[str]) -> tuple[str, ...]:
    return tuple(argument for name in names for argument in ("--case", name))


def command(name: str, script: str, arguments: Sequence[str], receipt: Path) -> Command:
    return Command(name, (sys.executable, str(HERE / script), *arguments), receipt)


def commands(
    group: str, config: Configuration, images: Sequence[Image]
) -> tuple[Command, ...]:
    directory: Path = config.work / group
    receipt: Path = directory / "result.json"
    measurements: tuple[str, ...] = (
        "--samples",
        str(config.samples),
        "--warmups",
        str(config.warmups),
    )
    if group in {"cli", "io"}:
        cases: tuple[str, ...] = (
            CLI_CASES if group == "cli" else ("copy-file-source", "copy-file-object")
        )
        sizes: tuple[str, ...] = (
            () if group == "cli" else ("--file-bytes", str(64 * 1024 * 1024))
        )
        return (
            command(
                group,
                "journeys.py",
                (
                    *native_arguments(images),
                    *measurements,
                    *selected_cases(cases),
                    *sizes,
                    "--output",
                    str(receipt),
                ),
                receipt,
            ),
        )
    if group == "interactive":
        return (
            command(
                group,
                "interactive.py",
                (
                    *native_arguments(images),
                    *measurements,
                    *selected_cases(INTERACTIVE_CASES),
                    "--output",
                    str(receipt),
                ),
                receipt,
            ),
        )
    if group == "editor":
        return (
            command(
                group,
                "editor_latency.py",
                (
                    *native_arguments(images),
                    *selected_cases(("realistic", "flat400")),
                    "--samples",
                    str(config.samples),
                    "--output",
                    str(receipt),
                ),
                receipt,
            ),
        )
    if group == "throughput":
        return (
            command(
                group,
                "Corpus/throughput.py",
                (
                    *native_arguments(images),
                    *measurements,
                    "--scale",
                    config.scale,
                    "--route",
                    config.route,
                    "--profile",
                    config.profile,
                    "--jit",
                    config.jit,
                    "--requests",
                    str(config.requests),
                    *selected_cases(config.workloads),
                    *(
                        argument
                        for seed in config.seeds
                        for argument in ("--seed", str(seed))
                    ),
                    *(
                        argument
                        for count in config.concurrency
                        for argument in ("--concurrency", str(count))
                    ),
                    "--work",
                    str(directory),
                ),
                receipt,
            ),
        )
    if group == "applications":
        variants: tuple[str, ...] = ("--baseline", images[0].executable)
        if len(images) == 2:
            variants += ("--candidate", images[-1].executable)
        return (
            command(
                group,
                "Corpus/run.py",
                (
                    *variants,
                    "--pairs",
                    str(config.samples),
                    "--warmups",
                    str(config.warmups),
                    "--scale",
                    config.scale,
                    "--route",
                    config.route,
                    "--profile",
                    config.profile,
                    *selected_cases(config.workloads),
                    *(
                        argument
                        for seed in config.seeds
                        for argument in ("--seed", str(seed))
                    ),
                    "--work",
                    str(directory),
                ),
                receipt,
            ),
        )
    if group == "session":
        return tuple(
            command(
                f"{image.name}-{case}",
                "repl_tail.py",
                (
                    "--binary",
                    image.executable,
                    "--mode",
                    "prompt",
                    "--case",
                    case,
                    "--submissions",
                    str(config.submissions),
                    "--window",
                    str(config.window),
                    "--output",
                    str(directory / f"{image.name}-{case}.json"),
                ),
                directory / f"{image.name}-{case}.json",
            )
            for image in images
            for case in ("fixed", "growing")
        )
    raise ValueError(f"Unknown benchmark group: {group}")


def timestamp() -> str:
    return datetime.now(UTC).isoformat()


def harness_manifest() -> dict[str, str]:
    sources: set[Path] = {
        path
        for path in HERE.rglob("*.py")
        if not {"__pycache__", "bin", "obj"}.intersection(path.relative_to(HERE).parts)
    }
    sources.update(
        ROOT / "tests/Msync.EndToEnd" / name
        for name in ("lsp_latency.py", "lsp_response_journey.py", "terminal_journey.py")
    )
    return {
        path.relative_to(ROOT).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(sources)
    }


def checkpoint(path: Path, report: dict[str, Any]) -> None:
    temporary: Path = path.with_suffix(".pending")
    temporary.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def run_command(
    selected: Command, logs: Path, environment: dict[str, str] | None = None
) -> dict[str, Any]:
    logs.mkdir(parents=True, exist_ok=True)
    stdout: Path = logs / f"{selected.name}.stdout.log"
    stderr: Path = logs / f"{selected.name}.stderr.log"
    row: dict[str, Any] = {
        "name": selected.name,
        "status": "running",
        "command": selected.arguments,
        "cwd": str(ROOT),
        "receipt": str(selected.receipt),
        "stdout_log": str(stdout),
        "stderr_log": str(stderr),
        "started_utc": timestamp(),
    }
    try:
        with stdout.open("wb") as output, stderr.open("wb") as errors:
            result: subprocess.CompletedProcess[bytes] = subprocess.run(
                selected.arguments,
                cwd=ROOT,
                stdout=output,
                stderr=errors,
                check=False,
                env=environment,
            )
    except OSError as error:
        row.update(
            status="failed",
            error=str(error),
            exit_code=None,
            finished_utc=timestamp(),
            stdout=stdout.read_text(encoding="utf-8", errors="replace"),
            stderr=stderr.read_text(encoding="utf-8", errors="replace"),
        )
        return row
    row.update(
        status="complete" if result.returncode == 0 else "failed",
        exit_code=result.returncode,
        finished_utc=timestamp(),
    )
    if result.returncode:
        row.update(
            stdout=stdout.read_text(encoding="utf-8", errors="replace"),
            stderr=stderr.read_text(encoding="utf-8", errors="replace"),
        )
    elif not selected.receipt.is_file():
        row.update(
            status="failed",
            error="Child exited successfully without its result receipt",
        )
    else:
        try:
            child: Any = json.loads(selected.receipt.read_text(encoding="utf-8"))
            if not isinstance(child, dict) or child.get("status") in {
                "failed",
                "running",
            }:
                row.update(
                    status="failed",
                    error="Child receipt does not describe a completed run",
                )
        except (OSError, ValueError) as error:
            row.update(
                status="failed", error=f"Child result receipt is unreadable: {error}"
            )
    return row


def run(config: Configuration) -> int:
    config.work.mkdir(parents=True, exist_ok=True)
    with (config.work / ".run-owner").open("x", encoding="utf-8") as owner:
        owner.write(f"pid={os.getpid()}\n")
    receipt: Path = config.work / "result.json"
    settings: dict[str, Any] = asdict(config)
    settings.update(
        binary=str(config.binary),
        baseline=str(config.baseline) if config.baseline else None,
        work=str(config.work),
    )
    report: dict[str, Any] = {
        "schema": 1,
        "status": "running",
        "started_utc": timestamp(),
        "configuration": settings,
        "platform": platform.platform(),
        "python": platform.python_version(),
        "measurement": "Groups execute serially; child receipts define latency or concurrent throughput boundaries. Group elapsed time is not a product benchmark.",
        "comparison": "two frozen images; child receipts define pairing/session order"
        if config.baseline
        else "single current image; no synthetic before/after arm",
        "groups": [],
        "coverage": "complete",
        "images": [],
        "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "harness_sources_sha256": harness_manifest(),
    }
    checkpoint(receipt, report)
    try:
        environment: dict[str, str] = dict(os.environ)
        environment.pop("MSYNC_JIT", None)
        if config.jit != "auto":
            environment["MSYNC_JIT"] = config.jit
        images: list[Image] = []
        if config.baseline is not None:
            images.append(freeze(config.baseline, "baseline", config.work))
        images.append(freeze(config.binary, "current", config.work))
        report["images"] = [asdict(image) for image in images]
        if config.cpu is not None:
            os.sched_setaffinity(0, {config.cpu})
        report["affinity"] = (
            sorted(os.sched_getaffinity(0))
            if hasattr(os, "sched_getaffinity")
            else None
        )
        for group in config.groups:
            row: dict[str, Any] = {"name": group, "status": "running", "commands": []}
            report["groups"].append(row)
            if group == "session" and not sys.platform.startswith("linux"):
                row.update(
                    status="unsupported",
                    reason="The colored prompt harness requires Linux PTY and util-linux setsid; no prompt-readiness result was measured",
                )
                report["coverage"] = "partial"
                print(f"UNSUPPORTED {group}: {row['reason']}", flush=True)
                checkpoint(receipt, report)
                continue
            checkpoint(receipt, report)
            for selected in commands(group, config, images):
                row["active_command"] = {
                    "name": selected.name,
                    "command": selected.arguments,
                    "cwd": str(ROOT),
                    "receipt": str(selected.receipt),
                }
                checkpoint(receipt, report)
                print(f"RUN {group}/{selected.name}", flush=True)
                child: dict[str, Any] = run_command(
                    selected, config.work / "logs", environment
                )
                row["commands"].append(child)
                row.pop("active_command")
                checkpoint(receipt, report)
                if child["status"] == "failed":
                    row["status"] = "failed"
                    raise RuntimeError(
                        f"{group}/{selected.name} failed; see {child['stderr_log']}"
                    )
            row["status"] = "complete"
            checkpoint(receipt, report)
        report["status"] = "complete"
        return 0
    except KeyboardInterrupt:
        report.update(status="failed", error="Interrupted by user")
        return 130
    except Exception as error:
        report.update(status="failed", error=str(error))
        LOGGER.exception("Benchmark run failed")
        return 1
    finally:
        if report["status"] == "failed":
            for group_row in report["groups"]:
                if group_row["status"] == "running":
                    group_row["status"] = "failed"
        report["finished_utc"] = timestamp()
        checkpoint(receipt, report)
        print(receipt, flush=True)


def main(arguments: Sequence[str] | None = None) -> int:
    options: argparse.ArgumentParser = parser()
    values: argparse.Namespace = options.parse_args(arguments)
    if values.list:
        for name, description in GROUPS.items():
            print(f"{name:14s} {description}")
        print("\nDefault application workloads:\n  " + "\n  ".join(CORE_CASES))
        print(
            "\nOptional diagnostic workloads (not a general performance score):\n  "
            + "\n  ".join(DIAGNOSTIC_CASES)
        )
        return 0
    try:
        config: Configuration = configuration(values)
    except (ValueError, OSError) as error:
        options.error(str(error))
    return run(config)


if __name__ == "__main__":
    raise SystemExit(main())
