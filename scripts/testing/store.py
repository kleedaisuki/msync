"""Controller-owned SQLite results, phase decisions and lossless byte logs."""

from __future__ import annotations

import json
import sqlite3
import time
from pathlib import Path
from typing import Any

from .model import Task

SCHEMA: str = """
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA foreign_keys=ON;
PRAGMA user_version=1;
CREATE TABLE run(id INTEGER PRIMARY KEY CHECK(id=1),status TEXT NOT NULL,metadata_json TEXT NOT NULL);
CREATE TABLE tasks(id INTEGER PRIMARY KEY,name TEXT UNIQUE NOT NULL,spec_json TEXT NOT NULL,status TEXT NOT NULL,
 command_json TEXT,cwd TEXT,queued_ns INTEGER NOT NULL,started_ns INTEGER,finished_ns INTEGER,
 process_id INTEGER,exit_code INTEGER,error TEXT,resources_json TEXT,stdout_path TEXT,stderr_path TEXT,receipt_path TEXT);
CREATE TABLE phases(id INTEGER PRIMARY KEY,name TEXT NOT NULL,task_id INTEGER REFERENCES tasks(id),timestamp_ns INTEGER NOT NULL,details_json TEXT NOT NULL);
CREATE TABLE output_chunks(task_id INTEGER NOT NULL REFERENCES tasks(id),stream TEXT NOT NULL,offset INTEGER NOT NULL,data BLOB NOT NULL,
 PRIMARY KEY(task_id,stream,offset));
CREATE INDEX phase_owner ON phases(task_id,timestamp_ns);
CREATE VIEW outcomes AS SELECT *, (started_ns-queued_ns)/1e6 AS queue_ms,
 (finished_ns-started_ns)/1e6 AS elapsed_ms FROM tasks;
"""


class Store:
    def __init__(self, path: Path, metadata: dict[str, Any]) -> None:
        self.db: sqlite3.Connection = sqlite3.connect(path)
        self.db.executescript(SCHEMA)
        self.update("running", metadata)

    def update(self, status: str, metadata: dict[str, Any]) -> None:
        with self.db:
            self.db.execute(
                "INSERT OR REPLACE INTO run VALUES(1,?,?)",
                (status, json.dumps(metadata)),
            )

    def register(self, tasks: list[Task], start: int = 0) -> None:
        with self.db:
            self.db.executemany(
                "INSERT INTO tasks(id,name,spec_json,status,queued_ns) VALUES(?,?,?,?,?)",
                (
                    (
                        number,
                        task.name,
                        json.dumps(task.describe()),
                        "queued",
                        time.perf_counter_ns(),
                    )
                    for number, task in enumerate(tasks, start=start)
                ),
            )

    def event(
        self, name: str, details: dict[str, Any], task_id: int | None = None
    ) -> None:
        with self.db:
            self.db.execute(
                "INSERT INTO phases(name,task_id,timestamp_ns,details_json) VALUES(?,?,?,?)",
                (name, task_id, time.perf_counter_ns(), json.dumps(details)),
            )

    def start(
        self, number: int, command: list[str], cwd: Path, stdout: Path, stderr: Path
    ) -> None:
        with self.db:
            self.db.execute(
                "UPDATE tasks SET status='running',started_ns=?,command_json=?,cwd=?,stdout_path=?,stderr_path=? WHERE id=?",
                (
                    time.perf_counter_ns(),
                    json.dumps(command),
                    str(cwd),
                    str(stdout),
                    str(stderr),
                    number,
                ),
            )

    def finish(self, number: int, row: dict[str, Any]) -> None:
        with self.db:
            self.db.execute(
                "UPDATE tasks SET status=?,finished_ns=?,process_id=?,exit_code=?,error=?,resources_json=?,receipt_path=? WHERE id=?",
                (
                    row["status"],
                    row["finished_ns"],
                    row.get("pid"),
                    row.get("exit_code"),
                    row.get("error"),
                    json.dumps(row.get("resources", {})),
                    row.get("receipt_path"),
                    number,
                ),
            )
            for stream in ("stdout", "stderr", "receipt", "compiler"):
                path: str | None = row.get(stream + "_path")
                if path is not None:
                    self.output(number, stream, Path(path))

    def output(self, number: int, name: str, path: Path) -> None:
        with path.open("rb") as stream:
            offset: int = 0
            while chunk := stream.read(65536):
                self.db.execute(
                    "INSERT INTO output_chunks VALUES(?,?,?,?)",
                    (number, name, offset, chunk),
                )
                offset += len(chunk)

    def close(self) -> None:
        self.db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        self.db.close()
