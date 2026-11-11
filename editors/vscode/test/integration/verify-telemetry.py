"""Validate the local SQLite profile after the real extension host exits."""

from __future__ import annotations

import argparse
import json
import sqlite3
import time
from contextlib import closing
from pathlib import Path


def verify(database: Path) -> dict[str, object]:
    with sqlite3.connect(f"{database.as_uri()}?mode=ro", uri=True) as connection:
        sessions: list[tuple[str, str]] = connection.execute(
            "SELECT trace_id, status FROM sessions"
        ).fetchall()
        assert sessions and all(status == "complete" for _, status in sessions), (
            sessions
        )
        client: dict[str, int] = dict(
            connection.execute(
                """
                SELECT r.name, max(m.value_int)
                FROM events r JOIN metrics m ON r.id = m.record_id
                JOIN attributes a ON a.record_id = r.id
                WHERE r.name LIKE 'editor.client.%' AND m.key = 'duration_ns'
                    AND a.key = 'client.outcome' AND a.value = 'ok'
                GROUP BY r.name
                """
            )
        )
        for operation in ("startup", "stdlibSource", "artifact", "hover", "completion"):
            assert client.get(f"editor.client.{operation}", 0) > 0, client
        server: dict[str, int] = dict(
            connection.execute(
                """
                SELECT a.value, max(r.duration_ns)
                FROM spans r JOIN attributes a ON r.id = a.record_id
                WHERE r.name = 'lsp.request' AND a.key = 'lsp.method' AND r.status = 'ok'
                GROUP BY a.value
                """
            )
        )
        for method in (
            "initialize",
            "textDocument/hover",
            "textDocument/completion",
            "msync/stdlibSource",
            "msync/artifact",
            "textDocument/semanticTokens/full",
            "textDocument/semanticTokens/full/delta",
        ):
            assert server.get(method, 0) > 0, server
        wire: dict[str, int] = dict(
            connection.execute(
                """
            SELECT a.value, min(m.value_int)
            FROM spans r JOIN attributes a ON r.id = a.record_id
            JOIN metrics m ON r.id = m.record_id
            JOIN attributes d ON r.id = d.record_id AND d.key = 'document.uri'
            WHERE r.name = 'lsp.request' AND r.status = 'ok' AND a.key = 'lsp.method'
              AND a.value LIKE 'textDocument/semanticTokens/%' AND m.key = 'lsp.response_bytes'
              AND d.value LIKE '%/editor-telemetry.msync'
            GROUP BY a.value
            """
            )
        )
        full = wire["textDocument/semanticTokens/full"]
        delta = wire["textDocument/semanticTokens/full/delta"]
        assert full > 1000 and 0 < delta < full, wire
        assert connection.execute(
            """
            SELECT count(*) FROM spans r JOIN metrics m ON m.record_id = r.id
            WHERE r.name = 'lsp.request' AND m.key = 'lsp.dispatch_to_reply_ns' AND m.value_int > 0
            """
        ).fetchone()[0] >= len(server)
        assert (
            connection.execute(
                "SELECT count(*) FROM spans WHERE name = 'lsp.analysis' AND duration_ns > 0"
            ).fetchone()[0]
            > 0
        )
        stages: set[str] = {
            name
            for (name,) in connection.execute(
                """
                SELECT child.name FROM spans child JOIN spans parent
                  ON child.trace_id = parent.trace_id AND child.parent_span_id = parent.span_id
                WHERE parent.name = 'lsp.analysis'
                """
            )
        }
        assert {"editor.parse", "editor.semantic", "editor.project"} <= stages, stages
        assert connection.execute(
            "SELECT count(*) FROM spans WHERE name = 'language-server' AND parent_span_id IS NULL"
        ).fetchone()[0] == len(sessions)
        assert (
            connection.execute(
                "SELECT count(*) FROM attributes WHERE value LIKE '%unique_user_source_must_not_be_in_diagnostics%'"
            ).fetchone()[0]
            == 0
        )
    return {
        "sessions": len(sessions),
        "editor_client_ns": client,
        "language_server_ns": server,
        "semantic_tokens_response_bytes": wire,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path)
    parser.add_argument("--wait-for")
    args = parser.parse_args()
    database: Path = args.database.resolve()
    if args.wait_for is not None:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if database.exists():
                with closing(
                    sqlite3.connect(f"{database.as_uri()}?mode=ro", uri=True)
                ) as connection:
                    found = connection.execute(
                        """
                        SELECT 1 FROM spans r JOIN attributes a ON r.id = a.record_id
                        WHERE r.name = 'lsp.request' AND r.status = 'ok'
                          AND a.key = 'lsp.method' AND a.value = ? LIMIT 1
                        """,
                        (args.wait_for,),
                    ).fetchone()
                    if found:
                        return
            time.sleep(0.05)
        raise TimeoutError(
            f"Expected a successful {args.wait_for} request in {database}"
        )
    print(json.dumps(verify(database), ensure_ascii=False))


if __name__ == "__main__":
    main()
