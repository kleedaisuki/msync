"""Verify the private Number local lane with the CLI's three execution routes."""

from __future__ import annotations

import argparse
import sqlite3
import subprocess
import tempfile
from collections.abc import Sequence
from contextlib import closing
from pathlib import Path

HERE: Path = Path(__file__).resolve().parent
TEMP: Path = HERE.parents[1] / ".temp"


def invoke(
    binary: Path, directory: Path, args: Sequence[str], data: bytes = b""
) -> bytes:
    completed: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), "--no-config", "-O2", *args],
        cwd=directory,
        input=data,
        capture_output=True,
        check=False,
        timeout=30,
    )
    if completed.returncode != 0:
        raise RuntimeError(
            f"{args!r}: {completed.stderr.decode('utf-8', errors='replace')}"
        )
    return completed.stdout


def check(binary: Path, directory: Path, artifact: str, metrics: bool) -> None:
    database: Path = directory / f"{artifact}.sqlite"
    normal: bytes = invoke(
        binary, directory, ["--no-telemetry", "run", artifact], b"257\n"
    )
    output: bytes = invoke(
        binary,
        directory,
        [
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            "profile",
            "run",
            artifact,
        ],
        b"257\n",
    )
    if output.splitlines() != [b"257", b"0", b"0.25", b"0.75"]:
        raise AssertionError(f"{artifact}: unexpected output {output!r}")
    if normal != output:
        raise AssertionError(
            f"{artifact}: telemetry changed stdout: {normal!r} != {output!r}"
        )
    with closing(sqlite3.connect(database)) as db:
        if db.execute("SELECT status, exporter_error FROM sessions").fetchall() != [
            ("complete", None)
        ]:
            raise AssertionError(f"{artifact}: incomplete telemetry")
        counts: dict[str, int] = dict(
            db.execute(
                """SELECT m.key, SUM(m.value_int)
               FROM records r JOIN attributes a
                   ON a.record_id = r.id AND a.key = 'function.name'
               JOIN metrics m ON m.record_id = r.id
               WHERE r.name = 'vm.function' AND a.value = 'integrate'
               GROUP BY m.key"""
            ).fetchall()
        )
    if metrics and not (
        counts.get("raw_local_writes", 0) > 0
        and counts.get("numeric_local_commits", 0) > 0
    ):
        raise AssertionError(
            f"{artifact}: no private numeric stores/commits: {counts!r}"
        )
    print(
        f"PASS {artifact}: private writes={counts.get('raw_local_writes', 0)}, "
        f"numeric commits={counts.get('numeric_local_commits', 0)}, "
        f"native entries={counts.get('jit_entries', 0)}"
    )


def run(binary: Path, directory: Path, metrics: bool) -> None:
    source: Path = directory / "program.msync"
    portable: Path = directory / "program.mso"
    bound: Path = directory / "program.mexe"
    source.write_bytes((HERE / "typed_locals.msync").read_bytes())
    check(binary, directory, source.name, metrics)
    invoke(
        binary,
        directory,
        ["--no-telemetry", "compile", source.name, "-o", portable.name],
    )
    source.unlink()
    check(binary, directory, portable.name, metrics)
    invoke(
        binary,
        directory,
        ["--no-telemetry", "bind-ir", portable.name, "-o", bound.name],
    )
    portable.unlink()
    check(binary, directory, bound.name, metrics)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--semantic-only", action="store_true")
    args: argparse.Namespace = parser.parse_args()
    TEMP.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="typed-local-cli-", dir=TEMP) as directory:
        run(args.binary.resolve(strict=True), Path(directory), not args.semantic_only)


if __name__ == "__main__":
    main()
