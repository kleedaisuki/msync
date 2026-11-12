"""Workload descriptions and independent expected results for the Native CLI corpus."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class OutputFile:
    path: str
    sha256: str
    initial: bytes | None = None


@dataclass(frozen=True)
class Fixture:
    source: str
    stdin: str = ""
    stdout: str = ""
    outputs: tuple[OutputFile, ...] = ()
    absent: tuple[str, ...] = ()
    output_roots: tuple[str, ...] = ()


@dataclass(frozen=True)
class Case:
    name: str
    family: str
    description: str
    default_size: int
    generate: Callable[[Path, int, int], Fixture]
