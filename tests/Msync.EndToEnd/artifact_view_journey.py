#!/usr/bin/env python3
"""Read real bytecode through the editor protocol without executing its effects."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import subprocess
import tempfile
from pathlib import Path
from typing import Any, cast

from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]
Json = dict[str, Any]


def invoke(
    binary: Path, cwd: Path, arguments: list[str]
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        [str(binary), "--no-config", "--color", "never", *arguments],
        cwd=cwd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)
    return result


def payload(path: Path, *, uri: str | None = None) -> Json:
    return {
        "uri": uri or path.as_uri(),
        "data": base64.b64encode(path.read_bytes()).decode("ascii"),
    }


def receive(client: Client, identifier: int) -> Json:
    for index, saved in enumerate(client.notifications):
        if saved.get("id") == identifier:
            return client.notifications.pop(index)
    while True:
        response = client.responses.get(timeout=30)
        if isinstance(response, Exception):
            raise response
        if response.get("id") == identifier:
            return response
        client.notifications.append(response)


def request(client: Client, method: str, parameters: Json) -> Json:
    client.next_id += 1
    client.send(method, parameters, client.next_id)
    return receive(client, client.next_id)


def position(value: Json, lines: list[str]) -> tuple[int, int]:
    line = int(value["line"])
    character = int(value["character"])
    assert 0 <= line < len(lines), (value, len(lines))
    assert 0 <= character <= len(lines[line].encode("utf-16-le")) // 2, value
    return line, character


def checked_range(
    value: Json, lines: list[str]
) -> tuple[tuple[int, int], tuple[int, int]]:
    start = position(cast(Json, value["start"]), lines)
    end = position(cast(Json, value["end"]), lines)
    assert start <= end, value
    return start, end


def inspect_model(model: Json, stage: str) -> dict[str, int]:
    assert model["schemaVersion"] == 1, model
    assert model["languageVersion"] == "0.8.0", model
    assert isinstance(model["formatVersion"], int) and model["formatVersion"] > 0
    assert model["stage"] == stage, model["stage"]
    assert model["optimizationLevel"] == 0, model["optimizationLevel"]
    text = str(model["text"])
    assert "\x1b" not in text
    for expected in ("select", "Path", "Bool", "MUST NOT EXECUTE"):
        assert expected in text, (expected, text)
    assert "const" in text.lower() and "block" in text.lower(), text
    lines = text.split("\n")
    counts = {
        "symbols": 0,
        "internal_references": 0,
        "source_references": 0,
        "hovers": 0,
    }

    def symbols(values: list[Json]) -> None:
        for symbol in values:
            counts["symbols"] += 1
            assert symbol["name"] and isinstance(symbol["kind"], int), symbol
            begin, end = checked_range(cast(Json, symbol["range"]), lines)
            selected_begin, selected_end = checked_range(
                cast(Json, symbol["selectionRange"]), lines
            )
            assert begin <= selected_begin <= selected_end <= end, symbol
            symbols(cast(list[Json], symbol.get("children", [])))

    symbols(cast(list[Json], model["symbols"]))
    assert counts["symbols"] > 0
    for reference in cast(list[Json], model["references"]):
        checked_range(cast(Json, reference["range"]), lines)
        if "targetLine" in reference:
            position(
                {
                    "line": reference["targetLine"],
                    "character": reference["targetCharacter"],
                },
                lines,
            )
            counts["internal_references"] += 1
        elif "sourceUri" in reference:
            assert isinstance(reference["sourceUri"], str)
            assert reference["sourceStart"] >= 0 and reference["sourceLength"] >= 0
            counts["source_references"] += 1
    assert counts["internal_references"] > 0
    for hover in cast(list[Json], model["hovers"]):
        checked_range(cast(Json, hover["range"]), lines)
        assert hover["markdown"], hover
        counts["hovers"] += 1
    assert counts["hovers"] > 0
    return counts


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    parent = ROOT / ".temp" / "artifact-view"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="协议 空间-", dir=parent) as temporary:
        cwd = Path(temporary)
        source = cwd / "工作 𐐀.msync"
        effect_path = str(cwd / "viewer-side-effect").replace('"', '""')
        source.write_text(
            "fn select(file: Path, ready: Bool) -> Path { if ready { file } else { ./unused } };\n"
            "let chosen = select(./input, exists ./not-present);\n"
            f'"MUST NOT EXECUTE <script>alert(""artifact"")</script> command:msync.test.artifactInjection \u0001" -> "{effect_path}";\n'
            "chosen\n",
            encoding="utf-8",
        )
        relocatable = cwd / "工作 𐐀.mso"
        executable = cwd / "工作 𐐀.mexe"
        invoke(binary, cwd, ["compile", str(source), "-O0", "-o", str(relocatable)])
        invoke(
            binary,
            cwd,
            [
                "bind-ir",
                str(relocatable),
                "--source-file",
                str(source),
                "-o",
                str(executable),
            ],
        )
        original = {
            path: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in (relocatable, executable)
        }
        source.unlink()
        client = Client(binary, cwd=cwd)
        summaries: dict[str, object] = {}
        try:
            initialized, _ = client.request("initialize", {"capabilities": {}})
            assert initialized["serverInfo"]["version"] == "0.8.0"
            live_uri = (cwd / "live source.msync").as_uri()
            client.send(
                "textDocument/didOpen",
                {
                    "textDocument": {
                        "uri": live_uri,
                        "languageId": "msync",
                        "version": 1,
                        "text": "let .codex = ~/.codex;\n.codex;\n",
                    }
                },
            )
            live_hover = {
                "textDocument": {"uri": live_uri},
                "position": {"line": 0, "character": 5},
            }
            initial_hover = request(client, "textDocument/hover", live_hover)
            assert "Path" in initial_hover["result"]["contents"]["value"], initial_hover
            for path, stage in (
                (relocatable, "relocatable"),
                (executable, "executable"),
            ):
                response = request(client, "msync/artifact", payload(path))
                assert "error" not in response, response
                model = cast(Json, response["result"])
                summaries[stage] = inspect_model(model, stage)

            virtual = request(
                client,
                "msync/artifact",
                payload(relocatable, uri="memfs:/工作 空间/project.mso"),
            )
            assert "error" not in virtual, virtual
            inspect_model(cast(Json, virtual["result"]), "relocatable")

            raw = relocatable.read_bytes()
            source_name = str(source).encode("utf-8")
            prefix = b"command:msync.test.artifactInjection#"
            assert len(source_name) > len(prefix) and source_name in raw
            injected = raw.replace(
                source_name, prefix + b"x" * (len(source_name) - len(prefix))
            )
            unsafe = request(
                client,
                "msync/artifact",
                {
                    "uri": relocatable.as_uri(),
                    "data": base64.b64encode(injected).decode("ascii"),
                },
            )
            assert "error" not in unsafe, unsafe
            unsafe_model = cast(Json, unsafe["result"])
            assert "command:msync.test.artifactInjection" in unsafe_model["text"]
            assert not any(
                str(reference.get("sourceUri", "")).startswith("command:")
                for reference in cast(list[Json], unsafe_model["references"])
            ), "Untrusted debug metadata became an actionable command link"

            for malformed in (
                {"uri": relocatable.as_uri(), "data": "not-base64"},
                {
                    "uri": relocatable.as_uri(),
                    "data": base64.b64encode(b"not msync bytecode").decode("ascii"),
                },
            ):
                response = request(client, "msync/artifact", malformed)
                assert response["error"]["code"] in (-32602, -32001), response
                assert response["error"]["message"], response
            unknown = bytearray(relocatable.read_bytes())
            unknown[8:10] = (65535).to_bytes(2, "little")
            response = request(
                client,
                "msync/artifact",
                {
                    "uri": relocatable.as_uri(),
                    "data": base64.b64encode(unknown).decode("ascii"),
                },
            )
            assert (
                response["error"]["code"] == -32001
                and "65535" in response["error"]["message"]
            ), response
            missing = request(client, "msync/artifact", {"uri": relocatable.as_uri()})
            assert missing["error"]["code"] == -32602, missing
            recovered = request(client, "msync/artifact", payload(relocatable))
            assert "error" not in recovered, recovered

            large_source = cwd / "large.msync"
            large_source.write_text(
                "".join(
                    f'"value-{index}" -> ./never-{index};\n' for index in range(2048)
                ),
                encoding="utf-8",
            )
            large = cwd / "large.mso"
            invoke(binary, cwd, ["compile", str(large_source), "-O0", "-o", str(large)])
            client.next_id += 1
            cancelled_id = client.next_id
            client.send("msync/artifact", payload(large), cancelled_id)
            client.send("$/cancelRequest", {"id": cancelled_id})
            client.next_id += 1
            hover_id = client.next_id
            client.send("textDocument/hover", live_hover, hover_id)
            response = receive(client, cancelled_id)
            assert response["error"]["code"] == -32800, response
            hover_after_cancel = receive(client, hover_id)
            assert "Path" in hover_after_cancel["result"]["contents"]["value"], (
                hover_after_cancel
            )
            final = request(client, "msync/artifact", payload(executable))
            assert "error" not in final, final
            summaries["cancelled_inflight"] = 1
            summaries["source_hover_after_cancel"] = 1
            summaries["invalid_payload_recovery"] = 4
        finally:
            client.close()
        for path, digest in original.items():
            assert hashlib.sha256(path.read_bytes()).hexdigest() == digest, path
        assert not (cwd / "viewer-side-effect").exists()
        assert not list(cwd.glob("never-*")), "Inspection executed a transfer"
        summaries["binary_sha256"] = {
            path.name: digest for path, digest in original.items()
        }
        summaries["binary"] = str(binary)
        summaries["program_sha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
        summaries["implementation_sha256"] = {
            path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(binary.parent.glob("*.dll"))
            if path.name.startswith("Msync.") or path.name == "msync.dll"
        }
        report = json.dumps(summaries, ensure_ascii=False, indent=2)
        if arguments.output:
            arguments.output.parent.mkdir(parents=True, exist_ok=True)
            arguments.output.write_text(report + "\n", encoding="utf-8")
        print(json.dumps(summaries, indent=2))


if __name__ == "__main__":
    main()
