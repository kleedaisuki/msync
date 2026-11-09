#!/usr/bin/env python3
"""Exercise indexed-memory reuse and closed native call domains through the CLI."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path
from typing import Any, cast

from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]
PROGRAM: str = """
let items = $std::storage::Buffer::new([10, 20]);
let before = $std::storage::Buffer::get(items, 0);
$std::storage::Buffer::set(items, 1, 99);
let after = $std::storage::Buffer::get(items, 0);
((before + after) as String) -> ./values.txt;
let first_size = $std::storage::Buffer::size(items);
$std::storage::Buffer::set(items, 1, 77);
let second_size = $std::storage::Buffer::size(items);
((first_size + second_size) as String) -> ./sizes.txt;
let nodes = $std::storage::Buffer::new([scope { value: 1 }]);
let node = $std::storage::Buffer::get(nodes, 0);
node::value = 2;
($std::storage::Buffer::get(nodes, 0)::value as String) -> ./alias.txt;
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
    text: str = str(document["text"])
    entry: re.Match[str] | None = re.search(r"entry @f(\d+)", text)
    assert entry is not None
    body: re.Match[str] | None = re.search(
        r"^fn @f" + entry.group(1) + r" .*?^}\n", text, re.DOTALL | re.MULTILINE
    )
    assert body is not None
    gets: int = body.group(0).count('NativeCall "storage.buffer_get"')
    sizes: int = body.group(0).count('NativeCall "storage.buffer_size"')
    if level >= 2:
        assert gets == 2, f"Expected one get per owned buffer, found {gets}"
        assert sizes == 1, f"Expected one stable length read, found {sizes}"
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
        "gets": gets,
        "sizes": sizes,
    }


if __name__ == "__main__":
    main()
