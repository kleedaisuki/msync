#!/usr/bin/env python3
"""Check real asynchronous LSP document lifetimes, versions and UTF-16 ranges."""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path
from typing import Any, cast

from lsp_latency import Client, document


def notify(
    client: Client, uri: str, version: int, text: str, *, opening: bool = False
) -> None:
    descriptor = {"uri": uri, "version": version}
    parameters: dict[str, Any]
    if opening:
        parameters = {
            "textDocument": {**descriptor, "languageId": "msync", "text": text}
        }
    else:
        parameters = {"textDocument": descriptor, "contentChanges": [{"text": text}]}
    client.send(
        "textDocument/didOpen" if opening else "textDocument/didChange", parameters
    )


def diagnostics(client: Client, uri: str, version: int | None) -> dict[str, Any]:
    while True:
        for index, message in enumerate(client.notifications):
            parameters = message.get("params", {})
            if (
                message.get("method") == "textDocument/publishDiagnostics"
                and parameters.get("uri") == uri
                and parameters.get("version") == version
            ):
                client.notifications.pop(index)
                return cast(dict[str, Any], parameters)
        received = client.responses.get(timeout=10)
        if isinstance(received, Exception):
            raise received
        client.notifications.append(received)


def run(binary: Path) -> dict[str, float]:
    client = Client(binary)
    uri = "file:///msync-analysis-lifetime.msync"
    second = "file:///msync-analysis-second.msync"
    try:
        client.request("initialize", {"capabilities": {}})
        notify(client, uri, 1, document(4000), opening=True)
        notify(client, uri, 2, "let wrong = null;\n")
        notify(client, uri, 3, "let latest = true;\nlatest\n")
        notify(client, uri, 2, "let stale = false;\nstale\n")
        hover, latency = client.request(
            "textDocument/hover",
            {"textDocument": {"uri": uri}, "position": {"line": 1, "character": 2}},
        )
        assert "`latest`" in hover["contents"]["value"], hover
        assert diagnostics(client, uri, 3)["diagnostics"] == []
        client.send("textDocument/didClose", {"textDocument": {"uri": uri}})
        assert diagnostics(client, uri, None)["diagnostics"] == []
        notify(client, uri, 1, 'let emoji = "😀";\rlet bad = null;\r\n', opening=True)
        error = diagnostics(client, uri, 1)["diagnostics"][0]
        assert error["range"] == {
            "start": {"line": 1, "character": 10},
            "end": {"line": 1, "character": 14},
        }, error
        notify(client, uri, 2, 'let emoji = "😀";\rlet active = true;\r\nactive\r')
        client.send(
            "textDocument/didChange",
            {
                "textDocument": {"uri": uri, "version": 3},
                "contentChanges": [
                    {
                        "range": {
                            "start": {"line": 1, "character": 13},
                            "end": {"line": 1, "character": 17},
                        },
                        "text": "false",
                    }
                ],
            },
        )
        hover, _ = client.request(
            "textDocument/hover",
            {"textDocument": {"uri": uri}, "position": {"line": 2, "character": 3}},
        )
        assert "`active`" in hover["contents"]["value"], hover
        tokens, _ = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        line, column = 0, 0
        observed: list[tuple[int, int, int]] = []
        for index in range(0, len(tokens["data"]), 5):
            row, character, length, _, _ = tokens["data"][index : index + 5]
            column = column + character if row == 0 else character
            line += row
            observed.append((line, column, length))
        assert (0, 12, 4) in observed, observed
        assert (2, 0, 6) in observed, observed
        assert diagnostics(client, uri, 3)["diagnostics"] == []
        notify(client, second, 1, document(2000), opening=True)
        notify(client, uri, 4, "let current = true;\n")
        notify(client, second, 2, "let second_current = true;\n")
        assert diagnostics(client, uri, 4)["diagnostics"] == []
        assert diagnostics(client, second, 2)["diagnostics"] == []
        notify(client, uri, 5, document(8000))
        _, shutdown = client.request("shutdown", {})
        client.send("exit", {})
        started = time.perf_counter_ns()
        client.process.wait(timeout=5)
        assert client.process.returncode == 0
        return {
            "hover_after_superseded_analysis_ms": latency,
            "shutdown_with_analysis_pending_ms": shutdown,
            "exit_join_ms": (time.perf_counter_ns() - started) / 1_000_000,
        }
    finally:
        if client.process.poll() is None:
            client.process.kill()
            client.process.wait()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(run(args.binary.resolve()), indent=2))


if __name__ == "__main__":
    main()
