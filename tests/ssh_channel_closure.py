#!/usr/bin/env python3
"""Real native-SSH archive duplex and cancelled physical-endpoint journeys.

Use the private loopback fixture; no user SSH configuration or account changes.
"""
from __future__ import annotations

from dataclasses import asdict
import errno
import hashlib
import json
import os
from pathlib import Path
import signal
import stat
import subprocess
import sys
from time import monotonic, sleep
from typing import Mapping

ROOT: Path = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / 'tests'))
from benches.ssh_v065 import Fixture, checked, lines, owned_run
from support.literal_string import quoted
from normal_ssh_model import invoke, remote


def descendants(pid: int) -> set[int]:
    try:
        children: list[int] = [int(value) for value in Path(f'/proc/{pid}/task/{pid}/children').read_text().split()]
    except FileNotFoundError:
        return set()
    found: set[int] = set(children)
    for child in children:
        found.update(descendants(child))
    return found


def wait_idle(fixture: Fixture) -> None:
    assert fixture.server is not None
    deadline: float = monotonic() + 5
    while descendants(fixture.server.pid):
        assert monotonic() < deadline, f'remote helpers remain: {descendants(fixture.server.pid)}'
        sleep(0.01)


def cancel_fifo(fixture: Fixture, binary: Path, direction: str) -> dict[str, object]:
    wait_idle(fixture)
    local: Path = fixture.root / ('cancel-' + direction)
    local.mkdir()
    fifo: Path = fixture.root / ('fifo-' + direction)
    os.mkfifo(fifo)
    (local / 'cmd').write_bytes(b'old payload must not arrive after cancelled connection')
    location: str = 'remote::' + quoted(str(fifo))
    source: str = ('cmd -> ' + location if direction == 'write' else location + ' -> received') + '\n'
    calls_before: int = len(lines(fixture.calls))
    child: subprocess.Popen[bytes] = subprocess.Popen(
        [str(binary), '--no-config', 'run', '-'], cwd=local,
        env=fixture.environment(), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, start_new_session=True,
    )
    assert child.stdin is not None
    child.stdin.write(('remote => ssh://Perf_Fixture\n' + source).encode())
    child.stdin.close()
    child.stdin = None
    assert fixture.server is not None
    deadline: float = monotonic() + 8
    relay: int | None = None
    try:
        while relay is None:
            assert child.poll() is None, f'msync ended before relay admission: {child.communicate()}'
            for pid in descendants(fixture.server.pid):
                try:
                    command: bytes = Path(f'/proc/{pid}/cmdline').read_bytes()
                except FileNotFoundError:
                    continue
                if command.startswith(b'python3\0-c\0') and b'def main() -> int:' in command and os.fsencode(fifo) in command:
                    if descendants(pid):
                        relay = pid
                        break
            assert monotonic() < deadline, 'physical relay never admitted'
            sleep(0.01)
        before: list[int] = sorted(descendants(fixture.server.pid))
        child.send_signal(signal.SIGINT)
        output, errors = child.communicate(timeout=8)
        assert child.returncode != 0, (output, errors)
        wait_idle(fixture)
        assert stat.S_ISFIFO(fifo.stat().st_mode)
        if direction == 'write':
            reader: int = os.open(fifo, os.O_RDONLY | os.O_NONBLOCK)
            assert os.read(reader, 4096) == b''
            os.close(reader)
        else:
            try:
                writer: int = os.open(fifo, os.O_WRONLY | os.O_NONBLOCK)
            except OSError as error:
                assert error.errno == errno.ENXIO, error
            else:
                os.close(writer)
                raise AssertionError('cancelled remote reader consumed a later writer')
        calls: list[str] = lines(fixture.calls)[calls_before:]
        assert calls.count('ssh') == 1 and calls.count('scp') == 0, calls
        (local / 'stdout.log').write_bytes(output)
        (local / 'stderr.log').write_bytes(errors)
        return {'direction': direction, 'ssh_clients': 1, 'remote_helpers_before': before, 'remote_helpers_after': [], 'no_late_payload': True}
    finally:
        if child.poll() is None:
            os.killpg(child.pid, signal.SIGTERM)
            try:
                child.communicate(timeout=3)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL)
                child.communicate()


def large_selection(fixture: Fixture, binary: Path) -> Mapping[str, object]:
    local: Path = fixture.root / 'selection-local'
    source: Path = fixture.root / 'selection-remote'
    local.mkdir()
    source.mkdir()
    payload: bytes = bytes(range(256)) * 2048
    name: str = '00000-big-' + 'x' * 120
    (source / name).write_bytes(payload)
    count: int = 2200
    for index in range(1, count):
        (source / (f'{index:05d}-' + 'x' * 120)).write_bytes(b'')
    calls_before: int = len(lines(fixture.calls))
    result: subprocess.CompletedProcess[bytes] = owned_run(
        [str(binary), '--no-config', 'run', '-'], cwd=local,
        environment=fixture.environment(), source=(
            'remote => ssh://Perf_Fixture\nremote::' + quoted(str(source)) + '/<.+> -> received/\n'
        ).encode(), timeout=45,
    )
    (local / 'stdout.log').write_bytes(result.stdout)
    (local / 'stderr.log').write_bytes(result.stderr)
    checked(result)
    target: Path = local / 'received'
    assert len(list(target.iterdir())) == count
    assert (target / name).read_bytes() == payload
    calls: list[str] = lines(fixture.calls)[calls_before:]
    assert calls.count('ssh') == 1 and calls.count('scp') == 0, calls
    wait_idle(fixture)
    return {'files': count, 'selection_name_bytes': sum(len(path.name.encode()) + 1 for path in source.iterdir()), 'first_payload_bytes': len(payload), 'ssh_clients': 1, 'scp_clients': 0}


def same_host_cohorts(fixture: Fixture, binary: Path) -> list[dict[str, object]]:
    local: Path = fixture.root / 'server-cohort-local'
    local.mkdir()
    target: Path = fixture.root / 'server-cohort-out'
    target.mkdir()
    safe: Path = fixture.root / 'safe'
    safe.write_bytes(b'safe payload')
    (target / 'a').write_bytes(b'preexisting own source')
    cases: list[dict[str, object]] = []
    cases.append(asdict(invoke(fixture, binary, local,
        f'[{remote(safe)}, {remote(target / "a")}] -> {remote(target)}/\n', 'server-cohort-alias')))
    assert (target / 'safe').read_bytes() == b'safe payload'
    assert (target / 'a').read_bytes() == b'preexisting own source'

    first: Path = fixture.root / 'readonly-first' / 'same'
    second: Path = fixture.root / 'readonly-second' / 'same'
    first.mkdir(parents=True)
    second.mkdir(parents=True)
    (first / 'old').write_bytes(b'old')
    (second / 'old').write_bytes(b'last')
    (second / 'new').write_bytes(b'new')
    first.chmod(0o500)
    second.chmod(0o500)
    cases.append(asdict(invoke(fixture, binary, local,
        f'[{remote(first)}, {remote(second)}] -> {remote(target)}/\n', 'server-cohort-readonly-merge')))
    assert (target / 'same/old').read_bytes() == b'last'
    assert (target / 'same/new').read_bytes() == b'new'
    for directory in [first, second, target / 'same']:
        assert directory.stat().st_mode & 0o777 == 0o500
        directory.chmod(0o700)

    outside: Path = fixture.root / 'outside'
    outside.write_bytes(b'outside must stay unchanged')
    link: Path = fixture.root / 'link-first' / 'node'
    value: Path = fixture.root / 'link-second' / 'node'
    link.parent.mkdir()
    value.parent.mkdir()
    link.symlink_to(outside)
    value.write_bytes(b'replacement value')
    cases.append(asdict(invoke(fixture, binary, local,
        f'[{remote(link)}, {remote(value)}] -> {remote(target)}/\n', 'server-cohort-link-replacement')))
    assert not (target / 'node').is_symlink()
    assert (target / 'node').read_bytes() == b'replacement value'
    assert outside.read_bytes() == b'outside must stay unchanged'
    assert link.is_symlink()
    return cases


def native_directory_markers(fixture: Fixture, binary: Path) -> list[dict[str, object]]:
    local: Path = fixture.root / 'native-marker-local'
    local.mkdir()
    parent: Path = fixture.root / 'real-parent\n'
    (parent / 'child').mkdir(parents=True)
    (parent / 'data').write_bytes(b'native parent directory')
    (parent / 'child/nested').write_bytes(b'nested')
    alias: Path = fixture.root / 'native-alias'
    alias.symlink_to(parent / 'child', target_is_directory=True)
    selected: str = 'remote::' + quoted(str(alias) + '/..')
    cases: list[dict[str, object]] = []
    cases.append(asdict(invoke(fixture, binary, local,
        selected + ' -> received/\n', 'download-native-parent')))
    assert (local / 'received' / parent.name / 'data').read_bytes() == b'native parent directory'
    assert (local / 'received' / parent.name / 'child/nested').read_bytes() == b'nested'
    target: Path = fixture.root / 'parent-copied'
    cases.append(asdict(invoke(fixture, binary, local,
        f'[{selected}, {remote(parent / "data")}] -> {remote(target)}/\n', 'server-cohort-native-parent')))
    assert (target / parent.name / 'data').read_bytes() == b'native parent directory'
    assert (target / 'data').read_bytes() == b'native parent directory'
    invalid: str = 'remote::' + quoted(str(parent / 'data') + '/.')
    cases.append(asdict(invoke(fixture, binary, local,
        invalid + ' -> invalid-received\n', 'download-file-directory-marker', expected_success=False)))
    assert not (local / 'invalid-received').exists()
    assert (parent / 'data').read_bytes() == b'native parent directory'
    return cases


def main() -> None:
    binary: Path = Path(sys.argv[1]).resolve()
    report_path: Path = Path(sys.argv[2]).resolve()
    report: dict[str, object] = {'binary_sha256': hashlib.sha256(binary.read_bytes()).hexdigest()}
    fixture: Fixture = Fixture(ROOT / '.temp/normal-user-model')
    report['fixture'] = str(fixture.root)
    try:
        with fixture:
            report['large_selection'] = large_selection(fixture, binary)
            report['fifo_cancel'] = [cancel_fifo(fixture, binary, direction) for direction in ['write', 'read']]
            report['same_host_cohorts'] = same_host_cohorts(fixture, binary)
            report['native_directory_markers'] = native_directory_markers(fixture, binary)
        report['status'] = 'passed'
    except BaseException as error:
        report['status'] = 'failed'
        report['error'] = repr(error)
        raise
    finally:
        report_path.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
