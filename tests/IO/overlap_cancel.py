#!/usr/bin/env python3
"""Cancel an actual overlapping tree copy after retained input has copied bytes."""

from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def readonly_snapshot(binary: Path, root: Path) -> None:
    source = root / "readonly"
    locked = source / "locked"
    locked.mkdir(parents=True)
    (locked / "file").write_bytes(b"readonly input snapshot")
    locked.chmod(0o555)
    (root / "readonly.msync").write_text(
        "./readonly -> ./readonly/target/.;\n", encoding="utf-8"
    )
    try:
        result = subprocess.run(
            [str(binary), "--no-config", "run", "readonly.msync"],
            cwd=root,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        target = source / "target" / "locked"
        if (
            result.returncode != 0
            or (target / "file").read_bytes() != b"readonly input snapshot"
        ):
            raise AssertionError(
                f"Readonly snapshot publication failed: {result.stderr}"
            )
        if (
            locked.stat().st_mode & 0o777 != 0o555
            or target.stat().st_mode & 0o777 != 0o555
        ):
            raise AssertionError(
                "Snapshot cleanup changed source or published directory permissions"
            )
        if any(source.glob(".msync-*")):
            raise AssertionError("Readonly snapshot cleanup retained its owned stage")
    finally:
        locked.chmod(0o755)
        target = source / "target" / "locked"
        if target.exists():
            target.chmod(0o755)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if os.name != "posix":
        raise SystemExit(
            "The CLI signal fixture requires POSIX; native Windows progress cancellation is a separate probe"
        )
    binary: Path = args.binary.resolve()
    (ROOT / ".temp").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="overlap-cancel-", dir=ROOT / ".temp"
    ) as temporary:
        root = Path(temporary)
        readonly_snapshot(binary, root)
        source = root / "source"
        target = source / "target"
        target.mkdir(parents=True)
        (target / "previous").write_bytes(b"previous target\n")
        (source / ".msync-user-data").write_bytes(b"ordinary user data\n")
        with (source / "large.bin").open("wb") as large_file:
            large_file.write(b"L" * 65536)
            large_file.truncate(512 * 1024 * 1024)
        (root / "copy.msync").write_text(
            "./source -> ./source/target/.;\n", encoding="utf-8"
        )
        process = subprocess.Popen(
            [str(binary), "--no-config", "run", "copy.msync"],
            cwd=root,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        progress = 0
        try:
            deadline = time.monotonic() + 10
            while process.poll() is None and time.monotonic() < deadline:
                for stage in source.glob(".msync-*"):
                    file = stage / "payload" / "large.bin"
                    if not stage.is_dir() or not file.exists():
                        continue
                    with file.open("rb") as stream:
                        if stream.read(1) == b"L":
                            progress = file.stat().st_size
                            break
                if progress > 0:
                    break
                time.sleep(0.001)
            if progress == 0:
                raise AssertionError(
                    "No actual retained-input byte progress was observed before copy completed"
                )
            started = time.perf_counter_ns()
            process.send_signal(signal.SIGINT)
            stdout, stderr = process.communicate(timeout=10)
            elapsed = (time.perf_counter_ns() - started) / 1_000_000
            if process.returncode != 130:
                raise AssertionError(
                    f"Cancelled CLI exited {process.returncode}: {stdout!r}\n{stderr!r}"
                )
            if (target / "previous").read_bytes() != b"previous target\n" or (
                target / "large.bin"
            ).exists():
                raise AssertionError(
                    "Cancellation published over the previous tree before retaining input completed"
                )
            if any(stage.is_dir() for stage in source.glob(".msync-*")):
                raise AssertionError("Cancellation left an owned retained-input stage")
            if (source / ".msync-user-data").read_bytes() != b"ordinary user data\n":
                raise AssertionError(
                    "Live-owner cleanup changed similarly named user data"
                )
            report = {
                "retained_input_bytes_observed": progress,
                "cancel_to_exit_ms": elapsed,
                "exit": process.returncode,
                "stderr": stderr.decode("utf-8"),
            }
            if args.output is not None:
                output: Path = args.output
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            print(json.dumps(report, indent=2))
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()


if __name__ == "__main__":
    main()
