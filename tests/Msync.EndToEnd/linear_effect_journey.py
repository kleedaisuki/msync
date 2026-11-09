#!/usr/bin/env python3
"""Preserve actual literal-effect bytes and fallback errors across all IR stages."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def invoke(
    binary: Path, directory: Path, arguments: list[str]
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )


def require_success(result: subprocess.CompletedProcess[str]) -> None:
    assert result.returncode == 0, (result.args, result.stdout, result.stderr)


def journey(binary: Path, root: Path) -> int:
    fixtures: list[tuple[str, str, dict[str, bytes]]] = [
        (
            "literal-chain",
            '"alpha 世界" -> alpha.bin; "beta" -> beta.bin; alpha.bin -> copy.bin;',
            {
                "alpha.bin": "alpha 世界".encode(),
                "beta.bin": b"beta",
                "copy.bin": "alpha 世界".encode(),
            },
        ),
        (
            "discarded-literals",
            '"discarded"; not-read.bin; "chosen" -> chosen.bin;',
            {"chosen.bin": b"chosen"},
        ),
        (
            "qualified-fallback",
            '"qualified" -> ./target @ $local;',
            {"target": b"qualified"},
        ),
    ]
    assertions = 0
    for name, source, expected in fixtures:
        case = root / name
        case.mkdir()
        program = case / "input.msync"
        program.write_text(source, encoding="utf-8")
        portable = case / "input.mso"
        require_success(
            invoke(binary, case, ["compile", str(program), "-o", str(portable)])
        )
        for stage in ("source", "mso", "mexe"):
            run = case / stage
            run.mkdir()
            artifact = program if stage == "source" else portable
            if stage == "mexe":
                artifact = case / "input.mexe"
                require_success(
                    invoke(binary, run, ["bind-ir", str(portable), "-o", str(artifact)])
                )
            working_directory = case if stage == "mexe" else run
            require_success(invoke(binary, working_directory, ["run", str(artifact)]))
            actual = {
                path.name: path.read_bytes() for path in run.iterdir() if path.is_file()
            }
            assert actual == expected, (name, stage, expected, actual)
            if stage == "mexe":
                assert not any((case / filename).exists() for filename in expected), (
                    "Executable lost its bound cwd"
                )
            assertions += 1

    case = root / "selected-error"
    case.mkdir()
    program = case / "error.msync"
    program.write_text(
        'let _ = $std::string::replace("text", "", "x");', encoding="utf-8"
    )
    portable = case / "error.mso"
    executable = case / "error.mexe"
    require_success(
        invoke(binary, case, ["compile", str(program), "-o", str(portable)])
    )
    require_success(
        invoke(binary, case, ["bind-ir", str(portable), "-o", str(executable)])
    )
    for artifact in (program, portable, executable):
        result = invoke(binary, case, ["run", str(artifact)])
        assert result.returncode != 0 and "replace" in result.stderr, (
            artifact,
            result.returncode,
            result.stderr,
        )
        assertions += 1
    return assertions


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="linear-effect-", dir=temporary
    ) as directory:
        assertions = journey(binary, Path(directory))
    print(json.dumps({"linear_effect_journeys": assertions}))


if __name__ == "__main__":
    main()
