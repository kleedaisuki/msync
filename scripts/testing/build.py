"""Let MSBuild own incremental rebuild decisions in one serialized build tree."""

from __future__ import annotations

from pathlib import Path

from . import compiler
from .model import ROOT, Task

BASE: Path = ROOT / "artifacts/test-execution/build-tree"


def command(cpus: int) -> Task:
    return Task(
        "build",
        (
            "dotnet",
            "build",
            "{root}/src/Msync.Cli/Msync.Cli.csproj",
            "--artifacts-path",
            str(BASE),
            f"-m:{cpus}",
            "-nr:false",
            "-p:UseSharedCompilation=true",
            f"-p:SharedCompilationId={compiler.pipe()}",
            "-p:RestoreLockedMode=true",
            "--nologo",
        ),
        timeout=900,
        cwd="{root}",
        uses_compiler=True,
    )
