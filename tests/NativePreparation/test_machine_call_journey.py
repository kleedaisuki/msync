"""Exercise native-to-native calls and field spans through bound executables."""

from __future__ import annotations

import argparse
import json
import os
import platform
import sqlite3
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """
fn kernel(a: Number, b: Number) -> Number {
    let x = a * 2 + b * 3;
    let y = (a + b) * (b - a);
    let z = (x + y) * (x - y);
    z + a * 4 + b
};
fn eligible(a: Number, b: Number) -> Bool {
    (a + 1) * (b + 2) < 1286 * (b + 4)
};
fn run(limit: Number) {
    let index = 0;
    let total = 0;
    loop {
        if index >= limit { break total; };
        if eligible(index, 3) { total = total + kernel(index, 3); };
        index = index + 1;
    }
};
let text? = $std::io::read_line();
let limit = if text? { 0 } else { text as Number };
$std::io::println(run(limit) as String);
"""
CAPTURE_SOURCE: str = """
fn discarded(seed: Number) {
    let first = scope { value: seed, next?: null };
    let second = scope { value: seed + 1, next?: null };
    first::next = second; second::next = first;
    first
};
fn churn() {
    let i = 0;
    let total = 0;
    loop {
        if i >= 10000 { break total; };
        let node = discarded(i);
        total = total + node::value;
        i = i + 1;
    }
};
fn counter(seed: Number) {
    let value = seed;
    fn bump(delta: Number) {
        global value;
        value = (value + delta) % 131071;
    };
    fn peek() -> Number { value };
    scope { bump: bump, peek: peek }
};
fn run(limit: Number) {
    let left = counter(-7);
    let alias = left;
    let right = counter(13);
    let i = 0;
    let total = churn();
    loop {
        if i >= limit { break total; };
        if i == 1000 { alias::bump = right::bump; };
        alias::bump(-1);
        right::bump(2);
        total = total + 2 * left::peek() + right::peek();
        i = i + 1;
    }
};
$std::io::println(run(2000) as String);
"""
CLOSURE_REUSE_SOURCE: str = """
fn capture(seed: Number) {
    let value = seed;
    fn { value }
};
fn churn(limit: Number) {
    let cell = scope { read: capture(0) };
    let anchor = capture(7);
    let i = 0;
    let total = 0;
    loop {
        if i >= limit { break; };
        cell::read = capture(i);
        total = total + cell::read();
        i = i + 1;
    };
    total + anchor()
};
$std::io::println(churn(40000) as String);
"""
FIELD_SOURCE: str = """
fn make(seed: Number) {
    let left = scope { prefix: "label", value: seed, keep: true, next?: null };
    let right = scope { prefix: "label", value: seed + 3, keep: false, next?: null };
    left::next = right;
    right::next = left;
    left
};
fn churn(limit: Number) {
    let left = make(7);
    let alias = left;
    let right = left::next;
    let i = 0;
    let total = 0;
    loop {
        if i >= limit { break; };
        alias::value = alias::value + 1;
        left::keep = if left::keep { false } else { true };
        if alias::keep { total = total + left::value; };
        right::value = right::value + 2;
        total = total + right::value;
        i = i + 1;
    };
    total + alias::next::next::value
};
$std::io::println(churn(40000) as String);
"""
STACK_SOURCE: str = """
fn dive(n: Number) { if n == 0 { return 0; }; dive(n - 1) + 1 };
fn swap(n: Number, a: Number, b: Number) {
    if n == 0 { return a * 100 + b; }; swap(n - 1, b, a)
};
$std::io::println((dive(20000) + swap(100001, 7, 13)) as String);
"""


def expected(limit: int) -> int:
    return sum(
        (2 * a + 9) ** 2 - (9 - a * a) ** 2 + 4 * a + 3
        for a in range(limit)
        if 5 * (a + 1) < 7 * 1286
    )


def bind(binary: Path, work: Path, source: str, optimization: int = 2) -> list[str]:
    work.mkdir()
    (work / "program.msync").write_text(source, encoding="utf-8")
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    preparation: list[list[str]] = [
        ["compile", f"-O{optimization}", "program.msync", "-o", "program.mso"],
        ["bind-ir", "program.mso", "-o", "program.mexe"],
    ]
    for args in preparation:
        subprocess.run(
            [*cli, "--no-telemetry", *args], cwd=work, capture_output=True, check=True
        )
    (work / "program.msync").unlink()
    (work / "program.mso").unlink()
    return cli


def run(
    cli: list[str],
    work: Path,
    policy: str,
    limit: int,
    output: int,
    telemetry_level: str = "instructions",
) -> Path:
    database: Path = work / f"{policy}.sqlite"
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [
            *cli,
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            telemetry_level,
            "run",
            "program.mexe",
        ],
        cwd=work,
        env=dict(os.environ, MSYNC_JIT=policy),
        input=f"{limit}\n",
        text=True,
        capture_output=True,
        check=True,
        timeout=30,
    )
    assert result.stdout == f"{output}\n" and not result.stderr, result
    return database


def supports_x64_tier() -> bool:
    return platform.system() in ("Linux", "Windows") and platform.machine().lower() in ("x86_64", "amd64")


def journey(binary: Path, work: Path) -> dict[str, object]:
    cli: list[str] = bind(binary, work, SOURCE)
    receipt: dict[str, object] = {}
    limit: int = 2000
    for policy in ("off", "auto", "force"):
        db_path: Path = run(cli, work, policy, limit, expected(limit))
        with sqlite3.connect(db_path) as db:
            metrics: dict[str, int] = dict(
                db.execute("""
                    SELECT key, sum(value_int) FROM metrics m JOIN records r ON r.id=m.record_id
                    WHERE key IN ('tier.machine_guest_calls', 'tier.bridge_invocations',
                        'jit_machine_call_sites', 'jit_machine_guest_calls') GROUP BY key
                """)
            )
            targets: dict[str, int] = dict(
                db.execute("""
                    SELECT a.value, sum(m.value_int) FROM records r
                    JOIN attributes a ON a.record_id=r.id AND a.key='target.function.name'
                    JOIN metrics m ON m.record_id=r.id AND m.key='hits'
                    WHERE r.name='vm.call' AND a.value IN ('eligible', 'kernel') GROUP BY a.value
                """)
            )
        assert targets == {"eligible": limit, "kernel": 1800}, (policy, targets)
        machine: int = metrics["tier.machine_guest_calls"]
        if supports_x64_tier() and policy != "off":
            assert metrics["jit_machine_call_sites"] == 2, metrics
            assert machine >= 3500 and metrics["jit_machine_guest_calls"] == machine
            assert metrics["tier.bridge_invocations"] == 0, metrics
        else:
            assert machine == 0, metrics
        receipt[policy] = metrics
    return receipt


def capture_journey(binary: Path, work: Path) -> dict[str, object]:
    cli: list[str] = bind(binary, work, CAPTURE_SOURCE)
    limit: int = 2000
    want: int = sum(range(10000)) + sum(
        -1 if i <= 1000 else i - 1001 for i in range(1, limit + 1)
    )
    receipt: dict[str, object] = {}
    for policy in ("off", "auto", "force"):
        db_path: Path = run(cli, work, policy, limit, want, "diagnostics")
        with sqlite3.connect(db_path) as db:
            metrics: dict[str, int] = dict(
                db.execute("""
                    SELECT key, sum(value_int) FROM metrics
                    WHERE key IN ('jit_machine_closure_calls', 'jit_machine_field_calls',
                        'native_heap_callable_bytes', 'native_heap_capture_vector_locations',
                        'native_heap_environment_views_created', 'heap.raw_environment_edges',
                        'heap.objects.reclaimed') GROUP BY key
                """)
            )
        assert metrics.get("heap.objects.reclaimed", 0) > 15000, metrics
        if sys.maxsize > 2**32:
            assert metrics["native_heap_environment_views_created"] > 0, metrics
            assert metrics["heap.raw_environment_edges"] > 0, metrics
        if supports_x64_tier() and policy != "off":
            assert metrics["jit_machine_closure_calls"] > 7000, metrics
            assert metrics["jit_machine_field_calls"] > 3000, metrics
            assert metrics["native_heap_callable_bytes"] > 0, metrics
            assert metrics["native_heap_capture_vector_locations"] >= 4, metrics
        else:
            assert metrics["jit_machine_closure_calls"] == 0, metrics
        receipt[policy] = metrics
    return receipt


def stack_journey(binary: Path, work: Path) -> dict[str, object]:
    cli: list[str] = bind(binary, work, STACK_SOURCE, optimization=0)
    receipt: dict[str, object] = {}
    for policy in ("off", "force"):
        db_path: Path = run(cli, work, policy, 0, 21307)
        with sqlite3.connect(db_path) as db:
            metrics: dict[str, int] = dict(
                db.execute("""
                    SELECT key, sum(value_int) FROM metrics
                    WHERE key IN ('guest_call_pushes', 'guest_call_resumes', 'stack_segment_allocations',
                        'tail_argument_buffers', 'tail_argument_buffer_reuses', 'native_heap_objects')
                    GROUP BY key
                """)
            )
        assert metrics["stack_segment_allocations"] > 1, metrics
        assert metrics["guest_call_pushes"] == metrics["guest_call_resumes"] > 10000, (
            metrics
        )
        assert metrics["tail_argument_buffers"] == 2, metrics
        assert metrics["tail_argument_buffer_reuses"] > 99000, metrics
        assert metrics["native_heap_objects"] >= metrics["tail_argument_buffers"], (
            metrics
        )
        receipt[policy] = metrics
    return receipt


def closure_reuse_journey(binary: Path, work: Path) -> dict[str, object]:
    cli: list[str] = bind(binary, work, CLOSURE_REUSE_SOURCE, optimization=0)
    receipt: dict[str, object] = {}
    count: int = 40000
    for policy in ("off", "force"):
        database: Path = run(cli, work, policy, 0, count * (count - 1) // 2 + 7, "diagnostics")
        with sqlite3.connect(database) as db:
            metrics: dict[str, int] = dict(db.execute("""
                SELECT key, sum(value_int) FROM metrics
                WHERE key IN ('native_heap_environment_views_created', 'native_heap_environment_views_reused',
                    'heap.raw_environment_edges', 'heap.objects.reclaimed') GROUP BY key
            """))
        assert metrics["heap.objects.reclaimed"] > count, metrics
        if sys.maxsize > 2**32:
            allocated: int = metrics["native_heap_environment_views_created"]
            reused: int = metrics["native_heap_environment_views_reused"]
            assert allocated > 0 and reused > allocated and reused + allocated >= count, metrics
            assert metrics["heap.raw_environment_edges"] > 0, metrics
        receipt[policy] = metrics
    return receipt


def field_journey(binary: Path, work: Path) -> dict[str, object]:
    cli: list[str] = bind(binary, work, FIELD_SOURCE)
    limit: int = 40000
    answer: int = sum(10 + 2 * i for i in range(1, limit + 1))
    answer += sum(7 + i for i in range(2, limit + 1, 2)) + 7 + limit
    receipt: dict[str, object] = {}
    for policy in ("off", "auto", "force"):
        database: Path = run(cli, work, policy, 0, answer, "diagnostics")
        with sqlite3.connect(database) as db:
            fields: dict[str, int] = dict(db.execute("""
                SELECT m.key, sum(m.value_int) FROM metrics m JOIN records r ON m.record_id=r.id
                JOIN attributes a ON a.record_id = r.id AND a.key='function.name' AND a.value='churn'
                WHERE r.name='vm.function' AND m.key LIKE 'jit_guest_%' GROUP BY m.key
            """))
            bridges: int = db.execute("""
                SELECT m.value_int FROM metrics m JOIN records r ON m.record_id=r.id
                WHERE r.name='execute' AND m.key='tier.bridge_invocations'
            """).fetchone()[0]
        if supports_x64_tier() and policy != "off":
            assert fields["jit_guest_load_sites"] >= 3 and fields["jit_guest_store_sites"] >= 3, fields
            assert fields["jit_guest_loads"] > 2 * limit and fields["jit_guest_stores"] > 2 * limit, fields
            assert bridges < 10, bridges
        else:
            assert fields["jit_guest_loads"] == fields["jit_guest_stores"] == 0, fields
        receipt[policy] = fields | {"tier.bridge_invocations": bridges}
    return receipt


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    temporary: Path = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="native-machine-call-", dir=temporary
    ) as path:
        receipt: dict[str, object] = {
            "scalars": journey(args.binary.resolve(), Path(path) / "scalars"),
            "captures": capture_journey(args.binary.resolve(), Path(path) / "captures"),
            "closure_reuse": closure_reuse_journey(args.binary.resolve(), Path(path) / "closure-reuse"),
            "fields": field_journey(args.binary.resolve(), Path(path) / "fields"),
            "stack": stack_journey(args.binary.resolve(), Path(path) / "stack"),
        }
    if args.output:
        args.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(
        "PASS: machine calls, closure lifetimes, scalar field spans, and native stack/tail storage"
    )


if __name__ == "__main__":
    main()
