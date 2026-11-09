#!/usr/bin/env python3
"""Keep ordinary source reads and streamed stdin byte semantics equivalent."""

from __future__ import annotations

import argparse
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def invoke(
    binary: Path,
    fixture: Path,
    *arguments: str,
    source: bytes | None = None,
    environment: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=fixture,
        env=environment,
        input=source,
        capture_output=True,
        check=False,
        timeout=30,
    )


def run(binary: Path, fixture: Path, legacy: Path | None) -> None:
    payload = "Unicode 可莉 𐐨\r\n" + ("βé" * 16000) + "\r\n尾部"
    source = ('"' + payload + '" -> ./output;\r\n').encode("utf-8")
    script = fixture / "Unicode source.msync"
    script.write_bytes(b"\xef\xbb\xbf" + source)
    for command in ("check", "compile"):
        result = invoke(binary, fixture, command, str(script))
        assert result.returncode == 0, result.stderr
        assert not (fixture / "output").exists()
    result = invoke(binary, fixture, "run", str(script))
    assert result.returncode == 0, result.stderr
    assert (fixture / "output").read_bytes() == payload.encode("utf-8")
    (fixture / "output").unlink()
    result = invoke(binary, fixture, "run", "-", source=source)
    assert result.returncode == 0, result.stderr
    assert (fixture / "output").read_bytes() == payload.encode("utf-8")
    result = invoke(binary, fixture, "run", str(fixture / "missing.msync"))
    assert result.returncode != 0 and b"missing.msync" in result.stderr
    result = invoke(binary, fixture, "run", str(fixture))
    assert result.returncode != 0 and str(fixture).encode() in result.stderr
    if legacy is not None:
        environment = dict(os.environ)
        environment["MSYNC_LEGACY"] = str(legacy)
        old = '# msync: 0.7\n"old 可莉" -> old-output\n'.encode()
        result = invoke(
            binary, fixture, "run", "-", source=old, environment=environment
        )
        assert result.returncode == 0, result.stderr
        assert (fixture / "old-output").read_bytes() == "old 可莉".encode()
    print(
        "PASS: finite Unicode/BOM/CRLF source, streamed stdin, actionable read errors and optional consumed legacy stdin"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--legacy", type=Path)
    args = parser.parse_args()
    root = ROOT / ".temp/product-latency"
    root.mkdir(parents=True, exist_ok=True)
    fixture = Path(tempfile.mkdtemp(prefix="source-input-", dir=root))
    run(args.binary.resolve(), fixture, args.legacy.resolve() if args.legacy else None)


if __name__ == "__main__":
    main()
