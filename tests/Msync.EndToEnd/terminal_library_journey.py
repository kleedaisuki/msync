#!/usr/bin/env python3
"""Use typed terminal tools through real source, object and executable journeys."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import subprocess
import sys
import tempfile
from pathlib import Path

from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]
INPUT: str = "beta\nalpha\nalpha\n世界🙂\n"
PROGRAM: str = r"""
let shell = $std::shell;
shell::mkdir(./work/nested);
shell::cat(./input.txt) -> ./cat.txt;
$std::string::join(shell::head(./input.txt, 2), "|") -> ./head.txt;
$std::string::join(shell::tail(./input.txt, 2), "|") -> ./tail.txt;
$std::string::join(shell::uniq(shell::sort(./input.txt)), "|") -> ./unique.txt;
$std::string::join(shell::uniq(["a", "a", "b", "a"]), "|")
    -> ./adjacent.txt;
$std::string::join(shell::grep("^a", ./input.txt), "|") -> ./grep.txt;
$std::string::join(shell::grep_fixed("世", ./input.txt), "|")
    -> ./grep-fixed.txt;
$std::string::join(shell::grep_not("^a", ./input.txt), "|")
    -> ./grep-not.txt;
$std::string::join(shell::sed("a", "A", ["cat", "alpha"]), "|")
    -> ./sed.txt;
$std::string::join(shell::cut(["one two", "three four"], 1), "|")
    -> ./cut.txt;
let numbered = shell::nl(["alpha", "beta"]);
$std::string::join($std::ranges::map(numbered, fn(row) {
    $std::string::concat(row::index as String, row::value)
}), "|") -> ./numbered.txt;
let counts = shell::wc(./input.txt);
(counts::lines as String) -> ./count-lines.txt;
(counts::words as String) -> ./count-words.txt;
(counts::characters as String) -> ./count-characters.txt;
(counts::bytes as String) -> ./count-bytes.txt;
(shell::wc_lines(./input.txt) as String) -> ./logical-lines.txt;
(shell::wc("no final newline")::lines as String) -> ./no-newline-count.txt;
(shell::wc_lines("no final newline") as String) -> ./no-newline-records.txt;
let empty_tail = shell::tail(./not-present.txt, 0);
($std::ranges::empty(empty_tail) as String) -> ./empty-tail.txt;
$std::string::join(shell::tail(["retained"], 2147483648), "|")
    -> ./large-count.txt;
shell::tee("tee-value", ./tee-written.txt) -> ./tee-returned.txt;
$std::string::join(shell::tee_lines(["a", "b"], ./tee-lines.txt), "|")
    -> ./tee-lines-returned.txt;
shell::cp("./input.txt", ./work/copied.txt);
shell::mv(./work/copied.txt, ./work/moved.txt);
shell::touch(./work/moved.txt);
shell::cat(./work/moved.txt) -> ./touched-copy.txt;
shell::touch(./new-empty.txt);
($std::io::file_size(./new-empty.txt) as String) -> ./new-empty-size.txt;
shell::touch(./retained.txt);
shell::cat(./retained.txt) -> ./retained-copy.txt;
shell::touch(./work);
shell::ln(./input.txt, ./work/hard-link.txt);
shell::cat(./work/hard-link.txt) -> ./hard-link-copy.txt;
(shell::stat(./work/moved.txt)::kind) -> ./kind.txt;
shell::basename(./work/moved.txt) as String -> ./basename.txt;
shell::dirname(./work/moved.txt) as String -> ./dirname.txt;
($std::fs::is_absolute(shell::realpath(./work/moved.txt)) as String)
    -> ./absolute.txt;
($std::ranges::size(shell::ls_at(./work)) as String) -> ./list-count.txt;
($std::ranges::size(shell::find(./work)) as String) -> ./find-count.txt;
($std::ranges::size(shell::find_if(./work, fn(path: Path) {
    $std::fs::is_regular_file(path)
})) as String) -> ./find-files.txt;
shell::setenv("MSYNC_TERMINAL_JOURNEY", "Klee");
let variable? = shell::getenv("MSYNC_TERMINAL_JOURNEY");
if variable is not null { variable -> ./environment.txt; };
($std::ranges::any(shell::env(), fn(row) {
    row::name == "MSYNC_TERMINAL_JOURNEY" and row::value == "Klee"
}) as String) -> ./environment-list.txt;
shell::unsetenv("MSYNC_TERMINAL_JOURNEY");
((shell::getenv("MSYNC_TERMINAL_JOURNEY") is null) as String)
    -> ./environment-unset.txt;
((shell::which("MSYNC_NO_SUCH_EXECUTABLE_894793") is null) as String)
    -> ./which-missing.txt;
($std::ranges::any(shell::ps(), fn(row) {
    row::pid > 0 and $std::string::length(row::name) > 0
}) as String) -> ./process-list.txt;
let origin = shell::pwd();
shell::mkdir(./anchor-dir);
let gated = shell::spawn("@PYTHON@" as Path,
    ["-c", "import sys; sys.stdin.read(); sys.stdout.write('deferred-target.txt')"]);
let cast_gated = shell::spawn("@PYTHON@" as Path,
    ["-c", "import sys; sys.stdin.read(); sys.stdout.write('cast-target.txt')"]);
($std::process::running(gated) as String) -> ./live-process.txt;
"admitted-before-cd" -> gated::stdout;
let delayed_path = cast_gated::stdout as Path;
"cast-before-cd" -> delayed_path;
shell::cd(./anchor-dir);
$std::process::close_stdin(gated);
$std::process::close_stdin(cast_gated);
acquire gated;
acquire cast_gated;
shell::cat(origin/deferred-target.txt) -> origin/deferred-path-copy.txt;
shell::cat(origin/cast-target.txt) -> origin/cast-path-copy.txt;
shell::cd(origin);
let process = shell::run("@PYTHON@" as Path,
    ["-c", "import sys; sys.stdout.write('Klee process')"]);
$std::process::stdout(process) -> ./process-output.txt;
($std::process::success(process) as String) -> ./process-success.txt;
shell::rm(./work/moved.txt);
shell::rmdir(./work/nested);
shell::rm_tree(./work);
((not exists ./work) as String) -> ./removed-tree.txt;
shell::echo("Klee") -> ./echo.txt;
"""

OUTPUTS: dict[str, bytes] = {
    "cat.txt": INPUT.encode(),
    "head.txt": b"beta|alpha",
    "tail.txt": "alpha|世界🙂".encode(),
    "unique.txt": "alpha|beta|世界🙂".encode(),
    "adjacent.txt": b"a|b|a",
    "grep.txt": b"alpha|alpha",
    "grep-fixed.txt": "世界🙂".encode(),
    "grep-not.txt": "beta|世界🙂".encode(),
    "sed.txt": b"cAt|AlphA",
    "cut.txt": b"two|four",
    "numbered.txt": b"1alpha|2beta",
    "count-lines.txt": b"4",
    "count-words.txt": b"4",
    "count-characters.txt": str(len(INPUT)).encode(),
    "count-bytes.txt": str(len(INPUT.encode())).encode(),
    "logical-lines.txt": b"4",
    "no-newline-count.txt": b"0",
    "no-newline-records.txt": b"1",
    "empty-tail.txt": b"true",
    "large-count.txt": b"retained",
    "tee-written.txt": b"tee-value",
    "tee-returned.txt": b"tee-value",
    "tee-lines.txt": b"a\nb",
    "tee-lines-returned.txt": b"a|b",
    "touched-copy.txt": INPUT.encode(),
    "new-empty-size.txt": b"0",
    "retained-copy.txt": b"retained payload",
    "hard-link-copy.txt": INPUT.encode(),
    "kind.txt": b"regular",
    "basename.txt": b"moved.txt",
    "absolute.txt": b"true",
    "list-count.txt": b"3",
    "find-count.txt": b"3",
    "find-files.txt": b"2",
    "environment.txt": b"Klee",
    "environment-list.txt": b"true",
    "environment-unset.txt": b"true",
    "which-missing.txt": b"true",
    "process-list.txt": b"true",
    "live-process.txt": b"true",
    "deferred-target.txt": b"admitted-before-cd",
    "deferred-path-copy.txt": b"admitted-before-cd",
    "cast-target.txt": b"cast-before-cd",
    "cast-path-copy.txt": b"cast-before-cd",
    "process-output.txt": b"Klee process",
    "process-success.txt": b"true",
    "removed-tree.txt": b"true",
    "echo.txt": b"Klee",
}


def execute(binary: Path, parent: Path, levels: list[int]) -> int:
    executions: int = 0
    source_text: str = PROGRAM.replace("@PYTHON@", sys.executable.replace('"', '""'))
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            (directory / "input.txt").write_text(INPUT, encoding="utf-8", newline="")
            retained: Path = directory / "retained.txt"
            retained.write_bytes(b"retained payload")
            os.utime(retained, (946684800, 946684800))
            source: Path = directory / "terminal 体验.msync"
            source.write_text(source_text, encoding="utf-8")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"),
                "terminal check",
            )
            assert not (directory / "work").exists(), "Checking executed mkdir"
            assert retained.stat().st_mtime == 946684800, "Checking executed touch"
            if stage == "source":
                result: subprocess.CompletedProcess[str] = invoke(
                    binary, directory, "run", str(source), f"-O{level}"
                )
            else:
                artifact: Path = source.with_suffix(".mso")
                accept(
                    invoke(
                        binary,
                        directory,
                        "compile",
                        str(source),
                        "-o",
                        str(artifact),
                        f"-O{level}",
                    ),
                    "terminal compile",
                )
                if stage == "executable":
                    bound: Path = source.with_suffix(".mexe")
                    accept(
                        invoke(
                            binary,
                            directory,
                            "bind-ir",
                            str(artifact),
                            "-o",
                            str(bound),
                            "--source-file",
                            str(source),
                        ),
                        "terminal bind",
                    )
                    artifact = bound
                assert not (directory / "work").exists(), (
                    "Compilation/binding performed IO"
                )
                source.unlink()
                launch: Path = (
                    directory
                    if stage == "relocatable"
                    else directory / "other launch directory"
                )
                launch.mkdir(exist_ok=True)
                result = invoke(binary, launch, "run-ir", str(artifact))
            accept(result, f"terminal O{level} {stage}")
            for name, expected in OUTPUTS.items():
                assert (directory / name).read_bytes() == expected, (
                    level,
                    stage,
                    name,
                    (directory / name).read_bytes(),
                    expected,
                )
            assert Path((directory / "dirname.txt").read_text()) == Path("work")
            assert (
                retained.read_bytes() == b"retained payload"
                and retained.stat().st_mtime > 946684800
            )
            assert not any((directory / "anchor-dir").iterdir()), (
                "A prior deferred admission moved with cd"
            )
            executions += 1
    return executions


def edge_cases(binary: Path, parent: Path) -> dict[str, object]:
    directory: Path = parent / "edge-cases"
    directory.mkdir()
    source: Path = directory / "paths.msync"
    source.write_text(
        """$std::shell::basename(./work/) as String -> ./basename.txt;
$std::shell::dirname("name.txt") as String -> ./dirname.txt;
$std::shell::dirname(./work/) as String -> ./trailing-parent.txt;
let unique = $std::shell::uniq(["a", "a", "b", "a"]);
$std::string::join(unique, "|") -> ./first.txt;
$std::string::join(unique, "|") -> ./second.txt;
""",
        encoding="utf-8",
    )
    accept(
        invoke(binary, directory, "run", str(source)),
        "terminal path and iterator edges",
    )
    assert (directory / "basename.txt").read_text() == "work"
    assert Path((directory / "dirname.txt").read_text()) == Path(".")
    assert Path((directory / "trailing-parent.txt").read_text()) == Path(".")
    assert (directory / "first.txt").read_bytes() == b"a|b|a"
    assert (directory / "second.txt").read_bytes() == b"a|b|a"
    (directory / "bom.txt").write_bytes(b"\xef\xbb\xbfA\n")
    source.write_text(
        "$std::shell::wc(./bom.txt)::bytes as String -> ./bom-count.txt;",
        encoding="utf-8",
    )
    accept(invoke(binary, directory, "run", str(source)), "raw file byte count")
    assert (directory / "bom-count.txt").read_bytes() == b"5"
    executable: str = sys.executable.replace('"', '""')
    source.write_text(
        f'''let child = $std::shell::spawn("{executable}" as Path,
    ["-c", "import sys; sys.stdin.read(); sys.stdout.write('7')"]);
let converted = (child::stdout as Number) as String;
$std::process::close_stdin(child);
converted -> ./chained-cast.txt;
acquire child;
''',
        encoding="utf-8",
    )
    accept(invoke(binary, directory, "run", str(source)), "deferred scalar cast chain")
    assert (directory / "chained-cast.txt").read_bytes() == b"7"
    for name, text, reason in (
        (
            "negative-tail",
            '$std::ranges::size($std::shell::tail(["a"], -1));',
            "shell.tail_lines",
        ),
        (
            "fractional-tail",
            '$std::ranges::size($std::shell::tail(["a"], .5));',
            "integral count",
        ),
        ("rmdir-file", "$std::shell::rmdir(./retained.txt);", "requires a directory"),
    ):
        source.write_text(text, encoding="utf-8")
        (directory / "retained.txt").write_bytes(b"do not remove")
        result: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "run", str(source)
        )
        assert result.returncode != 0 and reason in result.stderr, (
            name,
            result.stdout,
            result.stderr,
        )
        assert (directory / "retained.txt").read_bytes() == b"do not remove"
    permission_cases: int = 0
    if os.name != "nt" and os.geteuid() != 0:
        execute_only: Path = directory / "execute-only"
        no_search: Path = directory / "no-search"
        execute_only.mkdir()
        no_search.mkdir()
        try:
            execute_only.chmod(0o100)
            no_search.chmod(0o400)
            source.write_text("$std::shell::cd(./execute-only);", encoding="utf-8")
            accept(
                invoke(binary, directory, "run", str(source)),
                "cd accepts execute-only directory",
            )
            source.write_text("$std::shell::cd(./no-search);", encoding="utf-8")
            denied: subprocess.CompletedProcess[str] = invoke(
                binary, directory, "run", str(source)
            )
            assert denied.returncode != 0 and "Cannot cd" in denied.stderr, (
                denied.stderr
            )
            permission_cases = 2
        finally:
            execute_only.chmod(0o700)
            no_search.chmod(0o700)
    return {
        "lexical_path_outputs": 3,
        "repeated_unique_iterator_outputs": 2,
        "deferred_scalar_cast_chain": True,
        "file_byte_count_includes_bom": True,
        "negative_diagnostics": 3,
        "posix_search_permission_cases": permission_cases,
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--level", type=int, choices=(0, 2, 3), action="append")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    scratch: Path = ROOT / ".temp/terminal-library-journey"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=scratch) as temporary:
        report: dict[str, object] = {
            "binary": str(binary),
            "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "managed_components": {
                path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(binary.parent.glob("*.dll"))
                if path.name.lower()
                in {
                    "msync.dll",
                    "msync.core.dll",
                    "msync.compiler.dll",
                    "msync.runtime.dll",
                }
            },
            "platform": platform.platform(),
            "levels": arguments.level or [2],
            "staged_program_executions": execute(
                binary, Path(temporary), arguments.level or [2]
            ),
            "exact_outputs_per_execution": len(OUTPUTS),
            "touch_preserves_bytes_and_updates_timestamp": True,
            "source_deleted_binary_runs": True,
            "checks_compile_bind_perform_no_io": True,
            "edge_cases": edge_cases(binary, Path(temporary)),
        }
    encoded: str = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(encoded, encoding="utf-8")
    if sys.stdout.encoding and sys.stdout.encoding.lower().replace("-", "") != "utf8":
        sys.stdout.reconfigure(encoding="utf-8")
    print(encoded, end="")


if __name__ == "__main__":
    main()
