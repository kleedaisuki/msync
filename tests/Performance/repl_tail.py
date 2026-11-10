#!/usr/bin/env python3
"""Measure Native REPL session-age tails without mixing readiness and queue delay."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import math
import os
import queue
import subprocess
import sys
import threading
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Protocol, cast

ROOT = Path(__file__).resolve().parents[2]
SETUP = "let state = scope { n: 0 }; 0"


@dataclass(frozen=True)
class Sample:
    submission: int
    user_exports: int
    milliseconds: float
    render_milliseconds: float | None = None


class PromptTerminal(Protocol):
    plain: str
    raw: bytearray
    process: subprocess.Popen[bytes]

    def __init__(
        self,
        binary: Path,
        directory: Path,
        arguments: tuple[str, ...],
        no_color: bool = True,
    ) -> None: ...

    def key(self, value: bytes) -> None: ...
    def send(self, data: bytes) -> None: ...
    def line(self, value: str, start: int = 0) -> int: ...
    def prompt(self, start: int) -> None: ...
    def finish(self, log: Path) -> None: ...


def percentile(values: list[float], fraction: float) -> float:
    return sorted(values)[max(0, math.ceil(len(values) * fraction) - 1)]


def summarize(samples: list[Sample]) -> dict[str, Any]:
    values = [sample.milliseconds for sample in samples]
    result: dict[str, Any] = {
        "submissions": [samples[0].submission, samples[-1].submission],
        "user_exports": [samples[0].user_exports, samples[-1].user_exports],
        "count": len(values),
        "p50_ms": percentile(values, 0.50),
        "p95_ms": percentile(values, 0.95),
        "p99_ms": percentile(values, 0.99),
        "max_ms": max(values),
        "p99_upper_sample_count": len(values) - math.ceil(len(values) * 0.99) + 1,
    }
    renders = [
        sample.render_milliseconds
        for sample in samples
        if sample.render_milliseconds is not None
    ]
    if renders:
        result["render"] = {
            "p50_ms": percentile(renders, 0.50),
            "p95_ms": percentile(renders, 0.95),
            "p99_ms": percentile(renders, 0.99),
            "max_ms": max(renders),
        }
    return result


def submission(case: str, index: int) -> str:
    prefix = (
        f'let r{index} = scope {{ s: "row_{index}", p: ./item, b: true }}; '
        if case == "growing"
        else ""
    )
    return prefix + "state::n = state::n + 1; state::n"


def record(
    case: str, index: int, elapsed_ns: int, render_ns: int | None = None
) -> Sample:
    return Sample(
        index,
        index + 1 if case == "growing" else 1,
        elapsed_ns / 1_000_000,
        None if render_ns is None else render_ns / 1_000_000,
    )


def environment(directory: Path) -> dict[str, str]:
    home = directory / "home"
    home.mkdir(parents=True, exist_ok=True)
    return dict(
        os.environ, HOME=str(home), XDG_CONFIG_HOME=str(home / ".config"), NO_COLOR="1"
    )


def load_terminal() -> type[PromptTerminal]:
    if os.name != "posix":
        raise RuntimeError(
            "prompt mode currently requires the Linux PTY journey; burst mode is portable"
        )
    path = ROOT / "tests/Msync.EndToEnd/terminal_journey.py"
    specification = importlib.util.spec_from_file_location(
        "msync_terminal_journey", path
    )
    assert specification is not None and specification.loader is not None
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return cast(type[PromptTerminal], module.Terminal)


def prompt_samples(
    binary: Path, directory: Path, case: str, count: int
) -> tuple[list[Sample], float]:
    terminal_type = load_terminal()
    started = time.perf_counter_ns()
    terminal = terminal_type(
        binary, directory, ("--no-config", "--no-telemetry", "repl"), no_color=False
    )
    samples: list[Sample] = []
    segments: list[bytes] = []
    try:
        terminal.prompt(0)
        startup_ms = (time.perf_counter_ns() - started) / 1_000_000
        terminal.key(SETUP.encode())
        terminal.send(b"\r")
        terminal.prompt(terminal.line("0"))
        segments.append(bytes(terminal.raw))
        terminal.raw.clear()
        terminal.plain = ""
        for index in range(1, count + 1):
            render_started = time.perf_counter_ns()
            terminal.key(submission(case, index).encode())
            rendered = time.perf_counter_ns()
            started = time.perf_counter_ns()
            terminal.send(b"\r")
            terminal.prompt(terminal.line(str(index)))
            completed = time.perf_counter_ns()
            samples.append(
                record(case, index, completed - started, rendered - render_started)
            )
            # Bound parser/logging work by one request, not the entire history.
            segments.append(bytes(terminal.raw))
            terminal.raw.clear()
            terminal.plain = ""
    finally:
        terminal.finish(directory / "last-terminal-segment.log")
        segments.append((directory / "last-terminal-segment.log").read_bytes())
        (directory / "terminal.log").write_bytes(b"".join(segments))
    if terminal.process.returncode != 0:
        raise AssertionError(
            f"Terminal REPL exited {terminal.process.returncode}; see terminal.log"
        )
    return samples, startup_ms


def burst_samples(
    binary: Path, directory: Path, case: str, count: int
) -> tuple[list[Sample], float]:
    process = subprocess.Popen(
        [str(binary), "--no-config", "--no-telemetry", "repl"],
        cwd=directory,
        env=environment(directory),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    assert (
        process.stdin is not None
        and process.stdout is not None
        and process.stderr is not None
    )
    replies: queue.SimpleQueue[tuple[int, bytes] | None] = queue.SimpleQueue()
    stdout: list[bytes] = []
    stderr: list[bytes] = []

    def read_output() -> None:
        assert process.stdout is not None
        for line in process.stdout:
            completed = time.perf_counter_ns()
            stdout.append(line)
            replies.put((completed, line.rstrip(b"\r\n")))
        replies.put(None)

    def read_errors() -> None:
        assert process.stderr is not None
        stderr.append(process.stderr.read())

    output_reader = threading.Thread(target=read_output, daemon=True)
    error_reader = threading.Thread(target=read_errors, daemon=True)
    output_reader.start()
    error_reader.start()
    started = time.perf_counter_ns()
    process.stdin.write((SETUP + "\n").encode())
    process.stdin.flush()
    ready = replies.get(timeout=60)
    if ready is None or ready[1] != b"0":
        process.kill()
        raise AssertionError(f"REPL setup failed: {ready!r}")
    startup_ms = (ready[0] - started) / 1_000_000
    sources = (
        "\n".join(submission(case, index) for index in range(1, count + 1))
        + "\n/quit\n"
    )
    samples: list[Sample] = []
    began = time.perf_counter_ns()
    try:
        # One common enqueue instant: later replies include intentional queue delay.
        process.stdin.write(sources.encode())
        process.stdin.flush()
        for index in range(1, count + 1):
            reply = replies.get(timeout=120)
            if reply is None or reply[1] != str(index).encode():
                raise AssertionError(f"REPL result {index} differs: {reply!r}")
            samples.append(record(case, index, reply[0] - began))
        process.stdin.close()
        code = process.wait(timeout=30)
        output_reader.join(timeout=5)
        error_reader.join(timeout=5)
        if code != 0 or b"".join(stderr):
            raise AssertionError(f"REPL exited {code}: {b''.join(stderr)!r}")
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        (directory / "stdout.log").write_bytes(b"".join(stdout))
        (directory / "stderr.log").write_bytes(b"".join(stderr))
    return samples, startup_ms


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--mode", choices=("prompt", "burst"), required=True)
    parser.add_argument("--case", choices=("fixed", "growing"), required=True)
    parser.add_argument("--submissions", type=int, default=1024)
    parser.add_argument("--window", type=int, default=256)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    binary = options.binary.resolve()
    output = options.output.resolve()
    if options.submissions <= 0 or options.window <= 0:
        parser.error("submissions and window must be positive")
    directory = output.parent / (output.stem + "-work")
    directory.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    clock_deltas: list[float] = []
    for _ in range(4096):
        before = time.perf_counter_ns()
        after = time.perf_counter_ns()
        clock_deltas.append(float(after - before))
    run = prompt_samples if options.mode == "prompt" else burst_samples
    samples, startup_ms = run(binary, directory, options.case, options.submissions)
    report = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "clock_calibration": {
            "scope": "4096 adjacent counter-read deltas before product launch; descriptive overhead, never subtracted",
            "p50_ns": percentile(clock_deltas, 0.50),
            "p99_ns": percentile(clock_deltas, 0.99),
            "max_ns": max(clock_deltas),
        },
        "mode": options.mode,
        "case": options.case,
        "presentation": "Native default colors, lexical highlighting and hints"
        if options.mode == "prompt"
        else "Redirected stdout; NO_COLOR=1",
        "presentation_environment": {
            "TERM": "xterm-256color",
            "NO_COLOR": None,
            "color_policy": "auto",
            "rows": 30,
            "columns": 120,
        }
        if options.mode == "prompt"
        else {"NO_COLOR": "1", "redirected": True},
        "measurement": "Enter to verified result and next rendered prompt; typing outside timer"
        if options.mode == "prompt"
        else "Common offered-burst instant to numbered stdout result; includes producer pipe backpressure and queue delay, not prompt readiness or service time",
        "distribution": "fixed retained state"
        if options.case == "fixed"
        else "nonstationary growing exports; use age windows, not pooled tail claims",
        "percentile": "nearest rank ceil(N*p); raw samples retained; small windows have few P99 upper ranks",
        "render_measurement": "Paste write to first observed complete input text; separate from Enter to next ready prompt"
        if options.mode == "prompt"
        else None,
        "startup_ms": startup_ms,
        "startup_measurement": "spawn to first prompt"
        if options.mode == "prompt"
        else "setup write to first result; process spawn excluded",
        "windows": [
            summarize(samples[start : start + options.window])
            for start in range(0, len(samples), options.window)
        ],
        "overall_fixed_workload": summarize(samples)
        if options.case == "fixed"
        else None,
        "samples": [asdict(sample) for sample in samples],
        "functional": "Every numbered result equals the expected retained-state increment; process exit checked",
        "python": sys.version,
    }
    output.write_text(json.dumps(report, indent=2) + "\n")
    print(
        json.dumps(
            {key: value for key, value in report.items() if key not in {"samples"}},
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
