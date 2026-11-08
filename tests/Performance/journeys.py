#!/usr/bin/env python3
"""Compare optimized 0.7 and Native AOT 0.8 through equivalent CLI journeys."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import statistics
import subprocess
import time
from collections.abc import Callable, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CASES = (
    "startup",
    "check-small",
    "check-batch",
    "copy-file-source",
    "copy-file-object",
    "copy-tree-source",
    "copy-tree-object",
)
VALUE_CASES = {
    "check-values": ("check", "msync"),
    "values-source": ("run", "msync"),
    "values-object": ("run", "object"),
    "values-bound": ("run-ir", "bound"),
}


@dataclass(frozen=True)
class Image:
    name: str
    executable: str
    version: str
    sha256: str
    bytes: int
    payload_sha256: dict[str, str]


@dataclass(frozen=True)
class Measurement:
    image: str
    case: str
    milliseconds: list[float]
    median_ms: float
    p95_ms: float
    minimum_ms: float
    maximum_ms: float


class InvocationFailure(RuntimeError):
    def __init__(
        self,
        image: Image,
        args: Sequence[str],
        cwd: Path,
        code: int,
        stderr: str,
        elapsed: float,
    ) -> None:
        message = f"{image.name} {' '.join(args)} exited {code}:\n{stderr}"
        self.details: dict[str, object] = {
            "message": message,
            "command": [image.executable, *args],
            "working_directory": str(cwd),
            "exit_code": code,
            "stderr": stderr,
            "elapsed_ms": elapsed,
        }
        super().__init__(message)


class SamplingFailure(RuntimeError):
    def __init__(self, details: dict[str, object]) -> None:
        self.details = details
        super().__init__(str(details["message"]))


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def percentile(values: Sequence[float], quantile: float) -> float:
    ordered = sorted(values)
    position = (len(ordered) - 1) * quantile
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def invoke(image: Image, args: Sequence[str], cwd: Path) -> float:
    started = time.perf_counter_ns()
    result = subprocess.run(
        [image.executable, *args],
        cwd=cwd,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        check=False,
        text=True,
        encoding="utf-8",
    )
    elapsed = (time.perf_counter_ns() - started) / 1_000_000
    if result.returncode != 0:
        raise InvocationFailure(
            image, args, cwd, result.returncode, result.stderr, elapsed
        )
    return elapsed


def freeze(path: Path, name: str, root: Path) -> Image:
    destination = root / "images" / name / path.name
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(path, destination)
    for dependency in path.parent.iterdir():
        if dependency.is_file() and (
            dependency.suffix.lower() in {".so", ".dll", ".dylib"}
            or ".so." in dependency.name
        ):
            shutil.copy2(dependency, destination.parent / dependency.name)
    version = subprocess.check_output(
        [str(destination), "--version"], text=True, encoding="utf-8"
    ).strip()
    expected = "msync 0.7.3" if name == "rust" else "msync 0.8.0"
    if version != expected:
        raise ValueError(f"Expected {expected}, found {version} at {path}")
    return Image(
        name,
        str(destination),
        version,
        digest(destination),
        destination.stat().st_size,
        {
            item.name: digest(item)
            for item in sorted(destination.parent.iterdir())
            if item.is_file()
        },
    )


def source(image: Image, text: str) -> str:
    suffix = "" if image.name == "rust" else ";"
    return "\n".join(line + suffix for line in text.splitlines()) + "\n"


def values_source(image: Image, statements: int) -> str:
    if image.name == "rust":
        function = "symbol route(value):\n    return value\n\n"
        bindings = "".join(
            f'value_{index} := route("payload_{index}")\n'
            for index in range(statements)
        )
        return function + bindings + f"value_{statements - 1} -> values-result.txt\n"
    function = "fn route(value: String) { value };\n"
    bindings = "".join(
        f'let value_{index} = route("payload_{index}");\n'
        for index in range(statements)
    )
    return function + bindings + f"value_{statements - 1} -> values-result.txt;\n"


def prepare(
    image: Image,
    root: Path,
    file_count: int,
    file_bytes: int,
    ordinary_bytes: int | None = None,
    statements: int = 128,
    include_values: bool = False,
) -> Path:
    workspace = root / image.name
    workspace.mkdir(parents=True, exist_ok=True)
    payload = bytes(range(256)) * (file_bytes // 256) + bytes(range(file_bytes % 256))
    ordinary = file_bytes if ordinary_bytes is None else ordinary_bytes
    (workspace / "input.bin").write_bytes(
        bytes(range(256)) * (ordinary // 256) + bytes(range(ordinary % 256))
    )
    tree = workspace / "input-tree"
    tree.mkdir(exist_ok=True)
    for index in range(file_count):
        item = tree / f"{index // 32:03d}" / f"{index:06d}.bin"
        item.parent.mkdir(exist_ok=True)
        item.write_bytes(payload)
        os.utime(item, ns=(1_700_000_000_000_000_000,) * 2)
        item.chmod(0o750 if index == 0 else 0o640)
    scripts = {
        "small": "input.bin -> output.bin",
        "tree": "input-tree -> output-tree/",
        "batch": "\n".join(
            f"source-{i}.bin -> destination-{i}.bin" for i in range(statements)
        ),
    }
    extension = "mobj" if image.name == "rust" else "mso"
    for name, text in scripts.items():
        script = workspace / f"{name}.msync"
        script.write_text(source(image, text), encoding="utf-8")
        invoke(
            image,
            [
                "--no-config",
                "compile",
                str(script),
                "-o",
                str(workspace / f"{name}.{extension}"),
            ],
            workspace,
        )
    if include_values:
        script = workspace / "values.msync"
        script.write_text(values_source(image, statements), encoding="utf-8")
        invoke(
            image,
            [
                "--no-config",
                "compile",
                str(script),
                "-o",
                str(workspace / f"values.{extension}"),
            ],
            workspace,
        )
        binding = (
            [
                "emit-ir",
                str(script),
                "--ir-stage",
                "contextual",
                "-o",
                str(workspace / "values.json"),
            ]
            if image.name == "rust"
            else [
                "bind-ir",
                str(workspace / "values.mso"),
                "-o",
                str(workspace / "values.mexe"),
            ]
        )
        invoke(image, ["--no-config", *binding], workspace)
    return workspace


def arguments(image: Image, case: str, workspace: Path) -> list[str]:
    if case == "startup":
        return ["--version"]
    if case in VALUE_CASES:
        command, kind = VALUE_CASES[case]
        extensions = {
            "msync": "msync",
            "object": "mobj" if image.name == "rust" else "mso",
            "bound": "json" if image.name == "rust" else "mexe",
        }
        extension = extensions[kind]
        return ["--no-config", command, str(workspace / f"values.{extension}")]
    if case.startswith("check-"):
        name = "small" if case == "check-small" else "batch"
        return ["--no-config", "check", str(workspace / f"{name}.msync")]
    name = "tree" if case.startswith("copy-tree") else "small"
    extension = (
        "msync"
        if case.endswith("source")
        else "mobj"
        if image.name == "rust"
        else "mso"
    )
    return ["--no-config", "run", str(workspace / f"{name}.{extension}")]


def verify(case: str, workspace: Path, file_count: int, statements: int = 128) -> None:
    if case in VALUE_CASES and case != "check-values":
        assert (
            workspace / "values-result.txt"
        ).read_bytes() == f"payload_{statements - 1}".encode(), (
            "Typed function/binding result differs"
        )
        return
    if not case.startswith("copy-"):
        return
    if case.startswith("copy-file"):
        if (workspace / "input.bin").read_bytes() != (
            workspace / "output.bin"
        ).read_bytes():
            raise AssertionError("Ordinary file bytes differ")
        return
    source_tree = workspace / "input-tree"
    target_tree = workspace / "output-tree" / "input-tree"
    actual = sorted(target_tree.rglob("*.bin"))
    if len(actual) != file_count:
        raise AssertionError(f"Expected {file_count} copied files, found {len(actual)}")
    for target in actual:
        original = source_tree / target.relative_to(target_tree)
        if original.read_bytes() != target.read_bytes():
            raise AssertionError(f"Copied bytes differ: {target}")
        if os.name != "nt" and original.stat().st_mode != target.stat().st_mode:
            raise AssertionError(f"Copied mode differs: {target}")
        if original.stat().st_mtime_ns != target.stat().st_mtime_ns:
            raise AssertionError(f"Copied modification time differs: {target}")


def measure(
    images: Sequence[Image],
    workspaces: dict[str, Path],
    cases: Sequence[str],
    warmups: int,
    samples: int,
    file_count: int,
    checkpoint: Callable[[Sequence[Measurement], dict[str, object] | None], None]
    | None = None,
    statements: int = 128,
) -> list[Measurement]:
    measurements: list[Measurement] = []
    for case in cases:
        try:
            values = sample_case(
                images, workspaces, case, warmups, samples, file_count, statements
            )
        except SamplingFailure as error:
            if checkpoint is not None:
                checkpoint(measurements, error.details)
            raise
        for image in images:
            timings = values[image.name]
            measurements.append(
                Measurement(
                    image.name,
                    case,
                    timings,
                    statistics.median(timings),
                    percentile(timings, 0.95),
                    min(timings),
                    max(timings),
                )
            )
            print(
                f"{case:20s} {image.name:6s} p50={statistics.median(timings):9.3f} ms p95={percentile(timings, 0.95):9.3f} ms range={min(timings):.3f}–{max(timings):.3f}",
                flush=True,
            )
        if checkpoint is not None:
            checkpoint(measurements, None)
    return measurements


def sample_case(
    images: Sequence[Image],
    workspaces: dict[str, Path],
    case: str,
    warmups: int,
    samples: int,
    file_count: int,
    statements: int,
) -> dict[str, list[float]]:
    values: dict[str, list[float]] = {image.name: [] for image in images}
    for iteration in range(warmups + samples):
        order = list(images) if iteration % 2 == 0 else list(reversed(images))
        for image in order:
            workspace = workspaces[image.name]
            try:
                elapsed = invoke(image, arguments(image, case, workspace), workspace)
                verify(case, workspace, file_count, statements)
            except (InvocationFailure, AssertionError, OSError) as error:
                details: dict[str, object] = (
                    error.details.copy()
                    if isinstance(error, InvocationFailure)
                    else {"message": str(error), "error_type": type(error).__name__}
                )
                details.update(
                    {
                        "case": case,
                        "image": image.name,
                        "iteration": iteration,
                        "warmup": iteration < warmups,
                        "partial_samples_ms": values,
                    }
                )
                raise SamplingFailure(details) from error
            if iteration >= warmups:
                values[image.name].append(elapsed)
    return values


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    previous = parser.add_mutually_exclusive_group(required=True)
    previous.add_argument("--rust", type=Path, help="Optimized Rust 0.7.3 executable")
    previous.add_argument(
        "--previous-aot", type=Path, help="Previous Native AOT 0.8.0 executable"
    )
    parser.add_argument(
        "--aot", type=Path, required=True, help="Native AOT 0.8.0 executable"
    )
    parser.add_argument("--case", choices=(*CASES, *VALUE_CASES), action="append")
    parser.add_argument("--samples", type=int, default=31)
    parser.add_argument("--warmups", type=int, default=3)
    parser.add_argument("--files", type=int, default=256)
    parser.add_argument("--bytes", type=int, default=4096)
    parser.add_argument(
        "--file-bytes",
        type=int,
        help="Override ordinary-file size independently of tree member size",
    )
    parser.add_argument("--statements", type=int, default=128)
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".temp/performance-v080/comparison.json"
    )
    options = parser.parse_args()
    options.output = options.output.resolve()
    root = options.output.parent / f"fixture-{time.time_ns()}"
    previous_image = (
        freeze(options.rust.resolve(), "rust", root)
        if options.rust
        else freeze(options.previous_aot.resolve(), "before", root)
    )
    images = [previous_image, freeze(options.aot.resolve(), "aot", root)]
    workspaces = {
        image.name: prepare(
            image,
            root,
            options.files,
            options.bytes,
            options.file_bytes,
            options.statements,
            any(case in VALUE_CASES for case in options.case or ()),
        )
        for image in images
    }
    result: dict[str, object] = {
        "environment": {
            "platform": platform.platform(),
            "processor": platform.processor(),
            "machine": platform.machine(),
            "logical_cpus": os.cpu_count(),
            "affinity": sorted(os.sched_getaffinity(0))
            if hasattr(os, "sched_getaffinity")
            else None,
            "python": platform.python_version(),
        },
        "method": {
            "warmups": options.warmups,
            "samples": options.samples,
            "tree_files": options.files,
            "file_bytes": options.bytes,
            "ordinary_file_bytes": options.file_bytes
            if options.file_bytes is not None
            else options.bytes,
            "batch_statements": options.statements,
            "percentiles": "p50 median; p95 linearly interpolated empirical quantile",
            "cache": "warm, no page-cache eviction",
            "order": "alternating AB/BA per sample",
            "verification": "outside timed interval; exact file bytes, tree count, native modes and ns mtime",
        },
        "images": [asdict(image) for image in images],
        "artifacts": [
            {
                "image": image.name,
                "case": name,
                "bytes": (workspaces[image.name] / f"{name}.{extension}")
                .stat()
                .st_size,
            }
            for image in images
            for extension in ("mobj" if image.name == "rust" else "mso",)
            for name in ("small", "tree", "batch")
        ],
        "measurements": [],
    }
    options.output.parent.mkdir(parents=True, exist_ok=True)
    completed = False

    def checkpoint(
        done: Sequence[Measurement], failure: dict[str, object] | None
    ) -> None:
        result["measurements"] = [asdict(measurement) for measurement in done]
        result["status"] = (
            "failed" if failure is not None else "complete" if completed else "running"
        )
        result["failure"] = failure
        options.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")

    checkpoint([], None)
    measurements = measure(
        images,
        workspaces,
        options.case or CASES,
        options.warmups,
        options.samples,
        options.files,
        checkpoint,
        options.statements,
    )
    for image in images:
        if digest(Path(image.executable)) != image.sha256:
            raise RuntimeError(
                f"Frozen executable changed during measurement: {image.executable}"
            )
        for filename, expected_digest in image.payload_sha256.items():
            if digest(Path(image.executable).parent / filename) != expected_digest:
                raise RuntimeError(
                    f"Frozen payload changed during measurement: {filename}"
                )
    completed = True
    checkpoint(measurements, None)
    print(options.output)


if __name__ == "__main__":
    main()
