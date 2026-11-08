#!/usr/bin/env python3
"""Apply contextual LSP completions, then compile and inspect their real value types."""

from __future__ import annotations

import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import uuid
from collections.abc import Callable
from pathlib import Path
from typing import Any, cast

from lsp_analysis_journey import diagnostics, notify
from lsp_latency import Client

ROOT = Path(__file__).resolve().parents[2]
CARET = "⟦caret⟧"


def lsp_position(text: str, offset: int) -> dict[str, int]:
    before = text[:offset]
    return {
        "line": before.count("\n"),
        "character": len(before.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2,
    }


def source_offset(text: str, position: dict[str, int]) -> int:
    lines = text.splitlines(keepends=True)
    line = position["line"]
    return sum(map(len, lines[:line])) + len(
        lines[line].encode("utf-16-le")[: position["character"] * 2].decode("utf-16-le")
    )


def apply_edit(text: str, edit: dict[str, Any]) -> str:
    start = source_offset(text, edit["range"]["start"])
    end = source_offset(text, edit["range"]["end"])
    return text[:start] + edit["newText"] + text[end:]


def native_run(
    binary: Path, environment: dict[str, str], cwd: Path, source: str, expected: str
) -> None:
    path = cwd / "completed.msync"
    path.write_text(source, encoding="utf-8")
    checked = subprocess.run(
        [str(binary), "--no-config", "check", str(path)],
        cwd=cwd,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=15,
    )
    assert checked.returncode == 0, (source, checked.stderr)
    result = subprocess.run(
        [str(binary), "--no-config", "repl"],
        input=source + "\n/quit\n",
        cwd=cwd,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=15,
    )
    assert result.returncode == 0 and result.stdout.strip() == expected, (
        source,
        result.stdout,
        result.stderr,
    )


def quote(text: str) -> str:
    return '"' + text.replace('"', '""') + '"'


def path_expression(raw: str) -> str:
    """Preserve the user's path spelling; escape only syntax-sensitive items."""
    separators = {"/", "\\"} if os.name == "nt" else {"/"}
    keywords = {
        "true",
        "false",
        "null",
        "fn",
        "scope",
        "if",
        "else",
        "match",
        "for",
        "in",
        "is",
        "not",
        "and",
        "or",
        "exists",
        "decltype",
        "return",
        "break",
        "loop",
        "native",
        "Bool",
        "Range",
        "String",
        "Function",
        "Scope",
        "Path",
        "Process",
        "Connection",
        "Null",
        "Type",
        "Number",
    }
    parts: list[str] = []
    start = 0
    for index in range(len(raw) + 1):
        if index != len(raw) and raw[index] not in separators:
            continue
        part = raw[start:index]
        drive = start == 0 and len(part) == 2 and part[0].isalpha() and part[1] == ":"
        special = not drive and (
            part in keywords
            or part.isdigit()
            or any(
                character.isspace() or character in '{}()[],;?"$|<>!=:+*'
                for character in part
            )
        )
        parts.append(quote(part) if special else part)
        if index != len(raw):
            parts.append(raw[index])
        start = index + 1
    return "".join(parts)


def run(binary: Path, directory: Path) -> dict[str, int]:
    workspace = directory / "workspace"
    home = directory / "home"
    workspace.mkdir()
    (home / ".ssh/hosts.d").mkdir(parents=True)
    (workspace / "plain.txt").write_text("PLAIN", encoding="utf-8")
    (workspace / "two words.txt").write_bytes(b"SOURCE_BYTES_NOT_FILENAME")
    names = [
        "space name.txt",
        "semi;colon",
        'quote"name',
        "可莉.txt",
        "😀file.txt",
        "hash#name",
        "cash$money",
        "true",
        "12",
    ]
    if os.name == "nt":
        names.remove('quote"name')
    for name in names:
        (workspace / name).write_text("fixture", encoding="utf-8")
    if os.name != "nt":
        (workspace / "colon:name").write_text("colon", encoding="utf-8")
        (workspace / "report::20").write_text("colons", encoding="utf-8")
    (workspace / "nested").mkdir()
    (workspace / "nested/child.txt").write_text("child", encoding="utf-8")
    (directory / "parent-file.txt").write_text("parent", encoding="utf-8")
    (home / "home-file.txt").write_text("home", encoding="utf-8")
    (home / ".ssh/config").write_text(
        'Host forge forge-backup\n  HostName never-contact.invalid\nInclude "hosts.d/*.conf"\n'
        "Host *.wild !excluded\n  User nobody\n",
        encoding="utf-8",
    )
    (home / ".ssh/hosts.d/aliases.conf").write_text(
        "Host lab-alias\n  HostName also-never-contact.invalid\n",
        encoding="utf-8",
    )
    (home / ".ssh/known_hosts").write_text(
        "2001:db8::1 ssh-ed25519 AAAA\n", encoding="utf-8"
    )
    environment = dict(os.environ)
    environment.update(
        HOME=str(home),
        USERPROFILE=str(home),
        APPDATA=str(home / "AppData/Roaming"),
        NO_COLOR="1",
    )
    client = Client(binary, cwd=workspace, environment=environment)
    uri = (workspace / "editing.msync").as_uri()
    version = 0

    def complete(marked: str) -> tuple[str, list[dict[str, Any]]]:
        nonlocal version
        version += 1
        offset = marked.index(CARET)
        text = marked.replace(CARET, "")
        notify(client, uri, version, text, opening=version == 1)
        result, _ = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": lsp_position(text, offset),
            },
        )
        return text, cast(list[dict[str, Any]], result)

    def selected(fragment: str, expected_expression: str, expected_type: str) -> None:
        text, items = complete("let selected = " + fragment + ";\ndecltype(selected)\n")
        candidates = [
            item
            for item in items
            if apply_edit(text, item.get("textEdit", {}))
            == f"let selected = {expected_expression};\ndecltype(selected)\n"
        ]
        assert len(candidates) == 1, (fragment, expected_expression, items)
        candidate = candidates[0]
        assert expected_type in candidate.get("detail", ""), candidate
        edited = apply_edit(text, candidate["textEdit"])
        assert (
            edited == f"let selected = {expected_expression};\ndecltype(selected)\n"
        ), edited
        native_run(binary, environment, workspace, edited, expected_type)

    try:
        client.request(
            "initialize", {"rootUri": workspace.as_uri(), "capabilities": {}}
        )
        cases = [
            ("./pl" + CARET, "./plain.txt", "Path"),
            ("./spa" + CARET, "./space name.txt", "Path"),
            ("./semi" + CARET, "./semi;colon", "Path"),
            ("./可" + CARET, "./可莉.txt", "Path"),
            ("./😀" + CARET, "./😀file.txt", "Path"),
            ("./hash" + CARET, "./hash#name", "Path"),
            ("./cash" + CARET, "./cash$money", "Path"),
            ("./tru" + CARET, "./true", "Path"),
            ("./1" + CARET, "./12", "Path"),
            ("../pa" + CARET, "../parent-file.txt", "Path"),
            ("~/ho" + CARET, "~/home-file.txt", "Path"),
            ("./nest" + CARET, "./nested/", "Path"),
            ("./nest" + CARET + "/child.txt", "./nested/child.txt", "Path"),
            ('"./nest' + CARET + '/child.txt"', "./nested/child.txt", "String"),
            ('"./pl' + CARET + '"', "./plain.txt", "String"),
            ("./pl" + CARET + "NOT_A_SUFFIX", "./plain.txt", "Path"),
            ("./two w" + CARET, "./two words.txt", "Path"),
        ]
        if os.name != "nt":
            cases.append(("./quo" + CARET, './quote"name', "Path"))
            cases.append(("./colo" + CARET, "./colon:name", "Path"))
            cases.append(('"./quo' + CARET + '"', './quote"name', "String"))
            cases.append(('"./report::2' + CARET + '"', "./report::20", "String"))
            cases.append(('./"report::2' + CARET + '"', "./report::20", "Path"))
        absolute_prefix = str(workspace / "pl")
        absolute_target = str(workspace / "plain.txt")
        if not any(character.isspace() for character in absolute_target):
            cases.append((absolute_prefix + CARET, absolute_target, "Path"))
        for fragment, raw, expected in cases:
            expression = quote(raw) if expected == "String" else path_expression(raw)
            selected(fragment, expression, expected)
        windows_partial_roots = 0
        if os.name == "nt":
            _, rooted = complete("let selected = \\" + CARET + ";\n")
            assert any("textEdit" in item for item in rooted), rooted
            windows_partial_roots += 1
            for prefix in (
                "\\\\",
                "\\\\msync-incomplete-host",
                "\\\\msync-incomplete-host\\",
            ):
                _, partial = complete("let selected = " + prefix + CARET + ";\n")
                assert partial == [], (prefix, partial)
                windows_partial_roots += 1

        (workspace / "two words.txt").write_bytes(b"SOURCE_BYTES_NOT_FILENAME")
        (workspace / "alpha").write_bytes(b"SELECTED_ALPHA")
        (workspace / "other").write_bytes(b"WRONG_SHADOWED_BINDING")

        def transfer(
            marked: str,
            basename: str,
            expected: bytes,
            destination: str,
            expected_type: str,
        ) -> None:
            text, items = complete(marked)
            matches = [
                item
                for item in items
                if basename in item.get("textEdit", {}).get("newText", "")
            ]
            assert len(matches) == 1, (marked, items)
            item = matches[0]
            assert expected_type in item.get("detail", ""), item
            edited = apply_edit(text, item["textEdit"])
            source = workspace / "transfer-completed.msync"
            source.write_text(edited, encoding="utf-8")
            result = subprocess.run(
                [str(binary), "--no-config", "run", str(source)],
                cwd=workspace,
                env=environment,
                capture_output=True,
                check=False,
                timeout=15,
            )
            assert result.returncode == 0, (edited, result.stderr)
            assert (workspace / destination).read_bytes() == expected, edited

        transfer(
            "./two w" + CARET + " -> ./space-output;\n",
            "two words.txt",
            b"SOURCE_BYTES_NOT_FILENAME",
            "space-output",
            "Path",
        )
        transfer(
            "let alpha = ./other;\n./al" + CARET + " -> ./identity-output;\n",
            "alpha",
            b"SELECTED_ALPHA",
            "identity-output",
            "Path",
        )
        transfer(
            '"./two w' + CARET + '" -> ./literal-output;\n',
            "two words.txt",
            b"./two words.txt",
            "literal-output",
            "String",
        )
        _, hosts = complete("let selected = ssh://" + CARET + ";\n")
        replacements = {item.get("textEdit", {}).get("newText", "") for item in hosts}
        assert {
            "forge",
            "forge-backup",
            "lab-alias",
        } <= replacements, hosts
        assert not any(
            "*" in replacement or "!excluded" in replacement
            for replacement in replacements
        ), hosts
        for prefix, alias in [("ssh://for", "forge"), ("ssh://lab", "lab-alias")]:
            text, items = complete(
                "let selected = " + prefix + CARET + ";\ndecltype(selected)\n"
            )
            matches = [
                item
                for item in items
                if item.get("textEdit", {}).get("newText") == alias
            ]
            assert len(matches) == 1 and "Connection" in matches[0].get("detail", ""), (
                alias,
                items,
            )
            native_run(
                binary,
                environment,
                workspace,
                apply_edit(text, matches[0]["textEdit"]),
                "Connection",
            )
        ipv6, ipv6_items = complete(
            "let selected = ssh://user@[2001" + CARET + "];\ndecltype(selected)\n"
        )
        ipv6_edits = [
            item
            for item in ipv6_items
            if item.get("textEdit", {}).get("newText") == "[2001:db8::1]"
        ]
        assert len(ipv6_edits) == 1, ipv6_items
        ipv6_completed = apply_edit(ipv6, ipv6_edits[0]["textEdit"])
        assert "ssh://user@[2001:db8::1]" in ipv6_completed, ipv6_completed
        native_run(binary, environment, workspace, ipv6_completed, "Connection")
        for marked in [
            "# ./pl" + CARET,
            "// ssh://for" + CARET,
            "match ./ { <./pl" + CARET + "> => { true }, _ => { false } }",
        ]:
            _, items = complete(marked)
            assert not any("textEdit" in item for item in items), (marked, items)
        return {
            "applied_path_completions": len(cases),
            "windows_partial_roots": windows_partial_roots,
            "ssh_alias_sources": 2,
            "noncode_contexts": 3,
            "completion_actual_io_oracles": 3,
        }
    finally:
        client.close()


def lexical_scope_queries(binary: Path, directory: Path) -> dict[str, int]:
    client = Client(binary, cwd=directory)
    uri = (directory / "scope-editing.msync").as_uri()
    library = "scope Library { run: fn(value: Path) { value }, ready: true };\n"
    cases: list[tuple[str, dict[str, str], set[str] | None]] = [
        ("fn outer(file: Path) { fi" + CARET, {"file": "Path"}, None),
        (
            library
            + "let captured = Library;\nfn outer(file: Path) { let inner = captured; inner::"
            + CARET,
            {"run": "fn run", "ready": "Bool"},
            {"run", "ready"},
        ),
        (library + "fn outer() { let Library = ./; Library::" + CARET, {}, set()),
        (
            "scope Outer { Inner: scope { run: fn(x) { x }, active: true } };\nOuter::Inner::"
            + CARET,
            {"run": "fn run", "active": "Bool"},
            {"run", "active"},
        ),
        (
            "fn make() { scope { run: fn(x) { x }, active: true } };\nmake()::" + CARET,
            {"run": "fn run", "active": "Bool"},
            {"run", "active"},
        ),
        (
            library + "fn outer(file: Path) { Library::run(fi" + CARET,
            {"file": "Path"},
            None,
        ),
    ]
    try:
        client.request(
            "initialize", {"rootUri": directory.as_uri(), "capabilities": {}}
        )
        for version, (marked, expected, exact) in enumerate(cases, 1):
            offset = marked.index(CARET)
            text = marked.replace(CARET, "")
            notify(client, uri, version, text, opening=version == 1)
            response, _ = client.request(
                "textDocument/completion",
                {
                    "textDocument": {"uri": uri},
                    "position": lsp_position(text, offset),
                },
            )
            items = {item["label"]: item for item in response}
            for name, detail in expected.items():
                assert name in items and detail in (items[name].get("detail") or ""), (
                    marked,
                    name,
                    items,
                )
            if exact:
                assert set(items) == exact, (marked, items)
            elif exact == set():
                assert not set(items).intersection({"run", "ready"}), (marked, items)
        return {"nested_lexical_scope_queries": len(cases)}
    finally:
        client.close()


def alias_path_queries(binary: Path, directory: Path) -> dict[str, int]:
    actual = directory / "actual"
    actual.mkdir()
    (actual / "file.txt").write_bytes(b"REAL_PATH_VALUE")
    for trap in ["folder", "fi"]:
        (directory / trap).mkdir()
        (directory / trap / "false-candidate.txt").write_bytes(
            b"DO_NOT_READ_ALIAS_SPELLING"
        )
    client = Client(binary, cwd=directory)
    uri = (directory / "alias-editing.msync").as_uri()
    cases = [
        ("let folder = ./actual;\nfolder/fi", "folder/"),
        ("scope Config { root: ./actual };\nConfig::root/fi", "Config::root/"),
        ("fn factory() { ./actual };\nfactory()/fi", "factory()/"),
        ('let text = "./actual";\ntext/fi', "text/"),
    ]
    try:
        client.request(
            "initialize", {"rootUri": directory.as_uri(), "capabilities": {}}
        )
        for version, (prefix, retained) in enumerate(cases, 1):
            text = prefix + " -> ./alias-output;\n"
            notify(client, uri, version, text, opening=version == 1)
            response, _ = client.request(
                "textDocument/completion",
                {
                    "textDocument": {"uri": uri},
                    "position": lsp_position(text, len(prefix)),
                },
            )
            matches = [
                item
                for item in response
                if "file.txt" in item.get("textEdit", {}).get("newText", "")
            ]
            assert len(matches) == 1 and "Path" in matches[0].get("detail", ""), (
                prefix,
                response,
            )
            edited = apply_edit(text, matches[0]["textEdit"])
            assert retained + "file.txt" in edited, edited
            source = directory / "alias-run.msync"
            source.write_text(edited, encoding="utf-8")
            result = subprocess.run(
                [str(binary), "--no-config", "run", str(source)],
                cwd=directory,
                capture_output=True,
                check=False,
                timeout=15,
            )
            assert result.returncode == 0, (edited, result.stderr)
            assert (directory / "alias-output").read_bytes() == b"REAL_PATH_VALUE", (
                edited
            )
        unknown = "fn unknown(fi: Path) { fi/fa"
        notify(client, uri, len(cases) + 1, unknown)
        response, _ = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": lsp_position(unknown, len(unknown)),
            },
        )
        items = {item["label"]: item for item in response}
        assert "fi" in items and "Path" in (items["fi"].get("detail") or ""), items
        assert not any("false-candidate" in str(item) for item in response), response
        return {
            "known_path_alias_actual_reads": len(cases),
            "unknown_path_alias_no_io_guess": 1,
        }
    finally:
        client.close()


def compressed_scope_queries(binary: Path, directory: Path) -> dict[str, int]:
    home = directory / "compact-home"
    (home / ".ssh").mkdir(parents=True)
    (home / ".ssh/config").write_text(
        "Host forge\n HostName never-contact.invalid\n", encoding="utf-8"
    )
    (directory / "space compact.txt").write_bytes(b"SPACE")
    environment = dict(os.environ)
    environment.update(HOME=str(home), USERPROFILE=str(home), NO_COLOR="1")
    client = Client(binary, cwd=directory, environment=environment)
    uri = (directory / "compact-scope.msync").as_uri()
    cases = [
        (
            "scope C {remote:ssh://for" + CARET + "};",
            "ssh://forge",
            "remote",
            "Connection",
        ),
        (
            "scope C {root:./spa" + CARET + "};",
            './"space compact.txt"',
            "root",
            "Path",
        ),
    ]
    try:
        client.request(
            "initialize", {"rootUri": directory.as_uri(), "capabilities": {}}
        )
        for version, (marked, replacement, member, expected_type) in enumerate(
            cases, 1
        ):
            offset = marked.index(CARET)
            text = marked.replace(CARET, "")
            notify(client, uri, version, text, opening=version == 1)
            response, _ = client.request(
                "textDocument/completion",
                {
                    "textDocument": {"uri": uri},
                    "position": lsp_position(text, offset),
                },
            )
            matches = [
                item
                for item in response
                if apply_edit(text, item.get("textEdit", {}))
                == "scope C {" + member + ":" + replacement + "};"
            ]
            assert len(matches) == 1, (marked, replacement, response)
            edited = apply_edit(text, matches[0]["textEdit"])
            assert edited == "scope C {" + member + ":" + replacement + "};", edited
            native_run(
                binary,
                environment,
                directory,
                edited + f"\nlet selected = C::{member};\ndecltype(selected)\n",
                expected_type,
            )
        return {"compact_scope_field_completions": len(cases)}
    finally:
        client.close()


def remote_queries(
    binary: Path,
    directory: Path,
    endpoint: str,
    remote: str,
    environment: dict[str, str],
    change: Callable[[str], None],
    before_transfer: Callable[[], None] | None = None,
) -> dict[str, int]:
    client = Client(binary, cwd=directory, environment=environment)
    uri = (directory / "remote-editing.msync").as_uri()
    version = 0
    try:
        client.request(
            "initialize", {"rootUri": directory.as_uri(), "capabilities": {}}
        )

        def query(
            prefix: str,
            *,
            alias: bool = False,
            shadowed: bool = False,
            stored: bool = False,
        ) -> tuple[str, list[dict[str, Any]]]:
            nonlocal version
            version += 1
            declaration = f"let remote = {endpoint};\n" if alias or stored else ""
            if shadowed:
                declaration += (
                    "{ let remote = ssh://msync-unselected.invalid; null; };\n"
                )
            qualifier = "remote" if alias else endpoint
            if stored:
                declaration += "let folder = remote::" + quote(remote) + ";\n"
                fragment = "folder/" + prefix
            else:
                fragment = qualifier + "::" + remote + "/" + prefix
            text = (
                declaration + "let selected = " + fragment + ";\ndecltype(selected)\n"
            )
            offset = text.index(fragment) + len(fragment)
            notify(client, uri, version, text, opening=version == 1)
            response, _ = client.request(
                "textDocument/completion",
                {
                    "textDocument": {"uri": uri},
                    "position": lsp_position(text, offset),
                },
            )
            return text, cast(list[dict[str, Any]], response)

        def chosen(
            prefix: str,
            basename: str,
            *,
            alias: bool = False,
            shadowed: bool = False,
            stored: bool = False,
        ) -> str:
            text, items = query(prefix, alias=alias, shadowed=shadowed, stored=stored)
            matches = [
                item
                for item in items
                if basename in item.get("textEdit", {}).get("newText", "")
            ]
            assert len(matches) == 1, (endpoint, prefix, basename, items)
            item = matches[0]
            assert "Path" in item.get("detail", ""), item
            edited = apply_edit(text, item["textEdit"])
            assert ("remote::" if alias or stored else endpoint + "::") in edited, (
                edited
            )
            native_run(binary, environment, directory, edited, "Path")
            return edited

        chosen("pl", "plain.txt")
        chosen("spa", "space name.txt", alias=True)
        chosen("pl", "plain.txt", alias=True, shadowed=True)
        change("touch " + shlex.quote(remote + "/plain-new-after-query.txt"))
        chosen("pl", "plain-new-after-query.txt")
        stored = chosen("pl", "plain.txt", stored=True)
        assert "folder/plain.txt" in stored, stored
        if before_transfer is not None:
            before_transfer()
        script = stored.replace(
            "decltype(selected)\n", "selected -> ./remote-alias-output;\n"
        )
        source = directory / "remote-alias-read.msync"
        source.write_text(script, encoding="utf-8")
        transferred = subprocess.run(
            [str(binary), "--no-config", "run", str(source)],
            cwd=directory,
            env=environment,
            capture_output=True,
            check=False,
            timeout=30,
        )
        assert transferred.returncode == 0, (script, transferred.stderr)
        assert (directory / "remote-alias-output").read_bytes() == b"remote", script
        return {
            "remote_endpoint_queries": 5,
            "remote_alias_completion": 3,
            "remote_stored_path_actual_read": 1,
            "restored_outer_endpoint_after_shadow": 1,
            "remote_refresh_visible": 1,
        }
    finally:
        client.close()


def distinct_endpoint_queries(
    binary: Path,
    directory: Path,
    endpoint: str,
    remote_directory: Path,
    remote_pwd: str,
    environment: dict[str, str],
) -> dict[str, int]:
    relative = Path(os.path.relpath(remote_directory, remote_pwd))
    local_cwd = directory / "domain-local-cwd"
    local_cwd.mkdir()
    local_directory = local_cwd / relative
    local_directory.mkdir(parents=True)
    (local_directory / "LOCAL_ONLY.txt").write_bytes(b"local domain")
    (remote_directory / "REMOTE_ONLY.txt").write_bytes(b"remote domain")
    raw = "./" + relative.as_posix() + "/"
    declaration = "let remote = " + endpoint + ";\n"
    stored = declaration + "let folder = remote::" + quote(raw) + ";\n"
    cases = [
        (declaration + "remote::" + raw + CARET, "REMOTE_ONLY.txt"),
        (declaration + "remote::(" + raw + CARET + ")", "REMOTE_ONLY.txt"),
        (
            "scope Endpoints { host: "
            + endpoint
            + " };\nEndpoints::host::("
            + raw
            + CARET
            + ")",
            "REMOTE_ONLY.txt",
        ),
        (stored + "folder/" + CARET, "REMOTE_ONLY.txt"),
        (stored + "$local::(folder/" + CARET + ")", "LOCAL_ONLY.txt"),
        (stored + "remote::($local::(folder/" + CARET + "))", "REMOTE_ONLY.txt"),
        (declaration + "$local::(remote::(" + raw + CARET + "))", "LOCAL_ONLY.txt"),
        (declaration + "remote:: " + quote(raw + CARET), "REMOTE_ONLY.txt"),
        (declaration + "remote::(" + quote(raw + CARET) + ")", "REMOTE_ONLY.txt"),
        (declaration + "remote::({ " + quote(raw + CARET) + " })", "REMOTE_ONLY.txt"),
        (
            declaration
            + "remote::(if true { "
            + quote(raw + CARET)
            + " } else { ./unused })",
            "REMOTE_ONLY.txt",
        ),
        (
            declaration
            + "remote::(match true { true => { "
            + quote(raw + CARET)
            + " }, false => { ./unused } })",
            "REMOTE_ONLY.txt",
        ),
        (
            declaration
            + "fn identity(path: Path) { path };\nremote::(identity("
            + quote(raw + CARET)
            + "))",
            "LOCAL_ONLY.txt",
        ),
        (
            declaration
            + "remote::(if exists "
            + quote(raw + CARET)
            + " { ./unused } else { ./unused })",
            "LOCAL_ONLY.txt",
        ),
        (
            declaration + "remote::((fn() { " + quote(raw + CARET) + " })())",
            "LOCAL_ONLY.txt",
        ),
    ]
    client = Client(binary, cwd=local_cwd, environment=environment)
    uri = (local_cwd / "endpoint-domain.msync").as_uri()
    try:
        client.request("initialize", {"capabilities": {}})
        for version, (marked, expected) in enumerate(cases, 1):
            offset = marked.index(CARET)
            text = marked.replace(CARET, "") + ";\n"
            notify(client, uri, version, text, opening=version == 1)
            response, _ = client.request(
                "textDocument/completion",
                {"textDocument": {"uri": uri}, "position": lsp_position(text, offset)},
            )
            names = {
                marker
                for marker in ("LOCAL_ONLY.txt", "REMOTE_ONLY.txt")
                if any(
                    marker in item.get("textEdit", {}).get("newText", "")
                    for item in response
                )
            }
            assert names == {expected}, (version, marked, expected, names, response)
        invalid_version = len(cases) + 1
        malformed = "fn inspect(scope) { scope::endpoint::(/tmp/) };\n"
        notify(client, uri, invalid_version, malformed)
        problem = diagnostics(client, uri, invalid_version)
        assert problem["diagnostics"], problem
        corrected = "scope Ready { useful: true };\nReady::"
        notify(client, uri, invalid_version + 1, corrected)
        recovered, _ = client.request(
            "textDocument/completion",
            {
                "textDocument": {"uri": uri},
                "position": lsp_position(corrected, len(corrected)),
            },
        )
        items = {item["label"]: item for item in recovered}
        assert "useful" in items and "Bool" in (items["useful"].get("detail") or ""), (
            items
        )
        assert client.process.poll() is None
        return {
            "distinct_local_remote_domains": len(cases),
            "same_process_malformed_edit_recovery": 1,
        }
    finally:
        client.close()


def wsl(binary: Path, directory: Path, distro: str) -> dict[str, int]:
    executable = shutil.which("wsl.exe")
    assert executable is not None, "Actual WSL completion requires native wsl.exe"
    remote = "/tmp/msync-completion-" + uuid.uuid4().hex

    def command(source: str) -> None:
        result = subprocess.run(
            [executable, "--distribution", distro, "--exec", "sh", "-c", source],
            capture_output=True,
            check=False,
            timeout=30,
        )
        assert result.returncode == 0, (source, result.stdout, result.stderr)

    command(
        "mkdir -p "
        + shlex.quote(remote)
        + "; printf remote > "
        + shlex.quote(remote + "/plain.txt")
        + "; printf spaced > "
        + shlex.quote(remote + "/space name.txt")
    )
    try:
        return remote_queries(
            binary, directory, "wsl://" + distro, remote, dict(os.environ), command
        )
    finally:
        command("rm -rf -- " + shlex.quote(remote))


def ssh_fixture(binary: Path, directory: Path) -> dict[str, int]:
    sys.path.insert(0, str(ROOT / "legacy/benches"))
    from ssh_v065 import Fixture, lines

    with Fixture(ROOT / ".temp/completion-v080/ssh") as fixture:
        remote = fixture.root / "remote-files"
        remote.mkdir()
        (remote / "plain.txt").write_text("remote", encoding="utf-8")
        (remote / "space name.txt").write_text("spaced", encoding="utf-8")
        environment: dict[str, str] = fixture.environment()

        def change(source: str) -> None:
            result = subprocess.run(
                ["/usr/bin/ssh", "-F", str(fixture.config), "Perf_Fixture", source],
                cwd=directory,
                env=environment,
                capture_output=True,
                check=False,
                timeout=15,
            )
            assert result.returncode == 0, (result.stdout, result.stderr)

        before = len(lines(fixture.calls))

        def completed_queries() -> None:
            calls = lines(fixture.calls)[before:]
            assert calls.count("ssh") == 1 and "scp" not in calls, calls

        result = remote_queries(
            binary,
            directory,
            "ssh://Perf_Fixture",
            str(remote),
            environment,
            change,
            completed_queries,
        )
        calls = lines(fixture.calls)[before:]
        assert calls.count("ssh") == 2 and "scp" not in calls, calls
        result["retained_native_ssh_clients"] = 1
        result["selected_cli_transfer_ssh_clients"] = 1
        pwd_result = subprocess.run(
            ["/usr/bin/ssh", "-F", str(fixture.config), "Perf_Fixture", "pwd"],
            cwd=directory,
            env=environment,
            capture_output=True,
            check=False,
            timeout=15,
        )
        assert pwd_result.returncode == 0, pwd_result.stderr
        domain_remote = fixture.root / "endpoint-directory"
        domain_remote.mkdir()
        before_domain = len(lines(fixture.calls))
        result.update(
            distinct_endpoint_queries(
                binary,
                directory,
                "ssh://Perf_Fixture",
                domain_remote,
                pwd_result.stdout.decode("utf-8").strip(),
                environment,
            )
        )
        domain_calls = lines(fixture.calls)[before_domain:]
        assert domain_calls.count("ssh") == 1 and "scp" not in domain_calls, (
            domain_calls
        )
        result["domain_query_retained_native_ssh_clients"] = 1
        return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--wsl-distro", help="Use an already-owned actual Windows WSL distribution"
    )
    parser.add_argument(
        "--ssh-fixture",
        action="store_true",
        help="Start the owned encrypted loopback SSH fixture",
    )
    arguments = parser.parse_args()
    parent = ROOT / ".temp/completion-v080"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=parent) as directory:
        binary = arguments.binary.resolve()
        working = Path(directory).resolve()
        result = run(binary, working)
        result.update(lexical_scope_queries(binary, working))
        result.update(alias_path_queries(binary, working))
        result.update(compressed_scope_queries(binary, working))
        if arguments.wsl_distro:
            result.update(wsl(binary, working, arguments.wsl_distro))
        if arguments.ssh_fixture:
            result.update(ssh_fixture(binary, working))
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
