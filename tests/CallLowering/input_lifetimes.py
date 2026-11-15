"""Exercise abandoned and ordered standard-input Futures through the real CLI."""
from __future__ import annotations
import argparse
import subprocess
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]

def invoke(binary: Path, work: Path, arguments: list[str], data: bytes = b'') -> bytes:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), '--no-config', '--no-telemetry', *arguments], cwd=work,
        input=data, capture_output=True, timeout=30, check=False)
    assert result.returncode == 0, (arguments, result.stderr)
    return result.stdout

def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    work: Path = ROOT / '.temp/input-future-lifetimes'
    work.mkdir(parents=True, exist_ok=True)
    source: Path = work / 'discard.msync'
    source.write_text('$std::io::read_line(); $std::io::println("done");\n')
    invoke(binary, work, ['compile', str(source), '-O0', '-o', 'discard.mso'])
    invoke(binary, work, ['bind-ir', 'discard.mso', '-o', 'discard.mexe'])
    for arguments in (['run', str(source), '-O0'], ['run-ir', 'discard.mexe']):
        child: subprocess.Popen[bytes] = subprocess.Popen(
            [str(binary), '--no-config', '--no-telemetry', *arguments], cwd=work,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            # Leave stdin open: cancellation must not depend on receiving input or EOF.
            assert child.wait(timeout=10) == 0
            assert child.stdout is not None and child.stdout.read() == b'done\n'
        finally:
            if child.poll() is None: child.kill(); child.wait()
            for stream in (child.stdin, child.stdout, child.stderr):
                if stream is not None: stream.close()
    ordered: Path = work / 'ordered.msync'
    ordered.write_text('let first? = $std::io::read_line(); let second? = $std::io::read_line();\n'
        '$std::io::println(if first is null {"nil"} else {first});\n'
        '$std::io::println(if second is null {"nil"} else {second});\n')
    for level in ('-O0', '-O2'):
        assert invoke(binary, work, ['run', str(ordered), level], b'one\ntwo\n') == b'one\ntwo\n'
    print('PASS input Future lifetimes: open stdin abandonment and ordered reads, source/bound and O0/O2')

if __name__ == '__main__':
    main()
