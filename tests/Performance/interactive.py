#!/usr/bin/env python3
"""Measure persistent native REPL and LSP request round trips on Windows/Linux."""

from __future__ import annotations

import argparse
import json
import queue
import subprocess
import threading
import time
from collections.abc import Sequence
from dataclasses import asdict
from pathlib import Path
from typing import IO, Any, cast

from journeys import ROOT, Image, Measurement, freeze, percentile

CASES = ("repl-copy", "repl-scalar", "lsp-initialize", "lsp-hover", "lsp-tokens")


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
        self.started = time.perf_counter_ns()
        self.process = subprocess.Popen(
            [image.executable, *arguments],
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


class Repl(NativeProcess):
    def __init__(self, image: Image, directory: Path) -> None:
        super().__init__(image, ["--no-config", "repl"], directory)
        self.lines: queue.Queue[bytes | None] = queue.Queue()
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self) -> None:
        for line in self.output:
            self.lines.put(line.rstrip(b"\r\n"))
        self.lines.put(None)

    def request(self, source: str) -> tuple[float, bytes]:
        started = time.perf_counter_ns()
        self.input.write(source.encode("utf-8") + b"\n")
        self.input.flush()
        reply = self.lines.get(timeout=30)
        elapsed = (time.perf_counter_ns() - started) / 1_000_000
        if reply is None:
            raise RuntimeError(
                f"REPL exited before response: {b''.join(self.errors)!r}"
            )
        return elapsed, reply

    def close(self) -> None:
        self.input.write(b"/quit\n")
        self.input.flush()
        self.finish()
        self.reader.join(timeout=2)


class Lsp(NativeProcess):
    def __init__(self, image: Image, directory: Path) -> None:
        super().__init__(image, ["--language-server"], directory)
        self.messages: queue.Queue[dict[str, Any] | Exception | None] = queue.Queue()
        self.identifier = 0
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
            return elapsed, message.get("result")

    def initialize(self) -> float:
        elapsed, result = self.request("initialize", {"capabilities": {}}, startup=True)
        assert result.get("capabilities") is not None, result
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
) -> list[Measurement]:
    selected = [
        image for image in images if case != "repl-scalar" or image.name != "rust"
    ]
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
                    assert (
                        reply.startswith(b"done:")
                        if image.name == "rust"
                        else reply == b"true"
                    ), reply
                    assert (
                        directories[image.name] / "roundtrip.txt"
                    ).read_bytes() == payload.encode()
                if iteration >= warmups:
                    values[image.name].append(elapsed)
        for client in clients.values():
            client.close()
    finally:
        for client in clients.values():
            client.abort()
    return [summary(image, case, values[image.name]) for image in selected]


def lsp_startup(
    images: Sequence[Image], directories: dict[str, Path], samples: int, warmups: int
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
) -> list[Measurement]:
    clients = {image.name: Lsp(image, directories[image.name]) for image in images}
    uri = (directories[images[0].name].parent / "editor.msync").as_uri()
    measurements: list[Measurement] = []
    try:
        for image in images:
            client = clients[image.name]
            client.initialize()
            declaration = (
                "value{0} := ./input{0}.txt"
                if image.name == "rust"
                else "let value{0} = ./input{0}.txt;"
            )
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
                        "character": 2 if image.name == "rust" else 6,
                    },
                },
            )
        for case in cases:
            values: dict[str, list[float]] = {image.name: [] for image in images}
            method = (
                "textDocument/hover"
                if case == "lsp-hover"
                else "textDocument/semanticTokens/full"
            )
            parameters: dict[str, Any] = {"textDocument": {"uri": uri}}
            for iteration in range(samples + warmups):
                order = list(images) if iteration % 2 == 0 else list(reversed(images))
                for image in order:
                    if case == "lsp-hover":
                        parameters["position"] = {
                            "line": bindings // 2,
                            "character": 2 if image.name == "rust" else 6,
                        }
                    elapsed, result = clients[image.name].request(method, parameters)
                    assert result is not None, (image.name, method)
                    if case == "lsp-tokens":
                        assert (
                            len(result["data"]) > 0 and len(result["data"]) % 5 == 0
                        ), result
                    if iteration >= warmups:
                        values[image.name].append(elapsed)
            measurements.extend(
                summary(image, case, values[image.name]) for image in images
            )
        for client in clients.values():
            client.close()
    finally:
        for client in clients.values():
            client.abort()
    return measurements


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    previous = parser.add_mutually_exclusive_group(required=True)
    previous.add_argument("--rust", type=Path)
    previous.add_argument("--previous-aot", type=Path)
    parser.add_argument("--aot", type=Path, required=True)
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
    output = options.output.resolve()
    root = output.parent / f"interactive-{time.time_ns()}"
    before = (
        freeze(options.rust.resolve(), "rust", root)
        if options.rust
        else freeze(options.previous_aot.resolve(), "before", root)
    )
    images = [before, freeze(options.aot.resolve(), "aot", root)]
    directories = {image.name: root / image.name for image in images}
    for directory in directories.values():
        directory.mkdir()
    cases: list[str] = options.case or list(CASES)
    measurements: list[Measurement] = []
    for case in ("repl-copy", "repl-scalar"):
        if case in cases:
            measurements.extend(
                repl_pair(images, directories, case, options.samples, options.warmups)
            )
    if "lsp-initialize" in cases:
        measurements.extend(
            lsp_startup(images, directories, options.samples, options.warmups)
        )
    requests = [case for case in cases if case in ("lsp-hover", "lsp-tokens")]
    if requests:
        measurements.extend(
            lsp_requests(
                images,
                directories,
                requests,
                options.samples,
                options.warmups,
                options.bindings,
            )
        )
    output.write_text(
        json.dumps(
            {
                "images": [asdict(image) for image in images],
                "method": {
                    "samples": options.samples,
                    "warmups": options.warmups,
                    "bindings": options.bindings,
                    "roundtrip": "client write/flush to parsed server response; persistent native process; AB/BA order",
                    "startup": "LSP initialization includes process creation; other requests exclude startup",
                    "repl_copy": "same literal overwrite with actual atomic publication; bytes checked after response",
                    "repl_scalar": "0.8-only expression result capability, not a legacy comparison",
                },
                "measurements": [asdict(item) for item in measurements],
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    print(output)


if __name__ == "__main__":
    main()
