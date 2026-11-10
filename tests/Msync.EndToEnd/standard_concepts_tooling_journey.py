#!/usr/bin/env python3
"""Exercise standard concept navigation, completion and persistent REPL use."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from collections.abc import Callable
from io import TextIOWrapper
from pathlib import Path
from typing import cast

from completion_journey import apply_edit
from language_upgrade_tooling_journey import position, request_at, token_roles
from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client
from terminal_screen import Screen

ROOT = Path(__file__).resolve().parents[2]


def protocol(binary: Path, directory: Path) -> dict[str, object]:
    uri = (directory / "standard-concepts.msync").as_uri()
    text = """fn first(value: $std::concepts::Product) {
    value::first
};
concept Named { $std::concepts::Product, name: String, };
concept LocalPair {
    first?: typename First,
    second?: typename Second,
    convert: fn(value: First) -> Second,
};
fn preserve(value: $std::concepts::Option?) -> $std::concepts::Option? {
    value
};
let selected? = preserve($std::data::Option::some("Klee"));
let arithmetic = fn(value: Number) -> Number { value + 1 };
let inferred = $std::meta::invoke_result(arithmetic, Number);
let capable = $std::meta::is_invocable(arithmetic, Number);
"""
    client = Client(binary, cwd=directory)
    try:
        initialized, _ = client.request("initialize", {"capabilities": {}})
        legend: list[str] = initialized["capabilities"]["semanticTokensProvider"][
            "legend"
        ]["tokenTypes"]
        notify(client, uri, 1, text, opening=True)
        assert not diagnostics(client, uri, 1)["diagnostics"]
        product = text.index("Product")
        hover = request_at(client, "textDocument/hover", uri, text, product)
        description = hover["contents"]["value"]
        assert "Product" in description and "first" in description, hover
        assert "independently typed" in description, hover
        definition = request_at(client, "textDocument/definition", uri, text, product)
        target = definition[0] if isinstance(definition, list) else definition
        assert target["uri"].startswith("msync-stdlib:") and target["uri"].endswith(
            "/concepts.msync"
        ), definition
        source, _ = client.request("msync/stdlibSource", {"uri": target["uri"]})
        assert "concept Product" in source, source
        tokens, _ = client.request(
            "textDocument/semanticTokens/full", {"textDocument": {"uri": uri}}
        )
        roles = token_roles(text, tokens["data"], legend)
        for name in ("std", "concepts", "Product", "Option", "First", "Second"):
            assert roles.get(name) and set(roles[name]) == {"type"}, (name, roles)
        assert set(roles["fn"]) == {"keyword"}, roles
        assert set(roles["typename"]) == {"keyword"}, roles
        inferred = request_at(
            client, "textDocument/hover", uri, text, text.index("inferred =")
        )
        assert "Number" in inferred["contents"]["value"], inferred
        query_hover = request_at(
            client, "textDocument/hover", uri, text, text.index("is_invocable")
        )
        assert "...arguments" in query_hover["contents"]["value"], query_hover
        assert "without executing" in query_hover["contents"]["value"], query_hover
        binder = request_at(
            client, "textDocument/definition", uri, text, text.index("First)")
        )
        binder_target = binder[0] if isinstance(binder, list) else binder
        assert binder_target["uri"] == uri, binder
        binder_line = text.splitlines()[binder_target["range"]["start"]["line"]]
        assert "first?: typename First" in binder_line, binder
        optional = text.index("Option")
        option_hover = request_at(client, "textDocument/hover", uri, text, optional)
        assert "Null" in option_hover["contents"]["value"], option_hover
        option_definition = request_at(
            client, "textDocument/definition", uri, text, optional
        )
        option_target = (
            option_definition[0]
            if isinstance(option_definition, list)
            else option_definition
        )
        assert option_target["uri"] == target["uri"], option_definition
        inherited = request_at(
            client,
            "textDocument/completion",
            uri,
            text,
            text.index("value::first") + len("value::"),
        )
        labels = {item["label"] for item in inherited}
        assert {"first", "second"} <= labels, inherited

        invalid = '$std::containers::EqualitySet::with_equality(["x"], scope { missing: true });'
        notify(client, uri, 2, invalid)
        rejected = diagnostics(client, uri, 2)["diagnostics"]
        assert len(rejected) == 1 and rejected[0]["code"] == "E0214", rejected
        failure = rejected[0]
        assert "equal" in failure["message"].lower(), failure
        assert failure["range"]["start"] == position(invalid, invalid.index("scope")), (
            failure
        )
        assert any(
            item["location"]["uri"].startswith("msync-stdlib:")
            for item in failure.get("relatedInformation", [])
        ), failure

        completions: list[dict[str, str]] = []
        for version, (fragment, expected) in enumerate(
            (
                ("$", "$std::"),
                ("$std::", "concepts::"),
                ("$std::concepts::", "Product"),
                ("$std::concepts::Pro", "Product"),
                ("$std :: concepts :: Pro", "Product"),
                ("$std::concepts::Opt", "Option"),
            ),
            3,
        ):
            editing = f"fn inspect(value: {fragment}) {{ value }};"
            caret = editing.index(")")
            notify(client, uri, version, editing)
            diagnostics(client, uri, version)
            candidates = request_at(
                client, "textDocument/completion", uri, editing, caret
            )
            choice = next(item for item in candidates if item["label"] == expected)
            completed = apply_edit(editing, choice["textEdit"])
            assert "$$" not in completed and "concepts::concepts" not in completed
            assert choice["kind"] == 7 and "documentation" in choice, choice
            completions.append(
                {"typed": fragment, "inserted": choice["textEdit"]["newText"]}
            )
        expression = "$std::concepts::"
        notify(client, uri, version + 1, expression)
        diagnostics(client, uri, version + 1)
        runtime = request_at(
            client, "textDocument/completion", uri, expression, len(expression)
        )
        assert not any(item["label"] == "Product" for item in runtime), runtime
        return {
            "qualified_type_tokens": True,
            "hover_from_source_comments": True,
            "embedded_concept_definition": target["uri"],
            "inherited_operation_completion": sorted(labels),
            "prefix_completions": completions,
            "no_runtime_concept_value": True,
            "option_nullable_contract": True,
            "inline_typename_definition": True,
            "unevaluated_query_inspection": True,
            "policy_argument_diagnostic": True,
        }
    finally:
        client.close()


def repl(binary: Path, directory: Path) -> dict[str, object]:
    source = """fn preserve(value: $std::concepts::Option?) -> $std::concepts::Option? { value };
let selected? = preserve($std::data::Option::some("Klee"));
match selected { null => { "none" }, _ => { selected::value } }
$std::data::Option::some(null) is null
$std::data::Option::none() is null
$std::data::Result::fold($std::data::Result::ok("ok"), fn(value: String) { value }, fn(error: String) { error })
/quit
"""
    completed = subprocess.run(
        [str(binary), "--no-config", "repl"],
        input=source,
        cwd=directory,
        text=True,
        encoding="utf-8",
        capture_output=True,
        timeout=30,
        check=False,
    )
    assert completed.returncode == 0 and completed.stdout.splitlines() == [
        "Klee",
        "false",
        "true",
        "ok",
    ], (
        completed.stdout,
        completed.stderr,
    )
    for topic in (
        "language concepts",
        "library concepts",
        "library data",
        "library containers",
        "library meta",
    ):
        help_result = subprocess.run(
            [str(binary), "--no-config", "skill", *topic.split()],
            cwd=directory,
            text=True,
            encoding="utf-8",
            capture_output=True,
            timeout=30,
            check=False,
        )
        assert help_result.returncode == 0 and len(help_result.stdout) > 200, (
            topic,
            help_result.stderr,
        )
        if topic == "library concepts":
            assert "compile-time structural" in help_result.stdout.lower()
            assert "Call\nthese operations" not in help_result.stdout
    return {"persistent_repl_qualified_contract": True, "progressive_skill_pages": True}


def complete_product(
    key: Callable[[str], None],
    until: Callable[[Callable[[], bool], str], None],
    screen: Screen,
) -> None:
    key("fn identify(value: $std::concepts::Pro")
    key("\t")
    until(lambda: screen.contains("concept ProcessRunner"), "initial concept menu")
    key("\x1b[B")
    until(lambda: screen.contains("concept Product"), "selected Product documentation")
    key("\t")
    until(
        lambda: screen.contains("fn identify(value: $std::concepts::Product"),
        "committed Product in the input buffer",
    )
    key(') { value::first }; "CONCEPT_READY"')


def terminal(binary: Path, directory: Path) -> dict[str, object]:
    if os.name == "nt":
        from windows_terminal_journey import Terminal as WindowsTerminal

        windows = WindowsTerminal(binary, directory, color=True)
        try:
            windows.until(lambda: "> " in windows.plain, "standard concept prompt")
            screen = Screen(lambda: windows.raw)
            complete_product(windows.key, windows.until, screen)
            windows.send("\r")
            windows.prompt(windows.line("CONCEPT_READY"))
            windows.evaluate('identify(scope { first?: 41, second?: "kept" })', "41")
            windows.send("\x04")
            windows.exit()
        finally:
            windows.finish(directory / "standard-concept-terminal.raw")
    else:
        from terminal_journey import Terminal

        console = Terminal(binary, directory, no_color=False)
        try:
            console.until(lambda: "> " in console.plain, "standard concept prompt")
            screen = Screen(lambda: console.raw)
            complete_product(
                lambda text: console.key(text.encode()), console.until, screen
            )
            console.send(b"\r")
            console.prompt(console.line("CONCEPT_READY"))
            console.evaluate('identify(scope { first?: 41, second?: "kept" })', "41")
            console.send(b"\x04")
            console.process.wait(timeout=5)
            assert console.process.returncode == 0
        finally:
            console.finish(directory / "standard-concept-terminal.raw")
    return {
        "real_terminal_qualified_type_completion": True,
        "selected_product_invocation": 41,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    binary = arguments.binary.resolve()
    parent = ROOT / ".temp/standard-concepts-tooling"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as temporary:
        directory = Path(temporary)
        result = {
            **protocol(binary, directory),
            **repl(binary, directory),
            **terminal(binary, directory),
        }
    encoded = json.dumps(result, indent=2, ensure_ascii=False)
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(encoded + "\n", encoding="utf-8")
    if sys.stdout.encoding and sys.stdout.encoding.lower().replace("-", "") != "utf8":
        cast(TextIOWrapper, sys.stdout).reconfigure(encoding="utf-8")
    print(encoded)


if __name__ == "__main__":
    main()
