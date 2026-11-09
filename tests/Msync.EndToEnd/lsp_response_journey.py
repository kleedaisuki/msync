#!/usr/bin/env python3
"""Exercise typed response reuse and negotiated completion details over real LSP."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import tempfile
from pathlib import Path
from typing import Any, cast

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]
RESOLVE_CAPABILITIES: dict[str, Any] = {
    "textDocument": {
        "completion": {
            "completionItem": {"resolveSupport": {"properties": ["documentation"]}}
        }
    }
}
PROJECT = """# An astral character remains ordinary source text: 😀.
scope Project {
    root: ./source,
    remote: ssh://not-selected.invalid,
    new: fn() { scope { root: ./source } },
    copy: fn(file: Path, destination: Path) { file -> destination },
};
let make = Project::new;
let instance = make();
let choose = Project::copy;
let selected = $std::bool::both(true, false);
choose(instance::root, ./must-not-exist);
"""


def position(source: str, needle: str, *, last: bool = False) -> dict[str, int]:
    offset = source.rindex(needle) if last else source.index(needle)
    prefix = source[:offset]
    return {
        "line": prefix.count("\n"),
        "character": len(prefix.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2,
    }


def eof(source: str) -> dict[str, int]:
    return {
        "line": source.count("\n"),
        "character": len(source.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2,
    }


def documentation(item: dict[str, Any]) -> str:
    value = item.get("documentation", "")
    return cast(str, value.get("value", "")) if isinstance(value, dict) else str(value)


def items(result: Any) -> list[dict[str, Any]]:
    return (
        cast(list[dict[str, Any]], result.get("items", []))
        if isinstance(result, dict)
        else cast(list[dict[str, Any]], result)
    )


def complete(
    client: Client, uri: str, source: str
) -> tuple[list[dict[str, Any]], float]:
    result, elapsed = client.request(
        "textDocument/completion",
        {"textDocument": {"uri": uri}, "position": eof(source)},
    )
    return items(result), elapsed


def selected(values: list[dict[str, Any]], name: str) -> dict[str, Any]:
    matching = [item for item in values if item["label"] == name]
    assert len(matching) == 1, (name, matching)
    return matching[0]


def resolve(
    client: Client, item: dict[str, Any], supported: bool
) -> tuple[dict[str, Any], float]:
    if not supported:
        return item, 0.0
    result, elapsed = client.request("completionItem/resolve", item)
    assert isinstance(result, dict), result
    for key, value in item.items():
        assert result.get(key) == value, (key, item, result)
    return cast(dict[str, Any], result), elapsed


def run(binary: Path, workspace: Path, *, negotiate: bool) -> dict[str, Any]:
    client = Client(binary, cwd=workspace)
    uri = (workspace / "project.msync").as_uri()
    second_uri = (workspace / "second.msync").as_uri()
    observations: dict[str, Any] = {"documentation_negotiated": negotiate}
    try:
        initialized, _ = client.request(
            "initialize", {"capabilities": RESOLVE_CAPABILITIES if negotiate else {}}
        )
        supported = bool(
            initialized["capabilities"]["completionProvider"].get("resolveProvider")
        )
        observations["resolve_supported"] = supported

        # The first-ever semantic request includes canonical embedded standard sources.
        standard = "let ready = $std::bool::both(true, false);\n$std::bool::"
        notify(client, uri, 1, standard, opening=True)
        cold, elapsed = complete(client, uri, standard)
        both = selected(cold, "both")
        assert both["kind"] == 3 and "Bool" in both["detail"], both
        detailed, resolved_ms = resolve(client, both, supported)
        assert documentation(detailed) and "fn both" in detailed["detail"], detailed
        if negotiate and supported:
            assert "documentation" not in both and "data" in both, both
        else:
            assert documentation(both), both
        observations.update(
            cold_standard_completion_ms=elapsed, first_resolve_ms=resolved_ms
        )

        notify(client, uri, 2, PROJECT)
        assert diagnostics(client, uri, 2)["diagnostics"] == []
        hovered, hover_ms = client.request(
            "textDocument/hover",
            {"textDocument": {"uri": uri}, "position": position(PROJECT, "choose")},
        )
        assert (
            "fn choose" in hovered["contents"]["value"]
            and "Path" in hovered["contents"]["value"]
        ), hovered
        observations["typed_hover_ms"] = hover_ms

        first, first_ms = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        repeated, repeated_ms = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        assert first["data"] and repeated == first, (first, repeated)
        observations.update(first_tokens_ms=first_ms, repeated_tokens_ms=repeated_ms)

        current, completion_ms = complete(client, uri, PROJECT)
        original = selected(current, "choose")
        assert original["kind"] == 3 and "fn choose" in original["detail"], original
        detailed, resolve_ms = resolve(client, original, supported)
        assert documentation(detailed), detailed
        observations.update(
            ordinary_completion_ms=completion_ms,
            selected_resolve_ms=resolve_ms,
            completion_items=len(current),
            completion_payload_bytes=len(
                json.dumps(current, ensure_ascii=False).encode("utf-8")
            ),
        )

        # Different documents may use the same binding name and client version.
        second = "let choose = true;\n"
        notify(client, second_uri, 2, second, opening=True)
        assert diagnostics(client, second_uri, 2)["diagnostics"] == []
        other_items, _ = complete(client, second_uri, second)
        other, _ = resolve(client, selected(other_items, "choose"), supported)
        assert (
            "Bool" in other["detail"] and "boolean" in documentation(other).lower()
        ), other
        again, _ = resolve(client, original, supported)
        assert again["kind"] == 3 and "fn choose" in again["detail"], again
        assert "boolean value" not in documentation(again).lower(), again

        receiver_source = (
            "scope Functions { entry: fn(value: Path) { value } };\n"
            "scope Paths { entry: ./source };\n"
            "Functions::entry(./source);\n"
            "Functions::entry;\n"
            "Paths::entry;\n"
        )
        notify(client, second_uri, 3, receiver_source)
        assert diagnostics(client, second_uri, 3)["diagnostics"] == []
        receiver_items: list[dict[str, Any]] = []
        for row, column in ((3, 11), (4, 7)):
            response, _ = client.request(
                "textDocument/completion",
                {
                    "textDocument": {"uri": second_uri},
                    "position": {"line": row, "character": column},
                },
            )
            receiver_items.append(selected(items(response), "entry"))
        function_entry, _ = resolve(client, receiver_items[0], supported)
        path_entry, _ = resolve(client, receiver_items[1], supported)
        assert function_entry["kind"] == 3 and "fn entry" in function_entry["detail"], (
            function_entry
        )
        assert path_entry["kind"] == 6 and "Path" in path_entry["detail"], path_entry
        assert "filesystem path" in documentation(path_entry).lower(), path_entry

        # A changed generation must neither reuse token bytes nor reinterpret old data.
        changed = "let choose = ./replacement;\n"
        notify(client, uri, 3, changed)
        assert diagnostics(client, uri, 3)["diagnostics"] == []
        new_tokens, _ = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        assert new_tokens != first, new_tokens
        stale, _ = resolve(client, original, supported)
        assert "fn choose" in stale["detail"] and "replacement" not in documentation(
            stale
        ), stale
        changed_items, _ = complete(client, uri, changed)
        changed_item, _ = resolve(client, selected(changed_items, "choose"), supported)
        assert (
            "Path" in changed_item["detail"]
            and "filesystem path" in documentation(changed_item).lower()
        ), changed_item

        client.send("textDocument/didClose", {"textDocument": {"uri": uri}})
        assert diagnostics(client, uri, None)["diagnostics"] == []
        reopened = "let choose = scope { fresh: true };\n"
        notify(client, uri, 2, reopened, opening=True)
        assert diagnostics(client, uri, 2)["diagnostics"] == []
        expired, _ = resolve(client, original, supported)
        assert "fn choose" in expired["detail"] and "fresh" not in documentation(
            expired
        ), expired
        reopened_items, _ = complete(client, uri, reopened)
        reopened_item, _ = resolve(
            client, selected(reopened_items, "choose"), supported
        )
        assert "Scope" in reopened_item["detail"] and "fresh" in documentation(
            reopened_item
        ), reopened_item
        assert not (workspace / "must-not-exist").exists()
        assert not any(
            message.get("method") in ("msync/sshPrompt", "window/showMessage")
            for message in client.notifications
        ), client.notifications
        observations["current_shadow_stale_close_reopen_isolation"] = True
        observations["same_label_receiver_isolation"] = True
        observations["source_effects"] = 0
        return observations
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    scratch = ROOT / ".temp/lsp-response"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=scratch) as temporary:
        fixture = Path(temporary)
        image = fixture / "image"
        image.mkdir()
        for path in binary.parent.iterdir():
            if path.is_file():
                shutil.copy2(path, image / path.name)
        records = []
        for negotiate in (False, True):
            workspace = fixture / ("negotiated" if negotiate else "eager")
            workspace.mkdir()
            records.append(run(image / binary.name, workspace, negotiate=negotiate))
        result = {
            "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "image_sha256": {
                path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in image.iterdir()
                if path.is_file()
            },
            "journeys": records,
        }
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(
            json.dumps(result, indent=2) + "\n", encoding="utf-8"
        )
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
