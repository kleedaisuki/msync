"""Check that a bound CALL takes writable arguments, including through GC and pending fallback."""
from __future__ import annotations

import argparse
import os
import sqlite3
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory

from native_physical_call_journey import bind, expect_line

ROOT: Path = Path(__file__).resolve().parents[2]
CALLS: int = 4096
OBJECTS: int = 6000
SOURCE: str = """
fn revise(n: Number, node) {
    n = n+1;
    if n == 3 {
        let j=0;
        loop {
            if j>=6000 { break; };
            let p=scope { value:j, next?:null };
            let q=scope { value:j+1, next?:null };
            p::next=q;
            q::next=p;
            node::value=node::value+p::next::value;
            j=j+1;
        };
    };
    node::value=node::value+n;
    node::value
};
INPUT
let i=0;
let score=0;
loop {
    if i>=4096 { break; };
    score=score+revise(i+2, scope { value:i });
    i=i+1;
};
$std::io::println(score as String);
OUTPUT
"""
EXPECTED: bytes = f"{CALLS * (CALLS - 1) + 3 * CALLS + OBJECTS * (OBJECTS + 1) // 2}\n".encode()


def check(database: Path, *, adopt: bool, pending: bool, staged: int = 0) -> None:
    with sqlite3.connect(database) as connection:
        assert connection.execute("SELECT status,exporter_error FROM sessions").fetchall() == [("complete", None)]
        counts: dict[str, int] = dict(connection.execute("""
            SELECT m.key,sum(m.value_int)
            FROM records r JOIN metrics m ON m.record_id=r.id JOIN attributes a ON a.record_id=r.id
            WHERE r.name='vm.function' AND a.key='function.name' AND a.value='revise'
              AND (m.key LIKE 'call_window%' OR m.key='call_arguments_copied')
            GROUP BY m.key
        """))
        assert counts["call_windows_adopted"] == (CALLS if adopt else 0), counts
        assert counts["call_windows_zero_stage"] == (CALLS if adopt and not staged else 0), counts
        assert counts["call_window_arguments"] == (2 * CALLS if adopt else 0), counts
        assert counts["call_window_staged_arguments"] == staged * CALLS, counts
        assert counts["call_window_pending_misses"] == (CALLS if pending else 0), counts
        assert counts["call_arguments_copied"] == (staged * CALLS if adopt else 2 * CALLS), counts
        if adopt:
            collections: int = connection.execute("""
                SELECT count(*)
                FROM records r JOIN metrics m ON m.record_id=r.id
                JOIN attributes a ON a.record_id=r.id AND a.key='function.name'
                WHERE r.name='heap.collect' AND a.value='revise'
                  AND m.key='heap.native_stack_frames' AND m.value_int>=2
            """).fetchone()[0]
            assert collections > 0, "GC never ran inside the adopted frame"


def run_ready(
    cli: list[str], artifact: str, database: Path, work: Path, environment: dict[str, str], expected: bytes
) -> None:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [*cli, "--telemetry-db", str(database), "--telemetry-level", "diagnostics", "run", artifact],
        cwd=work, env=environment, capture_output=True, check=True, timeout=30,
    )
    assert result.stdout == expected and not result.stderr, result


def exercise(binary: Path, work: Path) -> None:
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    ready_source: bytes = SOURCE.replace("INPUT", "").replace("OUTPUT", "").encode()
    pending_source: bytes = SOURCE.replace("INPUT", "let line?=$std::io::read_line();").replace(
        "OUTPUT", '$std::io::println(if line is null {"eof"} else {line});'
    ).encode()
    ready: str = bind(cli, work, "ready", ready_source, "-O2")
    pending: str = bind(cli, work, "pending", pending_source, "-O2")
    live_source: bytes = ready_source.replace(b"revise(i+2,", b"revise(i+1,")
    live: str = bind(cli, work, "live", live_source, "-O2")
    live_expected: bytes = f"{int(EXPECTED) - CALLS}\n".encode()
    captured_source: bytes = b"let bias=scope { value:0 };\n" + live_source.replace(
        b"node::value=node::value+n;", b"node::value=node::value+n+bias::value; bias::value=bias::value+1;"
    ) + b"$std::io::println(bias::value as String);\n"
    captured: str = bind(cli, work, "captured", captured_source, "-O2")
    captured_expected: bytes = f"{int(live_expected) + CALLS * (CALLS - 1) // 2}\n{CALLS}\n".encode()
    for tier in ("off", "auto", "force"):
        environment: dict[str, str] = {**os.environ, "MSYNC_JIT": tier}
        database: Path = work / f"ready-{tier}.sqlite"
        run_ready(cli, ready, database, work, environment, EXPECTED)
        check(database, adopt=True, pending=False)
        database = work / f"live-{tier}.sqlite"
        run_ready(cli, live, database, work, environment, live_expected)
        check(database, adopt=True, pending=False, staged=1)
        database = work / f"captured-{tier}.sqlite"
        run_ready(cli, captured, database, work, environment, captured_expected)
        check(database, adopt=True, pending=False, staged=1)
        database = work / f"pending-{tier}.sqlite"
        with subprocess.Popen(
            [*cli, "--telemetry-db", str(database), "--telemetry-level", "diagnostics", "run", pending],
            cwd=work, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        ) as child:
            assert child.stdout is not None
            try:
                expect_line(child.stdout.fileno(), EXPECTED)
                stdout, stderr = child.communicate(b"ready\n", timeout=10)
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait()
        assert child.returncode == 0 and stdout == b"ready\n" and not stderr, (stdout, stderr)
        check(database, adopt=False, pending=True)
        print(f"{tier}: {CALLS} writable windows through GC in each ready case; live input staged with and without capture; pending input not demanded")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    options: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="native-owned-window-") as temporary:
        exercise(options.binary.resolve(), Path(temporary))


if __name__ == "__main__":
    main()
