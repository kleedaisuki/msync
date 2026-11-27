#!/usr/bin/env python3
"""Exercise native field reuse, alias identity and closed call domains through the CLI."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import tempfile
from pathlib import Path
from typing import Any, cast

from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]
PROGRAM: str = """
let items = object { first: 10, second: 20, count: 2 };
let before = items::first;
items::second = 99;
let after = items::first;
((before + after) as String) -> ./values.txt;
let first_size = items::count;
items::second = 77;
let second_size = items::count;
((first_size + second_size) as String) -> ./sizes.txt;
let nodes = $std::ranges::materialize([object { value: 1 }]);
let node = $std::ranges::at(nodes, 0);
node::value = 2;
($std::ranges::at(nodes, 0)::value as String) -> ./alias.txt;
let selected = $std::math::abs;
fn argument() {
    global selected;
    selected = $std::math::ceil;
    -1.5
};
let original = selected(argument());
let replacement = selected(-1.5);
((original * 10 + replacement) as String) -> ./callee.txt;
"""
OUTPUTS: dict[str, bytes] = {
    "values.txt": b"20",
    "sizes.txt": b"4",
    "alias.txt": b"2",
    "callee.txt": b"14",
}


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--levels", default="2")
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    levels: list[int] = [int(level) for level in args.levels.split(",")]
    reports: list[dict[str, Any]] = []
    temporary: Path = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="static-optimization-", dir=temporary
    ) as name:
        base: Path = Path(name)
        for level in levels:
            for stage in ("source", "relocatable", "executable"):
                reports.append(
                    execute(binary, base / f"O{level}" / stage, level, stage)
                )
    report: dict[str, Any] = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "source_sha256": hashlib.sha256(PROGRAM.encode()).hexdigest(),
        "journeys": reports,
    }
    payload: str = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output is not None:
        output: Path = args.output
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(payload + "\n", encoding="utf-8")
    print(payload)


def execute(binary: Path, directory: Path, level: int, stage: str) -> dict[str, Any]:
    directory.mkdir(parents=True)
    source: Path = directory / "indexed calls.msync"
    source.write_text(PROGRAM, encoding="utf-8")
    accept(invoke(binary, directory, "check", str(source), f"-O{level}"), "check")
    artifact: Path = source.with_suffix(".mso")
    accept(
        invoke(
            binary, directory, "compile", str(source), "-o", str(artifact), f"-O{level}"
        ),
        "compile",
    )
    disassembly: subprocess.CompletedProcess[str] = invoke(
        binary, directory, "disassemble", str(artifact), "--json"
    )
    accept(disassembly, "disassemble")
    document: dict[str, Any] = cast(dict[str, Any], json.loads(disassembly.stdout))
    text: str = json.dumps(document)
    assert document["representation"] == "native-v1", document
    operations: list[str] = [
        instruction["op"]
        for function in document["functions"]
        for instruction in function["instructions"]
    ]
    assert "CallNative" in operations and "Transfer" in operations, operations
    assert "storage.vector_" not in text and "arena." not in text, text
    assert "@msync-callee:" not in text and "@msync-cell:" not in text
    if stage == "executable":
        executable: Path = source.with_suffix(".mexe")
        accept(
            invoke(
                binary,
                directory,
                "bind-ir",
                str(artifact),
                "-o",
                str(executable),
                "--source-file",
                str(source),
            ),
            "bind",
        )
        artifact.unlink()
        artifact = executable
    assert not any((directory / name).exists() for name in OUTPUTS), (
        "Static stages executed IO"
    )
    if stage == "source":
        accept(invoke(binary, directory, "run", str(source), f"-O{level}"), "source")
    else:
        source.unlink()
        launch: Path = directory / "other cwd" if stage == "executable" else directory
        launch.mkdir(exist_ok=True)
        accept(invoke(binary, launch, "run-ir", str(artifact)), stage)
    for name, expected in OUTPUTS.items():
        assert (directory / name).read_bytes() == expected, name
    return {
        "level": level,
        "stage": stage,
        "exact_outputs": len(OUTPUTS),
        "native_instructions": len(operations),
    }


if __name__ == "__main__":
    main()
