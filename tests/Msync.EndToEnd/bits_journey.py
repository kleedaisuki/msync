#!/usr/bin/env python3
"""Check $std::bits against an independent word oracle in real CLI journeys."""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import sqlite3
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
WIDTH: int = 32
MODULUS: int = 1 << WIDTH
MASK: int = MODULUS - 1


@dataclass(frozen=True)
class Operation:
    name: str
    arguments: tuple[str, ...]

    def expression(self) -> str:
        return f"$std::bits::{self.name}({', '.join(self.arguments)})"


OPERATIONS: tuple[Operation, ...] = (
    Operation("bit_and", ("word", "other")),
    Operation("bit_or", ("word", "other")),
    Operation("bit_xor", ("word", "other")),
    Operation("bit_not", ("word",)),
    Operation("shift_left", ("word", "count")),
    Operation("shift_right", ("word", "count")),
    Operation("shift_right_signed", ("word", "count")),
    Operation("rotate_left", ("word", "count")),
    Operation("rotate_right", ("word", "count")),
    Operation("pop_count", ("word",)),
    Operation("leading_zero_count", ("word",)),
    Operation("trailing_zero_count", ("word",)),
    Operation("reverse_bytes", ("word",)),
)


def program() -> str:
    statements: list[str] = []
    for index, operation in enumerate(OPERATIONS):
        statements.append(
            f"if selected == {index} {{\n"
            f"    $std::io::println({operation.expression()} as String);\n"
            "};"
        )
    values: list[str] = [operation.expression() for operation in OPERATIONS]
    values.append(
        "$std::bits::bit_or($std::bits::shift_left(word, count), "
        "$std::bits::shift_right(other, count))"
    )
    selected: str = "\n".join(
        f"    {line}" for line in "\n".join(statements).splitlines()
    )
    batch: str = ",\n            ".join(f"({value}) as String" for value in values)
    return f"""fn next_number() -> Number {{
    let line? = $std::io::read_line();
    if line is null {{ -1 }} else {{ line as Number }}
}};
let size = next_number();
if size == 0 {{
    let selected = next_number();
    let word = next_number();
    let other = next_number();
    let count = next_number();
{selected}
}} else {{
    for _ in $std::ranges::iota(0, size) {{
        let word = next_number();
        let other = next_number();
        let count = next_number();
        let rendered = [
            {batch},
        ];
        $std::io::println($std::string::join(rendered, ","));
    }};
}};
"""


def oracle(word: int, other: int, count: int) -> tuple[int, ...]:
    text: str = f"{word:032b}"
    right: str = f"{other:032b}"
    shifted_left: int = (word * 2**count) % MODULUS
    shifted_right: int = word // 2**count
    signed: int = word - MODULUS if text[0] == "1" else word
    return (
        int("".join(str(int(a == b == "1")) for a, b in zip(text, right)), 2),
        int("".join(str(int("1" in (a, b))) for a, b in zip(text, right)), 2),
        int("".join(str(int(a != b)) for a, b in zip(text, right)), 2),
        MASK - word,
        shifted_left,
        shifted_right,
        (signed // 2**count) % MODULUS,
        int(text[count:] + text[:count], 2),
        int(text[WIDTH - count :] + text[: WIDTH - count], 2),
        text.count("1"),
        WIDTH - len(text.lstrip("0")),
        WIDTH - len(text.rstrip("0")),
        int.from_bytes(word.to_bytes(4, "big"), "little"),
        shifted_left | (other // 2**count),
    )


def vectors() -> list[tuple[str, str, int]]:
    rng: random.Random = random.Random(20261112)
    selected: list[tuple[str, str, int]] = [
        ("-0", "0", 0),
        ("0", "-0", 31),
        (str(MASK), "0", 0),
        ("0", str(MASK), 1),
        (str(MASK), str(MASK), 31),
        (str(1 << 31), "1", 1),
        (str(0x01020380), str(0xFFEEDDCC), 8),
    ]
    selected.extend(
        (str(1 << count), str(MASK - (1 << count)), count) for count in range(WIDTH)
    )
    selected.extend(
        (str(rng.randrange(MODULUS)), str(rng.randrange(MODULUS)), rng.randrange(WIDTH))
        for _ in range(48)
    )
    return selected


def invoke(
    cli: Path,
    cwd: Path,
    args: list[str | Path],
    stdin: str = "",
    profile: Path | None = None,
) -> subprocess.CompletedProcess[str]:
    telemetry: list[str | Path] = (
        ["--telemetry-db", profile, "--telemetry-level", "profile"]
        if profile is not None
        else ["--no-telemetry"]
    )
    return subprocess.run(
        [cli, "--no-config", *telemetry, *args],
        cwd=cwd,
        input=stdin,
        capture_output=True,
        check=False,
        text=True,
        encoding="utf-8",
        timeout=45,
    )


def expect_success(result: subprocess.CompletedProcess[str], stdout: str) -> None:
    if result.returncode != 0 or result.stdout != stdout or result.stderr:
        raise AssertionError(
            (
                result.args,
                result.returncode,
                result.stdout[:1000],
                stdout[:1000],
                result.stderr,
            )
        )


def check_route(
    cli: Path,
    cwd: Path,
    args: list[str | Path],
    case_set: list[tuple[str, str, int]],
    source: Path,
    source_text: str,
    profile: Path | None = None,
) -> dict[str, int]:
    expect_batch(cli, cwd, args, case_set, profile=profile)

    failures: list[tuple[int, str, str, str, str]] = [
        (index, "-1", "0", "0", "Word32 operand") for index in range(len(OPERATIONS))
    ]
    failures.extend(
        (0, "1", value, "0", "Word32 operand")
        for value in ("0.5", str(MODULUS), "NaN", "Infinity", "-Infinity")
    )
    failures.extend(
        (6, str(1 << 31), "0", value, "Word32 shift count")
        for value in ("-1", "0.5", "32", "NaN", "Infinity", "-Infinity")
    )
    for index, word, other, count, diagnostic in failures:
        result: subprocess.CompletedProcess[str] = invoke(
            cli, cwd, args, f"0\n{index}\n{word}\n{other}\n{count}\n"
        )
        call: str = OPERATIONS[index].expression()
        start: int = source_text.index(call)
        line: int = source_text.count("\n", 0, start) + 1
        column: int = start - source_text.rfind("\n", 0, start)
        locations: tuple[str, str] = (
            f"{source}:{line}:{column}",
            f"{source} (source offset {start}..{start + len(call)})",
        )
        if (
            result.returncode == 0
            or result.stdout
            or diagnostic not in result.stderr
            or not any(location in result.stderr for location in locations)
        ):
            raise AssertionError((args, index, word, other, count, result))
    report: dict[str, int] = {
        "vectors": len(case_set),
        "results": len(case_set) * (len(OPERATIONS) + 1),
        "rejected_calls": len(failures),
    }
    if profile is not None:
        report["fast_calls"] = check_profile(profile, len(case_set))
    return report


def check_profile(profile: Path, vectors: int) -> int:
    observed: dict[tuple[str, str], int] = {}
    with sqlite3.connect(profile) as database:
        for name, metric, count in database.execute(
            """
            SELECT a.value, m.key, SUM(m.value_int)
            FROM records r
            JOIN attributes a ON a.record_id = r.id AND a.key = 'native.name'
            JOIN metrics m ON m.record_id = r.id AND m.key IN ('calls', 'fast_calls')
            WHERE r.name = 'vm.native' AND a.value LIKE 'bits.%'
            GROUP BY a.value, m.key
            """
        ):
            observed[(name, metric)] = count
    nested: set[str] = {"bit_or", "shift_left", "shift_right"}
    expected: dict[str, int] = {
        "bits." + operation.name: vectors * (1 + int(operation.name in nested))
        for operation in OPERATIONS
    }
    wanted: dict[tuple[str, str], int] = {
        (name, metric): count
        for name, count in expected.items()
        for metric in ("calls", "fast_calls")
    }
    if observed != wanted:
        raise AssertionError((observed, wanted))
    return sum(expected.values())


def expect_batch(
    cli: Path,
    cwd: Path,
    args: list[str | Path],
    case_set: list[tuple[str, str, int]],
    *,
    profile: Path | None = None,
) -> None:
    lines: list[str] = [str(len(case_set))]
    expected: list[str] = []
    for word, other, count in case_set:
        lines.extend((word, other, str(count)))
        results: tuple[int, ...] = oracle(int(word), int(other), count)
        expected.append(",".join(map(str, results)))
    expect_success(
        invoke(cli, cwd, args, "\n".join(lines) + "\n", profile),
        "\n".join(expected) + "\n",
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--profile", action="store_true")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    cli: Path = args.binary.resolve(strict=True)
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    case_set: list[tuple[str, str, int]] = vectors()
    report: dict[str, object] = {
        "binary_sha256": hashlib.sha256(cli.read_bytes()).hexdigest(),
        "managed_assemblies_sha256": {
            assembly.name: hashlib.sha256(assembly.read_bytes()).hexdigest()
            for assembly in sorted(cli.parent.glob("*.dll"))
            if assembly.name == "msync.dll" or assembly.name.startswith("Msync.")
        },
    }
    with tempfile.TemporaryDirectory(prefix="bits-journey-", dir=scratch) as name:
        work: Path = Path(name)
        source: Path = work / "word stream.msync"
        portable: Path = work / "word stream.mso"
        executable: Path = work / "word stream.mexe"
        launch: Path = work / "elsewhere"
        launch.mkdir()
        source_text: str = program()
        source.write_text(source_text, encoding="utf-8", newline="\n")

        def profile_for(route: str) -> Path | None:
            return work / f"{route}.sqlite" if args.profile else None

        source_report: dict[str, int] = check_route(
            cli,
            work,
            ["run", "-O2", source],
            case_set,
            source,
            source_text,
            profile_for("source"),
        )
        expect_success(
            invoke(cli, work, ["compile", "-O2", source, "-o", portable]), ""
        )
        source.unlink()
        portable_report: dict[str, int] = check_route(
            cli,
            launch,
            ["run", portable],
            case_set,
            source,
            source_text,
            profile_for("mso"),
        )
        expect_success(invoke(cli, work, ["bind-ir", portable, "-o", executable]), "")
        portable.unlink()
        executable_report: dict[str, int] = check_route(
            cli,
            launch,
            ["run", executable],
            case_set,
            source,
            source_text,
            profile_for("mexe"),
        )
        report.update(
            source=source_report,
            source_free_mso=portable_report,
            source_and_mso_free_mexe=executable_report,
        )
    if args.report is not None:
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
