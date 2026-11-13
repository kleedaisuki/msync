"""Check orchestration contracts; real CLI smoke journeys validate the payloads."""

from __future__ import annotations

import contextlib
import io
import json
import os
import sys
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
from typing import Any, cast
from unittest.mock import patch

import run
from Corpus.catalog import CORE_CASES, DIAGNOSTIC_CASES
from journeys import Image


class EntrypointTests(unittest.TestCase):
    directory: Path
    temporary: tempfile.TemporaryDirectory[str]
    binary: Path

    def setUp(self) -> None:
        (run.ROOT / ".temp").mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(
            prefix="benchmark-entry-tests-", dir=run.ROOT / ".temp"
        )
        self.directory = Path(self.temporary.name)
        self.binary = self.directory / "msync"
        self.binary.write_text("fixture executable identity", encoding="utf-8")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def config(self, *arguments: str) -> run.Configuration:
        return run.configuration(
            run.parser().parse_args(
                [
                    "--binary",
                    str(self.binary),
                    "--work",
                    str(self.directory / "work"),
                    *arguments,
                ]
            )
        )

    def image(self, name: str = "current") -> Image:
        return Image(name, str(self.binary), "msync 0.8.6", "fixture-hash", 1, {})

    def test_list_requires_no_image(self) -> None:
        output: io.StringIO = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(run.main(["--list"]), 0)
        self.assertIn("set-dedup", output.getvalue())
        self.assertIn("Optional diagnostic workloads", output.getvalue())
        self.assertTrue(set(CORE_CASES).isdisjoint(DIAGNOSTIC_CASES))

    def test_rejects_outside_or_nonempty_workspace(self) -> None:
        for path in (run.ROOT, run.ROOT / ".temp", self.directory):
            values: Any = run.parser().parse_args(
                ["--binary", str(self.binary), "--work", str(path)]
            )
            with self.assertRaises(ValueError):
                run.configuration(values)

    def test_single_image_has_no_fake_baseline(self) -> None:
        self.assertEqual(
            run.native_arguments([self.image()]), ("--binary", str(self.binary))
        )
        self.assertEqual(
            run.native_arguments([self.image("baseline"), self.image()]),
            (
                "--binary",
                str(self.binary),
                "--baseline",
                str(self.binary),
            ),
        )

    def test_default_corpus_defers_to_shared_selection(self) -> None:
        config: run.Configuration = self.config("--route", "all", "--seed", "503")
        selected: run.Command = run.commands("applications", config, [self.image()])[0]
        self.assertNotIn("--case", selected.arguments)
        self.assertNotIn("--candidate", selected.arguments)
        self.assertIn("all", selected.arguments)
        self.assertIn("503", selected.arguments)

    def test_explicit_workload_and_comparison(self) -> None:
        config: run.Configuration = self.config("--workload", "ordered-calibration")
        selected: run.Command = run.commands(
            "applications", config, [self.image("baseline"), self.image()]
        )[0]
        self.assertIn("--candidate", selected.arguments)
        self.assertIn("ordered-calibration", selected.arguments)
        self.assertNotIn("polygon-queries", selected.arguments)
        interactive: run.Command = run.commands("interactive", config, [self.image()])[
            0
        ]
        self.assertIn("lsp-edit", interactive.arguments)
        self.assertIn("lsp-delta", interactive.arguments)

    def test_editor_observations_are_fresh_process_journeys(self) -> None:
        config: run.Configuration = self.config("--warmups", "3")
        self.assertIn("editor", config.groups)
        editor: run.Command = run.commands("editor", config, [self.image()])[0]
        self.assertIn("realistic", editor.arguments)
        self.assertIn("flat400", editor.arguments)
        self.assertNotIn("flat4000", editor.arguments)
        self.assertNotIn("--warmups", editor.arguments)

    def test_manifest_covers_generators_and_protocol_helpers(self) -> None:
        manifest: dict[str, str] = run.harness_manifest()
        self.assertIn("tests/Performance/Corpus/catalog.py", manifest)
        self.assertIn("tests/Performance/Corpus/text_paths.py", manifest)
        self.assertIn("tests/Performance/editor_latency.py", manifest)
        self.assertIn("tests/Msync.EndToEnd/lsp_response_journey.py", manifest)
        self.assertTrue(all(len(value) == 64 for value in manifest.values()))

    def test_explicit_tier_policy_reaches_children_without_ambient_leaks(self) -> None:
        for policy in ("auto", "off", "linked", "force"):
            with self.subTest(policy=policy):
                config: run.Configuration = replace(
                    self.config("--group", "cli", "--jit", policy),
                    work=self.directory / policy,
                )
                receipt: Path = self.directory / f"{policy}.json"
                code: str = (
                    "import json, os; from pathlib import Path; "
                    f"Path({str(receipt)!r}).write_text(json.dumps("
                    "{'status':'complete','policy':os.environ.get('MSYNC_JIT')}))"
                )
                selected: run.Command = run.Command(
                    "tier-environment", (sys.executable, "-c", code), receipt
                )
                with (
                    patch.dict(os.environ, {"MSYNC_JIT": "ambient-invalid"}),
                    patch.object(run, "freeze", return_value=self.image()),
                    patch.object(run, "commands", return_value=(selected,)),
                    contextlib.redirect_stdout(io.StringIO()),
                ):
                    self.assertEqual(run.run(config), 0)
                child: dict[str, Any] = json.loads(receipt.read_text())
                self.assertEqual(child["policy"], None if policy == "auto" else policy)
                report: dict[str, Any] = json.loads(
                    (config.work / "result.json").read_text()
                )
                self.assertEqual(report["configuration"]["jit"], policy)

    def test_child_failure_preserves_diagnostics(self) -> None:
        selected: run.Command = run.Command(
            "failure",
            (
                sys.executable,
                "-c",
                "import sys; print('selected output'); sys.stderr.write('actual failure\\n'); sys.exit(17)",
            ),
            self.directory / "absent.json",
        )
        row: dict[str, Any] = run.run_command(selected, self.directory / "logs")
        self.assertEqual(row["status"], "failed")
        self.assertEqual(row["exit_code"], 17)
        self.assertIn("selected output", row["stdout"])
        self.assertIn("actual failure", row["stderr"])
        self.assertEqual(row["command"], selected.arguments)
        self.assertEqual(row["cwd"], str(run.ROOT))

    def test_missing_command_preserves_attempt(self) -> None:
        selected: run.Command = run.Command(
            "missing",
            (str(self.directory / "missing"),),
            self.directory / "absent.json",
        )
        row: dict[str, Any] = run.run_command(selected, self.directory / "logs")
        self.assertEqual(row["status"], "failed")
        self.assertIsNone(row["exit_code"])
        self.assertIn("error", row)

    def test_failed_group_retains_completed_group(self) -> None:
        config: run.Configuration = self.config("--group", "cli", "--group", "io")
        good: Path = self.directory / "good.json"
        good.write_text('{"status":"complete"}', encoding="utf-8")

        def planned(
            group: str, config: run.Configuration, images: list[Image]
        ) -> tuple[run.Command, ...]:
            code: str = "pass" if group == "cli" else "import sys; sys.exit(9)"
            return (run.Command(group, (sys.executable, "-c", code), good),)

        with (
            patch.object(run, "freeze", return_value=self.image()),
            patch.object(run, "commands", side_effect=planned),
            contextlib.redirect_stdout(io.StringIO()),
            contextlib.redirect_stderr(io.StringIO()),
        ):
            self.assertEqual(run.run(config), 1)
        report: dict[str, Any] = cast(
            dict[str, Any], json.loads((config.work / "result.json").read_text())
        )
        self.assertEqual(report["status"], "failed")
        self.assertEqual(
            [row["status"] for row in report["groups"]], ["complete", "failed"]
        )
        self.assertEqual(report["groups"][1]["commands"][0]["exit_code"], 9)
        with self.assertRaises(ValueError):
            self.config()

    def test_unsupported_prompt_is_visible_partial_coverage(self) -> None:
        config: run.Configuration = replace(self.config(), groups=("session",))
        with (
            patch.object(run, "freeze", return_value=self.image()),
            patch.object(sys, "platform", "win32"),
            contextlib.redirect_stdout(io.StringIO()),
        ):
            self.assertEqual(run.run(config), 0)
        report: dict[str, Any] = cast(
            dict[str, Any], json.loads((config.work / "result.json").read_text())
        )
        self.assertEqual(report["coverage"], "partial")
        self.assertEqual(report["groups"][0]["status"], "unsupported")


if __name__ == "__main__":
    unittest.main()
