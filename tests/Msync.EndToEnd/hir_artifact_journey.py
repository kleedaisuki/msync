#!/usr/bin/env python3
"""Export semantic HIR, remove source, then inspect/lower/execute that artifact."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Case:
    name: str
    source: str
    expected: dict[str, bytes]


CASES = (
    Case(
        "generic_control",
        """fn choose(file: Path, ready: Bool) -> Path {
    if ready { file } else { ./unused }
};
let destination = choose(./selected.txt, true);
"Klee" -> destination;
""",
        {"selected.txt": b"Klee"},
    ),
    Case(
        "captured_locations",
        """fn counter(initial: Number) {
    let value = initial;
    scope {
        get: fn { value },
        set: fn(next: Number) { value = next; },
    }
};
let first = counter(3);
let second = counter(4);
first::set(9);
(first::get() as String) -> ./first.txt;
(second::get() as String) -> ./second.txt;
""",
        {"first.txt": b"9", "second.txt": b"4"},
    ),
    Case(
        "concept_and_metadata",
        """concept Named { name: String, };
fn accept(value: Named) -> Named { value };
accept(scope { name: "HIR" })::name -> ./concept.txt;
let callable = fn(value: Number) { value + 1 };
($std::meta::is_invocable(callable, 2) as String) -> ./query.txt;
""",
        {"concept.txt": b"HIR", "query.txt": b"true"},
    ),
)


def invoke(
    binary: Path,
    cwd: Path,
    arguments: list[str],
    *,
    success: bool = True,
    stdin: bytes | None = None,
) -> subprocess.CompletedProcess[bytes]:
    completed = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", "--color", "never", *arguments],
        cwd=cwd,
        input=stdin,
        capture_output=True,
        check=False,
        timeout=60,
    )
    if (completed.returncode == 0) != success:
        raise AssertionError(
            (
                arguments,
                completed.returncode,
                completed.stdout.decode("utf-8"),
                completed.stderr.decode("utf-8"),
            )
        )
    return completed


def check_outputs(directory: Path, case: Case) -> int:
    for name, expected in case.expected.items():
        actual = (directory / name).read_bytes()
        if actual != expected:
            raise AssertionError((case.name, name, expected, actual))
    return len(case.expected)


def no_outputs(directory: Path, case: Case) -> None:
    for name in case.expected:
        if (directory / name).exists():
            raise AssertionError(
                (case.name, "compile or inspection executed effects", name)
            )


def run_case(binary: Path, directory: Path, case: Case, level: int) -> dict[str, Any]:
    directory.mkdir(parents=True)
    source = directory / "语义 source 𐐀.msync"
    source.write_text(case.source, encoding="utf-8", newline="")
    hir = directory / "program.mhir"
    direct = directory / "direct.mso"
    lowered = directory / "lowered.mso"
    canonical = directory / "canonical.mhir"
    executable = directory / "program.mexe"
    invoke(binary, directory, ["compile", str(source), "--emit", "hir", "-o", str(hir)])
    raw = hir.read_bytes()
    if not raw.startswith(b"MSYNHIR\x00"):
        raise AssertionError("HIR export is not the semantic binary format")
    invoke(binary, directory, ["compile", str(source), f"-O{level}", "-o", str(direct)])
    no_outputs(directory, case)
    source.unlink()

    document: dict[str, Any] = json.loads(
        invoke(binary, directory, ["disassemble", str(hir), "--json"]).stdout
    )
    if document["artifactKind"] != "hir" or document["formatVersion"] != 1:
        raise AssertionError(document)
    if document["schemaVersion"] != 1 or document["languageVersion"] != "0.8.0":
        raise AssertionError(document)
    if document.get("optimizationLevel") is not None:
        raise AssertionError(
            "The semantic artifact was described as optimized bytecode"
        )
    if "@n" not in document["text"] or not document["symbols"]:
        raise AssertionError("The HIR view has no semantic structure")
    invoke(
        binary, directory, ["compile", str(hir), "--emit", "hir", "-o", str(canonical)]
    )
    if canonical.read_bytes() != raw:
        raise AssertionError("Semantic file read/write changed the artifact")
    invoke(binary, directory, ["compile", str(hir), f"-O{level}", "-o", str(lowered)])
    if lowered.read_bytes() != direct.read_bytes():
        raise AssertionError(
            "Loaded HIR and direct source lowering produced different bytecode"
        )
    invoke(
        binary, directory, ["bind-ir", str(hir), f"-O{level}", "-o", str(executable)]
    )
    no_outputs(directory, case)

    damaged = directory / "damaged.mhir"
    damaged.write_bytes(raw[:8] + b"\xff\xff" + raw[10:])
    failure = invoke(binary, directory, ["disassemble", str(damaged)], success=False)
    if b"HIR" not in failure.stderr:
        raise AssertionError(failure.stderr)
    no_outputs(directory, case)

    observations = 0
    invoke(binary, directory, ["run-ir", str(lowered)])
    observations += check_outputs(directory, case)
    for name in case.expected:
        (directory / name).unlink()
    other = directory / "other cwd"
    other.mkdir()
    invoke(binary, other, ["run-ir", str(executable)])
    observations += check_outputs(directory, case)
    for name in case.expected:
        (directory / name).unlink()
    invoke(binary, other, ["run", str(hir), f"-O{level}"])
    observations += check_outputs(other, case)
    if hir.read_bytes() != raw:
        raise AssertionError("Lowering or execution modified its semantic input")
    return {
        "case": case.name,
        "optimization": level,
        "source_deleted": not source.exists(),
        "byte_observations": observations,
        "hir_sha256": hashlib.sha256(raw).hexdigest(),
        "hir_bytes": len(raw),
        "bytecode_sha256": hashlib.sha256(lowered.read_bytes()).hexdigest(),
        "symbols": len(document["symbols"]),
        "references": len(document["references"]),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--work-root", type=Path)
    parser.add_argument("--levels", default="0,2,3")
    options = parser.parse_args()
    binary = options.binary.resolve()
    levels = [int(value) for value in options.levels.split(",")]
    base = options.work_root or ROOT / ".temp"
    base.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="hir-artifacts-", dir=base) as temporary:
        root = Path(temporary)
        version = invoke(binary, root, ["--version"]).stdout.decode("utf-8").strip()
        routes = [
            run_case(binary, root / f"O{level}-{case.name}", case, level)
            for level in levels
            for case in CASES
        ]
    report: dict[str, Any] = {
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "managed_assemblies": {
            path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(binary.parent.glob("*.dll"))
            if path.name == "msync.dll" or path.name.startswith("Msync.")
        },
        "product": version,
        "routes": routes,
    }
    text = json.dumps(report, ensure_ascii=True, indent=2) + "\n"
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(text, encoding="utf-8")
    print(text, end="")


if __name__ == "__main__":
    main()
