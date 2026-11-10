#!/usr/bin/env python3
"""Exercise the C# transport over real loopback OpenSSH with native client counts."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "support"))
from ssh_fixture import Fixture, lines


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--test-dll", type=Path)
    parser.add_argument("--batch-only", action="store_true")
    parser.add_argument("--relay-batch-only", action="store_true")
    options: argparse.Namespace = parser.parse_args()
    artifacts: Path = ROOT / ".temp" / "build-transfer-tests"
    test_dll: Path = (
        options.test_dll
        or artifacts
        / "bin"
        / "Msync.Transfer.Tests"
        / "debug"
        / "Msync.Transfer.Tests.dll"
    )
    if options.test_dll is None:
        subprocess.run(
            [
                "dotnet",
                "build",
                str(ROOT / "tests/Msync.Transfer.Tests/Msync.Transfer.Tests.csproj"),
                "--artifacts-path",
                str(artifacts),
                "-m:1",
                "-nr:false",
                "--nologo",
            ],
            cwd=ROOT,
            check=True,
        )
    with Fixture(ROOT / ".temp" / "transfer-ssh") as fixture:
        environment: dict[str, str] = fixture.environment()
        environment["MSYNC_TRANSFER_WORK"] = str(fixture.root / "work")
        environment["MSYNC_TRANSFER_REMOTE"] = "Perf_Fixture"
        environment["MSYNC_TRANSFER_SECOND_REMOTE"] = "Second_Fixture"
        fixture.config.write_text(
            fixture.config.read_text().replace(
                "Host Perf_Fixture", "Host Perf_Fixture Second_Fixture"
            )
        )
        before_log: int = len(lines(fixture.log))
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            ["dotnet", str(test_dll.resolve()), *(["--relay-batch-only"] if options.relay_batch_only else ["--batch-only"] if options.batch_only else [])],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            timeout=90,
            check=False,
        )
        log: list[str] = lines(fixture.log)[before_log:]
        calls: list[str] = lines(fixture.calls)
        report: dict[str, Any] = {
            "exit_code": result.returncode,
            "ssh_clients": calls.count("ssh"),
            "scp_clients": calls.count("scp"),
            "authentications": sum("Accepted publickey for " in line for line in log),
            "remote_sessions": sum("Starting session:" in line for line in log),
            "stdout": result.stdout.decode(),
            "stderr": result.stderr.decode(),
            "fixture": str(fixture.root),
        }
        (fixture.root / "result.json").write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps(report, indent=2))
        assert result.returncode == 0, report
        expected_clients: int = 2 if options.relay_batch_only else 1 if options.batch_only else 28
        assert calls.count("ssh") == expected_clients, report
        assert calls.count("scp") == 0, report
        assert report["authentications"] == expected_clients, report
        assert report["remote_sessions"] == expected_clients, report


if __name__ == "__main__":
    main()
