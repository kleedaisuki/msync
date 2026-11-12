"""Check exact callee mod/ref against actual vector reads and SQLite telemetry."""

from __future__ import annotations

import argparse
import json
import re
import sqlite3
import subprocess
import tempfile
from contextlib import closing
from dataclasses import dataclass
from pathlib import Path

HERE: Path = Path(__file__).resolve().parent
ROOT: Path = HERE.parents[1]

SETTER: str = """
let storage = $std::storage;
fn helper(target, value: Number) {
    storage::vector_set(target, INDEX, value);
    storage::vector_size(target)
};
"""

CROSS_CALL: str = """
let values = storage::vector_new([10, 20]);
let receiver = RECEIVER;
let before = storage::vector_get(values, 0);
let marker = helper(receiver, 99);
let after = storage::vector_get(values, 0);
$std::io::println((before + marker + after) as String);
"""

READ_ONLY_JOIN: str = """
let storage = $std::storage;
fn helper(target) {
    $std::io::read_line();
    storage::vector_size(target)
};
let values = storage::vector_new([10, 20]);
let line? = $std::io::read_line();
let index = 0;
let before = if line? {
    storage::vector_get(values, index)
} else {
    index = 1;
    storage::vector_get(values, index)
};
let marker = helper(values);
let after = storage::vector_get(values, index);
$std::io::println((before + marker + after) as String);
"""

LAZY_CALLBACK: str = """
let storage = $std::storage;
fn helper(items) { $std::ranges::front(items) };
let values = storage::vector_new([10, 20]);
let before = storage::vector_get(values, 0);
let deferred = $std::ranges::transform([1], fn(item: Number) {
    storage::vector_set(values, 0, 99);
    item
});
let marker = helper(deferred);
let after = storage::vector_get(values, 0);
$std::io::println((before + marker + after) as String);
"""

PARAMETER_ALIAS: str = (
    SETTER.replace("INDEX", "0")
    .replace("storage::vector_set", "$std::storage::vector_set")
    .replace("storage::vector_size", "$std::storage::vector_size")
    + """
fn search(reader, writer) {
    let before = $std::storage::vector_get(reader, 0);
    let marker = helper(writer, 99);
    let after = $std::storage::vector_get(reader, 0);
    before + marker + after
};
let values = storage::vector_new([10, 20]);
$std::io::println(search(values, values) as String);
"""
)

CAPTURED_WRITE: str = """
let storage = $std::storage;
let values = storage::vector_new([10, 20]);
let stable = values;
fn helper(value: Number) {
    storage::vector_set(values, 1, value);
    storage::vector_size(values)
};
let before = storage::vector_get(stable, 0);
let marker = helper(99);
let after = storage::vector_get(stable, 0);
$std::io::println((before + marker + after) as String);
"""

FIRST_FAILURE: str = SETTER.replace("INDEX", "1") + CROSS_CALL.replace(
    "RECEIVER", "values"
).replace("get(values, 0)", "get(values, 9)")


@dataclass(frozen=True)
class Case:
    name: str
    source: str
    inputs: tuple[tuple[str, str], ...]
    old_reads: int
    new_reads: int
    native: str = "storage.vector_get"


@dataclass(frozen=True)
class Build:
    source: Path
    portable: Path
    executable: Path


def invoke(
    binary: Path,
    cwd: Path,
    level: int,
    *args: str,
    stdin: str = "",
    db: Path | None = None,
) -> subprocess.CompletedProcess[str]:
    recorder: list[str] = (
        ["--no-telemetry"]
        if db is None
        else ["--telemetry-db", str(db), "--telemetry-level", "profile"]
    )
    return subprocess.run(
        [str(binary), "--no-config", f"-O{level}", *recorder, *args],
        cwd=cwd,
        input=stdin,
        text=True,
        capture_output=True,
        check=False,
        timeout=60,
    )


def cli(
    binary: Path,
    cwd: Path,
    level: int,
    *args: str,
    stdin: str = "",
    db: Path | None = None,
) -> str:
    process: subprocess.CompletedProcess[str] = invoke(
        binary, cwd, level, *args, stdin=stdin, db=db
    )
    assert process.returncode == 0, (
        process.args,
        process.returncode,
        process.stdout,
        process.stderr,
    )
    return process.stdout


def build(binary: Path, directory: Path, name: str, source: str, level: int) -> Build:
    script: Path = directory / f"{name}.msync"
    portable: Path = directory / f"{name}-O{level}.mso"
    executable: Path = portable.with_suffix(".mexe")
    script.write_text(source, encoding="utf-8")
    cli(binary, directory, level, "compile", str(script), "-o", str(portable))
    cli(binary, directory, level, "bind-ir", str(portable), "-o", str(executable))
    return Build(script, portable, executable)


def disassemble(
    binary: Path,
    directory: Path,
    level: int,
    path: Path,
    helper: str,
    expected_gets: int,
    native: str = "storage.vector_get",
) -> None:
    ir: str = cli(binary, directory, level, "disassemble", str(path))
    targets: list[str] = re.findall(rf'^fn (@f\d+) "{helper}"\(', ir, re.MULTILINE)
    assert any(
        re.search(
            rf"^\s+\d+: Call arguments \d+, specialized {target}\b", ir, re.MULTILINE
        )
        for target in targets
    ), (path, f"missing retained source call to {helper}")
    gets: int = ir.count(f'NativeCall "{native}"')
    assert gets == expected_gets, (
        path,
        f"expected {expected_gets} {native}, got {gets}",
    )


def verify(
    binary: Path, directory: Path, case: Case, level: int, baseline: bool
) -> None:
    built: Build = build(binary, directory, case.name, case.source, level)
    expected_gets: int = case.old_reads if baseline or level == 0 else case.new_reads
    for artifact in (built.portable, built.executable):
        disassemble(
            binary, directory, level, artifact, "helper", expected_gets, case.native
        )
    for route in (built.source, built.portable, built.executable):
        for stdin, expected in case.inputs:
            assert (
                cli(binary, directory, level, "run", str(route), stdin=stdin)
                == f"{expected}\n"
            ), (
                case.name,
                level,
                route,
            )
        if route != built.executable:
            route.unlink()


def measured_calls(database: Path, record: str, label_key: str, label: str) -> int:
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as sql:
        assert sql.execute(
            "SELECT status, exporter_error FROM sessions"
        ).fetchall() == [("complete", None)]
        rows: list[tuple[str]] = sql.execute(
            """SELECT m.value
               FROM records r
               JOIN attributes a ON a.record_id = r.id
               JOIN metrics m ON m.record_id = r.id
               WHERE r.name = ? AND a.key = ? AND a.value = ? AND m.key = 'calls'""",
            (record, label_key, label),
        ).fetchall()
    return sum(int(row[0]) for row in rows)


def verify_first_failure(binary: Path, directory: Path) -> None:
    built: Build = build(binary, directory, "first-failure", FIRST_FAILURE, 2)
    for route in (built.source, built.portable, built.executable):
        database: Path = directory / f"first-failure-{route.suffix[1:]}.sqlite"
        run: subprocess.CompletedProcess[str] = invoke(
            binary, directory, 2, "run", str(route), db=database
        )
        assert run.returncode != 0 and run.stdout == "", (route, run)
        assert "storage.vector_get index 9 is outside" in run.stderr, (route, run)
        assert (
            measured_calls(database, "vm.native", "native.name", "storage.vector_get")
            == 1
        )
        assert (
            measured_calls(database, "vm.native", "native.name", "storage.vector_set")
            == 0
        )
        assert measured_calls(database, "vm.function", "function.name", "helper") == 0
        if route != built.executable:
            route.unlink()


def profile_loop(binary: Path, directory: Path, baseline: bool) -> dict[str, int]:
    level: int = 2
    iterations: int = 131_072
    loop: Build = build(
        binary,
        directory,
        "modref-loop",
        (HERE / "modref-loop.msync").read_text(),
        level,
    )
    for artifact in (loop.portable, loop.executable):
        disassemble(
            binary, directory, level, artifact, "touch_other", 2 if baseline else 1
        )
    loop.source.unlink()
    loop.portable.unlink()
    database: Path = directory / "modref-loop.sqlite"
    assert (
        cli(binary, directory, level, "run", str(loop.executable), db=database)
        == "2883584\n"
    )
    counts: dict[str, int] = {
        native: measured_calls(
            database, "vm.native", "native.name", f"storage.vector_{native}"
        )
        for native in ("get", "set", "size")
    }
    counts["helper"] = measured_calls(
        database, "vm.function", "function.name", "touch_other"
    )
    expected: dict[str, int] = {
        "get": iterations * (2 if baseline else 1),
        "set": iterations,
        "size": iterations,
        "helper": iterations,
    }
    assert counts == expected, (counts, expected)
    return counts


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument(
        "--baseline",
        action="store_true",
        help="expect both reads in a compiler without call mod/ref",
    )
    parser.add_argument(
        "--workspace",
        type=Path,
        help="retain generated artifacts and SQLite receipt in a new directory",
    )
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve(strict=True)
    workspace: Path | None = args.workspace
    baseline: bool = args.baseline
    cases: tuple[Case, ...] = (
        Case("read-only-join", READ_ONLY_JOIN, (("", "22"), ("right\n", "42")), 3, 2),
        Case(
            "disjoint-index",
            SETTER.replace("INDEX", "1") + CROSS_CALL.replace("RECEIVER", "values"),
            (("", "22"),),
            2,
            1,
        ),
        Case(
            "fresh-disjoint-vector",
            SETTER.replace("INDEX", "0")
            + CROSS_CALL.replace("RECEIVER", "storage::vector_new([40])"),
            (("", "21"),),
            2,
            1,
        ),
        Case(
            "overlapping-alias",
            SETTER.replace("INDEX", "0") + CROSS_CALL.replace("RECEIVER", "values"),
            (("", "111"),),
            2,
            2,
        ),
        Case("parameter-alias", PARAMETER_ALIAS, (("", "111"),), 2, 2),
        Case("captured-write", CAPTURED_WRITE, (("", "22"),), 2, 1),
        Case(
            "length-after-set",
            SETTER.replace("INDEX", "0")
            + CROSS_CALL.replace("RECEIVER", "values").replace(
                "get(values, 0)", "size(values)"
            ),
            (("", "6"),),
            3,
            2,
            "storage.vector_size",
        ),
        Case("lazy-callback", LAZY_CALLBACK, (("", "110"),), 2, 2),
    )

    def run(directory: Path) -> None:
        for level in (0, 2, 3):
            for case in cases:
                verify(binary, directory, case, level, baseline)
        verify_first_failure(binary, directory)
        counts: dict[str, int] = profile_loop(binary, directory, baseline)
        (directory / "modref-loop-counts.json").write_text(
            json.dumps(counts, indent=2) + "\n"
        )
        print(f"PASS indexed mod/ref CLI journeys and SQLite calls {counts}")

    if workspace is not None:
        output: Path = workspace.resolve()
        output.mkdir(parents=True)
        run(output)
    else:
        scratch: Path = ROOT / ".temp"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(
            prefix="indexed-modref-", dir=scratch
        ) as temporary:
            run(Path(temporary))


if __name__ == "__main__":
    main()
