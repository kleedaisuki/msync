"""Check borrowed multi-capture activations through the real CLI."""

from __future__ import annotations

import argparse
import json
import os
import platform
import sqlite3
import subprocess
import tempfile
from collections.abc import Sequence
from contextlib import closing
from dataclasses import dataclass
from pathlib import Path

HERE: Path = Path(__file__).resolve().parent
ROOT: Path = HERE.parents[1]
FrameCounts = tuple[int, int, int, int, int]
FRAME_KEYS: tuple[str, ...] = (
    "calls",
    "borrowed_frames",
    "split_frames",
    "detached_frames",
    "raw_arguments",
)


@dataclass(frozen=True)
class Fixture:
    name: str
    output: tuple[str, ...]
    frames: dict[str, FrameCounts]
    input: bytes = b""


FIXTURES: tuple[Fixture, ...] = (
    Fixture(
        "activation",
        (
            "98",
            "7",
            "20",
            "25",
            "18",
            "-17",
            "-17",
            "18",
            "4",
            "5",
            "6",
            "23",
            "-24",
            "-24",
            "52",
        ),
        {
            "multi-read": (3, 3, 3, 0, 0),
            "write-through": (2, 2, 2, 0, 0),
            "replace-capture": (2, 2, 2, 2, 0),
            "promote-argument": (2, 2, 2, 2, 0),
            "read-owned": (3, 3, 0, 0, 0),
            "extra-local": (1, 0, 0, 0, 0),
            "replace-plain-capture": (2, 2, 2, 2, 0),
            "copied-cell-argument": (1, 1, 1, 1, 0),
        },
    ),
    Fixture(
        "process",
        ("echo:returned", "5", "echo:original-argument", "echo:captured-cell"),
        {
            "return-process": (1, 1, 1, 0, 0),
            "write-original-argument": (1, 1, 1, 1, 1),
            "write-captured-cell": (1, 1, 1, 0, 0),
        },
    ),
    Fixture(
        "effect-tails",
        ("21324.5", "162.75"),
        {
            "read-captured-tail": (137, 137, 137, 0, 137),
            "write-captured-tail": (137, 137, 137, 0, 137),
            "commit-raw-tail": (137, 137, 137, 137, 137),
        },
    ),
    Fixture(
        "raw-numbers",
        ("-0", "NaN", "-0", "42", "NaN", "-0", "207"),
        {
            "raw-contiguous-identity": (1, 1, 0, 0, 1),
            "raw-split-identity": (1, 1, 1, 0, 1),
            "raw-detach": (1, 1, 1, 1, 2),
            "raw-promote": (1, 1, 1, 1, 1),
            "raw-promoted-read": (1, 1, 0, 0, 0),
            "raw-await": (1, 1, 1, 0, 1),
        },
        input=b"release\n",
    ),
    Fixture("captured", ("-5", "-18", "15", "33"), {"step": (2, 2, 2, 0, 0)}),
)


def invoke(
    command: Sequence[str],
    directory: Path,
    env: dict[str, str] | None = None,
    data: bytes = b"",
) -> bytes:
    process: subprocess.CompletedProcess[bytes] = subprocess.run(
        command,
        cwd=directory,
        input=data,
        capture_output=True,
        check=False,
        timeout=30,
        env=env,
    )
    if process.returncode != 0:
        raise RuntimeError(
            f"{process.args!r} in {directory} exited {process.returncode}: "
            f"stdout={process.stdout!r}; stderr={process.stderr!r}"
        )
    return process.stdout


def read_frames(database: Path) -> dict[str, dict[str, int]]:
    frames: dict[str, dict[str, int]] = {}
    with closing(sqlite3.connect(database)) as db:
        exported: list[tuple[str, str | None]] = db.execute(
            "SELECT status, exporter_error FROM sessions"
        ).fetchall()
        execute: list[tuple[str]] = db.execute(
            "SELECT status FROM spans WHERE name = 'execute'"
        ).fetchall()
        if exported != [("complete", None)] or execute != [("ok",)]:
            raise AssertionError(
                f"{database}: incomplete profile: {exported}, {execute}"
            )
        for name, key, value in db.execute(
            """SELECT a.value, m.key, SUM(m.value_int) FROM records r
               JOIN attributes a ON r.id=a.record_id AND a.key='function.name'
               JOIN metrics m ON r.id=m.record_id
               WHERE r.name='vm.function' GROUP BY a.value,m.key"""
        ):
            frames.setdefault(name, {})[key] = int(value)
    return frames


def check(
    binary: Path,
    directory: Path,
    fixture: Fixture,
    route: str,
    artifact: str,
    semantic_only: bool,
    *,
    native: bool = True,
    detailed: bool = False,
) -> dict[str, object]:
    env: dict[str, str] = os.environ.copy()
    if not native:
        env["MSYNC_JIT"] = "off"
    level: str = (
        "profile" if fixture.name == "effect-tails" and not detailed else "instructions"
    )
    database: Path = directory / f"{route}.sqlite"
    arguments: list[str] = [
        str(binary),
        "--no-config",
        "-O0",
        "--telemetry-db",
        str(database),
        "--telemetry-level",
        level,
        "run",
        artifact,
    ]
    output: tuple[str, ...] = tuple(
        invoke(arguments, directory, env, fixture.input).decode("utf-8").splitlines()
    )
    if output != fixture.output:
        raise AssertionError(
            f"{fixture.name}/{route}: {output!r} != {fixture.output!r}"
        )
    frames: dict[str, dict[str, int]] = read_frames(database)
    result: dict[str, FrameCounts] = {}
    tails: dict[str, int] = {}
    for name, expected in fixture.frames.items():
        metrics: dict[str, int] = frames[name]
        calls, borrowed, split, detached, raw = (
            metrics.get(key, 0) for key in FRAME_KEYS
        )
        observed: FrameCounts = (calls, borrowed, split, detached, raw)
        if not semantic_only and observed != expected:
            raise AssertionError(
                f"{fixture.name}/{route}/{name}: {observed} != {expected}"
            )
        if (
            split > borrowed
            or borrowed > calls
            or detached > borrowed
            or (raw > 0 and borrowed == 0)
        ):
            raise AssertionError(
                f"{fixture.name}/{route}/{name}: inconsistent frame counts {observed}"
            )
        result[name] = observed
        tails[name] = metrics.get("jit_tail_entries", 0)
    if fixture.name == "effect-tails" and not semantic_only:
        native_expected: bool = (
            level == "profile"
            and env.get("MSYNC_JIT", "").lower() != "off"
            and (
                platform.machine().lower() in {"x86_64", "amd64"}
                and platform.system() in {"Linux", "Windows"}
            )
        )
        if tails != {
            "read-captured-tail": 74 if native_expected else 0,
            "write-captured-tail": 0,
            "commit-raw-tail": 74 if native_expected else 0,
        }:
            raise AssertionError(
                f"{fixture.name}/{route}: read-only/write guard mismatch: {tails}"
            )
    print(f"PASS {fixture.name}/{route}", flush=True)
    return {"frames": result, "jit_tails": tails}


def run(binary: Path, work: Path, semantic_only: bool) -> dict[str, object]:
    reports: dict[str, object] = {}
    receipt: dict[str, object] = {
        "binary": str(binary),
        "status": "running",
        "fixtures": reports,
    }
    path: Path = work / "result.json"
    try:
        emit: Path = work / "emit"
        invoke(
            [
                "dotnet",
                "run",
                "--project",
                str(HERE),
                "-c",
                "Release",
                "--artifacts-path",
                str(work / "build"),
                "-p:RestoreLockedMode=true",
                "--",
                "--emit",
                str(emit),
            ],
            ROOT,
        )
        for fixture in FIXTURES:
            directory: Path = work / fixture.name
            directory.mkdir()
            routes: dict[str, object] = {}
            reports[fixture.name] = routes
            portable: Path = directory / "program.mso"
            if fixture.name == "captured":
                source: Path = directory / "program.msync"
                source.write_bytes((HERE / "captured.msync").read_bytes())
                routes["source"] = check(
                    binary, directory, fixture, "source", source.name, semantic_only
                )
                invoke(
                    [
                        str(binary),
                        "--no-config",
                        "--no-telemetry",
                        "-O0",
                        "compile",
                        source.name,
                        "-o",
                        portable.name,
                    ],
                    directory,
                )
                source.unlink()
            else:
                portable.write_bytes((emit / f"{fixture.name}.mso").read_bytes())
            invoke(
                [
                    str(binary),
                    "--no-config",
                    "--no-telemetry",
                    "bind-ir",
                    portable.name,
                    "-o",
                    "program.mexe",
                ],
                directory,
            )
            routes["object"] = check(
                binary, directory, fixture, "object", portable.name, semantic_only
            )
            routes["bound"] = check(
                binary, directory, fixture, "bound", "program.mexe", semantic_only
            )
            if fixture.name == "effect-tails":
                routes["bound-off"] = check(
                    binary,
                    directory,
                    fixture,
                    "bound-off",
                    "program.mexe",
                    semantic_only,
                    native=False,
                )
                routes["bound-instructions"] = check(
                    binary,
                    directory,
                    fixture,
                    "bound-instructions",
                    "program.mexe",
                    semantic_only,
                    detailed=True,
                )
        receipt["status"] = "complete"
    except BaseException as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        print(path)
    return receipt


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--work", type=Path)
    parser.add_argument(
        "--semantic-only",
        action="store_true",
        help="Do not require split-frame metrics",
    )
    args: argparse.Namespace = parser.parse_args()
    temp: Path = ROOT / ".temp"
    temp.mkdir(exist_ok=True)
    work: Path = (
        args.work.resolve()
        if args.work
        else Path(tempfile.mkdtemp(prefix="captured-call-frames-", dir=temp))
    )
    if not work.is_relative_to(temp) or work.exists() and any(work.iterdir()):
        parser.error(
            "--work must be a new or empty directory under repository-root .temp"
        )
    work.mkdir(parents=True, exist_ok=True)
    run(args.binary.resolve(strict=True), work, args.semantic_only)


if __name__ == "__main__":
    main()
