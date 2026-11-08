#!/usr/bin/env python3
"""Focused real-process regressions discovered during the 0.8 review."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from collections.abc import Callable
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class Journey:
    def __init__(self, binary: Path, directory: Path, artifacts: bool) -> None:
        self.binary = binary
        self.directory = directory
        self.artifacts = artifacts
        self.environment = dict(os.environ)
        self.environment.pop("MSYNC_SSH_CONFIG", None)

    def invoke(self, *arguments: str) -> None:
        result = subprocess.run(
            [str(self.binary), *arguments, "--no-config"],
            cwd=self.directory,
            env=self.environment,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        assert result.returncode == 0, (arguments, result.stdout, result.stderr)

    def run(self, source: str, verify: Callable[[], None]) -> None:
        script = self.directory / "journey.msync"
        script.write_text(source, encoding="utf-8")
        self.invoke("run", str(script))
        verify()
        if not self.artifacts:
            return
        portable = script.with_suffix(".mso")
        executable = script.with_suffix(".mexe")
        self.invoke("compile", str(script), "-o", str(portable))
        self.invoke("bind-ir", str(portable), "-o", str(executable))
        script.unlink()
        for artifact in (portable, executable):
            self.invoke("run-ir", str(artifact))
            verify()

    def expect(self, filename: str, content: bytes) -> None:
        observed = (self.directory / filename).read_bytes()
        assert observed == content, (filename, content, observed)


def posix_backslash(journey: Journey) -> None:
    if os.name == "nt":
        return
    for name, content in (("left\\", b"correct"), ("left", b"wrong")):
        directory = journey.directory / name
        directory.mkdir()
        (directory / "leaf").write_bytes(content)
    (journey.directory / "left" / "\\leaf").write_bytes(b"leading correct")

    def verify() -> None:
        journey.expect("out", b"correct")
        journey.expect("out-leading", b"leading correct")

    journey.run(
        '("./left\\" / "leaf") -> ./out;\n("./left" / "\\leaf") -> ./out-leading;\n',
        verify,
    )


def annotated_return(journey: Journey) -> None:
    (journey.directory / "input").write_bytes(b"actual file bytes")

    def verify() -> None:
        journey.expect("out-path", b"actual file bytes")
        journey.expect("out-string", b"./input")
        journey.expect("out-explicit-path", b"actual file bytes")
        journey.expect("out-explicit-string", b"./input")

    journey.run(
        'fn as_path() -> Path { "./input" };\n'
        "fn as_string() -> String { ./input };\n"
        'fn explicit_path() -> Path { return "./input"; };\n'
        "fn explicit_string() -> String { return ./input; };\n"
        "as_path() -> ./out-path; as_string() -> ./out-string;\n"
        "explicit_path() -> ./out-explicit-path;\n"
        "explicit_string() -> ./out-explicit-string;\n",
        verify,
    )


def recursive_capture(journey: Journey) -> None:
    journey.run(
        'let x = "outer";\n'
        "fn recurse(done: Bool) -> String {\n"
        '    if done { x } else { let x = "inner"; recurse(true) }\n'
        "};\n"
        "recurse(false) -> ./out;\n",
        lambda: journey.expect("out", b"outer"),
    )


def prepare_remote(journey: Journey) -> tuple[Path, Path, Path]:
    tools = journey.directory / "tools"
    home = journey.directory / "remote-home"
    tools.mkdir()
    home.mkdir()
    shim = tools / "ssh"
    shim.write_text(
        f"#!{sys.executable}\n"
        "from __future__ import annotations\n"
        "import json, os, subprocess, sys\n"
        "with open(os.environ['REVIEW_SSH_CALLS'], 'a', encoding='utf-8') as stream:\n"
        "    stream.write(json.dumps(sys.argv[1:]) + '\\n')\n"
        "raise SystemExit(subprocess.run(sys.argv[-1], shell=True, check=False).returncode)\n",
        encoding="utf-8",
    )
    shim.chmod(0o755)
    calls = journey.directory / "ssh-calls.jsonl"
    configuration = journey.directory / "ssh-config"
    configuration.write_text(
        "# Explicit native client configuration\n", encoding="utf-8"
    )
    journey.environment.update(
        PATH=str(tools) + os.pathsep + journey.environment["PATH"],
        HOME=str(home),
        MSYNC_SSH_CONFIG=str(configuration),
        REVIEW_SSH_CALLS=str(calls),
    )
    return home, calls, configuration


def remote_process(journey: Journey) -> None:
    if os.name == "nt":
        return
    home, calls, configuration = prepare_remote(journey)
    printf = shutil.which("printf")
    assert printf is not None
    shutil.copy2(printf, home / "tool")

    def verify() -> None:
        journey.expect("out", b"REMOTE OK")
        arguments: list[str] = json.loads(
            calls.read_text(encoding="utf-8").splitlines()[-1]
        )
        assert "-F" in arguments and arguments[arguments.index("-F") + 1] == str(
            configuration
        )
        assert "-p" in arguments and arguments[arguments.index("-p") + 1] == "2222"

    journey.run(
        "let remote = ssh://loopback:2222;\n"
        'let p = (remote::~/tool)("REMOTE OK");\n'
        "p::stdout -> ./out; p::stderr -> ./err;\n",
        verify,
    )


def remote_replace(journey: Journey) -> None:
    if os.name == "nt":
        return
    prepare_remote(journey)
    source = journey.directory / "source"
    target = journey.directory / "target"
    source.mkdir()
    target.mkdir()
    (source / "file").write_bytes(b"replacement bytes")
    (target / "obsolete").write_bytes(b"old bytes")

    def verify() -> None:
        journey.expect("target/file", b"replacement bytes")
        assert not (target / "obsolete").exists()

    journey.run(
        f'let remote = ssh://loopback; ./source -> remote::"{target}/.";\n',
        verify,
    )


def remote_overlap(journey: Journey) -> None:
    if os.name == "nt":
        return
    prepare_remote(journey)
    source = journey.directory / "source"
    source.mkdir()
    (source / "file").write_bytes(b"source snapshot bytes")

    def verify() -> None:
        journey.expect("source/backup/file", b"source snapshot bytes")
        assert not list((source / "backup").rglob(".msync-*"))

    journey.run(
        f'let remote = ssh://loopback; remote::"{source}" -> remote::"{source}/backup/.";\n',
        verify,
    )


CASES: dict[str, Callable[[Journey], None]] = {
    "posix-backslash": posix_backslash,
    "annotated-return": annotated_return,
    "recursive-capture": recursive_capture,
    "remote-process": remote_process,
    "remote-replace": remote_replace,
    "remote-overlap": remote_overlap,
}


def distribution_digest(directory: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(directory.rglob("*")):
        if not path.is_file():
            continue
        digest.update(path.relative_to(directory).as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--case", action="append", choices=tuple(CASES))
    parser.add_argument("--artifacts", action="store_true")
    args = parser.parse_args()
    temporary = ROOT / ".temp/review-v080"
    temporary.mkdir(parents=True, exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="regressions-", dir=temporary))
    original = args.binary.resolve()
    frozen = root / "binary"
    shutil.copytree(original.parent, frozen)
    binary = frozen / original.name
    selected: list[str] = args.case or list(CASES)
    outcomes: dict[str, str] = {}
    for name in selected:
        directory = root / name
        directory.mkdir()
        try:
            CASES[name](Journey(binary, directory, args.artifacts))
            outcomes[name] = "passed"
        except (AssertionError, OSError) as error:
            outcomes[name] = f"failed: {error}"
    report = {
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "distribution_sha256": distribution_digest(frozen),
        "fixtures": str(root),
        "outcomes": outcomes,
    }
    output = json.dumps(report, indent=2) + "\n"
    (root / "results.json").write_text(output, encoding="utf-8")
    print(output, end="")
    raise SystemExit(0 if all(value == "passed" for value in outcomes.values()) else 1)


if __name__ == "__main__":
    main()
