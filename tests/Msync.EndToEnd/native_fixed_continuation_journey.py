"""Check physical guest arguments and returns across a cold input, GC, and a driver handoff."""
from __future__ import annotations

import argparse
import os
import platform
import sqlite3
import subprocess
from contextlib import closing
from pathlib import Path
from tempfile import TemporaryDirectory

from native_physical_call_journey import bind, expect_line

ROOT: Path = Path(__file__).resolve().parents[2]
COLD_READ: bytes = b"""        let line?=$std::io::read_line();
        $std::io::println("cold");
        if line is null {0} else {line as Number}"""
BODY: bytes = b"""
let calculate=fn(n: Number) {
    if n==4096 {
COLD_BODY
    } else { n+1 }
};
fn wrap(n: Number) {
    let result=calculate(n);
    result+1
};
let i=0;
let total=0;
loop {
    if i>4096 {break;};
    if i==4096 {$std::io::println(total as String);};
    total=total+wrap(i);
    i=i+1;
};
$std::io::println(total as String);
"""
SOURCE: bytes = b"let label = \"stale\";\n" + BODY.replace(
    b"COLD_BODY", COLD_READ.replace(b'println("cold")', b"println(label)")
).replace(b"if i==4096 {", b'if i==4096 { label = "cold"; ')
POLL_SOURCE: bytes = SOURCE.replace(b"let line?=", b"""
        let j=0;
        let sum=0;
        loop {
            if j>=1024 {break;};
            j=j+1;
            sum=sum+j;
        };
        let line?=""").replace(b"else {line as Number}", b"else {(line as Number)+sum}")
def with_shared_counter(source: bytes) -> bytes:
    return source.replace(b"fn(n: Number) {", b"fn(n: Number, node) {\n    node::value=node::value+1;").replace(
        b"fn wrap(n: Number)", b"fn wrap(n: Number, node)"
    ).replace(b"calculate(n)", b"calculate(n,node)").replace(
        b"let i=0;", b"let shared=scope {value:0};\nlet i=0;"
    ).replace(b"wrap(i)", b"wrap(i,shared)") + b"$std::io::println(shared::value as String);\n"


REFERENCE_SOURCE: bytes = with_shared_counter(SOURCE)
HANDOFF_SOURCE: bytes = b"""
let read_once=fn(seed: Number) {
    let line?=$std::io::read_line();
    if line is null {seed} else {line as Number}
};
let announce=fn(n: Number) { $std::io::println("cold"); n };
""" + with_shared_counter(BODY.replace(b"COLD_BODY", b"""
        let root=scope {value:17, next?:null};
        let seed=scope {value:19, next?:root};
        root::next=seed;
        let j=0;
        let sum=0;
        loop {
            if j>=6000 {break;};
            let left=scope {value:j, next?:null};
            let right=scope {value:j+1, next?:left};
            left::next=right;
            sum=sum+left::next::value;
            j=j+1;
        };
        let reply=read_once(announce(n));
        reply+1+sum+root::next::next::value+node::value - 4097
"""))
BOOL_SOURCE: bytes = b"""
let threshold = 2048;
let accept=fn(n: Number) {
    if n==4096 {
        let line?=$std::io::read_line();
        $std::io::println("cold bool");
        if line is null {false} else {line=="yes"}
    } else { n<threshold }
};
fn wrap(n: Number) {
    let value=accept(n);
    if value {2} else {1}
};
let i=0;
let total=0;
loop {
    if i>4096 {break;};
    if i==4096 {threshold=0; $std::io::println(total as String);};
    total=total+wrap(i);
    i=i+1;
};
$std::io::println(total as String);
"""
RAW_BOOL_SOURCE: bytes = BOOL_SOURCE.replace(b"let threshold = 2048;\n", b"").replace(
    b"n<threshold", b"n<2048"
).replace(b"threshold=0; ", b"")
DEPTH: int = 1200
REPEATED_COLD_SOURCE: bytes = b"""
let calculate=fn(n: Number, node) {
    node::value=node::value+1;
    if n==4120 {
        $std::io::println("cold");
        let line?=$std::io::read_line();
        if line is null {0} else {line as Number}
    } else {n+1}
};
fn run_loop(node) {
    let i=0;
    let total=0;
    loop {
        if i>4120 {break;};
        let result=calculate(i,node);
        total=total+result+1;
        i=i+1;
    };
    total
};
let shared=scope {value:0};
$std::io::println(run_loop(shared) as String);
$std::io::println(shared::value as String);
"""
SEGMENT_SOURCE: bytes = b"""
fn climb(n: Number, root) {
    if n == 0 { root::value }
    else {
        let kept=scope { value:n, next?:root };
        let result=climb(n - 1, root);
        kept::value + kept::next::value + result
    }
};
let root=scope {value:7};
let first=climb(DEPTH, root);
root::value=11;
let second=climb(DEPTH, root);
$std::io::println(first as String);
$std::io::println(second as String);
""".replace(b"DEPTH", str(DEPTH).encode())


def exercise(binary: Path, work: Path, *, handoff: bool = False, boolean: bool = False,
             poll: bool = False, raw_bool: bool = False, reference: bool = False) -> None:
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    name: str = "ready-bool" if boolean else "cold-handoff" if handoff else "cold-poll" if poll else "cold-demand"
    source: bytes = BOOL_SOURCE if boolean else HANDOFF_SOURCE if handoff else POLL_SOURCE if poll else SOURCE
    if raw_bool:
        name, source = "raw-bool", RAW_BOOL_SOURCE
    if reference:
        name, source = "raw-reference", REFERENCE_SOURCE
    program: str = bind(cli, work, name, source, "-O2")
    total: int = sum(n + 2 for n in range(4096))
    warm: bytes = f"{total}\ncold\n".encode()
    expected: bytes = f"{total + 74 + (1 + sum(range(1, 6001)) + 17 if handoff else 0)}\n".encode()
    if handoff or reference:
        expected += b"4097\n"
    if poll:
        expected = f"{int(expected) + sum(range(1, 1025))}\n".encode()
    reply: bytes = b"73\n"
    if boolean:
        warm, expected, reply = b"6144\ncold bool\n", b"6146\n", b"yes\n"
    if not boolean and not poll:
        run_gated(cli, work, program, ["--no-telemetry"], "force", warm, expected, reply)
    for tier in (("force",) if poll else ("off", "auto", "force")):
        database: Path = work / f"{name}-{tier}.sqlite"
        run_gated(cli, work, program, ["--telemetry-db", str(database), "--telemetry-level", "diagnostics"],
                  tier, warm, expected, reply)
        with closing(sqlite3.connect(database)) as connection:
            assert connection.execute("SELECT status,exporter_error FROM sessions").fetchall() == [("complete", None)]
            counts: dict[str, int] = dict(connection.execute("""
                SELECT key,sum(value_int) FROM metrics
                WHERE key IN ('jit_frame_load_calls_continued','jit_frame_callable_copies_elided',
                    'jit_fixed_guest_single_frame_returns','jit_fixed_guest_driver_handoffs',
                    'jit_fixed_native_entries','jit_fixed_native_calls','jit_fixed_native_frame_resumes',
                    'jit_fixed_native_scalar_returns','jit_fixed_native_argument_placements',
                    'jit_fixed_native_arguments_placed','jit_fixed_native_argument_reference_words',
                    'jit_fixed_native_workless_returns','jit_fixed_native_work_bridge_entries',
                    'jit_fixed_native_work_call_entries','jit_fixed_native_work_resume_entries',
                    'jit_fixed_native_raw_returns','jit_fixed_native_raw_managed_exits',
                    'jit_fixed_native_raw_other_exits',
                    'jit_fixed_native_unattached_returns','jit_fixed_native_frame_attachments','jit_fixed_native_reservations',
                    'jit_fixed_native_inline_returns','jit_fixed_native_emitted_frames',
                    'jit_fixed_native_capture_bindings_deferred','jit_fixed_native_capture_bindings_materialized',
                    'jit_fixed_native_capture_bindings_avoided')
                GROUP BY key
            """))
            collections: int = connection.execute("""
                SELECT count(*) FROM records r JOIN metrics m ON r.id=m.record_id
                WHERE r.name='heap.collect' AND m.key='heap.native_stack_frames' AND m.value_int>=3
            """).fetchone()[0]
        hits: int = counts.get("jit_frame_load_calls_continued", 0)
        if tier == "force" and platform.machine().lower() in {"x86_64", "amd64"}:
            assert hits > 4000, counts
            assert counts.get("jit_frame_callable_copies_elided") == hits, counts
            assert counts.get("jit_fixed_guest_single_frame_returns", 0) > 4000, counts
            assert counts.get("jit_fixed_guest_driver_handoffs") == int(handoff), counts
            assert counts.get("jit_fixed_native_calls", 0) > 3900, counts
            assert counts.get("jit_fixed_native_scalar_returns", 0) > 3900, counts
            assert counts.get("jit_fixed_native_workless_returns", 0) > 3900, counts
            assert sum(counts.get(f"jit_fixed_native_work_{bc}_entries", 0) for bc in ("bridge", "call", "resume")) >= 1, counts
            if not handoff and not boolean:
                assert counts.get("jit_fixed_native_capture_bindings_avoided", 0) > 3900, counts
                deferred = counts["jit_fixed_native_capture_bindings_deferred"]
                bound = counts.get("jit_fixed_native_capture_bindings_materialized", 0)
                assert bound > 0 and deferred == bound * counts["jit_fixed_native_entries"], counts
                assert deferred == counts["jit_fixed_native_capture_bindings_avoided"] + bound, counts
                assert counts.get("jit_fixed_native_raw_returns") == counts["jit_fixed_native_scalar_returns"], counts
                assert counts.get("jit_fixed_native_raw_managed_exits") == int(not poll), counts
                assert counts.get("jit_fixed_native_raw_other_exits") == int(poll), counts
                assert counts.get("jit_fixed_native_unattached_returns") == counts["jit_fixed_native_raw_returns"], counts
                assert counts.get("jit_fixed_native_inline_returns") == counts["jit_fixed_native_unattached_returns"], counts
                assert counts.get("jit_fixed_native_frame_attachments") == 1, counts
                assert counts.get("jit_fixed_native_emitted_frames") == counts["jit_fixed_native_reservations"], counts
            if raw_bool:
                assert counts.get("jit_fixed_native_inline_returns") == counts["jit_fixed_native_scalar_returns"], counts
                assert counts.get("jit_fixed_native_frame_attachments") == 1, counts
                assert counts.get("jit_fixed_native_emitted_frames") == counts["jit_fixed_native_reservations"], counts
            elif boolean:
                assert counts.get("jit_fixed_native_capture_bindings_deferred", 0) == 0, counts
                assert counts.get("jit_fixed_native_raw_returns", 0) == 0, counts
            assert counts.get("jit_fixed_native_entries") == counts["jit_fixed_native_calls"], counts
            assert counts.get("jit_fixed_native_reservations", 0) == (
                counts.get("jit_fixed_native_unattached_returns", 0)
                + counts.get("jit_fixed_native_frame_attachments", 0)
            ), counts
            assert counts.get("jit_fixed_native_argument_placements", 0) > 3900, counts
            assert counts.get("jit_fixed_native_arguments_placed", 0) >= counts["jit_fixed_native_argument_placements"], counts
            if handoff or reference:
                assert counts.get("jit_fixed_native_argument_reference_words", 0) > 3900, counts
            if handoff:
                assert collections > 0, "The fixed callee never collected with its caller and frame active"
                assert counts.get("jit_fixed_native_frame_resumes", 0) > 0, counts
        if tier == "off":
            assert hits == 0 and counts.get("jit_fixed_native_emitted_frames", 0) == 0, counts
        print(f"{name}/{tier}: one cold read, {hits} fused continuations, "
              f"{counts.get('jit_fixed_native_calls', 0)} native entries completed, "
              f"{counts.get('jit_fixed_native_arguments_placed', 0)} arguments and "
              f"{counts.get('jit_fixed_native_argument_reference_words', 0)} root words placed by caller, "
              f"{counts.get('jit_fixed_native_scalar_returns', 0)} scalar returns written by caller, "
              f"{counts.get('jit_fixed_native_workless_returns', 0)} returns without entering work, "
              f"{counts.get('jit_fixed_native_raw_returns', 0)} scalar returns without managed tier binding, "
              f"{counts.get('jit_fixed_native_raw_managed_exits', 0)} raw managed exits, "
              f"{counts.get('jit_fixed_native_unattached_returns', 0)} unattached returns, "
              f"{counts.get('jit_fixed_native_inline_returns', 0)} returns without a managed callback, "
              f"{counts.get('jit_fixed_native_frame_attachments', 0)} physical-frame attachments, "
              f"{counts.get('jit_fixed_native_capture_bindings_avoided', 0)} capture bindings avoided, "
              f"{counts.get('jit_fixed_native_capture_bindings_materialized', 0)} materialized, "
              f"{counts.get('jit_fixed_native_frame_resumes', 0)} frame resumes, "
              f"{counts.get('jit_fixed_guest_driver_handoffs', 0)} driver handoffs, {collections} active collections")
    if boolean or poll:
        return
    failed: subprocess.CompletedProcess[bytes] = subprocess.run(
        [*cli, "--no-telemetry", "run", program],
        cwd=work, env={**os.environ, "MSYNC_JIT": "force"},
        input=b"bad\n", capture_output=True, check=False, timeout=10,
    )
    assert failed.returncode != 0 and failed.stdout == warm, failed
    assert b"Cannot cast String 'bad' to Number" in failed.stderr and f"{name}.msync".encode() in failed.stderr, failed
    print(f"{name}/force: cold-path failure retains callee source and does not resume caller")


def run_gated(cli: list[str], work: Path, program: str, observation: list[str],
              tier: str, warm: bytes, expected: bytes, reply: bytes) -> None:
    with subprocess.Popen(
        [*cli, *observation, "run", program], cwd=work, env={**os.environ, "MSYNC_JIT": tier},
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    ) as child:
        assert child.stdout is not None
        try:
            expect_line(child.stdout.fileno(), warm)
            stdout, stderr = child.communicate(reply, timeout=8)
            assert child.returncode == 0 and stdout == expected and not stderr, (stdout, stderr, child.returncode)
        finally:
            if child.poll() is None:
                child.kill()
                child.wait()


def exercise_segments(binary: Path, work: Path) -> None:
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    program: str = bind(cli, work, "segment-cursor", SEGMENT_SOURCE, "-O2")
    expected: bytes = "".join(
        f"{DEPTH * (DEPTH + 1) // 2 + (DEPTH + 1) * value}\n" for value in (7, 11)
    ).encode()
    for tier in ("off", "force"):
        database: Path = work / f"segment-cursor-{tier}.sqlite"
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            [*cli, "--telemetry-db", str(database), "--telemetry-level", "profile", "run", program],
            cwd=work, env={**os.environ, "MSYNC_JIT": tier}, capture_output=True, check=True, timeout=25,
        )
        assert result.stdout == expected and not result.stderr, result
        with closing(sqlite3.connect(database)) as connection:
            assert connection.execute("SELECT status,exporter_error FROM sessions").fetchall() == [("complete", None)]
            counts: dict[str, int] = dict(connection.execute("""
                SELECT key,sum(value_int) FROM metrics
                WHERE key IN ('stack_segment_allocations','stack_segment_reentries')
                GROUP BY key
            """))
        assert counts.get("stack_segment_allocations", 0) > 1 and counts.get("stack_segment_reentries", 0) > 0, counts
        print(f"segment-cursor/{tier}: 2 x {DEPTH} nested calls restored live aliases; "
              f"{counts['stack_segment_reentries']} cached-segment reentries")

def exercise_repeated_cold(binary: Path, work: Path) -> None:
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    name: str = "repeated-cold"
    program: str = bind(cli, work, name, REPEATED_COLD_SOURCE, "-O2")
    expected: bytes = f"{sum(n + 2 for n in range(4120)) + 74}\n4121\n".encode()
    database: Path = work / f"{name}.sqlite"
    observations: tuple[list[str], ...] = (
        ["--telemetry-db", str(database), "--telemetry-level", "profile"],
        ["--no-telemetry"],
    )
    for observation in observations:
        if os.name == "posix":
            run_gated(cli, work, program, observation, "force", b"cold\n", expected, b"73\n")
        else:
            result: subprocess.CompletedProcess[bytes] = subprocess.run(
                [*cli, *observation, "run", program], cwd=work,
                env={**os.environ, "MSYNC_JIT": "force"}, input=b"73\n",
                capture_output=True, check=True, timeout=30,
            )
            assert result.stdout.replace(b"\r\n", b"\n") == b"cold\n" + expected and not result.stderr, result
    with closing(sqlite3.connect(database)) as connection:
        assert connection.execute("SELECT status,exporter_error FROM sessions").fetchall() == [("complete", None)]
        counts: dict[str, int] = dict(connection.execute("""
            SELECT key,sum(value_int) FROM metrics
            WHERE key IN ('jit_fixed_native_repeated_admissions','jit_fixed_native_repeated_attachments',
                          'jit_fixed_native_reservations','jit_fixed_native_emitted_frames',
                          'jit_fixed_native_inline_returns','jit_fixed_native_frame_attachments',
                          'jit_fixed_native_frame_resumes')
            GROUP BY key
        """))
    if platform.machine().lower() in {"x86_64", "amd64"}:
        assert counts["jit_fixed_native_repeated_admissions"] > 3000, counts
        assert counts["jit_fixed_native_repeated_attachments"] == counts["jit_fixed_native_frame_attachments"] == 1, counts
        assert counts["jit_fixed_native_emitted_frames"] == counts["jit_fixed_native_reservations"], counts
        assert counts["jit_fixed_native_inline_returns"] + 1 == counts["jit_fixed_native_emitted_frames"], counts
        assert counts["jit_fixed_native_frame_resumes"] == 1, counts
    print(f"{name}: repeated caller resumes its child without replaying the shared write; {counts}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--repeated-only", action="store_true", help="Run the portable repeated cold call")
    options: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="native-fixed-continuation-") as temporary:
        if options.repeated_only:
            exercise_repeated_cold(options.binary.resolve(), Path(temporary))
            return
        exercise(options.binary.resolve(), Path(temporary))
        exercise(options.binary.resolve(), Path(temporary), handoff=True)
        exercise(options.binary.resolve(), Path(temporary), reference=True)
        exercise(options.binary.resolve(), Path(temporary), boolean=True)
        exercise(options.binary.resolve(), Path(temporary), boolean=True, raw_bool=True)
        exercise(options.binary.resolve(), Path(temporary), poll=True)
        exercise_repeated_cold(options.binary.resolve(), Path(temporary))
        exercise_segments(options.binary.resolve(), Path(temporary))


if __name__ == "__main__":
    main()
