#!/usr/bin/env python3
"""Run local checks selected by changed contracts, without imposing CI gates."""

from __future__ import annotations

import argparse
import subprocess
import sys
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def execute(arguments: Sequence[str]) -> None:
    print("+ " + " ".join(arguments), flush=True)
    subprocess.run(arguments, cwd=ROOT, check=True)


def git_paths(arguments: Sequence[str]) -> set[str]:
    result = subprocess.run(
        ["git", *arguments], cwd=ROOT, check=True, capture_output=True, text=True
    )
    return set(result.stdout.splitlines())


def changed_paths(base: str | None) -> set[str]:
    paths = git_paths(["diff", "--name-only", "HEAD"])
    paths.update(git_paths(["ls-files", "--others", "--exclude-standard"]))
    if base is not None:
        paths.update(git_paths(["diff", "--name-only", f"{base}...HEAD"]))
    return paths


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", help="Include committed changes since this revision")
    parser.add_argument("--skip-format", action="store_true")
    parser.add_argument(
        "--journey",
        action="append",
        choices=["package", "language", "configuration", "telemetry", "cli", "lsp", "editor"],
        default=[],
        help="Run an explicitly selected real-process journey, not a full regression",
    )
    args = parser.parse_args()
    paths = changed_paths(args.base)
    cli_project = "src/Msync.Cli/Msync.Cli.csproj"
    execute(["dotnet", "build", cli_project, "-m:1", "-nr:false", "--nologo"])
    csharp = sorted(
        path
        for path in paths
        if path.endswith(".cs")
        and path.startswith(("src/", "tests/"))
        and (ROOT / path).is_file()
    )
    if csharp and not args.skip_format:
        execute(
            [
                "dotnet",
                "format",
                "whitespace",
                "msync.slnx",
                "--no-restore",
                "--verify-no-changes",
                "--include",
                *csharp,
            ]
        )
    native = "msync.exe" if sys.platform == "win32" else "msync"
    binary = str(ROOT / "src/Msync.Cli/bin/Debug/net10.0" / native)
    commands: dict[str, list[str]] = {
        "package": [sys.executable, "packaging/smoke.py", "--binary", binary],
        "language": [
            sys.executable,
            "tests/Acceptance/language_journeys.py",
            "--binary",
            binary,
        ],
        "configuration": [
            sys.executable,
            "tests/Configuration/cli_journeys.py",
            binary,
        ],
        "telemetry": [
            sys.executable,
            "tests/Telemetry/journeys.py",
            binary,
        ],
        "cli": [sys.executable, "tests/Msync.EndToEnd/journeys.py", "--binary", binary],
        "lsp": [sys.executable, "tests/Msync.EndToEnd/lsp_journey.py", "--binary", binary],
        "editor": ["npm", "--prefix", "editors/vscode", "test"],
    }
    for journey in dict.fromkeys(args.journey):
        execute(commands[journey])


if __name__ == "__main__":
    main()
