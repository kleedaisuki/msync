#!/usr/bin/env python3
"""Check diagnostic publication decisions over real OpenSSH with both mv capabilities."""

from __future__ import annotations

import argparse
import os
import shlex
import sqlite3
import subprocess
import sys
from contextlib import closing
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/support"))
from ssh_fixture import Fixture, lines


class WrappedMv(Fixture):
    wrapper: str

    def write_wrappers(self) -> None:
        super().write_wrappers()
        remote = self.root / "remote-bin"
        remote.mkdir()
        mv = remote / "mv"
        mv.write_text("#!/bin/sh\n" + self.wrapper + '\nexec /usr/bin/mv "$@"\n')
        mv.chmod(0o700)
        dispatch = self.root / "dispatch"
        dispatch.write_text(
            f'#!/bin/sh\nexport PATH={shlex.quote(str(remote))}:"$PATH"\n'
            'exec /bin/sh -c "$SSH_ORIGINAL_COMMAND"\n'
        )
        dispatch.chmod(0o700)
        with (self.root / "sshd_config").open("a") as config:
            config.write(f"ForceCommand {dispatch}\n")


class LegacyMv(WrappedMv):
    wrapper = 'if [ "$1" = --help ]; then printf "legacy mv\\n"; exit 0; fi'


class RacedMv(WrappedMv):
    wrapper = """case "$1" in
        --help) printf '%s\\n' --no-copy; exit 0 ;;
        --no-copy)
            mkdir -- "$5"
            printf keep > "$5/receiver-only"
            exit 0 ;;
    esac"""


def check(binary: Path, fixture: Fixture, supports_rename: bool) -> None:
    root = fixture.root
    source = root / "src" / "tree"
    (source / "nested").mkdir(parents=True)
    (source / "nested" / "hidden\nname").write_bytes(b"original")
    (source / "dangling").symlink_to("missing")
    target = root / "published" / "tree"
    (root / "copy.msync").write_text(
        f'let peer = ssh://Perf_Fixture; ./src/tree -> (peer @ "{target.parent}/");\n'
    )
    timestamp = 1_700_000_000_123_456_789
    for phase in ("first", "merge"):
        if phase == "merge":
            (target / "receiver-only").write_bytes(b"keep")
            (source / "nested" / "hidden\nname").write_bytes(b"changed")
        os.utime(source / "nested", ns=(timestamp, timestamp))
        os.utime(source, ns=(timestamp, timestamp))
        database = root / f"{phase}.sqlite"
        before = len(lines(fixture.calls))
        subprocess.run(
            [
                str(binary),
                "--no-config",
                "--telemetry-level",
                "diagnostics",
                "--telemetry-db",
                str(database),
                "run",
                "copy.msync",
            ],
            cwd=root,
            env=fixture.environment(),
            check=True,
            capture_output=True,
            timeout=30,
        )
        assert lines(fixture.calls)[before:] == ["ssh"]
        assert (
            target.stat().st_mtime_ns
            == (target / "nested").stat().st_mtime_ns
            == timestamp
        )
        assert (target / "nested" / "hidden\nname").read_bytes() == (
            b"original" if phase == "first" else b"changed"
        )
        assert (target / "dangling").readlink() == Path("missing")
        assert not list(target.parent.glob(".msync-*"))
        if phase == "merge" or isinstance(fixture, RacedMv):
            assert (target / "receiver-only").read_bytes() == b"keep"
        with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as db:
            assert db.execute("select status from sessions").fetchall() == [
                ("complete",)
            ]
            metrics = dict(
                db.execute(
                    """SELECT key,value_int FROM spans JOIN metrics ON id=record_id
                WHERE name='transport.install.result' AND status='ok'"""
                )
            )
            fast = phase == "first" and supports_rename
            fallback = phase == "first" and not supports_rename
            assert metrics == {
                "install.subtrees_renamed": int(fast),
                "install.subtree_fallbacks": int(fallback),
                "install.directories_merged": 0 if fast else 2,
                "install.leaves_moved": 0 if fast else 2,
            }, metrics
            assert (
                db.execute("""SELECT count(*) FROM spans
                WHERE name='transport.receipt_wait' AND status='ok'""").fetchone()
                == (1,)
            )
    print(
        f"PASS {type(fixture).__name__}: fresh install, checked merge, diagnostic receipt"
    )


def check_peer_installs(binary: Path, fixture: Fixture) -> None:
    root = fixture.root
    fixture.config.write_text(
        fixture.config.read_text().replace(
            "Host Perf_Fixture\n", "Host Perf_Fixture Perf_Relay\n"
        )
    )
    source = root / "src" / "tree"
    (root / "peer.msync").write_text(
        "let peer = ssh://Perf_Fixture; let relay = ssh://Perf_Relay;\n"
        f'(peer @ "{source}") -> (peer @ "{root}/peer-copy/");\n'
        f'(peer @ "{source}") -> (relay @ "{root}/peer-relay/");\n'
    )
    database = root / "peer.sqlite"
    before = len(lines(fixture.calls))
    subprocess.run(
        [
            str(binary),
            "--no-config",
            "--telemetry-level",
            "diagnostics",
            "--telemetry-db",
            str(database),
            "run",
            "peer.msync",
        ],
        cwd=root,
        env=fixture.environment(),
        check=True,
        capture_output=True,
        timeout=30,
    )
    assert lines(fixture.calls)[before:] == ["ssh"] * 3
    for kind in ("copy", "relay"):
        target = root / f"peer-{kind}/tree"
        assert (target / "dangling").readlink() == Path("missing")
        assert (target / "nested" / "hidden\nname").read_bytes() == b"changed"
        assert target.stat().st_mtime_ns == source.stat().st_mtime_ns
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as db:
        assert db.execute("""SELECT value_int FROM spans JOIN metrics ON id=record_id
            WHERE name='transport.install.result' AND key='install.subtrees_renamed'
            AND status='ok'""").fetchall() == [(0,), (0,)]
    print("PASS remote copy/relay: recursive publication and checked receipts")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    binary: Path = parser.parse_args().binary.resolve(strict=True)
    parent = ROOT / ".temp" / "remote-publication"
    capabilities = subprocess.check_output(["/usr/bin/mv", "--help"], text=True)
    with Fixture(parent) as fixture:
        check(binary, fixture, "--no-copy" in capabilities)
        check_peer_installs(binary, fixture)
    with LegacyMv(parent) as fixture:
        check(binary, fixture, False)
    with RacedMv(parent) as fixture:
        check(binary, fixture, False)


if __name__ == "__main__":
    main()
