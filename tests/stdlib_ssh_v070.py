#!/usr/bin/env python3
"""Source-free filesystem and Scope-range journeys through actual OpenSSH."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT: Path = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from benches.ssh_v065 import Fixture, checked, lines, owned_run


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def invoke(
    fixture: Fixture, binary: Path, local: Path, arguments: list[str],
) -> subprocess.CompletedProcess[bytes]:
    return owned_run(
        [str(binary), '--no-config', *arguments], cwd=local,
        environment=fixture.environment(), timeout=45.0,
    )


def verify(fixture: Fixture, binary: Path) -> dict[str, object]:
    local: Path = fixture.root / 'local'
    remote: Path = fixture.root / "remote 可莉 ' $(not-a-command)\nworkspace"
    local.mkdir()
    remote_path: str = 'remote::' + json.dumps(str(remote), ensure_ascii=False)
    source: str = f'''fs => $std::fs
views => $std::ranges
remote => ssh://Perf_Fixture
root := {remote_path}
native directory(path) -> Bool = "fs.is_directory"
fs::create_directories(root)
fs::remove(root/link)
for endpoint in [remote] | views::take(1):
    "remote bytes 可莉" -> endpoint::{json.dumps(str(remote / 'source'), ensure_ascii=False)}
fs::rename(root/source, root/renamed)
fs::create_symlink("renamed", root/link)
fs::set_permissions(root/renamed, "owner_read|owner_write|group_read")
if directory(root) and fs::exists(root/renamed) and fs::is_symlink(root/link):
    "ready" -> marker
else:
    "wrong" -> marker
if fs::permissions(root/renamed) | views::contains("group_read"):
    "permissions" -> mode-marker
fs::string(fs::read_symlink(root/link)) -> link-target
boolean plain(file):
    return $std::fs::is_regular_file(file) and not $std::fs::is_symlink(file)
for file in fs::directory_iterator(root) | views::filter(plain):
    file -> received/
'''
    script: Path = local / 'entry.msync'
    script.write_text(source)
    before: int = len(lines(fixture.calls))
    for arguments in [
        ['check', 'entry.msync'], ['plan', 'entry.msync'],
        ['run', 'entry.msync', '--dry-run'],
        ['compile', 'entry.msync', '-o', 'entry.mobj'],
        ['emit-ir', 'entry.msync', '-o', 'entry.json'],
    ]:
        checked(invoke(fixture, binary, local, arguments))
    assert len(lines(fixture.calls)) == before, 'preview authenticated an endpoint'
    assert not remote.exists(), 'preview performed a native mutation'
    script.unlink()
    completed: list[str] = []
    for arguments in [['run', 'entry.mobj'], ['run-ir', 'entry.json']]:
        checked(invoke(fixture, binary, local, arguments))
        assert (local / 'marker').read_bytes() == b'ready'
        assert (local / 'mode-marker').read_bytes() == b'permissions'
        assert (local / 'link-target').read_bytes() == b'renamed'
        assert (remote / 'renamed').read_text() == 'remote bytes 可莉'
        assert (remote / 'renamed').stat().st_mode & 0o777 == 0o640
        assert (remote / 'link').readlink() == Path('renamed')
        assert (local / 'received/renamed').read_text() == 'remote bytes 可莉'
        completed.append(' '.join(arguments))
    (local / 'upload').write_bytes(b'one finite native SSH channel')
    transfer: Path = local / 'copy.msync'
    transfer.write_text(f'remote => ssh://Perf_Fixture\nupload -> {remote_path}/uploaded\n')
    before = len(lines(fixture.calls))
    before_log: int = len(lines(fixture.log))
    checked(invoke(fixture, binary, local, ['run', 'copy.msync']))
    calls: list[str] = lines(fixture.calls)[before:]
    logs: list[str] = lines(fixture.log)[before_log:]
    assert calls == ['ssh'], calls
    assert sum('Accepted publickey for ' in line for line in logs) == 1, logs
    assert sum('Starting session:' in line for line in logs) == 1, logs
    assert (remote / 'uploaded').read_bytes() == (local / 'upload').read_bytes()
    return {
        'source_free_journeys': completed,
        'preview_ssh_clients': 0,
        'finite_transfer_ssh_clients': 1,
        'finite_transfer_authentications': 1,
        'finite_transfer_remote_sessions': 1,
        'fixture': str(fixture.root),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('binary', type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / '.temp/v070/ssh-results.json')
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    report: dict[str, object] = {'binary_sha256': digest(binary)}
    try:
        with Fixture(ROOT / '.temp/v070/ssh') as fixture:
            report.update(verify(fixture, binary))
        report['status'] = 'passed'
    except BaseException as error:
        report['status'] = 'failed'
        report['error'] = repr(error)
        raise
    finally:
        output: Path = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
