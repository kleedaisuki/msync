"""Check the guarded Object AOT region on a real corpus fixture."""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path

from journeys import Level, Profile, invoke, profile


def check_route(
    binary: Path,
    directory: Path,
    filename: str,
    stdin: bytes,
    expected: bytes,
    attempts: int,
    crossings: int | None,
    *,
    check_sites: bool,
) -> dict[str, dict[str, int]]:
    def run(options: list[str], *, native: bool) -> bytes:
        return invoke(
            binary,
            directory,
            [*options, "-O2", "run", filename],
            data=stdin,
            native=native,
        )

    if run(["--no-telemetry"], native=True) != expected:
        raise AssertionError(f"{directory.name}: unobserved output changed")
    profiles: dict[str, Profile] = {}
    for level in ("profile", "instructions") if check_sites else ("profile",):
        for native, mode in ((True, "auto"), (False, "off")):
            database: Path = directory / f"{level}-{mode}.sqlite"
            options: list[str] = [
                "--telemetry-db",
                str(database),
                "--telemetry-level",
                level,
            ]
            if run(options, native=native) != expected:
                raise AssertionError(f"{directory.name}/{level}-{mode}: output changed")
            level_name: Level = level
            snapshot: Profile = profile(database, level_name)
            if (
                snapshot.status != "ok"
                or "guarded_scope_regions" not in snapshot.metrics
            ):
                raise AssertionError(
                    f"{directory.name}/{level}-{mode}: missing execute profile"
                )
            hits: int = snapshot.metrics["guarded_scope_regions"]
            misses: int = snapshot.metrics["guarded_scope_misses"]
            crossing_hits: int = snapshot.metrics.get("guarded_scope_crossings", 0)
            crossing_misses: int = snapshot.metrics.get(
                "guarded_scope_crossing_misses", 0
            )
            if level == "profile":
                if hits == 0 or hits + misses != attempts:
                    raise AssertionError(
                        f"{directory.name}/{level}-{mode}: {hits=} {misses=} {attempts=}"
                    )
                if crossings is not None and (
                    crossing_hits != crossings or crossing_misses
                ):
                    raise AssertionError(
                        f"{directory.name}/{level}-{mode}: {crossing_hits=} {crossing_misses=} {crossings=}"
                    )
            elif hits or misses or crossing_hits or crossing_misses:
                raise AssertionError("instruction-site profile entered the AOT region")
            profiles[f"{level}-{mode}"] = snapshot

    profiles["profile-auto"].check_logical(profiles["profile-off"], "profile")
    if check_sites:
        profiles["instructions-auto"].check_logical(
            profiles["instructions-off"], "instructions"
        )
        profiles["profile-auto"].check_logical(profiles["instructions-auto"], "profile")
    return {name: snapshot.metrics for name, snapshot in profiles.items()}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--attempts", type=int, required=True)
    parser.add_argument("--crossings", type=int)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    fixture: Path = args.fixture.resolve(strict=True)
    work: Path = args.work.resolve()
    work.mkdir(parents=True)
    stdin: bytes = (fixture / "stdin.utf8").read_bytes()
    stdout: bytes = (fixture / "expected.utf8").read_bytes()
    build: Path = work / "prepare"
    build.mkdir()
    shutil.copyfile(fixture / "baseline.mso", build / "case.mso")
    invoke(binary, build, ["--no-telemetry", "bind-ir", "case.mso", "-o", "case.mexe"])
    files: dict[str, Path] = {
        "source": fixture / "workload.msync",
        "object": build / "case.mso",
        "bound": build / "case.mexe",
    }
    report: dict[str, dict[str, dict[str, int]]] = {}
    for route, source in files.items():
        directory: Path = work / route
        directory.mkdir()
        shutil.copyfile(source, directory / source.name)
        report[route] = check_route(
            binary,
            directory,
            source.name,
            stdin,
            stdout,
            args.attempts,
            args.crossings,
            check_sites=route == "bound",
        )
        metrics: dict[str, int] = report[route]["profile-auto"]
        print(
            f"PASS Scope region/{route}: {metrics['guarded_scope_regions']} prefixes, "
            f"{metrics.get('guarded_scope_crossings', 0)} crossings",
            flush=True,
        )
    (work / "result.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    main()
