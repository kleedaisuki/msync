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


def pending_hover_lifecycle(binary: Path) -> dict[str, float]:
    """Queue protocol lifecycle work behind one unrelated compiler snapshot."""
    client = Client(binary)
    busy = "file:///msync-hover-busy.msync"
    uri = "file:///msync-hover-pending.msync"
    try:
        client.request("initialize", {"capabilities": {}})
        messages: list[dict[str, Any]] = []

        def send(
            method: str, parameters: dict[str, Any], identifier: int | None = None
        ) -> None:
            message: dict[str, Any] = {
                "jsonrpc": "2.0",
                "method": method,
                "params": parameters,
            }
            if identifier is not None:
                message["id"] = identifier
            messages.append(message)

        def opened(target: str, version: int, text: str) -> None:
            send(
                "textDocument/didOpen",
                {
                    "textDocument": {
                        "uri": target,
                        "version": version,
                        "languageId": "msync",
                        "text": text,
                    }
                },
            )

        def changed(version: int, text: str) -> None:
            send(
                "textDocument/didChange",
                {
                    "textDocument": {"uri": uri, "version": version},
                    "contentChanges": [{"text": text}],
                },
            )

        def hovered(identifier: int) -> None:
            send(
                "textDocument/hover",
                {"textDocument": {"uri": uri}, "position": {"line": 1, "character": 2}},
                identifier,
            )

        opened(busy, 1, document(4000))
        opened(uri, 1, "let first = true;\nfirst\n")
        hovered(10101)
        send("$/cancelRequest", {"id": 10101})
        changed(2, "let second = true;\nsecond\n")
        hovered(10102)
        changed(3, "let third = true;\nthird\n")
        hovered(10103)
        send("textDocument/didClose", {"textDocument": {"uri": uri}})
        opened(uri, 1, "let reopened = false;\nreopened\n")
        hovered(10104)
        send("shutdown", {}, 10105)
        frames: list[bytes] = []
        for message in messages:
            payload = json.dumps(message, ensure_ascii=False).encode("utf-8")
            frames.append(
                f"Content-Length: {len(payload)}\r\n\r\n".encode("ascii") + payload
            )
        started = time.perf_counter_ns()
        client.input.write(b"".join(frames))
        client.input.flush()
        replies: dict[int, dict[str, Any]] = {}
        while len(replies) < 5:
            reply = client.responses.get(timeout=10)
            if isinstance(reply, Exception):
                raise reply
            if isinstance(reply.get("id"), int) and reply["id"] in range(10101, 10106):
                assert reply["id"] not in replies, reply
                replies[reply["id"]] = reply
        assert replies[10101]["error"]["code"] == -32800, replies
        for identifier in range(10102, 10106):
            assert replies[identifier].get("result", "missing") is None, replies
        shutdown_ms = (time.perf_counter_ns() - started) / 1_000_000
        client.send("exit", {})
        client.process.wait(timeout=5)
        assert client.process.returncode == 0
        return {"pending_hover_cancel_edit_close_shutdown_ms": shutdown_ms}
    finally:
        if client.process.poll() is None:
            client.process.kill()
            client.process.wait()


def semantic_refresh_lifecycle(binary: Path) -> dict[str, float]:
    """Hold a refresh response while a newer compiler snapshot completes."""
    client = Client(binary)
    uri = "file:///msync-refresh-lifecycle.msync"
    try:
        client.request(
            "initialize",
            {
                "capabilities": {
                    "workspace": {
                        "semanticTokens": {"refreshSupport": True},
                    }
                }
            },
        )

        def refresh() -> dict[str, Any]:
            while True:
                for index, message in enumerate(client.notifications):
                    if message.get("method") == "workspace/semanticTokens/refresh":
                        return client.notifications.pop(index)
                received = client.responses.get(timeout=10)
                if isinstance(received, Exception):
                    raise received
                client.notifications.append(received)

        def respond(identifier: str | int) -> None:
            payload = json.dumps(
                {"jsonrpc": "2.0", "id": identifier, "result": None}
            ).encode("utf-8")
            client.input.write(
                f"Content-Length: {len(payload)}\r\n\r\n".encode("ascii") + payload
            )
            client.input.flush()

        notify(client, uri, 1, "let named = fn() { true };\nnamed\n", opening=True)
        assert diagnostics(client, uri, 1)["diagnostics"] == []
        first = refresh()
        before, _ = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        notify(client, uri, 2, "let named = scope { x: true };\nnamed\n")
        assert diagnostics(client, uri, 2)["diagnostics"] == []
        respond(first["id"])
        second = refresh()
        assert second["id"] != first["id"], (first, second)
        after, _ = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        assert before["data"] != after["data"], (before, after)
        respond(second["id"])
        notify(client, uri, 3, "let missing = ;\n")
        assert diagnostics(client, uri, 3)["diagnostics"]
        formatted, elapsed = client.request(
            "textDocument/formatting",
            {
                "textDocument": {"uri": uri},
                "options": {"tabSize": 4, "insertSpaces": True},
            },
        )
        assert formatted == [], formatted
        return {
            "invalid_document_format_response_ms": elapsed,
            "successive_semantic_refreshes": 2.0,
        }
    finally:
        client.close()


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
        assert "latest: Bool" in hover["contents"]["value"], hover
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
        assert "active: Bool" in hover["contents"]["value"], hover
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
    binary = args.binary.resolve()
    print(
        json.dumps(
            {
                **run(binary),
                **pending_hover_lifecycle(binary),
                **semantic_refresh_lifecycle(binary),
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
