#!/usr/bin/env python3
"""Paired directory deploys from Windows, including a real Linux OpenSSH receiver."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import platform
import shlex
import subprocess
import sys
import tarfile
import tempfile
import uuid
from collections.abc import Iterator
from contextlib import contextmanager
from dataclasses import asdict
from decimal import Decimal
from pathlib import Path, PurePosixPath
from time import perf_counter_ns

from journeys import Image, freeze
from sync_journey import (
    EMPTY,
    SENTINEL,
    STAMP,
    File,
    generate,
    metadata,
    put,
    revise,
    verify,
)

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "packaging"), str(ROOT / "tests/Msync.EndToEnd")]
from wsl_auth_fixture import WslAuthFixture
from wsl_journey import download_rootfs

PROFILES = ("source-tree", "artifact-batch")
SEED = 17


class Peer:
    def __init__(self, ssh: Path, config: Path, root: str, label: str) -> None:
        self.ssh = ssh
        self.config = config
        self.root = PurePosixPath(root)
        self.label = label
        self.environment = dict(os.environ, MSYNC_SSH_CONFIG=str(config))
        self.environment["PATH"] = (
            str(ssh.parent) + os.pathsep + self.environment.get("PATH", "")
        )

    def shell(self, *words: str, source: bytes | None = None) -> bytes:
        return subprocess.run(
            [
                str(self.ssh),
                "-T",
                "-F",
                str(self.config),
                "Perf_Fixture",
                shlex.join(words),
            ],
            input=source,
            capture_output=True,
            timeout=120,
            check=True,
        ).stdout


@contextmanager
def owned_peer(work: Path, binary: Path, ssh: Path) -> Iterator[Peer]:
    distro = "msync-tree-" + uuid.uuid4().hex[:12]
    archive = work / "alpine.tar.gz"
    download_rootfs(archive)
    command = ["wsl.exe", "--distribution", distro, "--exec"]
    subprocess.run(
        [
            "wsl.exe",
            "--import",
            distro,
            str(work / "distro"),
            str(archive),
            "--version",
            "1",
        ],
        check=True,
        timeout=120,
    )
    try:
        subprocess.run(
            [*command, "apk", "add", "--no-cache", "coreutils", "tar", "openssh"],
            check=True,
            timeout=180,
        )
        with WslAuthFixture(distro, binary, "") as fixture:
            key = fixture.guest_run('cat "$1/host.pub"', fixture.guest).decode().strip()
            (fixture.root / "known_hosts").write_text(
                f"[127.0.0.1]:{fixture.port} {key}\n"
            )
            fixture.config.write_text(
                fixture.config.read_text()
                .replace("BatchMode no", "BatchMode yes")
                .replace("Checking ask", "Checking yes")
            )
            peer = Peer(
                ssh,
                fixture.config,
                fixture.guest + "/home",
                "windows-native-ssh-to-wsl1-loopback",
            )
            if peer.shell("printf", "ready") != b"ready":
                raise RuntimeError("The native SSH fallback fixture did not connect")
            yield peer
    finally:
        subprocess.run(["wsl.exe", "--unregister", distro], check=True, timeout=60)


def verify_peer(
    peer: Peer, base: PurePosixPath, files: dict[Path, File], marker: File | None
) -> None:
    payload = peer.shell(
        "tar", "--format=pax", "-cf", "-", "-C", str(base), "release-tree"
    )
    expected = files if marker is None else files | {SENTINEL: marker}
    observed: dict[Path, tuple[str, int, int]] = {}
    empty = False
    with tarfile.open(fileobj=io.BytesIO(payload), mode="r:") as tree:
        for item in tree:
            relative = Path(*PurePosixPath(item.name).relative_to("release-tree").parts)
            if item.isdir():
                empty |= relative == EMPTY
                continue
            if not item.isfile() or relative in observed:
                raise RuntimeError(f"Unexpected archive member {item.name}")
            stream = tree.extractfile(item)
            assert stream is not None
            with stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            stamp = int(Decimal(item.pax_headers.get("mtime", str(item.mtime))) * 10**9)
            observed[relative] = (digest, item.size, stamp)
    assert empty
    assert observed == {
        path: (file.sha256, file.bytes, file.mtime_ns)
        for path, file in expected.items()
    }


def round_trip(
    image: Image,
    work: Path,
    profile: str,
    peer: Peer | None,
    pair: int,
    trace: bool = False,
) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    with tempfile.TemporaryDirectory(prefix="d-", dir=work) as name:
        directory = Path(name)
        source = directory / "release-tree"
        target = directory / "target" / source.name
        identifier = "msync-tree-" + uuid.uuid4().hex
        remote = None if peer is None else peer.root / identifier
        script = "./release-tree -> ./target/;\n"
        if remote is not None and peer is not None:
            peer.shell("mkdir", "--", str(remote))
            script = f'let peer = ssh://Perf_Fixture; ./release-tree -> (peer @ "{str(remote).replace(chr(34), chr(34)*2)}/");\n'
        try:
            (directory / "deploy.msync").write_text(script, encoding="utf-8")
            files = generate(source, profile, SEED)
            marker: File | None = None
            for phase in ("first-deploy", "redeploy"):
                if phase == "redeploy":
                    marker_bytes = b"receiver-owned state\n"
                    put(directory, {}, SENTINEL, marker_bytes, 0o600, STAMP)
                    marker = metadata(directory / SENTINEL)
                    if remote is not None and peer is not None:
                        peer.shell(
                            "sh",
                            "-c",
                            'cat >"$1"; touch -d @1700000000.123456700 "$1"',
                            "sh",
                            str(remote / source.name / SENTINEL.name),
                            source=marker_bytes,
                        )
                    else:
                        put(target, {}, SENTINEL, marker_bytes, 0o600, STAMP)
                    revise(source, files, profile, SEED)
                lane = peer.label if peer is not None else "local"
                database = work / f"{lane}.{image.name}.{profile}.{phase}.sqlite"
                flags = (
                    [
                        "--telemetry-db",
                        str(database),
                        "--telemetry-level",
                        "diagnostics",
                    ]
                    if trace
                    else ["--no-telemetry"]
                )
                env = dict(os.environ) if peer is None else peer.environment.copy()
                env.pop("MSYNC_JIT", None)
                start = perf_counter_ns()
                run = subprocess.run(
                    [image.executable, "--no-config", *flags, "run", "deploy.msync"],
                    cwd=directory,
                    env=env,
                    capture_output=True,
                    timeout=120,
                    check=False,
                )
                elapsed = (perf_counter_ns() - start) / 1e6
                if run.returncode or run.stderr:
                    raise RuntimeError(
                        f"{phase}/{profile}/{image.name}: {run.returncode}: {run.stderr!r}"
                    )
                if peer is not None and remote is not None:
                    verify_peer(peer, remote, files, marker)
                else:
                    verify(target, files, marker)
                rows.append(
                    {
                        "lane": lane,
                        "image": image.name,
                        "phase": phase,
                        "profile": profile,
                        "pair": pair,
                        "elapsed_ms": elapsed,
                        "files": len(files),
                        "source_bytes": sum(f.bytes for f in files.values()),
                        "trace": database.name if trace else None,
                    }
                )
        finally:
            if peer is not None and remote is not None:
                peer.shell("rm", "-rf", "--", str(remote))
    return rows


def measure(images: list[Image], work: Path, peer: Peer | None, pairs: int) -> None:
    label = "local" if peer is None else peer.label
    with (work / f"{label}.jsonl").open("w", encoding="utf-8") as receipt:
        for profile in PROFILES:
            for pair in range(-1, pairs):
                for image in (images if pair % 2 == 0 else images[::-1]):
                    rows = round_trip(image, work, profile, peer, pair)
                    if pair >= 0:
                        for row in rows:
                            print(json.dumps(row), file=receipt, flush=True)
            for image in images:
                for row in round_trip(image, work, profile, peer, -2, trace=True):
                    print(json.dumps(row), file=receipt, flush=True)
            print(f"checked {label}/{profile}", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--pairs", type=int, default=7)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--wsl", action="store_true")
    modes.add_argument(
        "--ssh-config",
        type=Path,
        help="Known-host-pinned config with the Perf_Fixture alias",
    )
    parser.add_argument(
        "--remote-root", help="Operator-owned, writable, absolute POSIX scratch parent"
    )
    parser.add_argument("--remote-only", action="store_true")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("This benchmark requires a native Windows host")
    if (
        args.pairs < 1
        or bool(args.remote_root) != bool(args.ssh_config)
        or (args.remote_root and not args.remote_root.startswith("/"))
    ):
        parser.error(
            "Use positive pairs and supply an absolute --remote-root together with --ssh-config"
        )
    if args.remote_only and not (args.wsl or args.ssh_config):
        parser.error("--remote-only requires an SSH lane")
    work: Path = args.work.resolve()
    if not work.is_relative_to(ROOT / ".temp") or work == ROOT / ".temp":
        parser.error("--work must be a new directory inside .temp")
    work.mkdir(parents=True, exist_ok=False)
    images = [
        freeze(args.baseline.resolve(strict=True), "baseline", work),
        freeze(args.binary.resolve(strict=True), "current", work),
    ]
    (work / "environment.json").write_text(
        json.dumps(
            {
                "platform": platform.platform(),
                "cpu": os.cpu_count(),
                "seed": SEED,
                "pairs": args.pairs,
                "images": [asdict(i) for i in images],
            },
            indent=2,
        )
    )
    if not args.remote_only:
        measure(images, work, None, args.pairs)
    ssh = Path(os.environ["WINDIR"]) / "System32/OpenSSH/ssh.exe"
    if args.wsl:
        with owned_peer(work, args.binary.resolve(strict=True), ssh) as peer:
            measure(images, work, peer, args.pairs)
    if args.ssh_config:
        peer = Peer(
            ssh,
            args.ssh_config.resolve(strict=True),
            args.remote_root,
            "windows-native-ssh-to-external-linux",
        )
        measure(images, work, peer, args.pairs)


if __name__ == "__main__":
    main()
