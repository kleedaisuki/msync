#!/usr/bin/env python3
"""Measure consecutive UTF-16 body edits with checked LSP hover results."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import statistics
import sys
import time
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tests/Msync.EndToEnd"))

from lsp_latency import Client


def document(rows: int) -> list[str]:
    lines: list[str] = []
    for index in range(rows):
        lines.extend(
            (
                f"fn wrap_{index}(value) {{ scope {{ item: value }} }};",
                f"let row_{index} = wrap_{index}({index});",
            )
        )
    lines.append(f"let selected = row_{rows // 2}::item;")
    return lines


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def probe(binary: Path, rows: int, edits: int) -> dict[str, Any]:
    client = Client(binary)
    lines = document(rows)
    uri = "file:///msync-generic-edit.msync"
    descriptor = {"uri": uri}
    source = "\n".join(lines) + "\n"
    middle = rows // 2

    def hover(expected: str) -> float:
        result, elapsed = client.request(
            "textDocument/hover",
            {
                "textDocument": descriptor,
                "position": {"line": len(lines) - 1, "character": 6},
            },
        )
        if not result or f"selected: {expected}" not in result["contents"]["value"]:
            raise AssertionError((expected, result))
        return elapsed

    def change(version: int, line: int, text: str, expected: str) -> float:
        begin = time.perf_counter_ns()
        client.send(
            "textDocument/didChange",
            {
                "textDocument": {**descriptor, "version": version},
                "contentChanges": [
                    {
                        "range": {
                            "start": {"line": line, "character": 0},
                            "end": {"line": line, "character": len(lines[line])},
                        },
                        "text": text,
                    }
                ],
            },
        )
        lines[line] = text
        hover(expected)
        return (time.perf_counter_ns() - begin) / 1_000_000

    try:
        client.request("initialize", {"capabilities": {}})
        client.send(
            "textDocument/didOpen",
            {
                "textDocument": {
                    **descriptor,
                    "languageId": "msync",
                    "version": 1,
                    "text": source,
                }
            },
        )
        open_hover = hover("Number")
        trivia = change(2, 0, lines[0] + " ", "Number")
        raw: list[float] = []
        for index in range(edits):
            type_name = "String" if index % 2 == 0 else "Number"
            value = "value as String" if type_name == "String" else "value"
            raw.append(
                change(
                    index + 3,
                    middle * 2,
                    f"fn wrap_{middle}(value) {{ scope {{ item: {value} }} }};",
                    type_name,
                )
            )
        return {
            "binary": str(binary),
            "binary_sha256": sha(binary),
            "assemblies_sha256": {
                path.name: sha(path)
                for path in sorted(binary.parent.glob("Msync*.dll"))
            },
            "source_sha256": hashlib.sha256(source.encode()).hexdigest(),
            "open_hover_ms": open_hover,
            "cosmetic_edit_ms": trivia,
            "first_body_edit_ms": raw[0],
            "consecutive_body_edit_median_ms": statistics.median(raw[1:]),
            "body_edits_ms": raw,
        }
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--rows", type=int, default=400)
    parser.add_argument("--edits", type=int, default=9)
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.rows < 2 or args.edits < 2 or args.rounds < 1:
        parser.error("Use at least two rows and edits, and one paired round.")
    images: dict[str, Path] = {
        "baseline": args.baseline.resolve(),
        "candidate": args.candidate.resolve(),
    }
    rounds: list[dict[str, Any]] = []
    for index in range(args.rounds):
        order = list(images) if index % 2 == 0 else list(reversed(images))
        rounds.append(
            {
                "order": order,
                "samples": {
                    name: probe(images[name], args.rows, args.edits) for name in order
                },
            }
        )
    report = {
        "rows": args.rows,
        "edits_per_session": args.edits,
        "clock": "range didChange send through matching hover reply",
        "tiered_compilation": os.environ.get("DOTNET_TieredCompilation"),
        "rounds": rounds,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
