#!/usr/bin/env python3
"""Measure actual LSP edit bursts and semantic-token scaling without compiler mocks."""

from __future__ import annotations

import argparse
import hashlib
import json
import queue
import shutil
import subprocess
import tempfile
import threading
import time
from pathlib import Path
from typing import IO, Any, cast

ROOT = Path(__file__).resolve().parents[2]


class Client:
    def __init__(
        self,
        binary: Path,
        *,
        cwd: Path | None = None,
        environment: dict[str, str] | None = None,
    ) -> None:
        self.process = subprocess.Popen(
            [str(binary), "--language-server"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=cwd,
            env=environment,
        )
        assert self.process.stdin is not None and self.process.stdout is not None
        self.input: IO[bytes] = self.process.stdin
        self.output: IO[bytes] = self.process.stdout
        self.responses: queue.Queue[dict[str, Any] | Exception] = queue.Queue()
        self.notifications: list[dict[str, Any]] = []
        self.next_id = 0
        self.reader = threading.Thread(target=self._read_loop, daemon=True)
        self.reader.start()

    def _read_loop(self) -> None:
        try:
            while True:
                length = -1
                while True:
                    line = self.output.readline()
                    if not line:
                        return
                    if line in (b"\r\n", b"\n"):
                        break
                    name, value = line.decode("ascii").split(":", 1)
                    if name.lower() == "content-length":
                        length = int(value.strip())
                assert length >= 0
                body = self.output.read(length)
                assert len(body) == length
                self.responses.put(cast(dict[str, Any], json.loads(body)))
        except (OSError, ValueError, AssertionError) as error:
            self.responses.put(error)

    def send(
        self, method: str, parameters: dict[str, Any], identifier: int | None = None
    ) -> None:
        message: dict[str, Any] = {
            "jsonrpc": "2.0",
            "method": method,
            "params": parameters,
        }
        if identifier is not None:
            message["id"] = identifier
        payload = json.dumps(message, ensure_ascii=False).encode("utf-8")
        self.input.write(
            f"Content-Length: {len(payload)}\r\n\r\n".encode("ascii") + payload
        )
        self.input.flush()

    def request(self, method: str, parameters: dict[str, Any]) -> tuple[Any, float]:
        self.next_id += 1
        started = time.perf_counter_ns()
        self.send(method, parameters, self.next_id)
        while True:
            response = self.responses.get(timeout=30)
            if isinstance(response, Exception):
                raise response
            if response.get("id") == self.next_id:
                assert "error" not in response, response
                return response.get("result"), (
                    time.perf_counter_ns() - started
                ) / 1_000_000
            self.notifications.append(response)

    def close(self) -> None:
        try:
            self.request("shutdown", {})
            self.send("exit", {})
            self.process.wait(timeout=10)
            assert self.process.returncode == 0
        finally:
            if self.process.poll() is None:
                self.process.kill()
                self.process.wait()


def document(rows: int, suffix: str = "") -> str:
    return "".join(f"let item_{index} = {index};\n" for index in range(rows)) + suffix


def measure(
    binary: Path, rows: int, burst: int, *, resolve_documentation: bool = True
) -> dict[str, object]:
    client = Client(binary)
    uri = "file:///msync-lsp-latency.msync"
    try:
        capabilities = (
            {
                "textDocument": {
                    "completion": {
                        "completionItem": {
                            "resolveSupport": {"properties": ["documentation"]}
                        }
                    }
                }
            }
            if resolve_documentation
            else {}
        )
        initialized, initialization = client.request(
            "initialize", {"capabilities": capabilities}
        )
        source = document(rows)
        client.send(
            "textDocument/didOpen",
            {
                "textDocument": {
                    "uri": uri,
                    "languageId": "msync",
                    "version": 1,
                    "text": source,
                }
            },
        )
        hovered, open_hover = client.request(
            "textDocument/hover",
            {"textDocument": {"uri": uri}, "position": {"line": 0, "character": 5}},
        )
        assert "item_0: Number" in hovered["contents"]["value"], hovered
        tokens, token_time = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        assert len(tokens["data"]) // 5 == rows * 3, len(tokens["data"])
        repeated, repeat_time = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        assert repeated == tokens, "Unchanged document tokens changed between requests"
        started = time.perf_counter_ns()
        for version in range(2, burst + 2):
            client.send(
                "textDocument/didChange",
                {
                    "textDocument": {"uri": uri, "version": version},
                    "contentChanges": [
                        {"text": document(rows, f"let newest_{version} = true;\n")}
                    ],
                },
            )
        completions, completion = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": {"line": rows + 1, "character": 0},
            },
        )
        burst_time = (time.perf_counter_ns() - started) / 1_000_000
        labels = {item["label"] for item in completions}
        assert f"newest_{burst + 1}" in labels, labels
        selected = next(
            item for item in completions if item["label"] == f"newest_{burst + 1}"
        )
        resolve_time: float | None = None
        provider = initialized["capabilities"]["completionProvider"]
        if provider.get("resolveProvider"):
            detailed, resolve_time = client.request("completionItem/resolve", selected)
            assert detailed["label"] == selected["label"], detailed
            assert "Bool" in detailed["detail"] and detailed.get("documentation"), (
                detailed
            )
        return {
            "rows": rows,
            "burst": burst,
            "initialize_ms": initialization,
            "open_hover_ms": open_hover,
            "semantic_tokens_ms": token_time,
            "repeated_semantic_tokens_ms": repeat_time,
            "completion_after_burst_ms": completion,
            "whole_burst_ms": burst_time,
            "completion_payload_bytes": len(
                json.dumps(completions, ensure_ascii=False).encode("utf-8")
            ),
            "selected_completion_resolve_ms": resolve_time,
            "documentation_resolve_negotiated": resolve_documentation,
            "diagnostics_before_response": len(client.notifications),
        }
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--rows", type=int, action="append")
    parser.add_argument("--burst", type=int, default=16)
    parser.add_argument(
        "--eager-documentation",
        action="store_true",
        help="Use a legacy client without completion documentation resolution",
    )
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    root = ROOT / ".temp/product-latency"
    root.mkdir(parents=True, exist_ok=True)
    frozen = Path(tempfile.mkdtemp(prefix="lsp-", dir=root))
    binary = arguments.binary.resolve()
    for path in binary.parent.iterdir():
        if path.is_file():
            shutil.copy2(path, frozen / path.name)
    result = {
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "binary": str(frozen / binary.name),
        "measurements": [
            measure(
                frozen / binary.name,
                rows,
                arguments.burst,
                resolve_documentation=not arguments.eager_documentation,
            )
            for rows in arguments.rows or [1000, 4000]
        ],
    }
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
