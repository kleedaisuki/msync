#!/usr/bin/env python3
"""Read the embedded progressive skill and compile its examples as a real user."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import tempfile
from collections import deque
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CHILD = re.compile(r"^- `msync skill ([a-z ]+)`:\s", re.MULTILINE)
CODE = re.compile(r"^```msync\n(.*?)^```", re.MULTILINE | re.DOTALL)
ANSI = re.compile(r"\x1b\[[0-9;]*m")


def invoke(
    binary: Path,
    arguments: Sequence[str],
    directory: Path,
    environment: dict[str, str],
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), *arguments],
        cwd=directory,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )


def run(binary: Path, directory: Path) -> dict[str, int]:
    home = directory / "home"
    home.mkdir()
    environment = dict(os.environ)
    environment.pop("NO_COLOR", None)
    environment.update(
        {
            "HOME": str(home),
            "USERPROFILE": str(home),
            "APPDATA": str(home / "appdata"),
            "XDG_CONFIG_HOME": str(home / "config"),
            "XDG_DATA_HOME": str(home / "data"),
            "XDG_STATE_HOME": str(home / "state"),
        }
    )
    pending: deque[tuple[str, ...]] = deque([()])
    pages: dict[tuple[str, ...], str] = {}
    examples = 0
    while pending:
        topic = pending.popleft()
        if topic in pages:
            continue
        result = invoke(binary, ["skill", *topic], directory, environment)
        assert result.returncode == 0, (topic, result.stdout, result.stderr)
        assert not result.stderr, (topic, result.stderr)
        assert "\x1b" not in result.stdout, topic
        assert result.stdout.startswith("#") or result.stdout.startswith("---\n"), topic
        pages[topic] = result.stdout
        pending.extend(tuple(child.split()) for child in CHILD.findall(result.stdout))
        snippets = CODE.findall(result.stdout)
        if snippets:
            source = directory / f"example-{'-'.join(topic) or 'overview'}.msync"
            program = (
                f"let module = $std::{topic[1]};\n"
                if len(topic) == 2 and topic[0] == "library"
                else "\n".join(snippets)
            )
            source.write_text(program, encoding="utf-8")
            checked = invoke(
                binary,
                ["--no-config", "--no-telemetry", "check", str(source)],
                directory,
                environment,
            )
            assert checked.returncode == 0, (topic, checked.stdout, checked.stderr)
            examples += len(snippets)
    assert ("language", "functions") in pages and ("workflow", "artifacts") in pages
    assert ("paths", "connections") in pages and ("library", "fs") in pages
    for topic in ("numbers", "conversions", "concepts"):
        assert ("language", topic) in pages, topic
    assert ("library", "cast") not in pages
    root = pages[()]
    assert "name: msync" in root and "description:" in root
    assert "msync skill language" in root and len(root.splitlines()) < 80, root
    assert "Null" in pages[("language", "types")]
    assert "means **file is null**" in pages[("language", "types")]
    assert "Scope" in pages[("language", "scopes")]
    assert "singleton" in pages[("language", "scopes")]
    assert "path @ connection" in pages[("paths", "connections")]
    assert " as Number" in pages[("language", "conversions")]
    assert "type Self;" in pages[("language", "concepts")]
    for module in ("collections", "text", "regex", "files", "commands", "environment"):
        assert ("library", module) in pages, module

    syntax = invoke(
        binary, ["--color", "never", "help", "syntax"], directory, environment
    )
    assert syntax.returncode == 0, syntax.stderr
    assert " as Number" in syntax.stdout and "concept Named" in syntax.stdout
    assert "path @ connection" in syntax.stdout and "Connection::" not in syntax.stdout

    fs = pages[("library", "fs")]
    assert "native fn exists(path: Path | String) -> Bool;" in fs
    assert "native fn directory_iterator" in fs
    for topic in ((), ("language", "functions")):
        colored = invoke(
            binary, ["--color", "always", "skill", *topic], directory, environment
        )
        assert colored.returncode == 0 and ANSI.search(colored.stdout), colored
        assert ANSI.sub("", colored.stdout) == pages[topic]
        plain = invoke(
            binary, ["skill", *topic, "--color", "never"], directory, environment
        )
        assert plain.returncode == 0 and plain.stdout == pages[topic], plain
        disabled = invoke(
            binary,
            ["--color", "always", "skill", *topic],
            directory,
            {**environment, "NO_COLOR": "1"},
        )
        assert disabled.returncode == 0 and disabled.stdout == pages[topic], disabled

    slash = invoke(binary, ["skill", "language/functions"], directory, environment)
    assert slash.returncode == 0 and slash.stdout == pages[("language", "functions")]
    invalid = invoke(binary, ["skill", "language", "functons"], directory, environment)
    assert invalid.returncode != 0 and invalid.stdout == ""
    assert (
        "Unknown skill topic" in invalid.stderr
        and "language functions" in invalid.stderr
    )
    assert "msync skill language" in invalid.stderr

    config = home / ".msync"
    config.mkdir()
    leaf = invoke(
        binary, ["skill", "language", "functions", "more"], directory, environment
    )
    assert leaf.returncode != 0 and leaf.stdout == ""
    assert "no subtopics" in leaf.stderr and "Available topics: ." not in leaf.stderr
    assert "msync skill language functions" in leaf.stderr
    broken = config / "config.toml"
    broken.write_text("this is not valid toml [", encoding="utf-8")
    for arguments in (
        ["skill"],
        ["--config", str(broken), "skill"],
        ["skill", "--config", str(directory / "missing.toml")],
    ):
        unaffected = invoke(binary, arguments, directory, environment)
        assert unaffected.returncode == 0 and unaffected.stdout == root, unaffected
        assert not unaffected.stderr, unaffected.stderr
    assert set(home.rglob("*")) == {config, broken}, list(home.rglob("*"))
    assert not (directory / "backup.txt").exists()
    payload = b"skill example\x00\xff\r\n"
    (directory / "input.txt").write_bytes(payload)
    executed = invoke(
        binary,
        ["--no-config", "--no-telemetry", "run", "example-overview.msync"],
        directory,
        environment,
    )
    assert executed.returncode == 0, executed.stderr
    assert (directory / "backup.txt").read_bytes() == payload
    return {
        "pages": len(pages),
        "compiled_example_blocks": examples,
        "executed_copy_examples": 1,
        "color_modes": 6,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    arguments = parser.parse_args()
    scratch = ROOT / ".temp" / "skill-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="read-", dir=scratch) as temporary:
        result = run(arguments.binary.resolve(), Path(temporary))
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
