#!/usr/bin/env python3
"""Run one hot word kernel artifact with guest JIT on and off."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import sqlite3
import subprocess
import tempfile
from collections import Counter
from pathlib import Path

from bits_journey import MASK, MODULUS, ROOT, expect_success, invoke

KernelInput = tuple[int, int, int, int, int]

SOURCE: str = """fn next_number() -> Number {
    let line? = $std::io::read_line();
    if line is null { -1 } else { line as Number }
};
fn word_kernel(seed: Number, salt: Number, rounds: Number, split: Number, turn: Number) -> Number {
    let word = $std::bits::bit_xor(seed, salt);
    let step = 0;
    loop {
        if step >= rounds { break; };
        if step < split {
            word = $std::bits::bit_xor($std::bits::rotate_left(word, turn), salt);
        } else {
            let lower = $std::bits::shift_right_signed(word, 3);
            let upper = $std::bits::shift_left(word, 29);
            word = $std::bits::bit_xor($std::bits::bit_or(lower, upper), seed);
        };
        step = step + 1;
    };
    $std::bits::bit_xor(word, 0)
};
let size = next_number();
for _ in $std::ranges::iota(0, size) {
    let seed = next_number();
    let salt = next_number();
    let rounds = next_number();
    let split = next_number();
    let turn = next_number();
    $std::io::println(word_kernel(seed, salt, rounds, split, turn) as String);
};
"""


def vectors() -> list[KernelInput]:
    rng: random.Random = random.Random(37961)
    values: list[KernelInput] = [
        (MASK, 0, 0, 0, 31),
        (1 << 31, 1, 5, 0, 0),
        (1, 1 << 31, 11, 11, 31),
    ]
    for _ in range(253):
        rounds: int = rng.randint(19, 97)
        values.append(
            (
                rng.randrange(MODULUS),
                rng.randrange(MODULUS),
                rounds,
                rng.randrange(rounds + 1),
                rng.randrange(32),
            )
        )
    values.append((MASK, 1 << 31, 1500, 739, 19))
    return values


def oracle(vector: KernelInput) -> int:
    seed, salt, rounds, split, turn = vector
    word: int = seed ^ salt
    for step in range(rounds):
        if step < split:
            bits: str = f"{word:032b}"
            word = int(bits[turn:] + bits[:turn], 2) ^ salt
        else:
            signed: int = word if word < (1 << 31) else word - MODULUS
            low: int = signed // 8 % MODULUS
            high: int = word * (1 << 29) % MODULUS
            word = (low | high) ^ seed
    return word


def expected_calls(vectors: list[KernelInput], failed: bool = False) -> dict[str, int]:
    totals: Counter[str] = Counter()
    for _, _, rounds, split, _ in vectors:
        totals["bits.bit_xor"] += rounds + 2
        totals["bits.rotate_left"] += split
        for name in ("bit_or", "shift_right_signed", "shift_left"):
            totals["bits." + name] += rounds - split
    if failed:
        totals["bits.bit_xor"] += 1
        totals["bits.rotate_left"] += 1
    return dict(totals)


def profile(path: Path) -> tuple[dict[str, int], dict[str, int]]:
    with sqlite3.connect(path) as database:
        metrics: dict[str, int] = dict(
            database.execute(
                """
                SELECT m.key, SUM(m.value_int)
                FROM records r
                JOIN attributes a ON a.record_id = r.id AND a.key = 'function.name'
                JOIN metrics m ON m.record_id = r.id
                WHERE r.name = 'vm.function' AND a.value LIKE 'word_kernel%'
                    AND (m.key LIKE 'native_worker_%' OR m.key = 'jit_yields')
                GROUP BY m.key
                """
            )
        )
        calls: dict[str, int] = dict(
            database.execute(
                """
                SELECT a.value, SUM(m.value_int)
                FROM records r
                JOIN attributes a ON a.record_id = r.id AND a.key = 'native.name'
                JOIN metrics m ON m.record_id = r.id AND m.key = 'calls'
                WHERE r.name = 'vm.native' AND a.value LIKE 'bits.%'
                GROUP BY a.value
                """
            )
        )
    return metrics, calls


def run(
    cli: Path,
    artifact: Path,
    launch: Path,
    vectors: list[KernelInput],
    *,
    jit: bool,
    failed: bool,
) -> tuple[dict[str, object], str]:
    mode: str = "auto" if jit else "off"
    database: Path = launch / (mode + ("-invalid" if failed else "") + ".sqlite")
    cases: list[KernelInput] = vectors + [(1, 2, 1, 1, 32)] if failed else vectors
    lines: list[str] = [str(len(cases))]
    lines.extend(str(field) for case in cases for field in case)
    expected: str = "".join(f"{oracle(vector)}\n" for vector in vectors)
    environment: dict[str, str] = dict(os.environ, MSYNC_JIT=mode)
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [
            cli,
            "--no-config",
            "--telemetry-db",
            database,
            "--telemetry-level",
            "profile",
            "run",
            artifact,
        ],
        cwd=launch,
        env=environment,
        input="\n".join(lines) + "\n",
        capture_output=True,
        check=False,
        text=True,
        encoding="utf-8",
        timeout=60,
    )
    if failed:
        start: int = SOURCE.index("$std::bits::rotate_left")
        end: int = start + len("$std::bits::rotate_left(word, turn)")
        if (
            result.returncode == 0
            or result.stdout != expected
            or "Word32 shift count" not in result.stderr
            or f"(source offset {start}..{end})" not in result.stderr
        ):
            raise AssertionError(
                (mode, result.returncode, result.stdout[-200:], result.stderr)
            )
    else:
        expect_success(result, expected)
    metrics, calls = profile(database)
    entries: int = metrics.get("native_worker_entries", 0)
    operations: int = metrics.get("native_worker_operations", 0)
    misses: int = metrics.get("native_worker_guard_misses", 0)
    unmapped: int = metrics.get("native_worker_unmapped_entries", 0)
    yields: int = metrics.get("jit_yields", 0)
    if jit:
        if (
            entries <= 0
            or operations <= 0
            or yields <= 0
            or misses != int(failed)
            or unmapped
        ):
            raise AssertionError((mode, failed, metrics, calls))
    elif entries or operations or misses or unmapped or yields:
        raise AssertionError((mode, failed, metrics))
    wanted: dict[str, int] = expected_calls(vectors, failed)
    if calls != wanted:
        raise AssertionError((mode, failed, calls, wanted))
    return {
        "results": len(vectors),
        "metrics": metrics,
        "logical_native_calls": calls,
    }, result.stderr


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    cli: Path = args.binary.resolve(strict=True)
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    batch: list[KernelInput] = vectors()
    with tempfile.TemporaryDirectory(prefix="bits-native-worker-", dir=scratch) as name:
        work: Path = Path(name)
        launch: Path = work / "elsewhere"
        launch.mkdir()
        source: Path = work / "word kernel.msync"
        portable: Path = work / "word kernel.mso"
        artifact: Path = work / "word kernel.mexe"
        source.write_text(SOURCE, encoding="utf-8", newline="\n")
        expect_success(
            invoke(cli, work, ["compile", "-O2", source, "-o", portable]), ""
        )
        source.unlink()
        expect_success(invoke(cli, work, ["bind-ir", portable, "-o", artifact]), "")
        portable.unlink()
        report: dict[str, object] = {
            "binary_sha256": hashlib.sha256(cli.read_bytes()).hexdigest(),
            "artifact_sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(),
            "managed_assemblies_sha256": {
                assembly.name: hashlib.sha256(assembly.read_bytes()).hexdigest()
                for assembly in sorted(cli.parent.glob("*.dll"))
                if assembly.name == "msync.dll" or assembly.name.startswith("Msync.")
            },
        }
        for failed in (False, True):
            auto, native_error = run(
                cli, artifact, launch, batch, jit=True, failed=failed
            )
            off, canonical_error = run(
                cli, artifact, launch, batch, jit=False, failed=failed
            )
            if native_error != canonical_error:
                raise AssertionError((native_error, canonical_error))
            report["invalid_after_warmup" if failed else "valid"] = {
                "jit_auto": auto,
                "jit_off": off,
            }
    if args.report is not None:
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
