#!/usr/bin/env python3
"""Exercise contextual I/O plans through real source, object and executable CLI routes."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import tempfile
from collections.abc import Callable, Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ROUTES = ("source", "mso", "mexe")
Report = dict[str, object]



def implementation_hashes(binary: Path) -> dict[str, str]:
    names = ("Msync.Core.dll", "Msync.Hir.dll", "Msync.Compiler.dll", "Msync.Optimization.dll", "Msync.Runtime.dll", "msync.dll")
    return {name: hashlib.sha256((binary.parent / name).read_bytes()).hexdigest()
            for name in names if (binary.parent / name).is_file()}


def invoke(binary: Path, directory: Path, arguments: Sequence[str], *, timeout: float = 40) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=timeout,
    )


def checked(binary: Path, directory: Path, arguments: Sequence[str], *, timeout: float = 40) -> subprocess.CompletedProcess[str]:
    result = invoke(binary, directory, arguments, timeout=timeout)
    if result.returncode:
        raise AssertionError((list(arguments), result.returncode, result.stdout, result.stderr))
    return result


def prepare(binary: Path, directory: Path, source: str, route: str) -> list[str]:
    script = directory / "main.msync"
    script.write_text(source, encoding="utf-8", newline="\n")
    if route == "source":
        return ["run", str(script)]
    obj = directory / "main.mso"
    checked(binary, directory, ["compile", str(script), "-O2", "-o", str(obj)])
    if route == "mso":
        return ["run-ir", str(obj)]
    executable = directory / "main.mexe"
    checked(binary, directory, ["bind-ir", str(obj), "-o", str(executable)])
    return ["run-ir", str(executable)]


def file_text(directory: Path, name: str) -> str:
    return (directory / name).read_text(encoding="utf-8")


def overwrite(binary: Path, directory: Path, route: str) -> Report:
    (directory / "first.txt").write_text("first-generation", encoding="utf-8")
    (directory / "second.txt").write_text("second-generation", encoding="utf-8")
    source = "./first.txt -> ./shared.txt;\n./second.txt -> ./shared.txt;\n"
    command = prepare(binary, directory, source, route)
    assert not (directory / "shared.txt").exists(), "Preparation executed a transfer"
    checked(binary, directory, command)
    assert file_text(directory, "shared.txt") == "second-generation"
    return {"shared": "second-generation", "source_sha256": hashlib.sha256(source.encode()).hexdigest()}


def read_observer(binary: Path, directory: Path, route: str) -> Report:
    (directory / "first.txt").write_text("first-generation", encoding="utf-8")
    (directory / "second.txt").write_text("second-generation", encoding="utf-8")
    source = (
        "./first.txt -> ./shared.txt;\n"
        "./shared.txt -> ./observed.txt;\n"
        "./second.txt -> ./shared.txt;\n"
    )
    command = prepare(binary, directory, source, route)
    checked(binary, directory, command)
    assert file_text(directory, "observed.txt") == "first-generation"
    assert file_text(directory, "shared.txt") == "second-generation"
    return {"observed": "first-generation", "shared": "second-generation"}


def result_observer(binary: Path, directory: Path, route: str) -> Report:
    (directory / "first.txt").write_text("first-generation", encoding="utf-8")
    (directory / "second.txt").write_text("second-generation", encoding="utf-8")
    source = (
        "let copied = ./first.txt -> ./shared.txt;\n"
        "copied as String -> ./copy-result.txt;\n"
        "./second.txt -> ./shared.txt;\n"
    )
    command = prepare(binary, directory, source, route)
    checked(binary, directory, command)
    assert file_text(directory, "copy-result.txt") == "true"
    assert file_text(directory, "shared.txt") == "second-generation"
    return {"copied": True, "shared": "second-generation"}


def directory_observer(binary: Path, directory: Path, route: str) -> Report:
    (directory / "first.txt").write_text("first-generation", encoding="utf-8")
    (directory / "second.txt").write_text("second-generation", encoding="utf-8")
    (directory / "tree").mkdir()
    (directory / "tree" / "existing.txt").write_text("existing-member", encoding="utf-8")
    source = (
        "./first.txt -> ./tree/value.txt;\n"
        "./tree -> ./observed;\n"
        "./second.txt -> ./tree/value.txt;\n"
    )
    command = prepare(binary, directory, source, route)
    checked(binary, directory, command)
    assert file_text(directory, "observed/value.txt") == "first-generation"
    assert file_text(directory, "tree/value.txt") == "second-generation"
    assert file_text(directory, "observed/existing.txt") == "existing-member"
    return {"observed/value.txt": "first-generation", "tree/value.txt": "second-generation",
            "observed/existing.txt": "existing-member"}



def produced_selection(binary: Path, directory: Path, route: str) -> Report:
    inputs = directory / "inputs"
    inputs.mkdir()
    for filename, contents in (("first.pick", "first"), ("second.pick", "second"), ("ignored.txt", "ignored")):
        (inputs / filename).write_text(contents, encoding="utf-8")
    source = "./inputs -> ./produced;\n./produced/<.+\\.pick> -> ./selected/;\n"
    command = prepare(binary, directory, source, route)
    assert not (directory / "produced").exists(), "Preparation executed the namespace producer"
    checked(binary, directory, command)
    selected = directory / "selected"
    actual = sorted(path.name for path in selected.iterdir())
    assert actual == ["first.pick", "second.pick"], actual
    assert file_text(selected, "first.pick") == "first"
    assert file_text(selected, "second.pick") == "second"
    return {"selected": ["first.pick", "second.pick"], "produced_namespace_before_selection": True}


def directory_merge(binary: Path, directory: Path, route: str) -> Report:
    first = directory / "first" / "tree"
    second = directory / "second" / "tree"
    first.mkdir(parents=True)
    second.mkdir(parents=True)
    (first / "shared.txt").write_text("first", encoding="utf-8")
    (first / "first-only.txt").write_text("retained", encoding="utf-8")
    (second / "shared.txt").write_text("second", encoding="utf-8")
    (second / "second-only.txt").write_text("new", encoding="utf-8")
    command = prepare(binary, directory, "./first/tree -> ./merged/;\n./second/tree -> ./merged/;\n", route)
    checked(binary, directory, command)
    merged = directory / "merged" / "tree"
    expected = {"first-only.txt": "retained", "second-only.txt": "new", "shared.txt": "second"}
    assert {path.name: path.read_text(encoding="utf-8") for path in merged.iterdir()} == expected
    return {"merged": expected}



def namespace_entry(binary: Path, directory: Path, route: str) -> Report:
    (directory / "first.txt").write_text("first", encoding="utf-8")
    (directory / "second.txt").write_text("second", encoding="utf-8")
    target = directory / "target-real.txt"
    target.write_text("original", encoding="utf-8")
    link = directory / "target-link.txt"
    link.symlink_to(target)
    source = "./first.txt -> ./target-link.txt;\n./second.txt -> ./target-real.txt;\n"
    command = prepare(binary, directory, source, route)
    checked(binary, directory, command)
    assert not link.is_symlink(), "Atomic publication must replace the destination entry, not its referent"
    assert link.read_text(encoding="utf-8") == "first", link.read_text(encoding="utf-8")
    assert target.read_text(encoding="utf-8") == "second", target.read_text(encoding="utf-8")
    return {"target-link.txt": "first", "target-real.txt": "second", "destination_entry_replaced": True}




def namespace_read(binary: Path, directory: Path, route: str) -> Report:
    payload = b"replacement-data\n" * (512 * 1024)
    (directory / "first.txt").write_bytes(payload)
    referent = directory / "target-real.txt"
    referent.write_text("original referent", encoding="utf-8")
    link = directory / "target-link.txt"
    link.symlink_to(referent)
    source = (
        "let io = $std::io;\n"
        "./first.txt -> ./target-link.txt;\n"
        "io::read_text(./target-link.txt) -> ./observed.txt;\n"
    )
    command = prepare(binary, directory, source, route)
    checked(binary, directory, command)
    observed = (directory / "observed.txt").read_bytes()
    assert observed == payload, (len(observed), len(payload), observed[:80])
    assert referent.read_text(encoding="utf-8") == "original referent"
    return {"observed_sha256": hashlib.sha256(observed).hexdigest(), "observed_bytes": len(observed),
            "original_referent_unchanged": True}


def loop_invocations(binary: Path, directory: Path, route: str) -> Report:
    source = (
        'let prefix = "./first-";\n'
        "fn publish(start: Number) {\n"
        "    let index = start;\n"
        "    loop {\n"
        "        if index == start + 2 { break; };\n"
        "        let destination = $std::string::concat(prefix, index as String) as Path;\n"
        "        index as String -> destination;\n"
        "        index = index + 1;\n"
        "    }\n"
        "};\n"
        "publish(0);\n"
        'prefix = "./second-";\n'
        "publish(3);\n"
    )
    command = prepare(binary, directory, source, route)
    checked(binary, directory, command, timeout=10)
    expected = {"first-0": "0", "first-1": "1", "second-3": "3", "second-4": "4"}
    actual = {path.name: path.read_text(encoding="utf-8") for path in directory.iterdir()
              if path.name.startswith(("first-", "second-"))}
    assert actual == expected, actual
    return {"outputs": actual, "distinct_calls_and_captures": True, "terminated": True}


def frozen_selection(binary: Path, directory: Path, route: str) -> Report:
    assert route == "mexe"
    inputs = directory / "inputs"
    inputs.mkdir()
    (inputs / "first.pick").write_text("first", encoding="utf-8")
    (inputs / "second.pick").write_text("second", encoding="utf-8")
    (inputs / "ignored.txt").write_text("ignored", encoding="utf-8")
    command = prepare(binary, directory, "./inputs/<.+\\.pick> -> ./selected/;\n", route)
    assert not (directory / "selected").exists(), "Preparation published selected files"
    (inputs / "later.pick").write_text("not in the submitted plan", encoding="utf-8")
    checked(binary, directory, command)
    selected = directory / "selected"
    actual = sorted(path.name for path in selected.iterdir())
    assert actual == ["first.pick", "second.pick"], actual
    assert file_text(selected, "first.pick") == "first"
    assert file_text(selected, "second.pick") == "second"
    checked(binary, directory, command)
    actual = sorted(path.name for path in selected.iterdir())
    assert actual == ["first.pick", "second.pick"], actual
    return {"selected": ["first.pick", "second.pick"], "submissions": 2, "later_match_unselected": True}



def known_metadata_branch(binary: Path, directory: Path, route: str) -> Report:
    assert route == "mexe"
    marker = directory / "choose-first"
    marker.write_text("selected when contextualized", encoding="utf-8")
    (directory / "first.txt").write_text("first", encoding="utf-8")
    (directory / "second.txt").write_text("second", encoding="utf-8")
    source = (
        "if exists ./choose-first { ./first.txt -> ./chosen.txt; } "
        "else { ./second.txt -> ./chosen.txt; };\n"
    )
    command = prepare(binary, directory, source, route)
    assert not (directory / "chosen.txt").exists(), "Preparation executed a selected transfer"
    marker.unlink()
    checked(binary, directory, command)
    assert file_text(directory, "chosen.txt") == "first", file_text(directory, "chosen.txt")
    return {"chosen": "first", "backend_computed_metadata_branch": True}


def resubmit_error(binary: Path, directory: Path, route: str) -> Report:
    assert route == "mexe"
    parent = directory / "output-root"
    parent.mkdir()
    command = prepare(binary, directory, '"payload" -> ./output-root/result.txt;\n', route)
    assert not (parent / "result.txt").exists(), "Preparation performed output I/O"
    parent.rmdir()
    parent.write_text("parent is now a file", encoding="utf-8")
    failed = invoke(binary, directory, command)
    assert failed.returncode != 0, (failed.stdout, failed.stderr)
    assert "output-root" in failed.stderr, failed.stderr
    assert "stale plan" not in failed.stderr.lower(), failed.stderr
    assert "expired plan" not in failed.stderr.lower(), failed.stderr
    assert parent.read_text(encoding="utf-8") == "parent is now a file"
    parent.unlink()
    parent.mkdir()
    checked(binary, directory, command)
    assert file_text(parent, "result.txt") == "payload"
    return {"failure": failed.stderr, "retry": "payload", "submissions": 2}


CASES: dict[str, Callable[[Path, Path, str], Report]] = {
    "overwrite": overwrite,
    "read-observer": read_observer,
    "result-observer": result_observer,
    "directory-observer": directory_observer,
    "directory-merge": directory_merge,
    "produced-selection": produced_selection,
    "namespace-entry": namespace_entry,
    "namespace-read": namespace_read,
    "loop-invocations": loop_invocations,
    "frozen-selection": frozen_selection,
    "resubmit-error": resubmit_error,
    "known-metadata-branch": known_metadata_branch,
}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--case", action="append", choices=tuple(CASES), default=[])
    parser.add_argument("--route", action="append", choices=ROUTES, default=[])
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    scratch = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="contextual-io-journey-", dir=scratch))
    selected_cases: list[str] = arguments.case or list(CASES)
    selected_routes: list[str] = arguments.route or list(ROUTES)
    observations: list[Report] = []
    failures = 0
    for name in selected_cases:
        for route in selected_routes:
            if name in ("frozen-selection", "resubmit-error", "known-metadata-branch") and route != "mexe":
                continue
            work = directory / f"{name}-{route}"
            work.mkdir()
            record: Report = {"case": name, "route": route}
            try:
                record.update(CASES[name](binary, work, route))
                record["status"] = "passed"
            except (AssertionError, OSError, subprocess.TimeoutExpired) as error:
                failures += 1
                record.update(status="failed", error=str(error))
            observations.append(record)
    report: Report = {"binary": str(binary), "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
                      "implementation_sha256": implementation_hashes(binary),
                      "directory": str(directory), "status": "failed" if failures else "passed", "observations": observations}
    output: Path = arguments.output or directory / "report.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2, ensure_ascii=False))
    raise SystemExit(1 if failures else 0)


if __name__ == "__main__":
    main()
