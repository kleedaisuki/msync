"""Measure a real edit and hover while another document is analyzed."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import statistics
import time
from contextlib import closing
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

from lsp_latency import Client, document


@dataclass(frozen=True)
class Observation:
    focused_hover_ms: float
    both_documents_ms: float


@dataclass(frozen=True)
class Analysis:
    uri: str
    version: int
    started_ns: int
    elapsed_ns: int
    queue_ns: int
    status: str
    retry: str | None


def analysis(database: Path) -> list[Analysis]:
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as db:
        return [
            Analysis(*row)
            for row in db.execute(
                """
                SELECT uri.value, CAST(version.value AS INTEGER), r.start_unix_ns,
                       r.duration_ns, queue.value_int, r.status, retry.value
                FROM spans r
                JOIN attributes uri ON uri.record_id = r.id AND uri.key = 'document.uri'
                JOIN attributes version ON version.record_id = r.id AND version.key = 'document.version'
                JOIN metrics queue ON queue.record_id = r.id AND queue.key = 'lsp.queue_ns'
                LEFT JOIN attributes retry ON retry.record_id = r.id AND retry.key = 'analysis.retry'
                WHERE r.name = 'lsp.analysis'
                ORDER BY r.start_unix_ns
                """
            )
        ]


def take_reply(client: Client, identifier: int) -> dict[str, Any]:
    for index, stored in enumerate(client.notifications):
        if stored.get("id") == identifier:
            return client.notifications.pop(index)
    while True:
        response = client.responses.get(timeout=30)
        if isinstance(response, Exception):
            raise response
        if response.get("id") == identifier:
            return response
        client.notifications.append(response)


def run(
    binary: Path, work: Path, rows: int, edits: int, traced: bool = False
) -> tuple[list[Observation], list[Analysis]]:
    work.mkdir(parents=True)
    focused = (work / "focused.msync").as_uri()
    background_source = document(rows)
    database = work / "trace.sqlite"
    arguments = (
        ["--telemetry-db", str(database), "--telemetry-level", "phases"]
        if traced
        else []
    )
    client = Client(binary, cwd=work, server_arguments=arguments)

    def hover(uri: str, line: int, expected: str) -> float:
        result, elapsed = client.request(
            "textDocument/hover",
            {"textDocument": {"uri": uri}, "position": {"line": line, "character": 5}},
        )
        assert expected in result["contents"]["value"], result
        return elapsed

    def opened(uri: str, source: str) -> None:
        client.send(
            "textDocument/didOpen",
            {
                "textDocument": {
                    "uri": uri,
                    "version": 1,
                    "languageId": "msync",
                    "text": source,
                }
            },
        )

    observations: list[Observation] = []
    try:
        client.request("initialize", {"capabilities": {}})
        opened(focused, "let focused = 7;\nfocused\n")
        hover(focused, 1, "focused: Number")
        previous = "7"
        for index in range(edits):
            background = (work / f"background-{index}.msync").as_uri()
            target = {"textDocument": {"uri": background}}
            started = time.perf_counter_ns()
            opened(background, background_source)
            # A highlighter request is real work, but must not monopolize the
            # compiler worker while an interactive request is waiting elsewhere.
            token_request = -1 - index
            client.send("textDocument/semanticTokens/full", target, token_request)
            replacement = "true" if index % 2 == 0 else "7"
            client.send(
                "textDocument/didChange",
                {
                    "textDocument": {"uri": focused, "version": index + 2},
                    "contentChanges": [
                        {
                            "range": {
                                "start": {"line": 0, "character": 14},
                                "end": {"line": 0, "character": 14 + len(previous)},
                            },
                            "text": replacement,
                        }
                    ],
                },
            )
            current = "focused: Bool" if replacement == "true" else "focused: Number"
            latency = hover(focused, 1, current)
            result = take_reply(client, token_request)
            assert "error" not in result and len(result["result"]["data"]) == 20 * rows
            hover(background, 0, "item_0: Number")
            observations.append(
                Observation(latency, (time.perf_counter_ns() - started) / 1_000_000)
            )
            client.send("textDocument/didClose", target)
            previous = replacement
    finally:
        client.close()
    return observations, analysis(database) if traced else []


def verify(attempts: list[Analysis], rows: int, edits: int) -> None:
    successes = {(a.uri, a.version) for a in attempts if a.status == "ok"}
    assert len(successes) == 2 * edits + 1, attempts
    for attempt in attempts:
        if attempt.retry == "preempted":
            assert attempt.status == "ok", attempt
            assert any(
                other.uri == attempt.uri
                and other.version == attempt.version
                and other.status == "cancelled"
                and other.started_ns <= attempt.started_ns
                for other in attempts
            ), attempt
    if rows >= 4000:
        focused = {a.version: a for a in attempts if a.uri.endswith("/focused.msync")}
        completed = {
            a.uri.rsplit("/", 1)[-1]: a
            for a in attempts
            if a.uri.rsplit("/", 1)[-1].startswith("background-") and a.status == "ok"
        }
        # At least one actual contention must exercise the queue. Do not turn
        # host scheduling for every iteration into a correctness threshold.
        assert any(
            focused[index + 2].started_ns
            < completed[f"background-{index}.msync"].started_ns
            for index in range(edits)
        ), attempts


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--rows", type=int, action="append")
    parser.add_argument("--pairs", type=int, default=3)
    parser.add_argument("--edits", type=int, default=5)
    parser.add_argument("--cpu", type=int)
    args = parser.parse_args()
    candidate: Path = args.candidate.resolve(strict=True)
    baseline: Path | None = (
        args.baseline.resolve(strict=True) if args.baseline else None
    )
    work: Path = args.work.resolve()
    work.mkdir(parents=True)
    if args.cpu is not None:
        os.sched_setaffinity(0, {args.cpu})
    report: dict[int, object] = {}
    for rows in args.rows or [16, 4000]:
        samples: dict[str, list[Observation]] = {"baseline": [], "candidate": []}
        if baseline is not None:
            for pair in range(args.pairs):
                arms = [("baseline", baseline), ("candidate", candidate)]
                if pair % 2:
                    arms.reverse()
                for name, binary in arms:
                    observed, _ = run(
                        binary, work / f"{rows}-{pair}-{name}", rows, args.edits
                    )
                    samples[name].extend(observed)
        _, attempts = run(
            candidate, work / f"{rows}-trace", rows, args.edits, traced=True
        )
        verify(attempts, rows, args.edits)
        medians: dict[str, object] = {
            name: {
                "focused_hover_ms": statistics.median(a.focused_hover_ms for a in seen),
                "both_documents_ms": statistics.median(
                    a.both_documents_ms for a in seen
                ),
            }
            for name, seen in samples.items()
            if seen
        }
        report[rows] = {
            "samples": {
                name: [asdict(a) for a in seen] for name, seen in samples.items()
            },
            "medians": medians,
            "candidate_trace": [asdict(a) for a in attempts],
        }
        print(rows, json.dumps(medians), flush=True)
    (work / "result.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
