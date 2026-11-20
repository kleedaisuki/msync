#!/usr/bin/env python3
"""Check the published prefix and stage ownership after a real write failure."""

from __future__ import annotations

import argparse
import os
import sqlite3
import subprocess
import sys
import tempfile
from contextlib import closing
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/support"))
from ssh_fixture import Fixture


def check(
    binary: Path, root: Path, environment: dict[str, str], download: bool
) -> None:
    source = root / "source"
    target = root / "output" / source.name
    source.mkdir(parents=True)
    target.mkdir(parents=True)
    (target / ".msync-user-owned").write_bytes(b"keep")
    for index in range(8):
        (source / f"file-{index}").write_bytes(bytes([index]))
    with os.scandir(source) as entries:
        names = [entry.name for entry in entries]
    if not download:
        names.sort()
    blocked = names[2]
    (target / blocked).mkdir()
    (target / blocked / "owned").write_bytes(b"keep")
    script = (
        f'let remote = ssh://Perf_Fixture; (remote @ "{source}") -> ./output/;\n'
        if download
        else "./source -> ./output/;\n"
    )
    (root / "copy.msync").write_text(script)
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
            "copy.msync",
        ],
        cwd=root,
        env=environment,
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )
    assert result.returncode != 0 and blocked in result.stderr, result
    for name in names[:2]:
        assert (target / name).read_bytes() == (source / name).read_bytes(), name
    assert (target / blocked / "owned").read_bytes() == b"keep"
    assert {path.name for path in target.iterdir()} == {*names[:3], ".msync-user-owned"}
    assert (target / ".msync-user-owned").read_bytes() == b"keep"
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as db:
        assert db.execute("select status from sessions").fetchall() == [("complete",)]
        assert (
            db.execute("""SELECT max(value_int) FROM spans JOIN metrics ON id=record_id
            WHERE name='file.publication.wait' AND key='queue.depth'""").fetchone()
            == (4,)
        )
        assert db.execute("""SELECT count(*) FROM spans
            WHERE name='file.stage.cleanup' AND status='ok'""").fetchone()[0] >= 2
        assert (
            db.execute(
                """SELECT count(*) FROM spans child JOIN spans parent
            ON child.trace_id=parent.trace_id AND child.parent_span_id=parent.span_id
            WHERE child.name='file.sync' AND parent.name=?""",
                ("file.write" if download else "file.copy",),
            ).fetchone()[0]
            >= 4
        )
    print(
        f"PASS {'SSH download' if download else 'local'}: successful prefix, blocked member, staged-sibling cleanup"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    binary: Path = parser.parse_args().binary.resolve(strict=True)
    parent = ROOT / ".temp"
    with tempfile.TemporaryDirectory(prefix="publication-", dir=parent) as directory:
        check(binary, Path(directory), dict(os.environ), download=False)
    with Fixture(parent / "publication") as fixture:
        check(binary, fixture.root / "download", fixture.environment(), download=True)


if __name__ == "__main__":
    main()
