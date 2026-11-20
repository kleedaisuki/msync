#!/usr/bin/env python3
"""Check queued NTFS directory publication without replacing a blocked member."""

from __future__ import annotations

import argparse
import os
import sqlite3
import stat
import subprocess
import tempfile
from contextlib import closing
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def run(binary: Path, root: Path) -> None:
    source = root / "source"
    source.mkdir()
    target = root / "output" / source.name
    (target / "file-02").mkdir(parents=True)
    (target / "file-02" / "receiver").write_bytes(b"keep")
    (target / ".msync-user-owned").write_bytes(b"keep")
    stamp = 1_700_000_000_123_456_700
    for index in range(8):
        path = source / f"file-{index:02d}"
        path.write_bytes(bytes([index]))
        Path(str(path) + ":msync.test").write_bytes(b"native alternate stream")
        os.utime(path, ns=(stamp, stamp))
        if index in (0, 4):
            path.chmod(stat.S_IREAD)
    (root / "tree.msync").write_text("./source -> ./output/;\n", encoding="utf-8")
    database = root / "trace.sqlite"
    result = subprocess.run(
        [
            str(binary),
            "--no-config",
            "--telemetry-level",
            "diagnostics",
            "--telemetry-db",
            str(database),
            "run",
            "tree.msync",
        ],
        cwd=root,
        capture_output=True,
        check=False,
        timeout=30,
    )
    assert result.returncode == 1, result
    assert {path.name for path in target.iterdir()} == {
        "file-00",
        "file-01",
        "file-02",
        ".msync-user-owned",
    }
    assert (target / "file-02" / "receiver").read_bytes() == b"keep"
    assert (target / ".msync-user-owned").read_bytes() == b"keep"
    for index in (0, 1):
        published = target / f"file-{index:02d}"
        assert published.read_bytes() == bytes([index])
        assert (
            Path(str(published) + ":msync.test").read_bytes()
            == b"native alternate stream"
        )
        assert published.stat().st_mtime_ns == stamp
        assert bool(published.stat().st_mode & stat.S_IWRITE) == (index != 0)
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as sql:
        assert sql.execute("select status from sessions").fetchall() == [("complete",)]
        assert (
            sql.execute("""select max(value_int) from spans join metrics on record_id=id
            where name='file.publication.wait' and key='queue.depth'""").fetchone()
            == (4,)
        )
        assert sql.execute("""select count(*) from spans
            where name='file.stage.cleanup' and status='ok'""").fetchone()[0] >= 2
    print(
        "NTFS queued copy preserved ADS, readonly, timestamps, ordered prefix and owned-stage lifetime"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    binary: Path = parser.parse_args().binary.resolve(strict=True)
    if os.name != "nt":
        parser.error("This contract needs a real Windows NTFS host")
    parent = ROOT / ".temp"
    parent.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        dir=parent, ignore_cleanup_errors=True
    ) as directory:
        run(binary, Path(directory))


if __name__ == "__main__":
    main()
