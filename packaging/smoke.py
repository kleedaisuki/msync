#!/usr/bin/env python3
"""Exercise a packaged executable through source and both binary IR stages."""

from __future__ import annotations

import argparse
import sqlite3
import subprocess
import tempfile
from collections.abc import Sequence
from contextlib import closing
from functools import partial
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def run(
    binary: Path, arguments: Sequence[str], cwd: Path, runner: Path | None = None
) -> str:
    prefix = [str(runner)] if runner else []
    result = subprocess.run(
        [*prefix, str(binary), *arguments, "--no-config"],
        cwd=cwd,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    return result.stdout


def journey(binary: Path, workspace: Path, runner: Path | None = None) -> None:
    execute = partial(run, binary, cwd=workspace, runner=runner)
    payload = "installed artifact payload 可莉\n".encode()
    (workspace / "source.txt").write_bytes(payload)
    source = workspace / "sync.msync"
    source.write_text(
        "fn copy_one(source: Path, destination: Path) { source -> destination };\ncopy_one(source.txt, output.txt);\n",
        encoding="utf-8",
    )
    execute(["check", "sync.msync"])
    execute(["run", "sync.msync", "--dry-run"])
    assert not (workspace / "output.txt").exists(), "Dry-run wrote destination"
    execute(["compile", "sync.msync", "-o", "sync.mso"])
    execute(["bind-ir", "sync.mso", "-o", "sync.mexe"])
    execute(["plan-ir", "sync.mexe"])
    assert not (workspace / "output.txt").exists(), (
        "Compilation/binding executed a transfer"
    )
    execute(["run", "sync.msync"])
    assert (workspace / "output.txt").read_bytes() == payload
    source.unlink()
    for artifact in ("sync.mso", "sync.mexe"):
        (workspace / "output.txt").unlink()
        execute(["run", artifact])
        assert (workspace / "output.txt").read_bytes() == payload, artifact
    (workspace / "output.txt").unlink()
    execute(["run-ir", "sync.mexe"])
    assert (workspace / "output.txt").read_bytes() == payload
    database = workspace / "telemetry.sqlite"
    execute(
        [
            "run",
            "sync.mso",
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            "diagnostics",
        ]
    )
    with closing(sqlite3.connect(database)) as connection:
        assert connection.execute("PRAGMA application_id").fetchone() == (0x4D53594E,)
        assert connection.execute("PRAGMA user_version").fetchone() == (1,)
        assert connection.execute("SELECT status FROM sessions").fetchall() == [
            ("complete",)
        ]
        names = {row[0] for row in connection.execute("SELECT name FROM records")}
        assert {"run", "hpir.effect.transfer"} <= names, names
    print(
        "PASS: packaged source, dry-run, source-free .mso/.mexe, bound IR bytes and native SQLite telemetry"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--runner", type=Path)
    args = parser.parse_args()
    runner: Path | None = args.runner
    if runner is not None and runner.is_file():
        runner = runner.resolve()
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="package-smoke-", dir=temporary) as name:
        journey(args.binary.resolve(), Path(name), runner)


if __name__ == "__main__":
    main()
