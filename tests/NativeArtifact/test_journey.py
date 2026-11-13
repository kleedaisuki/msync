"""Exercise old-native input, compact output and source-free bound CLI execution."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import uuid
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def payload(binary: Path) -> dict[str, str]:
    return {
        path.relative_to(binary.parent).as_posix(): digest(path)
        for path in binary.parent.rglob("*")
        if path.is_file()
        and (
            path == binary
            or path.suffix in {".dll", ".so", ".dylib"}
            or path.name.endswith((".deps.json", ".runtimeconfig.json"))
        )
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--work", type=Path)
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve(strict=True)
    frozen: dict[str, str] = payload(binary)
    work: Path = (
        options.work or ROOT / ".temp/compact-codec-journey" / uuid.uuid4().hex
    ).resolve()
    if not work.is_relative_to(ROOT / ".temp") or work.exists():
        parser.error("--work must be a new directory below repository-root .temp")
    work.mkdir(parents=True)
    fixture: Path = Path(__file__).parent / "Fixtures/unpooled-v1-prereflection.mso"
    original: Path = work / "old.mso"
    shutil.copyfile(fixture, original)
    log: list[dict[str, Any]] = []

    def invoke(
        arguments: tuple[str, ...], cwd: Path = work, data: bytes = b""
    ) -> bytes:
        command: tuple[str, ...] = (
            str(binary),
            "--no-config",
            "--no-telemetry",
            "--color",
            "never",
            *arguments,
        )
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            command, cwd=cwd, input=data, capture_output=True, timeout=60, check=False
        )
        entry: dict[str, Any] = {
            "command": command,
            "cwd": str(cwd),
            "exit_code": result.returncode,
            "stdout": result.stdout.decode("utf-8", errors="backslashreplace"),
            "stderr": result.stderr.decode("utf-8", errors="backslashreplace"),
        }
        log.append(entry)
        (work / "invocations.json").write_text(json.dumps(log, indent=2) + "\n")
        if result.returncode != 0:
            raise AssertionError(entry)
        return result.stdout

    invoke(("compile", "old.mso", "-o", "compact.mso"))
    compact: Path = work / "compact.mso"
    old_bytes: int = original.stat().st_size
    compact_bytes: int = compact.stat().st_size
    if compact_bytes >= old_bytes:
        raise AssertionError((old_bytes, compact_bytes))
    inspected: dict[str, Any] = json.loads(
        invoke(("disassemble", "compact.mso", "--json"))
    )
    if inspected["representation"] != "native-v1":
        raise AssertionError(inspected)
    expected: bytes = b"7021\n105397\n146049\n"
    stdin: bytes = b"17\n8192\n"
    original.unlink()
    if invoke(("run-ir", "compact.mso"), data=stdin) != expected:
        raise AssertionError("Detached compact record stream changed its oracle")
    invoke(("bind-ir", "compact.mso", "-o", "bound.mexe"))
    compact_hash: str = digest(compact)
    compact.unlink()
    elsewhere: Path = work / "elsewhere"
    elsewhere.mkdir()
    if invoke(("run-ir", str(work / "bound.mexe")), elsewhere, stdin) != expected:
        raise AssertionError("Detached compact executable changed its oracle")
    if payload(binary) != frozen:
        raise AssertionError("CLI dependency payload changed during the journey")
    report: dict[str, Any] = {
        "status": "complete",
        "binary": str(binary),
        "payload_sha256": frozen,
        "input_sha256": digest(fixture),
        "compact_sha256": compact_hash,
        "unpooled_bytes": old_bytes,
        "compact_bytes": compact_bytes,
        "source_present": False,
        "invocations": len(log),
    }
    (work / "result.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"PASS detached compact codec journey: {old_bytes} -> {compact_bytes} bytes")


if __name__ == "__main__":
    main()
