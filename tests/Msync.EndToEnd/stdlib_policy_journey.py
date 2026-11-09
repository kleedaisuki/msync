#!/usr/bin/env python3
"""Exercise native Scope families and policy composition through actual CLI stages."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
import tempfile
from pathlib import Path

from collections_policy_journey import execute as execute_collections
from collections_policy_journey import reject as reject_collections
from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]

PROGRAM: str = r"""
let file = $std::files::File::new(./native.txt);
let alias = file;
$std::files::File::write(file, "native 星");
$std::files::File::append(alias, " text");
$std::files::File::move_to(alias, ./moved.txt);
$std::files::File::read_text(file) -> ./read.txt;
if file::path == alias::path and not exists ./native.txt {
    "shared-record" -> ./file-alias.txt;
};
let memory = scope { text: "", writes: 0, reads: 0 };
scope upper_store {
    read: fn(path: Path) -> String {
        memory::reads = memory::reads + 1;
        memory::text
    },
    lines: fn(path: Path) -> Range<String> {
        $std::string::lines(memory::text)
    },
    write: fn(path: Path, text: String) -> Null {
        memory::writes = memory::writes + 1;
        memory::text = $std::string::to_uppercase(text);
    },
    append: fn(path: Path, text: String) -> Null {
        memory::writes = memory::writes + 1;
        memory::text = $std::string::concat(memory::text,
            $std::string::to_uppercase(text));
    },
};
let selected = $std::files::File::with_policy(./never-opened.txt, upper_store);
if memory::reads == 0 and memory::writes == 0 {
    "no-construction-io" -> ./construction.txt;
};
$std::files::File::write(selected, "policy");
$std::files::File::append(selected, " text");
$std::files::File::read_text(selected) -> ./policy.txt;
if memory::reads == 1 and memory::writes == 2
    and not exists ./never-opened.txt {
    "selected-policy" -> ./policy-selected.txt;
};
let append_calls = 0;
scope eager_chunks {
    append: fn(chunks: Range<String>, text: String) -> Range<String> {
        append_calls = append_calls + 1;
        $std::ranges::materialize($std::ranges::concat(chunks, [text]))
    },
    build: fn(chunks: Range<String>) -> String {
        $std::string::join(chunks, "")
    },
};
let builder = $std::text::StringBuilder::with_policy(eager_chunks);
let builder_alias = builder;
$std::text::StringBuilder::append(builder, "A🙂");
let before = $std::text::StringBuilder::snapshot(builder);
$std::text::StringBuilder::append(builder_alias, "β");
$std::text::StringBuilder::build(builder) -> ./built.txt;
before -> ./text-before.txt;
if builder::length == 3 and append_calls == 2 {
    "typed-chunks" -> ./chunks.txt;
};
let independent = $std::text::StringBuilder::new();
$std::text::StringBuilder::append(independent, "separate");
$std::text::StringBuilder::build(independent) -> ./separate.txt;
let expression = $std::regex::Pattern::new("[0-9]+");
$std::regex::Pattern::replace(expression, "id12", "<number>") -> ./regex.txt;
let command = $std::commands::Command::new(__EXECUTABLE__ as Path,
    ["--version"]);
let result = $std::commands::Command::capture(command);
if result::success and result::code == 0
    and $std::string::contains(result::output, "0.8.0") {
    "actual-process" -> ./process.txt;
};
acquire;
"""

OUTPUTS: dict[str, bytes] = {
    "moved.txt": "native 星 text".encode(),
    "read.txt": "native 星 text".encode(),
    "file-alias.txt": b"shared-record",
    "construction.txt": b"no-construction-io",
    "policy.txt": b"POLICY TEXT",
    "policy-selected.txt": b"selected-policy",
    "built.txt": "A🙂β".encode(),
    "text-before.txt": "A🙂".encode(),
    "chunks.txt": b"typed-chunks",
    "separate.txt": b"separate",
    "regex.txt": b"id<number>",
    "process.txt": b"actual-process",
}


def execute_services(binary: Path, parent: Path, levels: list[int]) -> tuple[int, int]:
    executions: int = 0
    observations: int = 0
    program: str = PROGRAM.replace("__EXECUTABLE__", json.dumps(str(binary)))
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            source: Path = directory / "native families.msync"
            source.write_text(program, encoding="utf-8")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"), "check"
            )
            artifact: Path = source
            if stage != "source":
                artifact = source.with_suffix(".mso")
                accept(
                    invoke(
                        binary,
                        directory,
                        "compile",
                        str(source),
                        "-o",
                        str(artifact),
                        f"-O{level}",
                    ),
                    "compile",
                )
                if stage == "executable":
                    bound: Path = source.with_suffix(".mexe")
                    accept(
                        invoke(
                            binary,
                            directory,
                            "bind-ir",
                            str(artifact),
                            "-o",
                            str(bound),
                            "--source-file",
                            str(source),
                        ),
                        "bind",
                    )
                    artifact = bound
                source.unlink()
            if any((directory / name).exists() for name in OUTPUTS):
                raise AssertionError(
                    "Static compilation or binding executed selected IO"
                )
            launch: Path = directory
            if stage == "executable":
                launch = directory / "other launch"
                launch.mkdir()
            accept(invoke(binary, launch, "run", str(artifact), f"-O{level}"), stage)
            for name, expected in OUTPUTS.items():
                actual: bytes = (directory / name).read_bytes()
                if actual != expected:
                    raise AssertionError((level, stage, name, actual, expected))
                observations += 1
            if (directory / "never-opened.txt").exists():
                raise AssertionError(
                    "Custom text policy unexpectedly performed native IO"
                )
            executions += 1
    return executions, observations


def reject_services(binary: Path, directory: Path) -> int:
    programs: tuple[tuple[str, str], ...] = (
        (
            (
                "$std::text::StringBuilder::with_policy(scope { "
                "append: fn(chunks: Range<String>, text: String) { true }, "
                'build: fn(chunks: Range<String>) { "" } });'
            ),
            "append",
        ),
        (
            (
                "$std::files::File::with_policy(./target, scope { "
                "read: fn(path: Path) { 1 } });"
            ),
            "read",
        ),
        (
            (
                'let command = $std::commands::Command::new(./program, ["a"]); '
                "command::executable = true;"
            ),
            "Path",
        ),
    )
    for index, (program, fragment) in enumerate(programs):
        source: Path = directory / f"reject-{index}.msync"
        source.write_text(program, encoding="utf-8")
        result: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "check", str(source)
        )
        diagnostic: str = result.stdout + result.stderr
        if result.returncode == 0 or fragment not in diagnostic:
            raise AssertionError((program, diagnostic))
    return len(programs)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--levels", type=int, nargs="+", default=[0, 2, 3])
    parser.add_argument("--services-only", action="store_true")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="stdlib-policy-", dir=scratch) as name:
        directory: Path = Path(name)
        collection_routes: int = 0
        collection_outputs: int = 0
        collection_rejections: int = 0
        if not arguments.services_only:
            collections: Path = directory / "collections"
            collections.mkdir()
            collection_routes, collection_outputs = execute_collections(
                binary, collections, arguments.levels
            )
            collection_rejections = reject_collections(binary, collections)
        service_routes: int
        service_outputs: int
        service_routes, service_outputs = execute_services(
            binary, directory / "services", arguments.levels
        )
        rejections: int = reject_services(binary, directory)
    report: dict[str, object] = {
        "binary": str(binary),
        "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "managed_assemblies": {
            name: hashlib.sha256((binary.parent / name).read_bytes()).hexdigest()
            for name in (
                "msync.dll",
                "Msync.Compiler.dll",
                "Msync.Core.dll",
                "Msync.Runtime.dll",
            )
            if (binary.parent / name).exists()
        },
        "platform": platform.platform(),
        "staged_executions": collection_routes + service_routes,
        "byte_observations": collection_outputs + service_outputs,
        "negative_diagnostics": collection_rejections + rejections,
    }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
