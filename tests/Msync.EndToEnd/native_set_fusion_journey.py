#!/usr/bin/env python3
"""Check that bound Set keeps a live order alias while the VM fuses Scope copies."""
from __future__ import annotations

import argparse
import os
import sqlite3
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]
FIXTURE: Path = ROOT / "tests/Performance/NativeJit/method-set-live.msync"


def journey(binary: Path, work: Path) -> None:
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    source: Path = work / "live.msync"
    source.write_bytes(FIXTURE.read_bytes())
    for stage in (
        ["compile", "-O2", source.name, "-o", "live.mso"],
        ["bind-ir", "live.mso", "-o", "live.mexe"],
    ):
        subprocess.run([*cli, "--no-telemetry", *stage], cwd=work, check=True, timeout=30)
    source.unlink()
    (work / "live.mso").unlink()

    for tier in ("off", "auto"):
        database: Path = work / f"{tier}.sqlite"
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            [*cli, "run", "live.mexe", "--telemetry-db", str(database), "--telemetry-level", "profile"],
            cwd=work, env={**os.environ, "MSYNC_JIT": tier}, input=b"2048\n4096\n",
            check=True, capture_output=True, timeout=30,
        )
        assert result.stdout.splitlines() == [b"2048", b"4096"] and not result.stderr, result
        with sqlite3.connect(database) as connection:
            count: int = connection.execute("""
                SELECT coalesce(sum(m.value_int), 0)
                FROM metrics m JOIN attributes a ON a.record_id = m.record_id
                WHERE m.key = 'vm_scope_copies_forwarded'
                  AND a.key = 'function.name' AND a.value GLOB '_set_*'
            """).fetchone()[0]
        assert count > 0, f"{tier}: Set never used the linked Scope fusion"
        print(f"{tier}: {count} forwarded Set copies; live order alias preserved")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    options: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="native-set-fusion-") as temporary:
        journey(options.binary.resolve(), Path(temporary))


if __name__ == "__main__":
    main()
