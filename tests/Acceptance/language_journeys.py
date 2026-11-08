#!/usr/bin/env python3
"""Independent real-process acceptance for the v0.8 language contract."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Journey:
    name: str
    source: str
    expected: dict[str, bytes]
    inputs: dict[str, bytes] | None = None
    accepted: bool = True
    artifacts: bool = False
    run_exit: int = 0
    diagnostic: str | None = None
    relocation: bool = False


JOURNEYS: tuple[Journey, ...] = (
    Journey(
        "path_call_returns_real_process",
        "__VERSION_PROCESS__ child::stdout -> result.txt;",
        {"result.txt": f"msync 0.8.0{os.linesep}".encode()},
        artifacts=True,
    ),
    Journey(
        "removed_keywords_are_ordinary_names",
        'let async = "ordinary names"; let await = async; '
        "fn delete(value) { value }; delete(await) -> result.txt;",
        {"result.txt": b"ordinary names"},
        artifacts=True,
    ),
    Journey(
        "inferred_recursive_function",
        'fn settle(value) { if value { "done" } else { settle(true) } }; '
        "settle(false) -> result.txt;",
        {"result.txt": b"done"},
        artifacts=True,
    ),
    Journey(
        "native_range_callback",
        "let chosen = [./input.txt, ./missing.txt] | "
        "$std::ranges::filter(fn(file: Path) { exists file }); "
        "for file in chosen { file -> selected/; };",
        {"selected/input.txt": b"selected bytes"},
        {"input.txt": b"selected bytes"},
        artifacts=True,
    ),
    Journey(
        "decltype_process_does_not_launch",
        'let kind = decltype(./program-that-does-not-exist("arg")); '
        'if kind is Process { "typed process" -> result.txt; } '
        'else { "wrong" -> result.txt; };',
        {"result.txt": b"typed process"},
        artifacts=True,
    ),
    Journey(
        "type_union_exhaustive_match_narrows",
        'let value = if exists ./input.txt { ./input.txt } else { "fallback" }; '
        "let _ = match value { Path => { value -> result.txt }, "
        "String => { value -> result.txt } };",
        {"result.txt": b"typed bytes"},
        {"input.txt": b"typed bytes"},
        artifacts=True,
    ),
    Journey(
        "type_union_nonexhaustive_match_rejected",
        'let value = if exists ./input.txt { ./input.txt } else { "fallback" }; '
        "let _ = match value { Path => {} };",
        {},
        accepted=False,
    ),
    Journey(
        "artifact_context_relocation",
        "./input.txt -> ./result.txt;",
        {"result.txt": b"source context"},
        {"input.txt": b"source context"},
        artifacts=True,
        relocation=True,
    ),
    Journey(
        "brace_blocks_ignore_indentation",
        'fn echo(value) {\n            let inner = {\n "discarded";\n'
        "                    value\n  };\ninner\n};\n"
        'let text = echo("braces");\n            text -> result.txt;',
        {"result.txt": b"braces"},
        artifacts=True,
    ),
    Journey(
        "missing_statement_semicolon_rejected",
        "let first = true\nlet second = false;",
        {},
        accepted=False,
    ),
    Journey(
        "path_parameter_coerces_filename_string",
        "fn copy_file(file: Path, target: Path) { file -> target }; "
        'copy_file("input.txt", "result.txt");',
        {"result.txt": b"actual file bytes"},
        {"input.txt": b"actual file bytes"},
        artifacts=True,
    ),
    Journey(
        "path_binding_coerces_filename_string",
        'let file: Path = "input.txt"; file -> result.txt;',
        {"result.txt": b"actual file bytes"},
        {"input.txt": b"actual file bytes"},
        artifacts=True,
    ),
    Journey(
        "string_binding_coerces_path_to_raw_text",
        "let text: String = ./input.txt; text -> result.txt;",
        {"result.txt": b"./input.txt"},
        {"input.txt": b"actual file bytes"},
        artifacts=True,
    ),
    Journey(
        "transfer_false_runs_else_and_keeps_target",
        "let copied = ./missing.txt -> ./target.txt; "
        'if copied { "wrong" -> result.txt; } else { "failed" -> result.txt; };',
        {"result.txt": b"failed"},
        {"target.txt": b"old target"},
        artifacts=True,
        run_exit=1,
        diagnostic="missing.txt",
    ),
    Journey(
        "block_tail_and_discard",
        'let tail = { "discarded"; "tail" }; '
        'let empty? = { "discarded"; }; '
        'if empty is null { tail -> result.txt; } else { "wrong" -> result.txt; };',
        {"result.txt": b"tail"},
        artifacts=True,
    ),
    Journey(
        "semicolon_null_requires_marker",
        'let empty = { "discarded"; };',
        {},
        accepted=False,
    ),
    Journey(
        "nullable_without_marker_rejected",
        "let value = if exists ./input.txt { ./input.txt } else { null };",
        {},
        {"input.txt": b"input"},
        accepted=False,
    ),
    Journey(
        "nullable_transfer_rejected",
        "let value? = if exists ./input.txt { ./input.txt } else { null }; "
        "value -> result.txt;",
        {},
        {"input.txt": b"input"},
        accepted=False,
    ),
    Journey(
        "nullable_alias_without_marker_rejected",
        "let value? = if exists ./input.txt { ./input.txt } else { null }; "
        "let alias = value;",
        {},
        {"input.txt": b"input"},
        accepted=False,
    ),
    Journey(
        "nullable_present_question_narrows_else",
        "let value? = if exists ./input.txt { ./input.txt } else { null }; "
        'if value? { "missing" -> result.txt; } else { value -> result.txt; };',
        {"result.txt": b"input"},
        {"input.txt": b"input"},
        artifacts=True,
    ),
    Journey(
        "nullable_absent_question_means_is_null",
        "let value? = if exists ./missing.txt { ./missing.txt } else { null }; "
        'if value? { "missing" -> result.txt; } else { value -> result.txt; };',
        {"result.txt": b"missing"},
        artifacts=True,
    ),
    Journey(
        "nullable_is_not_narrows_then",
        "let value? = if exists ./input.txt { ./input.txt } else { null }; "
        'if value is not null { value -> result.txt; } else { "missing" -> result.txt; };',
        {"result.txt": b"input"},
        {"input.txt": b"input"},
    ),
    Journey("null_equality_rejected", "let equal = null == null;", {}, accepted=False),
    Journey(
        "null_boolean_rejected",
        'if null { "wrong" -> result.txt; };',
        {},
        accepted=False,
    ),
    Journey(
        "bool_exhaustive_match",
        'let value = match exists ./input.txt { true => { "yes" }, false => { "no" } }; '
        "value -> result.txt;",
        {"result.txt": b"yes"},
        {"input.txt": b"input"},
        artifacts=True,
    ),
    Journey(
        "bool_nonexhaustive_match_rejected",
        'let value = match exists ./input.txt { true => { "yes" } };',
        {},
        accepted=False,
    ),
    Journey(
        "nullable_match_narrows_wildcard",
        "let value? = if exists ./input.txt { ./input.txt } else { null }; "
        'let _ = match value { null => { "missing" -> result.txt }, '
        "_ => { value -> result.txt } };",
        {"result.txt": b"input"},
        {"input.txt": b"input"},
        artifacts=True,
    ),
    Journey(
        "regex_path_and_fallback",
        'let file = ./input.txt; let text = match file { <.*input[.]txt> => { "matched" }, '
        '_ => { "wrong" } }; text -> result.txt;',
        {"result.txt": b"matched"},
        artifacts=True,
    ),
    Journey(
        "regex_nonexhaustive_rejected",
        'let value = match ./input.txt { <.*> => { "matched" } };',
        {},
        accepted=False,
    ),
    Journey(
        "decltype_transfer_does_not_execute",
        'let value = if decltype("must-not-exist" -> forbidden.txt) is Bool '
        '{ "typed" } else { "wrong" }; value -> result.txt;',
        {"result.txt": b"typed"},
        artifacts=True,
    ),
    Journey(
        "type_match",
        'let kind = match decltype(./input.txt) { Path => { "path" }, _ => { "wrong" } }; '
        "kind -> result.txt;",
        {"result.txt": b"path"},
        artifacts=True,
    ),
    Journey(
        "generic_functions",
        "fn identity(value) { value }; "
        'identity("literal") -> literal.txt; identity(./input.txt) -> copied.txt;',
        {"literal.txt": b"literal", "copied.txt": b"input"},
        {"input.txt": b"input"},
        artifacts=True,
    ),
    Journey(
        "function_callback",
        "fn apply(callback, value) { callback(value) }; "
        "let identity = fn(value) { value }; "
        'apply(identity, "callback") -> result.txt;',
        {"result.txt": b"callback"},
        artifacts=True,
    ),
    Journey(
        "recursive_function",
        "fn countdown(value: Number) -> String { "
        'if value == 0 { "done" } else { countdown(value - 1) } }; '
        "countdown(5) -> result.txt;",
        {"result.txt": b"done"},
        artifacts=True,
    ),
    Journey(
        "anonymous_scope_and_function",
        "let library = scope { root: ./input.txt, echo: fn(value) { value }, }; "
        "library::echo(library::root) -> result.txt;",
        {"result.txt": b"input"},
        {"input.txt": b"input"},
        artifacts=True,
    ),
    Journey(
        "literal_is_not_existing_filename",
        '"input.txt" -> literal.txt; ./input.txt -> copied.txt;',
        {"literal.txt": b"input.txt", "copied.txt": b"actual file contents"},
        {"input.txt": b"actual file contents"},
        artifacts=True,
    ),
    Journey(
        "string_content_has_no_escape_decoding",
        '"雪\\n\\t" -> result.txt;',
        {"result.txt": "雪\\n\\t".encode()},
        artifacts=True,
    ),
    Journey(
        "unselected_connection_has_no_effect",
        "let remote = ssh://invalid-acceptance-host; "
        'if false { ./input.txt -> remote::./output.txt; } else { "local" -> result.txt; };',
        {"result.txt": b"local"},
        {"input.txt": b"input"},
        artifacts=True,
    ),
)


@dataclass(frozen=True)
class CommandResult:
    arguments: tuple[str, ...]
    returncode: int
    stdout: str
    stderr: str
    elapsed_seconds: float


def execute(command: Sequence[str], cwd: Path) -> CommandResult:
    start = time.monotonic()
    result = subprocess.run(
        list(command),
        cwd=cwd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        check=False,
    )
    return CommandResult(
        tuple(command),
        result.returncode,
        result.stdout,
        result.stderr,
        time.monotonic() - start,
    )


def describe(result: CommandResult) -> dict[str, object]:
    return {
        "arguments": result.arguments,
        "returncode": result.returncode,
        "stdout": result.stdout,
        "stderr": result.stderr,
        "elapsed_seconds": result.elapsed_seconds,
    }


def require_success(result: CommandResult) -> None:
    if result.returncode:
        raise AssertionError(
            f"command failed ({result.returncode}): {' '.join(result.arguments)}\n"
            f"{result.stdout}{result.stderr}"
        )


def assert_files(journey: Journey, directory: Path) -> None:
    for name, expected in journey.expected.items():
        target = directory / name
        if not target.exists():
            raise AssertionError(f"{name} was not created")
        actual = target.read_bytes()
        if actual != expected:
            raise AssertionError(f"{name}: expected {expected!r}; observed {actual!r}")
    if (directory / "forbidden.txt").exists():
        raise AssertionError("decltype evaluated its operand and created forbidden.txt")
    for name, expected in (journey.inputs or {}).items():
        if (directory / name).read_bytes() != expected:
            raise AssertionError(
                f"Read-only input or failed-copy target {name} changed"
            )


def assert_run(journey: Journey, result: CommandResult) -> None:
    if result.returncode != journey.run_exit:
        raise AssertionError(
            f"expected exit {journey.run_exit}, observed {result.returncode}: "
            f"{result.stdout}{result.stderr}"
        )
    if journey.diagnostic is not None and journey.diagnostic not in result.stderr:
        raise AssertionError(
            f"failure diagnostic lost {journey.diagnostic!r}: {result.stderr}"
        )


def clear_outputs(journey: Journey, directory: Path) -> None:
    for name in journey.expected:
        (directory / name).unlink(missing_ok=True)


def check_relocation(cli: Sequence[str], directory: Path) -> list[dict[str, object]]:
    commands: list[dict[str, object]] = []
    compiled_at = directory / "compiled-at"
    bound_at = directory / "bound-at"
    executed_at = directory / "executed-at"
    for folder in (compiled_at, bound_at, executed_at):
        folder.mkdir()
    (compiled_at / "input.txt").write_bytes(b"wrong compiler context")
    (bound_at / "input.txt").write_bytes(b"relocated context")
    source = compiled_at / "portable.msync"
    source.write_text("./input.txt -> ./result.txt;", encoding="utf-8")
    portable = compiled_at / "portable.mso"
    executable = compiled_at / "frozen.mexe"
    compiled = execute([*cli, "compile", str(source), "-o", str(portable)], compiled_at)
    commands.append(describe(compiled))
    require_success(compiled)
    source.unlink()
    ran = execute([*cli, "run-ir", str(portable)], bound_at)
    commands.append(describe(ran))
    require_success(ran)
    if (bound_at / "result.txt").read_bytes() != b"relocated context":
        raise AssertionError(
            "Relocatable .mso retained compiler cwd instead of execution cwd"
        )
    if (compiled_at / "result.txt").exists():
        raise AssertionError("Relocatable .mso wrote into compiler cwd")
    bound = execute([*cli, "bind-ir", str(portable), "-o", str(executable)], bound_at)
    commands.append(describe(bound))
    require_success(bound)
    (bound_at / "result.txt").unlink()
    (bound_at / "input.txt").write_bytes(b"fresh bytes after binding")
    ran_bound = execute([*cli, "run-ir", str(executable)], executed_at)
    commands.append(describe(ran_bound))
    require_success(ran_bound)
    if (bound_at / "result.txt").read_bytes() != b"fresh bytes after binding":
        raise AssertionError(
            "Executable .mexe lost bound cwd or retained stale input bytes"
        )
    if (executed_at / "result.txt").exists():
        raise AssertionError("Executable .mexe relocated to its later execution cwd")
    return commands


def quote_string(value: str) -> str:
    return '"' + value.replace('"', '""') + '"'


def run_journey(
    journey: Journey, cli: Sequence[str], directory: Path, artifacts: bool
) -> dict[str, object]:
    commands: list[dict[str, object]] = []
    report: dict[str, object] = {
        "name": journey.name,
        "fixture": str(directory),
        "commands": commands,
    }
    for name, content in (journey.inputs or {}).items():
        destination = directory / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(content)
    source = directory / "main.msync"
    command = shutil.which(cli[0]) or cli[0]
    process_arguments = [*cli[1:], "--version"]
    process = (
        f"let program: Path = {quote_string(command)}; "
        f"let child = program({', '.join(quote_string(value) for value in process_arguments)});"
    )
    source.write_text(
        journey.source.replace("__VERSION_PROCESS__", process), encoding="utf-8"
    )
    try:
        checked = execute([*cli, "check", str(source)], directory)
        commands.append(describe(checked))
        if journey.accepted:
            require_success(checked)
        elif checked.returncode == 0:
            raise AssertionError(
                "check accepted a program forbidden by the language contract"
            )
        if any((directory / name).exists() for name in journey.expected):
            raise AssertionError("check performed an output effect")
        if (directory / "forbidden.txt").exists():
            raise AssertionError("check executed the decltype transfer operand")
        if not journey.accepted:
            if not checked.stderr.strip():
                raise AssertionError("rejected source supplied no diagnostic on stderr")
            report["passed"] = True
            return report
        ran = execute([*cli, "run", str(source)], directory)
        commands.append(describe(ran))
        assert_run(journey, ran)
        assert_files(journey, directory)
        if artifacts and journey.artifacts:
            clear_outputs(journey, directory)
            relocatable = directory / "main.mso"
            executable = directory / "main.mexe"
            compiled = execute(
                [*cli, "compile", str(source), "-o", str(relocatable)], directory
            )
            commands.append(describe(compiled))
            require_success(compiled)
            bound = execute(
                [*cli, "bind-ir", str(relocatable), "-o", str(executable)], directory
            )
            commands.append(describe(bound))
            require_success(bound)
            if any((directory / name).exists() for name in journey.expected):
                raise AssertionError(
                    "artifact compile/binding performed an output effect"
                )
            source.unlink()
            for artifact in (relocatable, executable):
                ran_artifact = execute([*cli, "run-ir", str(artifact)], directory)
                commands.append(describe(ran_artifact))
                assert_run(journey, ran_artifact)
                assert_files(journey, directory)
                clear_outputs(journey, directory)
        if artifacts and journey.relocation:
            commands.extend(check_relocation(cli, directory))
        report["passed"] = True
    except (AssertionError, OSError, subprocess.TimeoutExpired) as error:
        report["passed"] = False
        report["error"] = str(error)
    return report


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "binary",
        type=Path,
        help="Native msync executable or managed msync.dll (launched with dotnet)",
    )
    parser.add_argument(
        "--case", action="append", default=[], choices=[item.name for item in JOURNEYS]
    )
    parser.add_argument(
        "--artifacts",
        action="store_true",
        help="Also execute binary source-free IR journeys",
    )
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".temp/acceptance-v080/results.json"
    )
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    binary = arguments.binary.resolve()
    if not binary.is_file():
        raise SystemExit(f"CLI does not exist: {binary}")
    temporary_root = ROOT / ".temp/acceptance-v080"
    temporary_root.mkdir(parents=True, exist_ok=True)
    snapshot = Path(tempfile.mkdtemp(prefix="cli-", dir=temporary_root))
    managed_host = binary.with_name(f"{binary.stem}.runtimeconfig.json").is_file()
    if binary.suffix == ".dll" or managed_host:
        shutil.copytree(binary.parent, snapshot, dirs_exist_ok=True)
    else:
        shutil.copy2(binary, snapshot / binary.name)
    frozen_binary = snapshot / binary.name
    snapshot_files = {
        str(path.relative_to(snapshot)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(snapshot.rglob("*"))
        if path.is_file()
    }
    snapshot_digest = hashlib.sha256(
        json.dumps(snapshot_files, sort_keys=True).encode()
    ).hexdigest()
    cli: list[str] = (
        ["dotnet", str(frozen_binary)]
        if frozen_binary.suffix == ".dll"
        else [str(frozen_binary)]
    )
    selected: set[str] = set(arguments.case)
    journeys: list[dict[str, object]] = []
    for journey in JOURNEYS:
        if selected and journey.name not in selected:
            continue
        directory = Path(
            tempfile.mkdtemp(prefix=f"{journey.name}-", dir=temporary_root)
        )
        report = run_journey(journey, cli, directory, arguments.artifacts)
        journeys.append(report)
        print(f"{'PASS' if report['passed'] else 'FAIL'} {journey.name}", flush=True)
        if not report["passed"]:
            print(report["error"], flush=True)
    passed = all(report["passed"] for report in journeys)
    output = arguments.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(
            {
                "binary": str(binary),
                "frozen_binary": str(frozen_binary),
                "sha256": hashlib.sha256(frozen_binary.read_bytes()).hexdigest(),
                "snapshot_sha256": snapshot_digest,
                "files_sha256": snapshot_files,
                "artifacts": arguments.artifacts,
                "passed": passed,
                "journeys": journeys,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(
        f"{sum(bool(report['passed']) for report in journeys)}/{len(journeys)} passed; {output}"
    )
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
