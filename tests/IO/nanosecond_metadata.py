#!/usr/bin/env python3
"""Check native file/directory/link timestamp precision through real CLI copies."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
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


def prepare(root: Path) -> dict[str, int]:
    tree = root / "source-tree"
    (tree / "nested").mkdir(parents=True)
    (tree / "file").write_bytes(b"native nanoseconds\n")
    (tree / "nested" / "before-epoch").write_bytes(b"before Unix epoch\n")
    (tree / "link").symlink_to("file")
    (tree / "dangling").symlink_to("missing")
    expected = {
        "file": 1_700_000_000_123_456_789,
        "nested/before-epoch": -123_456_789,
        "link": 1_700_000_000_222_333_444,
        "dangling": 1_700_000_000_555_666_777,
        "nested": 1_700_000_000_987_654_321,
        "": 1_700_000_000_345_678_901,
    }
    for relative, timestamp in expected.items():
        os.utime(tree / relative, ns=(timestamp, timestamp), follow_symlinks=False)
    (tree / "file").chmod(0o750)
    os.link(tree / "file", root / "unrelated-source-link")
    return expected


def verify(tree: Path, expected: dict[str, int]) -> dict[str, int]:
    observed = {
        relative: (tree / relative).lstat().st_mtime_ns for relative in expected
    }
    if observed != expected:
        raise AssertionError(f"Nanosecond metadata changed: {observed} != {expected}")
    if (tree / "file").read_bytes() != b"native nanoseconds\n":
        raise AssertionError("Ordinary payload bytes changed")
    if (tree / "nested" / "before-epoch").read_bytes() != b"before Unix epoch\n":
        raise AssertionError("Before-epoch file bytes changed")
    if (
        os.readlink(tree / "link") != "file"
        or os.readlink(tree / "dangling") != "missing"
    ):
        raise AssertionError("Symbolic link spelling changed")
    if (tree / "file").stat().st_mode & 0o777 != 0o750:
        raise AssertionError("Native executable mode changed")
    return observed


def run(binary: Path, root: Path) -> dict[str, dict[str, int]]:
    expected = prepare(root)
    (root / "copy.msync").write_text("./source-tree -> ./output/;\n", encoding="utf-8")
    invoke(binary, ["compile", "copy.msync", "-o", "copy.mso"], root)
    invoke(binary, ["bind-ir", "copy.mso", "-o", "copy.mexe"], root)
    results: dict[str, dict[str, int]] = {}
    for stage in ("msync", "mso", "mexe"):
        invoke(binary, ["run", f"copy.{stage}"], root)
        results[stage] = verify(root / "output" / "source-tree", expected)
        verify(root / "source-tree", expected)
        if (root / "unrelated-source-link").stat().st_mtime_ns != expected["file"]:
            raise AssertionError("Copy changed unrelated hard-link metadata")
        shutil.rmtree(root / "output")
    return results


def run_ssh(binary: Path) -> dict[str, dict[str, int]]:
    sys.path.insert(0, str(ROOT / "legacy" / "benches"))
    from ssh_v065 import Fixture, lines

    results: dict[str, dict[str, int]] = {}
    with Fixture(ROOT / ".temp" / "nanosecond-ssh") as fixture:
        root = fixture.root / "work"
        root.mkdir()
        expected = prepare(root)
        remote = fixture.root / "remote"
        environment: dict[str, str] = fixture.environment()
        before_log = len(lines(fixture.log))
        scripts = {
            "upload": f'let remote = ssh://Perf_Fixture; ./source-tree -> remote::"{remote}/";\n',
            "download": f'let remote = ssh://Perf_Fixture; remote::"{remote}/source-tree" -> ./download/;\n',
        }
        for name, source in scripts.items():
            (root / f"{name}.msync").write_text(source, encoding="utf-8")
            for command in (
                ["compile", f"{name}.msync", "-o", f"{name}.mso"],
                ["bind-ir", f"{name}.mso", "-o", f"{name}.mexe"],
            ):
                subprocess.run(
                    [str(binary), "--no-config", *command],
                    cwd=root,
                    env=environment,
                    check=True,
                    capture_output=True,
                )
        if lines(fixture.calls):
            raise AssertionError("Compile or bind authenticated SSH")
        for stage in ("msync", "mso", "mexe"):
            for name, target in (
                ("upload", remote / "source-tree"),
                ("download", root / "download" / "source-tree"),
            ):
                before = len(lines(fixture.calls))
                result = subprocess.run(
                    [str(binary), "--no-config", "run", f"{name}.{stage}"],
                    cwd=root,
                    env=environment,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    check=False,
                )
                if result.returncode != 0:
                    raise RuntimeError(
                        f"SSH {name}.{stage}: {result.returncode}\n{result.stdout}\n{result.stderr}"
                    )
                if lines(fixture.calls)[before:] != ["ssh"]:
                    raise AssertionError(
                        f"{name}.{stage} did not use exactly one native SSH client"
                    )
                results[f"{name}-{stage}"] = verify(target, expected)
            shutil.rmtree(root / "download")
            shutil.rmtree(remote)
        log: list[str] = lines(fixture.log)[before_log:]
        if (
            sum("Accepted publickey for " in line for line in log) != 6
            or sum("Starting session:" in line for line in log) != 6
        ):
            raise AssertionError(
                "SSH client count disagreed with actual authentication/session count"
            )
        (fixture.root / "nanosecond-result.json").write_text(
            json.dumps(results, indent=2) + "\n", encoding="utf-8"
        )
        print(f"SSH native nanosecond proof: {fixture.root / 'nanosecond-result.json'}")
    return results


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--ssh",
        action="store_true",
        help="Also check upload/download through six real native SSH sessions",
    )
    args = parser.parse_args()
    if os.name != "posix":
        raise SystemExit(
            "The native-nanosecond fixture requires a POSIX filesystem; Windows has a separate 100ns journey"
        )
    (ROOT / ".temp").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="nanosecond-metadata-", dir=ROOT / ".temp"
    ) as temporary:
        results = run(args.binary.resolve(), Path(temporary))
        if args.ssh:
            results.update(run_ssh(args.binary.resolve()))
        if args.output is not None:
            output: Path = args.output
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
        print(
            "Native nanosecond source/object/executable metadata preserved files, directories, links and hard-link ownership."
        )


if __name__ == "__main__":
    main()
