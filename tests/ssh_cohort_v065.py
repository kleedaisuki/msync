#!/usr/bin/env python3
"""Exercise retryable cohorts and whole-workspace copies through real OpenSSH."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shlex
import socket
import subprocess
import sys
from typing import TypedDict

ROOT: Path = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from benches.ssh_v065 import Fixture, checked, lines, owned_run, prepared_file, verified_file


class TraceSpan(TypedDict):
    kind: str
    name: str
    span_id: str
    parent_span_id: str | None
    status: str
    attributes: dict[str, str]
    metrics: dict[str, int]


def check_upload_trace(fixture: Fixture, binary: Path, completed: list[str]) -> None:
    for long_path in (False, True):
        label: str = 'long' if long_path else 'short'
        name: str = 'trace-' + label
        if long_path:
            name += '-ordinary-long-workspace-' + 'x' * 85
        local, remote = workspace(fixture, name)
        payload: bytes = b'x' * 1795
        mtime: int = 1_700_000_000_123_456_000
        prepared_file(local / 'cmd', payload, mtime_ns=mtime)
        trace: Path = local / 'trace.jsonl'
        source: str = f'cmd -> {remote_path(remote / "cmd")}\n'
        before_calls: int = len(lines(fixture.calls))
        before_log: int = len(lines(fixture.log))
        result: subprocess.CompletedProcess[bytes] = owned_run(
            [str(binary), '--no-config', 'run', '-', '--trace', str(trace)],
            cwd=local, environment=fixture.environment(),
            source=('remote => ssh://Perf_Fixture\n' + source).encode(),
        )
        checked(result)
        verified_file(remote / 'cmd', payload, mtime_ns=mtime)
        spans: list[TraceSpan] = [
            record for line in trace.read_text().splitlines()
            if (record := json.loads(line))['kind'] == 'span'
        ]
        transfers: list[TraceSpan] = [row for row in spans if row['name'] == 'ssh.transfer']
        assert len(transfers) == 1, transfers
        transfer: TraceSpan = transfers[0]
        assert transfer['status'] == 'ok', transfer
        assert transfer['attributes']['ssh.session.strategy'] == 'native_single_exec'
        assert transfer['attributes']['endpoint.host'] == 'Perf_Fixture'
        assert transfer['metrics']['client.invocations'] == 1
        phases: set[str | None] = {row['attributes'].get('phase') for row in spans}
        assert not {'allocate', 'publish', 'settle', 'recover', 'staging.allocate', 'staging.cleanup'} & phases
        assert not any(row['name'] == 'ssh.session.admit' for row in spans)
        identities: set[str] = {row['span_id'] for row in spans}
        assert all(row['parent_span_id'] is None or row['parent_span_id'] in identities for row in spans)
        calls: list[str] = lines(fixture.calls)[before_calls:]
        log: list[str] = lines(fixture.log)[before_log:]
        assert calls == ['ssh'], calls
        assert sum('Accepted publickey for ' in line for line in log) == 1, log
        assert sum('Starting session:' in line for line in log) == 1, log
        assert not list(remote.glob('.msync-v4.*'))
        completed.append(f'fractional-single-exchange-trace-{label}-workspace')


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def workspace(fixture: Fixture, name: str) -> tuple[Path, Path]:
    root: Path = fixture.root / name
    local: Path = root / "local"
    remote: Path = root / "remote"
    local.mkdir(parents=True)
    remote.mkdir()
    return local, remote


def invoke(
    fixture: Fixture, binary: Path, local: Path, source: str,
    errors: tuple[str, ...] = (),
) -> subprocess.CompletedProcess[bytes]:
    script: Path = local / "cohort.msync"
    script.write_text("remote => ssh://Perf_Fixture\n" + source)
    result: subprocess.CompletedProcess[bytes] = owned_run(
        [str(binary), "--no-config", "run", str(script)], cwd=local,
        environment=fixture.environment(), timeout=45.0,
    )
    (local / "stdout.log").write_bytes(result.stdout)
    (local / "stderr.log").write_bytes(result.stderr)
    if errors:
        assert result.returncode != 0, f"unexpected success: {source}"
        diagnostic: str = result.stderr.decode(errors="replace")
        assert any(error in diagnostic for error in errors), diagnostic
    else:
        checked(result)
    return result


def remote_path(path: Path) -> str:
    return "remote::" + json.dumps(str(path))


def check_cohorts(fixture: Fixture, binary: Path, completed: list[str]) -> None:
    local, remote = workspace(fixture, "publish")
    prepared_file(local / "a", b"new a")
    prepared_file(local / "b", b"new b")
    invoke(fixture, binary, local, f"[a, b] -> {remote_path(remote / 'out')}/\n")
    verified_file(remote / "out/a", b"new a")
    verified_file(remote / "out/b", b"new b")
    assert sorted(path.name for path in (remote / "out").iterdir()) == ["a", "b"]
    completed.append("one-cohort-bytes-metadata-no-extra-nesting")

    local, remote = workspace(fixture, "overwrite")
    prepared_file(local / "a", b"new a")
    prepared_file(local / "b", b"new b")
    (remote / "out").mkdir()
    prepared_file(remote / "out/b", b"retained b")
    invoke(fixture, binary, local, f"[a, b] -> {remote_path(remote / 'out')}/\n")
    verified_file(remote / "out/a", b"new a")
    verified_file(remote / "out/b", b"new b")
    invoke(fixture, binary, local, f"[a, b] -> {remote_path(remote / 'out')}/\n")
    verified_file(remote / "out/a", b"new a")
    verified_file(remote / "out/b", b"new b")
    assert not list(remote.glob(".msync-v4.*"))
    completed.append("cohort-overwrite-and-repeat-are-normal-copy-operations")

    local, remote = workspace(fixture, "merge")
    for parent, name in [("first", "a"), ("second", "b")]:
        directory: Path = local / parent / "same"
        directory.mkdir(parents=True)
        prepared_file(directory / name, name.encode())
    (remote / "out/same").mkdir(parents=True)
    prepared_file(remote / "out/same/retained", b"retained")
    invoke(fixture, binary, local, f"[first/same, second/same] -> {remote_path(remote / 'out')}/\n")
    for name in ["a", "b"]:
        verified_file(remote / "out/same" / name, name.encode())
    verified_file(remote / "out/same/retained", b"retained")
    completed.append("repeated-directory-name-merges-without-cloning-unrelated-data")

    local, remote = workspace(fixture, "alias")
    prepared_file(remote / "safe", b"safe input")
    (remote / "out").mkdir()
    prepared_file(remote / "out/a", b"retained a")
    source: str = f"[{remote_path(remote / 'safe')}, {remote_path(remote / 'out/a')}]"
    invoke(fixture, binary, local, f"{source} -> {remote_path(remote / 'out')}/\n")
    verified_file(remote / "out/safe", b"safe input")
    verified_file(remote / "out/a", b"retained a")
    assert not list(remote.glob(".msync-v4.*"))
    completed.append("cohort-source-alias-remains-valid-and-preserves-requested-bytes")

    local, remote = workspace(fixture, "empty")
    invoke(fixture, binary, local, f"[] -> {remote_path(remote / 'absent')}/\n")
    assert not (remote / "absent").exists()
    assert not (local / ".temp").exists()
    completed.append("empty-cohort-does-not-create-runtime-or-destination")

    local, remote = workspace(fixture, "literal")
    invoke(fixture, binary, local, f'["payload"] -> {remote_path(remote / "absent")}/\n', ("basename",))
    assert not (remote / "absent").exists()
    assert not list(remote.glob(".msync-v4.*"))
    completed.append("literal-data-does-not-invent-container-filenames")


def record_ssh_arguments(fixture: Fixture) -> Path:
    arguments: Path = fixture.root / "client-arguments.log"
    wrapper: Path = fixture.root / "bin/ssh"
    body: str = wrapper.read_text()
    anchor: str = "exec /usr/bin/ssh"
    assert body.count(anchor) == 1
    recording: str = f"printf '%s\\0' \"$@\" >> {shlex.quote(str(arguments))}\n"
    if recording not in body:
        wrapper.write_text(body.replace(anchor, recording + anchor))
        wrapper.chmod(0o700)
    return arguments


def verify_native_clients(arguments: Path, offset: int, expected: int) -> None:
    values: list[str] = arguments.read_bytes()[offset:].decode().split("\0")
    assert "-M" not in values and "-O" not in values, values
    assert "-S" not in values, values
    assert values.count("-T") == expected, values


def check_whole_cwd(fixture: Fixture, binary: Path, completed: list[str]) -> None:
    arguments: Path = record_ssh_arguments(fixture)
    for destination_kind in ["local", "remote"]:
        local, remote = workspace(fixture, "wc-l" if destination_kind == "local" else "wc-r")
        prepared_file(local / "data", b"whole-cwd data")
        prepared_file(remote / "probe", b"warmup bytes")
        user: Path = local / ".temp/ssh-user-data"
        user.mkdir(parents=True)
        prepared_file(user / "retained", b"user-owned SSH-looking data")
        target: Path = local.parent / "copy" if destination_kind == "local" else remote / "copy"
        destination: str = "$local::" + json.dumps(str(target)) if destination_kind == "local" else remote_path(target)
        source: str = (
            f"{remote_path(remote / 'probe')} -> warmup\n"
            f"$local::{json.dumps(str(local))} -> {destination}/.\n"
        )
        offset: int = arguments.stat().st_size if arguments.exists() else 0
        invoke(fixture, binary, local, source)
        verify_native_clients(arguments, offset, 1 if destination_kind == "local" else 2)
        verified_file(target / "data", b"whole-cwd data")
        verified_file(target / "warmup", b"warmup bytes")
        verified_file(target / ".temp/ssh-user-data/retained", b"user-owned SSH-looking data")
        assert sorted(path.name for path in (target / ".temp").iterdir()) == ["ssh-user-data"]
        completed.append(f"whole-cwd-{destination_kind}-copy-retains-user-temp-without-implicit-master")

    local, remote = workspace(fixture, "wc-s")
    prepared_file(remote / "probe", b"warmup bytes")
    user = local / ".temp/ssh-user-socket"
    user.mkdir(parents=True)
    path: Path = user / "s1"
    target = local.parent / "copy"
    source = (
        f"{remote_path(remote / 'probe')} -> warmup\n"
        f"$local::{json.dumps(str(local))} -> $local::{json.dumps(str(target))}/.\n"
    )
    offset = arguments.stat().st_size
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as external:
        external.bind(str(path))
        invoke(fixture, binary, local, source, ("unsupported", "socket", "ordinary file", "stream"))
        verify_native_clients(arguments, offset, 1)
        assert path.is_socket(), "unrelated socket was removed as private storage"
        verified_file(local / "warmup", b"warmup bytes")
    completed.append("similarly-named-unrelated-socket-remains-visible-and-untouched")


def check_repl_capture(fixture: Fixture, binary: Path, completed: list[str]) -> None:
    arguments: Path = record_ssh_arguments(fixture)
    local, remote = workspace(fixture, "wc-p")
    prepared_file(remote / "probe", b"warmup bytes")
    prepared_file(local / "data", b"local-only submission")
    user: Path = local / ".temp/ssh-user-data"
    user.mkdir(parents=True)
    prepared_file(user / "retained", b"not control storage")
    target: Path = local.parent / "copy"
    source: str = (
        "remote => ssh://Perf_Fixture\n"
        f"{remote_path(remote / 'probe')} -> warmup\n"
        f"$local::{json.dumps(str(local))} -> $local::{json.dumps(str(target))}/.\n"
        "/quit\n"
    )
    (local / "input.txt").write_text(source)
    offset: int = arguments.stat().st_size if arguments.exists() else 0
    result: subprocess.CompletedProcess[bytes] = owned_run(
        [str(binary), "--no-config", "repl"], cwd=local,
        environment=fixture.environment(), source=source.encode(), timeout=45.0,
    )
    (local / "stdout.log").write_bytes(result.stdout)
    (local / "stderr.log").write_bytes(result.stderr)
    checked(result)
    assert b"error:" not in result.stderr, result.stderr.decode(errors="replace")
    verify_native_clients(arguments, offset, 1)
    verified_file(target / "data", b"local-only submission")
    verified_file(target / "warmup", b"warmup bytes")
    verified_file(target / ".temp/ssh-user-data/retained", b"not control storage")
    assert sorted(path.name for path in (target / ".temp").iterdir()) == ["ssh-user-data"]
    completed.append("later-local-only-repl-copy-retains-user-temp-without-new-ssh-work")


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / ".temp/v065/publication-ssh-results.json")
    selection: argparse._MutuallyExclusiveGroup = parser.add_mutually_exclusive_group()
    selection.add_argument("--whole-cwd-only", action="store_true", help="run whole-workspace, user-socket and later REPL copy cases")
    selection.add_argument("--repl-capture-only", action="store_true", help="run only the later local-only REPL submission copy")
    selection.add_argument("--cohorts-only", action="store_true", help="run only cohort copying, merging and retry semantics")
    selection.add_argument("--trace-only", action="store_true", help="run only one-exchange tracing in short and long workspaces")
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve(strict=True)
    output: Path = arguments.output.resolve()
    if not output.is_relative_to(ROOT):
        parser.error("keep result files inside the project")
    image: str = digest(binary)
    version: str = checked(owned_run([str(binary), "--version"], cwd=ROOT)).decode().strip()
    completed: list[str] = []
    fixture: Fixture = Fixture(ROOT / ".temp/v065")
    report: dict[str, object] = {
        "binary_sha256": image, "version": version, "harness_sha256": digest(Path(__file__)),
        "fixture": str(fixture.root), "completed": completed,
    }
    try:
        with fixture:
            if arguments.repl_capture_only:
                check_repl_capture(fixture, binary, completed)
            elif arguments.cohorts_only:
                check_cohorts(fixture, binary, completed)
            elif arguments.trace_only:
                check_upload_trace(fixture, binary, completed)
            else:
                if not arguments.whole_cwd_only:
                    check_cohorts(fixture, binary, completed)
                    check_upload_trace(fixture, binary, completed)
                check_whole_cwd(fixture, binary, completed)
                check_repl_capture(fixture, binary, completed)
        assert digest(binary) == image, "the identified test executable changed during acceptance"
        report["status"] = "passed"
    except BaseException as error:
        report["status"] = "failed"
        report["error"] = repr(error)
        raise
    finally:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
