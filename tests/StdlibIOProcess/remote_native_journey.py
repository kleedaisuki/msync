#!/usr/bin/env python3
"""Run the real product CLI's I/O and Process source/IR over loopback SSH."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

from native_journey import Example, quoted

ROOT: Path = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "legacy" / "benches"))
from ssh_v065 import Fixture, lines


def invoke(
    binary: Path, arguments: list[str], directory: Path, environment: dict[str, str]
) -> None:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=directory,
        env=environment,
        capture_output=True,
        timeout=60,
        check=False,
    )
    if result.returncode:
        raise RuntimeError(
            f"{arguments!r} failed ({result.returncode}): {result.stderr.decode('utf-8', errors='replace')}"
        )


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--child", type=Path, required=True)
    parser.add_argument("--case", action="append", default=[])
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve()
    child: Path = options.child.resolve()
    reports: list[dict[str, Any]] = []
    with Fixture(ROOT / ".temp" / "stdlib-native-ssh") as fixture:
        environment: dict[str, str] = fixture.environment()
        remote_file: str = quoted(str(fixture.root / "remote text 星.txt"))
        remote_bytes: str = quoted(str(fixture.root / "remote bytes.dat"))
        executable: str = quoted(str(child))
        examples: tuple[Example, ...] = (
            Example(
                "remote-files",
                f"""
let endpoint = ssh://Perf_Fixture;
let file = {remote_file} @ endpoint;
$std::io::write_text(file, "星🙂\ntext");
$std::io::append_text(file, "\0");
$std::io::read_text(file) -> ./result;
let bytes = {remote_bytes} @ endpoint;
$std::io::write_bytes(bytes, [0, 255, 128, 10]);
$std::io::write_bytes(./binary, $std::io::read_bytes(bytes));
""",
                (
                    ("result", "星🙂\ntext\0".encode()),
                    ("binary", bytes([0, 255, 128, 10])),
                ),
            ),
            Example(
                "remote-pipe",
                f"""
let endpoint = ssh://Perf_Fixture;
let process = $std::process::spawn({executable} @ endpoint, ["--child-identity"]);
let identity = $std::process::pid(process);
$std::process::write_stdin(process, "remote 星🙂");
$std::process::close_stdin(process);
let completed = $std::process::wait(process);
let expected = $std::string::concat(identity as String, ":remote 星🙂");
if $std::process::stdout(completed) == expected {{ "remote-pipe-ok" -> ./result; }};
""",
                (("result", b"remote-pipe-ok"),),
            ),
            *(
                Example(
                    f"remote-{operation}",
                    f"""
let endpoint = ssh://Perf_Fixture;
let process = $std::process::spawn({executable} @ endpoint, ["--child-wait"]);
let requested = $std::process::{operation}(process);
let completed = $std::process::wait(process);
if requested and not $std::process::success(completed) {{ "stopped" -> ./result; }};
""",
                    (("result", b"stopped"),),
                )
                for operation in ("kill", "terminate")
            ),
        )
        for example in examples:
            if options.case and example.name not in options.case:
                continue
            directory: Path = fixture.root / example.name
            directory.mkdir()
            source: Path = directory / "main.msync"
            source.write_text(example.source, encoding="utf-8")
            object_file: Path = directory / "main.mso"
            executable_file: Path = directory / "main.mexe"
            initial_calls: int = len(lines(fixture.calls))
            invoke(
                binary,
                ["compile", str(source), "-o", str(object_file)],
                directory,
                environment,
            )
            invoke(
                binary,
                ["bind-ir", str(object_file), "-o", str(executable_file)],
                directory,
                environment,
            )
            assert len(lines(fixture.calls)) == initial_calls, (
                "compile/bind authenticated a remote endpoint"
            )
            for artifact in (source, object_file, executable_file):
                for name, _ in example.outputs:
                    (directory / name).unlink(missing_ok=True)
                start: int = len(lines(fixture.calls))
                start_log: int = len(lines(fixture.log))
                invoke(binary, ["run", str(artifact)], directory, environment)
                for name, expected in example.outputs:
                    assert (directory / name).read_bytes() == expected, (
                        example.name,
                        artifact.suffix,
                        name,
                    )
                calls: list[str] = lines(fixture.calls)[start:]
                log: list[str] = lines(fixture.log)[start_log:]
                count: int = 5 if example.name == "remote-files" else 1
                assert calls.count("ssh") == count and calls.count("scp") == 0, (
                    example.name,
                    calls,
                )
                assert (
                    sum("Accepted publickey for " in line for line in log) == count
                ), (example.name, log)
                assert sum("Starting session:" in line for line in log) == count, (
                    example.name,
                    log,
                )
                reports.append(
                    {
                        "case": example.name,
                        "stage": artifact.suffix,
                        "ssh_channels": count,
                    }
                )
        (fixture.root / "native-report.json").write_text(
            json.dumps(reports, indent=2), encoding="utf-8"
        )
        print(json.dumps({"binary": str(binary), "stages": reports}, ensure_ascii=True))


if __name__ == "__main__":
    main()
