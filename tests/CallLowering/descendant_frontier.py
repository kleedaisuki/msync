"""Hold a child process across an inherited worker acquire after its parent job completes."""
from __future__ import annotations
import argparse
import json
import queue
import subprocess
import sys
import threading
from pathlib import Path
from typing import BinaryIO

ROOT: Path = Path(__file__).resolve().parents[2]

def lines(stream: BinaryIO, output: queue.Queue[bytes]) -> None:
    for line in iter(stream.readline, b''):
        output.put(line.rstrip(b'\r\n'))
    output.put(b'<eof>')

def invoke(binary: Path, work: Path, arguments: list[str]) -> None:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), '--no-config', '--no-telemetry', *arguments], cwd=work,
        input=b'', capture_output=True, timeout=30, check=False)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors='replace'))

def journey(binary: Path, route: str, level: int, trace: bool = False, stream: bool = False) -> None:
    work: Path = ROOT/'.temp/descendant-frontier'/f'{"stream" if stream else "transfer"}-{route}-O{level}'
    work.mkdir(parents=True, exist_ok=True)
    gate: Path = work/'child.gate'
    finished: Path = work/'child.finished'
    gate.unlink(missing_ok=True)
    finished.unlink(missing_ok=True)
    (work/'input').write_text('payload', encoding='utf-8')
    for name in ('a', 'b'):
        (work/name).mkdir(exist_ok=True)
    worker: Path = work/'child.py'
    worker.write_text(
        'from pathlib import Path\nimport time\n'
        'gate: Path = Path("child.gate")\n'
        'while not gate.exists(): time.sleep(0.005)\n'
        'Path("child.finished").write_text("done")\n', encoding='utf-8')
    source: Path = work/'case.msync'
    source.write_text(
        f'let python: Path = {json.dumps(sys.executable)};\n'
        'let state = object { child: python("-c", "pass") };\n'
        'let first = $std::ranges::transform([./input], fn(path) {\n'
        '  let signal? = $std::io::read_line(); if signal is not null {\n'
        f'    state::child = release python({json.dumps(str(worker))});\n'
        '  }; $std::io::read_line(); path\n'
        '});\n'
        'first -> ./a;\n'
        'let second = $std::ranges::transform([./input], fn(path) {\n'
        '  $std::io::println("joining"); acquire; $std::io::println("acquired"); path\n'
        '});\n'
        'second -> ./b; $std::io::println("submitted");\n', encoding='utf-8')
    if stream:
        program: str = source.read_text().replace('first -> ./a;', 'let a = $std::ranges::size(first);')
        program = program.replace('second -> ./b; $std::io::println("submitted");',
            'let b = $std::ranges::size(second); $std::io::println("submitted"); acquire;')
        source.write_text(program)
    arguments: list[str] = ['run', str(source), f'-O{level}']
    if route == 'bound':
        invoke(binary, work, ['compile', str(source), f'-O{level}', '-o', 'case.mso'])
        invoke(binary, work, ['bind-ir', 'case.mso', '-o', 'case.mexe'])
        arguments = ['run-ir', 'case.mexe']
    options: list[str] = ['--no-config', '--trace', str(work/'trace.jsonl'), '--telemetry-level', 'diagnostics'] if trace else ['--no-config', '--no-telemetry']
    process: subprocess.Popen[bytes] = subprocess.Popen(
        [str(binary), *options, *arguments], cwd=work,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    assert process.stdout is not None and process.stdin is not None and process.stderr is not None
    output: queue.Queue[bytes] = queue.Queue()
    reader: threading.Thread = threading.Thread(target=lines, args=(process.stdout, output), daemon=True)
    reader.start()
    premature: bool = False
    acquired: bool = False
    try:
        first: bytes = output.get(timeout=15)
        if stream:
            assert first in (b'joining', b'submitted'), (first, process.stderr.read() if first == b'<eof>' else b'')
            assert {first, output.get(timeout=15)} == {b'joining', b'submitted'}
        else:
            assert first == b'submitted', (first, process.stderr.read() if first == b'<eof>' else b'')
        process.stdin.write(b'go\n')
        process.stdin.flush()
        if not stream:
            assert output.get(timeout=15) == b'joining'
        # This is an intentionally held lifetime gate, not a latency acceptance threshold.
        try:
            line: bytes = output.get(timeout=0.3)
            assert line == b'acquired', line
            acquired = True
            premature = not finished.exists()
        except queue.Empty:
            pass
        gate.touch()
        if not acquired:
            assert output.get(timeout=15) == b'acquired'
        assert process.wait(timeout=15) == 0, process.stderr.read()
        assert not premature, 'Inherited acquire returned while its older job\'s released child was still gated'
        assert finished.read_text() == 'done'
        if not stream:
            assert (work/'a/input').read_text() == 'payload'
            assert (work/'b/input').read_text() == 'payload'
    finally:
        gate.touch()
        if process.poll() is None:
            process.kill()
            process.wait()
        (work/'stderr.txt').write_bytes(process.stderr.read())
        for stream in (process.stdin, process.stdout, process.stderr):
            stream.close()
        reader.join(timeout=1)

def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    parser.add_argument('--single', action='store_true')
    parser.add_argument('--trace', action='store_true')
    parser.add_argument('--stream', action='store_true', help='Use scalar stream jobs rather than transfers')
    args: argparse.Namespace = parser.parse_args()
    cases: list[tuple[str, int]] = [('source', 0)] if args.single else [(route, level) for level in (0, 2) for route in ('source', 'bound')]
    for route, level in cases:
        journey(args.binary.resolve(), route, level, args.trace, args.stream)
        print(f'PASS {"stream" if args.stream else "transfer"} descendant frontier: {route} O{level}', flush=True)

if __name__ == '__main__':
    main()
