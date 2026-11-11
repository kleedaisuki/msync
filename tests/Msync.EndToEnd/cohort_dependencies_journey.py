"""Check that finite local cohorts wait only for actual producers."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
import time
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]


def command(binary: Path, work: Path, *args: str) -> str:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *args],
        cwd=work,
        check=True,
        capture_output=True,
        text=True,
        timeout=30,
    ).stdout


def wait_for(path: Path, seconds: float) -> bool:
    deadline: float = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if path.exists():
            return True
        time.sleep(0.005)
    return path.exists()


def run(binary: Path, parent: Path, route: str, kind: str) -> dict[str, object]:
    work: Path = parent / f"{route}-{kind}"
    work.mkdir(parents=True, exist_ok=False)
    source: str = """
let child = $std::process::spawn(/bin/sh, ["./wait.sh"]);
child::stdout -> ./held.txt;
"""
    if kind == "scalar":
        source += "./input.txt -> ./output.txt;\n"
        output: Path = work / "output.txt"
    elif kind == "cohort":
        source += "let members = $std::ranges::materialize([./input.txt]);\nmembers -> ./output/;\n"
        output = work / "output/input.txt"
    elif kind == "scalar-dependent":
        source += "./held.txt -> ./output.txt;\n"
        output = work / "output.txt"
    else:
        source += "let members = $std::ranges::materialize([./held.txt]);\nmembers -> ./output/;\n"
        output = work / "output/held.txt"
    source += "acquire child;\n"
    (work / "input.txt").write_bytes(b"independent")
    (work / "wait.sh").write_text(
        "set -eu\n: > child-ready\nwhile [ ! -e gate ]; do sleep .01; done\nprintf held\n"
    )
    (work / "case.msync").write_text(source)
    if route != "msync":
        command(binary, work, "compile", "case.msync", "-o", "case.mso")
    if route == "mexe":
        command(binary, work, "bind-ir", "case.mso", "-o", "case.mexe")
        data: dict[str, object] = json.loads(
            command(binary, work, "disassemble", "case.mexe", "--json")
        )
        assert not data["executionPlans"], data["executionPlans"]
    started: float = time.monotonic()
    child: subprocess.Popen[str] = subprocess.Popen(
        [str(binary), "--no-config", "--no-telemetry", "run", f"case.{route}"],
        cwd=work,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    try:
        assert wait_for(work / "child-ready", 10), (child.poll(), work)
        before_release: bool = wait_for(output, 0.5)
        assert not (work / "held.txt").exists(), work
    finally:
        (work / "gate").touch()
        stdout, stderr = child.communicate(timeout=15)
    assert child.returncode == 0, (stdout, stderr, work)
    assert output.read_bytes() == (
        b"held" if kind.endswith("dependent") else b"independent"
    ), work
    assert before_release == (not kind.endswith("dependent")), (route, kind, work)
    return {
        "route": route,
        "kind": kind,
        "output_before_gate": before_release,
        "elapsed_ms": round((time.monotonic() - started) * 1000, 2),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    opts: argparse.Namespace = parser.parse_args()
    binary: Path = opts.binary.resolve()
    scratch = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="cohort-dependencies-", dir=scratch
    ) as work:
        outcomes = [
            run(binary, Path(work), route, kind)
            for route in ("msync", "mso", "mexe")
            for kind in ("scalar", "cohort", "scalar-dependent", "cohort-dependent")
        ]
    print(json.dumps(outcomes, indent=2))


if __name__ == "__main__":
    main()
