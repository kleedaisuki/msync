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

from lsp_analysis_journey import notify
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


def run(binary: Path, directory: Path) -> dict[str, int]:
    workspace = directory / "workspace"
    home = directory / "home"
    workspace.mkdir()
    (home / ".ssh/hosts.d").mkdir(parents=True)
    (workspace / "plain.txt").write_text("PLAIN", encoding="utf-8")
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
    (workspace / "nested").mkdir()
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
            if item.get("textEdit", {}).get("newText") == expected_expression
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
            ("./spa" + CARET, '"./space name.txt"', "String"),
            ("./semi" + CARET, './"semi;colon"', "Path"),
            ("./可" + CARET, "./可莉.txt", "Path"),
            ("./😀" + CARET, "./😀file.txt", "Path"),
            ("./hash" + CARET, "./hash#name", "Path"),
            ("./cash" + CARET, './"cash$money"', "Path"),
            ("./tru" + CARET, './"true"', "Path"),
            ("./1" + CARET, './"12"', "Path"),
            ("../pa" + CARET, "../parent-file.txt", "Path"),
            ("~/ho" + CARET, "~/home-file.txt", "Path"),
            ("./nest" + CARET, "./nested/", "Path"),
            ('"./pl' + CARET + '"', '"./plain.txt"', "String"),
            ("./pl" + CARET + "NOT_A_SUFFIX", "./plain.txt", "Path"),
        ]
        if os.name != "nt":
            cases.append(("./quo" + CARET, './"quote""name"', "Path"))
            cases.append(("./colo" + CARET, './"colon:name"', "Path"))
        else:
            cases = [
                (
                    fragment,
                    '$local::"./' + expression[3:-1] + '"'
                    if expression.startswith('./"')
                    else expression,
                    expected,
                )
                for fragment, expression, expected in cases
            ]
        absolute_prefix = str(workspace / "pl")
        absolute_target = str(workspace / "plain.txt")
        if not any(character.isspace() for character in absolute_target):
            cases.append((absolute_prefix + CARET, absolute_target, "Path"))
        if os.name != "nt":
            cases.append(('"./quo' + CARET + '"', '"./quote""name"', "String"))
        for fragment, expression, expected in cases:
            selected(fragment, expression, expected)
        _, hosts = complete("let selected = ssh://" + CARET + ";\n")
        replacements = {item.get("textEdit", {}).get("newText", "") for item in hosts}
        assert {
            "ssh://forge",
            "ssh://forge-backup",
            "ssh://lab-alias",
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
                if item.get("textEdit", {}).get("newText") == "ssh://" + alias
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
        for marked in [
            "# ./pl" + CARET,
            "// ssh://for" + CARET,
            "match ./ { <./pl" + CARET + "> => { true }, _ => { false } }",
        ]:
            _, items = complete(marked)
            assert not any("textEdit" in item for item in items), (marked, items)
        return {
            "applied_path_completions": len(cases),
            "ssh_alias_sources": 2,
            "noncode_contexts": 3,
        }
    finally:
        client.close()


def remote_queries(
    binary: Path,
    directory: Path,
    endpoint: str,
    remote: str,
    environment: dict[str, str],
    change: Callable[[str], None],
) -> dict[str, int]:
    client = Client(binary, cwd=directory, environment=environment)
    uri = (directory / "remote-editing.msync").as_uri()
    version = 0
    try:
        client.request(
            "initialize", {"rootUri": directory.as_uri(), "capabilities": {}}
        )

        def query(
            prefix: str, *, alias: bool = False
        ) -> tuple[str, list[dict[str, Any]]]:
            nonlocal version
            version += 1
            declaration = f"let remote = {endpoint};\n" if alias else ""
            qualifier = "remote" if alias else endpoint
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

        def chosen(prefix: str, basename: str, *, alias: bool = False) -> str:
            text, items = query(prefix, alias=alias)
            matches = [
                item
                for item in items
                if basename in item.get("textEdit", {}).get("newText", "")
            ]
            assert len(matches) == 1, (endpoint, prefix, basename, items)
            item = matches[0]
            assert "Path" in item.get("detail", ""), item
            edited = apply_edit(text, item["textEdit"])
            assert ("remote::" if alias else endpoint + "::") in edited, edited
            native_run(binary, environment, directory, edited, "Path")
            return edited

        chosen("pl", "plain.txt")
        chosen("spa", "space name.txt", alias=True)
        change("touch " + shlex.quote(remote + "/plain-new-after-query.txt"))
        chosen("pl", "plain-new-after-query.txt")
        return {
            "remote_endpoint_queries": 3,
            "remote_alias_completion": 1,
            "remote_refresh_visible": 1,
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
        result = remote_queries(
            binary, directory, "ssh://Perf_Fixture", str(remote), environment, change
        )
        calls = lines(fixture.calls)[before:]
        assert calls.count("ssh") == 1 and "scp" not in calls, calls
        result["retained_native_ssh_clients"] = 1
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
        if arguments.wsl_distro:
            result.update(wsl(binary, working, arguments.wsl_distro))
        if arguments.ssh_fixture:
            result.update(ssh_fixture(binary, working))
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
