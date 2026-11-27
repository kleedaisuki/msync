#!/usr/bin/env python3
"""Exercise a real REPL's effects, diagnosed failures and session ownership."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from collections.abc import Mapping
from pathlib import Path
from typing import TypedDict

ROOT: Path = Path(__file__).resolve().parents[2]
PAYLOAD: bytes = b"copied through a live REPL\x00\xff\n"


class Span(TypedDict):
    name: str
    status: str
    attributes: dict[str, str]
    metrics: dict[str, int]
    error_chain: list[dict[str, str | int | None]]


def submit(
    cli: list[str],
    work: Path,
    inputs: list[str],
    optimization: int = 2,
    env: Mapping[str, str] | None = None,
) -> tuple[subprocess.CompletedProcess[str], list[Span]]:
    trace: Path = work / "repl.jsonl"
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [
            *cli,
            "--no-config",
            "--color",
            "never",
            f"-O{optimization}",
            "--trace",
            str(trace),
            "--telemetry-level",
            "diagnostics",
            "repl",
        ],
        cwd=work,
        input="\n".join(inputs) + "\n",
        text=True,
        encoding="utf-8",
        capture_output=True,
        timeout=30,
        env=env,
        check=False,
    )
    return result, [json.loads(row) for row in trace.read_text().splitlines()]


def check_transfer(cli: list[str], work: Path, optimization: int) -> None:
    (work / "packs").mkdir(parents=True)
    (work / "packs" / "file.bin").write_bytes(PAYLOAD)
    (work / "output").mkdir()
    (work / "text.txt").write_bytes(b"forwarded\n")
    result, spans = submit(
        cli,
        work,
        [
            'let dir: Path = "./packs";',
            "dir -> ./output/",
            'if dir -> ./branch/ { "copied" } else { "not copied" }',
            'let missing: Path = "./does-not-exist";',
            "let prefix = 61; missing -> ./unwritten",
            "prefix",
            "fn older() { $std::fs::canonical(./missing-from-older) };",
            "older()",
            'let file: Path = "./packs/file.bin";',
            "$std::io::file_size(file)",
            "let size = $std::io::file_size(file);",
            "size",
            "$std::fs::is_regular_file(file)",
            "fn maybe(show: Bool) -> Number? { if show { $std::io::file_size(file) } else { null } };",
            "maybe(true)",
            "maybe(false)",
            "$std::io::read_text(./text.txt) -> ./forwarded.txt",
            "let text = $std::io::read_text(./text.txt);",
            "text",
            "object { items: [1, 2], caption: $std::io::read_text(./text.txt) }",
            "let stable = 9;",
            'let stable = "bad number" as Number;',
            "stable",
        ],
        optimization,
    )
    assert result.returncode == 0, result
    assert result.stdout.splitlines() == [
        "true",
        "copied",
        "false",
        "61",
        str(len(PAYLOAD)),
        str(len(PAYLOAD)),
        "true",
        str(len(PAYLOAD)),
        "true",
        "forwarded",
        'object { items: Range<Number>, caption: "forwarded',
        '" }',
        "9",
    ], result
    for directory in ("output", "branch"):
        assert (work / directory / "packs" / "file.bin").read_bytes() == PAYLOAD
    assert (work / "forwarded.txt").read_bytes() == b"forwarded\n"
    assert not (work / "unwritten").exists()
    assert "error[E0300]" in result.stderr and "<repl:5>:1:18" in result.stderr, result
    assert (
        "missing -> ./unwritten" in result.stderr and "does-not-exist" in result.stderr
    ), result
    assert (
        "<repl:7>:1:14" in result.stderr
        and "fn older() { $std::fs::canonical" in result.stderr
    ), result
    assert "source offset" not in result.stderr, result
    failure: list[Span] = [
        span
        for span in spans
        if span["name"] == "repl.submission"
        and span["metrics"].get("submission.index") == 5
    ]
    assert [
        (span["status"], span["metrics"].get("transfers.failed")) for span in failure
    ] == [("error", 1)]
    assert any(
        span["name"] == "hpir.effect.transfer"
        and span["status"] == "error"
        and span["attributes"].get("source.file") == "<repl:5>"
        for span in spans
    )
    assert any(
        span["name"] == "compiler.hpir.select.function"
        and span["metrics"].get("session.return.demands", 0)
        for span in spans
    )


def check_fifo(cli: list[str], work: Path) -> None:
    work.mkdir()
    os.mkfifo(work / "in-one")
    os.mkfifo(work / "in-two")
    os.mkfifo(work / "never-written")
    result, spans = submit(
        cli,
        work,
        [
            'let one = $std::io::read_text(./in-one); $std::io::write_text(./in-one, "same"); one',
            "let two = $std::io::read_text(./in-two);",
            '$std::io::write_text(./in-two, "later");',
            "two -> ./observed",
            'two = "ready";',
            "two -> ./ready",
            "let pending = $std::io::read_text(./never-written);",
            "/quit",
        ],
    )
    assert result.returncode == 0 and not result.stderr, result
    assert result.stdout == "same\ntrue\ntrue\n", result
    assert (work / "observed").read_text() == "later"
    assert (work / "ready").read_text() == "ready"
    assert any(
        span["name"] == "hpir.work.settle"
        and span["metrics"].get("work.session_file_reads") == 1
        for span in spans
    )


def check_processes(cli: list[str], work: Path) -> None:
    work.mkdir()
    retire: list[str] = []
    for _ in range(8):
        retire.extend(
            [
                'let child = release /bin/sh("-c", "printf child");',
                "$std::process::wait(child);",
            ]
        )
    retire.extend(
        [
            "let child = 0;",
            'fn pressure(n: Number) { $std::string::length($std::string::repeat("x", n)) };',
            *(["pressure(200000)"] * 8),
            "child",
        ]
    )
    result, spans = submit(cli, work, retire)
    assert (
        result.returncode == 0
        and not result.stderr
        and result.stdout.splitlines()[-1] == "0"
    ), result
    assert (
        sum(
            span["metrics"].get("gc.process_results_released", 0)
            for span in spans
            if span["name"] == "hpir.session.collect_between_submissions"
        )
        == 8
    )

    close: Path = work / "close"
    close.mkdir()
    result, spans = submit(
        cli,
        close,
        [
            "let one = release ./no-exe-one(); let two = release ./no-exe-two();",
            'let gate = release /bin/sleep("0.20"); $std::process::wait(gate);',
            "42",
            "/quit",
        ],
    )
    assert result.returncode == 1 and result.stdout == "42\n", result
    assert (
        result.stderr.count("error[E0300]") == 2
        and "Unhandled exception" not in result.stderr
    ), result
    assert "<repl:1>:1:19" in result.stderr and "<repl:1>:1:53" in result.stderr, result
    assert all(
        name in result.stderr
        for name in ("no-exe-one", "no-exe-two", "No such file or directory")
    ), result
    assert any(
        span["name"] == "repl.close"
        and span["status"] == "error"
        and span["attributes"].get("diagnostic.cause.0.source.file") == "<repl:1>"
        and span["attributes"].get("diagnostic.cause.1.source.offset") == "52"
        and sum(cause.get("raw_os_error") == 2 for cause in span["error_chain"]) >= 2
        for span in spans
    )

    displayed: Path = work / "displayed"
    displayed.mkdir()
    result, spans = submit(
        cli,
        displayed,
        [
            "let child = release ./no-exe();",
            'let gate = release /bin/sleep("0.20"); $std::process::wait(gate);',
            "child",
            "42",
        ],
    )
    assert result.returncode == 0 and result.stdout == "42\n", result
    assert (
        result.stderr.count("error[E0300]") == 1 and "<repl:1>:1:21" in result.stderr
    ), result
    assert any(
        span["name"] == "repl.close" and span["status"] == "ok" for span in spans
    )

    pending: Path = work / "deferred-result"
    pending.mkdir()
    result, _ = submit(
        cli,
        pending,
        [
            'let child = release /bin/sh("-c", "sleep 0.15; exit 7");',
            "fn code(keep: Bool) -> Number? { if keep { child::exit_code } else { null } };",
            "code(false) is null",
            "code(true)",
        ],
    )
    assert (
        result.returncode == 0 and not result.stderr and result.stdout == "true\n7\n"
    ), result


def check_ssh(cli: list[str], work: Path) -> None:
    sys.path.insert(0, str(ROOT / "tests" / "support"))
    from ssh_fixture import Fixture, lines

    with Fixture(work) as fixture:
        local: Path = fixture.root / "client-work"
        remote: Path = fixture.root / "remote"
        (local / "packs").mkdir(parents=True)
        (local / "packs" / "file.bin").write_bytes(PAYLOAD)
        remote.mkdir()
        (remote / "blocker").write_text("not a directory")
        start: int = len(lines(fixture.calls))
        result, spans = submit(
            cli,
            local,
            [
                'let dir: Path = "./packs";',
                "let endpoint = ssh://Perf_Fixture;",
                f"let target: Path = {json.dumps(str(remote) + '/')};",
                "dir -> endpoint @ target",
                f"let denied: Path = {json.dumps(str(remote / 'blocker' / 'denied') + '/')};",
                "dir -> endpoint @ denied",
                "42",
            ],
            env=fixture.environment(),
        )
        assert result.returncode == 0 and result.stdout == "true\nfalse\n42\n", result
        assert (remote / "packs" / "file.bin").read_bytes() == PAYLOAD
        assert (remote / "blocker").read_text() == "not a directory"
        assert lines(fixture.calls)[start:] == ["ssh", "ssh"], fixture.calls.read_text()
        assert (
            "<repl:6>:1:1" in result.stderr
            and "dir -> endpoint @ denied" in result.stderr
        ), result
        assert "blocker" in result.stderr and "Not a directory" in result.stderr, result
        assert any(
            span["name"] == "repl.submission"
            and span["status"] == "error"
            and span["metrics"].get("transfers.failed") == 1
            for span in spans
        )
        assert any(
            span["name"] == "hpir.effect.transfer"
            and any(
                "exit status 1" in str(cause.get("message"))
                and cause.get("raw_os_error") is None
                for cause in span["error_chain"]
            )
            for span in spans
        )


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--dotnet", type=Path, help="Host for a managed msync.dll")
    parser.add_argument(
        "--ssh", action="store_true", help="Also exercise a private real OpenSSH server"
    )
    args: argparse.Namespace = parser.parse_args()
    cli: list[str] = [str(args.binary.resolve(strict=True))]
    if args.dotnet:
        cli.insert(0, str(args.dotnet.resolve(strict=True)))
    base: Path = ROOT / ".temp"
    base.mkdir(exist_ok=True)
    work: Path = Path(tempfile.mkdtemp(dir=base, prefix="repl-boundary-"))
    print(f"REPL evidence: {work}", flush=True)
    for optimization in (0, 2):
        check_transfer(cli, work / f"o{optimization}", optimization)
    if sys.platform == "linux":
        check_fifo(cli, work / "fifo")
        check_processes(cli, work / "process")
    if args.ssh:
        check_ssh(cli, work / "ssh")
    print(
        "PASS: REPL transfer receipts, actual bytes, precise failure attribution and retained work"
    )


if __name__ == "__main__":
    main()
