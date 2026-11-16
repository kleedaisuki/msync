"""Compare three compiler images in balanced fresh-process blocks."""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import os
import resource
import statistics
import subprocess
import time
from pathlib import Path
from typing import Any


def sha256(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def observe(binary: Path, source: Path, output: Path) -> dict[str, Any]:
    arguments: list[str] = [str(binary), '--no-config', '--no-telemetry', 'compile',
                            str(source), '-O2', '-o', str(output)]
    before: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    started: int = time.perf_counter_ns()
    result: subprocess.CompletedProcess[str] = subprocess.run(
        arguments, cwd=source.parent, env={**os.environ, 'NO_COLOR': '1'},
        capture_output=True, text=True, check=False)
    elapsed: float = (time.perf_counter_ns() - started) / 1_000_000
    after: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    return {
        'wall_ms': elapsed,
        'cpu_ms': ((after.ru_utime + after.ru_stime)
                   - (before.ru_utime + before.ru_stime)) * 1000,
        'exit_code': result.returncode, 'stdout': result.stdout, 'stderr': result.stderr,
        'artifact_sha256': sha256(output) if result.returncode == 0 else None,
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    for name in ('original', 'before', 'after', 'source', 'receipt', 'work', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--rounds', type=int, default=2)
    args: argparse.Namespace = parser.parse_args()
    images: dict[str, Path] = {name: getattr(args, name).resolve()
                              for name in ('original', 'before', 'after')}
    source: Path = args.source.resolve()
    work: Path = args.work.resolve()
    work.mkdir(parents=True, exist_ok=True)
    report: dict[str, Any] = {
        'status': 'running', 'source_sha256': sha256(source),
        'images': {name: {'sha256': sha256(path), 'path': str(path)}
                   for name, path in images.items()},
        'receipt_sha256': sha256(args.receipt),
        'environment_overrides': {'NO_COLOR': '1'},
        'method': 'all six image permutations per round; fresh process; telemetry off; '
                  'warm OS cache; one overwritten output; hashes outside timer',
        'command': ['BINARY', '--no-config', '--no-telemetry', 'compile', str(source),
                    '-O2', '-o', str(work / 'output.mso')],
        'warmups': {}, 'blocks': [],
    }
    try:
        for name, image in images.items():
            observation: dict[str, Any] = observe(image, source, work / 'output.mso')
            report['warmups'][name] = observation
            if observation['exit_code'] != 0:
                raise RuntimeError(f'{name} warmup failed: {observation["stderr"]}')
        for _ in range(args.rounds):
            for order in itertools.permutations(images):
                block: dict[str, Any] = {'order': list(order), 'observations': {}}
                report['blocks'].append(block)
                for name in order:
                    observation = observe(images[name], source, work / 'output.mso')
                    block['observations'][name] = observation
                    if observation['exit_code'] != 0:
                        raise RuntimeError(f'{name} compilation failed: {observation["stderr"]}')
        contrasts: dict[str, Any] = {}
        for candidate, baseline in [('before', 'original'), ('after', 'original'), ('after', 'before')]:
            contrasts[f'{candidate}_minus_{baseline}'] = {}
            for metric in ('wall_ms', 'cpu_ms'):
                differences: list[float] = [
                    block['observations'][candidate][metric]
                    - block['observations'][baseline][metric] for block in report['blocks']]
                contrasts[f'{candidate}_minus_{baseline}'][metric] = {
                    'mean': statistics.mean(differences),
                    'median': statistics.median(differences), 'raw': differences}
        report['contrasts'] = contrasts
        report['status'] = 'passed'
        print(json.dumps(contrasts, indent=2))
    except RuntimeError:
        report['status'] = 'failed'
        raise
    finally:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
