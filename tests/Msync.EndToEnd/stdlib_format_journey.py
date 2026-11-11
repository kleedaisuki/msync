#!/usr/bin/env python3
"""Format real values through source, source-free HIR, and typed bytecode."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
SCRIPT: Path = Path(__file__).resolve()
PROGRAM: str = r"""
let text = $std::text;
text::format("{{{}}} / {} / {}", "ready", true, null) -> ./plain.txt;
text::format("{1} {0} {1}", "one", "two") -> ./indexed.txt;
text::format("{:d}", $std::ranges::size($std::ranges::of(3, "S", 4))) -> ./pack.txt;
text::format("{:🙂^5}|{:.1s}|{:>4}", "é", "😀K", "日本") -> ./unicode.txt;
text::format("{:+06d}|{:#08x}|{:#b}|{:#o}", -42, 42, 42, 42) -> ./integers.txt;
let wide = 18446744073709551616;
text::format("{:d}|{:X}|{:b}|{:o}", wide, wide, wide, wide) -> ./wide.txt;
text::format("{:.2f}|{:.1e}|{:g}|{:f}", 0.125, 12.5, -0, -0) -> ./decimal.txt;
text::format("{:?}", "a""b\c") -> ./debug.txt;
let record = scope { owner: "Klee", count: 4 };
text::format_fields("{owner:<6}/{count:02d}", record) -> ./named.txt;
record::count = 6;
text::format_fields("{owner}/{count}", record) -> ./updated.txt;
let template = $std::io::read_text(./template.txt);
text::format(template, "actual") -> ./dynamic.txt;
let lazy = $std::ranges::map([1], fn(value) {
    "unexpected" -> ./enumerated.txt;
    value
});
"""
EXPECTED: dict[str, str] = {
    "plain.txt": "{ready} / true / null",
    "indexed.txt": "two one two",
    "pack.txt": "3",
    "unicode.txt": "🙂🙂é🙂🙂|😀|  日本",
    "integers.txt": "-00042|0x00002a|0b101010|0o52",
    "wide.txt": "18446744073709551616|10000000000000000|1" + "0" * 64 + "|2" + "0" * 21,
    "decimal.txt": "0.12|1.2e+001|-0|-0.00",
    "debug.txt": r'"a\"b\\c"',
    "named.txt": "Klee  /04",
    "updated.txt": "Klee/6",
    "dynamic.txt": "dynamic actual",
    "gate.txt": "Range<Number> Process(deferred)",
    "selected.txt": "child payload",
}
INVALID: tuple[tuple[str, str], ...] = (
    ('format("{} {0}", 1)', "cannot be mixed"),
    ('format("{1}", 1)', "argument 1 is missing"),
    ('format("{:.2f}", "not numeric")', "expects Number"),
    ('format("{:x}", 1.25)', "finite integral Number"),
    ('format("😀}", 1)', "template character 2"),
    ('format_fields("{missing}", scope { present: 1 })', "Scope has no member"),
)


def literal(value: str | Path) -> str:
    return '"' + str(value).replace('"', '""') + '"'


def program() -> str:
    return (
        PROGRAM
        + f"let python: Path = {literal(sys.executable)};\n"
        + f'let process = release python({literal(SCRIPT)}, "--child");\n'
        + """
let output = scope {
    pending: process::stdout,
    description: text::format("{} {}", lazy, process)
};
text::format_fields("{description}", output) -> ./gate.txt;
text::format_fields("{pending}", output) -> ./selected.txt;
acquire;
"""
    )


def child() -> int:
    deadline: float = time.monotonic() + 12
    while not Path("gate.txt").is_file():
        if time.monotonic() >= deadline:
            print("format blocked before the gate could be published", file=sys.stderr)
            return 1
        time.sleep(0.005)
    print("child payload", end="", flush=True)
    return 0


def invoke(
    binary: Path, directory: Path, *arguments: str
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        encoding="utf-8",
        capture_output=True,
        timeout=40,
        check=False,
    )


def run(binary: Path, directory: Path, *arguments: str) -> None:
    result: subprocess.CompletedProcess[str] = invoke(binary, directory, *arguments)
    if result.returncode:
        raise AssertionError(
            (arguments, result.returncode, result.stdout, result.stderr)
        )


def verify(directory: Path) -> None:
    for name, expected in EXPECTED.items():
        path: Path = directory / name
        actual: bytes = path.read_bytes()
        if actual != expected.encode("utf-8"):
            raise AssertionError((name, expected, actual))
        path.unlink()
    if (directory / "enumerated.txt").exists():
        raise AssertionError("Formatting a lazy Range enumerated it.")


def negative(binary: Path, directory: Path) -> None:
    source: Path = directory / "bad-format.msync"
    for expression, diagnostic in INVALID:
        source.write_text(f"$std::text::{expression};\n", encoding="utf-8")
        checked: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "check", source.name
        )
        if (
            checked.returncode == 0
            or "E0230" not in checked.stderr
            or "bad-format.msync" not in checked.stderr
            or diagnostic not in checked.stderr
        ):
            raise AssertionError((expression, checked.stderr, checked.stdout))
    source.write_text(
        "$std::text::format($std::io::read_text(./template.txt), 1) -> ./bad.txt;\n",
        encoding="utf-8",
    )
    run(binary, directory, "check", source.name)
    (directory / "template.txt").write_text("{:z}", encoding="utf-8")
    failed: subprocess.CompletedProcess[str] = invoke(
        binary, directory, "run", source.name
    )
    if (
        failed.returncode == 0
        or "unknown format specifier" not in failed.stderr
        or "bad-format.msync" not in failed.stderr
        or (directory / "bad.txt").exists()
    ):
        raise AssertionError((failed.stdout, failed.stderr))


def main() -> int:
    if sys.argv[1:] == ["--child"]:
        return child()
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    temporary: Path = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="format-", dir=temporary) as work:
        directory: Path = Path(work)
        source: Path = directory / "format.msync"
        source.write_text(program(), encoding="utf-8", newline="\n")
        (directory / "template.txt").write_text("dynamic {}", encoding="utf-8")
        for level in ("-O0", "-O2"):
            run(binary, directory, "run", source.name, level)
            verify(directory)
        run(
            binary,
            directory,
            "compile",
            source.name,
            "--emit",
            "hir",
            "-o",
            "format.mhir",
        )
        source.unlink()
        run(binary, directory, "run", "format.mhir", "-O2")
        verify(directory)
        run(binary, directory, "compile", "format.mhir", "-O2", "-o", "format.mso")
        run(binary, directory, "run-ir", "format.mso")
        verify(directory)
        run(binary, directory, "bind-ir", "format.mso", "-o", "format.mexe")
        run(binary, directory, "run-ir", "format.mexe")
        verify(directory)
        negative(binary, directory)
    report: dict[str, object] = {
        "binary": str(binary),
        "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "routes": [
            "source O0",
            "source O2",
            "source-free HIR",
            "relocatable",
            "executable",
        ],
        "exact_outputs": len(EXPECTED) * 5,
        "negative_diagnostics": len(INVALID) + 1,
    }
    if args.output is not None:
        output: Path = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(
        "PASS: brace formatting, diagnostics, and demand-driven values through five CLI routes"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
