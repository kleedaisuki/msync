#!/usr/bin/env python3
"""Exercise terminal guidance and compiler-backed editor insight as a user journey."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]
ANSI = re.compile(r"\x1b\[[0-9;]*m")


def invoke(
    binary: Path,
    cwd: Path,
    arguments: list[str],
    *,
    no_color: bool = False,
    input_source: str | None = None,
) -> subprocess.CompletedProcess[str]:
    environment = dict(os.environ)
    environment.pop("NO_COLOR", None)
    if no_color:
        environment["NO_COLOR"] = "1"
    return subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=cwd,
        env=environment,
        capture_output=True,
        input=input_source,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=30,
    )


def terminal_guidance(binary: Path, cwd: Path) -> dict[str, int]:
    topics = [
        (["--help"], ["check", "fmt", "--color", "-O3", "default: O2"]),
        (["-h"], ["check", "fmt", "--color", "-O3", "default: O2"]),
        (["help", "syntax"], ["fn", "scope", "null", "match"]),
        (["check", "--help"], ["check", "FILE", "-O3"]),
        (["compile", "--help"], ["compile", "FILE", "-O3"]),
    ]
    for arguments, snippets in topics:
        plain = invoke(binary, cwd, ["--color", "never", *arguments])
        colored = invoke(binary, cwd, ["--color", "always", *arguments])
        automatic = invoke(binary, cwd, ["--color", "auto", *arguments])
        disabled = invoke(binary, cwd, ["--color", "always", *arguments], no_color=True)
        assert (
            plain.returncode
            == colored.returncode
            == automatic.returncode
            == disabled.returncode
            == 0
        )
        assert "\x1b" not in plain.stdout + automatic.stdout + disabled.stdout
        assert ANSI.search(colored.stdout), colored.stdout
        assert ANSI.sub("", colored.stdout) == plain.stdout
        for snippet in snippets:
            assert snippet in plain.stdout, (arguments, snippet, plain.stdout)

    return {"help_color_modes": len(topics) * 4}


def terminal(binary: Path, cwd: Path) -> dict[str, int]:
    guidance = terminal_guidance(binary, cwd)
    cases = {
        "nullable": ("let value = null;\n", ["Null", "?", "help:"]),
        "annotation": ("let value: Bool = ./;\n", ["Bool", "Path", "help:"]),
        "delimiter": ("fn copy(value) {\n    value\n", ["}", "help:"]),
        "generic": (
            "fn get(some) {\n    some::x\n};\nget(./);\n",
            ["Path", "Scope", "Connection", "call", "get", "help:"],
        ),
    }
    for name, (source, fragments) in cases.items():
        path = cwd / f"{name}.msync"
        path.write_text(source, encoding="utf-8")
        plain = invoke(binary, cwd, ["--color", "never", "check", str(path)])
        colored = invoke(binary, cwd, ["--color", "always", "check", str(path)])
        assert plain.returncode != 0 and colored.returncode == plain.returncode
        assert "\x1b" not in plain.stderr and ANSI.search(colored.stderr)
        assert ANSI.sub("", colored.stderr) == plain.stderr
        assert (
            "error[" in plain.stderr and "-->" in plain.stderr and "^" in plain.stderr
        ), plain.stderr
        assert path.name in plain.stderr and " | " in plain.stderr, plain.stderr
        for fragment in fragments:
            assert fragment in plain.stderr, (name, fragment, plain.stderr)
        if name == "generic":
            assert "some::x" in plain.stderr and "get(./)" in plain.stderr, plain.stderr
    return {**guidance, "source_diagnostics": len(cases)}


def optimization_reference(binary: Path, cwd: Path) -> dict[str, int]:
    plain = invoke(binary, cwd, ["--color", "never", "skill", "workflow", "artifacts"])
    assert plain.returncode == 0 and "\x1b" not in plain.stdout, plain
    for level in range(4):
        assert f"`-O{level}`" in plain.stdout, plain.stdout
    assert "| `-O2` | Default:" in plain.stdout and "`.mexe`" in plain.stdout
    colored = invoke(
        binary, cwd, ["--color", "always", "skill", "workflow", "artifacts"]
    )
    disabled = invoke(
        binary,
        cwd,
        ["--color", "always", "skill", "workflow", "artifacts"],
        no_color=True,
    )
    assert colored.returncode == disabled.returncode == 0
    assert ANSI.search(colored.stdout) and ANSI.sub("", colored.stdout) == plain.stdout
    assert disabled.stdout == plain.stdout
    source = cwd / "optimization.msync"
    source.write_text(
        "fn label(value: String) -> String { value };\n"
        'label("optimized") -> ./optimized.txt;\n',
        encoding="utf-8",
    )
    checked = invoke(binary, cwd, ["check", "-O3", str(source)])
    assert checked.returncode == 0, checked.stderr
    for option, filename, level in (
        ([], "default.mso", 2),
        (["-O1"], "explicit.mso", 1),
        (["-O3"], "optimized.mso", 3),
    ):
        compiled = invoke(
            binary, cwd, ["compile", *option, str(source), "-o", filename]
        )
        assert compiled.returncode == 0, compiled.stderr
        plan = invoke(binary, cwd, ["plan", filename])
        assert plan.returncode == 0 and f"optimization O{level}" in plan.stdout, plan
    output = cwd / "optimized.txt"
    assert not output.exists()
    default_bound = invoke(
        binary, cwd, ["bind-ir", "default.mso", "-o", "default.mexe"]
    )
    assert default_bound.returncode == 0, default_bound.stderr
    default_plan = invoke(binary, cwd, ["plan", "default.mexe"])
    assert default_plan.returncode == 0 and "optimization O2" in default_plan.stdout
    for arguments in (
        ["run", str(source)],
        ["run-ir", "default.mso"],
        ["run-ir", "default.mexe"],
    ):
        executed = invoke(binary, cwd, arguments)
        assert executed.returncode == 0, executed.stderr
        assert output.read_bytes() == b"optimized"
        output.unlink()
    repl = invoke(
        binary,
        cwd,
        ["repl"],
        input_source='let label = "REPL_DEFAULT";\nlabel\n/quit\n',
    )
    assert repl.returncode == 0 and "REPL_DEFAULT" in repl.stdout, repl
    bound = invoke(binary, cwd, ["bind-ir", "optimized.mso", "-o", "optimized.mexe"])
    assert bound.returncode == 0, bound.stderr
    plan = invoke(binary, cwd, ["plan", "optimized.mexe"])
    assert plan.returncode == 0 and "optimization O3" in plan.stdout, plan
    source.unlink()
    executed = invoke(binary, cwd, ["run-ir", "optimized.mexe"])
    assert executed.returncode == 0, executed.stderr
    assert output.read_bytes() == b"optimized"
    return {
        "artifact_skill_color_modes": 3,
        "optimization_artifact_workflows": 3,
        "default_O2_execution_stages": 4,
    }


def runtime_diagnostics(binary: Path, cwd: Path) -> dict[str, int]:
    cases = {
        "transfer": "./missing-source -> ./untouched;\n",
        "process": "./missing-program();\n",
    }
    for name, source in cases.items():
        path = cwd / f"runtime-{name}.msync"
        artifact = path.with_suffix(".mso")
        path.write_text(source, encoding="utf-8")
        compiled = invoke(binary, cwd, ["compile", str(path), "-o", str(artifact)])
        assert compiled.returncode == 0, compiled.stderr
        direct = invoke(binary, cwd, ["--color", "never", "run", str(path)])
        assert direct.returncode != 0 and "error[E0300]" in direct.stderr, direct.stderr
        assert path.name in direct.stderr and source.strip() in direct.stderr, (
            direct.stderr
        )
        assert "^" in direct.stderr and "\x1b" not in direct.stderr, direct.stderr
        if name == "transfer":
            assert "help:" in direct.stderr, direct.stderr
        path.unlink()
        plain = invoke(binary, cwd, ["--color", "never", "run", str(artifact)])
        colored = invoke(binary, cwd, ["--color", "always", "run", str(artifact)])
        assert plain.returncode != 0 and colored.returncode == plain.returncode
        assert path.name in plain.stderr and "source offset" in plain.stderr, (
            plain.stderr
        )
        assert "1 |" not in plain.stderr, (
            "Unavailable source must not become fabricated line data"
        )
        assert (
            ANSI.search(colored.stderr) and ANSI.sub("", colored.stderr) == plain.stderr
        )
        assert not (cwd / "untouched").exists()
    return {"runtime_diagnostic_stages": len(cases) * 2}


def position(source: str, spelling: str, occurrence: int = 0) -> dict[str, int]:
    offset = -1
    for _ in range(occurrence + 1):
        offset = source.index(spelling, offset + 1)
    prefix = source[:offset]
    return {"line": prefix.count("\n"), "character": len(prefix.rsplit("\n", 1)[-1])}


def semantic_types(
    client: Client, uri: str, legend: list[str]
) -> dict[tuple[int, int], str]:
    result, _ = client.request(
        "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
    )
    line = column = 0
    tokens: dict[tuple[int, int], str] = {}
    for offset in range(0, len(result["data"]), 5):
        rows, columns, _, kind, _ = result["data"][offset : offset + 5]
        line += rows
        column = columns if rows else column + columns
        tokens[(line, column)] = legend[kind]
    return tokens


def editor(binary: Path, cwd: Path) -> dict[str, int]:
    version = invoke(binary, cwd, ["--version"])
    package = re.fullmatch(r"msync (\d+\.\d+\.\d+)\s*", version.stdout)
    assert version.returncode == 0 and package is not None, (
        version.stdout,
        version.stderr,
    )
    client = Client(binary)
    uri = (cwd / "screenshot.msync").as_uri()
    source = (
        "scope MyClass {\n"
        "    new: fn() { scope { x: ~/.codex } },\n"
        "    get: fn(some) { some::x }\n"
        "};\n"
        "let make = MyClass::new;\n"
        "let Alias = MyClass;\n"
        "let instance = make();\n"
        "Alias::get(instance) -> ./;\n"
    )
    try:
        initialized, _ = client.request(
            "initialize",
            {
                "capabilities": {
                    "workspace": {"semanticTokens": {"refreshSupport": True}}
                }
            },
        )
        assert initialized["serverInfo"]["version"] == package.group(1), initialized
        legend: list[str] = initialized["capabilities"]["semanticTokensProvider"][
            "legend"
        ]["tokenTypes"]
        notify(client, uri, 1, source, opening=True)
        assert diagnostics(client, uri, 1)["diagnostics"] == []
        tokens = semantic_types(client, uri, legend)
        refresh = next(
            (
                message
                for message in client.notifications
                if message.get("method") == "workspace/semanticTokens/refresh"
            ),
            None,
        )
        assert refresh is not None and "id" in refresh, client.notifications
        reply = json.dumps(
            {"jsonrpc": "2.0", "id": refresh["id"], "result": None}
        ).encode("utf-8")
        client.input.write(
            f"Content-Length: {len(reply)}\r\n\r\n".encode("ascii") + reply
        )
        client.input.flush()
        checks = [
            ("fn", 0, "keyword"),
            ("scope", 0, "keyword"),
            ("scope", 1, "keyword"),
            ("MyClass", 0, "type"),
            ("MyClass", 1, "type"),
            ("new", 0, "function"),
            ("new", 1, "function"),
            ("get", 0, "function"),
            ("get", 1, "function"),
            ("make", 0, "function"),
            ("make", 1, "function"),
            ("Alias", 0, "type"),
            ("Alias", 1, "type"),
            ("instance", 0, "variable"),
            ("instance", 1, "variable"),
        ]
        for spelling, count, expected in checks:
            location = position(source, spelling, count)
            actual = tokens.get((location["line"], location["character"]))
            assert actual == expected, (spelling, count, actual, expected, tokens)
        for spelling, expected in [
            ("make", "fn"),
            ("MyClass", "Scope"),
            ("instance", "x"),
        ]:
            hover, _ = client.request(
                "textDocument/hover",
                {"textDocument": {"uri": uri}, "position": position(source, spelling)},
            )
            text = hover["contents"]["value"]
            assert (
                expected in text
                and "typed expression" not in text
                and "\x1b" not in text
            ), hover

        for spelling, fragments in [
            ("let", ["mutable value", "fixed", "shadows"]),
            ("scope", ["Field values are mutable", "types stay fixed", "?"]),
            ("fn", ["callable value", "specialized"]),
        ]:
            hover, _ = client.request(
                "textDocument/hover",
                {"textDocument": {"uri": uri}, "position": position(source, spelling)},
            )
            text = hover["contents"]["value"]
            assert all(fragment in text for fragment in fragments), hover
            assert "immutable lexical value" not in text, hover

        completions, _ = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": {"line": 8, "character": 0},
            },
        )
        suggestions = {item["label"]: item for item in completions}
        assert (
            suggestions["make"]["kind"] == 3
            and "fn make" in suggestions["make"]["detail"]
        ), suggestions
        assert (
            suggestions["Alias"]["kind"] == 7
            and "Scope" in suggestions["Alias"]["detail"]
        ), suggestions
        member_position = position(source, "get", 1)
        member_position["character"] += 1
        members, _ = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": member_position,
            },
        )
        member_suggestions = {item["label"]: item for item in members}
        assert set(member_suggestions) == {"get", "new"}, member_suggestions
        assert (
            member_suggestions["get"]["kind"] == 3
            and "fn get" in member_suggestions["get"]["detail"]
        ), member_suggestions
        definition, _ = client.request(
            "textDocument/definition",
            {
                "textDocument": {"uri": uri},
                "position": position(source, "make", 1),
            },
        )
        assert definition["uri"] == uri and definition["range"]["start"] == position(
            source, "make"
        ), definition

        invalid = "fn get(some) {\n    some::x\n};\nget(./);\n"
        notify(client, uri, 2, invalid)
        problem = diagnostics(client, uri, 2)["diagnostics"][0]
        assert "file:" not in problem["message"] and "\x1b" not in problem["message"], (
            problem
        )
        assert "Help:" in problem["message"] and "Path" in problem["message"], problem
        assert problem["range"]["start"]["line"] == 1, problem
        assert any(
            info["location"]["range"]["start"]["line"] == 3
            for info in problem["relatedInformation"]
        ), problem

        broken = "let first = true\nlet second = false;\n"
        notify(client, uri, 3, broken)
        problem = diagnostics(client, uri, 3)["diagnostics"][0]
        actions, _ = client.request(
            "textDocument/codeAction",
            {
                "textDocument": {"uri": uri},
                "range": problem["range"],
                "context": {"diagnostics": [problem]},
            },
        )
        assert actions, (
            "A diagnostic lightbulb must offer its separator repair",
            problem,
        )
        changes = actions[0]["edit"]["documentChanges"][0]
        assert changes["textDocument"] == {"uri": uri, "version": 3}, changes
        edit = changes["edits"][0]
        start = edit["range"]["start"]
        end = edit["range"]["end"]
        lines = broken.splitlines(keepends=True)
        first = sum(map(len, lines[: start["line"]])) + start["character"]
        last = sum(map(len, lines[: end["line"]])) + end["character"]
        repaired = broken[:first] + edit["newText"] + broken[last:]
        notify(client, uri, 4, repaired)
        assert diagnostics(client, uri, 4)["diagnostics"] == []
        return {
            "inferred_token_sites": len(checks),
            "typed_hovers": 3,
            "keyword_guidance": 3,
            "quickfix_roundtrips": 1,
            "inferred_completion_items": 4,
            "local_definition": 1,
        }
    finally:
        client.close()


def editing_recovery(binary: Path, cwd: Path) -> dict[str, int]:
    client = Client(binary)
    uri = (cwd / "editing.msync").as_uri()
    declaration = "scope Box { get: fn(x) { x }, value: true };\n"
    try:
        initialized, _ = client.request("initialize", {"capabilities": {}})
        legend = initialized["capabilities"]["semanticTokensProvider"]["legend"][
            "tokenTypes"
        ]
        valid = declaration + "Box::get(true);\n"
        notify(client, uri, 1, valid, opening=True)
        first_tokens = semantic_types(client, uri, legend)
        declared = position(valid, "Box")
        assert first_tokens[(declared["line"], declared["character"])] == "type", (
            first_tokens
        )
        assert diagnostics(client, uri, 1)["diagnostics"] == []

        unfinished = declaration + "Box::"
        notify(client, uri, 2, unfinished)
        assert diagnostics(client, uri, 2)["diagnostics"]
        hover, _ = client.request(
            "textDocument/hover",
            {
                "textDocument": {"uri": uri},
                "position": position(unfinished, "Box"),
            },
        )
        assert "Box: Scope" in hover["contents"]["value"], hover
        members, _ = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": {"line": 1, "character": 5},
            },
        )
        suggestions = {item["label"]: item for item in members}
        assert set(suggestions) == {"get", "value"}, suggestions
        assert (
            suggestions["get"]["kind"] == 3 and "fn get" in suggestions["get"]["detail"]
        ), suggestions
        assert "Bool" in suggestions["value"]["detail"], suggestions

        shadowed = declaration + "let Box = ./;\nBox::"
        notify(client, uri, 3, shadowed)
        assert diagnostics(client, uri, 3)["diagnostics"]
        members, _ = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": {"line": 2, "character": 5},
            },
        )
        labels = {item["label"] for item in members}
        assert not labels.intersection({"get", "value"}), members
        shadow, _ = client.request(
            "textDocument/hover",
            {
                "textDocument": {"uri": uri},
                "position": position(shadowed, "Box", 1),
            },
        )
        assert "Box: Path" in shadow["contents"]["value"], shadow
        return {
            "unfinished_scope_member_completions": 2,
            "shadowed_scope_recovery": 1,
            "first_inferred_tokens_without_refresh": 1,
        }
    finally:
        client.close()


def regex_coloring(binary: Path, cwd: Path) -> dict[str, int]:
    client = Client(binary)
    uri = (cwd / "regex-colors.msync").as_uri()
    source = (
        "let label = match ./file {\n"
        "    <(?<fn>if|else|match)?> => { true },\n"
        "    _ => { false },\n};\n"
        'let text = "<fn?>"; // <if?>\n'
        "let value? = null;\n"
        "release; acquire; acqrel true;\n"
    )
    try:
        initialized, _ = client.request("initialize", {"capabilities": {}})
        legend = initialized["capabilities"]["semanticTokensProvider"]["legend"][
            "tokenTypes"
        ]
        notify(client, uri, 1, source, opening=True)
        assert diagnostics(client, uri, 1)["diagnostics"] == []
        tokens = semantic_types(client, uri, legend)
        assert tokens[(1, 4)] == "regexp", tokens
        assert [point for point in tokens if point[0] == 1 and point[1] < 27] == [
            (1, 4)
        ], tokens
        assert tokens[(0, 12)] == "controlKeyword", tokens
        question = position(source, "?")
        assert (question["line"], question["character"]) not in tokens
        admitted = position(source, "value?")
        admitted["character"] += 5
        assert tokens[(admitted["line"], admitted["character"])] == "controlKeyword"
        for keyword in ("release", "acquire", "acqrel"):
            barrier = position(source, keyword)
            assert tokens[(barrier["line"], barrier["character"])] == "controlKeyword"
        unfinished = "match ./file { <(?<fn>if"
        notify(client, uri, 2, unfinished)
        assert diagnostics(client, uri, 2)["diagnostics"]
        tokens = semantic_types(client, uri, legend)
        assert tokens[(0, 15)] == "regexp", tokens
        assert all(point[1] <= 15 for point in tokens), tokens
        return {
            "complete_and_incomplete_regex_color_regions": 2,
            "directional_barrier_control_tokens": 3,
        }
    finally:
        client.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--reference-only",
        action="store_true",
        help="Only help/color and optimization artifact workflows",
    )
    arguments = parser.parse_args()
    parent = ROOT / ".temp/presentation-v080"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as directory:
        cwd = Path(directory)
        if arguments.reference_only:
            result = {
                **terminal_guidance(arguments.binary.resolve(), cwd),
                **optimization_reference(arguments.binary.resolve(), cwd),
            }
        else:
            result = complete_presentation(arguments.binary.resolve(), cwd)
    print(json.dumps(result, indent=2))


def complete_presentation(binary: Path, cwd: Path) -> dict[str, int]:
    return {
        **terminal(binary, cwd),
        **optimization_reference(binary, cwd),
        **runtime_diagnostics(binary, cwd),
        **editor(binary, cwd),
        **editing_recovery(binary, cwd),
        **regex_coloring(binary, cwd),
    }


if __name__ == "__main__":
    main()
