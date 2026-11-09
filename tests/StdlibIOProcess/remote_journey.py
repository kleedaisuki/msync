#!/usr/bin/env python3
"""Exercise binary/text I/O and Process control over one native SSH channel."""

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
    parser.add_argument("--binary", type=Path, required=True)
    options: argparse.Namespace = parser.parse_args()
    with Fixture(ROOT / ".temp" / "stdlib-io-ssh") as fixture:
        environment: dict[str, str] = fixture.environment()
        environment["MSYNC_IO_REMOTE"] = "Perf_Fixture"
        start: int = len(lines(fixture.log))
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            [str(options.binary.resolve())],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            timeout=120,
            check=False,
        )
        calls: list[str] = lines(fixture.calls)
        log: list[str] = lines(fixture.log)[start:]
        report: dict[str, Any] = {
            "exit_code": result.returncode,
            "ssh_clients": calls.count("ssh"),
            "scp_clients": calls.count("scp"),
            "authentications": sum("Accepted publickey for " in entry for entry in log),
            "sessions": sum("Starting session:" in entry for entry in log),
            "stdout": result.stdout.decode("utf-8", errors="replace"),
            "stderr": result.stderr.decode("utf-8", errors="replace"),
        }
        (fixture.root / "report.json").write_text(
            json.dumps(report, indent=2), encoding="utf-8"
        )
        print(json.dumps(report, indent=2, ensure_ascii=True))
        assert result.returncode == 0, report
        assert (
            report["ssh_clients"]
            == report["authentications"]
            == report["sessions"]
            == 17
        ), report
        assert report["scp_clients"] == 0, report


if __name__ == "__main__":
    main()
