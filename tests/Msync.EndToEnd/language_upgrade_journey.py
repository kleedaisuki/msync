#!/usr/bin/env python3
"""Exercise the integrated language contract through real staged CLI execution."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
import tempfile
from pathlib import Path

from stdlib_experience_journey import accept, invoke

ROOT = Path(__file__).resolve().parents[2]

PROGRAM = r"""
concept Semigroup {
    combine: fn(left: typename Self, right: Self) -> Self,
};
concept Monoid {
    Semigroup,
    identity: Self,
};
scope TextConcat {
    Self: String,
    identity: "",
    combine: fn(left: String, right: String) -> String {
        $std::string::concat(left, right)
    },
};
fn constrained(operations: Monoid) -> Monoid { operations };
constrained(TextConcat)::combine("Klee", "!") -> ./concept.txt;
(((.5*2)+(0x10%3)) as String) -> ./compact.txt;
((1e3/4) as String) -> ./exponent.txt;
((1_000+0o10+0b10) as String) -> ./bases.txt;
let nan = 0/0;
if nan != nan and not (nan < 0) and 1/2 == .5 and -7%4 == -3
    and 2**3**2 == 512 and $std::math::signbit(-0)
    and $std::ranges::empty($std::ranges::take([], 2147483648)) {
    "numeric-ok" -> ./numeric-semantics.txt;
};
fn named(value: String) {
    scope Saved { item: value };
    Saved
};
let first = named("before");
first::item = "shared";
named("ignored")::item -> ./singleton.txt;
fn fresh(value: String) { scope { item: value } };
let changed = fresh("before");
changed::item = "changed";
fresh("fresh")::item -> ./anonymous.txt;
let source = "./input data.bin" as Path;
$local @ source -> ./copied.bin;
let order = 0;
fn endpoint() { global order; order = order * 10 + 2; $local };
fn pathname() { global order; order = order * 10 + 1; ./copied.bin };
let qualified = endpoint() @ pathname();
(order as String) -> ./qualification-order.txt;
for selected in $local @ ./fixture/<.*> { selected -> ./qualified-selection.txt };
acquire;
"""

OUTPUTS: dict[str, bytes] = {
    "concept.txt": b"Klee!",
    "compact.txt": b"2",
    "exponent.txt": b"250",
    "bases.txt": b"1010",
    "numeric-semantics.txt": b"numeric-ok",
    "singleton.txt": b"shared",
    "anonymous.txt": b"fresh",
    "copied.bin": bytes(range(256)) + b"\x00\r\n",
    "qualification-order.txt": b"21",
    "qualified-selection.txt": b"selected after qualification",
}

NEGATIVE: tuple[tuple[str, str, tuple[str, ...]], ...] = (
    (
        "missing-concept-member",
        (
            "concept Named { name: String, }; fn use(value: Named) { value }; "
            'use(scope { other: "not a name" });'
        ),
        ("name",),
    ),
    ("forbidden-cast", 'let impossible = "not a process" as Process;', ("Process",)),
    ("no-implicit-number-text", "let text: String = 42;", ("String",)),
    ("old-endpoint-operator", "$local::./file;", ("@",)),
    ("old-qualification-order", "./file @ $local;", ("connection @ path",)),
)


def execute(binary: Path, parent: Path, levels: list[int]) -> int:
    executions: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            (directory / "input data.bin").write_bytes(OUTPUTS["copied.bin"])
            (directory / "fixture").mkdir()
            (directory / "fixture" / "entry.txt").write_bytes(
                OUTPUTS["qualified-selection.txt"]
            )
            source: Path = directory / "language 体验.msync"
            source.write_text(PROGRAM, encoding="utf-8")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"), "check"
            )
            assert not any((directory / name).exists() for name in OUTPUTS)
            if stage == "source":
                result: subprocess.CompletedProcess[str] = invoke(
                    binary, directory, "run", str(source), f"-O{level}"
                )
            else:
                artifact: Path = source.with_suffix(".mso")
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
                accept(
                    invoke(binary, directory, "disassemble", str(artifact), "--json"),
                    "disassemble",
                )
                source.unlink()
                launch: Path = (
                    directory / "different launch directory"
                    if stage == "executable"
                    else directory
                )
                launch.mkdir(exist_ok=True)
                result = invoke(binary, launch, "run-ir", str(artifact))
            accept(result, f"O{level} {stage}")
            for name, expected in OUTPUTS.items():
                actual: bytes = (directory / name).read_bytes()
                assert actual == expected, (level, stage, name, actual, expected)
            executions += 1
    return executions


def negative(binary: Path, parent: Path) -> int:
    directory: Path = parent / "negative"
    directory.mkdir()
    for name, text, expected in NEGATIVE:
        source: Path = directory / f"{name}.msync"
        source.write_text(text, encoding="utf-8")
        result: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "check", str(source)
        )
        assert result.returncode != 0, (name, result.stdout, result.stderr)
        assert all(word in result.stderr for word in expected), (name, result.stderr)
        assert "error" in result.stderr.lower(), (name, result.stderr)
    return len(NEGATIVE)


def artifact_versions(binary: Path, parent: Path) -> int:
    directory: Path = parent / "artifact-format"
    directory.mkdir()
    source: Path = directory / "current.msync"
    source.write_text("1;\n", encoding="utf-8")
    artifact: Path = source.with_suffix(".mso")
    accept(
        invoke(binary, directory, "compile", str(source), "-o", str(artifact)),
        "current format compile",
    )
    image: bytes = artifact.read_bytes()
    assert image[:8] == b"MSYNCBC\0" and int.from_bytes(image[8:10], "little") == 5
    source.unlink()
    accept(
        invoke(binary, directory, "run-ir", str(artifact)), "source-free current format"
    )
    for version in (1, 2, 3, 4):
        obsolete: Path = directory / f"obsolete-{version}.mso"
        obsolete.write_bytes(image[:8] + version.to_bytes(2, "little") + image[10:])
        result: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "run-ir", str(obsolete)
        )
        assert result.returncode != 0, (version, result.stdout, result.stderr)
        assert f"format {version}" in result.stderr and "Recompile" in result.stderr, (
            result.stderr
        )
    return 5


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--levels", type=int, choices=(0, 1, 2, 3), nargs="+", default=[0, 2, 3]
    )
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    scratch: Path = ROOT / ".temp/language-upgrade-delivery"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="integrated-", dir=scratch) as name:
        directory: Path = Path(name)
        report: dict[str, object] = {
            "binary": str(binary),
            "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "managed_components": {
                str(path.name): hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(binary.parent.glob("*.dll"))
            },
            "platform": platform.platform(),
            "staged_program_executions": execute(binary, directory, arguments.levels),
            "negative_diagnostics": negative(binary, directory),
            "artifact_version_contracts": artifact_versions(binary, directory),
        }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
