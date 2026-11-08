#!/usr/bin/env python3
"""Exercise a packaged executable through source and both binary IR stages."""

from __future__ import annotations

import argparse
import os
import sqlite3
import subprocess
import sys
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def run(binary: Path, arguments: Sequence[str], cwd: Path) -> str:
    result = subprocess.run(
        [str(binary), *arguments, "--no-config"],
        cwd=cwd,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    return result.stdout


def legacy_dispatch(binary: Path, workspace: Path) -> None:
    environment = dict(os.environ)
    environment["MSYNC_LEGACY"] = sys.executable
    for option in ("--version", "--help"):
        expected = subprocess.run(
            [sys.executable, option],
            cwd=workspace,
            env=environment,
            capture_output=True,
            check=True,
        )
        delegated = subprocess.run(
            [str(binary), "legacy", option],
            cwd=workspace,
            env=environment,
            capture_output=True,
            check=True,
        )
        assert (delegated.stdout, delegated.stderr) == (
            expected.stdout,
            expected.stderr,
        ), ("Parent help/version intercepted the delegated baseline flags", option)


def journey(binary: Path, workspace: Path) -> None:
    payload = "installed artifact payload 可莉\n".encode()
    (workspace / "source.txt").write_bytes(payload)
    source = workspace / "sync.msync"
    source.write_text(
        "fn copy_one(source: Path, destination: Path) { source -> destination };\ncopy_one(source.txt, output.txt);\n",
        encoding="utf-8",
    )
    run(binary, ["check", "sync.msync"], workspace)
    run(binary, ["run", "sync.msync", "--dry-run"], workspace)
    assert not (workspace / "output.txt").exists(), "Dry-run wrote destination"
    run(binary, ["compile", "sync.msync", "-o", "sync.mso"], workspace)
    run(binary, ["bind-ir", "sync.mso", "-o", "sync.mexe"], workspace)
    run(binary, ["plan-ir", "sync.mexe"], workspace)
    assert not (workspace / "output.txt").exists(), (
        "Compilation/binding executed a transfer"
    )
    run(binary, ["run", "sync.msync"], workspace)
    assert (workspace / "output.txt").read_bytes() == payload
    source.unlink()
    for artifact in ("sync.mso", "sync.mexe"):
        (workspace / "output.txt").unlink()
        run(binary, ["run", artifact], workspace)
        assert (workspace / "output.txt").read_bytes() == payload, artifact
    (workspace / "output.txt").unlink()
    run(binary, ["run-ir", "sync.mexe"], workspace)
    assert (workspace / "output.txt").read_bytes() == payload
    database = workspace / "telemetry.sqlite"
    run(binary, ["run", "sync.mso", "--telemetry-db", str(database)], workspace)
    with sqlite3.connect(database) as connection:
        assert connection.execute("PRAGMA application_id").fetchone() == (0x4D53594E,)
        assert connection.execute("PRAGMA user_version").fetchone() == (1,)
        assert connection.execute("SELECT status FROM sessions").fetchall() == [
            ("complete",)
        ]
        names = {row[0] for row in connection.execute("SELECT name FROM records")}
        assert "run" in names and names & {"copy", "transfer"}, names
    legacy_dispatch(binary, workspace)
    print(
        "PASS: packaged source, dry-run, source-free .mso/.mexe, bound IR bytes and native SQLite telemetry"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    args = parser.parse_args()
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="package-smoke-", dir=temporary) as name:
        journey(args.binary.resolve(), Path(name))


if __name__ == "__main__":
    main()
