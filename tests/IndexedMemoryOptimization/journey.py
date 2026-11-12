"""Verify selected indexed reads through source and serialized CLI routes."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

HERE: Path = Path(__file__).resolve().parent
ROOT: Path = HERE.parents[1]

SELECTED: str = """
let storage = $std::storage;
let values = storage::vector_new([10, 20]);
let alias = values;
let line? = $std::io::read_line();
let index = 0;
let before = if line? {
    let first = storage::vector_get(values, index);
    MUTATION
    first
} else {
    index = 1;
    storage::vector_get(values, index)
};
let after = storage::vector_get(values, index);
$std::io::println((before + after) as String);
"""


@dataclass(frozen=True)
class Journey:
    name: str
    source: str
    observations: tuple[tuple[str, str], ...]
    optimized_gets: int


def cli(binary: Path, cwd: Path, *args: str, text: str = "") -> str:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *args],
        cwd=cwd,
        input=text,
        text=True,
        capture_output=True,
        check=True,
        timeout=30,
    ).stdout


def verify(binary: Path, directory: Path, journey: Journey, level: int) -> None:
    source: Path = directory / f"{journey.name}.msync"
    portable: Path = directory / f"{journey.name}-O{level}.mso"
    executable: Path = portable.with_suffix(".mexe")
    source.write_text(journey.source, encoding="utf-8")
    cli(binary, directory, "compile", f"-O{level}", str(source), "-o", str(portable))
    cli(binary, directory, "bind-ir", str(portable), "-o", str(executable))
    for artifact in (portable, executable):
        text: str = cli(binary, directory, "disassemble", str(artifact))
        expected_gets: int = 3 if level == 0 else journey.optimized_gets
        assert text.count('NativeCall "storage.vector_get"') == expected_gets, (
            journey.name,
            level,
            artifact,
        )
    for route in (source, portable, executable):
        for input_text, expected in journey.observations:
            assert (
                cli(binary, directory, "run", f"-O{level}", str(route), text=input_text)
                == f"{expected}\n"
            ), (journey.name, level, route)
        route.unlink()


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    journeys: tuple[Journey, ...] = (
        Journey(
            "selected",
            SELECTED.replace("MUTATION", ""),
            (("", "20"), ("right\n", "40")),
            2,
        ),
        Journey(
            "alias-write",
            SELECTED.replace("MUTATION", "storage::vector_set(alias, index, 99);"),
            (("", "109"), ("right\n", "40")),
            3,
        ),
        Journey(
            "selected-loop",
            (HERE / "selected-loop.msync").read_text(),
            (("", "720896"),),
            2,
        ),
    )
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="indexed-join-", dir=scratch) as name:
        directory: Path = Path(name)
        for level in (0, 2, 3):
            for journey in journeys:
                verify(binary, directory, journey, level)
    print("PASS indexed join CLI journeys (O0/O2/O3; source, mso, mexe)")


if __name__ == "__main__":
    main()
