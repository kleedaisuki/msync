"""Exercise the compiled storage contract through HIR, object and bound artifacts."""
from __future__ import annotations

import argparse
import os
import subprocess
from pathlib import Path

SOURCE: str = '''let row = object { n: 7 };
fn increment(value: Number) { value + 1 };
if $std::fs::exists(./input) { "ready" -> ./output; };
$std::io::println(increment(row::n) as String)
'''


def invoke(binary: Path, work: Path, arguments: list[str], *, policy: str = 'auto', success: bool = True) -> subprocess.CompletedProcess[bytes]:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), '--no-config', '--no-telemetry', *arguments], cwd=work,
        env=dict(os.environ, MSYNC_JIT=policy), input=b'', capture_output=True, check=False, timeout=60)
    if (result.returncode == 0) != success:
        raise AssertionError(f'{result.args!r}: exit={result.returncode}; stdout={result.stdout!r}; stderr={result.stderr!r}')
    return result


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    work: Path = args.work.resolve()
    work.mkdir(parents=True, exist_ok=True)
    (work / 'input').write_text('exists\n')
    source: Path = work / 'case.msync'
    source.write_text(SOURCE)
    invoke(binary, work, ['compile', str(source), '--emit', 'hir', '-o', 'case.mhir'])
    source.unlink()
    old_hir: bytearray = bytearray((work / 'case.mhir').read_bytes())
    old_hir[8:10] = (3).to_bytes(2, 'little')
    (work / 'old.mhir').write_bytes(old_hir)
    invoke(binary, work, ['compile', 'old.mhir', '-o', 'rejected.mso'], success=False)
    for level in (0, 2):
        invoke(binary, work, ['compile', 'case.mhir', f'-O{level}', '-o', f'case{level}.mso'])
        invoke(binary, work, ['bind-ir', f'case{level}.mso', '-o', f'case{level}.mexe'])
    (work / 'case.mhir').unlink()
    for level in (0, 2):
        for extension in ('mso', 'mexe'):
            for policy in ('off', 'auto', 'force'):
                (work / 'output').unlink(missing_ok=True)
                result: subprocess.CompletedProcess[bytes] = invoke(binary, work, ['run-ir', f'case{level}.{extension}'], policy=policy)
                if result.stdout != b'8\n' or (work / 'output').read_text() != 'ready':
                    raise AssertionError(f'O{level} {extension} {policy}: {result.stdout!r}, {result.stderr!r}')
    old: bytearray = bytearray((work / 'case2.mso').read_bytes())
    old[8:10] = (1).to_bytes(2, 'little')
    (work / 'old.mso').write_bytes(old)
    rejection: subprocess.CompletedProcess[bytes] = invoke(binary, work, ['run-ir', 'old.mso'], success=False)
    if b'Recompile' not in rejection.stderr:
        raise AssertionError(f'Missing rebuild guidance: {rejection.stderr!r}')
    (work / 'v6.mso').write_bytes(b'MSYNCBC\0' + (6).to_bytes(2, 'little'))
    invoke(binary, work, ['run-ir', 'v6.mso'], success=False)
    print('PASS compiled storage: source-free HIR/object/bound routes, selected transfer, O0/O2, Off/Auto/Force, old-format rejection')


if __name__ == '__main__':
    main()
