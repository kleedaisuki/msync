#!/usr/bin/env python3
"""Verify complete builtin/namespace semantics through real source-free routes."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import uuid
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, cast

ROOT: Path = Path(__file__).resolve().parents[2]
ROUTES: tuple[str, ...] = ("source", "hir", "object", "executable")
BUILTINS: tuple[str, ...] = (
    "Null",
    "Bool",
    "Number",
    "String",
    "Path",
    "Connection",
    "Function",
    "Range",
    "Process",
    "Type",
)
OLD_OBJECT_OPERATIONS: frozenset[str] = frozenset(
    {
        "MakeScope",
        "NewScope",
        "GetMember",
        "SetMember",
        "AdmitScope",
        "LoadStaticScope",
        "LoadField",
        "StoreField",
        "LoadStaticField",
        "StoreStaticField",
        "GetSingleton",
        "SetSingleton",
        "LoadCell",
        "LoadStorage",
    }
)


@dataclass(frozen=True)
class Fixture:
    name: str
    source: str
    stdout: bytes = b""
    stdin: bytes = b""
    inputs: dict[str, bytes] = field(default_factory=dict)
    outputs: dict[str, bytes] = field(default_factory=dict)
    absent: tuple[str, ...] = ("must-not-exist",)


def fixtures() -> tuple[Fixture, ...]:
    python: str = json.dumps(Path(sys.executable).as_posix())
    child: str = json.dumps(
        "import sys; from pathlib import Path; p = Path('child-count.txt'); "
        "p.write_text((p.read_text() if p.exists() else '') + '1'); "
        "sys.stdout.write(sys.stdin.read())"
    )
    payload: bytes = b"native\x00path\xff\r\n" + "Klee 汉字𐐀\n".encode()
    return (
        Fixture(
            "all-builtins",
            """fn roundtrip(value) {
    let saved = value;
    let read = fn { saved };
    read()
};
fn nullable_roundtrip(value?) {
    let saved? = value;
    let read = fn { saved };
    read()
};
let line? = $std::io::read_line();
let input = if line is null { 0 } else { line as Number };
let bundle = scope {
    number: roundtrip(input),
    flag: roundtrip(true),
    text: roundtrip("initial"),
    path: roundtrip(./payload.bin),
    endpoint: roundtrip($local),
    optional?: nullable_roundtrip(null),
    type: roundtrip(Number),
    operation: roundtrip(fn(value: Number) { value * 3 + 1 }),
    items: roundtrip($std::ranges::materialize([input, input + 1])),
};
let alias = bundle;
alias::text = "Klee 汉字𐐀";
$std::io::println(bundle::text);
$std::io::println(bundle::operation(bundle::number) as String);
$std::io::println((bundle::flag and (bundle::optional is null)) as String);
$std::io::println((bundle::endpoint is Connection) as String);
$std::io::println($std::meta::is_same(bundle::type, Number) as String);
$std::io::println($std::path::filename(bundle::path) as String);
$std::io::println($std::ranges::size(bundle::items) as String);
let remote = ssh://native-model-unselected.invalid;
let remote_path = remote @ ./never;
if false { remote_path -> ./must-not-exist; };
""",
            "Klee 汉字𐐀\n22\ntrue\ntrue\ntrue\npayload.bin\n2\n".encode(),
            b"7\n",
        ),
        Fixture(
            "fresh-alias-and-rebinding",
            """fn make(value: Number) { scope { value: value, text: "fresh" } };
let first = make(1);
let second = make(2);
let alias = first;
alias::value = 9;
first = make(3);
$std::io::println(alias::value as String);
$std::io::println(first::value as String);
$std::io::println(second::value as String);
let selected = make(4);
fn current() { selected::value };
selected = make(5);
$std::io::println(current() as String);
let empty = scope {};
let other_empty = scope {};
$std::io::println((empty is Scope) as String);
$std::io::println((other_empty is Scope) as String);
let order = "";
fn tick(mark: String) { order = $std::string::concat(order, mark); mark };
let ordered = scope { z: tick("z"), a: tick("a") };
$std::io::println(order);
""",
            b"9\n3\n2\n5\ntrue\ntrue\nza\n",
        ),
        Fixture(
            "captured-locations-and-return",
            """fn counter(initial: Number) {
    let value = initial;
    scope {
        read: fn { value },
        write: fn(next: Number) { value = next; },
    }
};
let first = counter(2);
let alias = first;
let second = counter(3);
alias::write(11);
$std::io::println(first::read() as String);
$std::io::println(second::read() as String);
let retained = scope {
    value: 4,
    read: fn { value },
    write: fn(next: Number) { value = next; },
};
retained::write(12);
$std::io::println(retained::value as String);
retained::value = 13;
$std::io::println(retained::read() as String);
""",
            b"11\n3\n12\n13\n",
        ),
        Fixture(
            "recursive-control-and-union",
            """let line? = $std::io::read_line();
let input = if line is null { 0 } else { line as Number };
fn factorial(value: Number) -> Number {
    if value <= 1 { 1 } else { value * factorial(value - 1) }
};
$std::io::println(factorial(input) as String);
let field = scope { value: if input > 0 { input } else { "empty" } };
field::value = "changed";
$std::io::println(field::value as String);
field::value = input + 1;
$std::io::println(field::value as String);
let action = fn(value: Bool) { "A" };
let replacement = fn(value: Bool) { "B" };
fn change() { action = replacement; true };
let one = action(change());
let two = action(false);
$std::io::println($std::string::concat(one, two));
let zero = input - input;
let bits = scope { negative: -zero, nan: zero / zero };
$std::io::println($std::math::signbit(bits::negative) as String);
$std::io::println((bits::nan != bits::nan) as String);
""",
            b"720\nchanged\n7\nAB\ntrue\ntrue\n",
            b"6\n",
        ),
        Fixture(
            "recursive-references",
            """let first = scope { value: 1, next?: null };
let second = scope { value: 2, next?: null };
first::next = second;
second::next = first;
let selected = first;
for _ in $std::ranges::iota(0, 4) {
    $std::io::println(selected::value as String);
    if selected::next is not null { selected = selected::next; };
};
""",
            b"1\n2\n1\n2\n",
        ),
        Fixture(
            "lazy-stdin-demand",
            """let calls = 0;
fn lines() { $std::io::stdin_lines() };
let parsed = $std::ranges::transform(lines(), fn(text: String) {
    calls = calls + 1;
    text as Number
});
let total = 0;
for value in $std::ranges::take(parsed, 3) { total = total + value; };
$std::io::println(total as String);
$std::io::println(calls as String);
""",
            b"20\n3\n",
            b"3\n7\n10\nnot-a-number\n",
        ),
        Fixture(
            "path-context-and-publication",
            """let paths = scope { source: $local @ ./payload.bin, destination: ./result.bin };
let alias = paths;
alias::source -> paths::destination;
""",
            inputs={"payload.bin": payload},
            outputs={"result.bin": payload},
        ),
        Fixture(
            "process-return-and-suspension",
            f"""fn roundtrip(value: Process) {{
    let saved = value;
    let read = fn {{ saved }};
    read()
}};
fn start() {{
    let child = roundtrip($std::process::spawn({python}, ["-c", {child}]));
    scope {{ child: child, marker: "returned" }}
}};
let state = start();
let alias = state;
$std::process::write_stdin(alias::child, "owned-through-return");
$std::process::close_stdin(state::child);
let finished = $std::process::wait(alias::child);
$std::io::println($std::process::stdout(finished));
$std::io::println($std::process::success(finished) as String);
""",
            b"owned-through-return\ntrue\n",
            outputs={"child-count.txt": b"1"},
        ),
    )


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def timestamp() -> str:
    return datetime.now(UTC).isoformat()


class InvocationFailure(RuntimeError):
    def __init__(self, details: dict[str, Any]) -> None:
        super().__init__(str(details))
        self.details = details


@dataclass
class Journey:
    binary: Path
    work: Path
    report: dict[str, Any]
    environment: dict[str, str]
    sequence: int = 0

    def checkpoint(self) -> None:
        target: Path = self.work / "result.json"
        pending: Path = target.with_suffix(".pending")
        pending.write_text(json.dumps(self.report, indent=2) + "\n", encoding="utf-8")
        pending.replace(target)

    def invoke(
        self, directory: Path, arguments: list[str], stdin: bytes = b""
    ) -> bytes:
        self.sequence += 1
        command: list[str] = [
            str(self.binary),
            "--no-config",
            "--no-telemetry",
            "--color",
            "never",
            *arguments,
        ]
        prefix: Path = self.work / "logs" / f"{self.sequence:04d}"
        prefix.parent.mkdir(exist_ok=True)
        record: dict[str, Any] = {
            "command": command,
            "cwd": str(directory),
            "started_utc": timestamp(),
            "stdin_bytes": len(stdin),
            "stdin_sha256": sha(stdin),
            "status": "running",
        }
        self.report["invocations"].append(record)
        self.checkpoint()
        try:
            completed: subprocess.CompletedProcess[bytes] = subprocess.run(
                command,
                cwd=directory,
                input=stdin,
                env=self.environment,
                capture_output=True,
                check=False,
                timeout=60,
            )
            output, error = completed.stdout, completed.stderr
            record["exit_code"] = completed.returncode
        except subprocess.TimeoutExpired as failure:
            output = failure.stdout or b""
            error = failure.stderr or b""
            record.update(exit_code=None, timeout_seconds=60)
        except OSError as failure:
            output, error = b"", str(failure).encode()
            record.update(exit_code=None, launch_error=repr(failure))
        prefix.with_suffix(".stdout").write_bytes(output)
        prefix.with_suffix(".stderr").write_bytes(error)
        record.update(
            finished_utc=timestamp(),
            stdout_file=str(prefix.with_suffix(".stdout")),
            stderr_file=str(prefix.with_suffix(".stderr")),
            stdout_sha256=sha(output),
            stderr_sha256=sha(error),
            status="complete" if record["exit_code"] == 0 and not error else "failed",
        )
        if record["status"] == "failed":
            record.update(
                stdout=output.decode("utf-8", errors="backslashreplace"),
                stderr=error.decode("utf-8", errors="backslashreplace"),
                stdout_hex=output.hex(),
                stderr_hex=error.hex(),
            )
            self.checkpoint()
            raise InvocationFailure(record)
        self.checkpoint()
        return output


def initialize(directory: Path, fixture: Fixture) -> None:
    directory.mkdir(parents=True)
    for name, content in fixture.inputs.items():
        target: Path = directory / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)


def check_outputs(directory: Path, fixture: Fixture) -> None:
    for name, expected in fixture.outputs.items():
        target: Path = directory / name
        actual: bytes = target.read_bytes()
        if actual != expected:
            raise AssertionError(
                (fixture.name, str(target), expected.hex(), actual.hex())
            )
    for name in fixture.absent:
        if (directory / name).exists():
            raise AssertionError((fixture.name, "unselected effect executed", name))


def no_effects(directory: Path, fixture: Fixture) -> None:
    for name in (*fixture.outputs, *fixture.absent):
        if (directory / name).exists():
            raise AssertionError((fixture.name, "preparation executed an effect", name))


def inspect_native_ir(document: dict[str, Any], required: bool) -> dict[str, Any]:
    text: str = str(document.get("text", ""))
    operations: list[str] = re.findall(r"^\s*\d+:\s+(\w+)", text, re.MULTILINE)
    operations.extend(structured_operations(document))
    legacy: list[str] = sorted(set(operations) & OLD_OBJECT_OPERATIONS)
    allocations: list[str] = re.findall(
        r'\bnative\("((?:arena|slots|storage)\.[^"]+)"\)', text
    )
    storage: bool = bool(
        re.search(r"^type\s+@t\d+\s*=.*\bStorage\b", text, re.MULTILINE)
    )
    marker: bool = document.get("representation") == "native-v1"
    result: dict[str, Any] = {
        "representation": document.get("representation"),
        "native_marker": marker,
        "legacy_object_operations": legacy,
        "legacy_allocator_operations": allocations,
        "storage_kind_present": storage,
        "operation_count": len(operations),
        "structurally_accepted": marker
        and bool(operations)
        and not (legacy or allocations or storage),
    }
    if required and not result["structurally_accepted"]:
        raise AssertionError(("Complete native IR evidence is required", result))
    return result


def structured_operations(value: object) -> list[str]:
    if isinstance(value, dict):
        result: list[str] = [value["op"]] if isinstance(value.get("op"), str) else []
        for child in value.values():
            result.extend(structured_operations(child))
        return result
    if isinstance(value, list):
        return [
            operation for child in value for operation in structured_operations(child)
        ]
    return []


def execute(
    journey: Journey,
    directory: Path,
    target: str,
    fixture: Fixture,
    output_root: Path | None = None,
    level: int | None = None,
) -> None:
    arguments: list[str] = ["run", target]
    if level is not None:
        arguments.append(f"-O{level}")
    output: bytes = journey.invoke(directory, arguments, fixture.stdin)
    if output.replace(b"\r\n", b"\n") != fixture.stdout:
        raise AssertionError((fixture.name, target, fixture.stdout, output))
    check_outputs(output_root or directory, fixture)


def run_fixture(
    journey: Journey, fixture: Fixture, level: int, native_required: bool
) -> None:
    root: Path = journey.work / f"O{level}-{fixture.name}"
    producer: Path = root / "producer"
    initialize(producer, fixture)
    source: Path = producer / "program.msync"
    source.write_bytes(fixture.source.encode())
    hir: Path = producer / "program.mhir"
    portable: Path = producer / "program.mso"
    row: dict[str, Any] = {
        "case": fixture.name,
        "optimization": level,
        "status": "running",
        "routes": [],
        "source_sha256": sha(source.read_bytes()),
        "stdin_sha256": sha(fixture.stdin),
        "expected_stdout_sha256": sha(fixture.stdout),
        "expected_files": {
            name: sha(content) for name, content in fixture.outputs.items()
        },
    }
    journey.report["cases"].append(row)
    journey.report["active"] = {
        "case": fixture.name,
        "level": level,
        "stage": "prepare",
    }
    journey.checkpoint()
    journey.invoke(producer, ["compile", source.name, "--emit", "hir", "-o", hir.name])
    journey.invoke(
        producer, ["compile", source.name, f"-O{level}", "-o", portable.name]
    )
    no_effects(producer, fixture)
    model: dict[str, Any] = cast(
        dict[str, Any],
        json.loads(journey.invoke(producer, ["disassemble", portable.name, "--json"])),
    )
    row["native_ir"] = inspect_native_ir(model, native_required)
    encoded_hir: bytes = hir.read_bytes()
    encoded_portable: bytes = portable.read_bytes()
    row.update(hir_sha256=sha(encoded_hir), portable_sha256=sha(encoded_portable))
    journey.report["active"]["stage"] = "source"
    execute(journey, producer, source.name, fixture, level=level)
    row["routes"].append("source")
    for path in (source, hir, portable):
        path.unlink()
    journey.checkpoint()

    semantic: Path = root / "hir-only"
    initialize(semantic, fixture)
    (semantic / hir.name).write_bytes(encoded_hir)
    journey.report["active"]["stage"] = "hir"
    journey.invoke(semantic, ["compile", hir.name, f"-O{level}", "-o", portable.name])
    hir_lowered: bytes = (semantic / portable.name).read_bytes()
    row["hir_lowered_sha256"] = sha(hir_lowered)
    row["hir_lowered_byte_identical"] = hir_lowered == encoded_portable
    hir_model: dict[str, Any] = cast(
        dict[str, Any],
        json.loads(journey.invoke(semantic, ["disassemble", portable.name, "--json"])),
    )
    row["hir_native_ir"] = inspect_native_ir(hir_model, native_required)
    (semantic / portable.name).unlink()
    no_effects(semantic, fixture)
    execute(journey, semantic, hir.name, fixture, level=level)
    row["routes"].append("hir")
    (semantic / hir.name).unlink()
    journey.checkpoint()

    relocated: Path = root / "object-only"
    initialize(relocated, fixture)
    (relocated / portable.name).write_bytes(encoded_portable)
    journey.report["active"]["stage"] = "object"
    execute(journey, relocated, portable.name, fixture)
    row["routes"].append("object")
    (relocated / portable.name).unlink()
    journey.checkpoint()

    binding: Path = root / "binding"
    initialize(binding, fixture)
    (binding / portable.name).write_bytes(encoded_portable)
    journey.report["active"]["stage"] = "bind"
    journey.invoke(binding, ["bind-ir", portable.name, "-o", "program.mexe"])
    no_effects(binding, fixture)
    executable_model: dict[str, Any] = cast(
        dict[str, Any],
        json.loads(journey.invoke(binding, ["disassemble", "program.mexe", "--json"])),
    )
    row["executable_native_ir"] = inspect_native_ir(executable_model, native_required)
    executable: bytes = (binding / "program.mexe").read_bytes()
    row["executable_sha256"] = sha(executable)
    (binding / portable.name).unlink()
    (binding / "program.mexe").unlink()
    runtime: Path = root / "unrelated-runtime-cwd"
    runtime.mkdir()
    (runtime / "program.mexe").write_bytes(executable)
    journey.report["active"]["stage"] = "executable"
    execute(journey, runtime, "program.mexe", fixture, binding)
    no_effects(runtime, fixture)
    row["routes"].append("executable")
    row["status"] = "complete"
    journey.checkpoint()


def ssh_guard(work: Path) -> tuple[dict[str, str], bool]:
    environment: dict[str, str] = dict(os.environ)
    environment["MSYNC_SSH_CONFIG"] = str(work / "ssh-config-must-not-be-read")
    if os.name != "posix":
        return environment, False
    directory: Path = work / "guard-bin"
    directory.mkdir()
    marker: Path = work / "unexpected-ssh.txt"
    executable: Path = directory / "ssh"
    executable.write_text(
        f"#!{sys.executable}\nfrom pathlib import Path\n"
        f"marker: Path = Path({str(marker)!r})\n"
        "marker.write_text('SSH unexpectedly executed', encoding='utf-8')\n"
        "raise SystemExit(97)\n",
        encoding="utf-8",
    )
    executable.chmod(0o755)
    environment["PATH"] = str(directory) + os.pathsep + environment.get("PATH", "")
    return environment, True


def main() -> None:
    available: tuple[Fixture, ...] = fixtures()
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--work", type=Path)
    parser.add_argument(
        "--case", action="append", choices=[fixture.name for fixture in available]
    )
    parser.add_argument("--level", type=int, action="append", choices=range(4))
    mode: argparse._MutuallyExclusiveGroup = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--semantic-only",
        action="store_true",
        help="Explicit migration baseline; does not accept the native representation",
    )
    mode.add_argument(
        "--require-native-ir",
        action="store_true",
        help="Require native-v1 IR evidence (the default)",
    )
    options: argparse.Namespace = parser.parse_args()
    binary: Path = options.binary.resolve(strict=True)
    work: Path = (
        options.work or ROOT / ".temp/native-model-journey" / uuid.uuid4().hex
    ).resolve()
    temporary: Path = (ROOT / ".temp").resolve()
    if (
        not temporary.is_relative_to(ROOT)
        or not work.is_relative_to(temporary)
        or work == temporary
    ):
        parser.error("--work must be a child of repository-root .temp")
    if work.exists() and (not work.is_dir() or any(work.iterdir())):
        parser.error("--work must be new or empty; prior evidence is never overwritten")
    work.mkdir(parents=True, exist_ok=True)
    environment, guarded = ssh_guard(work)
    report: dict[str, Any] = {
        "status": "running",
        "started_utc": timestamp(),
        "binary": str(binary),
        "binary_sha256": sha(binary.read_bytes()),
        "cases": [],
        "invocations": [],
        "builtin_contract": BUILTINS,
        "routes": ROUTES,
        "ssh_guard_available": guarded,
        "selected_cases": options.case or [fixture.name for fixture in available],
        "full_suite_selected": options.case is None
        or set(options.case) == {fixture.name for fixture in available},
        "structural_native_ir_required": not options.semantic_only,
        "acceptance_scope": "native IR and semantic journeys"
        if not options.semantic_only
        else "semantic migration baseline only; not v0.9 architecture acceptance",
    }
    journey: Journey = Journey(binary, work, report, environment)
    try:
        report["product_version"] = journey.invoke(work, ["--version"]).decode().strip()
        for level in options.level or [0, 2, 3]:
            for fixture in available:
                if options.case is None or fixture.name in options.case:
                    run_fixture(journey, fixture, level, not options.semantic_only)
                    print(
                        f"PASS O{level} {fixture.name}: source/hir/object/executable",
                        flush=True,
                    )
        if (work / "unexpected-ssh.txt").exists():
            raise AssertionError(
                "Merely representing an unselected Connection launched SSH"
            )
        report.update(status="complete", active=None, unwanted_ssh=False)
    except BaseException as failure:
        report.update(status="failed", failure=repr(failure))
        if isinstance(failure, InvocationFailure):
            report["failed_invocation"] = failure.details
        raise
    finally:
        report["finished_utc"] = timestamp()
        journey.checkpoint()
        print(work / "result.json", flush=True)


if __name__ == "__main__":
    main()
