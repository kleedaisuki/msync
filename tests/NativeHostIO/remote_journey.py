"""Exercise the native-model host over real single-channel loopback OpenSSH."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/support"))
from ssh_fixture import Fixture, lines


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--case", action="append")
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve()
    prefix: list[str] = (
        ["dotnet", str(binary)] if binary.suffix == ".dll" else [str(binary)]
    )
    reports: list[dict[str, Any]] = []
    with Fixture(ROOT / ".temp/native-host-io-ssh") as fixture:
        for name in options.case or (
            "upload",
            "download",
            "cohort",
            "mixed-cohort",
            "selector",
            "process",
            "bytes",
            "planned-upload",
            "planned-download",
            "planned-merge",
            "planned-changed-kind",
            "planned-directory-metadata",
            "error",
        ):
            directory: Path = fixture.root / name
            directory.mkdir()
            start_calls: int = len(lines(fixture.calls))
            start_log: int = len(lines(fixture.log))
            command: list[str] = [*prefix, "--remote-case", name, str(directory)]
            result: subprocess.CompletedProcess[bytes] = subprocess.run(
                command,
                cwd=ROOT,
                env=fixture.environment(),
                capture_output=True,
                check=False,
                timeout=60,
            )
            calls: list[str] = lines(fixture.calls)[start_calls:]
            log: list[str] = lines(fixture.log)[start_log:]
            expected: int = 2 if name in ("bytes", "planned-changed-kind") else 1
            row: dict[str, Any] = {
                "case": name,
                "command": command,
                "cwd": str(ROOT),
                "exit_code": result.returncode,
                "ssh_clients": calls.count("ssh"),
                "scp_clients": calls.count("scp"),
                "authentications": sum(
                    "Accepted publickey for " in line for line in log
                ),
                "sessions": sum("Starting session:" in line for line in log),
                "stdout": result.stdout.decode("utf-8", errors="replace"),
                "stderr": result.stderr.decode("utf-8", errors="replace"),
            }
            reports.append(row)
            (fixture.root / "result.json").write_text(
                json.dumps(reports, indent=2) + "\n"
            )
            assert result.returncode == 0, row
            assert (
                row["ssh_clients"]
                == row["authentications"]
                == row["sessions"]
                == expected
            ), row
            assert row["scp_clients"] == 0, row
            print(f"PASS {name}: {expected} native client/channel(s)", flush=True)


if __name__ == "__main__":
    main()
