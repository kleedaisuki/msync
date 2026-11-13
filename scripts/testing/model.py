"""Immutable task contracts; existing scripts own assertions and guest semantics."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Task:
    name: str
    command: tuple[str, ...]
    cpu: int = 1
    memory_mib: int = 512
    timeout: float = 300
    locks: tuple[str, ...] = ()
    exclusive: bool = False
    cwd: str = "{work}"
    environment: tuple[tuple[str, str], ...] = ()
    receipt: str | None = None
    uses_compiler: bool = False

    def render(self, value: str, binary: Path, work: Path) -> str:
        for key, path in (
            ("{root}", ROOT),
            ("{binary}", binary),
            ("{assemblies}", binary.parent),
            ("{work}", work),
        ):
            value = value.replace(key, str(path))
        return value

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> Task:
        return cls(
            **(
                value
                | {
                    "command": tuple(value["command"]),
                    "locks": tuple(value.get("locks", ())),
                    "environment": tuple(
                        tuple(pair) for pair in value.get("environment", ())
                    ),
                }
            )
        )

    def describe(self) -> dict[str, Any]:
        return asdict(self)
