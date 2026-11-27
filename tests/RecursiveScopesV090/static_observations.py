"""Preserve compile-time type observations across source and detached CLI routes."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import uuid
from dataclasses import dataclass
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Case:
    name: str
    binding: str
    samples: tuple[tuple[str, str], ...]


CASES: tuple[Case, ...] = (
    Case("precise", "let output = object { last?: null };", (("", "true\nNull\n"),)),
    Case(
        "broader",
        """
        let input? = $std::io::read_line();
        let output = object { last?: if input is null { null }
            else { if input == "node" { object { number: 1 } } else { null } } };
        """,
        (("none\n", "true\nScope?\n"), ("node\n", "false\nScope?\n")),
    ),
)


def invoke(binary: Path, cwd: Path, arguments: list[str], data: str = "") -> str:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments, "--color", "never"],
        cwd=cwd,
        input=data,
        text=True,
        capture_output=True,
        check=False,
        timeout=30,
        env={**os.environ, "MSYNC_JIT": "off"},
    )
    if result.returncode or result.stderr:
        raise AssertionError(
            (result.args, result.returncode, result.stdout, result.stderr)
        )
    return result.stdout


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve(strict=True)
    work: Path = ROOT / ".temp/static-observations" / uuid.uuid4().hex
    work.mkdir(parents=True)
    launch: Path = work / "launch"
    launch.mkdir()
    rows: list[dict[str, object]] = []
    for case in CASES:
        source: str = (
            "fn observe(output) { decltype(output::last) as String };\n"
            + case.binding
            + "\nlet observed = observe(output);\n"
            + "$std::io::println((output::last is null) as String);\n"
            + "$std::io::println(observed);\n"
        )
        for level in (0, 2):
            directory: Path = work / f"{case.name}-O{level}"
            directory.mkdir()
            file: Path = directory / "case.msync"
            portable: Path = directory / "case.mso"
            bound: Path = directory / "case.mexe"
            file.write_text(source, encoding="utf-8")
            invoke(
                binary,
                directory,
                ["compile", str(file), f"-O{level}", "-o", str(portable)],
            )
            invoke(binary, directory, ["bind-ir", str(portable), "-o", str(bound)])
            for route, artifact in (
                ("source", file),
                ("object", portable),
                ("bound", bound),
            ):
                if route == "bound":
                    file.unlink()
                    portable.unlink()
                for data, expected in case.samples:
                    actual: str = invoke(binary, launch, ["run", str(artifact)], data)
                    if actual != expected:
                        raise AssertionError(
                            (case.name, level, route, data, expected, actual)
                        )
                    rows.append(
                        {
                            "case": case.name,
                            "optimization": level,
                            "route": route,
                            "stdin": data,
                            "expected_stdout": expected,
                            "source_sha256": hashlib.sha256(
                                source.encode()
                            ).hexdigest(),
                            "bound_sha256": hashlib.sha256(
                                bound.read_bytes()
                            ).hexdigest(),
                            "status": "passed",
                        }
                    )
    report: dict[str, object] = {
        "status": "passed",
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "scope": "Runtime Null does not erase distinct static decltype evidence; broader input retains its static observation for either runtime branch",
        "checks": rows,
    }
    receipt: Path = work / "result.json"
    receipt.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(receipt)
    print(f"PASS {len(rows)} static observation source/object/detached-bound routes")


if __name__ == "__main__":
    main()
