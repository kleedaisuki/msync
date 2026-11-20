#!/usr/bin/env python3
"""Measure first deployment and small-edit redeployment as a linked journey."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import random
import statistics
import subprocess
import sys
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path
from time import perf_counter_ns
from typing import Literal, TypedDict

from journeys import Image, freeze

ROOT = Path(__file__).resolve().parents[2]
Transport = Literal["local", "ssh-loopback", "ssh-download"]
TRANSPORTS = ("local", "ssh-loopback", "ssh-download")
PROFILES = ("source-tree", "release-v1", "artifact-batch")
Phase = Literal["first-deploy", "redeploy"]
STAMP = 1_700_000_000_123_456_700
SENTINEL = Path("receiver-only.local")
EMPTY = Path("empty")
EDITS = (Path("settings.toml"), Path("modules/00/00.txt"), Path("modules/11/07.txt"))
ADDED = Path("modules/00/release-notes.txt")


@dataclass(frozen=True)
class File:
    sha256: str
    bytes: int
    mode: int
    mtime_ns: int


class Sample(TypedDict):
    image: str
    transport: Transport
    phase: Phase
    profile: str
    seed: int
    pair: int
    order: int
    elapsed_ms: float
    source_files: int
    source_bytes: int
    changed_files: int
    changed_bytes: int
    directories: int
    max_path_depth: int
    file_size_histogram: dict[str, int]
    ssh_clients: int
    ssh_authentications: int
    ssh_sessions: int


def metadata(path: Path) -> File:
    info = path.stat()
    with path.open("rb") as stream:
        return File(
            hashlib.file_digest(stream, "sha256").hexdigest(),
            info.st_size,
            info.st_mode & 0o777,
            info.st_mtime_ns,
        )


def put(
    root: Path,
    files: dict[Path, File],
    relative: Path,
    body: bytes,
    mode: int,
    stamp: int,
) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(body)
    path.chmod(mode)
    os.utime(path, ns=(stamp, stamp))
    files[relative] = metadata(path)


def generate(root: Path, profile: str, seed: int) -> dict[Path, File]:
    rng = random.Random(seed)
    files: dict[Path, File] = {}
    (root / EMPTY).mkdir(parents=True)
    for directory in range(12):
        for entry in range(8):
            line = f"module {directory:02d} / line {entry:02d}\n".encode()
            body = line * rng.randrange(24, 500)
            put(
                root,
                files,
                Path(f"modules/{directory:02d}/{entry:02d}.txt"),
                body,
                0o644,
                STAMP,
            )
    put(root, files, Path("settings.toml"), b'version = "1"\n', 0o640, STAMP)
    put(root, files, Path("bin/start"), b"#!/bin/sh\nexit 0\n", 0o755, STAMP)
    put(root, files, Path("assets/index.bin"), rng.randbytes(256 * 1024), 0o644, STAMP)
    put(
        root,
        files,
        Path("assets/bundle.bin"),
        rng.randbytes(4 * 1024 * 1024),
        0o644,
        STAMP,
    )
    if profile == "source-tree":
        # More directory/entry work without growing payload bytes proportionally.
        for group in range(24):
            for entry in range(rng.randrange(7, 17)):
                name = Path(
                    f"src/area-{group // 4:02d}/模块-{group:02d}/file-{entry:02d}.txt"
                )
                size = rng.choice((0, 96, 512, 1536, 4096, 12_288))
                put(root, files, name, rng.randbytes(size), 0o640, STAMP)
    if profile == "artifact-batch":
        for filename, mib in (
            ("worker-x64", 6),
            ("worker-arm64", 11),
            ("data-pack", 24),
        ):
            put(
                root,
                files,
                Path("assets", filename),
                rng.randbytes(mib * 1024**2),
                0o644,
                STAMP,
            )
    return files


def histogram(files: dict[Path, File]) -> dict[str, int]:
    bands = {"empty": 0, "1B-4KiB": 0, ">4KiB-1MiB": 0, ">1MiB": 0}
    for file in files.values():
        size = file.bytes
        band = (
            "empty"
            if size == 0
            else "1B-4KiB"
            if size <= 4096
            else ">4KiB-1MiB"
            if size <= 1024**2
            else ">1MiB"
        )
        bands[band] += 1
    return bands


def revise(
    root: Path, files: dict[Path, File], profile: str, seed: int
) -> tuple[Path, ...]:
    edits: tuple[Path, ...] = EDITS
    rebuilt: tuple[Path, ...] = ()
    stamp = STAMP + 2_000_000_000
    if profile == "source-tree":
        deep = [path for path in files if path.parts[0] == "src" and files[path].bytes]
        edits = tuple(deep[:: max(1, len(deep) // 16)])
    elif profile == "artifact-batch":
        artifact = Path("assets/data-pack")
        old = files[artifact]
        payload = random.Random(f"{seed}:redeploy").randbytes(old.bytes)
        put(root, files, artifact, payload, old.mode, stamp)
        rebuilt = (artifact,)
        edits = (Path("settings.toml"),)
    for relative in edits:
        path = root / relative
        with path.open("r+b") as stream:
            byte = stream.read(1)
            stream.seek(0)
            stream.write(b"!" if profile == "release-v1" else bytes([byte[0] ^ 1]))
        os.utime(path, ns=(stamp, stamp))
        files[relative] = metadata(path)
    put(root, files, ADDED, b"Second release\n", 0o644, stamp)
    return (*edits, *rebuilt, ADDED)


def verify(target: Path, expected: dict[Path, File], marker: File | None) -> None:
    files = expected if marker is None else expected | {SENTINEL: marker}
    actual = {path.relative_to(target) for path in target.rglob("*") if path.is_file()}
    if actual != files.keys():
        raise RuntimeError(
            f"Deployment file set differs: missing={files.keys() - actual}, extra={actual - files.keys()}"
        )
    if not (target / EMPTY).is_dir():
        raise RuntimeError("Deployment lost the empty directory")
    for relative, expected_file in files.items():
        observed = metadata(target / relative)
        if observed != expected_file:
            raise RuntimeError(
                f"{relative}: expected {expected_file}, observed {observed}"
            )


@dataclass(frozen=True)
class Connection:
    transport: Transport
    work: Path
    environment: dict[str, str]
    calls: Path | None = None
    server_log: Path | None = None

    def program(self, source: Path, destination: Path) -> str:
        if self.transport == "local":
            return "./release-tree -> ./target/;\n"
        if self.transport == "ssh-download":
            path = source.as_posix().replace('"', '""')
            return f'let peer = ssh://Perf_Fixture; (peer @ "{path}") -> ./target/;\n'
        path = destination.as_posix().replace('"', '""')
        return f'let peer = ssh://Perf_Fixture; ./release-tree -> (peer @ "{path}/");\n'

    def counts(self) -> tuple[int, int, int]:
        calls = (
            self.calls.read_text().splitlines()
            if self.calls and self.calls.exists()
            else []
        )
        log = (
            self.server_log.read_text(errors="replace").splitlines()
            if self.server_log
            else []
        )
        return (
            calls.count("ssh"),
            sum("Accepted publickey for " in line for line in log),
            sum("Starting session:" in line for line in log),
        )


def deploy(
    image: Image, connection: Connection, directory: Path
) -> tuple[float, tuple[int, int, int]]:
    before = connection.counts()
    command = [image.executable, "--no-config", "--no-telemetry", "run", "deploy.msync"]
    start = perf_counter_ns()
    completed = subprocess.run(
        command,
        cwd=directory,
        env=connection.environment,
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )
    elapsed = (perf_counter_ns() - start) / 1_000_000
    if completed.returncode != 0:
        raise RuntimeError(
            f"{command!r} in {directory} exited {completed.returncode}:\n"
            f"{completed.stdout}\n{completed.stderr}"
        )
    clients, auth, sessions = connection.counts()
    return elapsed, (clients - before[0], auth - before[1], sessions - before[2])


def run_round(
    image: Image,
    connection: Connection,
    profile: str,
    seed: int,
    pair: int,
    order: int,
    samples: list[Sample],
) -> None:
    with tempfile.TemporaryDirectory(
        prefix="deployment-", dir=connection.work
    ) as scratch:
        directory = Path(scratch)
        origin = (
            directory / "peer" if connection.transport == "ssh-download" else directory
        )
        source = origin / "release-tree"
        destination = directory / "target"
        target = destination / "release-tree"
        (directory / "deploy.msync").write_text(
            connection.program(source, destination), encoding="utf-8"
        )
        expected = generate(source, profile, seed)
        changed = tuple(expected)
        marker: File | None = None
        phases: tuple[Phase, ...] = ("first-deploy", "redeploy")
        for phase in phases:
            if phase == "redeploy":
                put(target, {}, SENTINEL, b"receiver-owned state\n", 0o600, STAMP)
                marker = metadata(target / SENTINEL)
                changed = revise(source, expected, profile, seed)
            elapsed, counts = deploy(image, connection, directory)
            verify(target, expected, marker)
            if pair >= 0:
                samples.append(
                    Sample(
                        image=image.name,
                        transport=connection.transport,
                        phase=phase,
                        profile=profile,
                        seed=seed,
                        pair=pair,
                        order=order,
                        elapsed_ms=elapsed,
                        source_files=len(expected),
                        source_bytes=sum(file.bytes for file in expected.values()),
                        changed_files=len(changed),
                        changed_bytes=sum(expected[path].bytes for path in changed),
                        directories=sum(path.is_dir() for path in source.rglob("*")),
                        max_path_depth=max(len(path.parts) for path in expected),
                        file_size_histogram=histogram(expected),
                        ssh_clients=counts[0],
                        ssh_authentications=counts[1],
                        ssh_sessions=counts[2],
                    )
                )


def measure(
    images: list[Image],
    connection: Connection,
    profiles: list[str],
    seeds: list[int],
    count: int,
    warmups: int,
    samples: list[Sample],
) -> None:
    for profile in profiles:
        for seed in seeds:
            print(
                f"RUN {connection.transport}/{profile}/{seed} first-deploy + redeploy",
                flush=True,
            )
            for pair in range(-warmups, count):
                order = images if pair % 2 == 0 else images[::-1]
                for index, image in enumerate(order):
                    run_round(image, connection, profile, seed, pair, index, samples)


def summarize(samples: list[Sample]) -> list[dict[str, object]]:
    summary: list[dict[str, object]] = []
    for transport, profile, seed, phase in dict.fromkeys(
        (row["transport"], row["profile"], row["seed"], row["phase"]) for row in samples
    ):
        matching = [
            row
            for row in samples
            if (row["transport"], row["profile"], row["seed"], row["phase"])
            == (transport, profile, seed, phase)
        ]
        milliseconds = {
            image: [row["elapsed_ms"] for row in matching if row["image"] == image]
            for image in dict.fromkeys(row["image"] for row in matching)
        }
        line: dict[str, object] = {
            "transport": transport,
            "phase": phase,
            "profile": profile,
            "seed": seed,
            "p50_ms": {
                image: statistics.median(values)
                for image, values in milliseconds.items()
            },
        }
        if "baseline" in milliseconds and "current" in milliseconds:
            line["paired_median_percent_change"] = statistics.median(
                (current / baseline - 1.0) * 100
                for baseline, current in zip(
                    milliseconds["baseline"], milliseconds["current"], strict=True
                )
            )
        summary.append(line)
    return summary


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--transport", choices=TRANSPORTS, action="append")
    parser.add_argument("--profile", choices=PROFILES, action="append")
    parser.add_argument("--seed", type=int, action="append")
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--work", type=Path)
    parser.add_argument("--samples", type=int, default=7)
    options = parser.parse_args()
    if options.samples < 1 or options.warmups < 0:
        parser.error("Expected positive --samples and nonnegative --warmups")
    transports: list[Transport] = list(dict.fromkeys(options.transport or ["local"]))
    parent = ROOT / ".temp" / "benchmarks"
    parent.mkdir(parents=True, exist_ok=True)
    work: Path = options.work or Path(
        tempfile.mkdtemp(prefix="sync-journey-", dir=parent)
    )
    work = work.resolve()
    if work == parent.parent or not work.is_relative_to(parent.parent):
        parser.error("--work must be a child of repository-root .temp")
    if work.exists() and (not work.is_dir() or any(work.iterdir())):
        parser.error("--work must be new or empty")
    work.mkdir(parents=True, exist_ok=True)
    profiles: list[str] = list(dict.fromkeys(options.profile or ["release-v1"]))
    seeds: list[int] = list(dict.fromkeys(options.seed or [17]))
    receipt = work / "result.json"
    print(receipt, flush=True)
    environment = os.environ | {"NO_COLOR": "1"}
    samples: list[Sample] = []
    report: dict[str, object] = {
        "schema": 2,
        "status": "running",
        "host": platform.platform(),
        "samples": samples,
        "measurement": "Source CLI wall time, telemetry off; fixture setup and verification excluded",
        "profiles": profiles,
        "seeds": seeds,
        "jit": os.environ.get("MSYNC_JIT", "auto"),
        "rounds": options.samples,
        "warmups": options.warmups,
        "transport": transports,
        "source_selection": "whole tree, including unchanged files",
    }
    try:
        images: list[Image] = []
        if options.baseline is not None:
            images.append(
                freeze(options.baseline.resolve(strict=True), "baseline", work)
            )
        images.append(freeze(options.binary.resolve(strict=True), "current", work))
        report["images"] = [asdict(image) for image in images]
        for transport in transports:
            if transport == "local":
                measure(
                    images,
                    Connection(transport, work, environment),
                    profiles,
                    seeds,
                    options.samples,
                    options.warmups,
                    samples,
                )
            else:
                if not sys.platform.startswith("linux"):
                    raise ValueError("ssh-loopback uses the Linux OpenSSH fixture")
                sys.path.insert(0, str(ROOT / "tests" / "support"))
                from ssh_fixture import Fixture

                with Fixture(work) as fixture:
                    measure(
                        images,
                        Connection(
                            transport,
                            fixture.root,
                            fixture.environment(),
                            fixture.calls,
                            fixture.log,
                        ),
                        profiles,
                        seeds,
                        options.samples,
                        options.warmups,
                        samples,
                    )
        report["summary"] = summarize(samples)
        report["status"] = "complete"
    except (Exception, KeyboardInterrupt) as error:
        report["status"] = "failed"
        report["error"] = str(error) or type(error).__name__
        raise
    finally:
        receipt.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
