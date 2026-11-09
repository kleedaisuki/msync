#!/usr/bin/env python3
"""Use the composed standard library through real CLI stages and editor requests."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import tempfile
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any, cast

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client
from navigation_journey import position

ROOT = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class Example:
    name: str
    source: str
    outputs: tuple[tuple[str, bytes], ...]
    inputs: tuple[tuple[str, bytes], ...] = ()


EXAMPLES: tuple[Example, ...] = (
    Example(
        "lines-and-functions",
        """
let normalize = $std::functional::compose(
    $std::string::to_uppercase, $std::string::trim
);
let lines = $std::io::read_lines(./input.txt);
let normalized = $std::ranges::transform(lines, normalize);
let selected = $std::ranges::filter(
    normalized, fn(line: String) { not $std::string::empty(line) }
);
$std::io::write_text(./result.txt, $std::string::join(selected, "|"));
""",
        (("result.txt", "HELLO|世界🙂|TRAILING".encode()),),
    ),
    Example(
        "typed-cast-and-unicode",
        """
let number = $std::cast("27", Number);
$std::cast(number + 1, String) -> ./number.txt;
let text = "A🙂é";
$std::cast($std::string::length(text), String) -> ./length.txt;
$std::string::char_at(text, 1) -> ./character.txt;
$std::string::substring(text, 1, 2) -> ./slice.txt;
$std::string::join($std::string::chars(text), "|") -> ./characters.txt;
let file = $std::cast("./input data/世界🙂.txt", Path);
$std::cast($std::path::filename(file), String) -> ./filename.txt;
let endpoint = $std::cast("ssh://stdlib-test.invalid", Connection);
$std::cast(endpoint, String) -> ./connection.txt;
""",
        (
            ("number.txt", b"28"),
            ("length.txt", b"4"),
            ("character.txt", "🙂".encode()),
            ("slice.txt", "🙂e".encode()),
            ("characters.txt", "A|🙂|e|́".encode()),
            ("filename.txt", "世界🙂.txt".encode()),
            ("connection.txt", b"ssh://stdlib-test.invalid"),
        ),
    ),
    Example(
        "structured-range-elements",
        """
let pairs = $std::ranges::zip(["a", "b"], ["1", "2"]);
let labels = $std::ranges::transform(pairs, fn(pair) {
    $std::string::concat(pair::first, pair::second)
});
$std::string::join(labels, ",") -> ./pairs.txt;
let numbered = $std::ranges::enumerate(["α", "β"]);
let numbered_labels = $std::ranges::transform(numbered, fn(row) {
    $std::string::concat($std::cast(row::index, String), row::value)
});
$std::string::join(numbered_labels, "|") -> ./numbered.txt;
""",
        (("pairs.txt", b"a1,b2"), ("numbered.txt", "0α|1β".encode())),
    ),
    Example(
        "lazy-state-and-fold",
        """
let calls = 0;
let values = $std::ranges::transform([1, 2, 3], fn(value: Number) {
    calls = calls + 1;
    value
});
let empty = $std::ranges::take(values, 0);
$std::cast($std::ranges::size(empty), String) -> ./size.txt;
$std::cast(calls, String) -> ./before.txt;
let total = $std::ranges::fold(
    $std::ranges::take(values, 2), 0,
    fn(accumulator: Number, value: Number) { accumulator + value }
);
$std::cast(total, String) -> ./total.txt;
$std::cast(calls, String) -> ./after.txt;
""",
        (
            ("size.txt", b"0"),
            ("before.txt", b"0"),
            ("total.txt", b"3"),
            ("after.txt", b"2"),
        ),
    ),
    Example(
        "terminal-callback-effects",
        """
let calls = 0;
let values = $std::ranges::transform(["a", "b"], fn(value: String) {
    calls = calls + 1;
    value
});
$std::string::join(values, ",");
$std::cast(calls, String) -> ./join-calls.txt;
let byte_calls = 0;
let bytes = $std::ranges::transform([0, 255], fn(value: Number) {
    byte_calls = byte_calls + 1;
    value
});
$std::io::write_bytes(./payload.bin, bytes);
$std::cast(byte_calls, String) -> ./byte-calls.txt;
""",
        (
            ("join-calls.txt", b"2"),
            ("payload.bin", b"\x00\xff"),
            ("byte-calls.txt", b"2"),
        ),
    ),
    Example(
        "numeric-file-tools",
        """
let path = $std::cast("./input data/世界🙂.txt", Path);
let bytes = $std::io::file_size(path);
let blocks = $std::math::ceil($std::math::divide(bytes, 3));
$std::cast(blocks, String) -> ./blocks.txt;
$std::cast($std::math::cube(2), String) -> ./cube.txt;
$std::cast($std::math::is_finite($std::math::sqrt(9)), String) -> ./finite.txt;
""",
        (("blocks.txt", b"3"), ("cube.txt", b"8"), ("finite.txt", b"true")),
    ),
    *(
        Example(
            "denoted-type-number" if number else "denoted-type-string",
            """
let target = if exists ./flag { Number } else { String };
if target is Number {
    let value = $std::cast("42", target);
    $std::cast(value + 1, String) -> ./denoted-type.txt
} else if target is String {
    let value = $std::cast("42", target);
    $std::string::concat(value, "!") -> ./denoted-type.txt
};
""",
            (("denoted-type.txt", b"43" if number else b"42!"),),
            (("flag", b"selected"),) if number else (),
        )
        for number in (False, True)
    ),
)


def invoke(
    binary: Path, directory: Path, *arguments: str, no_color: bool = True
) -> subprocess.CompletedProcess[str]:
    environment: dict[str, str] = dict(os.environ)
    if no_color:
        environment["NO_COLOR"] = "1"
    else:
        environment.pop("NO_COLOR", None)
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )


def accept(result: subprocess.CompletedProcess[str], context: str) -> None:
    assert result.returncode == 0, (
        context,
        result.returncode,
        result.stdout,
        result.stderr,
    )


def fixtures(directory: Path) -> None:
    (directory / "input.txt").write_bytes(
        "  hello \r\n\r\n  世界🙂 \n trailing  \r\n".encode()
    )
    data = directory / "input data"
    data.mkdir()
    (data / "世界🙂.txt").write_bytes(b"FILE\x00\xff\r\n")


def execute(
    binary: Path,
    parent: Path,
    levels: Sequence[int],
    names: Sequence[str] | None = None,
) -> dict[str, int]:
    executions = 0
    selected = [
        example for example in EXAMPLES if names is None or example.name in names
    ]
    for example in selected:
        for level, stage in (
            (level, stage)
            for level in levels
            for stage in ("source", "relocatable", "executable")
        ):
            directory = parent / example.name / f"O{level}" / stage
            directory.mkdir(parents=True)
            fixtures(directory)
            for name, contents in example.inputs:
                (directory / name).write_bytes(contents)
            source = directory / "standard library 体验.msync"
            source.write_bytes(example.source.encode())
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"),
                example.name + " check",
            )
            assert all(
                not (directory / name).exists() for name, _ in example.outputs
            ), example.name
            if stage == "source":
                result = invoke(binary, directory, "run", str(source), f"-O{level}")
            else:
                artifact = source.with_suffix(".mso")
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
                    example.name + " compile",
                )
                if stage == "executable":
                    bound = source.with_suffix(".mexe")
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
                        example.name + " bind",
                    )
                    artifact = bound
                source.unlink()
                launch = (
                    directory / "different launch directory"
                    if stage == "executable"
                    else directory
                )
                launch.mkdir(exist_ok=True)
                result = invoke(binary, launch, "run-ir", str(artifact))
            accept(result, example.name + " " + stage)
            for name, expected in example.outputs:
                assert (directory / name).read_bytes() == expected, (
                    example.name,
                    stage,
                    name,
                )
            executions += 1
    return {
        "composed_user_programs": len(selected),
        "source_free_stage_executions": executions,
    }


def editor(binary: Path, parent: Path) -> dict[str, int]:
    client = Client(binary, cwd=parent)
    uri = (parent / "library navigation 体验.msync").as_uri()
    source = """let file = $std::cast("./input", Path);
let rows = $std::ranges::zip([file], [1]);
for row in rows { let selected = row::first; };
let text = $std::string::join(["a", "b"], "|");
"""
    try:
        client.request("initialize", {"capabilities": {}})
        notify(client, uri, 1, source, opening=True)
        assert diagnostics(client, uri, 1)["diagnostics"] == [], source
        checks: tuple[tuple[str, str], ...] = (
            ("file =", "Path"),
            ("selected =", "Path"),
            ("text =", "String"),
        )
        for name, expected in checks:
            result, _ = client.request(
                "textDocument/hover",
                {
                    "textDocument": {"uri": uri},
                    "position": position(source, source.index(name)),
                },
            )
            assert expected in result["contents"]["value"], (name, result)
        for spelling, module in (
            ("cast(", "_core"),
            ("zip(", "ranges"),
            ("join(", "string"),
        ):
            target, _ = client.request(
                "textDocument/definition",
                {
                    "textDocument": {"uri": uri},
                    "position": position(source, source.index(spelling)),
                },
            )
            assert target is not None, spelling
            target_uri = str(target["uri"])
            assert target_uri.startswith("msync-stdlib:") and target_uri.endswith(
                f"/0.8.0/{module}.msync"
            ), target
            text, _ = client.request("msync/stdlibSource", {"uri": target_uri})
            assert spelling.split("(")[0] in str(text), (target, text)
        completion, _ = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": position(source, source.index("first;") + 2),
            },
        )
        labels = {str(item["label"]) for item in cast(list[dict[str, Any]], completion)}
        assert {"first", "second"} <= labels, labels
        return {
            "inferred_library_hovers": len(checks),
            "embedded_library_definitions": 3,
            "structured_range_completion": 1,
        }
    finally:
        client.close()


def skills(binary: Path, parent: Path) -> dict[str, int]:
    client = Client(binary, cwd=parent)
    modules = ("io", "functional", "math", "ranges", "string", "path", "process")
    try:
        client.request("initialize", {"capabilities": {}})
        overview = invoke(binary, parent, "skill", "library")
        accept(overview, "library overview")
        assert "Range" in overview.stdout and "Array" in overview.stdout, (
            overview.stdout
        )
        assert "_core" not in overview.stdout, overview.stdout
        for topic, module in (
            ("cast", "_core"),
            *((module, module) for module in modules),
        ):
            result = invoke(binary, parent, "skill", "library", topic)
            accept(result, "library " + topic)
            embedded, _ = client.request(
                "msync/stdlibSource",
                {"uri": f"msync-stdlib:///0.8.0/{module}.msync"},
            )
            actual = str(embedded).strip()
            snippets = re.findall(
                r"^```msync\n(.*?)^```", result.stdout, re.MULTILINE | re.DOTALL
            )
            assert actual in [snippet.strip() for snippet in snippets], (
                topic,
                result.stdout,
            )
        for topic_path in ((), ("library",), ("library", "cast")):
            plain = invoke(binary, parent, "skill", *topic_path)
            colored = invoke(
                binary,
                parent,
                "--color",
                "always",
                "skill",
                *topic_path,
                no_color=False,
            )
            accept(plain, "plain skill")
            accept(colored, "colored skill")
            assert re.search(r"\x1b\[[0-9;]*m", colored.stdout), (
                topic_path,
                colored.stdout,
            )
            assert re.sub(r"\x1b\[[0-9;]*m", "", colored.stdout) == plain.stdout, (
                topic_path
            )
        return {"source_backed_skill_pages": len(modules) + 1, "skill_color_modes": 6}
    finally:
        client.close()


def target_contract(binary: Path, parent: Path) -> dict[str, int]:
    source = parent / "changed deferred cast target.msync"
    source.write_bytes(
        b"let target = decltype([0]);\n"
        b'let converted = $std::cast(["42"], target);\n'
        b' target = decltype(["changed"]);\n'
        b"for item in converted { $std::cast(item, String) -> ./unexpected; };\n"
    )
    result = invoke(binary, parent, "check", str(source))
    assert result.returncode != 0 and "E0213" in result.stderr, result
    assert "$std::cast" in result.stderr and "target" in result.stderr, result.stderr
    assert not (parent / "unexpected").exists()
    return {"deferred_target_contract_diagnostics": 1}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--case",
        choices=("all", "programs", "editor", "skills", "contracts"),
        default="all",
    )
    parser.add_argument(
        "--levels", type=int, nargs="+", choices=(0, 1, 2, 3), default=(0, 2, 3)
    )
    parser.add_argument(
        "--program",
        action="append",
        choices=tuple(example.name for example in EXAMPLES),
    )
    arguments = parser.parse_args()
    parent = ROOT / ".temp/stdlib-experience"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        directory = Path(temporary)
        report: dict[str, int] = {}
        if arguments.case in ("all", "programs"):
            report.update(
                execute(
                    arguments.binary.resolve(),
                    directory,
                    arguments.levels,
                    arguments.program,
                )
            )
        if arguments.case in ("all", "editor"):
            report.update(editor(arguments.binary.resolve(), directory))
        if arguments.case in ("all", "skills"):
            report.update(skills(arguments.binary.resolve(), directory))
        if arguments.case in ("all", "contracts"):
            report.update(target_contract(arguments.binary.resolve(), directory))
    print(json.dumps(report, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
