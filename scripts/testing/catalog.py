"""Small explicit adapters, not discovery of historical or diagnostic projects."""

from __future__ import annotations

import sys

from .model import Task


def python(
    name: str,
    script: str,
    *arguments: str,
    cpu: int = 1,
    memory_mib: int = 512,
    timeout: float = 300,
    locks: tuple[str, ...] = (),
    exclusive: bool = False,
    environment: tuple[tuple[str, str], ...] = (),
    receipt: str | None = None,
) -> Task:
    return Task(
        name,
        (sys.executable, "{root}/" + script, *arguments),
        cpu,
        memory_mib,
        timeout,
        locks,
        exclusive,
        environment=environment,
        receipt=receipt,
    )


def catalog() -> dict[str, Task]:
    tasks: tuple[Task, ...] = (
        python(
            "semantic",
            "tests/Msync.EndToEnd/hir_semantic_journey.py",
            "--binary",
            "{binary}",
            "--work",
            "{work}/fixture",
            "--output",
            "{work}/journey.json",
            cpu=2,
            memory_mib=1536,
            receipt="{work}/journey.json",
        ),
        python(
            "range-initialization",
            "tests/Msync.EndToEnd/range_initialization_journey.py",
            "--binary", "{binary}", "--output", "{work}/journey.json",
            cpu=2,
            memory_mib=2048,
            receipt="{work}/journey.json",
        ),
        python(
            "artifact",
            "tests/NativeArtifact/test_journey.py",
            "--binary",
            "{binary}",
            "--work",
            "{work}/fixture",
            memory_mib=1024,
            receipt="{work}/fixture/result.json",
        ),
        python(
            "bound-fusion",
            "tests/NativePreparation/test_fusion_journey.py",
            "--binary", "{binary}", "--output", "{work}/journey.json",
            receipt="{work}/journey.json",
        ),
        python(
            "guest-heap-repl",
            "tests/NativePreparation/test_guest_heap_journey.py",
            "--binary", "{binary}", "--output", "{work}/journey.json",
            receipt="{work}/journey.json",
        ),
        python(
            "machine-guest-call",
            "tests/NativePreparation/test_machine_call_journey.py",
            "--binary", "{binary}", "--output", "{work}/journey.json",
            receipt="{work}/journey.json",
        ),
        python(
            "native-number-range",
            "tests/Msync.EndToEnd/native_number_range_journey.py",
            "--binary", "{binary}", "--count", "4000", "--verify-machine",
            "--output", "{work}/journey.json",
            receipt="{work}/journey.json",
        ),
        python("cli", "tests/Msync.EndToEnd/journeys.py", "--binary", "{binary}"),
        python(
            "lsp",
            "tests/Msync.EndToEnd/lsp_journey.py",
            "--binary",
            "{binary}",
            memory_mib=1024,
        ),
        python(
            "language",
            "tests/Acceptance/language_journeys.py",
            "{binary}",
            cpu=2,
            memory_mib=1536,
            timeout=900,
        ),
        python("configuration", "tests/Configuration/cli_journeys.py", "{binary}"),
        python(
            "telemetry",
            "tests/Telemetry/journeys.py",
            "{binary}",
            locks=("telemetry-fixture",),
            memory_mib=1024,
        ),
        python("package", "packaging/smoke.py", "--binary", "{binary}"),
        python(
            "polish",
            "packaging/polish_journeys.py",
            "--binary",
            "{binary}",
            locks=("polish-fixture",),
            timeout=900,
        ),
        Task(
            "editor",
            ("npm", "--prefix", "{root}/editors/vscode", "test"),
            cpu=2,
            memory_mib=2048,
            locks=("editor-build",),
            timeout=900,
        ),
        python(
            "benchmark",
            "tests/Performance/run.py",
            "--binary",
            "{binary}",
            "--work",
            "{work}/benchmark",
            cpu=12,
            memory_mib=24 * 1024,
            exclusive=True,
            timeout=86400,
            environment=(("MSYNC_BENCHMARK_SCOPE", "1"),),
            receipt="{work}/benchmark/result.json",
        ),
    )
    return {task.name: task for task in tasks}


DEFAULTS: tuple[str, ...] = ("semantic", "artifact", "lsp")
