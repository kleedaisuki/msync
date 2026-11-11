"""Check that local cohorts and native copies wait only for actual hazards."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
import time
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
CASES: tuple[str, ...] = (
    "scalar",
    "cohort",
    "cohort-replace",
    "cohort-lazy",
    "cohort-lazy-dependent",
    "cohort-lazy-callback-dependent",
    "cohort-merge",
    "cohort-merge-alias",
    "cohort-merge-alias-dependent",
    "scalar-dependent",
    "cohort-dependent",
    "cohort-replace-dependent",
    "native-shared-reader",
    "native-source-dependent",
    "native-source-alias-dependent",
    "native-dot-dependent",
    "native-dotdot-dependent",
    "native-link-dotdot-dependent",
    "native-destination-dependent",
)


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
    elif kind == "cohort-replace":
        source += "let members = $std::ranges::materialize([./input.txt]);\nmembers -> ./output/.;\n"
        output = work / "output/input.txt"
    elif kind.startswith("cohort-lazy"):
        path: str = "./held.txt" if kind == "cohort-lazy-dependent" else "./input.txt"
        callback: str = "count = count + 1; p"
        if kind == "cohort-lazy-callback-dependent":
            callback = "$std::io::read_text(./held.txt); count = count + 1; p"
        source += (
            "let count = 0;\n"
            f"let members = $std::ranges::transform([{path}], fn(p) {{ {callback} }});\n"
            "members -> ./output/;\n"
            'if count != 1 { $std::io::write_text(./callback-error, "replayed"); };\n'
        )
        output = work / (
            "output/held.txt" if path == "./held.txt" else "output/input.txt"
        )
    elif kind.startswith("cohort-merge"):
        (work / "tree/sub/deep").mkdir(parents=True)
        (work / "tree/sub/deep/nested.txt").write_bytes(b"nested")
        (work / "tree/sub/input.txt").write_bytes(b"independent")
        (work / "output/tree").mkdir(parents=True)
        if kind.startswith("cohort-merge-alias"):
            (work / "alias").mkdir()
            (work / "output/tree/sub").symlink_to(
                work / "alias", target_is_directory=True
            )
            if kind.endswith("dependent"):
                source = source.replace("./held.txt", "./alias/input.txt")
        source += "let members = $std::ranges::materialize([./tree]);\nmembers -> ./output/;\n"
        output = work / "output/tree/sub/input.txt"
    elif kind == "scalar-dependent":
        source += "./held.txt -> ./output.txt;\n"
        output = work / "output.txt"
    elif kind == "cohort-dependent":
        source += "let members = $std::ranges::materialize([./held.txt]);\nmembers -> ./output/;\n"
        output = work / "output/held.txt"
    elif kind == "cohort-replace-dependent":
        source += "let members = $std::ranges::materialize([./held.txt]);\nmembers -> ./output/.;\n"
        output = work / "output/held.txt"
    elif kind == "native-shared-reader":
        source += "./input.txt -> ./held.txt;\n$std::fs::copy_file(./input.txt, ./output.txt);\n"
        output = work / "output.txt"
    elif kind == "native-source-dependent":
        source += "$std::fs::copy_file(./held.txt, ./output.txt);\n"
        output = work / "output.txt"
    elif kind in (
        "native-dot-dependent",
        "native-dotdot-dependent",
        "native-link-dotdot-dependent",
    ):
        (work / "watched").mkdir()
        observed: str = "./watched/."
        if kind != "native-dot-dependent":
            (work / "watched/child").mkdir()
            observed = "./watched/child/.."
        if kind == "native-link-dotdot-dependent":
            (work / "folder-link").symlink_to(
                work / "watched/child", target_is_directory=True
            )
            observed = "./folder-link/.."
        source = source.replace("./held.txt", "./watched/file")
        source += f'if $std::fs::is_empty("{observed}") {{ "unexpected" }} else {{ "independent" }} -> ./output.txt;\n'
        output = work / "output.txt"
    elif kind == "native-source-alias-dependent":
        (work / "source-alias").symlink_to(work / "input.txt")
        source = source.replace("./held.txt", "./input.txt")
        source += "$std::fs::copy_file(./source-alias, ./output.txt);\n"
        output = work / "output.txt"
    else:
        source += (
            "if $std::fs::copy_file(./input.txt, ./held.txt) "
            '{ "independent" -> ./output.txt; };\n'
        )
        output = work / "output.txt"
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
    reads_held: bool = kind in (
        "scalar-dependent",
        "cohort-dependent",
        "cohort-lazy-dependent",
        "cohort-replace-dependent",
        "native-source-dependent",
        "native-source-alias-dependent",
    )
    assert not (work / "callback-error").exists(), work
    if kind.startswith("cohort-merge"):
        assert (work / "output/tree/sub/deep/nested.txt").read_bytes() == b"nested", (
            work
        )
    assert output.read_bytes() == (b"held" if reads_held else b"independent"), work
    if kind in ("native-shared-reader", "native-destination-dependent"):
        assert (work / "held.txt").read_bytes() == b"independent", work
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
    parser.add_argument("--case", choices=CASES, action="append", default=[])
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
            for kind in (opts.case or CASES)
        ]
    print(json.dumps(outcomes, indent=2))


if __name__ == "__main__":
    main()
