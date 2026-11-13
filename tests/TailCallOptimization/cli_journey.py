"""Check deep source/portable/bound tail calls through one real CLI image."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import uuid
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = "fn sum(n, acc) { if n == 0 { acc } else { sum(n - 1, acc + n) } }; $std::io::println(sum(100000, 0) as String);\n"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve(strict=True)
    work: Path = ROOT / ".temp/native-tail" / ("aot-cli-" + uuid.uuid4().hex)
    work.mkdir(parents=True)
    receipt: Path = work / "result.json"
    report: dict[str, Any] = {
        "status": "running",
        "binary": str(binary),
        "binary_sha256": digest(binary),
        "source": SOURCE,
        "checks": [],
    }
    source: Path = work / "main.msync"
    source.write_text(SOURCE)
    ordinal: int = 0

    def run(arguments: list[str], policy: str = "off", expected: bytes = b"") -> None:
        nonlocal ordinal
        ordinal += 1
        environment: dict[str, str] = dict(os.environ)
        environment["MSYNC_JIT"] = policy
        command: list[str] = [
            str(binary),
            "--no-config",
            "--no-telemetry",
            "--color",
            "never",
            *arguments,
        ]
        row: dict[str, Any] = {
            "command": command,
            "policy": policy,
            "source_exists": source.exists(),
            "portable_exists": (work / "main.mso").exists(),
            "status": "running",
        }
        report["checks"].append(row)
        receipt.write_text(json.dumps(report, indent=2) + "\n")
        process: subprocess.CompletedProcess[bytes] = subprocess.run(
            command,
            cwd=work,
            env=environment,
            input=b"",
            capture_output=True,
            timeout=120,
            check=False,
        )
        (work / f"{ordinal}.stdout").write_bytes(process.stdout)
        (work / f"{ordinal}.stderr").write_bytes(process.stderr)
        row.update(
            exit_code=process.returncode,
            status="passed"
            if process.returncode == 0
            and process.stdout == expected
            and not process.stderr
            else "failed",
        )
        assert row["status"] == "passed", (row, process.stdout, process.stderr)

    try:
        for policy in ("off", "linked", "force"):
            run(["main.msync"], policy, b"5000050000\n")
        run(["compile", "-O2", "main.msync", "-o", "main.mso"])
        source.unlink()
        run(["bind-ir", "main.mso", "-o", "main.mexe"])
        report["artifact_sha256"] = {
            name: digest(work / name) for name in ("main.mso", "main.mexe")
        }
        for policy in ("off", "linked", "force"):
            run(["main.mso"], policy, b"5000050000\n")
        (work / "main.mso").unlink()
        for policy in ("off", "linked", "force"):
            run(["main.mexe"], policy, b"5000050000\n")
        report["status"] = "complete"
    except BaseException as error:
        report.update(status="failed", error=repr(error))
        raise
    finally:
        receipt.write_text(json.dumps(report, indent=2) + "\n")
        print(receipt, flush=True)


if __name__ == "__main__":
    main()
