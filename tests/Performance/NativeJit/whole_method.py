"""Validate whole-method native execution through current source and detached artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import platform
import runpy
import shutil
import sqlite3
import subprocess
from collections.abc import Callable, Sequence
from contextlib import closing
from dataclasses import dataclass
from pathlib import Path
from random import Random
from typing import Literal, TypeVar, cast

from journeys import HERE, Level, Profile, invoke, profile

DEFAULT_ENTRY: str = "native_worker_entries"
MethodExpectation = Literal["native", "fallback", "observe"]
T = TypeVar("T")
METRICS: tuple[str, ...] = (
    "calls",
    "borrowed_frames",
    "number_boxes",
    "predicate_calls",
    "direct_source_calls",
    "native_worker_entries",
    "native_worker_unmapped_entries",
    "native_worker_operations",
    "native_worker_guard_misses",
    "native_worker_prefix_exits",
    "native_worker_stores",
    "jit_compilations",
    "jit_entries",
    "jit_code_bytes",
)


@dataclass(frozen=True)
class Fixture:
    name: str
    source: bytes
    data: bytes
    expected: bytes


@dataclass(frozen=True)
class Route:
    executable: Path
    arguments: tuple[str, ...]
    expected: bytes
    search_path: Path | None = None
    detailed: bool = False


@dataclass(frozen=True)
class Wine:
    loader: Path
    mapped: Callable[[str], str]
    env: Callable[[bool], dict[str, str]]


@dataclass(frozen=True)
class Host:
    binary: Path
    wine: Wine | None = None

    def invoke(
        self,
        cwd: Path,
        args: Sequence[str],
        *,
        data: bytes = b"",
        native: bool = True,
        search_path: Path | None = None,
    ) -> bytes:
        if self.wine is None:
            output: bytes = invoke(
                self.binary,
                cwd,
                args,
                data=data,
                native=native,
                search_path=search_path,
            )
            return output.replace(b"\r\n", b"\n") if os.name == "nt" else output
        cmd: list[str] = [
            str(self.wine.loader),
            self.wine.mapped(str(self.binary)),
            "--no-config",
            *(self.wine.mapped(arg) for arg in args),
        ]
        proc: subprocess.CompletedProcess[bytes] = subprocess.run(
            cmd,
            cwd=cwd,
            input=data,
            capture_output=True,
            env=self.wine.env(native),
            timeout=90,
            check=False,
        )
        if proc.returncode or proc.stderr:
            raise AssertionError((cmd, proc.returncode, proc.stdout, proc.stderr))
        return proc.stdout.replace(b"\r\n", b"\n")


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def linked_fixture(mutate: bool, *, effect: bool = False) -> Fixture:
    rng = Random(8345)
    thresholds: list[int] = [rng.randrange(140) for _ in range(4096)]
    if mutate:
        thresholds.insert(2048, -1000)
    last: int = 256
    hits = checksum = 0
    output: list[int] = []
    for threshold in thresholds:
        if threshold == -1000:
            last = 1
            continue
        selected: int = next(
            (i for i in range(1, last + 1) if i * 23 % 127 >= threshold), 0
        )
        hits += selected != 0
        checksum = (checksum * 31 + selected) % 1_000_003
        if effect and selected != 0:
            output.append(selected)
    source: bytes = (HERE / "method-linked-gate.msync").read_bytes()
    if effect:
        source = source.replace(
            b"if cost >= threshold { return index; };",
            b"if cost >= threshold { io::println(index as String); return index; };",
        )
    return Fixture(
        "linked-effect" if effect else "linked-mutation" if mutate else "linked-gate",
        source,
        ("\n".join(map(str, thresholds)) + "\n").encode(),
        ("\n".join(map(str, (*output, hits, checksum))) + "\n").encode(),
    )


def signed_link_fixture() -> Fixture:
    base: Fixture = linked_fixture(True)
    after_cut: list[int] = [int(line) for line in base.data.splitlines()[2049:]]
    negatives: int = sum(threshold > 23 for threshold in after_cut)
    return Fixture(
        "linked-signed-zero",
        (HERE / "method-linked-signed-zero.msync").read_bytes(),
        base.data,
        base.expected + f"{negatives}\n".encode(),
    )


def set_prefix_fixture() -> Fixture:
    original: list[int] = list(range(0, 48, 2))
    inserted: set[int] = set()
    keys: list[int] = list(original)
    for stage, addition in enumerate((-1, 127, 49)):
        keys.extend(original[(i * 17 + stage) % len(original)] for i in range(96))
        keys.append(addition)
    score: int = 0
    for key in keys:
        if key not in inserted:
            inserted.add(key)
            score = (score * 7 + key) % 1_000_003
    expected: str = f"{len(inserted)}\n{len(keys) - len(inserted)}\n{score}\n{sum(inserted)}\n{len(inserted)}\n"
    return Fixture(
        "set-prefix",
        (HERE / "method-set-prefix.msync").read_bytes(),
        ("\n".join(map(str, keys)) + "\n").encode(),
        expected.encode(),
    )


def heap_trace(
    keys: Sequence[T], less: Callable[[T, T], bool], switch_at: int
) -> tuple[list[T], list[T], int]:
    heap: list[T] = []
    for value in keys:
        index: int = len(heap)
        heap.append(value)
        while index > 0:
            parent: int = (index - 1) // 2
            if not less(value, heap[parent]):
                break
            heap[index] = heap[parent]
            index = parent
        heap[index] = value
    snapshot: list[T] = heap.copy()
    popped: list[T] = []
    effects: int = 0
    for pop in range(len(keys)):
        popped.append(heap[0])
        value = heap.pop()
        if not heap:
            continue
        index = 0
        while 2 * index + 1 < len(heap):
            child: int = 2 * index + 1
            if child + 1 < len(heap):
                effects += pop >= switch_at
                if less(heap[child + 1], heap[child]):
                    child += 1
            effects += pop >= switch_at
            if not less(heap[child], value):
                break
            heap[index] = heap[child]
            index = child
        heap[index] = value
    return popped, snapshot, effects


def heap_live_fixture() -> Fixture:
    count: int = 1024
    switch_at: int = 384
    keys: list[int] = [2 * (40503 * i % 65536) for i in range(1, count + 1)]
    popped, snapshot, effects = heap_trace(keys, lambda a, b: a < b, switch_at)
    if popped != sorted(keys):
        raise AssertionError("heap model disagrees with independent sorted order")
    lines: tuple[int | str, ...] = (
        *sorted(keys),
        "snapshot",
        *snapshot,
        "summary",
        0,
        effects,
    )
    return Fixture(
        "heap-live",
        (HERE / "method-heap-live.msync").read_bytes(),
        f"{switch_at}\n".encode(),
        ("\n".join(map(str, lines)) + "\n").encode(),
    )


def heap_ieee_fixture() -> Fixture:
    keys: list[float] = [math.nan, -math.inf, math.inf, -0.0, 0.0, -7.0, 13.0, 1.0] * 64

    def label(value: float) -> str:
        if math.isnan(value):
            return "nan"
        if math.isinf(value):
            return "-inf" if value < 0 else "+inf"
        if value == 0:
            return "-0" if math.copysign(1, value) < 0 else "+0"
        return str(int(value))

    popped, _, _ = heap_trace(
        keys, lambda a, b: a < b or (not math.isnan(a) and math.isnan(b)), len(keys)
    )
    return Fixture(
        "heap-ieee",
        (HERE / "method-heap-ieee.msync").read_bytes(),
        b"",
        ("\n".join(map(label, popped)) + "\n").encode(),
    )


def fixtures() -> tuple[Fixture, ...]:
    n: int = 8192
    keys: set[int] = {2 * (40503 * i % 65536) for i in range(1, n + 1)}
    score: int = (
        len(keys)
        + 3 * sum(k in keys for k in keys)
        + 5 * sum(k + 1 not in keys for k in keys)
    )
    if score != 73728:
        raise AssertionError(score)
    rng: Random = Random(1927)
    readings: list[int] = [rng.randrange(-83, 126) for _ in range(8192)]
    prior: int = 3
    checksum: int = 0
    for reading in readings:
        value: int = (
            prior - reading * 2
            if reading < 0
            else (reading - prior if reading > prior else prior + reading)
        )
        prior = value - 32 if value > 64 else value + 4
        checksum = (checksum * 17 + prior) % 1_000_003
    return (
        Fixture(
            "set",
            (HERE / "method-set-score.msync").read_bytes(),
            b"",
            f"{score}\n".encode(),
        ),
        Fixture(
            "heap",
            (HERE / "method-heap-score.msync")
            .read_bytes()
            .replace(b"EXPECTED_KEYS", ", ".join(map(str, sorted(keys))).encode()),
            b"",
            f"{n}\n".encode(),
        ),
        Fixture(
            "set-live",
            (HERE / "method-set-live.msync").read_bytes(),
            b"2048\n4096\n",
            b"2048\n4096\n",
        ),
        Fixture(
            "estimate",
            (HERE / "method-estimate.msync").read_bytes(),
            ("\n".join(map(str, readings)) + "\n").encode(),
            f"{prior}\n{checksum}\n".encode(),
        ),
        linked_fixture(False),
        linked_fixture(True),
        signed_link_fixture(),
        linked_fixture(True, effect=True),
        set_prefix_fixture(),
        heap_live_fixture(),
        heap_ieee_fixture(),
    )


def prepare(producer: Host, inputs: Path, chosen: set[str]) -> None:
    inputs.mkdir(parents=True, exist_ok=False)
    for fixture in fixtures():
        if chosen and fixture.name not in chosen:
            continue
        folder: Path = inputs / fixture.name
        folder.mkdir()
        source: Path = folder / "program.msync"
        source.write_bytes(fixture.source)
        producer.invoke(
            folder,
            ("--no-telemetry", "compile", "-O2", "program.msync", "-o", "program.mso"),
        )
        source.unlink()
        producer.invoke(
            folder, ("--no-telemetry", "bind-ir", "program.mso", "-o", "program.mexe")
        )
        source.write_bytes(fixture.source)
        (folder / "stdin").write_bytes(fixture.data)
        (folder / "expected").write_bytes(fixture.expected)


def metric_rows(path: Path, watched: set[str]) -> dict[str, dict[str, int]]:
    with closing(sqlite3.connect(path)) as db:
        rows: list[tuple[str, str, str, int]] = db.execute(
            """SELECT i.value, f.value, m.key, m.value_int FROM records r
               JOIN attributes i ON i.record_id=r.id AND i.key='image.id'
               JOIN attributes f ON f.record_id=r.id AND f.key='function.id'
               JOIN metrics m ON m.record_id=r.id
               WHERE r.name='vm.function' AND m.value_int IS NOT NULL"""
        ).fetchall()
    result: dict[str, dict[str, int]] = {}
    for image, function, key, value in rows:
        if key in watched and value != 0:
            result.setdefault(f"{image}:{function}", {})[key] = value
    return result


def physical_natives(path: Path) -> dict[str, int]:
    with closing(sqlite3.connect(path)) as db:
        rows: list[tuple[str, int]] = db.execute(
            """SELECT a.value, SUM(m.value_int) FROM records r
               JOIN attributes a ON a.record_id=r.id AND a.key='native.name'
               JOIN metrics m ON m.record_id=r.id AND m.key='fast_calls'
               WHERE r.name='vm.native' GROUP BY a.value"""
        ).fetchall()
    return {f"fast-native:{name}": hits for name, hits in rows}


def stage_route(host: Host, directory: Path, fixture: Path, name: str) -> Route:
    directory.mkdir(parents=True)
    expected: bytes = (fixture / "expected").read_bytes()
    if name == "shebang":
        script: Path = directory / "program.msync"
        script.write_bytes(
            b"#!/usr/bin/env msync\n" + (fixture / "program.msync").read_bytes()
        )
        script.chmod(0o755)
        search: Path = directory / "bin"
        search.mkdir()
        (search / "msync").symlink_to(host.binary)
        return Route(script, ("-O2",), expected, search)
    ext: str = {
        "source": "msync",
        "object": "mso",
        "bound": "mexe",
        "rebound": "mso",
    }[name]
    original: Path = fixture / f"program.{ext}"
    path: Path = directory / f"program.{ext}"
    shutil.copyfile(original, path)
    if set(directory.iterdir()) != {path}:
        raise AssertionError(f"input isolation failed: {directory}")
    if name == "rebound":
        host.invoke(
            directory, ("--no-telemetry", "bind-ir", path.name, "-o", "program.mexe")
        )
        path.unlink()
        path = directory / "program.mexe"
    return Route(
        host.binary,
        ("-O2", "run", path.name),
        expected,
        detailed=name == "bound",
    )


def check_route(
    host: Host,
    route: Route,
    directory: Path,
    stdin: bytes,
    watched: set[str],
    entry: str,
    expectation: MethodExpectation,
    guard: str | None,
    reduction: set[str],
    *,
    detailed: bool,
) -> dict[str, object]:
    runner: Host = Host(route.executable, host.wine)
    reports: dict[str, object] = {}
    observed: dict[str, Profile] = {}
    physical: dict[str, dict[str, int]] = {}

    def invoke(native: bool, args: Sequence[str]) -> None:
        output: bytes = runner.invoke(
            directory,
            (*args, *route.arguments),
            data=stdin,
            native=native,
            search_path=route.search_path,
        )
        if output != route.expected:
            raise AssertionError((directory, native, args, output, route.expected))

    for native, mode in ((True, "auto"), (False, "off")):
        invoke(native, ("--no-telemetry",))
        level: Level
        for level in ("profile", "instructions") if detailed else ("profile",):
            database: str = f"{level}-{mode}.sqlite"
            invoke(native, ("--telemetry-db", database, "--telemetry-level", level))
            row: Profile = profile(directory / database, level)
            if row.status != "ok":
                raise AssertionError((directory, level, mode, row.status))
            label: str = f"{level}-{mode}"
            observed[label] = row
            fast_natives: dict[str, int] = physical_natives(directory / database)
            physical[label] = row.metrics | fast_natives
            reports[label] = {
                "totals": {k: row.metrics.get(k, 0) for k in sorted(watched)},
                "selected_functions": metric_rows(directory / database, watched),
                "logical_instructions": sum(row.opcodes.values()),
                "canonical_native_calls": row.native_calls,
                "physical_fast_native_calls": fast_natives,
            }

    auto, off = observed["profile-auto"], observed["profile-off"]
    unmapped: int = auto.metrics.get("native_worker_unmapped_entries", 0)
    if unmapped > auto.metrics.get(entry, 0):
        raise AssertionError(
            (directory, "unmapped worker count exceeds native entries")
        )
    if unmapped == 0:
        auto.check_logical(off, "profile")
    reports["aggregate_logical_counts_complete"] = unmapped == 0
    if off.metrics.get(entry, 0) != 0:
        raise AssertionError(
            (directory, "off still entered whole-method worker", entry, off.metrics)
        )
    if expectation == "native" and auto.metrics.get(entry, 0) <= 0:
        raise AssertionError(
            (directory, "auto did not enter whole-method worker", entry, auto.metrics)
        )
    if expectation == "native" and auto.metrics.get("native_worker_operations", 0) <= 0:
        raise AssertionError(
            (directory, "native entry had no counted typed operations")
        )
    if expectation == "fallback" and auto.metrics.get(entry, 0) != 0:
        raise AssertionError(
            (directory, "fallback entered whole-method worker", entry, auto.metrics)
        )
    if guard and auto.metrics.get(guard, 0) <= 0:
        raise AssertionError(
            (directory, "missing dynamic guard exit", guard, auto.metrics)
        )
    for key in reduction:
        a, b = (physical[f"profile-{mode}"].get(key, 0) for mode in ("auto", "off"))
        if a >= b:
            raise AssertionError((directory, key, "not reduced", a, b))
    if detailed:
        detail_auto, detail_off = (
            observed["instructions-auto"],
            observed["instructions-off"],
        )
        detail_auto.check_logical(detail_off, "instructions")
        if detail_auto.metrics.get(entry, 0) or detail_off.metrics.get(entry, 0):
            raise AssertionError(
                (directory, "detailed observation entered a method worker")
            )
        off.check_logical(detail_off, "profile")
    return reports


def adapter(path: Path) -> Wine:
    api: dict[str, object] = runpy.run_path(str(path))
    return Wine(
        cast(Path, api["LOADER"]),
        cast(Callable[[str], str], api["mapped"]),
        cast(Callable[[bool], dict[str, str]], api["env"]),
    )


def assignments(values: Sequence[str]) -> dict[str, str]:
    pairs: dict[str, str] = {}
    for value in values:
        name, sep, key = value.partition(":")
        if not sep or not name or not key:
            raise ValueError(f"expected case:value, got {value}")
        pairs[name] = key
    return pairs


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--producer", type=Path)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--inputs", type=Path)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--wine-adapter", type=Path)
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--route", action="append", default=[])
    parser.add_argument("--entry-counter", default=DEFAULT_ENTRY)
    parser.add_argument("--guard", action="append", default=[], help="case:counter")
    parser.add_argument(
        "--expect", action="append", default=[], help="case:native|fallback|observe"
    )
    parser.add_argument(
        "--reduce",
        action="append",
        default=[],
        help="case:counter or case:fast-native:operation (repeatable)",
    )
    parser.add_argument("--no-detailed", action="store_true")
    args: argparse.Namespace = parser.parse_args()

    binary: Path = args.binary.resolve(strict=True)
    wine: Wine | None = adapter(args.wine_adapter) if args.wine_adapter else None
    host = Host(binary, wine)
    work: Path = args.work.resolve()
    work.mkdir(parents=True, exist_ok=True)
    inputs: Path = args.inputs.resolve() if args.inputs else work / "inputs"
    if not inputs.exists():
        producer: Host = (
            Host(args.producer.resolve(strict=True)) if args.producer else host
        )
        prepare(producer, inputs, set(args.case))
    if args.prepare_only:
        return

    expect: dict[str, str] = assignments(args.expect)
    guards: dict[str, str] = assignments(args.guard)
    reductions: dict[str, set[str]] = {}
    for spec in args.reduce:
        for case, metric in assignments((spec,)).items():
            reductions.setdefault(case, set()).add(metric)
    watched: set[str] = {*METRICS, args.entry_counter, *guards.values()}
    watched.update(key for keys in reductions.values() for key in keys)
    result: dict[str, object] = {
        "binary": str(binary),
        "binary_sha256": digest(binary),
        "target": "win-x64 CoreCLR under Wine" if wine else platform.platform(),
        "entry_counter": args.entry_counter,
        "cases": {},
    }
    managed_payload: Path = binary.with_suffix(".dll")
    if managed_payload.is_file():
        result["managed_payload_sha256"] = digest(managed_payload)
    cases: dict[str, object] = {}
    for fixture in sorted(inputs.iterdir()):
        name: str = fixture.name
        if args.case and name not in args.case:
            continue
        default_routes: tuple[str, ...] = ("source", "object", "bound", "rebound")
        if wine is None and platform.system() == "Linux":
            default_routes += ("shebang",)
        routes: list[str] = [
            r for r in default_routes if not args.route or r in args.route
        ]
        if not routes:
            raise ValueError((name, routes))
        observations: dict[str, object] = {}
        stdin: bytes = (fixture / "stdin").read_bytes()
        for label in routes:
            selector: str = f"{name}/{label}"
            expectation: str = expect.get(selector, expect.get(name, "observe"))
            if expectation not in ("native", "fallback", "observe"):
                raise ValueError((selector, expectation))
            directory: Path = work / name / label
            route: Route = stage_route(host, directory, fixture, label)
            observations[label] = check_route(
                host,
                route,
                directory,
                stdin,
                watched,
                args.entry_counter,
                cast(MethodExpectation, expectation),
                guards.get(selector, guards.get(name)),
                reductions.get(selector, reductions.get(name, set())),
                detailed=route.detailed and not args.no_detailed,
            )
            print(f"PASS {name}/{label}", flush=True)
        cases[name] = {
            "inputs_sha256": {p.name: digest(p) for p in sorted(fixture.iterdir())},
            "routes": observations,
        }
        result["cases"] = cases
        (work / "result.json").write_text(
            json.dumps(result, indent=2) + "\n", encoding="utf-8"
        )


if __name__ == "__main__":
    main()
