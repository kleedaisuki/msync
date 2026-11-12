"""Verify the selected invalid Heap index after a genuinely warmed writer."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
from pathlib import Path

from journeys import HERE, Profile, environment, profile
from whole_method import Host

SOURCE: Path = HERE / "method-heap-index.msync"
EXPECTED: bytes = (
    b"error: storage.vector_get index 99 is outside the items of storage with 3 items."
)


def prepare(producer: Path, directory: Path) -> Path:
    directory.mkdir(parents=True, exist_ok=False)
    source: Path = directory / "program.msync"
    source.write_bytes(SOURCE.read_bytes())
    host = Host(producer)
    host.invoke(
        directory,
        ("--no-telemetry", "compile", "-O2", source.name, "-o", "program.mso"),
    )
    source.unlink()
    host.invoke(
        directory,
        ("--no-telemetry", "bind-ir", "program.mso", "-o", "program.mexe"),
    )
    (directory / "program.mso").unlink()
    return directory / "program.mexe"


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--work", type=Path, required=True)
    inputs = parser.add_mutually_exclusive_group()
    inputs.add_argument("--producer", type=Path)
    inputs.add_argument("--artifact", type=Path)
    parser.add_argument("--expect-native", action="store_true")
    args: argparse.Namespace = parser.parse_args()
    work: Path = args.work.resolve()
    binary: Path = args.binary.resolve(strict=True)
    directory: Path = work / "bound"
    directory.mkdir(parents=True, exist_ok=False)
    artifact: Path = directory / "program.mexe"
    if args.artifact:
        shutil.copyfile(args.artifact.resolve(strict=True), artifact)
    else:
        shutil.copyfile(
            prepare(
                args.producer.resolve(strict=True) if args.producer else binary,
                work / "producer",
            ),
            artifact,
        )

    diagnostics: list[bytes] = []
    profiles: dict[str, Profile] = {}
    for mode, native in (("auto", True), ("off", False)):
        db: str = f"{mode}.sqlite"
        proc: subprocess.CompletedProcess[bytes] = subprocess.run(
            [
                str(binary),
                "--no-config",
                "--telemetry-db",
                db,
                "--telemetry-level",
                "profile",
                "run",
                artifact.name,
            ],
            cwd=directory,
            input=b"99\n",
            capture_output=True,
            env=environment(native),
            timeout=30,
            check=False,
        )
        if proc.returncode != 1 or proc.stdout != b"selected\n":
            raise AssertionError((mode, proc.returncode, proc.stdout, proc.stderr))
        if not proc.stderr.startswith(EXPECTED + b"\n"):
            raise AssertionError((mode, proc.stderr))
        diagnostics.append(proc.stderr)
        data: Profile = profile(directory / db, "profile")
        if data.status == "ok":
            raise AssertionError((mode, "selected failure recorded as success"))
        if data.metrics.get("native_worker_unmapped_entries", 0):
            raise AssertionError((mode, "missing original logical replay"))
        profiles[mode] = data

    if diagnostics[0] != diagnostics[1]:
        raise AssertionError(
            "JIT changed the selected canonical error or original source attribution"
        )
    auto, off = profiles["auto"], profiles["off"]
    auto.check_logical(off, "profile")
    if off.metrics.get("native_worker_entries", 0):
        raise AssertionError("JIT off entered worker")
    if args.expect_native and not all(
        auto.metrics.get(key, 0) > 0
        for key in (
            "native_worker_entries",
            "native_worker_guard_misses",
            "native_worker_stores",
        )
    ):
        raise AssertionError(("no warmed writer or selected guard miss", auto.metrics))

    receipt: dict[str, object] = {
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "artifact_sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(),
        "diagnostic": diagnostics[0].decode(),
        "profile": {mode: item.metrics for mode, item in profiles.items()},
        "logical_instructions": sum(auto.opcodes.values()),
        "native_calls": auto.native_calls,
    }
    (work / "result.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(
        "PASS warmed Heap invalid index: original diagnostic, no extra selected effects"
    )


if __name__ == "__main__":
    main()
