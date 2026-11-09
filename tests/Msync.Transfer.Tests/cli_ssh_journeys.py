#!/usr/bin/env python3
"""Verify compiled source/object/executable selector journeys over native OpenSSH."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "support"))
from ssh_fixture import Fixture, lines


def quoted(value: str) -> str:
    return '"' + value.replace('"', '""') + '"'


def invoke(
    binary: Path, arguments: list[str], directory: Path, environment: dict[str, str]
) -> subprocess.CompletedProcess[bytes]:
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
            f"{arguments!r} failed ({result.returncode}): {result.stderr.decode(errors='replace')}"
        )
    return result


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve()
    reports: list[dict[str, Any]] = []
    with Fixture(ROOT / ".temp" / "cli-transfer-ssh") as fixture:
        environment: dict[str, str] = fixture.environment()
        for name in ("deferred-selector", "recursive-native"):
            directory: Path = fixture.root / name
            remote: Path = directory / "remote"
            remote.mkdir(parents=True)
            if name == "deferred-selector":
                inputs: dict[str, Path] = {
                    "a.pick": remote / "a.pick",
                    "b.pick": remote / "b.pick",
                }
                (remote / "skip.pick").write_bytes(b"must remain unselected")
                body: str = f"remote::{quoted(str(remote))}/<(?s)^(?!skip).+\\.pick$>"
            else:
                (remote / "nested" / "deeper").mkdir(parents=True)
                inputs = {
                    "a.pick": remote / "a.pick",
                    "deep.pick": remote / "nested" / "deep.pick",
                }
                (remote / "nested" / "deeper" / "z.pick").write_bytes(
                    b"beyond demanded prefix"
                )
                body = f"$std::fs::recursive_directory_iterator(remote::{quoted(str(remote))})"
            for target in inputs.values():
                target.write_bytes(b"initial")
            source: Path = directory / "main.msync"
            source.write_text(
                "let remote = ssh://Perf_Fixture;\n"
                f"fn candidates() {{ {body} }};\n"
                "candidates() | $std::ranges::filter(fn(file: Path) { $std::fs::is_regular_file(file) }) "
                "| $std::ranges::take(2) -> ./selected/;\n"
            )
            object_file: Path = directory / "main.mso"
            executable: Path = directory / "main.mexe"
            before_compile: int = len(lines(fixture.calls))
            invoke(
                binary,
                ["compile", str(source), "-o", str(object_file)],
                directory,
                environment,
            )
            invoke(
                binary,
                ["bind-ir", str(object_file), "-o", str(executable)],
                directory,
                environment,
            )
            assert len(lines(fixture.calls)) == before_compile, (
                "compile/bind authenticated an endpoint"
            )
            for stage, artifact in (
                ("source", source),
                ("mso", object_file),
                ("mexe", executable),
            ):
                execution_directory: Path = (
                    directory if stage == "source" else directory / f"execute-{stage}"
                )
                execution_directory.mkdir(exist_ok=True)
                expected_root: Path = (
                    execution_directory / "selected"
                    if stage != "mexe"
                    else directory / "selected"
                )
                expected: dict[str, bytes] = {
                    filename: f"{name}:{stage}:{filename}".encode()
                    for filename in inputs
                }
                for filename, target in inputs.items():
                    target.write_bytes(expected[filename])
                before_calls: int = len(lines(fixture.calls))
                before_log: int = len(lines(fixture.log))
                result: subprocess.CompletedProcess[bytes] = invoke(
                    binary, ["run", str(artifact)], execution_directory, environment
                )
                calls: list[str] = lines(fixture.calls)[before_calls:]
                log: list[str] = lines(fixture.log)[before_log:]
                report: dict[str, Any] = {
                    "journey": name,
                    "stage": stage,
                    "ssh_clients": calls.count("ssh"),
                    "scp_clients": calls.count("scp"),
                    "authentications": sum(
                        "Accepted publickey for " in line for line in log
                    ),
                    "remote_sessions": sum("Starting session:" in line for line in log),
                    "stderr": result.stderr.decode(),
                }
                assert (
                    report["ssh_clients"]
                    == report["authentications"]
                    == report["remote_sessions"]
                    == 1
                ), report
                assert report["scp_clients"] == 0, report
                assert {entry.name for entry in expected_root.iterdir()} == set(
                    expected
                ), report
                for filename, content in expected.items():
                    assert (expected_root / filename).read_bytes() == content, report
                if stage == "mexe":
                    assert not (execution_directory / "selected").exists(), (
                        "bound executable relocated its output context"
                    )
                reports.append(report)
        result_report: dict[str, Any] = {
            "binary": str(binary),
            "fixture": str(fixture.root),
            "journeys": reports,
        }
        (fixture.root / "cli-result.json").write_text(
            json.dumps(result_report, indent=2) + "\n"
        )
        print(json.dumps(result_report, indent=2))


if __name__ == "__main__":
    main()
