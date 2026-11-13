"""Contract checks for corpus selection, generators and frozen product ownership."""

from __future__ import annotations

import argparse
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from typing import Any, cast
from unittest.mock import patch

import collections_workloads
import run as corpus
import text_paths
from catalog import CORE_CASES, DIAGNOSTIC_CASES
from model import Case, Fixture

ROOT: Path = Path(__file__).resolve().parents[3]


class CorpusContracts(unittest.TestCase):
    def test_catalog_partitions_all_cases(self) -> None:
        names: list[str] = [case.name for case in corpus.CASES]
        self.assertEqual(len(names), len(set(names)))
        self.assertFalse(set(CORE_CASES) & set(DIAGNOSTIC_CASES))
        self.assertEqual(set(names), set(CORE_CASES) | set(DIAGNOSTIC_CASES))

    def test_current_sources_do_not_use_retired_allocator_builtins(self) -> None:
        temporary: Path = ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            root: Path = Path(name)
            for case in corpus.CASES:
                directory: Path = root / case.name
                directory.mkdir()
                fixture: Fixture = case.generate(directory, 16, 17)
                with self.subTest(case=case.name):
                    for module in ("storage", "arena", "slots"):
                        self.assertNotIn(f"$std::{module}::", fixture.source)

    def test_set_stream_preserves_independent_snapshot(self) -> None:
        for size in (1, 17, 128, 8192):
            for seed in (0, 17, 503):
                fixture: Fixture = collections_workloads._dedup(ROOT, size, seed)
                stream: list[int] = [int(line) for line in fixture.stdin.splitlines()]
                expected: list[int] = [
                    int(line) for line in fixture.stdout.splitlines()
                ]
                initial: set[int] = set(stream[:size])
                seen: set[int] = initial.copy()
                for encoded in stream[size:]:
                    if encoded % 2:
                        seen.discard(encoded // 2)
                self.assertEqual(len(stream), size + max(1, size // 2))
                self.assertEqual(expected[0] + expected[1], size)
                self.assertEqual(sum(expected[2:6]), max(1, size // 2))
                self.assertEqual(expected[6], len(seen))
                self.assertEqual(expected[8], len(initial))
                self.assertLessEqual(expected[6], expected[8])

    def test_cohort_copy_has_complete_unique_file_oracle(self) -> None:
        temporary: Path = ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        for seed in (17, 503):
            with tempfile.TemporaryDirectory(dir=temporary) as name:
                directory: Path = Path(name)
                fixture: Fixture = text_paths._local_range_copy(directory, 48, seed)
                sources: dict[str, Path] = {
                    path.name: path
                    for path in (directory / "input").rglob("*")
                    if path.is_file()
                }
                self.assertEqual(len(fixture.outputs), len(sources))
                self.assertEqual(fixture.stdout, f"{len(sources)}\n")
                self.assertEqual(
                    len({output.path for output in fixture.outputs}), len(sources)
                )
                for output in fixture.outputs:
                    self.assertEqual(Path(output.path).parent.as_posix(), "out")
                    self.assertEqual(
                        output.sha256,
                        hashlib.sha256(
                            sources[Path(output.path).name].read_bytes()
                        ).hexdigest(),
                    )
                self.assertTrue(fixture.absent)
                self.assertFalse(
                    set(fixture.absent) & {output.path for output in fixture.outputs}
                )

    def test_freeze_owns_adjacent_native_and_managed_dependencies(self) -> None:
        temporary: Path = ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            directory: Path = Path(name)
            source: Path = directory / "source"
            source.mkdir()
            binary: Path = source / "msync"
            binary.write_bytes(b"product")
            binary.chmod(0o755)
            (source / "library.so").write_bytes(b"native")
            (source / "msync.dll").write_bytes(b"managed")
            (source / "msync.deps.json").write_text("{}")
            (source / "msync.runtimeconfig.json").write_text("{}")
            (source / "unrelated.txt").write_text("not an executable dependency")
            (source / "credentials.json").write_text('{"token": "not a payload"}')
            (source / "runtimes/linux-x64/native").mkdir(parents=True)
            (source / "runtimes/linux-x64/native/other.so").write_bytes(b"nested")
            frozen: corpus.Variant = corpus.freeze(
                corpus.Variant("baseline", binary), directory
            )
            snapshot: dict[str, str] = corpus.payload(frozen.binary.parent)
            self.assertEqual(frozen.original, binary)
            self.assertEqual(frozen.binary.stat().st_mode & 0o777, 0o755)
            self.assertEqual(
                set(snapshot),
                {
                    "msync",
                    "library.so",
                    "msync.dll",
                    "msync.deps.json",
                    "msync.runtimeconfig.json",
                    "runtimes/linux-x64/native/other.so",
                },
            )
            binary.write_bytes(b"new build")
            (source / "library.so").write_bytes(b"replacement dependency")
            self.assertEqual(corpus.payload(frozen.binary.parent), snapshot)

    def test_failed_route_checkpoints_successful_samples_and_invocation(self) -> None:
        temporary: Path = ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        for completed in (0, 2):
            with tempfile.TemporaryDirectory(dir=temporary) as name:
                directory: Path = Path(name)
                image: Path = directory / "images/baseline/msync"
                image.parent.mkdir(parents=True)
                image.write_bytes(b"frozen product")
                variant: corpus.Variant = corpus.Variant("baseline", image)
                case: Case = Case(
                    "failure-probe",
                    "compute",
                    "test",
                    1,
                    lambda _directory, _size, _seed: Fixture(source="0;\n"),
                )
                args: argparse.Namespace = argparse.Namespace(
                    route="source",
                    pairs=4,
                    warmups=0,
                    profile="off",
                    same_artifact=False,
                )
                context: dict[str, object] = {
                    "command": [
                        str(image),
                        "--no-config",
                        "--no-telemetry",
                        "run",
                        "workload.msync",
                    ],
                    "cwd": str(directory / "failure-probe/standard/17"),
                    "exit_code": 7,
                    "stdout": "partial output\n",
                    "stderr": "selected failure\n",
                    "elapsed_ms": 3.0,
                }
                failure: corpus.InvocationFailure = corpus.InvocationFailure(
                    "selected failure", context
                )
                effects: list[float | corpus.InvocationFailure] = [
                    float(i + 1) for i in range(completed)
                ] + [failure]
                with (
                    patch.object(
                        corpus,
                        "invoke",
                        return_value=corpus.Execution(0.0, b"msync test\n"),
                    ),
                    patch.object(corpus, "checked", side_effect=effects),
                    self.assertRaises(corpus.InvocationFailure),
                ):
                    corpus.run(args, [variant], (case,), ["standard"], [17], directory)
                receipt: dict[str, Any] = cast(
                    dict[str, Any], json.loads((directory / "result.json").read_text())
                )
                route: dict[str, Any] = receipt["cases"][0]["routes"]["source"]
                self.assertEqual(receipt["status"], "failed")
                self.assertEqual(route["status"], "failed")
                self.assertEqual(
                    route["timings"]["baseline"]["samples_ms"],
                    [float(i + 1) for i in range(completed)],
                )
                self.assertEqual(route["active"]["pair"], completed)
                self.assertEqual(route["active"]["variant"], "baseline")
                self.assertEqual(route["active"]["phase"], "timing")
                self.assertEqual(route["invocation_failure"], context)
                if completed == 0:
                    self.assertNotIn("p50_ms", route["timings"]["baseline"])
                else:
                    self.assertEqual(route["timings"]["baseline"]["p50_ms"], 1.5)

    def test_profile_failure_preserves_timings_and_prior_profile(self) -> None:
        variants: list[corpus.Variant] = [
            corpus.Variant("baseline", ROOT / "before"),
            corpus.Variant("candidate", ROOT / "after"),
        ]
        result: dict[str, object] = {}
        args: argparse.Namespace = argparse.Namespace(
            pairs=1, warmups=0, profile="profile"
        )
        prior: dict[str, object] = {
            "database": "baseline.sqlite",
            "metrics": {"checked": 1},
        }
        with (
            patch.object(corpus, "checked", return_value=1.0),
            patch.object(
                corpus,
                "profile",
                side_effect=[prior, RuntimeError("profile read failed")],
            ),
            self.assertRaisesRegex(RuntimeError, "profile read failed"),
        ):
            corpus.measure(
                variants,
                ROOT,
                Fixture("0;"),
                b"",
                {"baseline": "before.mexe", "candidate": "after.mexe"},
                args,
                0,
                result,
            )
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["profiles"], {"baseline": prior})
        active: dict[str, object] = cast(dict[str, object], result["active"])
        self.assertEqual(active["phase"], "profile")
        self.assertEqual(active["variant"], "candidate")
        timings: dict[str, Any] = cast(dict[str, Any], result["timings"])
        self.assertEqual(timings["baseline"]["samples_ms"], [1.0])
        self.assertEqual(timings["candidate"]["samples_ms"], [1.0])


if __name__ == "__main__":
    unittest.main()
