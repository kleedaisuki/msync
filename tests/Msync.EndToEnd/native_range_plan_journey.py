#!/usr/bin/env python3
"""Exercise native lazy-plan sharing and cursor restart in a detached CLI run."""
from __future__ import annotations

import argparse
import os
import sqlite3
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """
let r = $std::ranges;
let incoming? = $std::io::read_line();
if incoming is not null {
    let source = r::sequence(0, incoming as Number, 1);
    let plan = r::concat(r::take(r::stride(r::drop(source, 1), 2), 3), r::repeat(9, 2));
    let first = 0;
    for item in plan { first = first * 10 + item; };
    let second = 0;
    for item in plan { second = second + item; if item == 3 { break; }; };
    $std::io::println(first as String);
    $std::io::println(second as String);
    for unreachable in r::take(r::sequence(9007199254740992, 9007199254740994, 1), 0) {
        $std::io::println("unexpected");
    };
};
let stopped = r::sequence(9007199254740992, 9007199254740994, 1);
for unreachable in r::drop(r::take(stopped, 1), 1) {
    $std::io::println("unexpected");
};
let text? = $std::io::read_line();
if text is not null {
    for char in r::take($std::string::chars(text), 2) { $std::io::println(char); };
    for word in r::take($std::string::split_whitespace(text), 1) { $std::io::println(word); };
};
"""


def verify(binary: Path, work: Path) -> None:
    base: list[str] = [str(binary), "--no-config", "--color", "never"]
    source: Path = work / "plan.msync"
    source.write_text(SOURCE, encoding="utf-8")
    for stage in (
        ["compile", "-O2", source.name, "-o", "plan.mso"],
        ["bind-ir", "plan.mso", "-o", "plan.mexe"],
    ):
        subprocess.run([*base, "--no-telemetry", *stage], cwd=work, check=True, timeout=30)
    source.unlink()
    (work / "plan.mso").unlink()

    fault: Path = work / "fence.msync"
    fault.write_text("""
let r = $std::ranges;
let source = r::sequence(9007199254740992, 9007199254740994, 1);
for item in r::drop(r::take(source, 2), 2) { $std::io::println("unexpected"); };
""", encoding="utf-8")
    subprocess.run([*base, "--no-telemetry", "compile", "-O2", fault.name, "-o", "fence.mso"],
                   cwd=work, check=True, timeout=30)
    subprocess.run([*base, "--no-telemetry", "bind-ir", "fence.mso", "-o", "fence.mexe"],
                   cwd=work, check=True, timeout=30)
    fault.unlink()
    (work / "fence.mso").unlink()

    for tier in ("off", "auto"):
        db: Path = work / f"{tier}.sqlite"
        result: subprocess.CompletedProcess[str] = subprocess.run(
            [*base, "--telemetry-level", "profile", "--telemetry-db", str(db), "run", "plan.mexe"],
            cwd=work, env={**os.environ, "MSYNC_JIT": tier}, input="8\nβ🙂  z\n", text=True,
            check=True, capture_output=True, timeout=30,
        )
        assert result.stdout.splitlines() == ["13599", "4", "β", "🙂", "β🙂"] and not result.stderr, result
        with sqlite3.connect(db) as connection:
            counts: dict[str, int] = dict(connection.execute("""
                SELECT key,sum(value_int) FROM metrics WHERE key LIKE 'native_range_%' GROUP BY key
            """))
        if sys.maxsize > 2**32:
            assert counts["native_range_plan_edges"] >= 10 and counts["native_range_cursor_opens"] == 6, counts
            assert counts["native_range_window_fusions"] >= 3 and counts["native_range_window_kernels"] >= 2, counts
            assert counts["native_range_cursor_yields"] == 10, counts
        stalled: subprocess.CompletedProcess[str] = subprocess.run(
            [*base, "--no-telemetry", "run", "fence.mexe"], cwd=work,
            env={**os.environ, "MSYNC_JIT": tier}, text=True, capture_output=True, timeout=30, check=False,
        )
        assert stalled.returncode != 0 and not stalled.stdout and "step no longer advances" in stalled.stderr, stalled
        print(tier, counts)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="native-range-plan-") as temporary:
        verify(args.binary.resolve(), Path(temporary))


if __name__ == "__main__":
    main()
