#!/usr/bin/env python3
"""Observe field memory, aliases and factory instances through actual CLI artifacts."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAYLOADS: dict[str, bytes] = {
    "left.bin": b"left\x00\xff\r\n",
    "right.bin": b"right\x00\xfe\n",
    "third.bin": "third 世界\n".encode(),
}


@dataclass(frozen=True)
class Example:
    name: str
    source: str
    expected: dict[str, bytes]
    selected: bool = False
    error: str | None = None
    baseline_compatible: bool = True


def examples() -> tuple[Example, ...]:
    branch = """scope Factory { new: fn(file: Path) { scope { file: file, marker: 0 } } };
let object = Factory::new(./left.bin); let alias = object;
if exists ./selected.flag { alias::file = ./right.bin; } else { alias::file = ./left.bin; };
object::file -> ./result.bin;
"""
    method = """let object = scope { file: ./left.bin, read: fn { file }, write: fn(value: Path) { file = value; } };
if exists ./selected.flag { object::write(./right.bin); } else { object::file = ./left.bin; };
object::read() -> ./result.bin;
"""
    return (
        Example("branch-true", branch, {"result.bin": PAYLOADS["right.bin"]}, True),
        Example("branch-false", branch, {"result.bin": PAYLOADS["left.bin"]}),
        Example(
            "loop-carried-fields",
            """let object = scope { file: ./left.bin, total: 0 };
for item in [1, 2, 3, 4] {
    if item > 2 { object::file = ./right.bin; } else { object::file = ./left.bin; };
    object::total = object::total + item;
};
if object::total == 10 { object::file -> ./result.bin; } else { "wrong sum" -> ./result.bin; };
""",
            {"result.bin": PAYLOADS["right.bin"]},
        ),
        Example(
            "disjoint-callee-fields",
            """fn update(object) { object::file = ./right.bin; };
let first = scope { file: ./left.bin }; let second = scope { file: ./left.bin };
let before = second::file; update(first);
before -> ./before.bin; second::file -> ./result.bin; first::file -> ./other.bin;
""",
            {
                "before.bin": PAYLOADS["left.bin"],
                "result.bin": PAYLOADS["left.bin"],
                "other.bin": PAYLOADS["right.bin"],
            },
        ),
        Example(
            "captured-field-true", method, {"result.bin": PAYLOADS["right.bin"]}, True
        ),
        Example("captured-field-false", method, {"result.bin": PAYLOADS["left.bin"]}),
        Example(
            "loop-site-generativity",
            """let objects = $std::ranges::transform([./left.bin, ./right.bin], fn(path: Path) { scope { file: path, read: fn { file } } });
let first = $std::ranges::first(objects); let second = $std::ranges::last(objects);
first::file = ./third.bin;
first::read() -> ./first.bin; second::read() -> ./second.bin;
""",
            {"first.bin": PAYLOADS["third.bin"], "second.bin": PAYLOADS["right.bin"]},
        ),
        Example(
            "nullable-loop-site-generativity",
            """let first? = null; let second? = null;
for path in [./left.bin, ./right.bin] {
    let object = scope { file: path, read: fn { file } };
    if path == ./left.bin { first = object; } else { second = object; };
};
if first is not null { if second is not null {
    first::file = ./third.bin;
    first::read() -> ./first.bin; second::read() -> ./second.bin;
}; };
""",
            {"first.bin": PAYLOADS["third.bin"], "second.bin": PAYLOADS["right.bin"]},
            baseline_compatible=False,
        ),
        Example(
            "loop-rebound-methods",
            """let objects = $std::ranges::transform([./left.bin, ./right.bin], fn(path: Path) {
    scope { file: path, read: fn { file } }
});
let saved = scope { file: ./left.bin, read: fn { file } }; let position = 0;
for object in objects {
    if position == 0 { saved = object; } else {
        saved::file = ./third.bin;
        saved::read() -> ./first.bin; object::read() -> ./second.bin;
    };
    position = position + 1;
};
""",
            {"first.bin": PAYLOADS["third.bin"], "second.bin": PAYLOADS["right.bin"]},
            baseline_compatible=False,
        ),
        Example(
            "deferred-callback-mutation",
            """let object = scope { file: ./left.bin };
let work = $std::ranges::transform([1], fn(item) { object::file = ./right.bin; item });
if exists ./selected.flag { let _ = $std::ranges::first(work); };
object::file -> ./result.bin;
""",
            {"result.bin": PAYLOADS["right.bin"]},
            True,
        ),
        Example(
            "deferred-callback-unselected",
            """let object = scope { file: ./left.bin };
let work = $std::ranges::transform([1], fn(item) { object::file = ./right.bin; item });
if exists ./selected.flag { let _ = $std::ranges::first(work); };
object::file -> ./result.bin;
""",
            {"result.bin": PAYLOADS["left.bin"]},
        ),
        Example(
            "selected-error",
            """let object = scope { file: ./left.bin };
if exists ./selected.flag { object::file = ./right.bin; let _ = $std::string::replace("text", "", "x"); };
object::file -> ./result.bin;
""",
            {},
            True,
            "oldValue",
        ),
    )


def invoke(
    binary: Path, cwd: Path, arguments: list[str]
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=cwd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
        timeout=30,
    )


def require_success(result: subprocess.CompletedProcess[str]) -> None:
    assert result.returncode == 0, (result.args, result.stdout, result.stderr)


def inputs(directory: Path, selected: bool) -> None:
    directory.mkdir()
    for name, payload in PAYLOADS.items():
        (directory / name).write_bytes(payload)
    if selected:
        (directory / "selected.flag").write_bytes(b"selected")


def journey(
    binary: Path, root: Path, selected_cases: set[str] | None, baseline_compatible: bool
) -> dict[str, int]:
    executions = 0
    failures = 0
    for example in examples():
        if selected_cases is not None and example.name not in selected_cases:
            continue
        if baseline_compatible and not example.baseline_compatible:
            continue
        for level in (0, 2):
            case = root / f"{example.name}-O{level}"
            case.mkdir()
            source = case / "input.msync"
            portable = case / "input.mso"
            source.write_text(example.source, encoding="utf-8")
            require_success(
                invoke(
                    binary,
                    case,
                    ["compile", str(source), f"-O{level}", "-o", str(portable)],
                )
            )
            for stage in ("source", "mso", "mexe"):
                run = case / stage
                inputs(run, example.selected)
                artifact = source if stage == "source" else portable
                cwd = run
                if stage == "mexe":
                    artifact = case / "input.mexe"
                    require_success(
                        invoke(
                            binary, run, ["bind-ir", str(portable), "-o", str(artifact)]
                        )
                    )
                    cwd = case / "launch elsewhere"
                    cwd.mkdir()
                result = invoke(binary, cwd, ["run", str(artifact), f"-O{level}"])
                if example.error is None:
                    require_success(result)
                else:
                    assert result.returncode != 0 and example.error in result.stderr, (
                        example.name,
                        stage,
                        result,
                    )
                    failures += 1
                actual = {
                    path.name: path.read_bytes()
                    for path in run.iterdir()
                    if path.is_file()
                    and path.name not in PAYLOADS
                    and path.name != "selected.flag"
                }
                assert actual == example.expected, (
                    example.name,
                    level,
                    stage,
                    example.expected,
                    actual,
                )
                if stage == "mexe":
                    assert not any(cwd.iterdir()), (
                        "Bound paths moved to launch cwd",
                        example.name,
                        level,
                    )
                executions += 1
    return {
        "actual_stage_executions": executions,
        "selected_error_executions": failures,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--baseline-compatible", action="store_true")
    parser.add_argument(
        "--case", action="append", choices=tuple(case.name for case in examples())
    )
    options = parser.parse_args()
    parent = ROOT / ".temp/memory-layout-journey"
    parent.mkdir(parents=True, exist_ok=True)
    selected: set[str] | None = set(options.case) if options.case else None
    with tempfile.TemporaryDirectory(prefix="树 memory ", dir=parent) as directory:
        print(
            json.dumps(
                journey(
                    options.binary.resolve(),
                    Path(directory),
                    selected,
                    options.baseline_compatible,
                ),
                indent=2,
            )
        )


if __name__ == "__main__":
    main()
