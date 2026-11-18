#!/usr/bin/env python3
"""Run native callback, ordered Range and text/Path operations from a bound artifact."""
from __future__ import annotations

import argparse
import os
import sqlite3
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """
let r = $std::ranges;
let io = $std::io;
let seq = r::sequence(0, 6, 1);
let input = r::transform(seq, fn(x: Number) { x * 2 });
let filtered = r::filter(input, fn(x: Number) { x > 4 });
for x in r::take(filtered, 2) { io::println(x as String); };
let records = r::transform([3, 1, 2], fn(x: Number) { scope { value: x } });
for record in records { io::println(record::value as String); };
let merged = r::merge(r::sort([8, 4, 6]), r::sort([7, 3, 5]));
for x in merged { io::println(x as String); };
for word in r::sort(["β", "a", "🙂"]) { io::println(word); };

let emitted = 0;
let generated = r::generate(4, fn(index: Number) {
    emitted = emitted + 1;
    index * 3
});
let frozen = r::materialize(generated);
for x in frozen { io::println(x as String); };
for x in frozen { io::println(x as String); };
io::println(emitted as String);
let deferred = r::of(io::read_text(./input.txt));
io::print(r::first(r::materialize(deferred)));

let text? = io::read_line();
if text is not null {
    let lower = $std::string::to_lowercase(text);
    let upper = $std::string::to_uppercase(text);
    io::println(lower);
    io::println(upper);
    io::println($std::string::replace($std::string::repeat(text, 2), "Σ", "🙂"));
    let base = ./out;
    let joined = $std::path::join(base, text);
    let other = $std::path::join(base, "ΟΣ");
    fn same(left: Path, right: Path) { left == right };
    io::println(same(joined, other) as String);
};
"""
EXPECTED: list[str] = ["6", "8", "3", "1", "2", "3", "4", "5", "6", "7", "8", "a", "β", "🙂",
                       "0", "3", "6", "9", "0", "3", "6", "9", "4", "deferred",
                       "ος", "ΟΣ", "Ο🙂Ο🙂", "true"]


def journey(binary: Path, work: Path) -> None:
    base: list[str] = [str(binary), "--no-config", "--color", "never"]
    source: Path = work / "program.msync"
    source.write_text(SOURCE, encoding="utf-8")
    (work / "input.txt").write_text("deferred\n", encoding="utf-8")
    for stage in (
        ["compile", "-O2", source.name, "-o", "program.mso"],
        ["bind-ir", "program.mso", "-o", "program.mexe"],
    ):
        subprocess.run([*base, "--no-telemetry", *stage], cwd=work, check=True, timeout=30)
    source.unlink()
    (work / "program.mso").unlink()
    for tier in ("off", "auto"):
        database: Path = work / f"{tier}.sqlite"
        result: subprocess.CompletedProcess[str] = subprocess.run(
            [*base, "--telemetry-level", "diagnostics", "--telemetry-db", str(database), "run", "program.mexe"],
            cwd=work, env={**os.environ, "MSYNC_JIT": tier}, input="ΟΣ\n",
            text=True, check=True, capture_output=True, timeout=30,
        )
        assert result.stdout.splitlines() == EXPECTED and not result.stderr, result
        with sqlite3.connect(database) as connection:
            counts: dict[str, int] = dict(connection.execute("""
                SELECT key,sum(value_int) FROM metrics WHERE key LIKE 'native_range_%'
                  OR key IN ('native_path_joins', 'native_text_comparisons') GROUP BY key
            """))
            materialized: list[tuple[int]] = connection.execute("""
                SELECT metrics.value_int FROM records
                JOIN metrics ON metrics.record_id = records.id
                WHERE records.name = 'range.materialize.datum' AND metrics.key = 'elements.observed'
            """).fetchall()
        assert materialized == [(1,)], materialized
        assert counts["native_range_ordered_nodes"] == 4, counts
        assert counts["native_range_callback_requests"] == counts["native_range_callback_resumes"] == 13, counts
        if os.name == "posix":
            assert counts["native_path_joins"] > 0 and counts["native_text_comparisons"] > 0, counts
        print(tier, counts)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    options: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="native-range-callback-") as temporary:
        journey(options.binary.resolve(), Path(temporary))


if __name__ == "__main__":
    main()
