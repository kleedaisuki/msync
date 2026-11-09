#!/usr/bin/env python3
"""Keep a real LSP process alive through malformed recursive editor inference."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client


def run(binary: Path) -> dict[str, Any]:
    client: Client = Client(binary)
    uri: str = "file:///msync-editor-probe-recovery.msync"
    malformed: str = (
        "let ready = scope { useful: true }; "
        "fn inspect(scope) { (/tmp/ @ scope::endpoint) }; "
        "inspect(scope { endpoint: ssh://one }); "
        "inspect(scope { endpoint: true });"
    )
    valid: str = "let recovered = true; recovered"
    try:
        client.request("initialize", {"capabilities": {}})
        notify(client, uri, 1, malformed, opening=True)
        report: dict[str, Any] = diagnostics(client, uri, 1)
        assert report["diagnostics"], report
        hovered, _ = client.request(
            "textDocument/hover",
            {"textDocument": {"uri": uri}, "position": {"line": 0, "character": 5}},
        )
        assert hovered is not None and "ready: Scope" in hovered["contents"]["value"], (
            hovered
        )
        notify(client, uri, 2, valid)
        assert diagnostics(client, uri, 2)["diagnostics"] == []
        recovered, _ = client.request(
            "textDocument/hover",
            {
                "textDocument": {"uri": uri},
                "position": {"line": 0, "character": valid.rindex("recovered") + 1},
            },
        )
        assert (
            recovered is not None
            and "recovered: Bool" in recovered["contents"]["value"]
        ), recovered
        assert client.process.poll() is None
        return {
            "malformed_diagnostics": len(report["diagnostics"]),
            "unrelated_scope_hover_retained": True,
            "valid_change_recovers_same_process": True,
        }
    finally:
        client.close()


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    print(json.dumps(run(arguments.binary.resolve()), indent=2))


if __name__ == "__main__":
    main()
