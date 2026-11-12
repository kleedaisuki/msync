"""Check the product guest JIT through source, portable bytecode and bound images."""

from __future__ import annotations

import argparse
import json
import math
import os
import platform
import selectors
import signal
import sqlite3
import struct
import subprocess
import tempfile
import time
from collections.abc import Sequence
from contextlib import closing
from dataclasses import dataclass
from pathlib import Path
from typing import Literal

HERE: Path = Path(__file__).resolve().parent
ROOT: Path = HERE.parents[2]
HOT_ITERATIONS: int = 80_000
JIT_METRICS: tuple[str, ...] = (
    "jit_entries",
    "jit_tail_entries",
    "jit_compilations",
    "jit_yields",
    "jit_compile_ns",
    "jit_code_bytes",
    "jit_execute_ns",
)
Level = Literal["profile", "instructions"]
CountKey = tuple[str, ...]


@dataclass(frozen=True)
class Input:
    name: str
    lines: tuple[str, ...]
    expected: tuple[str | float, ...]
    hot: bool = True
    native_eligible: bool = True
    logical_opcodes: tuple[tuple[str, int], ...] = ()
    native_calls: tuple[tuple[str, int], ...] = ()
    requires_numeric_loop: bool = True
    tail_entries: int = 0

    def stdin(self) -> bytes:
        return ("\n".join(self.lines) + "\n").encode("ascii")

    def check(self, stdout: bytes) -> None:
        lines: list[str] = stdout.decode("utf-8").splitlines()
        if len(lines) != len(self.expected):
            raise AssertionError(f"{self.name}: unexpected output {lines!r}")
        for observed, expected in zip(lines, self.expected, strict=True):
            if isinstance(expected, str):
                ok: bool = observed == expected
            elif math.isnan(expected):
                ok = math.isnan(float(observed))
            else:
                ok = struct.pack(">d", float(observed)) == struct.pack(">d", expected)
            if not ok:
                raise AssertionError(
                    f"{self.name}: expected {expected!r}, got {observed!r}"
                )

    def check_opcodes(self, opcodes: dict[CountKey, int]) -> None:
        for opcode, expected in self.logical_opcodes:
            actual: int = sum(
                count for key, count in opcodes.items() if key[-1] == opcode
            )
            if actual != expected:
                raise AssertionError(
                    f"{self.name}: {opcode} expected {expected} visits, got {actual}"
                )


@dataclass(frozen=True)
class Fixture:
    name: str
    inputs: tuple[Input, ...]
    source: bool = True


@dataclass(frozen=True)
class Profile:
    metrics: dict[str, int]
    opcodes: dict[CountKey, int]
    sites: dict[CountKey, int]
    native_calls: dict[str, int]
    status: str

    def check_native(self, expect_native: bool, tail_entries: int = 0) -> None:
        counters: dict[str, int] = {
            key: self.metrics.get(key, 0) for key in JIT_METRICS
        }
        if expect_native:
            required = set(JIT_METRICS) - {"jit_tail_entries", "jit_yields"}
            if any(counters[key] <= 0 for key in required):
                raise AssertionError(f"expected executed native code: {counters}")
            if counters["jit_tail_entries"] != tail_entries:
                raise AssertionError(
                    f"expected {tail_entries} tail entries: {counters}"
                )
            if (counters["jit_yields"] > 0) != (tail_entries == 0):
                raise AssertionError(f"unexpected loop/tail yield count: {counters}")
            if tail_entries and (
                counters["jit_entries"] != tail_entries
                or counters["jit_compilations"] != 1
            ):
                raise AssertionError(
                    f"expected one installed tail and no native loops: {counters}"
                )
        elif any(counters.values()):
            raise AssertionError(f"unexpected native code: {counters}")

    def check_logical(self, other: Profile, level: Level) -> None:
        if not self.opcodes or self.opcodes != other.opcodes:
            raise AssertionError("logical opcode counts changed")
        if self.native_calls != other.native_calls:
            raise AssertionError("canonical native-call counts changed")
        if level == "instructions" and (not self.sites or self.sites != other.sites):
            raise AssertionError("logical site or edge counts changed")


def recurrence(n: int, initial: float) -> Input:
    value: float = initial
    wraps: int = 0
    for _ in range(n):
        if value < 4:
            value = value * 1.0000000001 + 0.125
        else:
            value = value * 0.9375 - 1
            wraps += 1
    return Input(
        f"recurrence-{n}",
        (str(n), str(initial)),
        ("armed", value, float(wraps)),
        hot=n == HOT_ITERATIONS,
    )


def ieee(name: str, n: int, numerator: float, denominator: float) -> Input:
    if denominator == 0:
        same_sign: bool = math.copysign(1, numerator) == math.copysign(1, denominator)
        quotient: float = (
            math.nan if numerator == 0 else math.inf if same_sign else -math.inf
        )
    else:
        quotient = numerator / denominator
    # Both signs of zero compare equal. The final sign is still observable.
    flags: tuple[bool, ...] = (
        quotient < 0,
        quotient <= 0,
        quotient > 0,
        quotient >= 0,
        quotient == 0,
        0 != quotient,
    )
    score: int = n * sum(1 << i for i, flag in enumerate(flags) if flag)
    return Input(
        name,
        (str(n), "-0", str(numerator), str(denominator)),
        (0.0 if n % 2 else -0.0, quotient, float(score)),
    )


def remainder(n: int, initial: float) -> Input:
    value: float = initial
    for _ in range(n):
        value = math.fmod(value * -1.75 + 0.5, 7.25)
    return Input("remainder", (str(n), str(initial)), (value,), native_eligible=False)


def nested(rounds: int, width: int, initial: float) -> Input:
    value: float = initial
    wraps: int = 0
    for _ in range(rounds):
        for _ in range(width):
            if value < 8:
                value = value * 1.0000000001 + 0.25
            else:
                value = (value - 8) * 0.875
                wraps += 1
    return Input(
        "nested",
        (str(rounds), str(width), str(initial)),
        (value, float(wraps), float(rounds * width)),
    )


def merge(n: int, initial: float) -> Input:
    value: float = initial
    for _ in range(n):
        value = value + (0.125 if value < 2 else -0.6875)
    return Input(
        f"merge-{n}", (str(n), str(initial)), (value,), hot=n == HOT_ITERATIONS
    )


def carry_pair(n: int) -> Input:
    first: int = 3
    second: int = -2
    for i in range(n):
        first, second = second, first + i + 3
    return Input(
        f"carry-pair-{n}",
        (str(n),),
        (float(first - 3 * second),),
        hot=n == HOT_ITERATIONS,
    )


def effect_value(n: int, initial: float) -> float:
    value: float = initial
    for i in range(n):
        value = (value + i) * 0.5 - 0.25
    return value


def effect_tail(n: int, initial: float) -> Input:
    return Input(
        f"effect-tail-{n}",
        (str(n), str(initial)),
        (effect_value(n, initial), float(n)),
        hot=n >= 64,
        logical_opcodes=(("SetMember", n),),
        requires_numeric_loop=False,
        tail_entries=max(0, n - 63),
    )


def member_tail(n: int, initial: float) -> Input:
    return Input(
        f"member-tail-{n}",
        (str(n), str(initial)),
        (effect_value(n, initial), float(max(0, n - 1))),
        hot=n >= 64,
        logical_opcodes=(("GetMember", n + 1), ("SetMember", n)),
        requires_numeric_loop=False,
        tail_entries=max(0, n - 63),
    )


def tail_reader(
    n: int, *, cell: bool = False, direct: bool = False, switch: bool = False
) -> Input:
    opcodes: tuple[tuple[str, int], ...] = () if direct else (("Call", n),)
    if switch:
        opcodes += (("LoadCell", 1),)
    numeric_visits: int = min(n, 81) if switch else n
    payload: tuple[str, ...] = tuple(f"item-{i}" for i in range(n))
    return Input(
        f"reader-{n}",
        (str(n), *payload, "distinct sentinel", "trailing poison"),
        ("opaque prefix", "distinct sentinel", effect_value(n, 7.0)),
        hot=n >= 64,
        native_eligible=not cell,
        logical_opcodes=opcodes,
        native_calls=(("io.read_line", n + 2),),
        requires_numeric_loop=False,
        tail_entries=max(0, numeric_visits - 63),
    )


def fixtures() -> tuple[Fixture, ...]:
    return (
        Fixture("recurrence", (recurrence(HOT_ITERATIONS, -1.125), recurrence(3, 2.5))),
        Fixture(
            "ieee",
            (
                ieee("unordered", HOT_ITERATIONS, 0.0, 0.0),
                ieee("negative-infinity", HOT_ITERATIONS + 1, 1.0, -0.0),
                ieee("signed-zero", HOT_ITERATIONS, 0.0, -2.0),
                ieee("positive-fraction", HOT_ITERATIONS + 1, 1.0, 2.0),
                ieee("subnormal", HOT_ITERATIONS, 5e-324, 1.0),
            ),
        ),
        Fixture("remainder", (remainder(HOT_ITERATIONS, -1.125),)),
        Fixture("nested", (nested(6_001, 19, -1.125),)),
        Fixture("merge", (merge(HOT_ITERATIONS, -1.125), merge(3, 2.25))),
        Fixture(
            "carry-pair", (carry_pair(HOT_ITERATIONS), carry_pair(3)), source=False
        ),
        Fixture("effect-tail", (effect_tail(137, 7.0), effect_tail(63, 7.0))),
        Fixture("member-tail", (member_tail(137, 7.0),)),
        Fixture("tail-call", (tail_reader(137),), source=False),
        Fixture("tail-cell", (tail_reader(137, cell=True),), source=False),
        Fixture("tail-switch", (tail_reader(137, switch=True),), source=False),
        Fixture("tail-native", (tail_reader(137, direct=True),), source=False),
    )


def environment(native: bool) -> dict[str, str]:
    env: dict[str, str] = os.environ.copy()
    env.pop("MSYNC_JIT", None)
    if not native:
        env["MSYNC_JIT"] = "off"
    return env


def command(binary: Path, arguments: Sequence[str]) -> list[str]:
    return [str(binary), "--no-config", *arguments]


def invoke(
    binary: Path,
    directory: Path,
    arguments: Sequence[str],
    *,
    data: bytes = b"",
    native: bool = True,
    search_path: Path | None = None,
) -> bytes:
    env: dict[str, str] = environment(native)
    if search_path is not None:
        env["PATH"] = str(search_path) + os.pathsep + env.get("PATH", os.defpath)
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        command(binary, arguments),
        cwd=directory,
        input=data,
        capture_output=True,
        timeout=30,
        check=False,
        env=env,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"{result.args!r} in {directory} exited {result.returncode}: "
            f"stdout={result.stdout!r}; stderr={result.stderr!r}"
        )
    return result.stdout


def counts(
    db: sqlite3.Connection, record: str, dimensions: Sequence[str]
) -> dict[CountKey, int]:
    grouped: dict[CountKey, int] = {}
    rows: list[tuple[int, int]] = db.execute(
        """SELECT r.id, m.value_int FROM records r JOIN metrics m ON r.id = m.record_id
           WHERE r.name = ? AND m.key = 'hits'""",
        (record,),
    ).fetchall()
    for record_id, value in rows:
        attributes: dict[str, str] = dict(
            db.execute(
                "SELECT key, value FROM attributes WHERE record_id = ?", (record_id,)
            )
        )
        key: CountKey = (record, *(attributes[dimension] for dimension in dimensions))
        grouped[key] = grouped.get(key, 0) + value
    return grouped


def profile(path: Path, level: Level) -> Profile:
    with closing(sqlite3.connect(path)) as db:
        exported: list[tuple[str, str | None]] = db.execute(
            "SELECT status, exporter_error FROM sessions"
        ).fetchall()
        statuses: list[tuple[str]] = db.execute(
            "SELECT status FROM spans WHERE name = 'execute'"
        ).fetchall()
        if exported != [("complete", None)] or len(statuses) != 1:
            raise AssertionError(
                f"incomplete execution/profile at {path}: {exported}, {statuses}"
            )
        metrics: dict[str, int] = dict(
            db.execute(
                """SELECT m.key, SUM(m.value_int) FROM metrics m
                   JOIN records r ON r.id = m.record_id
                   WHERE r.name = 'vm.function' GROUP BY m.key"""
            )
        )
        image_function: tuple[str, str] = ("image.id", "function.id")
        opcodes: dict[CountKey, int] = counts(
            db, "vm.opcode", (*image_function, "opcode")
        )
        sites: dict[CountKey, int] = (
            counts(db, "vm.instruction", (*image_function, "instruction.pc", "opcode"))
            | counts(
                db,
                "vm.edge",
                (*image_function, "instruction.pc", "target.pc", "opcode"),
            )
            if level == "instructions"
            else {}
        )
        native_calls: dict[str, int] = dict(
            db.execute(
                """SELECT a.value, SUM(m.value_int) FROM records r
               JOIN metrics m ON r.id = m.record_id
               JOIN attributes a ON r.id = a.record_id AND a.key = 'native.name'
               WHERE r.name = 'vm.native' AND m.key = 'calls' GROUP BY a.value"""
            )
        )
    return Profile(metrics, opcodes, sites, native_calls, statuses[0][0])


def check_route(
    binary: Path,
    directory: Path,
    arguments: Sequence[str],
    route: str,
    fixture: Fixture,
    native_expected: bool,
) -> dict[str, object]:
    receipt: dict[str, object] = {}
    for example in fixture.inputs:
        example.check(
            invoke(
                binary, directory, ["--no-telemetry", *arguments], data=example.stdin()
            )
        )
        metrics: dict[str, dict[str, int]] = {}
        levels: tuple[Level, ...] = (
            ("profile", "instructions")
            if example is fixture.inputs[0]
            else ("profile",)
        )
        for level in levels:
            profiles: dict[bool, Profile] = {}
            for native in (False, True):
                mode: str = "auto" if native else "off"
                database: Path = (
                    directory / f"{route}-{example.name}-{level}-{mode}.sqlite"
                )
                observed: bytes = invoke(
                    binary,
                    directory,
                    [
                        "--telemetry-db",
                        str(database),
                        "--telemetry-level",
                        level,
                        *arguments,
                    ],
                    data=example.stdin(),
                    native=native,
                )
                example.check(observed)
                result: Profile = profile(database, level)
                example.check_opcodes(result.opcodes)
                for name, expected in example.native_calls:
                    if result.native_calls.get(name) != expected:
                        raise AssertionError(
                            f"{example.name}: {name} expected {expected} calls, got {result.native_calls.get(name)}"
                        )
                if result.status != "ok":
                    raise AssertionError(
                        f"{database}: expected successful execute span, got {result.status}"
                    )
                result.check_native(
                    native
                    and native_expected
                    and example.hot
                    and example.native_eligible
                    and level == "profile",
                    example.tail_entries,
                )
                if (
                    example.hot
                    and example.requires_numeric_loop
                    and result.metrics.get("numeric_loop_entries", 0) == 0
                ):
                    raise AssertionError(
                        f"{database}: input never reached the numeric tier"
                    )
                profiles[native] = result
                metrics[f"{level}-{mode}"] = result.metrics
            profiles[True].check_logical(profiles[False], level)
        receipt[example.name] = metrics
    print(f"PASS {fixture.name}/{route}", flush=True)
    return receipt


def ready_line(pipe: int) -> bytes:
    deadline: float = time.monotonic() + 10
    prefix: bytes = b""
    with selectors.DefaultSelector() as selector:
        selector.register(pipe, selectors.EVENT_READ)
        while b"\n" not in prefix and time.monotonic() < deadline:
            if not selector.select(max(0.0, deadline - time.monotonic())):
                break
            chunk: bytes = os.read(pipe, 256)
            if not chunk:
                break
            prefix += chunk
    return prefix


def cancel(binary: Path, directory: Path, native_expected: bool) -> dict[str, object]:
    database: Path = directory / "cancel.sqlite"
    argv: list[str] = command(
        binary,
        [
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            "profile",
            "run",
            "program.mexe",
        ],
    )
    with subprocess.Popen(
        argv,
        cwd=directory,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=environment(True),
    ) as process:
        assert process.stdin is not None and process.stdout is not None
        try:
            process.stdin.write(b"1000000000000\n-1.125\n")
            process.stdin.flush()
            prefix: bytes = ready_line(process.stdout.fileno())
            if prefix != b"armed\n":
                raise AssertionError(f"long loop did not start: {prefix!r}")
            # The marker comes after compilation and input. Allow the loop to
            # promote; the flushed counters below prove it actually entered.
            time.sleep(0.25)
            start: float = time.perf_counter()
            process.send_signal(signal.SIGINT)
            stdout, stderr = process.communicate(timeout=10)
            elapsed_ms: float = (time.perf_counter() - start) * 1000
            if process.returncode != 130 or stdout:
                raise AssertionError(
                    f"unexpected cancellation: {process.returncode} {stdout!r} {stderr!r}"
                )
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()
    result: Profile = profile(database, "profile")
    if result.status != "cancelled":
        raise AssertionError(
            f"cancellation did not close the execution span: {result.status}"
        )
    result.check_native(native_expected)
    print("PASS recurrence/Linux SIGINT", flush=True)
    return {"cancel_to_exit_ms": elapsed_ms, "metrics": result.metrics}


def check_shebang(
    binary: Path, root: Path, example: Input, native_expected: bool
) -> dict[str, int]:
    directory: Path = root / "script"
    directory.mkdir()
    search_path: Path = directory / "bin"
    search_path.mkdir()
    (search_path / "msync").symlink_to(binary)
    script: Path = directory / "example.msync"
    script.write_bytes(
        b"#!/usr/bin/env msync\n" + (HERE / "recurrence.msync").read_bytes()
    )
    script.chmod(0o755)
    database: Path = directory / "shebang.sqlite"
    output: bytes = invoke(
        script,
        directory,
        ["--telemetry-db", str(database), "--telemetry-level", "profile"],
        data=example.stdin(),
        search_path=search_path,
    )
    example.check(output)
    result: Profile = profile(database, "profile")
    if result.status != "ok":
        raise AssertionError(f"shebang run did not complete: {result.status}")
    result.check_native(native_expected)
    print("PASS recurrence/Linux executable shebang", flush=True)
    return result.metrics


def generate_raw(artifact: Path, build: Path, fixture: str) -> None:
    command_line: list[str] = [
        "dotnet",
        "run",
        "--project",
        str(HERE / "ArtifactFixture"),
        "--configuration",
        "Release",
        "--artifacts-path",
        str(build),
        "-p:RestoreLockedMode=true",
        "--",
        str(artifact),
        fixture,
    ]
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        command_line, cwd=ROOT, capture_output=True, timeout=60, check=False
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"{command_line!r} exited {result.returncode}: "
            f"stdout={result.stdout!r}; stderr={result.stderr!r}"
        )


def check_fixture(
    binary: Path,
    root: Path,
    fixture: Fixture,
    native_expected: bool,
    raw_artifact: Path | None,
) -> dict[str, object]:
    directory: Path = root / fixture.name
    directory.mkdir()
    isolated: Path = directory / "artifact-only"
    isolated.mkdir()
    portable: Path = isolated / "program.mso"
    report: dict[str, object] = {}
    if fixture.source:
        source: Path = directory / "program.msync"
        source.write_bytes((HERE / f"{fixture.name}.msync").read_bytes())
        report["source"] = check_route(
            binary, directory, [source.name], "source", fixture, native_expected
        )
        invoke(
            binary,
            directory,
            ["--no-telemetry", "-O2", "compile", source.name, "-o", str(portable)],
        )
        source.unlink()
    elif raw_artifact is not None:
        portable.write_bytes(raw_artifact.read_bytes())
    else:
        generate_raw(portable, root / "generator-build", fixture.name)
    invoke(
        binary,
        isolated,
        ["--no-telemetry", "bind-ir", portable.name, "-o", "program.mexe"],
    )
    report["object"] = check_route(
        binary, isolated, ["run", portable.name], "object", fixture, native_expected
    )
    report["bound"] = check_route(
        binary, isolated, ["run", "program.mexe"], "bound", fixture, native_expected
    )
    if fixture.name == "recurrence" and platform.system() == "Linux":
        report["cancel"] = cancel(binary, isolated, native_expected)
        report["shebang"] = check_shebang(
            binary, directory, fixture.inputs[0], native_expected
        )
    return report


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--work", type=Path)
    parser.add_argument(
        "--raw-artifact",
        type=Path,
        help="Pre-generated selected raw fixture .mso (defaults to carry-pair)",
    )
    workload: tuple[Fixture, ...] = fixtures()
    parser.add_argument("--fixture", choices=[fixture.name for fixture in workload])
    parser.add_argument(
        "--expect-fallback",
        action="store_true",
        help="Expect no native activation even on a normally supported host",
    )
    args: argparse.Namespace = parser.parse_args()
    raw_artifact: Path | None = (
        args.raw_artifact.resolve(strict=True) if args.raw_artifact else None
    )
    binary: Path = args.binary.resolve(strict=True)
    temp: Path = ROOT / ".temp"
    temp.mkdir(exist_ok=True)
    work: Path = (
        args.work.resolve()
        if args.work
        else Path(tempfile.mkdtemp(prefix="native-jit-", dir=temp))
    )
    if not work.is_relative_to(temp) or work.exists() and any(work.iterdir()):
        parser.error(
            "--work must be a new or empty directory under repository-root .temp"
        )
    work.mkdir(parents=True, exist_ok=True)
    host: str = platform.system()
    machine: str = platform.machine().lower()
    native_expected: bool = not args.expect_fallback and (
        host in {"Linux", "Windows"}
        and machine in {"amd64", "x86_64"}
        or host == "Linux"
        and machine in {"aarch64", "arm64"}
    )
    results: dict[str, object] = {}
    report: dict[str, object] = {
        "binary": str(binary),
        "native_expected": native_expected,
        "status": "running",
        "fixtures": results,
    }
    try:
        for fixture in workload:
            if args.fixture is None or args.fixture == fixture.name:
                results[fixture.name] = check_fixture(
                    binary,
                    work,
                    fixture,
                    native_expected,
                    raw_artifact
                    if fixture.name == (args.fixture or "carry-pair")
                    else None,
                )
        report["status"] = "complete"
    except BaseException as error:
        report.update(status="failed", error=str(error))
        raise
    finally:
        receipt: Path = work / "result.json"
        receipt.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(receipt)


if __name__ == "__main__":
    main()
