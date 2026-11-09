#!/usr/bin/env python3
"""Exercise the installed/native CLI's file and owned Process APIs end to end."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]


def quoted(value: str) -> str:
    return '"' + value.replace('"', '""') + '"'


@dataclass(frozen=True)
class Example:
    name: str
    source: str
    outputs: tuple[tuple[str, bytes], ...]


def examples(child: Path) -> tuple[Example, ...]:
    executable: str = quoted(str(child))
    return (
        Example(
            "live-pipe",
            f"""
let process = $std::process::spawn({executable}, ["--child-identity"]);
let identity = $std::process::pid(process);
$std::process::write_stdin(process, "Klee 星🙂");
$std::process::close_stdin(process);
let completed = $std::process::wait(process);
let expected = $std::string::concat($std::cast(identity, String), ":Klee 星🙂");
if $std::process::stdout(completed) == expected {{ "pipe-ok" -> ./result; }};
""",
            (("result", b"pipe-ok"),),
        ),
        Example(
            "kill",
            f"""
let process = $std::process::spawn({executable}, ["--child-wait"]);
let live = $std::process::running(process);
let requested = $std::process::kill(process);
let completed = $std::process::wait(process);
if live and requested and not $std::process::success(completed) {{ "kill-ok" -> ./result; }};
""",
            (("result", b"kill-ok"),),
        ),
        Example(
            "terminate",
            f"""
let process = $std::process::spawn({executable}, ["--child-wait"]);
let requested = $std::process::terminate(process);
if not requested {{ $std::process::kill(process); }};
let completed = $std::process::wait(process);
if not $std::process::success(completed) {{ "terminate-ok" -> ./result; }};
""",
            (("result", b"terminate-ok"),),
        ),
        Example(
            "reusable-lines",
            """
let lines = $std::io::read_lines(./"lines");
"first\n" -> ./"lines";
$std::string::join(lines, "|") -> ./first;
"second\n" -> ./"lines";
$std::string::join(lines, "|") -> ./second;
""",
            (("first", b"first"), ("second", b"second")),
        ),
        Example(
            "byte-roundtrip",
            """
$std::io::write_bytes(./bytes, [0, 255, 127, 10]);
$std::io::append_bytes(./bytes, [128, 0]);
$std::io::write_bytes(./result, $std::io::read_bytes(./bytes));
""",
            (("result", bytes([0, 255, 127, 10, 128, 0])),),
        ),
        Example(
            "metadata-composition",
            """
$std::io::create_directories(./nested/inner);
$std::io::write_text(./nested/inner/input, "metadata");
$std::io::rename(./nested/inner/input, ./nested/inner/renamed);
if $std::io::is_directory(./nested/inner)
    and $std::io::is_regular_file(./nested/inner/renamed)
    and $std::io::file_size(./nested/inner/renamed) == 8 {
    $std::io::read_text(./nested/inner/renamed) -> ./result;
};
$std::io::remove_all(./nested);
""",
            (("result", b"metadata"),),
        ),
        Example(
            "link-composition",
            """
$std::io::write_text(./target, "before");
$std::io::create_symlink(./target, ./link);
$std::io::write_text(./link, "after");
if $std::io::is_symlink(./link) {
    $std::io::read_text(./target) -> ./result;
};
$std::io::copy_symlink(./link, ./link-copy);
$std::io::remove(./link);
$std::io::read_text(./link-copy) -> ./copied;
$std::io::create_hard_link(./target, ./hard);
$std::io::read_text(./hard) -> ./hard-copy;
$std::io::create_directory(./directory);
$std::io::write_text(./directory/file, "kept");
$std::io::create_directory_symlink(./directory, ./directory-link);
$std::io::remove_all(./directory-link);
$std::io::read_text(./directory/file) -> ./directory-kept;
$std::io::remove(./link-copy);
$std::io::remove(./hard);
""",
            (
                ("result", b"after"),
                ("copied", b"after"),
                ("hard-copy", b"after"),
                ("directory-kept", b"kept"),
            ),
        ),
    )


def invoke(
    binary: Path, arguments: list[str], directory: Path
) -> subprocess.CompletedProcess[bytes]:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=directory,
        capture_output=True,
        timeout=45,
        check=False,
    )
    if result.returncode:
        raise RuntimeError(
            f"{arguments!r} failed ({result.returncode}): {result.stderr.decode('utf-8', errors='replace')}"
        )
    return result


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--child", type=Path, required=True)
    parser.add_argument("--case", action="append", default=[])
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    child: Path = arguments.child.resolve()
    parent: Path = ROOT / ".temp" / "stdlib-io-native"
    parent.mkdir(parents=True, exist_ok=True)
    count: int = 0
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        root: Path = Path(temporary)
        for example in examples(child):
            if arguments.case and example.name not in arguments.case:
                continue
            directory: Path = root / example.name
            directory.mkdir()
            source: Path = directory / "main.msync"
            source.write_text(example.source, encoding="utf-8")
            object_file: Path = directory / "main.mso"
            executable_file: Path = directory / "main.mexe"
            invoke(binary, ["compile", str(source), "-o", str(object_file)], directory)
            invoke(
                binary,
                ["bind-ir", str(object_file), "-o", str(executable_file)],
                directory,
            )
            for artifact in (source, object_file, executable_file):
                for name, _ in example.outputs:
                    (directory / name).unlink(missing_ok=True)
                invoke(binary, ["run", str(artifact)], directory)
                for name, expected in example.outputs:
                    actual: bytes = (directory / name).read_bytes()
                    assert actual == expected, (
                        example.name,
                        artifact.suffix,
                        name,
                        actual,
                        expected,
                    )
                count += 1
    print(
        json.dumps(
            {
                "native_binary": str(binary),
                "child": str(child),
                "io_process_stages": count,
            },
            ensure_ascii=True,
        )
    )


if __name__ == "__main__":
    main()
