"""Check literal demand and trace accounting through actual CLI artifact journeys."""

from __future__ import annotations

import argparse
import shutil
import sqlite3
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
FIXTURE: Path = Path(__file__).with_name("literal-demand.msync")
EXPECTED: tuple[str, ...] = ("9", "1", "literal", "0", "1", "1", "2", "2")


def run(binary: Path, work: Path, arguments: Sequence[str]) -> str:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=work,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"{arguments}: exit={result.returncode}, stdout={result.stdout!r}, "
            f"stderr={result.stderr!r}"
        )
    return result.stdout


def check_route(binary: Path, work: Path, filename: str, route: str) -> None:
    database: Path = work / f"{route}.sqlite"
    output: str = run(
        binary,
        work,
        [
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            "diagnostics",
            "run",
            filename,
        ],
    )
    assert tuple(output.splitlines()) == EXPECTED, (route, output)
    with sqlite3.connect(database) as connection:
        recipes: list[tuple[int, int]] = connection.execute(
            """
            SELECT CAST(m.value AS INTEGER), CAST(frames.value AS INTEGER)
              FROM metrics AS m
              JOIN records AS r ON r.id = m.record_id
              JOIN metrics AS frames ON frames.record_id = r.id AND frames.key = 'calls'
             WHERE r.name = 'vm.function' AND m.key = 'literal_factories'
               AND CAST(m.value AS INTEGER) > 0
            """
        ).fetchall()
        spans: tuple[int] | None = connection.execute(
            "SELECT count(*) FROM records WHERE name = 'vm.literal_scope'"
        ).fetchone()
    assert recipes == [(4, 0)] and spans == (4,), (route, recipes, spans)
    print(f"PASS: {route} preserves demand, identity and four recipe spans")


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    binary: Path = parser.parse_args().binary.resolve(strict=True)
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="literal-scope-journey-", dir=scratch
    ) as path:
        work: Path = Path(path)
        source: Path = work / FIXTURE.name
        shutil.copyfile(FIXTURE, source)
        check_route(binary, work, source.name, "source")
        run(
            binary,
            work,
            ["--no-telemetry", "compile", source.name, "-o", "literal.mso"],
        )
        source.unlink()
        check_route(binary, work, "literal.mso", "portable")
        run(
            binary,
            work,
            ["--no-telemetry", "bind-ir", "literal.mso", "-o", "literal.mexe"],
        )
        (work / "literal.mso").unlink()
        check_route(binary, work, "literal.mexe", "bound")


if __name__ == "__main__":
    main()
