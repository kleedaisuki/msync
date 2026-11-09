#!/usr/bin/env python3
"""Measure expanded-library CLI and real-terminal latency in one immutable image."""

from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import os
import platform
import statistics
import subprocess
import sys
import tempfile
import time
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import Protocol, cast

ROOT = Path(__file__).resolve().parents[2]
SOURCES: dict[str, str] = {
    "none": "let value = true; value",
    "bool": "$std::bool::both(true, false)",
    "ranges": "$std::ranges::size($std::ranges::take([1, 2, 3], 2))",
    "container_list": (
        "let list = $std::containers::List::new([1, 2, 3]); "
        "$std::containers::List::size(list)"
    ),
    "meta": "$std::meta::is_convertible(Path, String)",
    "shell": "$std::shell::pwd()",
}


class Terminal(Protocol):
    plain: str
    raw: bytes | bytearray | str

    def until(self, predicate: Callable[[], bool], description: str) -> None: ...
    def key(self, value: bytes | str) -> None: ...
    def send(self, value: bytes | str) -> None: ...
    def line(self, value: str, start: int = 0) -> int: ...
    def prompt(self, start: int) -> None: ...
    def finish(self, log: Path) -> None: ...


class TerminalFactory(Protocol):
    def __call__(self, binary: Path, directory: Path) -> Terminal: ...


def distribution(values: Sequence[float]) -> dict[str, object]:
    ordered = sorted(values)
    position = (len(ordered) - 1) * 0.95
    first = int(position)
    last = min(first + 1, len(ordered) - 1)
    return {
        "p50_ms": statistics.median(ordered),
        "p95_ms": ordered[first]
        + (ordered[last] - ordered[first]) * (position - first),
        "raw_ms": list(values),
    }


def image_hashes(binary: Path) -> dict[str, str]:
    files = [binary, *sorted(binary.parent.glob("Msync.*.dll"))]
    entry = binary.with_suffix(".dll")
    if entry.is_file() and entry not in files:
        files.append(entry)
    return {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in files}


def cli(
    binary: Path, directory: Path, samples: int, selected: Sequence[str]
) -> dict[str, object]:
    cases: dict[str, list[str]] = {"startup": ["--version"]}
    for name, source in SOURCES.items():
        path = directory / f"{name}.msync"
        path.write_text(source, encoding="utf-8")
        cases[f"check_{name}"] = ["--no-config", "check", str(path)]
    result: dict[str, object] = {}
    for name, arguments in cases.items():
        if name.removeprefix("check_") not in selected:
            continue
        times: list[float] = []
        failure: dict[str, object] | None = None
        for sample in range(samples + 1):
            began = time.perf_counter_ns()
            process = subprocess.run(
                [str(binary), *arguments],
                cwd=directory,
                capture_output=True,
                timeout=60,
                check=False,
                env={**os.environ, "NO_COLOR": "1"},
            )
            elapsed = (time.perf_counter_ns() - began) / 1_000_000
            (directory / f"{name}-{sample}.stdout").write_bytes(process.stdout)
            (directory / f"{name}-{sample}.stderr").write_bytes(process.stderr)
            if process.returncode != 0:
                failure = {
                    "status": "failed",
                    "returncode": process.returncode,
                    "stderr": process.stderr.decode("utf-8", errors="replace"),
                }
                break
            if sample:
                times.append(elapsed)
        result[name] = failure or {"status": "passed", **distribution(times)}
    return result


def terminal_factory() -> TerminalFactory:
    sys.path.insert(0, str(ROOT / "tests/Msync.EndToEnd"))
    module = importlib.import_module(
        "windows_terminal_journey" if os.name == "nt" else "terminal_journey"
    )
    return cast(TerminalFactory, module.Terminal)


def terminal_input(text: str) -> str | bytes:
    return text if os.name == "nt" else text.encode("utf-8")


def terminal_sample(
    binary: Path, directory: Path, completion: str, timings: dict[str, float]
) -> None:
    directory.mkdir()
    # The existing terminal journey's unique path proves the two-Tab selection.
    (directory / "completion-probe-unique.txt").write_text(
        "TAB_READY", encoding="utf-8"
    )
    factory = terminal_factory()
    began = time.perf_counter_ns()
    terminal = factory(binary, directory)
    try:
        terminal.until(lambda: "> " in terminal.plain, "first REPL prompt")
        timings["first_prompt_ms"] = (time.perf_counter_ns() - began) / 1_000_000
        prefix_began = time.perf_counter_ns()
        terminal.key(
            terminal_input(
                "./completion-probe-u"
                if completion == "path"
                else "$std::containers::Lis"
            )
        )
        timings["first_prefix_render_ms"] = (
            time.perf_counter_ns() - prefix_began
        ) / 1_000_000
        tab_began = time.perf_counter_ns()
        terminal.key(terminal_input("\t"))
        timings["first_tab_render_ms"] = (
            time.perf_counter_ns() - tab_began
        ) / 1_000_000
        terminal.key(terminal_input("\t"))
        start = len(terminal.plain)
        if completion == "path":
            terminal.key(terminal_input(' -> ./completed; "COMPLETION_READY"'))
            terminal.send(terminal_input("\r"))
            terminal.prompt(terminal.line("COMPLETION_READY", start))
            actual = (directory / "completed").read_text(encoding="utf-8")
            if actual != "TAB_READY":
                raise AssertionError(f"Tab selected the wrong path: {actual!r}")
        else:
            terminal.key(
                terminal_input(
                    "::size($std::containers::List::new([1, 2, 3])) as String"
                )
            )
            terminal.send(terminal_input("\r"))
            terminal.prompt(terminal.line("3", start))
    finally:
        terminal.finish(directory / "terminal.log")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument(
        "--work-root", type=Path, default=ROOT / ".temp/stdlib-expansion"
    )
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--skip-terminal", action="store_true")
    parser.add_argument(
        "--terminal-completion", choices=["path", "list"], default="path"
    )
    parser.add_argument("--case", action="append", choices=["startup", *SOURCES])
    options = parser.parse_args()
    binary: Path = options.binary.resolve()
    scratch: Path = options.work_root.resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix="journey-", dir=scratch))
    hashes = image_hashes(binary)
    report: dict[str, object] = {
        "binary": str(binary),
        "image_kind": "managed" if binary.with_suffix(".dll").is_file() else "native",
        "hashes": hashes,
        "platform": platform.platform(),
        "python": {"executable": sys.executable, "version": sys.version},
        "workspace": str(workspace),
        "method": {
            "samples": options.samples,
            "untimed_cli_warmups": 1,
            "cli": "fresh process, default optimization, warm OS cache",
            "terminal": "actual ConPTY" if os.name == "nt" else "actual Linux PTY",
            "first_tab": "keypress to first repaint; two-Tab selection verified by evaluated result",
            "terminal_completion": options.terminal_completion,
            "comparison": "same image no-stdlib versus selected library; no Debug/Release mixing",
        },
    }
    try:
        cli_results = cli(
            binary, workspace, options.samples, options.case or ["startup", *SOURCES]
        )
        report["cli"] = cli_results
        if not options.skip_terminal:
            raw: list[dict[str, float]] = []
            report["terminal_samples"] = raw
            for sample in range(options.samples):
                timings: dict[str, float] = {}
                raw.append(timings)
                terminal_sample(
                    binary,
                    workspace / f"terminal-{sample}",
                    options.terminal_completion,
                    timings,
                )
            report["terminal"] = {
                key: distribution([sample[key] for sample in raw]) for key in raw[0]
            }
        if image_hashes(binary) != hashes:
            raise RuntimeError("The measured image changed during observation")
        if any(
            isinstance(item, dict) and item.get("status") == "failed"
            for item in cli_results.values()
        ):
            raise RuntimeError("A CLI workload failed; see recorded case diagnostics")
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["failure"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(
            json.dumps(report, indent=2, ensure_ascii=True) + "\n", encoding="utf-8"
        )
    print(json.dumps(report, ensure_ascii=True))


if __name__ == "__main__":
    main()
