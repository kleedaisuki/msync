"""Exercise frozen format-6 artifacts through the one native CLI pipeline."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]


def command(cli: Path, *arguments: str) -> list[str]:
    prefix = ["dotnet", str(cli)] if cli.suffix == ".dll" else [str(cli)]
    return [*prefix, "--no-config", "--no-telemetry", "--color", "never", *arguments]


def run(
    cli: Path, directory: Path, *arguments: str, data: bytes | None = None
) -> bytes:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        command(cli, *arguments),
        cwd=directory,
        input=data,
        capture_output=True,
        timeout=30,
        check=False,
    )
    if result.returncode != 0:
        raise AssertionError(
            f"{arguments}: exit {result.returncode}: {result.stderr.decode(errors='replace')}"
        )
    return result.stdout


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frozen-cli", required=True, type=Path)
    parser.add_argument(
        "--native-cli",
        type=Path,
        default=ROOT / "src/Msync.Cli/bin/Debug/net10.0/msync.dll",
    )
    options: argparse.Namespace = parser.parse_args()
    frozen: Path = options.frozen_cli.resolve()
    native: Path = options.native_cli.resolve()
    temporary: Path = ROOT / ".temp/legacy-v6-cli-journeys"
    temporary.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temporary) as name:
        directory: Path = Path(name)
        payload: bytes = bytes(range(256)) * 37 + b"\x00detached\xff\n"
        (directory / "input.bin").write_bytes(payload)
        source: Path = directory / "source.msync"
        source.write_text(
            './input.bin -> ./output.bin;\n$std::io::println("copied");\n',
            encoding="utf-8",
        )
        run(frozen, directory, "compile", str(source), "-o", "legacy.mso")
        run(frozen, directory, "bind-ir", "legacy.mso", "-o", "legacy.mexe")
        source.unlink()
        run(native, directory, "compile", "legacy.mso", "-o", "native.mso")
        run(native, directory, "bind-ir", "legacy.mso", "-o", "native.mexe")
        for artifact in ("native.mso", "native.mexe"):
            assert (directory / artifact).read_bytes().startswith(b"MSYNCIR\x00"), (
                artifact
            )
        for artifact in ("legacy.mso", "legacy.mexe", "native.mso", "native.mexe"):
            destination: Path = directory / "output.bin"
            destination.unlink(missing_ok=True)
            assert run(native, directory, artifact) == b"copied\n", artifact
            assert destination.read_bytes() == payload, artifact
            document: dict[str, Any] = json.loads(
                run(native, directory, "disassemble", artifact, "--json")
            )
            assert document["representation"] == "native-v1", artifact
        streamed: dict[str, Any] = json.loads(
            run(
                native,
                directory,
                "disassemble",
                "-",
                "--json",
                data=(directory / "legacy.mexe").read_bytes(),
            )
        )
        assert streamed["representation"] == "native-v1"
        invalid: bytearray = bytearray((directory / "legacy.mso").read_bytes())
        invalid[8:10] = (5).to_bytes(2, "little")
        (directory / "unsupported.mso").write_bytes(invalid)
        rejected: subprocess.CompletedProcess[bytes] = subprocess.run(
            command(native, "check", "unsupported.mso"),
            cwd=directory,
            capture_output=True,
            timeout=30,
            check=False,
        )
        assert rejected.returncode != 0 and b"expected 6" in rejected.stderr, (
            rejected.stderr
        )
    print(
        "PASS format-6/native CLI: detached local bytes, prepared plans, import/rebind, file/stdin disassembly, version boundary"
    )


if __name__ == "__main__":
    main()
