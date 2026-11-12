"""Check literal Scope folds and a same-consumer effectful producer."""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path

from journeys import invoke, profile


def with_effect(source: str) -> str:
    lines: list[str] = source.splitlines(keepends=True)
    first: int = next(
        index
        for index, line in enumerate(lines)
        if line.lstrip().startswith("scope {") and line.rstrip().endswith(",")
    )
    row: str = lines[first].strip().removesuffix(",")
    lines[first] = f'    {{ $std::io::println("row-demand"); {row} }},\n'
    return "".join(lines)


def check_output(
    binary: Path, directory: Path, name: str, stdin: bytes, expected: bytes
) -> None:
    for native in (True, False):
        output: bytes = invoke(
            binary,
            directory,
            ["--no-telemetry", "-O2", "run", name],
            data=stdin,
            native=native,
        )
        if output != expected:
            raise AssertionError(f"{directory.name}: {output!r} != {expected!r}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--rows", type=int, required=True)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    fixture: Path = args.fixture.resolve(strict=True)
    work: Path = args.work.resolve()
    work.mkdir(parents=True)
    source: str = (fixture / "workload.msync").read_text(encoding="utf-8")
    stdin: bytes = (fixture / "stdin.utf8").read_bytes()
    expected: bytes = (fixture / "expected.utf8").read_bytes()
    queries: int = int(stdin.splitlines()[0])
    effect: str = with_effect(source)
    effect_output: bytes = b"row-demand\n" * queries + expected

    prepared: Path = work / "prepare"
    prepared.mkdir()
    shutil.copyfile(fixture / "baseline.mso", prepared / "literal.mso")
    (prepared / "effect.msync").write_text(effect, encoding="utf-8")
    invoke(
        binary,
        prepared,
        ["--no-telemetry", "-O2", "compile", "effect.msync", "-o", "effect.mso"],
    )
    (prepared / "effect.msync").unlink()
    for name in ("literal", "effect"):
        invoke(
            binary,
            prepared,
            ["--no-telemetry", "bind-ir", f"{name}.mso", "-o", f"{name}.mexe"],
        )

    for route, suffix in (("source", "msync"), ("object", "mso"), ("bound", "mexe")):
        directory: Path = work / route
        directory.mkdir()
        if route == "source":
            (directory / "literal.msync").write_text(source, encoding="utf-8")
            (directory / "effect.msync").write_text(effect, encoding="utf-8")
        else:
            for name in ("literal", "effect"):
                shutil.copyfile(
                    prepared / f"{name}.{suffix}", directory / f"{name}.{suffix}"
                )
        check_output(binary, directory, f"literal.{suffix}", stdin, expected)
        check_output(binary, directory, f"effect.{suffix}", stdin, effect_output)
        print(
            f"PASS readonly Scope fold/{route} and {queries} demanded effects",
            flush=True,
        )

    directory = work / "bound"
    profiles = {}
    for level in ("profile", "instructions"):
        database: Path = directory / f"{level}.sqlite"
        output = invoke(
            binary,
            directory,
            [
                "--telemetry-db",
                str(database),
                "--telemetry-level",
                level,
                "run",
                "literal.mexe",
            ],
            data=stdin,
        )
        if output != expected:
            raise AssertionError(f"{level}: observed output changed")
        if level == "profile":
            profiles["profile"] = profile(database, "profile")
        else:
            profiles["instructions"] = profile(database, "instructions")
    profiles["profile"].check_logical(profiles["instructions"], "profile")
    expected_factories: int = queries * args.rows
    for level, snapshot in profiles.items():
        if snapshot.metrics.get("literal_factories") != expected_factories:
            raise AssertionError(
                f"{level}: did not observe all {expected_factories} logical row pulls"
            )
    (work / "result.json").write_text(
        json.dumps(
            {
                "queries": queries,
                "rows": args.rows,
                "observed_literal_factories": expected_factories,
                "demanded_effects_per_route": queries,
                "routes": ["source", "object", "bound"],
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
