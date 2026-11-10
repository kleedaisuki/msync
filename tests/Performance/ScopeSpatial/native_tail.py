from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import platform
import shutil
import statistics
import subprocess
import sys
from collections.abc import Sequence
from datetime import UTC, datetime
from pathlib import Path
from time import perf_counter_ns
from typing import Any

ROOT = Path(__file__).resolve().parents[3]
DEFAULT_BASELINE = ROOT / (
    "artifacts/candidates/contextual-io/746f6b9/native-linux/"
    "msync-0.8.1-linux-x86_64/bin/msync"
)
DEFAULT_CANDIDATE = ROOT / (
    "artifacts/candidates/spatial-layout/a0cebdf/native-linux/"
    "msync-0.8.1-linux-x86_64/bin/msync"
)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def utc() -> str:
    return datetime.now(UTC).isoformat()


def host_state() -> dict[str, Any]:
    state: dict[str, Any] = {"utc": utc()}
    state["load_average"] = list(os.getloadavg()) if hasattr(os, "getloadavg") else None
    for name in ("stat", "meminfo", "pressure/cpu", "pressure/memory"):
        path = Path("/proc") / name
        state[f"proc_{name}"] = path.read_text() if path.exists() else None
    return state


def child_cpu() -> dict[str, float] | None:
    if sys.platform == "win32":
        return None
    import resource

    usage = resource.getrusage(resource.RUSAGE_CHILDREN)
    return {"user_seconds": usage.ru_utime, "system_seconds": usage.ru_stime}


def invoke(command: list[str], work: Path) -> dict[str, Any]:
    cpu_before = child_cpu()
    timestamp = utc()
    started = perf_counter_ns()
    result = subprocess.run(
        command,
        cwd=work,
        env=os.environ | {"NO_COLOR": "1"},
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=120,
    )
    elapsed = (perf_counter_ns() - started) / 1_000_000
    cpu_after = child_cpu()
    if result.returncode:
        raise RuntimeError((command, result.returncode, result.stdout, result.stderr))
    return {
        "utc": timestamp,
        "milliseconds": elapsed,
        "child_cpu": (
            {name: cpu_after[name] - value for name, value in cpu_before.items()}
            if cpu_before is not None and cpu_after is not None
            else None
        ),
        "stdout": result.stdout,
        "stderr": result.stderr,
    }


def program(case: str) -> str:
    if case != "references":
        return '"scalar-control" -> ./result.txt;\n'
    return """let seed = $std::io::read_text(./seed.txt) as Number;
fn build(values: Range) {
    let records = $std::storage::Buffer::new([]);
    for value in values {
        $std::storage::Buffer::push(records,
            scope { label: "kept", path: ./fixed, flag: true });
    };
    $std::storage::Buffer::snapshot(records)
};
let values = $std::ranges::materialize($std::ranges::iota(seed, seed + 512));
let rows = build(values);
let total = 0;
for row in rows {
    if row::label == "kept" and row::flag { total = total + 1; };
};
(total as String) -> ./result.txt;
"""


def prepare(
    binaries: dict[str, Path], work: Path, cases: Sequence[str]
) -> dict[str, Any]:
    fixtures: dict[str, Any] = {}
    for case in cases:
        directory = work / case
        directory.mkdir(parents=True, exist_ok=True)
        source = directory / "program.msync"
        source.write_text(program(case), encoding="utf-8", newline="\n")
        (directory / "seed.txt").write_text("11", encoding="utf-8")
        commands: dict[str, list[str]] = {}
        artifacts: dict[str, Any] = {}
        for label, binary in binaries.items():
            prefix = [str(binary), "--no-config", "--no-telemetry"]
            if case == "version":
                command = [*prefix, "--version"]
            elif case == "mexe":
                portable = directory / f"{label}.mso"
                executable = directory / f"{label}.mexe"
                invoke(
                    [*prefix, "compile", str(source), "-O2", "-o", str(portable)],
                    directory,
                )
                invoke(
                    [*prefix, "bind-ir", str(portable), "-o", str(executable)],
                    directory,
                )
                command = [*prefix, "run-ir", str(executable)]
                artifacts[label] = {
                    "mso_sha256": digest(portable),
                    "mexe_sha256": digest(executable),
                }
            else:
                command = [*prefix, "run", str(source), "-O2"]
            commands[label] = command
        fixtures[case] = {
            "directory": str(directory),
            "source": source.read_text(),
            "source_sha256": digest(source),
            "commands": commands,
            "artifacts": artifacts,
        }
    return fixtures


def run_case(
    fixture: dict[str, Any], case: str, label: str, seed: int
) -> dict[str, Any]:
    directory = Path(fixture["directory"])
    (directory / "seed.txt").write_text(str(seed), encoding="utf-8")
    output = directory / "result.txt"
    output.unlink(missing_ok=True)
    result = invoke(fixture["commands"][label], directory)
    if case != "version":
        actual = output.read_bytes()
        expected = b"512" if case == "references" else b"scalar-control"
        if actual != expected:
            raise AssertionError((case, label, expected, actual))
        result["output"] = actual.decode()
    result["seed"] = seed
    return result


def binomial_rank(n: int, probability: float, mass: float) -> int:
    cumulative = 0.0
    for k in range(n + 1):
        log_mass = (
            math.lgamma(n + 1)
            - math.lgamma(k + 1)
            - math.lgamma(n - k + 1)
            + k * math.log(probability)
            + (n - k) * math.log1p(-probability)
        )
        cumulative += math.exp(log_mass)
        if cumulative >= mass:
            return k
    return n


def describe(values: Sequence[float]) -> dict[str, Any]:
    ordered = sorted(values)
    n = len(ordered)
    result: dict[str, Any] = {
        "count": n,
        "median_ms": statistics.median(ordered),
        "min_ms": ordered[0],
        "max_ms": ordered[-1],
    }
    for probability in (0.95, 0.99):
        rank = math.ceil(probability * n)
        lower = binomial_rank(n, probability, 0.025)
        upper = binomial_rank(n, probability, 0.975) + 1
        result[f"p{round(probability * 100)}"] = {
            "ms": ordered[rank - 1],
            "rank": rank,
            "at_or_above_rank_count": n - rank + 1,
            "iid_95pct_population_quantile_rank_interval": [lower, upper],
            "iid_95pct_population_quantile_ms_interval": [
                ordered[lower - 1] if lower >= 1 else None,
                ordered[upper - 1] if upper <= n else None,
            ],
        }
    return result


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Blocked fresh-process Native tail probe"
    )
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--candidate", type=Path, default=DEFAULT_CANDIDATE)
    parser.add_argument("--work", type=Path, default=ROOT / ".temp/native-tail")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pairs", type=int, default=1000)
    parser.add_argument("--reference-pairs", type=int, default=200)
    parser.add_argument("--blocks", type=int, default=20)
    parser.add_argument("--calibrate-only", action="store_true")
    args = parser.parse_args()
    binaries = {
        "baseline": args.baseline.resolve(),
        "candidate": args.candidate.resolve(),
    }
    work = args.work.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    counts = dict.fromkeys(("version", "source", "mexe"), args.pairs)
    counts["references"] = args.reference_pairs
    fixtures = prepare(binaries, work, list(counts))
    report: dict[str, Any] = {
        "started_utc": utc(),
        "scope": "fresh child processes, warm OS caches; no cache flush",
        "images": {
            name: {"path": str(path), "sha256": digest(path)}
            for name, path in binaries.items()
        },
        "harness_sha256": digest(Path(__file__)),
        "python_executable": sys.executable,
        "python_version": sys.version,
        "platform": platform.platform(),
        "environment_overrides": {"NO_COLOR": "1"},
        "cpu_measurement": "RUSAGE_CHILDREN user/system delta"
        if os.name != "nt"
        else None,
        "counts": counts,
        "fixtures": fixtures,
        "initial_host": host_state(),
        "calibration": {},
        "blocks": [],
    }
    for case, fixture in fixtures.items():
        report["calibration"][case] = {
            label: run_case(fixture, case, label, 7) for label in binaries
        }
    if args.calibrate_only:
        (output / "calibration.json").write_text(json.dumps(report, indent=2) + "\n")
        print("Functional calibration passed for both images and all four phases.")
        return
    noop = shutil.which("true")
    report["host_noop"] = (
        {"command": noop, "samples": [invoke([noop], work) for _ in range(200)]}
        if noop is not None
        else {"command": None, "reason": "No host true executable available"}
    )
    raw_path = output / "raw.jsonl"
    with raw_path.open("x", encoding="utf-8") as raw:
        for block in range(args.blocks):
            block_record: dict[str, Any] = {
                "block": block,
                "before": host_state(),
                "cases": {},
            }
            for case, count in counts.items():
                start = count * block // args.blocks
                end = count * (block + 1) // args.blocks
                pairs: list[dict[str, Any]] = []
                for pair in range(start, end):
                    order = (
                        ("baseline", "candidate")
                        if pair % 2 == 0
                        else ("candidate", "baseline")
                    )
                    record = {
                        "block": block,
                        "case": case,
                        "pair": pair,
                        "order": order,
                        "runs": {
                            label: run_case(fixtures[case], case, label, 11 + pair)
                            for label in order
                        },
                    }
                    raw.write(json.dumps(record) + "\n")
                    pairs.append(record)
                block_record["cases"][case] = pairs
            raw.flush()
            block_record["after"] = host_state()
            report["blocks"].append(block_record)
            (output / "progress.json").write_text(json.dumps(report, indent=2) + "\n")
            print(f"Completed block {block + 1}/{args.blocks}", flush=True)
    report["summary"] = {}
    for case in counts:
        runs = [
            pair["runs"] for block in report["blocks"] for pair in block["cases"][case]
        ]
        report["summary"][case] = {
            label: describe([pair[label]["milliseconds"] for pair in runs])
            for label in binaries
        }
        deltas = [
            pair["candidate"]["milliseconds"] - pair["baseline"]["milliseconds"]
            for pair in runs
        ]
        report["summary"][case]["paired_delta"] = describe(deltas)
        report["summary"][case]["candidate_faster_pairs"] = sum(x < 0 for x in deltas)
    report["completed_utc"] = utc()
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report["summary"], indent=2), flush=True)


if __name__ == "__main__":
    main()
