#!/usr/bin/env python3
"""Exercise actual stdio LSP standard-source navigation and alias completion."""

from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path
from typing import IO, Any, cast


class Client:
    def __init__(self, binary: Path) -> None:
        self.process = subprocess.Popen(
            [str(binary), "--language-server"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        assert self.process.stdin is not None and self.process.stdout is not None
        self.input: IO[bytes] = self.process.stdin
        self.output: IO[bytes] = self.process.stdout
        self.next_id = 0

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
        data = json.dumps(message, ensure_ascii=False).encode()
        self.input.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
        self.input.flush()

    def read(self) -> dict[str, Any]:
        length = 0
        while (line := self.output.readline()) not in (b"\r\n", b"\n"):
            assert line, "Language server exited without an LSP response"
            name, value = line.decode().split(":", 1)
            if name.lower() == "content-length":
                length = int(value.strip())
        return cast(dict[str, Any], json.loads(self.output.read(length)))

    def request(self, method: str, parameters: dict[str, Any]) -> Any:
        self.next_id += 1
        self.send(method, parameters, self.next_id)
        while True:
            result = self.read()
            if result.get("id") == self.next_id:
                assert "error" not in result, result
                return result.get("result")

    def close(self) -> None:
        self.request("shutdown", {})
        self.send("exit", {})
        self.process.wait(timeout=10)
        assert self.process.returncode == 0


def run(binary: Path) -> None:
    client = Client(binary)
    try:
        client.request("initialize", {"capabilities": {}})
        uri = "file:///msync-lsp-stdlib-journey.msync"
        source = "let fs = $std::fs;\nlet p = $std::fs::exists(input);\nlet q = fs::exists(input);\nfs::\n"
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
        for line, column in ((1, 19), (2, 14)):
            result = client.request(
                "textDocument/definition",
                {
                    "textDocument": {"uri": uri},
                    "position": {"line": line, "character": column},
                },
            )
            assert result["uri"] == "msync-stdlib:///0.8.0/fs.msync", result
            standard = client.request("msync/stdlibSource", {"uri": result["uri"]})
            position = result["range"]["start"]
            assert standard.splitlines()[position["line"]][
                position["character"] :
            ].startswith("exists("), result
        items = client.request(
            "textDocument/completion",
            {"textDocument": {"uri": uri}, "position": {"line": 3, "character": 4}},
        )
        assert {"exists", "is_directory", "copy"}.issubset(
            {item["label"] for item in items}
        ), items
        print(
            "PASS: actual LSP direct/aliased standard definitions, embedded source bytes and member completion"
        )
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    args = parser.parse_args()
    run(args.binary.resolve())


if __name__ == "__main__":
    main()
