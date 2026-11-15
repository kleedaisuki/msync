#!/usr/bin/env python3
"""Check Native mid-end transformations through real source and detached artifacts."""
from __future__ import annotations

import argparse
from collections import defaultdict
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = r'''
let R = $std::ranges;
fn snapshot(start: Number) {
    let value = start;
    fn change() { value = 7; 3 };
    let result = value + change();
    result * 10 + value
};
(snapshot(2) as String) -> ./snapshot.txt;
fn argument_write(value: Number) { value = value + 10; value };
let original = 2;
((original + argument_write(original)) as String) -> ./argument-result.txt;
(original as String) -> ./argument-source.txt;
fn common(flag: Bool, a: Number, b: Number) {
    let selected = 0;
    if flag { selected = a + b; } else { selected = a - b; };
    selected + (a * b) + (a * b)
};
let enabled = $std::fs::exists(./enabled);
(common(enabled, 3, 2) as String) -> ./common-yes.txt;
(common(not enabled, 3, 2) as String) -> ./common-no.txt;
fn sparse(flag: Bool) {
    let selected = 7;
    if flag { selected = 9; } else { selected = 9; };
    if selected == 9 { 42 } else { 0 }
};
(sparse(enabled) as String) -> ./sparse.txt;
fn loop_kernel(limit: Number, scale: Number, shift: Number) {
    let index = 0;
    let total = 0;
    loop {
        if index >= limit { break; };
        let invariant = scale * scale + shift;
        total = total + invariant + index;
        index = index + 1;
    };
    total
};
let runtime_scale = if enabled { 3 } else { 4 };
let runtime_shift = if enabled { 1 } else { 2 };
(loop_kernel(8, runtime_scale, runtime_shift) as String) -> ./loop.txt;
(loop_kernel(0, 3, 1) as String) -> ./loop-zero.txt;
fn invariant_branch(flag: Bool, limit: Number) {
    let index = 0;
    let total = 0;
    loop {
        if index >= limit { break; };
        if flag { total = total + index + 1; } else { total = total + 2; };
        index = index + 1;
    };
    total
};
(invariant_branch(enabled, 5) as String) -> ./unswitch-yes.txt;
(invariant_branch(not enabled, 5) as String) -> ./unswitch-no.txt;
fn finite(value: Number) {
    let total = 0;
    for item in [1, 2, 3, 4] { total = total + value * item; };
    total
};
(finite(3) as String) -> ./finite.txt;
let nested = 0;
for outer in [1, 2, 3] {
    for inner in [1, 2] { nested = nested + outer * 10 + inner; };
};
(nested as String) -> ./nested.txt;
let first = fn { 0 };
let second = fn { 0 };
let third = fn { 0 };
for item in [1, 2, 3] {
    if item == 1 { first = fn { item }; } else {
        if item == 2 { second = fn { item }; } else { third = fn { item }; };
    };
};
((first() + second() * 10 + third() * 100) as String) -> ./iteration-captures.txt;
fn local_fields(value: Number) {
    let row = scope { left: value, right: value + 1 };
    row::left = row::left + 3;
    row::left + row::right
};
(local_fields(4) as String) -> ./local-fields.txt;
fn escaped(value: Number) { scope { value: value } };
let left = escaped(1);
let right = escaped(2);
let alias = left;
alias::value = 9;
((left::value * 10 + right::value) as String) -> ./escaped-fields.txt;
fn sum(n: Number, acc: Number) -> Number {
    if n <= 0 { acc } else { sum(n - 1, acc + n) }
};
(sum(20, 0) as String) -> ./tail.txt;
fn unused(value: Number, ignored: Number) { value + 1 };
let evaluated = 0;
fn argument_effect() { evaluated = evaluated + 1; 99 };
(unused(8, argument_effect()) as String) -> ./unused-argument.txt;
(evaluated as String) -> ./argument-effects.txt;
let result_effects = 0;
fn ignored_result(value: Number) { result_effects = result_effects + value; value * value };
ignored_result(3);
(result_effects as String) -> ./ignored-result-effects.txt;
fn cancellation(value: Number) { (value + 9007199254740992) - 9007199254740992 };
((cancellation(1) == 0) as String) -> ./ieee-order.txt;
fn negative_zero(value: Number) { value * 0 };
((1 / negative_zero(-1) < 0) as String) -> ./negative-zero.txt;
fn signed_choice(flag: Bool) { if flag { -0 } else { 0 } };
((1 / signed_choice(enabled) < 0) as String) -> ./negative-phi.txt;
((1 / signed_choice(not enabled) > 0) as String) -> ./positive-phi.txt;
let nan = 0 / 0;
((nan != nan) as String) -> ./nan.txt;
fn self_equal(value: Number) { value == value };
(self_equal(nan) as String) -> ./nan-argument.txt;
fn zero_trip(limit: Number) {
    let total = 0;
    for index in R::iota(0, limit) {
        let invalid = "not-a-number" as Number;
        total = total + invalid + index;
    };
    total
};
(zero_trip(0) as String) -> ./zero-trip-cast.txt;
let order = "";
fn record(value: Number) {
    order = $std::string::concat(order, value as String);
    value * 2
};
let pipeline = R::map([1, 2, 3, 4], record);
let pipeline_total = 0;
for item in R::take(pipeline, 2) { pipeline_total = pipeline_total + item; };
order -> ./callback-order.txt;
(pipeline_total as String) -> ./callback-total.txt;
let produced = 0;
let consumed = 0;
let generated = R::generate(5, fn(index: Number) { produced = produced + 1; index });
for item in generated {
    if item == 1 { break; };
    consumed = consumed + 1;
};
((produced * 10 + consumed) as String) -> ./early-exit.txt;
'''
FAILURE_SOURCE: str = r'''
fn selected(limit: Number) {
    let total = 0;
    for index in $std::ranges::iota(0, limit) {
        let invalid = "not-a-number" as Number;
        total = total + invalid + index;
    };
    total
};
(selected(1) as String) -> ./should-not-publish.txt;
'''
EXPECTED: dict[str, bytes] = {
    'snapshot.txt': b'57', 'argument-result.txt': b'14', 'argument-source.txt': b'2',
    'common-yes.txt': b'17', 'common-no.txt': b'13', 'sparse.txt': b'42',
    'loop.txt': b'108', 'loop-zero.txt': b'0', 'unswitch-yes.txt': b'15',
    'unswitch-no.txt': b'10', 'finite.txt': b'30', 'nested.txt': b'129',
    'iteration-captures.txt': b'321', 'local-fields.txt': b'12', 'escaped-fields.txt': b'92',
    'tail.txt': b'210', 'unused-argument.txt': b'9', 'argument-effects.txt': b'1',
    'ignored-result-effects.txt': b'3', 'negative-phi.txt': b'true', 'positive-phi.txt': b'true',
    'nan-argument.txt': b'false', 'ieee-order.txt': b'true', 'negative-zero.txt': b'true', 'nan.txt': b'true',
    'zero-trip-cast.txt': b'0', 'callback-order.txt': b'12', 'callback-total.txt': b'6',
    'early-exit.txt': b'21',
}


@dataclass(frozen=True)
class RuntimeCounts:
    instructions: int
    calls: int
    allocations: int
    opcodes: dict[str, int]


def invoke(binary: Path, work: Path, *arguments: str, recording: bool = False, success: bool = True) -> subprocess.CompletedProcess[str]:
    command: list[str] = [str(binary), '--no-config', *([] if recording else ['--no-telemetry']), *arguments]
    result: subprocess.CompletedProcess[str] = subprocess.run(
        command, cwd=work, env={**os.environ, 'NO_COLOR': '1'}, capture_output=True,
        text=True, encoding='utf-8', timeout=120,
    )
    if success and result.returncode:
        raise RuntimeError(f'{arguments}: exit {result.returncode}\n{result.stdout}\n{result.stderr}')
    if not success and result.returncode == 0:
        raise AssertionError(f'Expected runtime failure for {arguments}')
    return result


def oracle(work: Path) -> None:
    for name, expected in EXPECTED.items():
        path: Path = work / name
        actual: bytes = path.read_bytes()
        if actual != expected:
            raise AssertionError(f'{work.name}/{name}: expected {expected!r}, received {actual!r}')
        path.unlink()


def read_trace(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines()]


def runtime_counts(rows: list[dict[str, Any]]) -> RuntimeCounts:
    functions: list[dict[str, Any]] = [row for row in rows if row.get('name') == 'vm.function']
    opcodes: dict[str, int] = defaultdict(int)
    for row in rows:
        if row.get('name') == 'vm.opcode':
            opcodes[row['attributes']['opcode']] += int(row['metrics']['hits'])
    return RuntimeCounts(
        sum(int(row['metrics']['instructions']) for row in functions),
        sum(int(row['metrics']['calls']) for row in functions),
        sum(int(row['metrics']['logical_allocations']) for row in functions),
        dict(sorted(opcodes.items())),
    )


def source_operation_hits(rows: list[dict[str, Any]], expression: str, opcode: str) -> int:
    start: int = SOURCE.index(expression)
    end: int = start + len(expression)
    return sum(int(row['metrics']['hits']) for row in rows
               if row.get('name') == 'vm.instruction'
               and row.get('attributes', {}).get('opcode') == opcode
               and str(row['attributes'].get('source.file', '')).endswith('midend.msync')
               and int(row['attributes'].get('source.offset', -1)) >= start
               and int(row['attributes'].get('source.offset', -1))
               + int(row['attributes'].get('source.length', 0)) <= end)


def metric_totals(rows: list[dict[str, Any]]) -> dict[str, int]:
    totals: dict[str, int] = defaultdict(int)
    for row in rows:
        phase: str = str(row.get('name', ''))
        if row.get('kind') == 'span' and phase.startswith(('optimizer.pass.llvm-', 'optimizer.analysis.llvm-')):
            owner: str = phase.removeprefix('optimizer.').replace('.llvm-', '.', 1)
            for name, value in row.get('metrics', {}).items():
                totals[f'llvm.{owner}.{name}'] += int(value)
            continue
        if row.get('kind') == 'span' and str(row.get('name', '')).startswith('optimizer.'):
            for name, value in row.get('metrics', {}).items():
                if name.startswith(('scalar.', 'loop.', 'loops.', 'licm.', 'ipa.', 'aggregate.', 'memory.')):
                    totals[name] += int(value)
    return dict(sorted(totals.items()))


def failure_oracle(result: subprocess.CompletedProcess[str], work: Path) -> None:
    if 'cast' not in result.stderr.casefold() or 'failure.msync' not in result.stderr:
        raise AssertionError(f'Lost cast/source failure context: {result.stderr}')
    if (work / 'should-not-publish.txt').exists():
        raise AssertionError('A transfer dependent on the failing cast published output')


def run_failure_level(binary: Path, work: Path, level: int) -> list[str]:
    work.mkdir(parents=True, exist_ok=True)
    source: Path = work / 'failure.msync'
    source.write_text(FAILURE_SOURCE, encoding='utf-8')
    hir: Path = work / 'failure.mhir'
    obj: Path = work / 'failure.mso'
    executable: Path = work / 'failure.mexe'
    option: str = f'-O{level}'
    invoke(binary, work, 'compile', str(source), '--emit', 'hir', '-o', str(hir))
    failure_oracle(invoke(binary, work, 'run', str(source), option, success=False), work)
    source.unlink()
    invoke(binary, work, 'compile', str(hir), option, '-o', str(obj))
    failure_oracle(invoke(binary, work, 'run-ir', str(obj), success=False), work)
    invoke(binary, work, 'bind-ir', str(obj), '-o', str(executable))
    obj.unlink()
    hir.unlink()
    failure_oracle(invoke(binary, work, 'run-ir', str(executable), success=False), work)
    return ['source', 'detached-hir-object', 'detached-bound']


def run_level(binary: Path, work: Path, level: int) -> dict[str, Any]:
    work.mkdir(parents=True, exist_ok=True)
    source: Path = work / 'midend.msync'
    source.write_text(SOURCE, encoding='utf-8')
    (work / 'enabled').write_bytes(b'present')
    level_option: str = f'-O{level}'
    routes: list[str] = []
    invoke(binary, work, 'run', str(source), level_option)
    oracle(work)
    routes.append('source')
    hir: Path = work / 'midend.mhir'
    invoke(binary, work, 'compile', str(source), '--emit', 'hir', '-o', str(hir))
    (work / 'source.hidden').write_text(SOURCE, encoding='utf-8')
    source.unlink()
    obj: Path = work / 'midend.mso'
    compile_trace: Path = work / 'compile.jsonl'
    compile_trace.unlink(missing_ok=True)
    invoke(binary, work, '--trace', str(compile_trace), '--telemetry-level', 'profile',
           'compile', str(hir), level_option, '-o', str(obj), recording=True)
    disassembly: str = invoke(binary, work, 'disassemble', str(obj)).stdout
    (work / 'object.txt').write_text(disassembly, encoding='utf-8')
    runtime_trace: Path = work / 'runtime.jsonl'
    runtime_trace.unlink(missing_ok=True)
    invoke(binary, work, '--trace', str(runtime_trace), '--telemetry-level', 'instructions',
           'run-ir', str(obj), recording=True)
    oracle(work)
    routes.append('detached-hir-object')
    runtime_rows: list[dict[str, Any]] = read_trace(runtime_trace)
    counts: RuntimeCounts = runtime_counts(runtime_rows)
    if counts.instructions == 0:
        raise AssertionError('Instruction recording produced no Native execution counters')
    executable: Path = work / 'midend.mexe'
    invoke(binary, work, 'bind-ir', str(obj), '-o', str(executable))
    obj.unlink()
    hir.unlink()
    invoke(binary, work, 'run-ir', str(executable))
    oracle(work)
    routes.append('detached-bound')
    return {
        'level': level, 'routes': routes,
        'selected_failure_routes': run_failure_level(binary, work / 'failure', level),
        'instructions': counts.instructions,
        'calls': counts.calls, 'logical_allocations': counts.allocations,
        'opcodes': counts.opcodes,
        'kernel_binary_hits': source_operation_hits(runtime_rows, 'scale * scale + shift', 'Binary'),
        'optimizer_metrics': metric_totals(read_trace(compile_trace)),
        'disassembly_sha256': hashlib.sha256(disassembly.encode('utf-8')).hexdigest(),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', required=True, type=Path)
    parser.add_argument('--work', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--levels', nargs='+', type=int, default=[0, 2, 3], choices=(0, 1, 2, 3))
    parser.add_argument('--require-metric', action='append', default=[])
    parser.add_argument('--require-improvement', action='store_true')
    parser.add_argument('--require-kernel-improvement', action='store_true')
    options: argparse.Namespace = parser.parse_args()
    if (options.require_improvement or options.require_kernel_improvement) and (
            0 not in options.levels or not any(level >= 2 for level in options.levels)):
        parser.error('Improvement checks require O0 and at least one of O2/O3')
    binary: Path = options.binary.resolve()
    temporary: tempfile.TemporaryDirectory[str] | None = None
    if options.work is None:
        scratch: Path = ROOT / '.temp/native-midend'
        scratch.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix='journey-', dir=scratch)
        work: Path = Path(temporary.name)
    else:
        work = options.work.resolve()
        work.mkdir(parents=True, exist_ok=True)
    try:
        results: list[dict[str, Any]] = [run_level(binary, work / f'O{level}', level) for level in options.levels]
        totals: dict[str, int] = defaultdict(int)
        for result in results:
            if result['level'] >= 2:
                for name, value in result['optimizer_metrics'].items():
                    totals[name] += int(value)
        for name in options.require_metric:
            if totals.get(name, 0) <= 0:
                raise AssertionError(f'No actual rewrite recorded for required metric {name!r}: {dict(totals)}')
        if options.require_improvement:
            baseline: dict[str, Any] = next(row for row in results if row['level'] == 0)
            for result in results:
                if result['level'] >= 2 and result['instructions'] >= baseline['instructions']:
                    raise AssertionError(f'O{result["level"]} executed {result["instructions"]} instructions, O0 {baseline["instructions"]}')
        if options.require_kernel_improvement:
            baseline = next(row for row in results if row['level'] == 0)
            for result in results:
                if result['level'] >= 2 and result['kernel_binary_hits'] >= baseline['kernel_binary_hits']:
                    raise AssertionError(f'O{result["level"]} invariant arithmetic hits {result["kernel_binary_hits"]}, '
                                         f'O0 {baseline["kernel_binary_hits"]}')
        report: dict[str, Any] = {
            'status': 'passed', 'binary': str(binary),
            'binary_sha256': hashlib.sha256(binary.read_bytes()).hexdigest(),
            'managed_components': {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                                   for path in sorted(binary.parent.glob('Msync.*.dll'))},
            'source_sha256': hashlib.sha256(SOURCE.encode('utf-8')).hexdigest(),
            'failure_source_sha256': hashlib.sha256(FAILURE_SOURCE.encode('utf-8')).hexdigest(),
            'files_per_route': len(EXPECTED), 'source_removed_before_object': True,
            'hir_and_object_removed_before_bound': True, 'levels': results,
            'required_metrics': options.require_metric,
        }
        rendered: str = json.dumps(report, indent=2) + '\n'
        if options.output:
            options.output.parent.mkdir(parents=True, exist_ok=True)
            options.output.write_text(rendered, encoding='utf-8')
        print(rendered, end='')
    finally:
        if temporary is not None:
            temporary.cleanup()


if __name__ == '__main__':
    main()
