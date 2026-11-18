"""Exercise native call placement with Scope aliases and an unresolved argument."""
from __future__ import annotations

import argparse
import os
import platform
import select
import sqlite3
import subprocess
import time
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]
SET_FIXTURE: Path = ROOT / "tests/Performance/NativeJit/method-set-live.msync"
PENDING: str = """
fn relay(value?) { value };
let first? = $std::io::read_line();
let returned? = relay(first);
$std::io::println("submitted");
$std::io::println(if returned is null { "eof" } else { returned });
"""


def bind(cli: list[str], work: Path, name: str, payload: bytes, level: str) -> str:
    source: Path = work / f"{name}.msync"
    source.write_bytes(payload)
    obj: str = f"{name}.mso"
    executable: str = f"{name}.mexe"
    for action in (
        ["compile", level, source.name, "-o", obj],
        ["bind-ir", obj, "-o", executable],
    ):
        subprocess.run([*cli, "--no-telemetry", *action], cwd=work, check=True, timeout=40)
    source.unlink()
    (work / obj).unlink()
    return executable


def counts(database: Path, function: str) -> dict[str, int]:
    with sqlite3.connect(database) as connection:
        assert connection.execute("SELECT status, exporter_error FROM sessions").fetchall() == [("complete", None)]
        return dict(connection.execute("""
            SELECT m.key, sum(m.value_int)
            FROM metrics m JOIN attributes a USING(record_id)
            WHERE a.key = 'function.name' AND a.value GLOB ?
              AND m.key LIKE 'call_physical_%'
            GROUP BY m.key
        """, (function,)))


def command(cli: list[str], database: Path, artifact: str) -> list[str]:
    return [*cli, "--telemetry-db", str(database), "--telemetry-level", "profile", "run", artifact]


def materializations(database: Path) -> dict[str, int]:
    with sqlite3.connect(database) as connection:
        return dict(connection.execute("""
            SELECT key,sum(value_int) FROM metrics
            WHERE key IN ('jit_integer64_constants','jit_guest_null_constants','jit_guest_function_composes')
            GROUP BY key
        """))


def expect_line(stream: int, expected: bytes) -> None:
    observed: bytes = b""
    deadline: float = time.monotonic() + 10
    while len(observed) < len(expected):
        ready, _, _ = select.select([stream], [], [], max(0, deadline - time.monotonic()))
        assert ready, f"blocked before producing {expected!r}; observed {observed!r}"
        chunk: bytes = os.read(stream, len(expected) - len(observed))
        assert chunk, f"child closed stdout after {observed!r}"
        observed += chunk
    assert observed == expected, observed


def journey(binary: Path, work: Path) -> None:
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    set_artifact: str = bind(cli, work, "set-live", SET_FIXTURE.read_bytes(), "-O2")
    pending_artifact: str = bind(cli, work, "pending", PENDING.encode(), "-O0")
    for tier in ("off", "auto"):
        environment: dict[str, str] = {**os.environ, "MSYNC_JIT": tier}
        database: Path = work / f"set-{tier}.sqlite"
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            command(cli, database, set_artifact), cwd=work, env=environment,
            input=b"2048\n4096\n", check=True, capture_output=True, timeout=40,
        )
        assert result.stdout == b"2048\n4096\n" and not result.stderr, result
        ready: dict[str, int] = counts(database, "_set_child")
        assert ready.get("call_physical_arguments", 0) > 0, (tier, ready)
        assert ready.get("call_physical_omitted_arguments", 0) > 0, (tier, ready)
        values: dict[str, int] = materializations(database)
        if tier == "auto" and platform.machine().lower() in {"x86_64", "amd64"}:
            assert all(values.get(key, 0) > 0 for key in (
                "jit_integer64_constants", "jit_guest_null_constants", "jit_guest_function_composes",
            )), values
        if tier == "off":
            assert not any(values.values()), values
        database = work / f"pending-{tier}.sqlite"
        with subprocess.Popen(
            command(cli, database, pending_artifact), cwd=work, env=environment,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        ) as child:
            assert child.stdout is not None
            try:
                expect_line(child.stdout.fileno(), b"submitted\n")
                stdout, stderr = child.communicate(b"ready\n", timeout=10)
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait()
        assert child.returncode == 0 and stdout == b"ready\n" and not stderr, (stdout, stderr)
        pending: dict[str, int] = counts(database, "relay")
        assert pending.get("call_physical_pending_fallbacks") == 1, (tier, pending)
        print(f"{tier}: {ready['call_physical_arguments']} physical child arguments, "
              f"{ready['call_physical_omitted_arguments']} omitted; "
              f"{values.get('jit_guest_function_composes', 0)} native composes; pending relay deferred")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    options: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="native-physical-call-") as temporary:
        journey(options.binary.resolve(), Path(temporary))


if __name__ == "__main__":
    main()
