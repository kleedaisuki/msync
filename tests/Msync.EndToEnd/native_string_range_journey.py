#!/usr/bin/env python3
"""Exercise native String Range transport through actual files and artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path
from statistics import median
from tempfile import TemporaryDirectory
from time import perf_counter_ns

from native_number_range_journey import ROOT, invoke, receipts


@dataclass(frozen=True, slots=True)
class Fixture:
    name: str
    script: str
    input: bytes
    output: bytes


def fixtures(count: int) -> tuple[Fixture, Fixture, Fixture, Fixture]:
    words: tuple[str, ...] = ("", "Klee", "😀", "汉é", "𐀀", "alpha/beta")
    tokens: list[str] = ["", *(words[index % len(words)] for index in range(count)), ""]
    split = Fixture(
        "split-join",
        """
        let io = $std::io;
        fn tokenize(input: String) { $std::string::split(input, "<>") };
        let parts = tokenize(io::read_text(./input.txt));
        io::print($std::string::join(parts, "・"));
        """,
        "<>".join(tokens).encode(),
        "・".join(tokens).encode(),
    )

    lines: list[str] = ["first", "A\u2028B\u0085C", ""]
    endings: tuple[str, ...] = ("\r\n", "\n", "\r")
    source: str = "".join(
        line + endings[index % len(endings)]
        for index, line in enumerate([*lines, *tokens])
    )
    # StringReader's documented terminators are CR, LF and CRLF, not every
    # separator that Python str.splitlines recognizes.
    expected: list[str] = re.split(r"\r\n|\r|\n", source)[:-1]
    snapshot = Fixture(
        "line-snapshot",
        """
        let io = $std::io;
        let ranges = $std::ranges;
        fn lines(input: String) { $std::string::lines(input) };
        let snapshot = ranges::materialize(lines(io::read_text(./input.txt)));
        let frozen = ranges::materialize(snapshot);
        io::print($std::string::join(frozen, "⏎"));
        io::print("|");
        io::print(ranges::size(frozen) as String);
        io::print("|");
        io::print(ranges::at(frozen, 1));
        """,
        source.encode(),
        ("⏎".join(expected) + f"|{len(expected)}|{expected[1]}").encode(),
    )
    streamed = Fixture(
        "file-line-stream",
        """
        let io = $std::io;
        let ranges = $std::ranges;
        fn read_rows(path: Path) { io::read_lines(path) };
        let stream = read_rows(./input.txt);
        io::print(ranges::at(stream, 1));
        io::print("|");
        io::print($std::string::join(stream, "⏎"));
        """,
        source.encode(),
        (expected[1] + "|" + "⏎".join(expected)).encode(),
    )
    generic = Fixture(
        "generic-file-loop",
        """
        let io = $std::io;
        let scalars = 0;
        for line in io::read_lines(./input.txt) {
            scalars = scalars + $std::string::length(line);
        };
        io::print(scalars as String);
        """,
        source.encode(),
        str(sum(len(line) for line in expected)).encode(),
    )
    return split, snapshot, streamed, generic


def expect(run: subprocess.CompletedProcess[bytes], expected: bytes = b"") -> None:
    if run.returncode or run.stderr or run.stdout != expected:
        actual_digest: str = hashlib.sha256(run.stdout).hexdigest()
        expected_digest: str = hashlib.sha256(expected).hexdigest()
        raise AssertionError(
            (run.args, run.returncode, run.stderr, actual_digest, expected_digest)
        )


def exercise(
    fixture: Fixture,
    work: Path,
    hosts: dict[str, Path],
    rounds: int,
) -> dict[str, object]:
    source: Path = work / "source"
    detached: Path = work / "detached"
    for route in (source, detached):
        route.mkdir(parents=True)
        (route / "text.msync").write_text(
            fixture.script, encoding="utf-8", newline="\n"
        )
        (route / "input.txt").write_bytes(fixture.input)
    builder: Path = next(iter(hosts.values()))
    expect(
        invoke(
            builder,
            detached,
            ["--no-telemetry", "compile", "text.msync", "-o", "text.mso"],
        )
    )
    (detached / "text.msync").unlink()
    expect(
        invoke(
            builder,
            detached,
            ["--no-telemetry", "bind-ir", "text.mso", "-o", "text.mexe"],
        )
    )
    assert not list(detached.glob("*.msync"))

    profiles: dict[str, dict[str, dict[str, int]]] = {}
    for label, binary in hosts.items():
        profiles[label] = {}
        for kind, folder, filename in (
            ("source", source, "text.msync"),
            ("mso", detached, "text.mso"),
            ("mexe", detached, "text.mexe"),
        ):
            database: Path = work / f"{label}-{kind}.sqlite"
            expect(
                invoke(
                    binary,
                    folder,
                    [
                        "--telemetry-db",
                        str(database),
                        "--telemetry-level",
                        "profile",
                        "run",
                        filename,
                    ],
                ),
                fixture.output,
            )
            profiles[label][kind] = receipts(database)

    wall: dict[str, list[int]] = {label: [] for label in hosts}
    labels: tuple[str, ...] = tuple(hosts)
    for trial in range(rounds):
        for label in labels if trial % 2 == 0 else labels[::-1]:
            start: int = perf_counter_ns()
            expect(
                invoke(hosts[label], detached, ["--no-telemetry", "run", "text.mexe"]),
                fixture.output,
            )
            wall[label].append(perf_counter_ns() - start)
    return {
        "input_bytes": len(fixture.input),
        "output_bytes": len(fixture.output),
        "profile": profiles,
        "bound_wall_ns": wall,
        "bound_wall_median_ns": {
            label: median(samples) for label, samples in wall.items() if samples
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--count", type=int, default=120_000)
    parser.add_argument("--rounds", type=int, default=0)
    parser.add_argument(
        "--case",
        action="append",
        choices=(
            "split-join",
            "line-snapshot",
            "file-line-stream",
            "generic-file-loop",
        ),
    )
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    if args.count < 1 or args.rounds < 0:
        parser.error("--count must be positive and --rounds must be nonnegative")
    candidate: Path = args.binary.resolve(strict=True)
    hosts: dict[str, Path] = {"candidate": candidate}
    if args.baseline is not None:
        hosts = {"baseline": args.baseline.resolve(strict=True), **hosts}

    (ROOT / ".temp").mkdir(exist_ok=True)
    with TemporaryDirectory(prefix="string-range-", dir=ROOT / ".temp") as temporary:
        work: Path = Path(temporary)
        results: dict[str, object] = {
            fixture.name: exercise(fixture, work / fixture.name, hosts, args.rounds)
            for fixture in fixtures(args.count)
            if args.case is None or fixture.name in args.case
        }
    report: str = json.dumps(
        {
            "generated_values": args.count,
            "binary_sha256": {
                label: hashlib.sha256(path.read_bytes()).hexdigest()
                for label, path in hosts.items()
            },
            "fixtures": results,
        },
        indent=2,
    )
    if args.output is not None:
        output: Path = args.output
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(report + "\n", encoding="utf-8")
    print(report)


if __name__ == "__main__":
    main()
