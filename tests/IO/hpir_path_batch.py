"""Prove a typed Path mapper parks at acquire without blocking its submitting VM."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import subprocess
import sys
import time
from contextlib import closing
from pathlib import Path

CHILD: str = """
import pathlib,sys,time
root, name = pathlib.Path(sys.argv[1]), sys.argv[2]
(root / (name + '.started')).touch()
while not (root / (name + '.release')).exists():
    time.sleep(.005)
(root / (name + '.finished')).write_bytes(name.encode())
print(name, end='')
"""


def run(binary: Path, root: Path, mode: str) -> None:
    anchor: Path = root / "anchor"
    detached: Path = root / "detached"
    anchor.mkdir(parents=True)
    detached.mkdir()
    (anchor / "out").mkdir()
    for name in ("alpha", "beta"):
        (anchor / name).write_bytes(name.encode())
    child: Path = anchor / "child.py"
    child.write_text(CHILD, encoding="utf-8")
    source: Path = anchor / "program.msync"
    args: str = ", ".join(json.dumps(str(path)) for path in (child, anchor))
    source.write_text(
        f"let python: Path = {json.dumps(sys.executable)};\n"
        'let state = object { child: python("-c", "pass") };\n'
        f'let prior = release python({args}, "prior");\n'
        """\
let calls = 0;
let paths = $std::ranges::transform([./alpha, ./beta], fn(path) {
    calls = calls + 1;
    acquire;
    if calls == 2 { state::child = release python(CHILD_ARGS, "late"); };
    path
});
paths -> ./out;
$std::io::println("submitting guest continued");
acquire;
./late.finished -> ./observed;
state::child::stdout -> ./captured;
$std::io::println(calls as String);
""".replace("CHILD_ARGS", args),
        encoding="utf-8",
    )
    cli: tuple[str, ...] = (str(binary), "--color", "never", "--no-config")
    portable: Path = anchor / "program.mso"
    executable: Path = detached / "program.mexe"
    subprocess.run(
        (*cli, "--no-telemetry", "compile", "-O2", source.name, "-o", portable.name),
        cwd=anchor,
        check=True,
        timeout=20,
    )
    source.unlink()
    subprocess.run(
        (*cli, "--no-telemetry", "bind-ir", portable.name, "-o", str(executable)),
        cwd=anchor,
        check=True,
        timeout=20,
    )
    portable.unlink()
    if not executable.read_bytes().startswith(b"MSHPIR\0"):
        raise AssertionError("Public bind did not produce a physical image")
    database: Path = root / "execution.sqlite"
    output: Path = root / "run.out"
    arguments: tuple[str, ...] = (
        *cli,
        "--telemetry-db",
        str(database),
        "--telemetry-level",
        "diagnostics",
        "run",
        executable.name,
    )
    with (
        output.open("wb") as stdout,
        (root / "run.err").open("wb") as stderr,
        subprocess.Popen(
            arguments,
            cwd=detached,
            stdout=stdout,
            stderr=stderr,
            env={**os.environ, "MSYNC_JIT": mode},
        ) as running,
    ):
        try:
            deadline: float = time.monotonic() + 12
            while not (anchor / "prior.started").is_file() or not output.read_bytes():
                if running.poll() is not None or time.monotonic() >= deadline:
                    raise AssertionError(
                        f"{mode}: owner did not progress while the mapper was parked"
                    )
                time.sleep(0.005)
            if output.read_bytes().splitlines() != [
                b"submitting guest continued"
            ] or list((anchor / "out").iterdir()):
                raise AssertionError(
                    f"{mode}: prematurely consumed or published an item"
                )
            (anchor / "prior.release").touch()
            while (
                not (anchor / "late.started").is_file()
                or not (anchor / "out" / "beta").is_file()
            ):
                if running.poll() is not None or time.monotonic() >= deadline:
                    raise AssertionError(
                        f"{mode}: the late child and its producing cohort did not progress"
                    )
                time.sleep(0.005)
            if output.read_bytes().splitlines() != [b"submitting guest continued"]:
                raise AssertionError(f"{mode}: acquire let its late descendant escape")
            (anchor / "late.release").touch()
            if running.wait(timeout=20):
                raise RuntimeError((root / "run.err").read_text())
        finally:
            (anchor / "prior.release").touch()
            (anchor / "late.release").touch()
            if running.poll() is None:
                running.kill()
                running.wait(timeout=5)
    if output.read_bytes().splitlines() != [b"submitting guest continued", b"2"]:
        raise AssertionError("A callback was skipped or repeated")
    for name in ("alpha", "beta"):
        if (anchor / "out" / name).read_bytes() != name.encode():
            raise AssertionError(f"The cohort did not copy its exact {name} payload")
    if (anchor / "observed").read_bytes() != b"late" or (
        anchor / "captured"
    ).read_bytes() != b"late":
        raise AssertionError(
            "Caller did not join the late child escaped through the captured Object"
        )
    if list(detached.iterdir()) != [executable]:
        raise AssertionError("Effect executed at launch directory, not its anchor")

    with closing(sqlite3.connect(database)) as db:
        events: dict[str, int] = dict(
            db.execute(
                """SELECT name, COUNT(*) FROM records
               WHERE name IN ('hpir.guest.path_batch_launch','hpir.guest.resume','hpir.effect.path_batch',
                                  'hpir.work.guest_frontier_wait')
               GROUP BY name"""
            )
        )
        followed: int = db.execute(
            "SELECT COALESCE(SUM(value_int),0) FROM metrics WHERE key='work.followed_admissions'"
        ).fetchone()[0]
    if followed != 1:
        raise AssertionError(
            f"{mode}: expected one followed late admission, got {followed}"
        )
    if events != {
        "hpir.guest.path_batch_launch": 1,
        "hpir.guest.resume": 1,
        "hpir.effect.path_batch": 1,
        "hpir.work.guest_frontier_wait": 1,
    }:
        raise AssertionError(
            f"{mode}: expected one owned batch, one resume and one host cohort, got {events}"
        )


def collision(binary: Path, root: Path) -> None:
    root.mkdir()
    for directory in ("left", "right"):
        (root / directory).mkdir()
        (root / directory / "same.txt").write_bytes(directory.encode())
    (root / "out").mkdir()
    source: Path = root / "collision.msync"
    source.write_text(
        """\
let calls = 0;
let paths = $std::ranges::transform([./left/same.txt, ./right/same.txt], fn(path) {
    calls = calls + 1;
    path
});
paths -> ./out;
acquire;
$std::io::println(calls as String);
""",
        encoding="utf-8",
    )
    common: tuple[str, ...] = (
        str(binary),
        "--color",
        "never",
        "--no-config",
        "--no-telemetry",
        "run",
        source.name,
    )
    with subprocess.Popen(
        (*common, "--dry-run"), cwd=root, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    ) as preview:
        out, err = preview.communicate(timeout=20)
    if (
        preview.returncode
        or err
        or out.splitlines()[-1:] != [b"0"]
        or list((root / "out").iterdir())
    ):
        raise AssertionError(
            f"Dry run consumed the mapper or published a path: {out!r}, {err!r}"
        )
    actual: subprocess.CompletedProcess[bytes] = subprocess.run(
        common,
        cwd=root,
        capture_output=True,
        check=False,
        timeout=20,
    )
    (root / "result.err").write_bytes(actual.stderr)
    if (
        actual.returncode == 0
        or b"collide at destination name 'same.txt'" not in actual.stderr
    ):
        raise AssertionError(
            f"Expected finite basename preflight: {actual.returncode}, {actual.stderr!r}"
        )
    if list((root / "out").iterdir()):
        raise AssertionError(
            "The first source was already published when the duplicate was rejected"
        )


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--work", required=True, type=Path)
    parsed: argparse.Namespace = parser.parse_args()
    root: Path = parsed.work.resolve()
    root.mkdir(parents=True)
    for mode in ("off", "force"):
        mode_root: Path = root / mode
        mode_root.mkdir()
        run(parsed.binary.resolve(), mode_root, mode)
    collision(parsed.binary.resolve(), root / "collision")
    print(
        "PASS typed Path batch: detached suspension, independent owner progress, and finite duplicate preflight"
    )


if __name__ == "__main__":
    main()
