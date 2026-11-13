"""Copy one consistent apphost payload; never share mutable build outputs."""

from __future__ import annotations

import hashlib
import shutil
from pathlib import Path


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def payload(binary: Path) -> dict[str, str]:
    return {
        path.relative_to(binary.parent).as_posix(): digest(path)
        for path in sorted(binary.parent.rglob("*"))
        if path.is_file()
        and (
            path == binary
            or path.suffix.lower() in {".dll", ".so", ".dylib"}
            or ".so." in path.name
            or path.name.endswith((".deps.json", ".runtimeconfig.json"))
        )
    }


def freeze(binary: Path, destination: Path) -> tuple[Path, dict[str, str]]:
    expected: dict[str, str] = payload(binary)
    for relative in expected:
        target: Path = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(binary.parent / relative, target)
    frozen: Path = destination / binary.name
    if payload(frozen) != expected or payload(binary) != expected:
        raise RuntimeError(f"Product payload changed while freezing {binary}")
    return frozen, expected
