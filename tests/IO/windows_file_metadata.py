#!/usr/bin/env python3
"""Exercise Windows native copy metadata and ownership through the compiled CLI."""

from __future__ import annotations

import argparse
import json
import os
import stat
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def invoke(binary: Path, arguments: Sequence[str], cwd: Path) -> None:
    result = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=cwd,
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"{arguments}: exit {result.returncode}\n{result.stdout}\n{result.stderr}"
        )


def writable(path: Path) -> None:
    if path.exists():
        path.chmod(stat.S_IREAD | stat.S_IWRITE)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if os.name != "nt":
        raise SystemExit(
            "This fixture requires actual Windows named streams and readonly attributes"
        )
    binary: Path = args.binary.resolve()
    (ROOT / ".temp").mkdir(exist_ok=True)
    results: dict[str, dict[str, int | bool | str]] = {}
    with tempfile.TemporaryDirectory(
        prefix="windows-file-metadata-", dir=ROOT / ".temp"
    ) as temporary:
        root = Path(temporary)
        source = root / "source file.bin"
        target = root / "target file.bin"
        source.write_bytes(b"native payload\n")
        Path(str(source) + ":msync.probe").write_bytes(b"native named stream\n")
        source_time = 1_700_000_000_123_456_700
        os.utime(source, ns=(source_time, source_time))
        source.chmod(stat.S_IREAD)
        (root / "copy.msync").write_text(
            'let input: Path = "./source file.bin"; let output: Path = "./target file.bin"; input -> output;\n',
            encoding="utf-8",
        )
        invoke(binary, ["compile", "copy.msync", "-o", "copy.mso"], root)
        invoke(binary, ["bind-ir", "copy.mso", "-o", "copy.mexe"], root)
        try:
            for stage in ("msync", "mso", "mexe"):
                for overwrite in (False, True):
                    alias = root / "unrelated-destination-link.bin"
                    if overwrite:
                        target.write_bytes(b"previous destination\n")
                        Path(str(target) + ":old-stream").write_bytes(b"old stream\n")
                        os.utime(
                            target, ns=(source_time + 100_000, source_time + 100_000)
                        )
                        os.link(target, alias)
                    invoke(binary, ["run", f"copy.{stage}"], root)
                    if (
                        target.read_bytes() != b"native payload\n"
                        or Path(str(target) + ":msync.probe").read_bytes()
                        != b"native named stream\n"
                    ):
                        raise AssertionError(
                            f"{stage}: native payload or named stream was lost"
                        )
                    if (
                        target.stat().st_mtime_ns != source_time
                        or not target.stat().st_file_attributes
                        & stat.FILE_ATTRIBUTE_READONLY
                    ):
                        raise AssertionError(
                            f"{stage}: readonly source metadata was changed or rejected"
                        )
                    if (
                        source.stat().st_mtime_ns != source_time
                        or not source.stat().st_file_attributes
                        & stat.FILE_ATTRIBUTE_READONLY
                    ):
                        raise AssertionError(f"{stage}: source metadata was modified")
                    if overwrite:
                        if (
                            alias.read_bytes() != b"previous destination\n"
                            or alias.stat().st_mtime_ns != source_time + 100_000
                        ):
                            raise AssertionError(
                                f"{stage}: replacement modified unrelated hard-link data or metadata"
                            )
                        if (
                            alias.stat().st_file_attributes
                            & stat.FILE_ATTRIBUTE_READONLY
                        ):
                            raise AssertionError(
                                f"{stage}: replacement changed unrelated hard-link readonly state"
                            )
                        if Path(str(target) + ":old-stream").exists():
                            raise AssertionError(
                                f"{stage}: replacement retained an unrelated old named stream"
                            )
                        alias.unlink()
                    if any(root.glob(".msync-*")):
                        raise AssertionError(
                            f"{stage}: successful native copy left a stage"
                        )
                    results[f"{stage}-{'overwrite' if overwrite else 'fresh'}"] = {
                        "mtime_ns": target.stat().st_mtime_ns,
                        "readonly": True,
                        "named_stream": True,
                    }
                    writable(target)
                    target.unlink()
            link = root / "source-link"
            try:
                link.symlink_to(source.name)
            except OSError as error:
                if getattr(error, "winerror", None) != 1314:
                    raise
                results["symlink"] = {"available": False, "reason": str(error)}
            else:
                link_time = source_time + 200_000
                os.utime(link, ns=(link_time, link_time), follow_symlinks=False)
                (root / "link.msync").write_text(
                    "./source-link -> ./copied-link;\n", encoding="utf-8"
                )
                invoke(binary, ["run", "link.msync"], root)
                copied = root / "copied-link"
                if not copied.is_symlink() or os.readlink(copied) != source.name:
                    raise AssertionError("Native link copy changed link spelling")
                if (
                    copied.lstat().st_mtime_ns != link_time
                    or source.stat().st_mtime_ns != source_time
                ):
                    raise AssertionError(
                        "Native link timestamp was lost or changed the linked target"
                    )
                results["symlink"] = {
                    "available": True,
                    "mtime_ns": copied.lstat().st_mtime_ns,
                }
                copied.unlink()
                link.unlink()
        finally:
            writable(source)
            writable(target)
    if args.output is not None:
        output: Path = args.output
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print(
        "Windows source/object/executable copies preserved readonly sources, named streams, timestamps and unrelated hard-link ownership."
    )


if __name__ == "__main__":
    main()
