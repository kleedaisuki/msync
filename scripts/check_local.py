#!/usr/bin/env python3
"""Run local checks selected by changed contracts, without imposing CI gates."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import uuid
from collections.abc import Sequence
from pathlib import Path

from testing.build import BASE
from testing.catalog import catalog
from testing.model import Task

ROOT: Path = Path(__file__).resolve().parents[1]


def execute(arguments: Sequence[str]) -> None:
    print("+ " + " ".join(arguments), flush=True)
    subprocess.run(arguments, cwd=ROOT, check=True)


def git_paths(arguments: Sequence[str]) -> set[str]:
    result: subprocess.CompletedProcess[str] = subprocess.run(
        ["git", *arguments], cwd=ROOT, check=True, capture_output=True, text=True
    )
    return set(result.stdout.splitlines())


def changed_paths(base: str | None) -> set[str]:
    paths: set[str] = git_paths(["diff", "--name-only", "HEAD"])
    paths.update(git_paths(["ls-files", "--others", "--exclude-standard"]))
    if base is not None:
        paths.update(git_paths(["diff", "--name-only", f"{base}...HEAD"]))
    return paths


def format_command(csharp: list[str]) -> tuple[str, ...]:
    return (
        "dotnet",
        "format",
        "whitespace",
        "msync.slnx",
        "--no-restore",
        "--verify-no-changes",
        "--include",
        *csharp,
    )


def linux_checks(journeys: list[str], csharp: list[str]) -> None:
    selected: list[Task] = []
    if csharp:
        selected.append(
            Task(
                "format",
                format_command(csharp),
                exclusive=True,
                cwd="{root}",
                environment=(
                    ("ArtifactsPath", str(BASE)),
                    ("UseArtifactsOutput", "true"),
                ),
            )
        )
    entries: dict[str, Task] = catalog()
    selected.extend(entries[name] for name in dict.fromkeys(journeys))
    arguments: list[str] = [sys.executable, "scripts/test.py", "--fail-fast"]
    if not selected:
        execute([*arguments, "--prepare-only"])
        return
    directory: Path = ROOT / ".temp/test-execution"
    directory.mkdir(parents=True, exist_ok=True)
    plan: Path = directory / ("check-" + uuid.uuid4().hex + ".json")
    plan.write_text(
        json.dumps({"tasks": [task.describe() for task in selected]}), encoding="utf-8"
    )
    execute([*arguments, "--plan", str(plan)])


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", help="Include committed changes since this revision")
    parser.add_argument("--skip-format", action="store_true")
    parser.add_argument(
        "--journey",
        action="append",
        choices=(
            "package",
            "language",
            "configuration",
            "telemetry",
            "cli",
            "lsp",
            "polish",
            "editor",
        ),
        default=[],
        help="Run explicitly selected real-process journeys, not a full regression",
    )
    args: argparse.Namespace = parser.parse_args()
    paths: set[str] = changed_paths(args.base)
    csharp: list[str] = sorted(
        path
        for path in paths
        if path.endswith(".cs")
        and path.startswith(("src/", "tests/"))
        and (ROOT / path).is_file()
    )
    if args.skip_format:
        csharp = []
    if sys.platform.startswith("linux"):
        linux_checks(args.journey, csharp)
        return
    execute(
        [
            "dotnet",
            "build",
            "src/Msync.Cli/Msync.Cli.csproj",
            "-m:1",
            "-nr:false",
            "--nologo",
        ]
    )
    if csharp:
        execute(format_command(csharp))
    binary: Path = (
        ROOT
        / "src/Msync.Cli/bin/Debug/net10.0"
        / ("msync.exe" if sys.platform == "win32" else "msync")
    )
    entries: dict[str, Task] = catalog()
    for name in dict.fromkeys(args.journey):
        execute(
            [
                entries[name].render(value, binary, ROOT)
                for value in entries[name].command
            ]
        )


if __name__ == "__main__":
    main()
