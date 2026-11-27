#!/usr/bin/env python3
"""Exercise HPIR directory creation through the real CLI."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import tempfile
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
PROGRAM: str = """\
let fs = $std::fs;
fn report(value: Bool) {
    $std::io::println(if value { "created" } else { "existing" });
};
report(fs::create_directories(./nested/leaf));
report(fs::create_directories("nested/leaf"));
let destination = if exists ./choose-path { ./from-path/leaf } else { "from-text/leaf" };
report(fs::create_directories(destination));
"""
Row = dict[str, Any]


def invoke(
    binary: Path, cwd: Path, *args: str, status: int = 0, stdin: str | None = None
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        [str(binary), "--no-config", "--color", "never", *args],
        cwd=cwd,
        input=stdin,
        capture_output=True,
        encoding="utf-8",
        timeout=30,
        check=False,
    )
    assert result.returncode == status, (args, result.stdout, result.stderr)
    return result


def trace_rows(path: Path) -> list[Row]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def runs(binary: Path, work: Path) -> None:
    work.mkdir()
    source = work / "directories.msync"
    source.write_text(PROGRAM, encoding="utf-8")
    dry = invoke(binary, work, "run", source.name, "--dry-run", "--no-telemetry")
    assert dry.stdout.splitlines() == ["created"] * 3, dry
    assert not (work / "nested").exists() and not (work / "from-text").exists()

    live = invoke(
        binary,
        work,
        "run",
        source.name,
        "--trace",
        "create.jsonl",
        "--telemetry-level",
        "diagnostics",
    )
    assert live.stdout.splitlines() == ["created", "existing", "created"], live
    assert (work / "nested/leaf").is_dir() and (work / "from-text/leaf").is_dir()
    rows = trace_rows(work / "create.jsonl")
    assert any(
        row["metrics"].get("paths.create_directories_sites", 0) > 0 for row in rows
    )
    results = [
        row["attributes"]["file.outcome"]
        for row in rows
        if row["name"] == "hpir.directory_create.host"
    ]
    assert results == ["created", "existing", "created"], rows

    (work / "path.txt").write_text("from-input/leaf", encoding="utf-8")
    (work / "deferred.msync").write_text(
        "let path = $std::io::read_text(./path.txt);\n"
        "$std::fs::create_directories(path);\n",
        encoding="utf-8",
    )
    invoke(binary, work, "run", "deferred.msync", "--no-telemetry")
    assert (work / "from-input/leaf").is_dir()

    invoke(
        binary,
        work,
        "compile",
        source.name,
        "--emit",
        "hir",
        "-o",
        "portable.mhir",
        "--no-telemetry",
    )
    invoke(binary, work, "compile", source.name, "-o", "portable.mso", "--no-telemetry")
    source.unlink()
    detached = work / "detached"
    detached.mkdir()
    (detached / "choose-path").touch()
    invoke(
        binary,
        detached,
        "bind-ir",
        "../portable.mso",
        "-o",
        "bound.mexe",
        "--no-telemetry",
    )
    assert not (detached / "nested").exists() and not (detached / "from-path").exists()
    for image in ("../portable.mhir", "../portable.mso", "bound.mexe"):
        shutil.rmtree(detached / "nested", ignore_errors=True)
        shutil.rmtree(detached / "from-path", ignore_errors=True)
        execution = invoke(binary, detached, "run", image, "--no-telemetry")
        assert execution.stdout.splitlines() == ["created", "existing", "created"], (
            execution
        )
        assert (detached / "nested/leaf").is_dir()
        assert (detached / "from-path/leaf").is_dir() and not (
            detached / "from-text"
        ).exists()


def failure_and_recovery(binary: Path, work: Path) -> None:
    work.mkdir()
    (work / "seed.txt").write_bytes(b"preserved")
    operation = "$std::fs::create_directories(./blocked/child)"
    (work / "collision.msync").write_text(
        f"./seed.txt -> ./blocked;\n{operation};\n", encoding="utf-8"
    )
    failure = invoke(
        binary,
        work,
        "run",
        "collision.msync",
        "--trace",
        "failure.jsonl",
        "--telemetry-level",
        "diagnostics",
        status=1,
    )
    assert "error[E0300]" in failure.stderr and f"{operation};" in failure.stderr, (
        failure
    )
    assert (
        "this runtime operation failed" in failure.stderr
        and "caused by:" in failure.stderr
    )
    assert (work / "blocked").read_bytes() == b"preserved"
    rows = trace_rows(work / "failure.jsonl")
    failed = next(row for row in rows if row["name"] == "hpir.directory_create.apply")
    assert failed["status"] == "error" and failed["error_chain"], failed
    assert failed["attributes"]["file.operation"] == "fs.create_directories", failed
    assert failed["attributes"]["source.file"].endswith("collision.msync"), failed
    assert failed["attributes"]["diagnostic.code"] == "E0300", failed
    assert int(failed["attributes"]["source.offset"]) == len(
        "./seed.txt -> ./blocked;\n"
    ), failed
    assert int(failed["attributes"]["source.length"]) == len(operation), failed

    definition = f"fn create() {{ {operation} }};"
    repl = invoke(
        binary,
        work,
        "repl",
        "--no-telemetry",
        stdin=f"{definition}\ncreate()\n$std::fs::create_directories(./recovered/leaf)\n/quit\n",
    )
    assert "error[E0300]" in repl.stderr and "<repl:1>:1:" in repl.stderr, repl
    assert (
        definition in repl.stderr and "this runtime operation failed" in repl.stderr
    ), repl
    assert repl.stdout.splitlines() == ["true"] and (work / "recovered/leaf").is_dir()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    binary = args.binary.resolve()
    with tempfile.TemporaryDirectory(
        prefix="directory-creation-", dir=ROOT / ".temp"
    ) as temporary:
        root = Path(temporary)
        runs(binary, root / "runs")
        failure_and_recovery(binary, root / "failure")
    print("PASS live/detached mkdir, dry-run, source diagnostics and REPL recovery")


if __name__ == "__main__":
    main()
