#!/usr/bin/env python3
"""Compile CRLF C# sources and distinguish POSIX control code from CRLF user data."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def build_crlf(root: Path) -> Path:
    for name in (
        "Directory.Build.props",
        "Directory.Build.targets",
        "global.json",
        "NuGet.Config",
    ):
        if (ROOT / name).exists():
            shutil.copy2(ROOT / name, root / name)
    for name in ("src", "stdlib"):
        shutil.copytree(
            ROOT / name, root / name, ignore=shutil.ignore_patterns("bin", "obj")
        )
    for source in (root / "src").rglob("*.cs"):
        source.write_bytes(
            source.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
        )
    artifacts = root / "artifacts"
    subprocess.run(
        [
            "dotnet",
            "build",
            str(root / "src/Msync.Cli/Msync.Cli.csproj"),
            "--artifacts-path",
            str(artifacts),
            "-m:1",
            "-nr:false",
            "--nologo",
        ],
        cwd=root,
        check=True,
    )
    return artifacts / "bin/Msync.Cli/debug/msync"


def run(binary: Path) -> dict[str, int | bool]:
    sys.path.insert(0, str(ROOT / "legacy" / "benches"))
    from ssh_v065 import Fixture, lines

    with Fixture(ROOT / ".temp" / "shell-source-newlines") as fixture:
        work = fixture.root / "work"
        work.mkdir()
        environment: dict[str, str] = fixture.environment()
        before_log = len(lines(fixture.log))
        filename = "input\r\nfile"
        remote = fixture.root / "remote\r\nfile"
        contents = b"payload\r\nbytes\n"
        (work / filename).write_bytes(contents)
        argument = "process\r\nargument\nend\r"
        (work / "copy.msync").write_text(
            f'let remote = ssh://Perf_Fixture; let input: Path = "./{filename}"; input -> remote::"{remote}"; remote::"{remote}" -> ./output;\n'
            f'let process = (remote::"/bin/printf")("%s", "{argument}"); process::stdout -> ./process-output;\n',
            encoding="utf-8",
        )
        result = subprocess.run(
            [str(binary), "--no-config", "run", "copy.msync"],
            cwd=work,
            env=environment,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        if result.returncode != 0:
            raise RuntimeError(
                f"CRLF-source native transfer failed: {result.returncode}\n{result.stdout}\n{result.stderr}"
            )
        if (
            work / "output"
        ).read_bytes() != contents or remote.read_bytes() != contents:
            raise AssertionError(
                "Program-source newline normalization changed filename or payload bytes"
            )
        if (work / "process-output").read_bytes() != argument.encode("utf-8"):
            raise AssertionError(
                "Program-source newline normalization changed a native process argument"
            )
        calls: list[str] = lines(fixture.calls)
        log: list[str] = lines(fixture.log)[before_log:]
        if (
            calls != ["ssh", "ssh", "ssh"]
            or sum("Accepted publickey for " in line for line in log) != 3
            or sum("Starting session:" in line for line in log) != 3
        ):
            raise AssertionError(
                "Control-program repair changed actual selected native channel ownership"
            )
        report: dict[str, int | bool] = {
            "ssh_clients": 3,
            "crlf_filename_preserved": True,
            "crlf_payload_preserved": True,
            "crlf_process_argument_preserved": True,
        }
        (fixture.root / "result.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )
        print(fixture.root / "result.json")
        return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--binary", type=Path, help="Use an already-built CRLF-source image"
    )
    args = parser.parse_args()
    if args.binary is not None:
        binary = args.binary.resolve()
        run(binary)
        return
    (ROOT / ".temp").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="crlf-source-", dir=ROOT / ".temp"
    ) as temporary:
        binary = build_crlf(Path(temporary))
        run(binary)
        subprocess.run(
            [
                sys.executable,
                str(ROOT / "tests/Msync.Transfer.Tests/cli_ssh_journeys.py"),
                str(binary),
            ],
            cwd=ROOT,
            check=True,
        )


if __name__ == "__main__":
    main()
