"""Build and measure a checked Linux x86-64 collection/SHA-256 matrix."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import platform
import random
import statistics
import struct
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
LANGUAGES = ("C++", "C#", "Java", "Python", "msync")
CASES = ("tree", "heap", "hash", "sha256")
SIZE = 8192
SHA_BYTES = 1024
WORK = ROOT / ".temp/library-matrix"


def run(*args: str | Path) -> str:
    return subprocess.check_output(args, cwd=ROOT, text=True)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(msync: Path) -> dict[str, str]:
    expected = {"tree": str(SIZE * 9), "heap": str(SIZE), "hash": str(SIZE * 9)}
    keys = sorted(2 * (i * 40503 % 65536) for i in range(1, SIZE + 1))
    for case in ("tree", "heap", "hash"):
        source = (HERE / f"{case}.msync").read_text().replace("SIZE", str(SIZE))
        if case == "heap":
            source = source.replace("HEAP_EXPECTED", ",".join(map(str, keys)))
        build_mso(msync, case, source)
    return expected | prepare_sha(msync)


def prepare_sha(msync: Path, *, arithmetic: bool = False) -> dict[str, str]:
    constants = [
        int(word, 16) for word in (HERE / "round-constants.txt").read_text().split()
    ]
    sha = (HERE / "sha256.msync").read_text()
    if arithmetic:
        start = sha.index("    bits:")
        sha = sha[:start] + sha[sha.index("    word:", start) :]
        start = sha.index("    bit_and:")
        sha = (
            sha[:start]
            + (HERE / "and-arithmetic.msync.inc").read_text()
            + "\n"
            + sha[sha.index("    bit_xor:", start) :]
        )
    else:
        nibble = ",".join(str(a & b) for a in range(16) for b in range(16))
        sha = sha.replace("AND_NIBBLES", nibble)
    sha = sha.replace("ROUND_CONSTANTS", ",".join(map(str, constants)))
    inputs = {
        "sha-abc": b"abc",
        "sha-boundary": bytes(range(56)),
        "sha256": random.Random(881).randbytes(SHA_BYTES),
    }
    expected: dict[str, str] = {}
    for case, data in inputs.items():
        if arithmetic:
            case += "-arithmetic"
        # Padding and word decoding are outside the timed compression kernel.
        padded = data + b"\x80" + bytes((55 - len(data)) % 64)
        padded += (len(data) * 8).to_bytes(8, "big")
        words = list(struct.unpack(f">{len(padded) // 4}I", padded))
        expected[case] = hashlib.sha256(data).hexdigest()
        (WORK / f"{case}.words").write_text(" ".join(map(str, constants + words)))
        build_mso(
            msync,
            case,
            sha.replace("INPUT_WORDS", ",".join(map(str, words))).replace(
                "WORD_COUNT", str(len(words))
            ),
        )
    return expected


def build_mso(msync: Path, case: str, source: str) -> None:
    path = WORK / f"{case}.msync"
    path.write_text(source)
    run(msync, "--no-config", "compile", path, "-O2", "-o", WORK / f"{case}.mso")


def command(language: str) -> list[str | Path]:
    match language:
        case "C++":
            return [WORK / "cpp"]
        case "C#":
            return ["dotnet", WORK / "jit/library-matrix.dll"]
        case "Java":
            return ["java", "-Xms256m", "-Xmx256m", "-cp", WORK, "Probe"]
        case "Python":
            return [sys.executable, HERE / "python_probe.py"]
        case "msync":
            return [WORK / "aot/library-matrix"]
        case _:
            raise ValueError(language)


def measure(
    language: str, case: str, expected: str, *, verify_only: bool = False
) -> list[float]:
    sha = case.startswith("sha")
    if verify_only:
        warmup, samples, batch = 0, 1, 1
    elif language == "msync":
        warmup, samples, batch = 1, 5, 1
    elif language == "Python":
        warmup, samples, batch = 3, 11, 2 if case == "tree" else 8
    else:
        warmup, samples, batch = 10, 11, 512 if sha else 16
    workload = "msync" if language == "msync" else "sha256" if sha else case
    extra: list[Path] = []
    if language == "msync":
        extra.append(WORK / f"{case}.mso")
    elif sha:
        extra.append(WORK / f"{case}.words")
    output = run(
        *command(language),
        workload,
        str(SIZE),
        str(warmup),
        str(samples),
        str(batch),
        expected,
        *extra,
    )
    result = [float(line) for line in output.splitlines()]
    assert len(result) == samples and all(math.isfinite(t) and t > 0 for t in result)
    return result


def compare_and(msync: Path, cpu: int, output: Path) -> None:
    expected = prepare_sha(msync) | prepare_sha(msync, arithmetic=True)
    assert expected["sha256"] == expected["sha256-arithmetic"]
    os.sched_setaffinity(0, {cpu})
    for case in ("sha-abc-arithmetic", "sha-boundary-arithmetic"):
        measure("msync", case, expected[case], verify_only=True)
    variants = ("sha256", "sha256-arithmetic")
    results: dict[str, list[list[float]]] = {case: [] for case in variants}
    for order in (variants, tuple(reversed(variants))):
        for case in order:
            samples = measure("msync", case, expected[case])
            results[case].append(samples)
            print(f"{case}: {statistics.median(samples):.3f} ms", flush=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(
            {
                "sha_bytes": SHA_BYTES,
                "cpu_affinity": cpu,
                "msync_cli_sha256": digest(msync),
                "host_sha256": digest(WORK / "aot/library-matrix"),
                "artifacts_sha256": {
                    case: digest(WORK / f"{case}.mso") for case in variants
                },
                "expected": expected,
                "samples_ms_per_workload": results,
                "medians_ms": {
                    case: statistics.median(forks[0] + forks[1])
                    for case, forks in results.items()
                },
            },
            indent=2,
        )
        + "\n"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--msync", type=Path, required=True)
    parser.add_argument("--cpp", default="/usr/bin/g++")
    parser.add_argument("--cpu", type=int, default=min(os.sched_getaffinity(0)))
    parser.add_argument("--reuse-build", action="store_true")
    parser.add_argument(
        "--compare-and",
        action="store_true",
        help="Compare just the two msync SHA-256 AND implementations",
    )
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    msync: Path = args.msync.resolve()
    cpp: str = args.cpp
    WORK.mkdir(parents=True, exist_ok=True)
    if not args.reuse_build and not args.compare_and:
        run(
            cpp,
            "-std=c++20",
            "-O3",
            "-march=native",
            "-Wall",
            "-Wextra",
            "-Werror",
            HERE / "probe.cpp",
            "-o",
            WORK / "cpp",
        )
        run("javac", "-Xlint:all", "-Werror", "-d", WORK, HERE / "Probe.java")
        run(
            "dotnet",
            "build",
            HERE / "Probe.csproj",
            "-c",
            "Release",
            "-o",
            WORK / "jit",
        )
        run(
            "dotnet",
            "publish",
            HERE / "Probe.csproj",
            "-c",
            "Release",
            "-r",
            "linux-x64",
            "-p:MatrixNative=true",
            "-o",
            WORK / "aot",
        )
    if args.compare_and:
        compare_and(msync, args.cpu, args.output or WORK / "and.json")
        return
    expected = prepare(msync)
    os.sched_setaffinity(0, {args.cpu})
    for language in LANGUAGES:
        for case in ("sha-abc", "sha-boundary"):
            measure(language, case, expected[case], verify_only=True)
    results: dict[str, dict[str, list[list[float]]]] = {
        case: {language: [] for language in LANGUAGES} for case in CASES
    }
    for case in CASES:
        for order in (LANGUAGES, tuple(reversed(LANGUAGES))):
            for language in order:
                samples = measure(language, case, expected[case])
                results[case][language].append(samples)
                print(
                    f"{case:7} {language:6} {statistics.median(samples):12.6f} ms",
                    flush=True,
                )
    report = {
        "git_head": run("git", "rev-parse", "HEAD").strip(),
        "host": platform.platform(),
        "lscpu": run("lscpu"),
        "cpu_affinity": args.cpu,
        "versions": {
            "C++": run(cpp, "--version").splitlines()[0],
            "C#": run("dotnet", "--info"),
            "Java": run("java", "--version").strip(),
            "Python": {"version": sys.version, "executable": sys.executable},
            "msync": run(msync, "--version").strip(),
        },
        "images_sha256": {
            path.name: digest(path)
            for path in (
                msync,
                WORK / "cpp",
                WORK / "jit/library-matrix.dll",
                WORK / "aot/library-matrix",
                WORK / "Probe.class",
            )
        },
        "artifacts_sha256": {case: digest(WORK / f"{case}.mso") for case in CASES},
        "keys": SIZE,
        "sha_bytes": SHA_BYTES,
        "expected": expected,
        "samples_ms_per_workload": results,
        "medians_ms": {
            case: {
                language: statistics.median(forks[0] + forks[1])
                for language, forks in row.items()
            }
            for case, row in results.items()
        },
    }
    output: Path = args.output or WORK / "result.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
