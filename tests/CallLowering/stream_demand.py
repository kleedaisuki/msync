"""Require a stream reduction to return before its external input is available."""
from __future__ import annotations

import argparse
import selectors
import subprocess
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = '''let count = $std::ranges::size($std::ranges::filter([1], fn(value) {
    $std::io::read_line() is null
}));
$std::io::println("submitted");
$std::io::println(count as String);
'''


def invoke(binary: Path, work: Path, arguments: list[str]) -> None:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), '--no-config', '--no-telemetry', *arguments], cwd=work,
        capture_output=True, timeout=30, check=False)
    assert result.returncode == 0, (arguments, result.stderr.decode())


def journey(binary: Path, work: Path, arguments: list[str], expected: bytes) -> bool:
    child: subprocess.Popen[bytes] = subprocess.Popen(
        [str(binary), '--no-config', '--no-telemetry', *arguments], cwd=work,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
    try:
        assert child.stdout is not None and child.stdin is not None
        with selectors.DefaultSelector() as selector:
            selector.register(child.stdout, selectors.EVENT_READ)
            ready: bool = bool(selector.select(timeout=5))
        if ready:
            assert child.stdout.readline() == b'submitted\n'
        # Supply EOF only after testing progress, so the operation returns true.
        child.stdin.close()
        child.stdin = None
        output: bytes
        errors: bytes
        output, errors = child.communicate(timeout=15)
        assert child.returncode == 0, (arguments, errors.decode())
        assert output == (expected if ready else b'submitted\n' + expected), output
        return ready
    finally:
        if child.poll() is None:
            child.kill()
            child.wait()
        for stream in (child.stdin, child.stdout, child.stderr):
            if stream is not None:
                stream.close()


def abandonment(binary: Path, work: Path) -> None:
    source: Path = work / 'abandon.msync'
    source.write_text(SOURCE.replace('let count = ', '').replace('$std::io::println(count as String);', ''))
    child: subprocess.Popen[bytes] = subprocess.Popen(
        [str(binary), '--no-config', '--no-telemetry', 'run', str(source), '-O2'], cwd=work,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
    try:
        assert child.wait(timeout=15) == 0
        assert child.stdout is not None and child.stdout.read() == b'submitted\n'
    finally:
        if child.poll() is None:
            child.kill()
            child.wait()
        for stream in (child.stdin, child.stdout, child.stderr):
            if stream is not None:
                stream.close()
    print('PASS discarded reduction exits with stdin still open', flush=True)


def deferred_failures(binary: Path, work: Path) -> None:
    source: Path = work / 'failed-callback.msync'
    source.write_text('let result = $std::ranges::first($std::ranges::transform([1], '
        'fn(value) {$std::io::read_stdin() as Number})); '
        '$std::io::println("submitted"); $std::io::println(result as String);\n')
    for level in ('-O0', '-O2'):
        invoke(binary, work, ['compile', str(source), level, '-o', 'failed.mso'])
        invoke(binary, work, ['bind-ir', 'failed.mso', '-o', 'failed.mexe'])
        for arguments in (['run', str(source), level], ['run-ir', 'failed.mexe']):
            child: subprocess.Popen[bytes] = subprocess.Popen(
                [str(binary), '--no-config', '--no-telemetry', *arguments], cwd=work,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
            try:
                assert child.stdout is not None
                with selectors.DefaultSelector() as selector:
                    selector.register(child.stdout, selectors.EVENT_READ)
                    assert selector.select(timeout=5), 'Failure journey blocked before submitting'
                assert child.stdout.readline() == b'submitted\n'
                output: bytes
                errors: bytes
                output, errors = child.communicate(input=b'not-a-number', timeout=15)
                assert child.returncode != 0 and output == b'', (arguments, output, errors)
                assert b'failed-callback.msync' in errors, errors
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait()
                for stream in (child.stdin, child.stdout, child.stderr):
                    if stream is not None:
                        stream.close()
    print('PASS delayed stream failure attribution: source/bound O0/O2', flush=True)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    parser.add_argument('--failures', action='store_true', help='Run only delayed callback failures')
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    work: Path = ROOT / '.temp/stream-demand'
    work.mkdir(parents=True, exist_ok=True)
    if args.failures:
        deferred_failures(binary, work)
        return
    cases: dict[str, tuple[str, bytes]] = {
        'filter-size': (SOURCE, b'1\n'),
        'ready-state': (
            'let bias = 1; let result = $std::ranges::first('
            '$std::ranges::transform([2], fn(value) {value + bias})); bias = 9; '
            '$std::io::println("submitted"); $std::io::println(result as String);\n', b'3\n'),
        'ready-native-call': (
            'let bias = 1; let result = $std::ranges::first('
            '$std::ranges::transform([2], fn(value) {$std::math::floor(value + bias)})); bias = 9; '
            '$std::io::println("submitted"); $std::io::println(result as String);\n', b'3\n'),
        'pending-element': (
            'let result? = $std::ranges::first([$std::io::read_line()]); '
            '$std::io::println("submitted"); '
            '$std::io::println(if result is null {"nil"} else {result});\n', b'nil\n'),
        'predicate-any': (
            'let result = $std::ranges::any([1], fn(value) { $std::io::read_line() is null });\n'
            '$std::io::println("submitted"); $std::io::println(result as String);\n', b'true\n'),
        'callback-acquire': (
            'let result = $std::ranges::any([1], fn(value) { '
            'acquire; $std::io::read_line() is null }); '
            '$std::io::println("submitted"); acquire; '
            '$std::io::println(result as String);\n', b'true\n'),
        'captured-input': (
            'let input? = $std::io::read_line(); '
            'let result = $std::ranges::first($std::ranges::transform([1], fn(value) { '
            'if input is null {value} else {0} })); '
            '$std::io::println("submitted"); $std::io::println(result as String);\n', b'1\n'),
        'get-orders-state': (
            'let state = 0; let result = $std::ranges::first('
            '$std::ranges::transform([1], fn(value) {state = 2; '
            'if $std::io::read_line() is null {state} else {0} })); '
            '$std::io::println("submitted"); $std::io::println(result as String); '
            '$std::io::println(state as String);\n', b'2\n2\n'),
        'returned-capture': (
            'fn consume() { let offset = 7; '
            '$std::ranges::first($std::ranges::transform([1], fn(value) { '
            'if $std::io::read_line() is null {offset + value} else {0} })) };\n'
            'let result = consume(); $std::io::println("submitted"); '
            '$std::io::println(result as String);\n', b'8\n'),
    }
    failures: list[str] = []
    for name, (program, expected) in cases.items():
        source: Path = work / f'{name}.msync'
        source.write_text(program)
        for level in ('-O0', '-O2'):
            invoke(binary, work, ['compile', str(source), level, '-o', 'demand.mso'])
            invoke(binary, work, ['bind-ir', 'demand.mso', '-o', 'demand.mexe'])
            for route, arguments in (
                ('source', ['run', str(source), level]),
                ('bound', ['run-ir', 'demand.mexe']),
            ):
                label: str = f'{name} {route} {level}'
                passed: bool = journey(binary, work, arguments, expected)
                print(f'{"PASS" if passed else "FAIL"} {label}: progress before stdin EOF', flush=True)
                if not passed:
                    failures.append(label)
    assert not failures, f'Stream reduction blocked at submission: {failures}'
    abandonment(binary, work)
    eager: Path = work / 'eager.msync'
    eager.write_text(SOURCE.replace('$std::ranges::size($std::ranges::filter(',
        '$std::ranges::size($std::ranges::materialize($std::ranges::filter(').replace('}));', '})));'))
    assert not journey(binary, work, ['run', str(eager), '-O2'], b'1\n'), 'materialize lost eager snapshot semantics'
    fault: Path = work / 'ready-error.msync'
    fault.write_text('let value = $std::ranges::first($std::ranges::filter([1], fn(item) {false})); '
        '$std::io::println("not reached");\n')
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), '--no-config', '--no-telemetry', 'run', str(fault), '-O2'],
        cwd=work, input=b'', capture_output=True, timeout=15, check=False)
    assert result.returncode != 0 and result.stdout == b'', (result.stdout, result.stderr)
    assert b'ready-error.msync' in result.stderr, result.stderr
    print('PASS eager materialization, ready captured state, and ready failure ordering', flush=True)
    deferred_failures(binary, work)


if __name__ == '__main__':
    main()
