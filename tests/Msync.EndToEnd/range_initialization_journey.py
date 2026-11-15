#!/usr/bin/env python3
"""Exercise eager Range sources, lazy callbacks and shared HIR bodies through real artifacts."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = r'''
let R = $std::ranges;
let x = 1;
let direct = [x];
let computed = [x + 0];
x = 2;
for n in direct { (n as String) -> ./direct.txt; };
for n in computed { (n as String) -> ./computed.txt; };
let order = "";
fn mark(value: String) { order = $std::string::concat(order, value); value };
let initialized = [mark("a"), mark("b")];
order -> ./order.txt;
let calls = 0;
fn make(value: Number) { calls = calls + 1; [scope { value: value }] };
let made = make(x);
(calls as String) -> ./calls.txt;
x = 10;
let row? = R::first(made);
if row is not null { (row::value as String) -> ./argument.txt; row::value = 99; };
let again? = R::first(made);
if again is not null { (again::value as String) -> ./identity.txt; };
let map_calls = 0;
fn project(value: Number) { map_calls = map_calls + 1; value + x };
let mapped = R::map([1, 2], project);
(map_calls as String) -> ./map-before.txt;
(R::size(R::take(mapped, 0)) as String) -> ./zero.txt;
(map_calls as String) -> ./map-zero.txt;
let first? = R::first(mapped);
if first is not null { (first as String) -> ./mapped.txt; };
(map_calls as String) -> ./map-after.txt;
let generated_calls = 0;
let generated = R::generate(2, fn(index: Number) { generated_calls = generated_calls + 1; index + x });
(generated_calls as String) -> ./generate-before.txt;
(R::size(R::take(generated, 0)) as String) -> ./generate-zero.txt;
(generated_calls as String) -> ./generate-after-zero.txt;
let generated_first? = R::first(generated);
if generated_first is not null { (generated_first as String) -> ./generated.txt; };
(generated_calls as String) -> ./generate-after.txt;
let indices = "";
for index in R::generate(4, fn(index: Number) { index }) {
    indices = $std::string::concat(indices, index as String);
};
indices -> ./generated-indices.txt;
let action = fn { 1 };
fn install(flag: Bool) { if flag { action = fn { 9 }; }; };
install(false);
(action() as String) -> ./unchanged-first.txt;
action = fn { 2 };
install(false);
(action() as String) -> ./unchanged-second.txt;
install(true);
(action() as String) -> ./installed.txt;
fn replace(flag: Bool) {
    if flag { action = fn { 7 }; return; };
    action = fn { 8 };
};
replace(true);
(action() as String) -> ./early-return.txt;
replace(false);
(action() as String) -> ./fallthrough.txt;
fn choose(flag: Bool) {
    loop {
        if flag { action = fn { 5 }; break; };
        action = fn { 6 }; break;
    };
};
choose(true);
(action() as String) -> ./first-break.txt;
choose(false);
(action() as String) -> ./second-break.txt;
fn local_choice(flag: Bool) {
    let selected = fn { 0 };
    loop {
        if flag { selected = fn { 3 }; break; };
        selected = fn { 4 }; break;
    };
    selected()
};
(local_choice(true) as String) -> ./local-first.txt;
(local_choice(false) as String) -> ./local-second.txt;
fn touch(item) { item::value = item::value + 1; item };
let left = scope { value: 1 };
let right = scope { value: 10 };
let left_alias = touch(left);
let right_alias = touch(right);
left_alias::value = 20;
(left::value as String) -> ./left-alias.txt;
(right_alias::value as String) -> ./right-alias.txt;
let nested = [[scope { value: x + 1 }]];
for inner in nested { for item in inner { (item::value as String) -> ./nested.txt; }; };
let element_calls = 0;
fn stored_element() { element_calls = element_calls + 1; x };
let functions = [stored_element];
(element_calls as String) -> ./function-before.txt;
for element in functions { (element_calls as String) -> ./function-enumerated.txt; };
for element in functions { (element() as String) -> ./function-result.txt; };
(element_calls as String) -> ./function-after.txt;
'''
EXPECTED: dict[str, bytes] = {
    'direct.txt': b'1', 'computed.txt': b'1', 'order.txt': b'ab', 'calls.txt': b'1',
    'argument.txt': b'2', 'identity.txt': b'99', 'map-before.txt': b'0', 'zero.txt': b'0',
    'map-zero.txt': b'0', 'mapped.txt': b'11', 'map-after.txt': b'1',
    'generate-before.txt': b'0', 'generate-zero.txt': b'0', 'generate-after-zero.txt': b'0',
    'generated.txt': b'10', 'generate-after.txt': b'1', 'generated-indices.txt': b'0123', 'nested.txt': b'11',
    'unchanged-first.txt': b'1', 'unchanged-second.txt': b'2', 'installed.txt': b'9',
    'early-return.txt': b'7', 'fallthrough.txt': b'8',
    'first-break.txt': b'5', 'second-break.txt': b'6',
    'local-first.txt': b'3', 'local-second.txt': b'4',
    'left-alias.txt': b'20', 'right-alias.txt': b'11',
    'function-before.txt': b'0', 'function-enumerated.txt': b'0',
    'function-result.txt': b'10', 'function-after.txt': b'1',
}


def invoke(binary: Path, directory: Path, *arguments: str, recording: bool = False) -> subprocess.CompletedProcess[str]:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), '--no-config', *([] if recording else ['--no-telemetry']), *arguments], cwd=directory,
        env={**os.environ, 'NO_COLOR': '1'}, capture_output=True, text=True, timeout=120,
    )
    if result.returncode:
        raise RuntimeError(f'{arguments}: exit {result.returncode}\n{result.stdout}\n{result.stderr}')
    return result


def oracle(directory: Path) -> None:
    for filename, expected in EXPECTED.items():
        actual: bytes = (directory / filename).read_bytes()
        if actual != expected:
            raise AssertionError(f'{directory.name}/{filename}: {actual!r} != {expected!r}')
        (directory / filename).unlink()


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', required=True, type=Path)
    parser.add_argument('--output', type=Path)
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve()
    scratch: Path = ROOT / '.temp/range-initialization'
    scratch.mkdir(parents=True, exist_ok=True)
    routes: list[dict[str, Any]] = []
    with tempfile.TemporaryDirectory(dir=scratch) as temporary:
        directory: Path = Path(temporary)
        source: Path = directory / 'source.msync'
        source.write_text(SOURCE)
        hir: Path = directory / 'source.mhir'
        trace: Path = directory / 'compile.jsonl'
        invoke(binary, directory, '--trace', str(trace), '--telemetry-level', 'profile',
               'compile', str(source), '--emit', 'hir', '-o', str(hir), recording=True)
        records: list[dict[str, Any]] = [json.loads(line) for line in trace.read_text().splitlines()]
        for name in ('install', 'replace', 'choose', 'local_choice', 'touch'):
            templates: set[str] = {row['attributes']['template.id'] for row in records
                                   if row['name'] == 'compiler.semantic.instantiate'
                                   and row['attributes'].get('template.name') == name}
            definitions: list[dict[str, Any]] = [row for row in records
                                                if row['name'] == 'compiler.semantic.specialization'
                                                and row['attributes']['template.id'] in templates
                                                and int(row['attributes']['function.id']) >= 0]
            if len(definitions) != 1:
                raise AssertionError(f'{name} generated {len(definitions)} bodies instead of one')
        if any(row['name'] in {'compiler.hir.share_functions', 'compiler.lower.fold_functions'} for row in records):
            raise AssertionError('Post-generation body merging survived')
        hir_text: str = invoke(binary, directory, 'disassemble', str(hir)).stdout
        if '<range-item>' in hir_text or '<range-call>' in hir_text:
            raise AssertionError('Implicit delayed function survived in HIR')
        for level in (0, 2):
            level_option: str = f'-O{level}'
            invoke(binary, directory, 'run', str(source), level_option)
            oracle(directory)
            routes.append({'route': 'source', 'level': level})
            obj: Path = directory / f'object-{level}.mso'
            executable: Path = directory / f'bound-{level}.mexe'
            invoke(binary, directory, 'compile', str(hir), level_option, '-o', str(obj))
            text: str = invoke(binary, directory, 'disassemble', str(obj)).stdout
            if 'MakeLazyRange' in text or 'DeferRange' in text:
                raise AssertionError('Retired Native opcode survived')
            invoke(binary, directory, 'run-ir', str(obj))
            oracle(directory)
            routes.append({'route': 'hir-to-object', 'level': level})
            invoke(binary, directory, 'bind-ir', str(obj), '-o', str(executable))
            invoke(binary, directory, 'run-ir', str(executable))
            oracle(directory)
            routes.append({'route': 'executable', 'level': level})
    result: dict[str, Any] = {'status': 'passed', 'routes': routes, 'files_per_route': len(EXPECTED)}
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(json.dumps(result, indent=2))
    print(json.dumps(result))


if __name__ == '__main__':
    main()
