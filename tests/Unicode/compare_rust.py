#!/usr/bin/env python3
"""Compare the Unicode17 runtime casing contract with the preserved Rust semantics."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import random
import subprocess
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / ".temp/unicode-v080/build"


def host_rid() -> str:
    system = {"Linux": "linux", "Windows": "win", "Darwin": "osx"}[platform.system()]
    machine = platform.machine().lower()
    architecture = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64"}.get(
        machine, machine
    )
    return f"{system}-{architecture}"


def invoke(arguments: Sequence[str], source: str | None = None) -> str:
    result = subprocess.run(
        arguments,
        cwd=ROOT,
        input=source,
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=True,
    )
    return result.stdout


def sample_contexts() -> list[str]:
    examples = [
        "Straße",
        "ΑΣ",
        "ΟΣİ",
        "ΟΣ́",
        "ΟΣ́Α",
        "Σ",
        "\u0345Σ",
        "A\u0345Σ\u0345",
        "A\u0345Σ\u0345A",
        "ΑΣ’",
        "ΑΣ’Α",
        "ΑΣ\u200d",
        "ΑΣ\u200dΑ",
        "İıIißẞ",
        "𐐀𐐨",
        "ﬃ և",
        "",
        "aA01/path",
    ]
    generator = random.Random(170080)
    alphabet = [
        "A",
        "a",
        "Σ",
        "ς",
        "σ",
        "’",
        "\u0345",
        "\u0301",
        "\u200d",
        "1",
        " ",
        "İ",
        "ı",
        "𐐀",
        "可",
        "ß",
        "\n",
    ]
    examples.extend(
        "".join(generator.choices(alphabet, k=generator.randrange(1, 40)))
        for _ in range(2000)
    )
    return examples


def hex_source(values: Sequence[str]) -> str:
    return "".join(
        " ".join(f"{ord(character):X}" for character in value) + "\n"
        for value in values
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--native",
        action="store_true",
        help="Publish the focused C# oracle using Native AOT",
    )
    args = parser.parse_args()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    rust = ARTIFACTS / "rust-oracle"
    invoke(["rustc", "-O", "tests/Unicode/oracle.rs", "-o", str(rust)])
    project = "tests/Unicode/Msync.Unicode.Tests.csproj"
    if args.native:
        publish = ARTIFACTS / "native"
        print(
            invoke(
                [
                    "dotnet",
                    "publish",
                    project,
                    "-c",
                    "Release",
                    "-p:NativeOracleAot=true",
                    f"-p:RuntimeIdentifier={host_rid()}",
                    "--artifacts-path",
                    str(ARTIFACTS / "aot"),
                    "-o",
                    str(publish),
                    "-m:1",
                    "-nr:false",
                    "--nologo",
                ]
            )
        )
        csharp = [str(publish / "Msync.Unicode.Tests")]
    else:
        print(
            invoke(
                [
                    "dotnet",
                    "build",
                    project,
                    "--artifacts-path",
                    str(ARTIFACTS),
                    "-m:1",
                    "-nr:false",
                    "--nologo",
                ]
            )
        )
        csharp = [
            "dotnet",
            str(ARTIFACTS / "bin/Msync.Unicode.Tests/debug/Msync.Unicode.Tests.dll"),
        ]
    expected = invoke([str(rust), "--scalars"])
    observed = invoke([*csharp, "--scalars"])
    (ARTIFACTS / "rust-scalars.txt").write_text(expected, encoding="utf-8")
    (ARTIFACTS / "csharp-scalars.txt").write_text(observed, encoding="utf-8")
    assert observed == expected, (
        "Scalar maps differ; see .temp/unicode-v080/build/{rust,csharp}-scalars.txt"
    )
    contexts = sample_contexts()
    source = hex_source(contexts)
    expected_contexts = invoke([str(rust)], source)
    observed_contexts = invoke(csharp, source)
    (ARTIFACTS / "rust-contexts.txt").write_text(expected_contexts, encoding="utf-8")
    (ARTIFACTS / "csharp-contexts.txt").write_text(observed_contexts, encoding="utf-8")
    assert observed_contexts == expected_contexts, (
        "Contextual maps differ; see .temp/unicode-v080/build/{rust,csharp}-contexts.txt"
    )
    result = {
        "unicode_version": "17.0.0",
        "rustc": invoke(["rustc", "--version"]).strip(),
        "native_aot": args.native,
        "scalars_examined": 0x110000 - 0x800,
        "changed_scalars": len(expected.splitlines()),
        "contexts": len(contexts),
        "mapping_sha256": hashlib.sha256(observed.encode()).hexdigest(),
    }
    report = json.dumps(result, indent=2) + "\n"
    (
        ARTIFACTS / ("native-results.json" if args.native else "managed-results.json")
    ).write_text(report, encoding="utf-8")
    print(report, end="")


if __name__ == "__main__":
    main()
