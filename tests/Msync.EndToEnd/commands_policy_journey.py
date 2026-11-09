#!/usr/bin/env python3
"""Exercise typed command runners and exit policies through source-free CLI stages."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
import sys
import tempfile
from pathlib import Path

from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]
HELPER: str = """from __future__ import annotations
import sys
mode: str = sys.argv[1]
if mode == "fail":
    sys.stdout.write("out:failure")
    sys.stderr.write("err:failure")
    raise SystemExit(7)
sys.stdout.write("echo:" + sys.stdin.read())
"""

PROGRAM: str = r"""
let counts = scope { runs: 0, inputs: 0, spawns: 0 };
scope TrackedRunner {
    spawn: fn(program: Path, arguments: Range) -> Process {
        counts::spawns = counts::spawns + 1;
        $std::commands::NativeProcess::spawn(program, arguments)
    },
    run: fn(program: Path, arguments: Range) -> Process {
        counts::runs = counts::runs + 1;
        $std::commands::NativeProcess::run(program, arguments)
    },
    run_input: fn(program: Path, arguments: Range, input: String) -> Process {
        counts::inputs = counts::inputs + 1;
        $std::commands::NativeProcess::run_input(program, arguments, input)
    },
};
scope AcceptSeven {
    accept: fn(value: $std::concepts::CompletedProcess) -> Bool {
        value::code == 7
    },
};
let command = $std::commands::Command;
let checked = $std::commands::Checked;
let result = $std::data::Result;
let spec = command::with_runner(__EXECUTABLE__, __ECHO_ARGUMENTS__, TrackedRunner);
let changed = command::with_args(spec, __ECHO_ARGUMENTS__);
let extended = command::append_args(changed, ["extra"]);
let local = command::on(extended, $local);
let unused_remote = command::on(spec, ssh://unused-command-policy.invalid);
let failure = command::with_runner(__EXECUTABLE__, __FAIL_ARGUMENTS__, TrackedRunner);
let selected = checked::with_policy(failure, AcceptSeven);
if counts::runs == 0 and counts::inputs == 0 and counts::spawns == 0 {
    "pure-construction" -> ./construction.txt;
};
let echo = command::run_input(local, "policy 星");
$std::process::stdout(echo) -> ./echo.txt;
let child = command::spawn(spec);
$std::process::close_stdin(child);
let spawned = $std::process::wait(child);
$std::process::stdout(spawned) -> ./spawn.txt;
let failed = checked::run(checked::new(failure));
let accepted = checked::run(selected);
let succeeded = checked::run_input(checked::new(spec), "checked");
if result::is_err(failed) { "default-rejected" -> ./default.txt; };
let failure_data = result::fold(failed, fn(value) { value }, fn(error) { error });
(failure_data::code as String) -> ./code.txt;
failure_data::output -> ./failure-out.txt;
failure_data::error -> ./failure-err.txt;
let accepted_data = result::get_or(accepted, failure_data);
if result::is_ok(accepted) and accepted_data::code == 7
    and not accepted_data::success {
    "accepted-seven" -> ./custom.txt;
};
let success_data = result::get_or(succeeded, failure_data);
success_data::output -> ./success.txt;
if counts::runs == 2 and counts::inputs == 2 and counts::spawns == 1 {
    "selected-runners" -> ./dispatch.txt;
};
if $std::ranges::size(spec::arguments) == 4
    and $std::ranges::size(extended::arguments) == 5 {
    "argument-snapshots" -> ./snapshots.txt;
};
acquire;
"""

OUTPUTS: dict[str, bytes] = {
    "construction.txt": b"pure-construction",
    "echo.txt": "echo:policy 星".encode(),
    "spawn.txt": b"echo:",
    "default.txt": b"default-rejected",
    "code.txt": b"7",
    "failure-out.txt": b"out:failure",
    "failure-err.txt": b"err:failure",
    "custom.txt": b"accepted-seven",
    "success.txt": b"echo:checked",
    "dispatch.txt": b"selected-runners",
    "snapshots.txt": b"argument-snapshots",
}


def quote(value: str) -> str:
    """msync uses doubled quotes; backslashes are literal path data."""
    return '"' + value.replace('"', '""') + '"'


def source_for(helper: Path) -> str:
    executable: str = quote(sys.executable)
    prefix: list[str] = ["-X", "utf8", str(helper)]
    echo: str = "[" + ", ".join(quote(value) for value in [*prefix, "echo"]) + "]"
    fail: str = "[" + ", ".join(quote(value) for value in [*prefix, "fail"]) + "]"
    return (
        PROGRAM.replace("__EXECUTABLE__", executable)
        .replace("__ECHO_ARGUMENTS__", echo)
        .replace("__FAIL_ARGUMENTS__", fail)
    )


def execute(binary: Path, parent: Path, levels: list[int]) -> tuple[int, int]:
    executions: int = 0
    observations: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            helper: Path = directory / "child host.py"
            helper.write_text(HELPER, encoding="utf-8")
            source: Path = directory / "command policies.msync"
            source.write_text(source_for(helper), encoding="utf-8")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"), "check"
            )
            artifact: Path = source
            if stage != "source":
                artifact = source.with_suffix(".mso")
                accept(
                    invoke(
                        binary,
                        directory,
                        "compile",
                        str(source),
                        "-o",
                        str(artifact),
                        f"-O{level}",
                    ),
                    "compile",
                )
                if stage == "executable":
                    bound: Path = source.with_suffix(".mexe")
                    accept(
                        invoke(
                            binary,
                            directory,
                            "bind-ir",
                            str(artifact),
                            "-o",
                            str(bound),
                            "--source-file",
                            str(source),
                        ),
                        "bind",
                    )
                    artifact = bound
                source.unlink()
            if any((directory / name).exists() for name in OUTPUTS):
                raise AssertionError(
                    "Static checking, compilation or binding executed command effects"
                )
            launch: Path = directory
            if stage == "executable":
                launch = directory / "other launch"
                launch.mkdir()
            accept(invoke(binary, launch, "run", str(artifact), f"-O{level}"), stage)
            for name, expected in OUTPUTS.items():
                actual: bytes = (directory / name).read_bytes()
                if actual != expected:
                    raise AssertionError((level, stage, name, actual, expected))
                observations += 1
            executions += 1
    return executions, observations


def reject(binary: Path, directory: Path) -> int:
    programs: tuple[tuple[str, str], ...] = (
        ("$std::commands::Command::with_runner(./p, [], true);", "ProcessRunner"),
        (
            "$std::commands::Command::with_runner(./p, [], scope { run: fn(p:Path,a:Range)->Process { $std::process::run(p,a) } });",
            "spawn",
        ),
        (
            "$std::commands::Checked::with_policy($std::commands::Command::new(./p, []), scope { accept: fn(value:$std::concepts::CompletedProcess) { 1 } });",
            "accept",
        ),
        (
            "$std::commands::Command::run(scope { executable: ./p, arguments: [] });",
            "runner",
        ),
    )
    for index, (program, fragment) in enumerate(programs):
        source: Path = directory / f"rejected-policy-{index}.msync"
        source.write_text(program, encoding="utf-8")
        result: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "check", str(source)
        )
        diagnostic: str = result.stdout + result.stderr
        if result.returncode == 0 or fragment not in diagnostic:
            raise AssertionError((program, diagnostic))
    return len(programs)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--levels", type=int, nargs="+", default=[0, 2, 3])
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="command-policies-", dir=scratch) as name:
        directory: Path = Path(name)
        routes: int
        observations: int
        routes, observations = execute(binary, directory, arguments.levels)
        rejections: int = reject(binary, directory)
    report: dict[str, object] = {
        "binary": str(binary),
        "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "managed_assemblies": {
            name: hashlib.sha256((binary.parent / name).read_bytes()).hexdigest()
            for name in (
                "msync.dll",
                "Msync.Compiler.dll",
                "Msync.Core.dll",
                "Msync.Optimization.dll",
                "Msync.Runtime.dll",
            )
            if (binary.parent / name).exists()
        },
        "python": sys.executable,
        "python_version": sys.version,
        "platform": platform.platform(),
        "staged_executions": routes,
        "byte_observations": observations,
        "negative_diagnostics": rejections,
    }
    rendered: str = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
