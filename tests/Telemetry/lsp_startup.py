#!/usr/bin/env python3
"""Exercise the dedicated LSP reader on one CPU and report queue ownership."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys
import time
from pathlib import Path
from typing import Any
from uuid import uuid4

ROOT: Path = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/Msync.EndToEnd"))
from lsp_latency import Client


def journey(binary: Path, directory: Path) -> dict[str, Any]:
    directory.mkdir()
    database: Path = directory / "lsp.sqlite"
    environment: dict[str, str] = dict(os.environ)
    environment.pop("DOTNET_PROCESSOR_COUNT", None)
    started: int = time.perf_counter_ns()
    client: Client = Client(
        binary,
        cwd=directory,
        environment=environment,
        server_arguments=(
            "--no-config",
            "--telemetry-db",
            str(database),
            "--telemetry-level",
            "phases",
        ),
    )
    try:
        result: Any
        initialized_ms: float
        result, initialized_ms = client.request("initialize", {"capabilities": {}})
        assert result["capabilities"]["hoverProvider"], result
        startup_ms: float = (time.perf_counter_ns() - started) / 1e6
        client.send("initialized", {})
        uri: str = (directory / "startup.msync").as_uri()
        client.send(
            "textDocument/didOpen",
            {
                "textDocument": {
                    "uri": uri,
                    "languageId": "msync",
                    "version": 1,
                    "text": "let answer = 42;\nanswer",
                }
            },
        )
        result, _ = client.request(
            "textDocument/hover",
            {"textDocument": {"uri": uri}, "position": {"line": 1, "character": 2}},
        )
        assert result and result["contents"], result
    finally:
        client.close()
    with sqlite3.connect(database) as db:
        assert db.execute("SELECT status,exporter_error FROM sessions").fetchall() == [
            ("complete", None)
        ]
        reader: tuple[str] | None = db.execute(
            "SELECT a.value FROM spans s JOIN attributes a ON a.record_id=s.id WHERE s.parent_span_id IS NULL AND a.key='lsp.input.reader'"
        ).fetchone()
        assert reader == ("dedicated-thread",), reader
        metrics: dict[str, int] = dict(
            db.execute(
                "SELECT m.key,cast(m.value AS integer) FROM spans s JOIN attributes a ON a.record_id=s.id AND a.key='lsp.method' AND a.value='initialize' JOIN metrics m ON m.record_id=s.id WHERE s.name='lsp.request'"
            )
        )
    return {
        "spawn_to_initialize_ms": startup_ms,
        "initialize_roundtrip_ms": initialized_ms,
        "initialize_queue_ms": metrics["lsp.queue_ns"] / 1e6,
        "initialize_dispatch_ms": metrics["lsp.dispatch_to_reply_ns"] / 1e6,
        "reader": reader[0],
        "database": str(database),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--cpu", type=int)
    args: argparse.Namespace = parser.parse_args()
    if hasattr(os, "sched_setaffinity"):
        cpu: int = args.cpu if args.cpu is not None else min(os.sched_getaffinity(0))
        os.sched_setaffinity(0, {cpu})
    directory: Path = ROOT / ".temp" / ("lsp-dedicated-reader-" + uuid4().hex)
    directory.mkdir()
    rows: list[dict[str, Any]] = [
        journey(args.binary.resolve(), directory / str(index)) for index in range(3)
    ]
    report: dict[str, Any] = {
        "affinity": sorted(os.sched_getaffinity(0))
        if hasattr(os, "sched_getaffinity")
        else None,
        "method": "Three fresh framed sessions; initialize, document open, semantic hover and orderly shutdown. No elapsed-time quality threshold.",
        "observations": rows,
    }
    (directory / "result.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
