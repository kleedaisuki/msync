#!/usr/bin/env python3
"""Verify exact Word32 transport with guest JIT disabled."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import subprocess
import sys
import tempfile
from contextlib import closing
from pathlib import Path

from bits_journey import MASK, ROOT

ORDINALS: int = 160

PROGRAM: str = """fn next_number() -> Number {
    let line? = $std::io::read_line();
    if line is null { 0 } else { line as Number }
};
fn traverse(count: Number, rotation: Number, select_word: Bool) -> String {
    let head = scope { key: 0, ordinal: 1, next?: null };
    let tail = head;
    for ordinal in $std::ranges::iota(1, count + 1) {
        let added = scope { key: ordinal * 3, ordinal: ordinal + 1, next?: null };
        tail::next = added;
        tail = added;
    };
    let cursor? = head;
    let sum = 0;
    let word = $std::bits::bit_not(0);
    loop {
        if cursor is null { break; };
        sum = sum + cursor::key;
        word = $std::bits::bit_xor($std::bits::rotate_left(word, rotation), cursor::ordinal);
        cursor = cursor::next;
    };
    let joined = -0;
    if select_word { joined = word; };
    $std::string::join([sum as String, word as String, joined as String,
        $std::math::signbit(joined) as String], ":")
};
let count = next_number();
let rotation = next_number();
$std::io::println(traverse(count, rotation, next_number() != 0));
"""

BAD: str = """let line? = $std::io::read_line();
let input = if line is null { 0 } else { line as Number };
let word = $std::bits::bit_not(input);
$std::io::println($std::bits::rotate_right(1, $std::bits::pop_count(word)) as String);
"""


def invoke(
    cli: Path, cwd: Path, *args: str | Path, data: bytes = b"", db: Path | None = None
) -> subprocess.CompletedProcess[bytes]:
    options: list[str | Path] = ["--no-config"]
    if db is None:
        options.append("--no-telemetry")
    else:
        options.extend(["--telemetry-db", db, "--telemetry-level", "profile"])
    return subprocess.run(
        [cli, *options, *args],
        cwd=cwd,
        input=data,
        capture_output=True,
        check=False,
        timeout=45,
        env={**os.environ, "MSYNC_JIT": "off"},
    )


def checked(result: subprocess.CompletedProcess[bytes]) -> bytes:
    if result.returncode or result.stderr:
        raise RuntimeError(
            (
                result.args,
                result.returncode,
                result.stdout.decode(),
                result.stderr.decode(),
            )
        )
    return result.stdout.replace(b"\r\n", b"\n")


def score(count: int, rotation: int, select_word: bool) -> bytes:
    word: int = MASK
    for handle in range(1, count + 2):
        word = (((word << rotation) | (word >> (32 - rotation))) & MASK) ^ handle
    total: int = 3 * count * (count + 1) // 2
    return f"{total}:{word}:{word if select_word else '-0'}:{str(not select_word).lower()}\n".encode()


def telemetry(db: Path) -> dict[str, int]:
    with closing(sqlite3.connect(db)) as connection:
        counters: dict[str, int] = dict(
            connection.execute(
                "SELECT m.key,SUM(m.value_int) FROM metrics AS m "
                "JOIN records AS r ON r.id=m.record_id "
                "WHERE r.name='execute' AND m.key GLOB 'tier.*' GROUP BY m.key"
            )
        )
        counters["bits_calls"] = connection.execute(
            "SELECT COALESCE(SUM(m.value_int),0) FROM metrics AS m "
            "JOIN records AS r ON r.id=m.record_id "
            "JOIN attributes AS a ON a.record_id=r.id "
            "WHERE r.name='vm.native' AND m.key='calls' "
            "AND a.key='native.name' AND a.value GLOB 'bits.*'"
        ).fetchone()[0]
        return counters


def positive(cli: Path, work: Path, level: int) -> list[dict[str, object]]:
    work.mkdir(parents=True, exist_ok=True)
    source: Path = work / "workload.msync"
    portable: Path = work / "workload.mso"
    bound: Path = work / "workload.mexe"
    source.write_text(PROGRAM, encoding="utf-8", newline="\n")
    observations: list[dict[str, object]] = []
    for route, path in (("source", source), ("mso", portable), ("mexe", bound)):
        if route == "mso":
            checked(invoke(cli, work, "compile", f"-O{level}", source, "-o", portable))
            source.unlink()
        elif route == "mexe":
            checked(invoke(cli, work, "bind-ir", portable, "-o", bound))
            portable.unlink()
        for rotation, select_word in ((0, False), (7, True), (31, False)):
            db: Path = work / f"{route}-{rotation}.sqlite"
            args: list[str | Path] = (
                ["run", f"-O{level}", path] if route == "source" else ["run-ir", path]
            )
            actual: bytes = checked(
                invoke(
                    cli,
                    work,
                    *args,
                    data=f"{ORDINALS}\n{rotation}\n{int(select_word)}\n".encode(),
                    db=db,
                )
            )
            expected: bytes = score(ORDINALS, rotation, select_word)
            assert actual == expected, (route, level, rotation, actual, expected)
            counters: dict[str, int] = telemetry(db)
            assert (
                counters["tier.entries"] == 0 and counters["tier.compilations"] == 0
            ), counters
            assert counters["bits_calls"] > 0, counters
            observations.append(
                {
                    "route": route,
                    "level": level,
                    "rotation": rotation,
                    "output": actual.decode().strip(),
                    "counters": counters,
                }
            )
    return observations


def failure(cli: Path, work: Path) -> list[str]:
    work.mkdir(parents=True, exist_ok=True)
    source: Path = work / "invalid-count.msync"
    portable: Path = work / "invalid-count.mso"
    bound: Path = work / "invalid-count.mexe"
    source.write_text(BAD, encoding="utf-8", newline="\n")
    call: str = "$std::bits::rotate_right(1, $std::bits::pop_count(word))"
    start: int = BAD.index(call)
    end: int = start + len(call)
    line: int = BAD.count("\n", 0, start) + 1
    column: int = start - BAD.rfind("\n", 0, start)
    exit_code: int | None = None
    detached_diagnostic: bytes | None = None
    paths: list[str] = []
    for route, path in (("source", source), ("mso", portable), ("mexe", bound)):
        if route == "mso":
            checked(invoke(cli, work, "compile", "-O0", source, "-o", portable))
            source.unlink()
        elif route == "mexe":
            checked(invoke(cli, work, "bind-ir", portable, "-o", bound))
            portable.unlink()
        arguments: list[str | Path] = (
            ["run", "-O0", path] if route == "source" else ["run-ir", path]
        )
        selected: subprocess.CompletedProcess[bytes] = invoke(
            cli, work, *arguments, data=b"0\n"
        )
        assert selected.returncode != 0 and not selected.stdout, selected
        assert (
            b"Word32 shift count must be an exact integer from 0 through 31"
            in selected.stderr
        ), selected
        assert exit_code is None or exit_code == selected.returncode, selected
        exit_code = selected.returncode
        if route == "source":
            assert f":{line}:{column}".encode() in selected.stderr, selected
            caret: bytes = b"| " + b" " * (column - 1) + b"^" * len(call) + b" "
            assert caret in selected.stderr, selected
        else:
            assert f"(source offset {start}..{end})".encode() in selected.stderr, (
                selected
            )
            assert detached_diagnostic is None or detached_diagnostic == selected.stderr
            detached_diagnostic = selected.stderr
        paths.append(route)
    return paths


def pending(cli: Path, work: Path) -> list[str]:
    work.mkdir(parents=True, exist_ok=True)
    source: Path = work / "pending.msync"
    portable: Path = work / "pending.mso"
    bound: Path = work / "pending.mexe"
    executable: str = json.dumps(sys.executable)
    child: str = json.dumps("import sys; sys.stdin.read(); sys.exit(7)")
    source.write_text(
        f"""
let process = $std::process::spawn({executable}, ["-c", {child}]);
let delayed = $std::bits::bit_not($std::process::exit_code(process));
let selected = $std::bits::bit_and($std::bits::bit_not(0), delayed);
$std::process::write_stdin(process, "release");
$std::process::close_stdin(process);
$std::io::println(selected as String);
""",
        encoding="utf-8",
        newline="\n",
    )
    paths: list[str] = []
    for route, path in (("source", source), ("mso", portable), ("mexe", bound)):
        if route == "mso":
            checked(invoke(cli, work, "compile", "-O0", source, "-o", portable))
            source.unlink()
        elif route == "mexe":
            checked(invoke(cli, work, "bind-ir", portable, "-o", bound))
            portable.unlink()
        arguments: list[str | Path] = (
            ["run", "-O0", path] if route == "source" else ["run-ir", path]
        )
        db: Path = work / f"{route}.sqlite"
        assert checked(invoke(cli, work, *arguments, db=db)) == b"4294967288\n"
        assert telemetry(db)["bits_calls"] > 0
        paths.append(route)
    return paths


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--report", type=Path)
    args: argparse.Namespace = parser.parse_args()
    cli: Path = args.binary.resolve(strict=True)
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    observations: list[dict[str, object]] = []
    with tempfile.TemporaryDirectory(prefix="managed-word-", dir=scratch) as name:
        work: Path = Path(name)
        for level in (0, 2):
            observations.extend(positive(cli, work / f"O{level}", level))
        output: dict[str, object] = {
            "positive": observations,
            "failure_routes": failure(cli, work / "invalid"),
            "pending_routes": pending(cli, work / "pending"),
        }
    if args.report is not None:
        args.report.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(
        f"PASS: {len(observations)} exact word/native-linked-Scope results, three selected failures and three deferred Process routes"
    )


if __name__ == "__main__":
    main()
