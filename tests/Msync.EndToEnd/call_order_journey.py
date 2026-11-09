#!/usr/bin/env python3
"""Keep evaluated callees distinct from locations changed by their arguments."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXACT_SOURCE = 'let action = fn(value: Bool) { "A" }; let replacement = fn(value: Bool) { "B" }; fn change() { action = replacement; true }; let first = action(change()); let second = action(false); $std::string::concat(first, second)'
SOURCES: tuple[tuple[str, str], ...] = (
    ("captured-cell", EXACT_SOURCE),
    (
        "scope-field",
        'let service = scope { action: fn(value: Bool) { "A" } }; let replacement = fn(value: Bool) { "B" }; fn change() { service::action = replacement; true }; let first = service::action(change()); let second = service::action(false); $std::string::concat(first, second)',
    ),
    (
        "copied-callee",
        'let action = fn(value: Bool) { "A" }; let saved = action; let replacement = fn(value: Bool) { "B" }; fn change() { action = replacement; true }; let first = saved(change()); let second = action(false); $std::string::concat(first, second)',
    ),
)


def invoke(
    binary: Path, work: Path, arguments: list[str], source: str | None = None
) -> subprocess.CompletedProcess[str]:
    environment: dict[str, str] = dict(os.environ)
    environment["NO_COLOR"] = "1"
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=work,
        input=source,
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=environment,
        check=False,
        timeout=30,
    )


def successful(binary: Path, work: Path, arguments: list[str]) -> None:
    result = invoke(binary, work, arguments)
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)


def check(
    binary: Path, root: Path, levels: list[int], repl_only: bool
) -> tuple[int, int]:
    executions = 0
    repl_values = 0
    for level in levels:
        result = invoke(
            binary, root, ["repl", f"-O{level}"], EXACT_SOURCE + "\n/quit\n"
        )
        assert result.returncode == 0 and result.stdout.splitlines() == ["AB"], (
            level,
            result.stdout,
            result.stderr,
        )
        repl_values += 1
        if repl_only:
            continue
        for name, text in SOURCES:
            work = root / f"{name}-O{level}"
            work.mkdir()
            source = work / "example.msync"
            source.write_text(text + " -> ./result;\n", encoding="utf-8")
            relocatable, executable = source.with_suffix(".mso"), source.with_suffix(
                ".mexe"
            )
            successful(
                binary,
                work,
                ["compile", str(source), f"-O{level}", "-o", str(relocatable)],
            )
            successful(
                binary, work, ["bind-ir", str(relocatable), "-o", str(executable)]
            )
            output = work / "result"
            assert not output.exists(), "compilation executed argument effects"
            for target in (source, relocatable, executable):
                successful(binary, work, ["run", str(target), f"-O{level}"])
                assert output.read_bytes() == b"AB", (
                    name,
                    level,
                    target,
                    output.read_bytes(),
                )
                output.unlink()
                executions += 1
    return executions, repl_values


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--levels", type=int, nargs="+", choices=range(4), default=list(range(4))
    )
    parser.add_argument("--repl-only", action="store_true")
    arguments = parser.parse_args()
    parent = ROOT / ".temp/call-order-journey"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        executions, values = check(
            arguments.binary.resolve(),
            Path(temporary),
            arguments.levels,
            arguments.repl_only,
        )
    print(
        json.dumps(
            {
                "source_and_ir_executions": executions,
                "exact_source_repl_values": values,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
