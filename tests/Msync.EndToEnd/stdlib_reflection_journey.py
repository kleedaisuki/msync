#!/usr/bin/env python3
"""Exercise unevaluated reflection through source, typed HIR and both bytecode stages."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
PROGRAM: str = """
let meta = $std::meta;
let ranges = $std::ranges;
let calls = 0;
fn checked(value: Number) -> String {
    calls = calls + 1;
    "unexpected" -> ./call-ran.txt;
    value as String
};
fn generic(value) { value };
fn unit() { null };

let signature = meta::signature_of(checked);
let generic_signature = meta::signature_of(generic);
let executable = meta::signature_of(./never-start-this);
((signature::arity == 1 and signature::result_declared
    and meta::is_same(signature::result, String)
    and ranges::all_of(signature::parameters, fn(type: Type) {
        meta::is_same(type, Number)
    }) and not signature::variadic) as String) -> ./signature.txt;
((not generic_signature::result_declared
    and meta::is_same(generic_signature::result, Any)
    and executable::variadic and meta::is_same(executable::result, Process)
    and (meta::signature_of(42) is null)) as String) -> ./declared.txt;

let resolved = meta::try_invoke_result(generic, Number);
let nothing = meta::try_invoke_result(unit);
let combined = meta::common_type_of(Number, Path, Null);
((meta::is_same(resolved, Number) and meta::is_same(nothing, Null)
    and meta::is_same(combined, Number | Path | Null)
    and (meta::try_invoke_result(checked, String) is null)
    and meta::is_same(meta::try_invoke_result(checked, Number), String))
    as String) -> ./invocation.txt;

let members = meta::members_of({
    calls = calls + 1;
    "unexpected" -> ./operand-ran.txt;
    scope { label: "item", file?: ./input }
});
$std::string::join(ranges::map(members, fn(member) {
    $std::string::join([member::name, ":", meta::type_name(member::type),
        "/", meta::type_name(member::storage_type), ":", member::admits_null as String], "")
}), "|") -> ./members.txt;
((ranges::size(meta::members_of(scope {})) == 0
    and ranges::size(meta::members_of(0)) == 0) as String) -> ./empty.txt;

let other = meta::signature_of(checked);
signature::arity = 8;
((other::arity == 1 and calls == 0) as String) -> ./fresh.txt;
acquire;
"""
EXPECTED: dict[str, bytes] = {
    "signature.txt": b"true",
    "declared.txt": b"true",
    "invocation.txt": b"true",
    "members.txt": b"file:Path/Path?:true|label:String/String:false",
    "empty.txt": b"true",
    "fresh.txt": b"true",
}


def run(binary: Path, directory: Path, *arguments: str) -> str:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        encoding="utf-8",
        capture_output=True,
        timeout=60,
        check=False,
    )
    if result.returncode != 0:
        raise AssertionError(
            (arguments, result.returncode, result.stdout, result.stderr)
        )
    return result.stdout


def verify(directory: Path) -> None:
    for name, expected in EXPECTED.items():
        actual: bytes = (directory / name).read_bytes()
        if actual != expected:
            raise AssertionError((name, expected, actual))
        (directory / name).unlink()
    for name in ("call-ran.txt", "operand-ran.txt"):
        if (directory / name).exists():
            raise AssertionError(f"Reflection executed {name}")


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    temporary: Path = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="reflection-", dir=temporary) as work:
        directory: Path = Path(work)
        source: Path = directory / "reflection.msync"
        source.write_text(PROGRAM, encoding="utf-8", newline="\n")
        run(binary, directory, "run", source.name, "-O2")
        verify(directory)
        run(
            binary,
            directory,
            "compile",
            source.name,
            "--emit",
            "hir",
            "-o",
            "reflection.mhir",
        )
        source.unlink()
        run(binary, directory, "run", "reflection.mhir", "-O2")
        verify(directory)
        run(
            binary,
            directory,
            "compile",
            "reflection.mhir",
            "-O2",
            "-o",
            "reflection.mso",
        )
        run(binary, directory, "run-ir", "reflection.mso")
        verify(directory)
        run(binary, directory, "bind-ir", "reflection.mso", "-o", "reflection.mexe")
        run(binary, directory, "run-ir", "reflection.mexe")
        verify(directory)
    report: dict[str, object] = {
        "routes": ["source", "source-free HIR", "relocatable", "executable"],
        "checked_outputs": len(EXPECTED) * 4,
        "binary": str(binary),
    }
    if args.output is not None:
        output: Path = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("PASS: source, source-free HIR, relocatable and executable reflection")


if __name__ == "__main__":
    main()
