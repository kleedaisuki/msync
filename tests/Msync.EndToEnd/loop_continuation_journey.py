#!/usr/bin/env python3
"""Keep loop continuation effects and failures intact when empty preparation is skipped."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
ROUTES: tuple[str, ...] = ("source", "mso", "mexe")
COUNTS: tuple[int, ...] = (0, 1, 32)
Report = dict[str, object]

BUILD: str = """
let count = $std::io::read_text(./count.txt) as Number;
fn build(length: Number) {
    let records = $std::storage::Buffer::new([]);
    for index in $std::ranges::iota(0, length) {
        let path = $std::string::concat("./item-", index as String) as Path;
        $std::storage::Buffer::push(records, scope {
            index: index, path: path, label: "kept", flag: true,
        });
    };
    $std::storage::Buffer::snapshot(records)
};
let rows = build(count);
"""

REFERENCE_SCAN: str = BUILD + """
let total = 0;
for row in rows {
    if row::label == "kept" and row::flag { total = total + 1; };
};
(total as String) -> ./total.txt;
"""

METADATA_SCAN: str = BUILD + """
let existing = 0;
let total = 0;
for row in rows {
    if exists row::path {
        existing = existing + 1;
    } else {
        (row::index as String) -> row::path;
    };
    release;
    acquire;
    if exists row::path { total = total + 1; };
};
(existing as String) -> ./existing.txt;
(total as String) -> ./total.txt;
"""

PUBLISHED_READ: str = "./input.txt -> ./published.txt;\n" + BUILD + """
for row in rows {
    $std::io::read_text(./published.txt) -> row::path;
};
acquire;
"""

MID_LOOP_FAILURE: str = BUILD + """
for row in rows {
    let text = $std::io::read_text(row::path);
    let number = text as Number;
    let destination = $std::string::concat("./done-", row::index as String) as Path;
    (number as String) -> destination;
    acquire;
};
"unreachable" -> ./after.txt;
"""


def invoke(
    binary: Path, work: Path, arguments: Sequence[str]
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=work,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=60,
    )


def checked(binary: Path, work: Path, arguments: Sequence[str]) -> None:
    result: subprocess.CompletedProcess[str] = invoke(binary, work, arguments)
    if result.returncode:
        raise AssertionError(
            (list(arguments), result.returncode, result.stdout, result.stderr)
        )


def prepare(binary: Path, work: Path, source: str, route: str) -> list[str]:
    script: Path = work / "loop.msync"
    script.write_text(source, encoding="utf-8", newline="\n")
    if route == "source":
        return ["run", str(script), "-O2"]
    artifact: Path = work / "loop.mso"
    checked(binary, work, ["compile", str(script), "-O2", "-o", str(artifact)])
    if route == "mexe":
        executable: Path = work / "loop.mexe"
        checked(binary, work, ["bind-ir", str(artifact), "-o", str(executable)])
        artifact = executable
    script.unlink()
    return ["run-ir", str(artifact)]


def assert_text(work: Path, filename: str, expected: str) -> None:
    actual: str = (work / filename).read_text(encoding="utf-8")
    assert actual == expected, (filename, expected, actual)


def run_scan(binary: Path, work: Path, route: str, count: int, metadata: bool) -> Report:
    work.mkdir()
    (work / "count.txt").write_text(str(count), encoding="utf-8")
    source: str = METADATA_SCAN if metadata else REFERENCE_SCAN
    command: list[str] = prepare(binary, work, source, route)
    assert not (work / "total.txt").exists(), "Preparation wrote the scan result."
    assert not any(work.glob("item-*")), "Preparation executed loop transfers."
    checked(binary, work, command)
    assert_text(work, "total.txt", str(count))
    if metadata:
        assert_text(work, "existing.txt", "0")
        for index in range(count):
            assert_text(work, f"item-{index}", str(index))
    return {
        "count": count,
        "total": count,
        "source_sha256": hashlib.sha256(source.encode()).hexdigest(),
    }


def run_published_read(binary: Path, work: Path, route: str) -> Report:
    work.mkdir()
    payload: bytes = b"published before loop reads\n" * (128 * 1024)
    (work / "input.txt").write_bytes(payload)
    (work / "count.txt").write_text("2", encoding="utf-8")
    command: list[str] = prepare(binary, work, PUBLISHED_READ, route)
    assert not (work / "published.txt").exists(), "Preparation published input data."
    assert not any(work.glob("item-*")), "Preparation executed loop reads."
    checked(binary, work, command)
    assert (work / "published.txt").read_bytes() == payload
    for index in range(2):
        assert (work / f"item-{index}").read_bytes() == payload
    return {
        "payload_bytes": len(payload),
        "payload_sha256": hashlib.sha256(payload).hexdigest(),
        "source_sha256": hashlib.sha256(PUBLISHED_READ.encode()).hexdigest(),
    }


def run_failure(binary: Path, work: Path, route: str) -> Report:
    work.mkdir()
    (work / "count.txt").write_text("4", encoding="utf-8")
    for index, text in enumerate(("17", "19", "not-a-number", "23")):
        (work / f"item-{index}").write_text(text, encoding="utf-8")
    command: list[str] = prepare(binary, work, MID_LOOP_FAILURE, route)
    assert not any(work.glob("done-*")), "Preparation performed prior loop writes."
    result: subprocess.CompletedProcess[str] = invoke(binary, work, command)
    assert result.returncode != 0, "The selected invalid Number conversion succeeded."
    assert result.stderr.strip(), "The concrete loop failure had no diagnostic."
    assert_text(work, "done-0", "17")
    assert_text(work, "done-1", "19")
    assert not (work / "done-2").exists(), "The failing iteration published output."
    assert not (work / "done-3").exists(), "Execution continued after the failure."
    assert not (work / "after.txt").exists(), "The unreachable tail executed."
    return {
        "completed": ["done-0", "done-1"],
        "failure_exit": result.returncode,
        "diagnostic": result.stderr,
        "source_sha256": hashlib.sha256(MID_LOOP_FAILURE.encode()).hexdigest(),
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--route", action="append", choices=ROUTES, default=[])
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    directory: Path = Path(tempfile.mkdtemp(prefix="loop-continuation-", dir=scratch))
    routes: list[str] = arguments.route or list(ROUTES)
    observations: list[Report] = []
    failures: int = 0
    for route in routes:
        for metadata in (False, True):
            for count in COUNTS:
                name: str = "metadata" if metadata else "references"
                record: Report = {"case": name, "route": route, "count": count}
                work: Path = directory / f"{name}-{count}-{route}"
                try:
                    record.update(run_scan(binary, work, route, count, metadata))
                    record["status"] = "passed"
                except (AssertionError, OSError, subprocess.TimeoutExpired) as error:
                    failures += 1
                    record.update(status="failed", error=str(error))
                observations.append(record)
        for name in ("published-read", "mid-loop-failure"):
            record = {"case": name, "route": route}
            work = directory / f"{name}-{route}"
            try:
                record.update(
                    run_published_read(binary, work, route)
                    if name == "published-read"
                    else run_failure(binary, work, route)
                )
                record["status"] = "passed"
            except (AssertionError, OSError, subprocess.TimeoutExpired) as error:
                failures += 1
                record.update(status="failed", error=str(error))
            observations.append(record)
    report: Report = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "implementation_sha256": {
            name: hashlib.sha256((binary.parent / name).read_bytes()).hexdigest()
            for name in ("Msync.Core.dll", "Msync.Runtime.dll", "msync.dll")
            if (binary.parent / name).is_file()
        },
        "directory": str(directory),
        "status": "failed" if failures else "passed",
        "observations": observations,
    }
    output: Path = arguments.output or directory / "report.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    raise SystemExit(1 if failures else 0)


if __name__ == "__main__":
    main()
