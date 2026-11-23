"""Checked AB/BA execution of one HPIR image, with an optional packaged 0.9.3 lane."""

from __future__ import annotations

import argparse
import json
import os
import platform
import statistics
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from time import perf_counter_ns

HERE: Path = Path(__file__).resolve().parent
ROOT: Path = HERE.parents[2]
PROMOTED_LINUX_093: str = (
    "9edc10c0071b92dc16659f6546f367c21caaabcda9a241bdae36637c4c7c564b"
)
sys.path.insert(0, str(HERE.parent / "Corpus"))
from compute import _hysteresis
from run import Variant, digest, file_digest, freeze, payload


@dataclass(frozen=True)
class Command:
    name: str
    argv: tuple[str, ...]

    def invoke(self, cwd: Path, expected: bytes | None) -> float:
        start: int = perf_counter_ns()
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            self.argv,
            cwd=cwd,
            capture_output=True,
            timeout=180,
            check=False,
            env={**os.environ, "MSYNC_JIT": "auto"},
        )
        elapsed: float = (perf_counter_ns() - start) / 1e6
        if result.returncode != 0 or (
            expected is not None and result.stdout != expected
        ):
            raise RuntimeError(
                f"{self.argv!r} in {cwd}: exit {result.returncode}; "
                f"expected={expected!r}, stdout={result.stdout!r}, stderr={result.stderr!r}"
            )
        return elapsed


def comparison(
    before: Command,
    native: Command,
    expected: bytes,
    cwd: Path,
    pairs: int,
    warmups: int,
) -> dict[str, object]:
    samples: list[dict[str, float]] = []
    for index in range(pairs + warmups):
        order: tuple[Command, Command] = (
            (before, native) if index % 2 == 0 else (native, before)
        )
        pair: dict[str, float] = {arm.name: arm.invoke(cwd, expected) for arm in order}
        if index >= warmups:
            samples.append(pair)
    return {
        "commands": {arm.name: arm.argv for arm in (before, native)},
        "raw_pairs_ms": samples,
        "p50_ms": {
            arm.name: statistics.median(row[arm.name] for row in samples)
            for arm in (before, native)
        },
        "paired_p50_native_change": statistics.median(
            row[native.name] / row[before.name] - 1 for row in samples
        ),
    }


def fixture(
    directory: Path, size: int, seed: int, long_bursts: bool
) -> tuple[Path, bytes]:
    period, start, end = (1024, 512, 1024) if long_bursts else (128, 64, 96)
    template: str = (HERE / "ready_signal.msync").read_text(encoding="utf-8")
    if template.count("run(1024, 17);") != 1:
        raise RuntimeError("Expected the single size/seed marker in ready_signal.msync")
    source: str = (
        template.replace("run(1024, 17);", f"run({size}, {seed % 65536});")
        .replace("ordinal % 128", f"ordinal % {period}")
        .replace("phase >= 64 and phase < 96", f"phase >= {start} and phase < {end}")
    )
    path: Path = directory / "ready_signal.msync"
    path.write_text(source, encoding="utf-8")
    expected: bytes = _hysteresis(
        directory, size, seed, period=period, start=start, end=end
    ).stdout.encode()
    return path, expected


def run(args: argparse.Namespace) -> dict[str, object]:
    work: Path = args.work.resolve()
    if not work.is_relative_to(ROOT / ".temp"):
        raise ValueError("--work must be a new directory under the repository .temp")
    work.mkdir(parents=True)
    candidate: Path = args.candidate_aot.resolve()
    if candidate.with_suffix(".runtimeconfig.json").exists():
        raise ValueError("Use the Native AOT candidate, not the managed apphost")
    runner: Variant = freeze(Variant("candidate", candidate), work)
    release: Variant | None = (
        freeze(Variant("release", args.published.resolve()), work)
        if args.published
        else None
    )
    images: tuple[Variant, ...] = (runner,) if release is None else (runner, release)
    original: dict[str, dict[str, str]] = {
        image.name: payload(image.binary.parent) for image in images
    }
    if (
        release is not None
        and original["release"][release.binary.name] != PROMOTED_LINUX_093
    ):
        raise ValueError("--published is not the promoted v0.9.3 Linux x64 executable")
    results: list[dict[str, object]] = []
    for size in args.size or [4096, 65536]:
        directory: Path = work / str(size)
        directory.mkdir()
        source, expected = fixture(directory, size, args.seed, args.long_bursts)
        source_hash: str = file_digest(source)
        portable: Path = directory / "hpir.mso"
        bound: Path = directory / "hpir.mexe"
        cli: tuple[str, ...] = (
            ()
            if release is None
            else (str(release.binary), "--no-config", "--no-telemetry")
        )
        prep: dict[str, float] = {
            "hpir_compile_ms": Command(
                "hpir_compile",
                (str(runner.binary), "hpir-block-compile", str(source), str(portable)),
            ).invoke(directory, None)
        }
        if release is not None:
            prep["release_compile_ms"] = Command(
                "release_compile",
                (*cli, "compile", "-O2", str(source), "-o", "release.mso"),
            ).invoke(directory, None)
        source.unlink()
        prep["hpir_bind_ms"] = Command(
            "hpir_bind",
            (str(runner.binary), "hpir-block-bind", str(portable), str(bound)),
        ).invoke(directory, None)
        if release is not None:
            prep["release_bind_ms"] = Command(
                "release_bind", (*cli, "bind-ir", "release.mso", "-o", "release.mexe")
            ).invoke(directory, None)
        cwd: Path = directory / "detached"
        cwd.mkdir()
        arm: tuple[str, ...] = (str(runner.binary), "hpir-block-run", str(bound))
        native: Command = Command("hpir_native", (*arm, "native"))
        interpreted: Command = Command("hpir_interpreted", (*arm, "interpreted"))
        case: dict[str, object] = {
            "rows": size,
            "source_sha256": source_hash,
            "expected_sha256": digest(expected),
            "artifact_sha256": {
                x.name: file_digest(x)
                for x in sorted(directory.iterdir())
                if x.suffix in {".mso", ".mexe"} and x.is_file()
            },
            "preparation": prep,
            "native_vs_same_hpir": comparison(
                interpreted, native, expected, cwd, args.pairs, args.warmups
            ),
        }
        if release is not None:
            old: Command = Command(
                "released_093", (*cli, "run", str(directory / "release.mexe"))
            )
            case["native_vs_release"] = comparison(
                old, native, expected, cwd, args.pairs, args.warmups
            )
        results.append(case)
    if any(payload(image.binary.parent) != original[image.name] for image in images):
        raise RuntimeError("A frozen benchmark image changed")
    return {
        "schema": 1,
        "platform": platform.platform(),
        "affinity": sorted(os.sched_getaffinity(0)),
        "configuration": {
            "seed": args.seed,
            "pairs": args.pairs,
            "warmups": args.warmups,
            "long_bursts": args.long_bursts,
            "msync_jit": "auto",
            "timing_scope": "fresh bound process, includes native emission/publication",
        },
        "payload_sha256": original,
        "cases": results,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate-aot", type=Path, required=True)
    parser.add_argument("--published", type=Path)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--cpu", type=int, required=True)
    parser.add_argument("--size", action="append", type=int)
    parser.add_argument("--seed", type=int, default=17)
    parser.add_argument("--pairs", type=int, default=7)
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--long-bursts", action="store_true")
    args: argparse.Namespace = parser.parse_args()
    if args.pairs < 1 or args.warmups < 0 or any(size < 1 for size in args.size or []):
        parser.error("sizes/pairs must be positive, warmups may be zero")
    os.sched_setaffinity(0, {args.cpu})
    result: dict[str, object] = run(args)
    (args.work / "result.json").write_text(
        json.dumps(result, indent=2) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    main()
