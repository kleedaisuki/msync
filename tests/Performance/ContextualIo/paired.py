#!/usr/bin/env python3
"""Compare one actual small-file workload on two immutable CLI images."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import statistics
import subprocess
import sys
import tempfile
from collections import Counter
from collections.abc import Sequence
from pathlib import Path
from time import perf_counter_ns
from typing import cast

ROOT = Path(__file__).resolve().parents[3]


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()



def implementation_hashes(binary: Path) -> dict[str, str]:
    names = ("Msync.Core.dll", "Msync.Hir.dll", "Msync.Compiler.dll", "Msync.Optimization.dll", "Msync.Runtime.dll", "msync.dll")
    return {name: digest(binary.parent / name) for name in names if (binary.parent / name).is_file()}


def distribution(samples: Sequence[float]) -> dict[str, float]:
    ordered = sorted(samples)
    position = (len(ordered) - 1) * 0.95
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return {
        "p50_ms": statistics.median(ordered),
        "p95_ms": ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower),
        "min_ms": ordered[0],
        "max_ms": ordered[-1],
    }


def source_text(case: str, count: int) -> str:
    if case == "directory":
        return "./input -> ./output/;\n"
    if case == "selection":
        return "./input/<.+\\.dat> -> ./output/;\n"
    destination = "./output/final.dat" if case == "overwrite" else "./output/{index}.dat"
    return "".join(
        f"./input/{index}.dat -> {destination.format(index=index)};\n"
        for index in range(count)
    )


def payload(generation: int, index: int, size: int) -> bytes:
    prefix = f"generation={generation:08d}; item={index:08d};".encode()
    return prefix + b"x" * max(0, size - len(prefix))



def input_name(case: str, index: int) -> Path:
    return Path(f"group-{index // 32}/{index}.dat") if case == "directory" else Path(f"{index}.dat")


def check_metadata(source: Path, target: Path) -> None:
    original = source.stat()
    copied = target.stat()
    assert copied.st_mtime_ns == original.st_mtime_ns, (str(target), copied.st_mtime_ns, original.st_mtime_ns)
    if os.name != "nt":
        assert copied.st_mode & 0o777 == original.st_mode & 0o777, (str(source), str(target))


def run_image(
    binary: Path,
    work: Path,
    source: Path,
    case: str,
    count: int,
    size: int,
    generation: int,
) -> tuple[float, dict[str, object]]:
    inputs = work / "input"
    output = work / "output"
    for index in range(count):
        (inputs / input_name(case, index)).write_bytes(payload(generation, index, size))
    started = perf_counter_ns()
    completed = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "run", str(source)],
        cwd=work,
        env=os.environ | {"NO_COLOR": "1"},
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=120,
    )
    elapsed = (perf_counter_ns() - started) / 1_000_000
    if completed.returncode:
        raise RuntimeError((str(binary), completed.returncode, completed.stdout, completed.stderr))
    expected = {"final.dat": count - 1} if case == "overwrite" else {
        str(Path("input") / input_name(case, index)) if case == "directory" else f"{index}.dat": index
        for index in range(count)
    }
    actual = sorted(str(path.relative_to(output)) for path in output.rglob("*") if path.is_file())
    assert actual == sorted(expected), (actual, sorted(expected))
    for filename, index in expected.items():
        assert (output / filename).read_bytes() == payload(generation, index, size), filename
        if case == "directory":
            check_metadata(inputs / input_name(case, index), output / filename)
    if case == "directory":
        assert (output / "input" / "empty directory").is_dir()
        for source_directory in [inputs, *(path for path in inputs.rglob("*") if path.is_dir())]:
            check_metadata(source_directory, output / "input" / source_directory.relative_to(inputs))
    return elapsed, {"generation": generation, "verified_files": len(expected),
                     "directory_metadata_and_empty_directory": case == "directory",
                     "stdout": completed.stdout, "stderr": completed.stderr}



def inspect_plan(binary: Path, work: Path, source: Path, name: str) -> dict[str, object]:
    directory = work / "inspection"
    directory.mkdir(exist_ok=True)
    obj = directory / f"{name}.mso"
    executable = directory / f"{name}.mexe"
    document: dict[str, object] = {}
    for arguments in (["compile", str(source), "-O2", "-o", str(obj)],
                      ["bind-ir", str(obj), "-o", str(executable)],
                      ["disassemble", str(executable), "--json"]):
        result = subprocess.run([str(binary), "--no-config", "--no-telemetry", *arguments], cwd=work,
                                capture_output=True, text=True, encoding="utf-8", check=False, timeout=120)
        if result.returncode:
            raise RuntimeError((arguments, result.returncode, result.stdout, result.stderr))
        if arguments[0] == "disassemble":
            document = cast(dict[str, object], json.loads(result.stdout))
            (directory / f"{name}.json").write_text(result.stdout, encoding="utf-8")
    plans = cast(list[dict[str, object]], document.get("executionPlans", []))
    transfers = [item for plan in plans for item in cast(list[dict[str, object]], plan["transfers"])]
    kinds = Counter(str(item["kind"]) for item in transfers)
    return {"available": "executionPlans" in document, "plan_count": len(plans),
            "transfer_nodes": len(transfers), "kinds": dict(kinds),
            "batches": sum(len(cast(list[object], plan["batches"])) for plan in plans),
            "eliminated_transfers": sum(cast(int, plan["eliminatedTransfers"]) for plan in plans),
            "executable_sha256": digest(executable), "model": str(directory / f"{name}.json")}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--case", choices=("independent", "overwrite", "selection", "directory"), required=True)
    parser.add_argument("--count", type=int, required=True)
    parser.add_argument("--bytes", type=int, default=4096)
    parser.add_argument("--pairs", type=int, default=7)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--inspect-plan", action="store_true", help="After timing, record actual prepared graph node/kind/batch counts")
    args = parser.parse_args()
    case: str = args.case
    count: int = args.count
    size: int = args.bytes
    pair_count: int = args.pairs
    if count < 1 or pair_count < 1:
        parser.error("--count and --pairs must describe at least one operation/sample")
    scratch = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix=f"contextual-io-perf-{case}-", dir=scratch))
    (work / "input").mkdir()
    (work / "output").mkdir()
    for index in range(count):
        path = work / "input" / input_name(case, index)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.touch()
        if case == "directory" and os.name != "nt":
            path.chmod(0o750 if index % 16 == 0 else 0o640)
    if case == "directory":
        empty = work / "input" / "empty directory"
        empty.mkdir()
        if os.name != "nt":
            empty.chmod(0o750)
        os.utime(empty, ns=(1_700_000_000_123_456_700, 1_700_000_000_123_456_700))
    source = work / "main.msync"
    source.write_text(source_text(case, count), encoding="utf-8", newline="\n")
    images: dict[str, Path] = {"baseline": args.baseline.resolve(), "candidate": args.candidate.resolve()}
    image_records = {name: {"path": str(path), "sha256": digest(path), "implementation_sha256": implementation_hashes(path)}
                     for name, path in images.items()}
    generation = 0
    for binary in images.values():
        generation += 1
        run_image(binary, work, source, case, count, size, generation)
    times: dict[str, list[float]] = {name: [] for name in images}
    pairs: list[dict[str, object]] = []
    deltas: list[float] = []
    for index in range(pair_count):
        order = ("baseline", "candidate") if index % 2 == 0 else ("candidate", "baseline")
        values: dict[str, float] = {}
        checks: dict[str, dict[str, object]] = {}
        for name in order:
            generation += 1
            elapsed, verification = run_image(images[name], work, source, case, count, size, generation)
            values[name] = elapsed
            checks[name] = verification
            times[name].append(elapsed)
        delta = values["candidate"] - values["baseline"]
        deltas.append(delta)
        pairs.append({"order": order, "milliseconds": values, "delta_ms": delta, "verification": checks})
    report: dict[str, object] = {
        "scope": "Actual CLI source run, including frontend/backend, graph preparation, I/O and process startup",
        "case": case,
        "input_files": count,
        "payload_bytes": len(payload(0, 0, size)),
        "images": image_records,
        "source_sha256": digest(source),
        "source": str(source),
        "work": str(work),
        "command": ["BINARY", "--no-config", "--no-telemetry", "run", str(source)],
        "fixture_policy": "One shared directory, output overwrite after one untimed run per image; new fixed-length source generations before every run, exact output bytes verified after timing",
        "environment_overrides": {"NO_COLOR": "1"},
        "platform": platform.platform(),
        "python_executable": sys.executable,
        "python_version": sys.version,
        "pairs": pairs,
        "milliseconds": {name: distribution(values) for name, values in times.items()},
        "paired_delta": distribution(deltas),
    }
    if args.inspect_plan:
        report["plan_inspection"] = {name: inspect_plan(binary, work, source, name) for name, binary in images.items()}
    output: Path = args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"milliseconds": report["milliseconds"], "paired_delta": report["paired_delta"]}, indent=2))


if __name__ == "__main__":
    main()
