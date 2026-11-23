"""Exercise one detached HPIR package on real Windows x64 and NTFS."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import platform
import sqlite3
import stat
import subprocess
from contextlib import closing
from ctypes import wintypes
from pathlib import Path

SOURCE: str = """\
let input: Path = "./readonly input.bin";
let target: Path = "./existing target.bin";
input -> target;
./empty-input.bin -> ./new-empty-target.bin;
"""
MAGIC: bytes = b"MSHPIR\0"
INPUT: bytes = b"live bytes after binding\x00\r\n"
OLD: bytes = b"prior destination"
SOURCE_STREAM: bytes = b"readonly source named stream"
OLD_STREAM: bytes = b"prior destination named stream"
MTIME: int = 1_700_000_000_123_456_700


def confirm_native_ntfs(work: Path) -> str:
    if os.name != "nt" or platform.machine().lower() not in ("amd64", "x86_64"):
        raise RuntimeError("The detached release gate requires native Windows x64")
    if getattr(ctypes.WinDLL("ntdll.dll"), "wine_get_version", None) is not None:
        raise RuntimeError("Wine cannot satisfy the native Windows NTFS gate")
    kernel: ctypes.WinDLL = ctypes.WinDLL("kernel32.dll", use_last_error=True)
    kernel.GetVolumePathNameW.argtypes = (
        wintypes.LPCWSTR,
        wintypes.LPWSTR,
        wintypes.DWORD,
    )
    kernel.GetVolumeInformationW.argtypes = (
        wintypes.LPCWSTR,
        wintypes.LPWSTR,
        wintypes.DWORD,
        wintypes.LPDWORD,
        wintypes.LPDWORD,
        wintypes.LPDWORD,
        wintypes.LPWSTR,
        wintypes.DWORD,
    )
    mount: ctypes.Array[ctypes.c_wchar] = ctypes.create_unicode_buffer(32768)
    fs: ctypes.Array[ctypes.c_wchar] = ctypes.create_unicode_buffer(64)
    if not kernel.GetVolumePathNameW(str(work), mount, len(mount)) or not (
        kernel.GetVolumeInformationW(mount, None, 0, None, None, None, fs, len(fs))
    ):
        raise ctypes.WinError(ctypes.get_last_error())
    if fs.value != "NTFS":
        raise RuntimeError(f"The selected scratch volume is {fs.value}, not NTFS")
    return mount.value


def stream(path: Path, name: str) -> Path:
    return path.with_name(f"{path.name}:{name}")


def invoke(
    exe: Path,
    cwd: Path,
    work: Path,
    title: str,
    arguments: tuple[str, ...],
    mode: str = "off",
) -> None:
    proc: subprocess.CompletedProcess[bytes] = subprocess.run(
        (str(exe), "--no-config", "--color", "never", *arguments),
        cwd=cwd,
        env={**os.environ, "MSYNC_JIT": mode},
        stdin=subprocess.DEVNULL,
        capture_output=True,
        check=False,
        timeout=60,
    )
    (work / f"{title}.stdout").write_bytes(proc.stdout)
    (work / f"{title}.stderr").write_bytes(proc.stderr)
    if proc.returncode or proc.stderr:
        raise RuntimeError(f"{title}: code={proc.returncode}, stderr={proc.stderr!r}")


def ordinary(path: Path) -> None:
    if path.exists():
        path.chmod(stat.S_IREAD | stat.S_IWRITE)


def seed_old(target: Path, alias: Path, empty_target: Path) -> None:
    ordinary(target)
    target.unlink(missing_ok=True)
    alias.unlink(missing_ok=True)
    empty_target.unlink(missing_ok=True)
    target.write_bytes(OLD)
    stream(target, "prior").write_bytes(OLD_STREAM)
    os.utime(target, ns=(MTIME + 100_000, MTIME + 100_000))
    os.link(target, alias)


def require_prior(target: Path, alias: Path, empty_target: Path) -> None:
    if target.read_bytes() != OLD or alias.read_bytes() != OLD or empty_target.exists():
        raise AssertionError("Compile, bind or dry run changed the public outputs")


def require_copy(source: Path, target: Path, alias: Path, empty_target: Path) -> None:
    if source.read_bytes() != INPUT or target.read_bytes() != INPUT:
        raise AssertionError(
            "The staged copy did not read the source bytes written after binding"
        )
    if stream(source, "native").read_bytes() != SOURCE_STREAM or (
        stream(target, "native").read_bytes() != SOURCE_STREAM
    ):
        raise AssertionError("The package lost the NTFS alternate data stream")
    for path in (source, target):
        info: os.stat_result = path.stat()
        if (
            info.st_mtime_ns != MTIME
            or not info.st_file_attributes & stat.FILE_ATTRIBUTE_READONLY
        ):
            raise AssertionError(f"Readonly metadata or timestamp changed: {path}")
    if empty_target.read_bytes():
        raise AssertionError("Zero-byte transfer published nonempty data")
    if (
        stream(target, "prior").exists()
        or alias.read_bytes() != OLD
        or alias.stat().st_mtime_ns != MTIME + 100_000
        or stream(alias, "prior").read_bytes() != OLD_STREAM
    ):
        raise AssertionError(
            "Replacing the output modified another hard link's data or metadata"
        )
    if any(source.parent.glob(".msync-*")):
        raise AssertionError("Successful execution left a private publication stage")


def machine_census(path: Path, mode: str) -> int:
    with closing(sqlite3.connect(path)) as con:
        status: list[tuple[str, str | None]] = con.execute(
            "SELECT status, exporter_error FROM sessions"
        ).fetchall()
        if status != [("complete", None)]:
            raise RuntimeError(f"Execution census is incomplete: {status}")
        instructions: int = con.execute(
            """SELECT COALESCE(SUM(m.value_int),0) FROM records r JOIN metrics m
               ON m.record_id=r.id WHERE r.name='vm.function' AND m.key='machine_instructions'"""
        ).fetchone()[0]
    if (instructions > 0) != (mode == "force"):
        raise AssertionError(
            f"{mode}: unexpectedly observed {instructions} machine instructions"
        )
    return instructions


def run_package(exe: Path, work: Path) -> dict[str, object]:
    bind: Path = work / "producer ü"
    run: Path = work / "unrelated launch"
    bind.mkdir()
    run.mkdir()
    script: Path = bind / "user.msync"
    portable: Path = bind / "user.mso"
    bound: Path = run / "user.mexe"
    source: Path = bind / "readonly input.bin"
    target: Path = bind / "existing target.bin"
    alias: Path = bind / "previous hard link.bin"
    empty_target: Path = bind / "new-empty-target.bin"
    script.write_bytes(SOURCE.encode())
    source.write_bytes(b"old bind-time bytes")
    stream(source, "native").write_bytes(SOURCE_STREAM)
    (bind / "empty-input.bin").write_bytes(b"")
    seed_old(target, alias, empty_target)
    invoke(
        exe,
        bind,
        work,
        "compile",
        ("--no-telemetry", "compile", "-O2", script.name, "-o", portable.name),
    )
    if not portable.read_bytes().startswith(MAGIC):
        raise AssertionError("Public source compile did not actually publish HPIR")
    script.unlink()
    invoke(
        exe,
        bind,
        work,
        "bind",
        ("--no-telemetry", "bind-ir", portable.name, "-o", str(bound)),
    )
    portable.unlink()
    image: bytes = bound.read_bytes()
    if not image.startswith(MAGIC):
        raise AssertionError("Public binder did not publish a bound HPIR image")
    require_prior(target, alias, empty_target)

    # Rewrite only the default stream; a truncating open also clears named NTFS streams.
    with source.open("r+b") as default_stream:
        default_stream.write(INPUT)
        default_stream.truncate()
    os.utime(source, ns=(MTIME, MTIME))
    source.chmod(stat.S_IREAD)
    try:
        invoke(
            exe,
            run,
            work,
            "dry-run",
            ("--no-telemetry", "run", "--dry-run", bound.name),
        )
        require_prior(target, alias, empty_target)
        results: dict[str, int] = {}
        for mode in ("off", "force"):
            seed_old(target, alias, empty_target)
            db: Path = work / f"{mode}.sqlite"
            invoke(
                exe,
                run,
                work,
                mode,
                (
                    "--telemetry-db",
                    str(db),
                    "--telemetry-level",
                    "profile",
                    "run",
                    bound.name,
                ),
                mode,
            )
            require_copy(source, target, alias, empty_target)
            results[mode] = machine_census(db, mode)
            if list(run.iterdir()) != [bound] or script.exists() or portable.exists():
                raise AssertionError(
                    "The detached run created an output at the launch directory or regenerated source/object"
                )
        return {
            "source_sha256": hashlib.sha256(SOURCE.encode()).hexdigest(),
            "bound_image_sha256": hashlib.sha256(image).hexdigest(),
            "machine_instructions": results,
        }
    finally:
        ordinary(source)
        ordinary(target)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--work",
        type=Path,
        required=True,
        help="new repository-local scratch directory",
    )
    parsed: argparse.Namespace = parser.parse_args()
    exe: Path = parsed.binary.resolve(strict=True)
    work: Path = parsed.work.resolve()
    work.mkdir(parents=True)
    volume: str = confirm_native_ntfs(work)
    evidence: dict[str, object] = {
        "windows": platform.platform(),
        "machine": platform.machine(),
        "ntfs_volume": volume,
        "package_binary_sha256": hashlib.sha256(exe.read_bytes()).hexdigest(),
        **run_package(exe, work),
    }
    receipt: str = json.dumps(evidence, indent=2) + "\n"
    (work / "result.json").write_text(receipt, encoding="utf-8")
    print(receipt, end="")


if __name__ == "__main__":
    main()
