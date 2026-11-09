#!/usr/bin/env python3
"""Run the installed Native AOT payload in a base Linux image without .NET."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sqlite3
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IMAGE = "ubuntu:22.04@sha256:076e7bf82319619f28dd0aa9c02ab34b055ad37100395b4f49a2ffba04fb5e11"


def command(arguments: Sequence[str]) -> subprocess.CompletedProcess[bytes]:
    result = subprocess.run(arguments, capture_output=True, check=False)
    assert result.returncode == 0, (
        arguments,
        result.returncode,
        result.stdout.decode(errors="replace"),
        result.stderr.decode(errors="replace"),
    )
    return result


def run(payload: Path, image: str, fixture: Path) -> dict[str, str]:
    (fixture / "home").mkdir()
    source = fixture / "main.msync"
    source.write_text(
        'let message = "Native 可莉 β\n";\n'
        "message -> ./message.txt;\n"
        'let msync: Path = "/package/bin/msync";\n'
        'let process = msync("--version");\n'
        "process::stdout -> ./child.txt;\n",
        encoding="utf-8",
    )
    base = [
        "docker",
        "run",
        "--rm",
        "--user",
        f"{os.getuid()}:{os.getgid()}",
        "--mount",
        f"type=bind,src={payload},dst=/package,readonly",
        "--mount",
        f"type=bind,src={fixture},dst=/work",
        "--env",
        "HOME=/work/home",
    ]

    def invoke(arguments: Sequence[str], *, cwd: str = "/work") -> bytes:
        return command(
            [*base, "--workdir", cwd, image, "/package/bin/msync", *arguments]
        ).stdout

    command([*base, image, "/usr/bin/test", "!", "-e", "/usr/share/dotnet"])
    command([*base, image, "/usr/bin/test", "!", "-e", "/usr/bin/dotnet"])
    libraries = command([*base, image, "/sbin/ldconfig", "-p"]).stdout
    assert b"libicu" not in libraries, libraries.decode(errors="replace")
    version: bytes = invoke(["--version"])
    assert re.fullmatch(rb"msync \d+\.\d+\.\d+\r?\n", version), version
    invoke(["--no-config", "check", "/work/main.msync"])
    invoke(["--no-config", "compile", "/work/main.msync", "-o", "/work/main.mso"])
    invoke(["--no-config", "bind-ir", "/work/main.mso", "-o", "/work/main.mexe"])
    assert not (fixture / "message.txt").exists()
    invoke(["--no-config", "run", "/work/main.msync"])
    expected = "Native 可莉 β\n".encode()
    assert (fixture / "message.txt").read_bytes() == expected
    assert (fixture / "child.txt").read_bytes() == version
    source.unlink()
    for artifact, cwd in (("main.mso", "/work"), ("main.mexe", "/var/tmp")):
        (fixture / "message.txt").unlink()
        (fixture / "child.txt").unlink()
        invoke(
            [
                "--no-config",
                "run-ir",
                f"/work/{artifact}",
                "--telemetry-db",
                "/work/session.sqlite",
            ],
            cwd=cwd,
        )
        assert (fixture / "message.txt").read_bytes() == expected
        assert (fixture / "child.txt").read_bytes() == version
    with sqlite3.connect(fixture / "session.sqlite") as connection:
        sessions = connection.execute("SELECT count(*) FROM sessions").fetchone()
        assert sessions == (2,), sessions
    return {
        "image": image,
        "version": version.decode("utf-8").strip(),
        "binary_sha256": hashlib.sha256(
            (payload / "bin/msync").read_bytes()
        ).hexdigest(),
        "fixture": str(fixture),
        "result": "source/mso/mexe/process/SQLite passed without .NET runtime",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "payload", type=Path, help="Extracted package root containing bin/"
    )
    parser.add_argument("--image", default=IMAGE)
    args = parser.parse_args()
    parent = ROOT / ".temp/deployment-v080"
    parent.mkdir(parents=True, exist_ok=True)
    fixture = Path(tempfile.mkdtemp(prefix="native-", dir=parent))
    result = run(args.payload.resolve(), args.image, fixture)
    report = json.dumps(result, indent=2) + "\n"
    (fixture / "results.json").write_text(report, encoding="utf-8")
    print(report, end="")


if __name__ == "__main__":
    main()
