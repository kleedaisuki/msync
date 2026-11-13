"""Check real throughput receipts, timing properties and lossless telemetry export."""

from __future__ import annotations

import argparse
import json
import math
import sqlite3
from contextlib import closing
from pathlib import Path
from typing import Any

from throughput import percentile


def check(directory: Path, partial: bool) -> dict[str, Any]:
    report: dict[str, Any] = json.loads((directory / "result.json").read_text())
    assert partial or report["status"] == "complete", report.get("error")
    scope: dict[str, Any] = report["resource_scope"]
    assert len(scope["cpus"]) <= 12
    assert scope["memory_max_bytes"] <= 24 * 1024**3
    assert scope["cpu_quota"] <= 12 * scope["cpu_period"]
    with closing(
        sqlite3.connect((directory / "benchmark.sqlite").as_uri(), uri=True)
    ) as db:
        assert db.execute("PRAGMA foreign_key_check").fetchall() == []
        assert db.execute("PRAGMA quick_check").fetchall() == [("ok",)]
        count: int = 0
        for row in report["batches"]:
            samples: list[tuple[int, int, int, int, float | None, str, str]] = (
                db.execute(
                    "SELECT request,worker,started_ns,finished_ns,invocation_ms,status,cwd FROM samples WHERE batch_id=? ORDER BY request",
                    (row["id"],),
                ).fetchall()
            )
            assert len(samples) == row["requested"]
            assert [sample[0] for sample in samples] == list(range(row["requested"]))
            assert len({sample[6] for sample in samples}) == row["concurrency"]
            assert max(sample[3] for sample in samples) == row["elapsed_ns"]
            assert all(0 <= sample[2] < sample[3] for sample in samples)
            assert (
                sum(sample[5] == "complete" for sample in samples) == row["succeeded"]
            )
            values: list[float] = [
                sample[4]
                for sample in samples
                if sample[5] == "complete" and sample[4] is not None
            ]
            for quantile, label in ((0.5, "p50"), (0.95, "p95"), (0.99, "p99")):
                if label + "_ms" in row and values:
                    assert math.isclose(
                        percentile(values, quantile), row[label + "_ms"]
                    )
            assert math.isclose(
                row["successful_requests_per_second"],
                row["succeeded"] * 1e9 / row["elapsed_ns"],
            )
            count += len(samples)
        for identity, path in db.execute(
            "SELECT id,database_path FROM profiles"
        ).fetchall():
            db.execute(
                "ATTACH DATABASE ? AS original", (Path(path).as_uri() + "?mode=ro",)
            )
            for table in ("records", "attributes", "metrics", "error_causes"):
                columns: list[str] = [
                    column[1]
                    for column in db.execute(f"PRAGMA original.table_info({table})")
                ]
                projection: str = ",".join(columns)
                source: str = f"SELECT {projection} FROM original.{table}"
                exported: str = (
                    f"SELECT {projection} FROM {table} WHERE profile_id={int(identity)}"
                )
                assert db.execute(f"{source} EXCEPT {exported}").fetchone() is None, (
                    path,
                    table,
                    "missing",
                )
                assert db.execute(f"{exported} EXCEPT {source}").fetchone() is None, (
                    path,
                    table,
                    "extra",
                )
            original_session: tuple[Any, ...] = (
                db.execute("SELECT * FROM original.sessions").fetchone() or ()
            )
            exported_session: tuple[Any, ...] = (
                db.execute(
                    "SELECT trace_id,process_id,started_unix_ns,ended_unix_ns,status,exporter_error FROM profiles WHERE id=?",
                    (identity,),
                ).fetchone()
                or ()
            )
            assert original_session == exported_session
            db.execute("DETACH DATABASE original")
        return {
            "directory": str(directory),
            "status": report["status"],
            "samples": count,
            "batches": db.execute("SELECT COUNT(*) FROM batches").fetchone()[0],
            "profiles": db.execute("SELECT COUNT(*) FROM profiles").fetchone()[0],
            "records": db.execute("SELECT COUNT(*) FROM records").fetchone()[0],
            "memory_peak": report["resource_usage"]["memory.peak"],
        }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument(
        "--partial",
        action="store_true",
        help="Inspect retained completed batches from an explicitly failed run",
    )
    args: argparse.Namespace = parser.parse_args()
    print(json.dumps(check(args.directory.resolve(), args.partial), indent=2))


if __name__ == "__main__":
    main()
