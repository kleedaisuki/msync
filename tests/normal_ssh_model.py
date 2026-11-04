#!/usr/bin/env python3
"""Actual CLI acceptance for one-channel retryable SSH transfers."""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
from typing import Mapping

ROOT: Path = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from benches.ssh_v065 import Fixture, checked, lines, owned_run, prepared_file, verified_file
from support.literal_string import quoted


@dataclass(frozen=True)
class Journey:
    name: str
    ssh_clients: int
    scp_clients: int
    authentications: int
    remote_sessions: int
    exit_code: int


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def assert_no_protocol(path: Path) -> None:
    forbidden: tuple[str, ...] = ('.msync-recovery-v1', '.msync-remote-recovery-v1')
    for entry in path.rglob('*'):
        assert entry.name not in forbidden, entry
        assert not entry.name.startswith(('.msync-v4.', '.msync-statement-')), entry


def invoke(
    fixture: Fixture, binary: Path, local: Path, source: str, name: str,
    *, expected_success: bool = True, expected_clients: int | None = 1,
) -> Journey:
    before_calls: int = len(lines(fixture.calls))
    before_log: int = len(lines(fixture.log))
    result: subprocess.CompletedProcess[bytes] = owned_run(
        [str(binary), '--no-config', 'run', '-'], cwd=local,
        environment=fixture.environment(),
        source=('remote => ssh://Perf_Fixture\n' + source).encode(),
        timeout=45.0,
    )
    (local / f'{name}.stdout.log').write_bytes(result.stdout)
    (local / f'{name}.stderr.log').write_bytes(result.stderr)
    calls: list[str] = lines(fixture.calls)[before_calls:]
    log: list[str] = lines(fixture.log)[before_log:]
    journey: Journey = Journey(
        name, calls.count('ssh'), calls.count('scp'),
        sum('Accepted publickey for ' in line for line in log),
        sum('Starting session:' in line for line in log),
        result.returncode,
    )
    (local / f'{name}.result.json').write_text(json.dumps(asdict(journey), indent=2) + '\n')
    if expected_success:
        checked(result)
    else:
        assert result.returncode != 0, f'expected failure: {source}'
    if expected_clients is not None:
        assert journey.ssh_clients == expected_clients, journey
        assert journey.authentications == expected_clients, journey
        assert journey.remote_sessions == expected_clients, journey
    assert journey.scp_clients == 0, journey
    assert_no_protocol(local)
    return journey


def remote(path: Path) -> str:
    return 'remote::' + quoted(str(path))


def check_ssh(fixture: Fixture, binary: Path) -> list[Journey]:
    local: Path = fixture.root / 'normal-local'
    target: Path = fixture.root / 'normal-remote'
    local.mkdir()
    target.mkdir()
    payload: bytes = bytes(range(256)) * 7 + b'end'
    assert len(payload) == 1795
    mtime: int = 1_700_000_000_123_456_000
    prepared_file(local / 'cmd', payload, mtime_ns=mtime)
    journeys: list[Journey] = []

    journeys.append(invoke(fixture, binary, local, f'cmd -> {remote(target / "cmd")}\n', 'upload'))
    verified_file(target / 'cmd', payload, mtime_ns=mtime)

    prepared_file(local / 'cmd', payload + b'new', mtime_ns=mtime + 1000)
    journeys.append(invoke(fixture, binary, local, f'cmd -> {remote(target / "cmd")}\n', 'overwrite'))
    verified_file(target / 'cmd', payload + b'new', mtime_ns=mtime + 1000)

    journeys.append(invoke(fixture, binary, local, f'{remote(target / "cmd")} -> received\n', 'download'))
    verified_file(local / 'received', payload + b'new', mtime_ns=mtime + 1000)

    journeys.append(invoke(fixture, binary, local, f'"literal overwrite" -> {remote(target / "cmd")}\n', 'literal'))
    assert (target / 'cmd').read_bytes() == b'literal overwrite'
    assert (target / 'cmd').stat().st_mode & 0o777 == 0o640

    prepared_file(target / 'remote-source', b'already remote', mtime_ns=mtime)
    journeys.append(invoke(fixture, binary, local, f'{remote(target / "remote-source")} -> {remote(target / "remote-copy")}\n', 'same-endpoint'))
    verified_file(target / 'remote-copy', b'already remote', mtime_ns=mtime)

    tree: Path = local / 'tree'
    (tree / 'nested').mkdir(parents=True)
    prepared_file(tree / 'nested/program', b'#!/bin/sh\nexit 0\n', mtime_ns=mtime)
    (tree / 'nested/program').chmod(0o751)
    (tree / 'nested/link').symlink_to('program')
    (tree / 'dangling').symlink_to('not-created')
    journeys.append(invoke(fixture, binary, local, f'tree -> {remote(target / "tree")}/.\n', 'tree'))
    assert (target / 'tree/nested/program').read_bytes() == b'#!/bin/sh\nexit 0\n'
    assert (target / 'tree/nested/program').stat().st_mode & 0o777 == 0o751
    assert (target / 'tree/nested/program').stat().st_mtime_ns == mtime
    assert (target / 'tree/nested/link').readlink() == Path('program')
    assert (target / 'tree/dangling').readlink() == Path('not-created')

    prepared_file(local / 'first', b'first bytes', mtime_ns=mtime)
    prepared_file(local / 'second', b'second bytes', mtime_ns=mtime)
    (target / 'container').mkdir()
    prepared_file(target / 'container/retained', b'retained bytes', mtime_ns=mtime)
    journeys.append(invoke(fixture, binary, local, f'[first, second] -> {remote(target / "container")}/\n', 'cohort'))
    verified_file(target / 'container/first', b'first bytes', mtime_ns=mtime)
    verified_file(target / 'container/second', b'second bytes', mtime_ns=mtime)
    verified_file(target / 'container/retained', b'retained bytes', mtime_ns=mtime)

    prepared_file(local / 'first', b'first updated', mtime_ns=mtime + 1000)
    journeys.append(invoke(fixture, binary, local, f'[first, second] -> {remote(target / "container")}/\n', 'cohort-repeat'))
    verified_file(target / 'container/first', b'first updated', mtime_ns=mtime + 1000)
    verified_file(target / 'container/second', b'second bytes', mtime_ns=mtime)
    verified_file(target / 'container/retained', b'retained bytes', mtime_ns=mtime)

    prepared_file(tree / 'nested/program', b'updated executable', mtime_ns=mtime + 2000)
    (tree / 'nested/program').chmod(0o751)
    prepared_file(target / 'tree/retained', b'keep sibling', mtime_ns=mtime)
    journeys.append(invoke(fixture, binary, local, f'tree -> {remote(target)}/\n', 'tree-merge-repeat'))
    assert (target / 'tree/nested/program').read_bytes() == b'updated executable'
    assert (target / 'tree/nested/program').stat().st_mode & 0o777 == 0o751
    assert (target / 'tree/nested/program').stat().st_mtime_ns == mtime + 2000
    verified_file(target / 'tree/retained', b'keep sibling', mtime_ns=mtime)

    unusual: str = 'line\nslash\\ 可莉'
    prepared_file(local / unusual, b'literal names', mtime_ns=mtime)
    journeys.append(invoke(fixture, binary, local, f'$local::{quoted(unusual)} -> {remote(target / unusual)}\n', 'literal-name'))
    verified_file(target / unusual, b'literal names', mtime_ns=mtime)

    (target / 'blocked-parent').write_bytes(b'not a directory')
    retry: str = f'first -> {remote(target / "blocked-parent/retried")}\n'
    journeys.append(invoke(fixture, binary, local, retry, 'failed-parent', expected_success=False))
    assert (target / 'blocked-parent').read_bytes() == b'not a directory'
    (target / 'blocked-parent').unlink()
    (target / 'blocked-parent').mkdir()
    journeys.append(invoke(fixture, binary, local, retry, 'retry'))
    verified_file(target / 'blocked-parent/retried', b'first updated', mtime_ns=mtime + 1000)
    assert_no_protocol(target)
    return journeys


def check_local_syscalls(binary: Path, parent: Path) -> Mapping[str, object]:
    strace: str | None = shutil.which('strace')
    if strace is None:
        return {'status': 'unavailable', 'reason': 'strace is not installed'}
    local: Path = parent / 'normal-local-syscalls'
    local.mkdir()
    (local / 'source').write_bytes(b'abcdef')
    reports: list[dict[str, object]] = []
    for name, prior in [('new', None), ('overwrite', b'prior bytes')]:
        target: Path = local / 'target'
        if prior is not None:
            target.write_bytes(prior)
        trace: Path = local / f'{name}.strace'
        result: subprocess.CompletedProcess[bytes] = owned_run(
            [strace, '-f', '-qq', '-yy', '-e', 'trace=fsync,fdatasync,syncfs,rename,renameat,renameat2,unlink,unlinkat',
             '-o', str(trace), str(binary), '--no-config', 'run', '-'],
            cwd=local, source=b'source -> target\n',
        )
        checked(result)
        content: str = trace.read_text()
        flushes: int = len(re.findall(r'\b(?:fsync|fdatasync)\(', content))
        assert flushes <= 1, content
        assert 'syncfs(' not in content, content
        installed: list[str] = [line for line in content.splitlines() if 'rename' in line and f'"{target}"' in line]
        assert installed, content
        assert all(Path(re.findall(r'"([^"]*)"', line)[0]).parent == target.parent for line in installed), content
        assert not any('unlink' in line and f'"{target}"' in line for line in content.splitlines()), content
        assert target.read_bytes() == b'abcdef'
        assert_no_protocol(local)
        reports.append({'case': name, 'flush_calls': flushes, 'trace': str(trace)})
    return {'status': 'passed', 'observations': reports}


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('binary', type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / '.temp/normal-user-model/results.json')
    parser.add_argument('--local-only', action='store_true')
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    output: Path = args.output.resolve()
    image: str = digest(binary)
    report: dict[str, object] = {'binary_sha256': image, 'harness_sha256': digest(Path(__file__))}
    fixture: Fixture = Fixture(ROOT / '.temp/normal-user-model')
    report['fixture'] = str(fixture.root)
    try:
        if args.local_only:
            report['local'] = check_local_syscalls(binary, fixture.root)
        else:
            with fixture:
                report['ssh'] = [asdict(journey) for journey in check_ssh(fixture, binary)]
                report['local'] = check_local_syscalls(binary, fixture.root)
        assert digest(binary) == image, 'test executable changed during acceptance'
        report['status'] = 'passed'
    except BaseException as error:
        report['status'] = 'failed'
        report['error'] = repr(error)
        raise
    finally:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
