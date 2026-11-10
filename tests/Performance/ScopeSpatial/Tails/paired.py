#!/usr/bin/env python3
"""Alternate checked execute batches in two persistent frozen-assembly hosts."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import platform
import queue
import subprocess
import sys
import threading
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import TextIO, cast

ROOT: Path = Path(__file__).resolve().parents[4]


def utc() -> str:
    return datetime.now(UTC).isoformat()


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load() -> list[float] | None:
    return list(os.getloadavg()) if hasattr(os, "getloadavg") else None


def pump(stream: TextIO, log: TextIO, lines: queue.Queue[str | None]) -> None:
    try:
        for line in stream:
            log.write(line)
            log.flush()
            lines.put(line.rstrip("\n"))
    finally:
        lines.put(None)


@dataclass
class Host:
    label: str
    process: subprocess.Popen[str]
    lines: queue.Queue[str | None]
    stdout: TextIO
    stderr: TextIO
    reader: threading.Thread
    report: Path
    command: list[str]

    def expect(self, expected: str) -> None:
        line: str | None = self.lines.get(timeout=180)
        if line != expected:
            raise RuntimeError(f"{self.label}: expected {expected!r}, got {line!r}")

    def start_batch(self) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write("RUN\n")
        self.process.stdin.flush()

    def finish(self) -> None:
        returncode: int = self.process.wait(timeout=180)
        self.reader.join(timeout=10)
        self.stdout.close()
        self.stderr.close()
        if returncode:
            raise RuntimeError(
                f"{self.label}: exit {returncode}; inspect its stderr log"
            )


def start_host(
    label: str, binary: Path, output: Path, artifact: Path, args: argparse.Namespace
) -> Host:
    report: Path = output / f"{label}.json"
    command: list[str] = [
        str(binary.resolve()),
        "--case",
        args.case,
        "--rows",
        str(args.rows),
        "--passes",
        str(args.passes),
        "--warmups",
        str(args.warmups),
        "--samples",
        str(args.samples),
        "--batch-size",
        str(args.batch_size),
        "--gate",
        "true",
        "--root",
        str(output / "work"),
        "--output",
        str(report),
        "--input-artifact",
        str(artifact),
    ]
    stdout: TextIO = (output / f"{label}-stdout.log").open("w", encoding="utf-8")
    stderr: TextIO = (output / f"{label}-stderr.log").open("w", encoding="utf-8")
    process: subprocess.Popen[str] = subprocess.Popen(
        command,
        cwd=ROOT,
        env=os.environ | {"DOTNET_TieredCompilation": "0", "NO_COLOR": "1"},
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=stderr,
        text=True,
        encoding="utf-8",
        bufsize=1,
    )
    assert process.stdout is not None
    lines: queue.Queue[str | None] = queue.Queue()
    reader: threading.Thread = threading.Thread(
        target=pump, args=(process.stdout, stdout, lines), daemon=True
    )
    reader.start()
    return Host(label, process, lines, stdout, stderr, reader, report, command)


def prepare_artifact(
    binary: Path, output: Path, args: argparse.Namespace
) -> tuple[Path, dict[str, object]]:
    artifact: Path = output / "workload.mexe"
    command: list[str] = [
        str(binary.resolve()),
        "--case",
        args.case,
        "--rows",
        str(args.rows),
        "--passes",
        str(args.passes),
        "--warmups",
        "0",
        "--samples",
        "0",
        "--root",
        str(output / "work"),
        "--output",
        str(output / "prepare.json"),
        "--emit-artifact",
        str(artifact),
    ]
    result: subprocess.CompletedProcess[str] = subprocess.run(
        command,
        cwd=ROOT,
        env=os.environ | {"DOTNET_TieredCompilation": "0", "NO_COLOR": "1"},
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=180,
    )
    (output / "prepare-stdout.log").write_text(result.stdout, encoding="utf-8")
    (output / "prepare-stderr.log").write_text(result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(
            f"Artifact preparation exited {result.returncode}; inspect its stderr log"
        )
    return artifact, {"command": command, "sha256": digest(artifact)}


def summarize(report: dict[str, object]) -> dict[str, object]:
    samples: list[dict[str, object]] = cast(list[dict[str, object]], report["Samples"])
    durations: list[float] = sorted(
        float(cast(float, sample["Milliseconds"])) for sample in samples
    )
    with_gc: list[float] = [
        float(cast(float, sample["Milliseconds"]))
        for sample in samples
        if any(
            int(cast(int, sample[key]))
            for key in ("Gen0Collections", "Gen1Collections", "Gen2Collections")
        )
    ]
    without_gc: list[float] = [
        float(cast(float, sample["Milliseconds"]))
        for sample in samples
        if not any(
            int(cast(int, sample[key]))
            for key in ("Gen0Collections", "Gen1Collections", "Gen2Collections")
        )
    ]
    return {
        "samples": len(samples),
        "p50_ms": durations[math.ceil(len(durations) * 0.50) - 1],
        "p95_ms": durations[math.ceil(len(durations) * 0.95) - 1],
        "p99_ms": durations[math.ceil(len(durations) * 0.99) - 1],
        "max_ms": durations[-1],
        "total_execution_ms": sum(durations),
        "mean_allocated_bytes": int(cast(int, report["TotalAllocatedBytes"]))
        / len(samples),
        "gc_collections": {
            key: sum(int(cast(int, sample[key])) for sample in samples)
            for key in ("Gen0Collections", "Gen1Collections", "Gen2Collections")
        },
        "samples_with_gc": len(with_gc),
        "samples_without_gc": len(without_gc),
        "max_with_gc_ms": max(with_gc, default=None),
        "max_without_gc_ms": max(without_gc, default=None),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-host", type=Path, required=True)
    parser.add_argument("--candidate-host", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--case", choices=("references", "computed", "manifest"), default="references"
    )
    parser.add_argument("--rows", type=int, default=8192)
    parser.add_argument("--passes", type=int, default=4)
    parser.add_argument("--samples", type=int, default=1000)
    parser.add_argument("--batch-size", type=int, default=32)
    parser.add_argument("--warmups", type=int, default=5)
    args: argparse.Namespace = parser.parse_args()
    output: Path = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    blocks: list[dict[str, object]] = []
    hosts: dict[str, Host] = {}
    manifest: dict[str, object] = {
        "status": "running",
        "started_utc": utc(),
        "platform": platform.platform(),
        "python": sys.executable,
        "python_version": sys.version,
        "runner_sha256": digest(Path(__file__)),
        "method": "Two persistent managed hosts; compile/bind/warm once; alternating AB/BA batches. Nearest-rank percentiles; all outputs bit-checked outside timer. GC counts cover collections, not pause duration.",
        "case": args.case,
        "rows": args.rows,
        "passes": args.passes,
        "samples_per_host": args.samples,
        "batch_size": args.batch_size,
        "blocks": blocks,
    }
    try:
        artifact: Path
        artifact_receipt: dict[str, object]
        artifact, artifact_receipt = prepare_artifact(args.baseline_host, output, args)
        manifest["artifact"] = artifact_receipt
        hosts = {
            "baseline": start_host(
                "baseline", args.baseline_host, output, artifact, args
            ),
            "candidate": start_host(
                "candidate", args.candidate_host, output, artifact, args
            ),
        }
        manifest["hosts"] = {
            label: {
                "command": host.command,
                "executable_sha256": digest(Path(host.command[0])),
            }
            for label, host in hosts.items()
        }
        ready: dict[str, str] = {}
        for label, host in hosts.items():
            host.expect("READY 0")
            ready[label] = utc()
        manifest["observed_ready_utc"] = ready
        for block, start in enumerate(range(0, args.samples, args.batch_size)):
            order: tuple[str, str] = (
                ("baseline", "candidate")
                if block % 2 == 0
                else ("candidate", "baseline")
            )
            end: int = min(start + args.batch_size, args.samples)
            observations: list[dict[str, object]] = []
            for label in order:
                host = hosts[label]
                if start != 0:
                    host.expect(f"READY {start}")
                before: str = utc()
                load_before: list[float] | None = load()
                host.start_batch()
                host.expect(f"BATCH {end}")
                observations.append(
                    {
                        "image": label,
                        "started_utc": before,
                        "ended_utc": utc(),
                        "host_load_before": load_before,
                        "host_load_after": load(),
                    }
                )
            blocks.append(
                {
                    "block": block,
                    "start": start,
                    "end": end,
                    "order": list(order),
                    "observations": observations,
                }
            )
            (output / "paired.json").write_text(
                json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
            )
        for host in hosts.values():
            host.finish()
        reports: dict[str, dict[str, object]] = {
            label: cast(
                dict[str, object], json.loads(host.report.read_text(encoding="utf-8"))
            )
            for label, host in hosts.items()
        }
        assert (
            reports["baseline"]["SourceSha256"] == reports["candidate"]["SourceSha256"]
        ), "Source inputs differ"
        assert (
            reports["baseline"]["ArtifactSha256"]
            == reports["candidate"]["ArtifactSha256"]
            == digest(artifact)
        ), "Bound bytecode differs"
        manifest["hosts"] = {
            label: {
                "command": host.command,
                "executable_sha256": digest(Path(host.command[0])),
                "report_sha256": digest(host.report),
                "assemblies": reports[label]["Assemblies"],
            }
            for label, host in hosts.items()
        }
        manifest["summaries"] = {
            label: summarize(report) for label, report in reports.items()
        }
        manifest["status"] = "complete"
    except BaseException as error:
        manifest["status"] = "failed"
        manifest["error"] = repr(error)
        raise
    finally:
        manifest["ended_utc"] = utc()
        (output / "paired.json").write_text(
            json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
        )
        for host in hosts.values():
            if host.process.poll() is None:
                host.process.terminate()
                host.process.wait(timeout=10)
            host.stdout.close()
            host.stderr.close()
    print(json.dumps(manifest["summaries"], indent=2))


if __name__ == "__main__":
    main()
