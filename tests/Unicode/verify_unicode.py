#!/usr/bin/env python3
"""Check C# Unicode behavior against pinned UCD data, without a Rust toolchain."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import random
import subprocess
import sys
from bisect import bisect_right
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / ".temp/unicode-v080/build"
sys.path.insert(0, str(ROOT / "scripts"))
from generate_unicode import Interval, Mapping, mappings, properties


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


def order_pairs() -> list[tuple[str, str]]:
    pairs = [
        ("𐀀", "\ue000"),
        ("\ue000", "𐀀"),
        ("", ""),
        ("a", "aa"),
        ("a" * 128 + "𐀀", "a" * 128 + "\ue000"),
        ("𐀀", "𐀁"),
        ("\U0010ffff", "\uffff"),
        ("same", "same"),
    ]
    generator = random.Random(170081)
    alphabet = [
        "a",
        "z",
        "/",
        ".",
        "\0",
        "ß",
        "可",
        "\ue000",
        "\uffff",
        "𐀀",
        "𐀁",
        "𐐀",
        "😀",
        "\U0010ffff",
    ]
    for _ in range(10000):
        prefix = "a" * generator.randrange(0, 65)
        left = "".join(generator.choices(alphabet, k=generator.randrange(0, 30)))
        right = "".join(generator.choices(alphabet, k=generator.randrange(0, 30)))
        pairs.append((prefix + left, prefix + right))
    return pairs


def mapped(value: str, mapping: Mapping) -> str:
    return "".join(
        "".join(chr(item) for item in mapping.get(ord(character), (ord(character),)))
        for character in value
    )


def property_test(scalar: int, intervals: list[Interval], starts: list[int]) -> bool:
    index = bisect_right(starts, scalar) - 1
    return index >= 0 and scalar <= intervals[index][1]


def lower_context(
    value: str, lower: Mapping, cased: list[Interval], ignorable: list[Interval]
) -> str:
    cased_starts = [start for start, _ in cased]
    ignorable_starts = [start for start, _ in ignorable]
    output: list[str] = []
    for index, character in enumerate(value):
        if character != "Σ":
            output.append(mapped(character, lower))
            continue
        before = index - 1
        while before >= 0 and property_test(
            ord(value[before]), ignorable, ignorable_starts
        ):
            before -= 1
        after = index + 1
        while after < len(value) and property_test(
            ord(value[after]), ignorable, ignorable_starts
        ):
            after += 1
        final = (
            before >= 0
            and property_test(ord(value[before]), cased, cased_starts)
            and not (
                after < len(value)
                and property_test(ord(value[after]), cased, cased_starts)
            )
        )
        output.append("ς" if final else "σ")
    return "".join(output)


def compare_casing(csharp: Sequence[str]) -> dict[str, str | int]:
    lower, upper = mappings()
    changed = sorted(lower.keys() | upper.keys())
    expected = "".join(
        f"{scalar:X};{hex_source([mapped(chr(scalar), lower)]).strip()};{hex_source([mapped(chr(scalar), upper)]).strip()}\n"
        for scalar in changed
    )
    observed = invoke([*csharp, "--scalars"])
    (ARTIFACTS / "expected-scalars.txt").write_text(expected, encoding="utf-8")
    (ARTIFACTS / "csharp-scalars.txt").write_text(observed, encoding="utf-8")
    assert observed == expected, (
        "Scalar maps differ; inspect .temp/unicode-v080/build/{expected,csharp}-scalars.txt"
    )
    contexts = sample_contexts()
    cased, ignorable = properties()
    expected_contexts = "".join(
        hex_source([lower_context(value, lower, cased, ignorable)]).strip()
        + ";"
        + hex_source([mapped(value, upper)]).strip()
        + "\n"
        for value in contexts
    )
    observed_contexts = invoke(csharp, hex_source(contexts))
    (ARTIFACTS / "expected-contexts.txt").write_text(
        expected_contexts, encoding="utf-8"
    )
    (ARTIFACTS / "csharp-contexts.txt").write_text(observed_contexts, encoding="utf-8")
    assert observed_contexts == expected_contexts, (
        "Contextual maps differ; inspect .temp/unicode-v080/build/{expected,csharp}-contexts.txt"
    )
    return {
        "scalars_examined": 0x110000 - 0x800,
        "changed_scalars": len(changed),
        "contexts": len(contexts),
        "mapping_sha256": hashlib.sha256(observed.encode()).hexdigest(),
    }


def compare_order(csharp: Sequence[str]) -> dict[str, str | int]:
    pairs = order_pairs()
    source = "".join(
        hex_source([left]).strip() + ";" + hex_source([right]).strip() + "\n"
        for left, right in pairs
    )
    expected = "".join(
        str((left > right) - (left < right)) + "\n" for left, right in pairs
    )
    observed = invoke([*csharp, "--order"], source)
    assert observed == expected, (
        "Scalar ordering differs from code-point lexicographic order"
    )
    return {
        "ordered_pairs": len(pairs),
        "ordering_sha256": hashlib.sha256(observed.encode()).hexdigest(),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--native",
        action="store_true",
        help="Publish the C# conformance executable with Native AOT",
    )
    parser.add_argument("--order-only", action="store_true")
    parser.add_argument(
        "--binary", type=Path, help="Reuse an already built conformance executable"
    )
    args = parser.parse_args()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    if args.binary is not None:
        csharp = [str(args.binary.resolve())]
    elif args.native:
        publish = ARTIFACTS / "native"
        print(
            invoke(
                [
                    "dotnet",
                    "publish",
                    "tests/Unicode/Msync.Unicode.Tests.csproj",
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
        csharp = [
            str(
                publish
                / (
                    "Msync.Unicode.Tests.exe"
                    if platform.system() == "Windows"
                    else "Msync.Unicode.Tests"
                )
            )
        ]
    else:
        print(
            invoke(
                [
                    "dotnet",
                    "build",
                    "tests/Unicode/Msync.Unicode.Tests.csproj",
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
    result: dict[str, str | int | bool] = {
        "unicode_version": "17.0.0",
        "oracle": "pinned UCD mappings and scalar lexicographic order",
        "native_aot": args.native,
    }
    if not args.order_only:
        result.update(compare_casing(csharp))
    result.update(compare_order(csharp))
    report = json.dumps(result, indent=2) + "\n"
    (
        ARTIFACTS / ("native-results.json" if args.native else "managed-results.json")
    ).write_text(report, encoding="utf-8")
    print(report, end="")


if __name__ == "__main__":
    main()
