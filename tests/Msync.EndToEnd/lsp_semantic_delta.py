"""Check semantic-token delta reconstruction and benchmark the actual LSP stream."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import statistics
from contextlib import closing
from pathlib import Path
from typing import Any

from lsp_latency import Client, document

FULL = "textDocument/semanticTokens/full"
DELTA = FULL + "/delta"


def apply(previous: list[int], result: dict[str, Any]) -> list[int]:
    if "data" in result:
        return list(result["data"])
    values = previous.copy()
    for edit in reversed(result["edits"]):
        start = int(edit["start"])
        values[start : start + edit["deleteCount"]] = edit.get("data", [])
    return values


def exercise(
    binary: Path, work: Path, rows: int, edits: int, *, delta: bool, telemetry: bool
) -> dict[str, Any]:
    work.mkdir(parents=True)
    source = document(rows)
    uri = (work / "semantic-delta.msync").as_uri()
    target = {"textDocument": {"uri": uri}}
    database = work / "telemetry.sqlite"
    arguments = (
        ["--telemetry-db", str(database), "--telemetry-level", "phases"]
        if telemetry
        else []
    )
    sizes: dict[int, int] = {}

    def observe(message: dict[str, Any], size: int) -> None:
        identifier = message.get("id")
        if isinstance(identifier, int):
            sizes[identifier] = size

    client = Client(
        binary, cwd=work, server_arguments=arguments, observe_message=observe
    )
    records: list[dict[str, object]] = []
    control: list[dict[str, object]] = []
    try:
        token_capabilities = (
            {
                "textDocument": {
                    "semanticTokens": {"requests": {"full": {"delta": True}}}
                }
            }
            if delta
            else {}
        )
        capabilities, _ = client.request(
            "initialize", {"capabilities": token_capabilities}
        )
        full = capabilities["capabilities"]["semanticTokensProvider"]["full"]
        if delta:
            assert full == {"delta": True}, full
        else:
            assert full is True, full
        client.send(
            "textDocument/didOpen",
            {"textDocument": {"uri": uri, "version": 1, "text": source}},
        )
        previous, _ = client.request(FULL, target)
        assert len(previous["data"]) == 20 * rows, previous
        values = list(previous["data"])
        expected = values.copy()
        row = rows // 2
        column = len(f"let item_{row} = ")
        old_length = len(str(row))

        def update(version: int, replacement: str) -> None:
            nonlocal old_length
            client.send(
                "textDocument/didChange",
                {
                    "textDocument": {"uri": uri, "version": version},
                    "contentChanges": [
                        {
                            "range": {
                                "start": {"line": row, "character": column},
                                "end": {"line": row, "character": column + old_length},
                            },
                            "text": replacement,
                        }
                    ],
                },
            )
            old_length = len(replacement)
            expected[20 * row + 17] = old_length

        for version in range(2, edits + 2):
            update(version, "99999" if version % 2 == 0 else str(row))
            parameters = (
                {**target, "previousResultId": previous["resultId"]}
                if delta
                else target
            )
            result, elapsed = client.request(DELTA if delta else FULL, parameters)
            values = apply(values, result)
            assert values == expected, (row, version, result)
            if delta:
                assert len(result["edits"]) == 1, result
            records.append({"ms": elapsed, "bytes": sizes.pop(client.next_id)})
            previous = result

            parameters = (
                {**target, "previousResultId": previous["resultId"]}
                if delta
                else target
            )
            unchanged, elapsed = client.request(DELTA if delta else FULL, parameters)
            if delta:
                assert (
                    unchanged["edits"] == []
                    and unchanged["resultId"] == previous["resultId"]
                )
            else:
                assert unchanged["data"] == expected
            control.append({"ms": elapsed, "bytes": sizes.pop(client.next_id)})

        if delta:
            update(edits + 2, "888888")
            update(edits + 3, "7777777")
            skipped, _ = client.request(
                DELTA, {**target, "previousResultId": previous["resultId"]}
            )
            assert len(skipped["edits"]) == 1 and apply(values, skipped) == expected
            stale, _ = client.request(DELTA, {**target, "previousResultId": "unknown"})
            assert stale["data"] == expected, stale
            client.send("textDocument/didClose", target)
            client.send(
                "textDocument/didOpen",
                {"textDocument": {"uri": uri, "version": 1, "text": source}},
            )
            reopened, _ = client.request(
                DELTA, {**target, "previousResultId": stale["resultId"]}
            )
            assert reopened["resultId"] != stale["resultId"] and reopened["data"][
                20 * row + 17
            ] == len(str(row))
    finally:
        client.close()
    report: dict[str, Any] = {"changed": records, "unchanged": control}
    if telemetry:
        with closing(sqlite3.connect(database)) as connection:
            report["telemetry"] = [
                {"method": name, "metric": key, "value": value}
                for name, key, value in connection.execute(
                    """
                    SELECT a.value, m.key, m.value_int FROM records r
                    JOIN attributes a ON a.record_id = r.id AND a.key = 'lsp.method'
                    JOIN metrics m ON m.record_id = r.id
                    WHERE r.name = 'lsp.request' AND a.value LIKE 'textDocument/semanticTokens/%'
                    AND m.key IN ('lsp.response_bytes', 'lsp.queue_ns', 'lsp.dispatch_to_reply_ns')
                    ORDER BY r.id
                    """
                )
            ]
        observed = {
            (entry["method"], entry["value"])
            for entry in report["telemetry"]
            if entry["metric"] == "lsp.response_bytes"
        }
        method = DELTA if delta else FULL
        assert all(
            (method, sample["bytes"]) in observed for sample in records + control
        )
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--rows", type=int, action="append")
    parser.add_argument("--edits", type=int, default=6)
    parser.add_argument("--pairs", type=int, default=4)
    parser.add_argument("--cpu", type=int)
    args = parser.parse_args()
    baseline, candidate = (
        args.baseline.resolve(strict=True),
        args.candidate.resolve(strict=True),
    )
    work = args.work.resolve()
    work.mkdir(parents=True)
    if args.cpu is not None:
        os.sched_setaffinity(0, {args.cpu})
    report: dict[str, Any] = {}
    for rows in args.rows or [16, 4000]:
        cases: dict[str, Any] = {"pairs": []}
        for pair in range(args.pairs):
            paired = {}
            variants = [("full", baseline, False), ("delta", candidate, True)]
            if pair % 2:
                variants.reverse()
            for name, binary, delta in variants:
                paired[name] = exercise(
                    binary,
                    work / f"{rows}-{pair}-{name}",
                    rows,
                    args.edits,
                    delta=delta,
                    telemetry=False,
                )
            cases["pairs"].append(paired)
        cases["telemetry"] = exercise(
            candidate, work / f"{rows}-telemetry", rows, 2, delta=True, telemetry=True
        )
        cases["medians"] = {
            kind: {
                name: {
                    metric: statistics.median(
                        sample[metric]
                        for pair in cases["pairs"]
                        for sample in pair[name][kind]
                    )
                    for metric in ("ms", "bytes")
                }
                for name in ("full", "delta")
            }
            for kind in ("changed", "unchanged")
        }
        report[str(rows)] = cases
        print(rows, json.dumps(cases["medians"], sort_keys=True), flush=True)
    (work / "result.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
