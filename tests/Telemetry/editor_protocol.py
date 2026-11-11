"""Exercise opt-in editor recording over native framed stdin/stdout."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import subprocess
import tempfile
from contextlib import closing
from pathlib import Path
from typing import Any, cast

ROOT: Path = Path(__file__).resolve().parents[2]


def encode(message: dict[str, object]) -> bytes:
    body: bytes = json.dumps({"jsonrpc": "2.0", **message}).encode()
    return f"Content-Length: {len(body)}\r\n\r\n".encode() + body


def decode(output: bytes) -> list[dict[str, Any]]:
    frames: list[dict[str, Any]] = []
    while output:
        header, separator, payload = output.partition(b"\r\n\r\n")
        assert separator and header.startswith(b"Content-Length: "), output
        length: int = int(header.removeprefix(b"Content-Length: "))
        assert length > 0 and len(payload) >= length, output
        frames.append(cast(dict[str, Any], json.loads(payload[:length])))
        output = payload[length:]
    return frames


def exchange(binary: Path, cwd: Path, options: list[str], env: dict[str, str]) -> None:
    messages: list[dict[str, object]] = [
        {"id": 1, "method": "initialize", "params": {"capabilities": {}}},
        {"method": "initialized", "params": {}},
        {"id": 2, "method": "textDocument/hover", "params": {}},
        {
            "id": 3,
            "method": "msync/stdlibSource",
            "params": {"uri": "msync-stdlib:///0.8.0/fs.msync"},
        },
        {
            "method": "msync/observeClient",
            "params": {
                "operation": "hover",
                "outcome": "cancelled",
                "durationNanoseconds": 987654321,
                "document": "user payload not admitted by the protocol",
            },
        },
        {
            "method": "msync/observeClient",
            "params": {
                "operation": "unknown",
                "outcome": "ok",
                "durationNanoseconds": 10,
            },
        },
        {
            "method": "msync/observeClient",
            "params": {
                "operation": "startup",
                "outcome": "ok",
                "durationNanoseconds": -1,
            },
        },
        {"id": 4, "method": "shutdown", "params": {}},
        {"method": "exit"},
    ]
    process: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), "--language-server", *options],
        cwd=cwd,
        env=env,
        input=b"".join(encode(message) for message in messages),
        capture_output=True,
        check=False,
        timeout=30,
    )
    assert process.returncode == 0 and not process.stderr, process
    initialized, invalid, source, shutdown = decode(process.stdout)
    assert initialized["id"] == 1 and initialized["result"]["capabilities"]
    assert invalid["id"] == 2 and invalid["error"]["code"] == -32602
    assert source["id"] == 3 and "copy_file" in source["result"]
    assert shutdown["id"] == 4 and shutdown["result"] is None


def inspect(database: Path) -> None:
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as db:
        assert db.execute("SELECT status, exporter_error FROM sessions").fetchall() == [
            ("complete", None)
        ]
        assert db.execute(
            "SELECT status FROM spans WHERE name = 'language-server'"
        ).fetchall() == [("ok",)]
        spans: list[tuple[str, str, int, int, int, int]] = db.execute(
            """
            SELECT a.value, s.status, s.duration_ns, queue.value_int, dispatch.value_int, total.value_int
            FROM spans s
            JOIN attributes a ON a.record_id = s.id AND a.key = 'lsp.method'
            JOIN metrics queue ON queue.record_id = s.id AND queue.key = 'lsp.queue_ns'
            JOIN metrics dispatch ON dispatch.record_id = s.id AND dispatch.key = 'lsp.dispatch_to_reply_ns'
            JOIN metrics total ON total.record_id = s.id AND total.key = 'lsp.receive_to_reply_ns'
            WHERE s.name = 'lsp.request'
            """
        ).fetchall()
        assert {method: status for method, status, *_ in spans} == {
            "initialize": "ok",
            "textDocument/hover": "error",
            "msync/stdlibSource": "ok",
            "shutdown": "ok",
        }
        assert all(
            elapsed > 0
            and queue >= 0
            and dispatch > 0
            and abs(total - queue - dispatch) <= 100
            for _, _, elapsed, queue, dispatch, total in spans
        )
        client = db.execute(
            """
            SELECT e.name, e.status, m.value_int
            FROM events e JOIN metrics m ON m.record_id = e.id AND m.key = 'duration_ns'
            WHERE e.name LIKE 'editor.client.%'
            """
        ).fetchall()
        assert client == [("editor.client.hover", "cancelled", 987654321)], client
        assert db.execute(
            "SELECT count(*) FROM attributes WHERE value LIKE 'user payload %'"
        ).fetchone() == (0,)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    args = parser.parse_args()
    binary: Path = args.binary.resolve()
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="editor-lsp-observation-", dir=scratch
    ) as temporary:
        cwd: Path = Path(temporary)
        config: Path = cwd / "msync"
        config.mkdir()
        inherited: Path = cwd / "inherited.sqlite"
        (config / "config.toml").write_text(
            "[telemetry]\nenabled = true\nbackend = 'sqlite'\n"
            f"path = {json.dumps(str(inherited))}\n"
        )
        environment: dict[str, str] = dict(
            os.environ, XDG_CONFIG_HOME=str(cwd), APPDATA=str(cwd)
        )
        exchange(binary, cwd, [], environment)
        assert not inherited.exists(), inherited
        database: Path = cwd / "editor timing.sqlite"
        exchange(
            binary,
            cwd,
            ["--telemetry-db", str(database), "--telemetry-level", "phases"],
            environment,
        )
        assert not inherited.exists(), inherited
        inspect(database)
    print(
        "PASS editor framed LSP, explicit SQLite opt-in, error timing and client payload admission"
    )


if __name__ == "__main__":
    main()
