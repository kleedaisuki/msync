#!/usr/bin/env python3
"""Measure persistent native REPL and LSP request round trips on Windows/Linux."""

from __future__ import annotations

import argparse
import json
import queue
import subprocess
import threading
import time
from collections.abc import Callable, Sequence
from dataclasses import asdict
from pathlib import Path
from typing import IO, Any, cast

from journeys import ROOT, Image, Measurement, freeze, percentile

CASES = (
    "repl-copy",
    "repl-scalar",
    "lsp-initialize",
    "lsp-hover",
    "lsp-tokens",
    "lsp-edit",
    "lsp-delta",
)
Observer = Callable[[Image, str, float], None]


def ignore_observation(image: Image, case: str, elapsed: float) -> None:
    pass


def summary(image: Image, case: str, values: list[float]) -> Measurement:
    result = Measurement(
        image.name,
        case,
        values,
        percentile(values, 0.5),
        percentile(values, 0.95),
        min(values),
        max(values),
    )
    print(
        f"{case:20s} {image.name:6s} p50={result.median_ms:9.3f} ms p95={result.p95_ms:9.3f} ms",
        flush=True,
    )
    return result


class NativeProcess:
    def __init__(self, image: Image, arguments: Sequence[str], directory: Path) -> None:
        self.command = [image.executable, *arguments]
        self.directory = directory
        self.diagnostics = directory / f"process-{time.time_ns()}.json"
        self.last_request: object = None
        self.last_response: object = None
        self.started = time.perf_counter_ns()
        self.process = subprocess.Popen(
            self.command,
            cwd=directory,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        assert (
            self.process.stdin is not None
            and self.process.stdout is not None
            and self.process.stderr is not None
        )
        self.input: IO[bytes] = self.process.stdin
        self.output: IO[bytes] = self.process.stdout
        self.errors: list[bytes] = []
        self.error_reader = threading.Thread(
            target=self._errors, args=(self.process.stderr,), daemon=True
        )
        self.error_reader.start()

    def _errors(self, stream: IO[bytes]) -> None:
        for line in stream:
            self.errors.append(line)

    def finish(self) -> None:
        self.input.close()
        code = self.process.wait(timeout=15)
        self.error_reader.join(timeout=2)
        if code != 0 or self.errors:
            raise RuntimeError(
                f"Native process exited {code}: {b''.join(self.errors).decode('utf-8', errors='replace')}"
            )

    def abort(self) -> None:
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait()
        self.error_reader.join(timeout=2)
        self.diagnostics.write_text(
            json.dumps(
                {
                    "command": self.command,
                    "cwd": str(self.directory),
                    "exit_code": self.process.returncode,
                    "stderr": b"".join(self.errors).decode("utf-8", errors="replace"),
                    "last_request": self.last_request,
                    "last_response": self.last_response,
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )


class Repl(NativeProcess):
    def __init__(self, image: Image, directory: Path) -> None:
        super().__init__(image, ["--no-config", "--no-telemetry", "repl"], directory)
        self.lines: queue.Queue[bytes | None] = queue.Queue()
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self) -> None:
        for line in self.output:
            self.lines.put(line.rstrip(b"\r\n"))
        self.lines.put(None)

    def request(self, source: str) -> tuple[float, bytes]:
        self.last_request = source
        started = time.perf_counter_ns()
        self.input.write(source.encode("utf-8") + b"\n")
        self.input.flush()
        reply = self.lines.get(timeout=30)
        elapsed = (time.perf_counter_ns() - started) / 1_000_000
        if reply is None:
            raise RuntimeError(
                f"REPL exited before response: {b''.join(self.errors)!r}"
            )
        self.last_response = reply.decode("utf-8", errors="replace")
        return elapsed, reply

    def close(self) -> None:
        self.input.write(b"/quit\n")
        self.input.flush()
        self.finish()
        self.reader.join(timeout=2)


class Lsp(NativeProcess):
    def __init__(self, image: Image, directory: Path) -> None:
        super().__init__(
            image, ["--no-config", "--no-telemetry", "--language-server"], directory
        )
        self.messages: queue.Queue[dict[str, Any] | Exception | None] = queue.Queue()
        self.identifier = 0
        self.capabilities: dict[str, Any] = {}
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self) -> None:
        try:
            while header := self.output.readline():
                length = 0
                while header not in (b"\r\n", b"\n"):
                    key, value = header.decode("ascii").split(":", 1)
                    if key.lower() == "content-length":
                        length = int(value.strip())
                    header = self.output.readline()
                self.messages.put(
                    cast(dict[str, Any], json.loads(self.output.read(length)))
                )
        except (OSError, ValueError) as error:
            self.messages.put(error)
        finally:
            self.messages.put(None)

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
        self.last_request = message
        payload = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode(
            "utf-8"
        )
        self.input.write(
            f"Content-Length: {len(payload)}\r\n\r\n".encode("ascii") + payload
        )
        self.input.flush()

    def request(
        self, method: str, parameters: dict[str, Any], *, startup: bool = False
    ) -> tuple[float, Any]:
        self.identifier += 1
        started = self.started if startup else time.perf_counter_ns()
        self.send(method, parameters, self.identifier)
        while True:
            message = self.messages.get(timeout=30)
            if message is None or isinstance(message, Exception):
                raise RuntimeError(
                    f"LSP closed before {method}: {message}; {b''.join(self.errors)!r}"
                )
            if message.get("id") != self.identifier:
                continue
            elapsed = (time.perf_counter_ns() - started) / 1_000_000
            if "error" in message:
                raise RuntimeError(f"LSP {method}: {message['error']}")
            self.last_response = message
            return elapsed, message.get("result")

    def initialize(self, *, delta: bool = False) -> float:
        capabilities: dict[str, Any] = (
            {
                "textDocument": {
                    "semanticTokens": {"requests": {"full": {"delta": True}}}
                }
            }
            if delta
            else {}
        )
        elapsed, result = self.request(
            "initialize", {"capabilities": capabilities}, startup=True
        )
        assert result.get("capabilities") is not None, result
        self.capabilities = result["capabilities"]
        if delta:
            assert result["capabilities"]["semanticTokensProvider"]["full"] == {
                "delta": True
            }, result
        self.send("initialized", {})
        return elapsed

    def close(self) -> None:
        self.request("shutdown", {})
        self.send("exit", {})
        self.finish()
        self.reader.join(timeout=2)


def repl_pair(
    images: Sequence[Image],
    directories: dict[str, Path],
    case: str,
    samples: int,
    warmups: int,
    observe: Observer = ignore_observation,
) -> list[Measurement]:
    selected = list(images)
    clients = {image.name: Repl(image, directories[image.name]) for image in selected}
    values: dict[str, list[float]] = {image.name: [] for image in selected}
    try:
        for iteration in range(samples + warmups):
            order = list(selected) if iteration % 2 == 0 else list(reversed(selected))
            payload = f"request-{iteration:06d}"
            for image in order:
                client = clients[image.name]
                source = (
                    f'"{payload}"'
                    if case == "repl-scalar"
                    else f'"{payload}" -> roundtrip.txt'
                )
                elapsed, reply = client.request(source)
                if case == "repl-scalar":
                    assert reply == payload.encode(), reply
                else:
                    assert reply == b"true", reply
                    assert (
                        directories[image.name] / "roundtrip.txt"
                    ).read_bytes() == payload.encode()
                if iteration >= warmups:
                    values[image.name].append(elapsed)
                    observe(image, case, elapsed)
        for client in clients.values():
            client.close()
    finally:
        for client in clients.values():
            client.abort()
    return [summary(image, case, values[image.name]) for image in selected]


def lsp_startup(
    images: Sequence[Image],
    directories: dict[str, Path],
    samples: int,
    warmups: int,
    observe: Observer = ignore_observation,
) -> list[Measurement]:
    values: dict[str, list[float]] = {image.name: [] for image in images}
    for iteration in range(samples + warmups):
        order = list(images) if iteration % 2 == 0 else list(reversed(images))
        for image in order:
            client = Lsp(image, directories[image.name])
            try:
                elapsed = client.initialize()
                client.close()
                if iteration >= warmups:
                    values[image.name].append(elapsed)
                    observe(image, "lsp-initialize", elapsed)
            finally:
                client.abort()
    return [summary(image, "lsp-initialize", values[image.name]) for image in images]


def lsp_requests(
    images: Sequence[Image],
    directories: dict[str, Path],
    cases: Sequence[str],
    samples: int,
    warmups: int,
    bindings: int,
    observe: Observer = ignore_observation,
) -> list[Measurement]:
    clients = {image.name: Lsp(image, directories[image.name]) for image in images}
    uri = (directories[images[0].name].parent / "editor.msync").as_uri()
    measurements: list[Measurement] = []
    try:
        for image in images:
            client = clients[image.name]
            client.initialize()
            declaration = "let value{0} = ./input{0}.txt;"
            source = (
                "\n".join(declaration.format(index) for index in range(bindings)) + "\n"
            )
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
            client.request(
                "textDocument/hover",
                {
                    "textDocument": {"uri": uri},
                    "position": {
                        "line": 0,
                        "character": 6,
                    },
                },
            )
        for case in cases:
            values: dict[str, list[float]] = {image.name: [] for image in images}
            method = (
                "textDocument/hover"
                if case in ("lsp-hover", "lsp-edit")
                else "textDocument/semanticTokens/full"
            )
            parameters: dict[str, Any] = {"textDocument": {"uri": uri}}
            for iteration in range(samples + warmups):
                order = list(images) if iteration % 2 == 0 else list(reversed(images))
                for image in order:
                    if case in ("lsp-hover", "lsp-edit"):
                        parameters["position"] = {
                            "line": bindings // 2,
                            "character": 6,
                        }
                    client = clients[image.name]
                    if case == "lsp-edit":
                        number = iteration % 2 == 0
                        replacement = "12345" if number else "true"
                        text = (
                            "\n".join(
                                f"let value{index} = {replacement};"
                                if index == bindings // 2
                                else declaration.format(index)
                                for index in range(bindings)
                            )
                            + "\n"
                        )
                        started = time.perf_counter_ns()
                        client.send(
                            "textDocument/didChange",
                            {
                                "textDocument": {"uri": uri, "version": iteration + 2},
                                "contentChanges": [{"text": text}],
                            },
                        )
                        _, result = client.request(method, parameters)
                        elapsed = (time.perf_counter_ns() - started) / 1_000_000
                        expected = (
                            f"value{bindings // 2}: {'Number' if number else 'Bool'}"
                        )
                        assert expected in result["contents"]["value"], (
                            expected,
                            result,
                        )
                    else:
                        elapsed, result = client.request(method, parameters)
                    assert result is not None, (image.name, method)
                    if case == "lsp-hover":
                        assert (
                            f"value{bindings // 2}: Path" in result["contents"]["value"]
                        ), result
                    if case == "lsp-tokens":
                        assert (
                            len(result["data"]) > 0 and len(result["data"]) % 5 == 0
                        ), result
                    if iteration >= warmups:
                        values[image.name].append(elapsed)
                        observe(image, case, elapsed)
            measurements.extend(
                summary(image, case, values[image.name]) for image in images
            )
        for client in clients.values():
            client.close()
    finally:
        for client in clients.values():
            client.abort()
    return measurements


def apply_delta(previous: list[int], result: dict[str, Any]) -> list[int]:
    if "data" in result:
        return list(result["data"])
    values = previous.copy()
    for edit in reversed(result["edits"]):
        start = int(edit["start"])
        values[start : start + int(edit["deleteCount"])] = edit.get("data", [])
    return values


def check_numeric_tokens(
    data: list[int], legend: list[str], bindings: int, row: int, text: str
) -> None:
    actual: list[tuple[int, int, int, str]] = []
    line = 0
    column = 0
    assert len(data) % 5 == 0
    for index in range(0, len(data), 5):
        delta, offset, length, kind, _ = data[index : index + 5]
        line += delta
        column = offset if delta else column + offset
        actual.append((line, column, length, legend[kind]))
    expected: list[tuple[int, int, int, str]] = []
    for index in range(bindings):
        name = f"value{index}"
        expected.extend(
            [
                (index, 0, 3, "keyword"),
                (index, 4, len(name), "variable"),
                (index, 5 + len(name), 1, "operator"),
                (
                    index,
                    7 + len(name),
                    len(text if index == row else str(index)),
                    "number",
                ),
            ]
        )
    assert actual == expected, (actual[:12], expected[:12])


def lsp_delta(
    images: Sequence[Image],
    directories: dict[str, Path],
    samples: int,
    warmups: int,
    bindings: int,
    observe: Observer = ignore_observation,
) -> list[Measurement]:
    clients = {image.name: Lsp(image, directories[image.name]) for image in images}
    uri = (directories[images[0].name].parent / "delta.msync").as_uri()
    target: dict[str, Any] = {"textDocument": {"uri": uri}}
    prior: dict[str, dict[str, Any]] = {}
    tokens: dict[str, list[int]] = {}
    legends: dict[str, list[str]] = {}
    values: dict[str, list[float]] = {image.name: [] for image in images}
    row = bindings // 2
    try:
        for image in images:
            client = clients[image.name]
            client.initialize(delta=True)
            legends[image.name] = client.capabilities["semanticTokensProvider"][
                "legend"
            ]["tokenTypes"]
            text = (
                "\n".join(f"let value{index} = {index};" for index in range(bindings))
                + "\n"
            )
            client.send(
                "textDocument/didOpen",
                {
                    "textDocument": {
                        "uri": uri,
                        "languageId": "msync",
                        "version": 1,
                        "text": text,
                    }
                },
            )
            _, result = client.request("textDocument/semanticTokens/full", target)
            prior[image.name] = result
            tokens[image.name] = list(result["data"])
            check_numeric_tokens(
                tokens[image.name], legends[image.name], bindings, row, str(row)
            )
        for iteration in range(samples + warmups):
            replacement = "999999" if iteration % 2 == 0 else str(row)
            text = (
                "\n".join(
                    f"let value{index} = {replacement if index == row else str(index)};"
                    for index in range(bindings)
                )
                + "\n"
            )
            order = list(images) if iteration % 2 == 0 else list(reversed(images))
            for image in order:
                client = clients[image.name]
                started = time.perf_counter_ns()
                client.send(
                    "textDocument/didChange",
                    {
                        "textDocument": {"uri": uri, "version": iteration + 2},
                        "contentChanges": [{"text": text}],
                    },
                )
                _, result = client.request(
                    "textDocument/semanticTokens/full/delta",
                    {
                        **target,
                        "previousResultId": prior[image.name]["resultId"],
                    },
                )
                elapsed = (time.perf_counter_ns() - started) / 1_000_000
                assert result["resultId"] != prior[image.name]["resultId"], result
                reconstructed = apply_delta(tokens[image.name], result)
                check_numeric_tokens(
                    reconstructed, legends[image.name], bindings, row, replacement
                )
                _, full = client.request("textDocument/semanticTokens/full", target)
                assert (
                    full["data"] == reconstructed
                    and full["resultId"] == result["resultId"]
                )
                prior[image.name] = result
                tokens[image.name] = reconstructed
                if iteration >= warmups:
                    values[image.name].append(elapsed)
                    observe(image, "lsp-delta", elapsed)
        for client in clients.values():
            client.close()
    finally:
        for client in clients.values():
            client.abort()
    return [summary(image, "lsp-delta", values[image.name]) for image in images]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--previous-aot", type=Path)
    parser.add_argument("--aot", type=Path)
    parser.add_argument("--case", choices=CASES, action="append")
    parser.add_argument("--samples", type=int, default=31)
    parser.add_argument("--warmups", type=int, default=3)
    parser.add_argument("--bindings", type=int, default=128)
    parser.add_argument(
        "--output",
        type=Path,
        default=ROOT / ".temp/performance-v080/current/interactive.json",
    )
    options = parser.parse_args()
    if options.samples <= 0 or options.warmups < 0 or options.bindings <= 0:
        parser.error("samples/bindings must be positive and warmups nonnegative")
    if options.binary is not None:
        if options.previous_aot is not None or options.aot is not None:
            parser.error(
                "--binary/--baseline cannot be combined with legacy image flags"
            )
        selected: list[tuple[str, Path]] = (
            [("before", options.baseline)] if options.baseline is not None else []
        ) + [("aot", options.binary)]
    elif (
        options.previous_aot is not None
        and options.aot is not None
        and options.baseline is None
    ):
        selected = [("before", options.previous_aot), ("aot", options.aot)]
    else:
        parser.error(
            "use --binary CURRENT [--baseline BEFORE], or --previous-aot BEFORE --aot CURRENT"
        )
    output = options.output.resolve()
    root = output.parent / f"interactive-{time.time_ns()}"
    output.parent.mkdir(parents=True, exist_ok=True)
    cases: list[str] = options.case or list(CASES)
    measurements: list[Measurement] = []
    report: dict[str, Any] = {
        "status": "running",
        "active_case": None,
        "images": [],
        "method": {
            "samples": options.samples,
            "warmups": options.warmups,
            "bindings": options.bindings,
            "roundtrip": "client write/flush to parsed server response; persistent native process",
            "startup": "LSP initialization includes process creation; other requests exclude startup",
            "repl_copy": "versioned literal overwrite with atomic publication; bytes checked after response",
            "repl_scalar": "expression result bytes checked for each frozen Native AOT image",
            "lsp_edit": "didChange flush through current-type hover response; no readiness wait outside timing",
            "lsp_delta": "didChange through negotiated delta; reconstruction checked against independent positions and current full response outside timing",
        },
        "measurements": [],
        "observations_ms": {},
    }

    def checkpoint() -> None:
        report["measurements"] = [asdict(item) for item in measurements]
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    def observe(image: Image, case: str, elapsed: float) -> None:
        report["active_case"] = case
        report["observations_ms"].setdefault(case, {}).setdefault(
            image.name, []
        ).append(elapsed)
        checkpoint()

    try:
        images = [freeze(path.resolve(), name, root) for name, path in selected]
        report["images"] = [asdict(image) for image in images]
        report["method"]["order"] = (
            "single image" if len(images) == 1 else "alternating AB/BA"
        )
        directories = {image.name: root / image.name for image in images}
        for directory in directories.values():
            directory.mkdir()
        checkpoint()
        for case in cases:
            report["active_case"] = case
            checkpoint()
            if case in ("repl-copy", "repl-scalar"):
                result = repl_pair(
                    images, directories, case, options.samples, options.warmups, observe
                )
            elif case == "lsp-initialize":
                result = lsp_startup(
                    images, directories, options.samples, options.warmups, observe
                )
            elif case == "lsp-delta":
                result = lsp_delta(
                    images,
                    directories,
                    options.samples,
                    options.warmups,
                    options.bindings,
                    observe,
                )
            else:
                result = lsp_requests(
                    images,
                    directories,
                    [case],
                    options.samples,
                    options.warmups,
                    options.bindings,
                    observe,
                )
            measurements.extend(result)
            checkpoint()
        report.update(status="complete", active_case=None)
    except BaseException as error:
        report.update(status="failed", error=repr(error), diagnostics=str(root))
        raise
    finally:
        checkpoint()
    print(output)


if __name__ == "__main__":
    main()
