#!/usr/bin/env python3
"""Exercise the installed/native CLI's file and owned Process APIs end to end."""

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


def quoted(value: str) -> str:
    return '"' + value.replace('"', '""') + '"'


@dataclass(frozen=True)
class Example:
    name: str
    source: str
    outputs: tuple[tuple[str, bytes], ...]
    stdin: bytes = b""
    stdout: bytes = b""
    stderr: bytes = b""


def examples(child: Path) -> tuple[Example, ...]:
    executable: str = quoted(str(child))
    return (
        Example(
            "console-streams",
            """
let io = $std::io;
let empty? = io::read_line();
if empty is not null { io::println(empty); };
let name? = io::prompt("name? ");
if name is not null { io::print(name); };
let tail? = io::read_line();
if tail is not null { io::println($std::string::concat("|", tail)); };
io::eprintln("error");
io::flush_error();
""",
            (),
            "\r\n名\r\nuntouched".encode(),
            (os.linesep + "name? 名|untouched" + os.linesep).encode(),
            ("error" + os.linesep).encode(),
        ),
        Example(
            "live-pipe",
            f"""
let process = $std::process::spawn({executable}, ["--child-identity"]);
let identity = $std::process::pid(process);
$std::process::write_stdin(process, "Klee 星🙂");
$std::process::close_stdin(process);
let completed = $std::process::wait(process);
let expected = $std::string::concat(identity as String, ":Klee 星🙂");
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
            "unrelated-reader",
            f"""
let process = $std::process::spawn({executable}, ["--child-wait"]);
$std::process::stdout(process) -> ./pending-output;
$std::io::write_text(./independent, "ready\n");
let lines = $std::io::read_lines(./independent);
$std::string::join(lines, "|") -> ./result;
$std::process::close_stdin(process);
$std::process::wait(process);
""",
            (("result", b"ready"), ("pending-output", b"ready\n")),
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
            "reusable-bytes",
            """
let bytes = $std::io::read_bytes(./input);
"星🙂" -> ./input;
$std::io::write_bytes(./first, bytes);
"second\n" -> ./input;
$std::io::write_bytes(./second, bytes);
""",
            (("first", "星🙂".encode()), ("second", b"second\n")),
        ),
        Example(
            "inert-lazy-readers",
            """
let lines = $std::io::read_lines(./created-later);
let bytes = $std::io::read_bytes(./created-later);
let no_lines = $std::ranges::take($std::io::read_lines(./never-created), 0);
let no_bytes = $std::ranges::take($std::io::read_bytes(./never-created), 0);
($std::ranges::size(no_lines) as String) -> ./no-lines;
($std::ranges::size(no_bytes) as String) -> ./no-bytes;
"later\n" -> ./created-later;
$std::string::join(lines, "|") -> ./text;
$std::io::write_bytes(./binary, bytes);
""",
            (
                ("no-lines", b"0"),
                ("no-bytes", b"0"),
                ("text", b"later"),
                ("binary", b"later\n"),
            ),
        ),
        Example(
            "captured-reader-origin",
            """
$std::io::create_directory(./nested);
$std::io::write_text("input", "origin\n");
let lines = $std::io::read_lines("input");
let bytes = $std::io::read_bytes("input");
$std::environment::set_working_directory(./nested);
$std::io::write_text("input", "changed\n");
$std::string::join(lines, "|") -> ./result;
$std::io::write_bytes(./binary, bytes);
""",
            (("result", b"origin"), ("binary", b"origin\n")),
        ),
        Example(
            "explicit-acquire-read",
            """
let lines = $std::io::read_lines(./input);
acquire { "stable\n" -> ./input; };
$std::string::join(lines, "|") -> ./result;
""",
            (("result", b"stable"),),
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
    binary: Path, arguments: list[str], directory: Path, stdin: bytes = b""
) -> subprocess.CompletedProcess[bytes]:
    command: list[str] = [str(binary), "--no-config", *arguments]
    logs: Path = directory / "commands"
    logs.mkdir(exist_ok=True)
    invocation: str = f"{len(list(logs.glob('*.json'))):03d}"
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        command,
        cwd=directory,
        input=stdin,
        capture_output=True,
        timeout=45,
        check=False,
    )
    (logs / f"{invocation}.stdout").write_bytes(result.stdout)
    (logs / f"{invocation}.stderr").write_bytes(result.stderr)
    (logs / f"{invocation}.json").write_text(
        json.dumps({"command": command, "exit_code": result.returncode}),
        encoding="utf-8",
    )
    if result.returncode:
        raise RuntimeError(
            f"{arguments!r} failed ({result.returncode}): {result.stderr.decode('utf-8', errors='replace')}"
        )
    return result


def execute_example(
    binary: Path, directory: Path, example: Example, route: str, level: int
) -> dict[str, object]:
    directory.mkdir(parents=True)
    source: Path = directory / "main.msync"
    source.write_text(example.source, encoding="utf-8")
    selected: Path = source
    if route == "hir":
        selected = directory / "main.mhir"
        invoke(
            binary,
            ["compile", str(source), "--emit", "hir", "-o", str(selected)],
            directory,
        )
        source.unlink()
    elif route in {"object", "bound"}:
        selected = directory / "main.mso"
        invoke(
            binary,
            ["compile", f"-O{level}", str(source), "-o", str(selected)],
            directory,
        )
        source.unlink()
        if route == "bound":
            executable: Path = directory / "main.mexe"
            invoke(binary, ["bind-ir", str(selected), "-o", str(executable)], directory)
            selected.unlink()
            selected = executable
    process: subprocess.CompletedProcess[bytes] = invoke(
        binary, ["run", f"-O{level}", str(selected)], directory, example.stdin
    )
    assert process.stdout == example.stdout, (
        example.name,
        route,
        level,
        "stdout",
        process.stdout,
        example.stdout,
    )
    assert process.stderr == example.stderr, (
        example.name,
        route,
        level,
        "stderr",
        process.stderr,
        example.stderr,
    )
    for name, expected in example.outputs:
        actual: bytes = (directory / name).read_bytes()
        assert actual == expected, (example.name, route, level, name, actual, expected)
    return {
        "case": example.name,
        "route": route,
        "level": level,
        "artifact_sha256": digest(selected),
        "source_sha256": hashlib.sha256(example.source.encode()).hexdigest(),
        "outputs": {name: digest(directory / name) for name, _ in example.outputs},
        "source_present": source.exists(),
        "portable_present": (directory / "main.mso").exists(),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--child", type=Path, required=True)
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--level", type=int, choices=(0, 1, 2, 3), action="append")
    parser.add_argument(
        "--route", choices=("source", "hir", "object", "bound"), action="append"
    )
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    child: Path = arguments.child.resolve()
    cases: tuple[Example, ...] = examples(child)
    unknown: set[str] = set(arguments.case) - {example.name for example in cases}
    if unknown:
        parser.error(f"Unknown cases: {sorted(unknown)}")
    levels: tuple[int, ...] = tuple(dict.fromkeys(arguments.level or (0, 2)))
    routes: tuple[str, ...] = tuple(
        dict.fromkeys(arguments.route or ("source", "hir", "object", "bound"))
    )
    root: Path = ROOT / ".temp" / "stdlib-io-native" / uuid.uuid4().hex
    root.mkdir(parents=True)
    checks: list[dict[str, object]] = []
    report: dict[str, object] = {
        "status": "running",
        "native_binary": str(binary),
        "native_sha256": digest(binary),
        "native_payload_sha256": payload(binary),
        "child": str(child),
        "child_sha256": digest(child),
        "child_payload_sha256": payload(child),
        "harness_sha256": digest(Path(__file__)),
        "checks": checks,
        "levels": levels,
        "routes": routes,
    }
    receipt: Path = root / "result.json"
    try:
        for example in cases:
            if arguments.case and example.name not in arguments.case:
                continue
            for level in levels:
                for route in routes:
                    directory: Path = root / example.name / f"O{level}" / route
                    report["active"] = {
                        "case": example.name,
                        "level": level,
                        "route": route,
                    }
                    receipt.write_text(json.dumps(report, indent=2), encoding="utf-8")
                    checks.append(
                        execute_example(binary, directory, example, route, level)
                    )
                    receipt.write_text(json.dumps(report, indent=2), encoding="utf-8")
        assert checks, "No I/O/Process journey was selected"
        assert digest(binary) == report["native_sha256"], (
            "CLI image changed during journey"
        )
        assert digest(child) == report["child_sha256"], (
            "Process child changed during journey"
        )
        report.pop("active", None)
        report["status"] = "complete"
        report["io_process_stages"] = len(checks)
    except BaseException as error:
        report.update(status="failed", error=str(error))
        raise
    finally:
        receipt.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(receipt)


def payload(binary: Path) -> dict[str, str]:
    stem: str = binary.name[:-4] if binary.suffix.lower() == ".exe" else binary.name
    manifests: set[str] = {f"{stem}.deps.json", f"{stem}.runtimeconfig.json"}
    files: list[Path] = [
        path
        for path in binary.parent.iterdir()
        if path.is_file()
        and (
            path == binary
            or path.suffix in {".dll", ".so", ".dylib"}
            or ".so." in path.name
            or path.name in manifests
        )
    ]
    runtimes: Path = binary.parent / "runtimes"
    if runtimes.is_dir():
        files.extend(path for path in runtimes.rglob("*") if path.is_file())
    return {
        path.relative_to(binary.parent).as_posix(): digest(path)
        for path in sorted(files)
    }


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


if __name__ == "__main__":
    main()
