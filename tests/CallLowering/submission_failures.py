"""Check deferred process admission errors through source and bound CLI routes."""
from __future__ import annotations
import argparse
import json
import subprocess
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]

def invoke(binary: Path, work: Path, arguments: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run([str(binary), '--no-config', '--no-telemetry', *arguments],
        cwd=work, text=True, input='', capture_output=True, timeout=30, check=False)

def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    work: Path = ROOT / '.temp/process-submission-errors'
    work.mkdir(parents=True, exist_ok=True)
    records: list[dict[str, str | int]] = []
    for demand in ('acquire failed;', 'null'):
        name: str = 'demanded' if demand.startswith('acquire') else 'owned'
        source: Path = work / f'{name}.msync'
        source.write_text('let failed = release ./missing-submission-executable("argument");\n'
            '$std::io::println("submitted");\n' + demand + '\n')
        for command in (['compile', str(source), '-O2', '-o', f'{name}.mso'],
                        ['bind-ir', f'{name}.mso', '-o', f'{name}.mexe']):
            result: subprocess.CompletedProcess[str] = invoke(binary, work, command)
            assert result.returncode == 0, result.stderr
        for route in ('source', 'bound'):
            result = invoke(binary, work, ['run', str(source) if route == 'source' else f'{name}.mexe'])
            assert result.returncode != 0, 'Unobserved admission failure was lost'
            assert result.stdout == 'submitted\n', (route, result.stdout, result.stderr)
            assert 'missing-submission-executable' in result.stderr and f'{name}.msync' in result.stderr, result.stderr
            assert ':1:' in result.stderr or 'source offset 21..64' in result.stderr, result.stderr
            assert 'Unhandled exception' not in result.stderr and 'AggregateException' not in result.stderr, result.stderr
            records.append({'case': name, 'route': route, 'exit': result.returncode, 'diagnostic': result.stderr})
    (work / 'result.json').write_text(json.dumps(records, indent=2) + '\n')
    print('PASS deferred process failures: demanded/owned, source/bound, original source attribution')

if __name__ == '__main__':
    main()
