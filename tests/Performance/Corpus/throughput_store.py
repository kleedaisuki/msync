"""Single-owner benchmark store; product telemetry remains lossless schema-v1 data."""

from __future__ import annotations

import json
import sqlite3
from pathlib import Path
from typing import Any

SCHEMA: str = """
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA foreign_keys=ON;
PRAGMA user_version=1;
CREATE TABLE run(id INTEGER PRIMARY KEY CHECK(id=1), status TEXT NOT NULL, metadata_json TEXT NOT NULL);
CREATE TABLE batches(
 id INTEGER PRIMARY KEY, image TEXT NOT NULL, workload TEXT NOT NULL,
 route TEXT NOT NULL, seed INTEGER NOT NULL, concurrency INTEGER NOT NULL,
 repetition INTEGER NOT NULL, requested INTEGER NOT NULL, succeeded INTEGER NOT NULL,
 failed INTEGER NOT NULL, elapsed_ns INTEGER NOT NULL, child_user_seconds REAL NOT NULL,
 child_system_seconds REAL NOT NULL, status TEXT NOT NULL,
 p50_ms REAL, p95_ms REAL, p99_ms REAL);
CREATE TABLE samples(
 batch_id INTEGER NOT NULL REFERENCES batches(id), request INTEGER NOT NULL,
 worker INTEGER NOT NULL, started_ns INTEGER NOT NULL, finished_ns INTEGER NOT NULL,
 invocation_ms REAL, reset_ns INTEGER, verify_ns INTEGER, status TEXT NOT NULL, cwd TEXT NOT NULL,
 command_json TEXT NOT NULL, error_json TEXT,
 PRIMARY KEY(batch_id,request));
CREATE INDEX sample_latency ON samples(batch_id,status,invocation_ms);
CREATE TABLE profiles(
 id INTEGER PRIMARY KEY, image TEXT NOT NULL, workload TEXT NOT NULL,
 route TEXT NOT NULL, seed INTEGER NOT NULL, database_path TEXT NOT NULL,
 trace_id TEXT NOT NULL, process_id INTEGER NOT NULL, started_unix_ns INTEGER NOT NULL,
 ended_unix_ns INTEGER, status TEXT NOT NULL, exporter_error TEXT);
CREATE TABLE records(
 profile_id INTEGER NOT NULL REFERENCES profiles(id), id INTEGER NOT NULL,
 trace_id TEXT NOT NULL, kind TEXT NOT NULL, span_id TEXT NOT NULL,
 parent_span_id TEXT, name TEXT NOT NULL, start_unix_ns INTEGER, duration_ns INTEGER,
 timestamp_unix_ns INTEGER, status TEXT, completion TEXT, error_chain_truncated INTEGER NOT NULL,
 PRIMARY KEY(profile_id,id));
CREATE INDEX record_phase ON records(name,duration_ns DESC);
CREATE INDEX record_owner ON records(profile_id,trace_id,span_id);
CREATE TABLE attributes(profile_id INTEGER NOT NULL,record_id INTEGER NOT NULL,key TEXT NOT NULL,value TEXT NOT NULL,
 PRIMARY KEY(profile_id,record_id,key),FOREIGN KEY(profile_id,record_id) REFERENCES records(profile_id,id));
CREATE TABLE metrics(profile_id INTEGER NOT NULL,record_id INTEGER NOT NULL,key TEXT NOT NULL,value TEXT NOT NULL,value_int INTEGER,
 PRIMARY KEY(profile_id,record_id,key),FOREIGN KEY(profile_id,record_id) REFERENCES records(profile_id,id));
CREATE TABLE error_causes(profile_id INTEGER NOT NULL,record_id INTEGER NOT NULL,depth INTEGER NOT NULL,message TEXT NOT NULL,kind TEXT,raw_os_error INTEGER,
 PRIMARY KEY(profile_id,record_id,depth),FOREIGN KEY(profile_id,record_id) REFERENCES records(profile_id,id));
CREATE VIEW spans AS SELECT * FROM records WHERE kind='span';
CREATE VIEW events AS SELECT * FROM records WHERE kind='event';
CREATE VIEW throughput AS SELECT *,
 succeeded*1e9/elapsed_ns AS successful_requests_per_second,
 failed*1.0/requested AS failure_rate FROM batches;
"""


class Store:
    def __init__(self, path: Path, metadata: dict[str, Any]) -> None:
        self.db: sqlite3.Connection = sqlite3.connect(path.as_uri(), uri=True)
        self.db.executescript(SCHEMA)
        self.update("running", metadata)

    def update(self, status: str, metadata: dict[str, Any]) -> None:
        with self.db:
            self.db.execute(
                "INSERT OR REPLACE INTO run VALUES(1,?,?)",
                (status, json.dumps(metadata)),
            )

    def batch(self, row: dict[str, Any], samples: list[dict[str, Any]]) -> int:
        with self.db:
            cursor: sqlite3.Cursor = self.db.execute(
                """INSERT INTO batches(image,workload,route,seed,concurrency,repetition,
                requested,succeeded,failed,elapsed_ns,child_user_seconds,child_system_seconds,status,p50_ms,p95_ms,p99_ms)
                VALUES(:image,:workload,:route,:seed,:concurrency,:repetition,:requested,
                :succeeded,:failed,:elapsed_ns,:child_user_seconds,:child_system_seconds,:status,:p50_ms,:p95_ms,:p99_ms)""",
                row,
            )
            identity: int = int(cursor.lastrowid or 0)
            self.db.executemany(
                "INSERT INTO samples VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                (
                    (
                        identity,
                        sample["request"],
                        sample["worker"],
                        sample["started_ns"],
                        sample["finished_ns"],
                        sample["invocation_ms"],
                        sample["reset_ns"],
                        sample["verify_ns"],
                        sample["status"],
                        sample["cwd"],
                        json.dumps(sample["command"]),
                        json.dumps(sample["error"])
                        if sample["error"] is not None
                        else None,
                    )
                    for sample in samples
                ),
            )
        return identity

    def profile(
        self, path: Path, image: str, workload: str, route: str, seed: int
    ) -> None:
        # ATTACH copies on the SQLite engine, without materializing millions of rows in Python.
        self.db.execute("ATTACH DATABASE ? AS detail", (path.as_uri() + "?mode=ro",))
        try:
            with self.db:
                sessions: list[tuple[str, int, int, int | None, str, str | None]] = (
                    self.db.execute(
                        "SELECT trace_id,process_id,started_unix_ns,ended_unix_ns,status,exporter_error FROM detail.sessions"
                    ).fetchall()
                )
                if len(sessions) != 1:
                    raise RuntimeError(
                        f"Expected one recording session in {path}: {sessions}"
                    )
                trace, pid, started, ended, status, error = sessions[0]
                cursor: sqlite3.Cursor = self.db.execute(
                    "INSERT INTO profiles(image,workload,route,seed,database_path,trace_id,process_id,started_unix_ns,ended_unix_ns,status,exporter_error) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                    (
                        image,
                        workload,
                        route,
                        seed,
                        str(path),
                        trace,
                        pid,
                        started,
                        ended,
                        status,
                        error,
                    ),
                )
                identity: int = int(cursor.lastrowid or 0)
                for table in ("records", "attributes", "metrics", "error_causes"):
                    self.db.execute(
                        f"INSERT INTO {table} SELECT ?,d.* FROM detail.{table} d",
                        (identity,),
                    )
        finally:
            self.db.execute("DETACH DATABASE detail")
        if status != "complete" or error is not None:
            raise RuntimeError(f"Incomplete recording at {path}: {status}: {error}")

    def close(self) -> None:
        self.db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        self.db.close()
