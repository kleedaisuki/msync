#!/usr/bin/env python3
"""Exercise bind-time instruction selection in a detached executable."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """
./missing-source -> ./result.txt;
"published" -> ./result.txt;
./payload.dat -> ./copied.dat;
fn make(seed: Number) {
    let row = scope { count: seed, marker: "kept", factor: 2 };
    let alias = row;
    fn step(delta: Number) {
        let before = row::count;
        alias::count = before + delta;
        row::count
    };
    fn read() { row::count };
    scope { step: step, read: read }
};
fn counter(seed: Number) {
    let value = seed;
    fn bump(delta: Number) { value = value + delta; value };
    fn peek() { value };
    scope { bump: bump, peek: peek }
};
fn local(seed: Number) {
    let left = scope { count: seed, marker: "local", factor: 2 };
    let alias = left;
    let right = scope { count: seed, marker: "other", factor: 3 };
    alias::count = alias::count + 1;
    right::count = right::count + 2;
    left::count * left::factor + right::count * right::factor
};
let seed = $std::io::read_text(./seed.txt) as Number;
let first = make(seed);
let second = make(seed + 10);
$std::io::println(first::step(3) as String);
$std::io::println(second::step(5) as String);
for _ in $std::ranges::iota(0, 40) { first::step(1); };
$std::io::println(first::read() as String);
$std::io::println(second::read() as String);
$std::io::println(local(seed) as String);
let scalar = counter(seed);
$std::io::println(scalar::bump(4) as String);
$std::io::println(scalar::peek() as String);
"""


def invoke(
    binary: Path,
    cwd: Path,
    *arguments: str,
    policy: str = "off",
    trace: Path | None = None,
) -> str:
    recording: list[str] = (
        ["--trace", str(trace), "--telemetry-level", "diagnostics"]
        if trace is not None
        else ["--no-telemetry"]
    )
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "--no-config", *recording, *arguments],
        cwd=cwd,
        text=True,
        encoding="utf-8",
        capture_output=True,
        env=dict(os.environ, MSYNC_JIT=policy),
        timeout=60,
        check=False,
    )
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)
    return result.stdout


def read_trace(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def allocation_sites(data: dict[str, Any]) -> list[tuple[int, int, str]]:
    functions: dict[int, dict[str, Any]] = {row["id"]: row for row in data["functions"]}
    sites: list[tuple[int, int, str]] = []
    for site in data["allocations"]:
        owner: int = site["function"]
        instruction: dict[str, Any] = functions[owner]["instructions"][
            site["instruction"]
        ]
        assert instruction["op"] == "Allocate" and instruction["a"] == site["layout"]
        sites.append((owner, site["layout"], site["placement"]))
    return sorted(sites)


def journey(binary: Path, work: Path) -> dict[str, object]:
    bound: Path = work / "bound"
    launch: Path = work / "launch"
    bound.mkdir()
    launch.mkdir()
    source: Path = bound / "program.msync"
    portable: Path = bound / "program.mso"
    executable: Path = bound / "program.mexe"
    seed_file: Path = bound / "seed.txt"
    result: Path = bound / "result.txt"
    payload: Path = bound / "payload.dat"
    copied: Path = bound / "copied.dat"
    trace: Path = work / "bind.jsonl"
    source.write_text(SOURCE, encoding="utf-8")
    seed_file.write_text("1", encoding="utf-8")
    payload.write_bytes(b"binding-generation")
    (launch / seed_file.name).write_text("999", encoding="utf-8")
    invoke(binary, bound, "compile", source.name, "-O2", "-o", portable.name)
    source.unlink()
    invoke(binary, bound, "bind-ir", portable.name, "-o", executable.name, trace=trace)
    assert not result.exists() and not copied.exists(), (
        "Binding must not publish file content"
    )
    obj: dict[str, Any] = json.loads(
        invoke(binary, bound, "disassemble", portable.name, "--json")
    )
    exe: dict[str, Any] = json.loads(
        invoke(binary, bound, "disassemble", executable.name, "--json")
    )
    portable.unlink()
    assert obj["stage"] == "Relocatable" and exe["stage"] == "Executable"
    assert "backend" not in obj, (
        "Portable artifacts must not preselect executable handlers"
    )
    assert sum(plan["eliminatedTransfers"] for plan in exe["executionPlans"]) == 1
    assert sum(len(plan["transfers"]) for plan in exe["executionPlans"]) == 2

    functions: dict[int, str] = {row["id"]: row["name"] for row in exe["functions"]}
    allocations: list[tuple[int, int, str]] = allocation_sites(exe)
    assert allocations == allocation_sites(obj), "Binding changed allocation ownership"
    assert any(
        functions[owner] == "make" and placement == "LogicalHeap"
        for owner, _, placement in allocations
    ), "Escaping capture ownership must remain on the heap"
    assert all(functions[owner] != "local" for owner, _, _ in allocations), (
        "LLVM scalar replacement of the nonescaping local scopes was lost"
    )

    step: dict[str, Any] = next(
        row
        for row in exe["functions"]
        if row["name"] == "step" and not row["isTemplate"]
    )
    dispatch: list[dict[str, Any]] = next(
        row["instructions"] for row in exe["backend"] if row["function"] == step["id"]
    )
    assert {"FrameLoad", "OffsetLoad", "OffsetStore"} <= {
        row["handler"] for row in dispatch
    }, dispatch
    assert not any(row["op"] == "FrameAddress" for row in dispatch), dispatch
    bump: dict[str, Any] = next(
        row
        for row in exe["functions"]
        if row["name"] == "bump" and not row["isTemplate"]
    )
    bump_dispatch: list[dict[str, Any]] = next(
        row["instructions"] for row in exe["backend"] if row["function"] == bump["id"]
    )
    assert {"FrameLoad", "FrameStore"} <= {row["handler"] for row in bump_dispatch}, (
        bump_dispatch
    )
    assert not any(row["op"] == "FrameAddress" for row in bump_dispatch), bump_dispatch

    steps: list[dict[str, Any]] = [
        row["metrics"]
        for row in read_trace(trace)
        if row["name"] == "backend.function"
        and str(row["attributes"].get("function.id")) == str(step["id"])
    ]
    assert len(steps) == 1 and steps[0].get("handlers.FrameLoad", 0) > 0, steps
    assert steps[0].get("handlers.executed_sites") == len(dispatch), steps
    observations: dict[str, list[str]] = {}
    tier_entries: dict[str, int] = {}
    for policy, seed in (("off", 11), ("force", 17)):
        result.unlink(missing_ok=True)
        seed_file.write_text(str(seed), encoding="utf-8")
        generation: bytes = bytes(range(seed))
        payload.write_bytes(generation)
        run_trace: Path = work / f"run-{policy}.jsonl"
        output: list[str] = invoke(
            binary, launch, "run-ir", str(executable), policy=policy, trace=run_trace
        ).splitlines()
        expected: list[str] = [
            str(value)
            for value in (
                seed + 3,
                seed + 15,
                seed + 43,
                seed + 15,
                5 * seed + 8,
                seed + 4,
                seed + 4,
            )
        ]
        assert output == expected, (policy, output, expected)
        assert result.read_text(encoding="utf-8") == "published"
        assert copied.read_bytes() == generation, "Binding froze live payload bytes"
        assert not any(
            (launch / name).exists() for name in (result.name, copied.name)
        ), "Executable lost its bound context"
        recorded: list[dict[str, Any]] = read_trace(run_trace)
        assert not any(row["name"] == "backend.function" for row in recorded)
        assert any(
            row["name"] == "vm.function"
            and row["attributes"].get("function.name") == "make"
            and row["metrics"].get("logical_allocations", 0) > 0
            for row in recorded
        ), "Escaping allocations did not execute"
        assert any(
            row["name"] == "vm.function"
            and row["attributes"].get("function.name") == "local"
            and row["metrics"]["logical_allocations"] == 0
            for row in recorded
        ), "Local scalarized function did not execute without allocations"
        tier_entries[policy] = sum(
            int(row["metrics"].get("jit_entries", 0))
            for row in recorded
            if row["name"] == "vm.function"
            and row["attributes"].get("function.name") == "step"
        )
        if policy == "off":
            assert tier_entries[policy] == 0
        observations[policy] = output
    return {
        "bind_step_metrics": steps,
        "step_executable_handlers": [row["handler"] for row in dispatch],
        "bump_executable_handlers": [row["handler"] for row in bump_dispatch],
        "eliminated_transfers": 1,
        "allocation_sites": len(allocations),
        "step_jit_entries": tier_entries,
        "output": observations,
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    temp: Path = ROOT / ".temp"
    temp.mkdir(exist_ok=True)
    with TemporaryDirectory(prefix="bound-fusion-", dir=temp) as directory:
        result: dict[str, object] = journey(args.binary.resolve(), Path(directory))
    if args.output is not None:
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(
        "PASS: detached executable fusion, capture aliasing, allocation and file plan"
    )


if __name__ == "__main__":
    main()
