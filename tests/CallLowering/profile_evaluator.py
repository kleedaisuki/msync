"""Attribute a real executable workload with Callgrind, not wall-time sampling."""
from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
import time
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    parser.add_argument('--artifact', type=Path, required=True)
    parser.add_argument('--expected', type=Path,
                        default=ROOT / 'artifacts/benchmarks/storage-contract/expected.txt')
    parser.add_argument('--output', type=Path, required=True, help='Output file stem inside the repository')
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    artifact: Path = args.artifact.resolve()
    output: Path = args.output.resolve()
    output.relative_to(ROOT)
    output.parent.mkdir(parents=True, exist_ok=True)
    profile: Path = output.with_suffix('.callgrind')
    command: list[str] = [
        'valgrind', '--tool=callgrind', '--quiet', f'--callgrind-out-file={profile}',
        str(binary), '--no-config', '--no-telemetry', 'run-ir', str(artifact),
    ]
    started: float = time.monotonic()
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        command, cwd=ROOT, input=b'', capture_output=True, check=False)
    output.with_suffix('.stdout').write_bytes(result.stdout)
    output.with_suffix('.stderr').write_bytes(result.stderr)
    metadata: dict[str, Any] = {
        'measurement': 'Callgrind simulated instruction counts; not a throughput timing',
        'command': command,
        'platform': platform.platform(),
        'image_sha256': digest(binary),
        'artifact_sha256': digest(artifact),
        'stdin': 'EOF',
        'instrumented_wall_seconds': time.monotonic() - started,
        'exit_code': result.returncode,
        'stdout_matches': result.stdout == args.expected.read_bytes(),
    }
    output.with_suffix('.json').write_text(json.dumps(metadata, indent=2) + '\n')
    result.check_returncode()
    assert metadata['stdout_matches'], f'Workload result differs: {output.with_suffix(".stdout")}'
    report: subprocess.CompletedProcess[str] = subprocess.run(
        ['callgrind_annotate', '--auto=no', '--threshold=95', str(profile)],
        cwd=ROOT, capture_output=True, text=True, check=True)
    output.with_suffix('.annotated.txt').write_text(report.stdout)
    print(f'PASS workload output; instruction profile: {profile}', flush=True)


if __name__ == '__main__':
    main()
