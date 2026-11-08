#!/usr/bin/env python3
"""Answer native OpenSSH host-key/passphrase prompts during real LSP completion."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any

from lsp_latency import Client

ROOT: Path = Path(__file__).resolve().parents[2]


def reply(client: Client, identifier: str, value: str | None) -> None:
    body: bytes = json.dumps(
        {"jsonrpc": "2.0", "id": identifier, "result": {"value": value}},
        ensure_ascii=False,
    ).encode("utf-8")
    client.input.write(f"Content-Length: {len(body)}\r\n\r\n".encode("ascii") + body)
    client.input.flush()


def completion(
    client: Client,
    uri: str,
    source: str,
    password: str,
    *,
    cancel_secret: bool = False,
    dismiss_secret: bool = False,
    expected_prompt_kinds: tuple[str, ...] = ("confirm", "secret"),
) -> tuple[list[dict[str, Any]], list[dict[str, str]]]:
    client.next_id += 1
    identifier: int = client.next_id
    client.send(
        "textDocument/completion",
        {
            "textDocument": {"uri": uri},
            "position": {
                "line": source.count("\n"),
                "character": len(source.rsplit("\n", 1)[-1]),
            },
        },
        identifier,
    )
    prompts: list[dict[str, str]] = []
    cancelled: bool = False
    cancellation_ui: bool = False
    while True:
        message: dict[str, Any] | Exception = client.responses.get(timeout=30)
        if isinstance(message, Exception):
            raise message
        if message.get("method") == "msync/sshPrompt":
            prompt: dict[str, str] = message["params"]
            prompts.append({key: prompt[key] for key in ("endpoint", "kind", "prompt")})
            assert (
                len(prompts) <= len(expected_prompt_kinds)
                and prompt["kind"] == expected_prompt_kinds[len(prompts) - 1]
            ), prompts
            if cancel_secret and prompt["kind"] == "secret":
                client.send("$/cancelRequest", {"id": identifier})
                cancelled = True
            elif dismiss_secret and prompt["kind"] == "secret":
                reply(client, str(message["id"]), None)
                cancelled = True
            else:
                answer: str = "yes" if prompt["kind"] == "confirm" else password
                reply(client, str(message["id"]), answer)
        elif message.get("method") == "msync/sshPromptCancel":
            cancellation_ui = True
        elif message.get("id") == identifier:
            if cancelled:
                assert message.get("error", {}).get("code") == -32800, message
                if cancel_secret and not cancellation_ui:
                    while not cancellation_ui:
                        pending: dict[str, Any] | Exception = client.responses.get(
                            timeout=10
                        )
                        if isinstance(pending, Exception):
                            raise pending
                        cancellation_ui = (
                            pending.get("method") == "msync/sshPromptCancel"
                        )
                return [], prompts
            assert "error" not in message, message
            result: Any = message.get("result")
            return result.get("items", []) if isinstance(
                result, dict
            ) else result, prompts
        else:
            client.notifications.append(message)


def open_document(client: Client, uri: str, source: str) -> None:
    client.send(
        "textDocument/didOpen",
        {
            "textDocument": {
                "uri": uri,
                "version": 1,
                "languageId": "msync",
                "text": source,
            }
        },
    )


def lines(path: Path) -> list[str]:
    return path.read_text(errors="replace").splitlines() if path.exists() else []


def exercise(
    binary: Path,
    directory: Path,
    environment: dict[str, str],
    remote: str,
    log: Path,
    write_remote: Callable[[str, str], None],
    password: str,
) -> dict[str, Any]:
    write_remote("native.txt", "remote only")
    source: str = f"let remote = ssh://Perf_Fixture;\nremote::{remote}/"
    uri: str = (directory / "native-auth.msync").as_uri()
    before: int = len(lines(log))
    client: Client = Client(binary, cwd=directory, environment=environment)
    try:
        client.request(
            "initialize",
            {"capabilities": {}, "initializationOptions": {"sshPrompt": True}},
        )
        open_document(client, uri, source)
        items, prompts = completion(client, uri, source, password)
        assert any("native.txt" in item["label"] for item in items), items
        assert [item["kind"] for item in prompts] == ["confirm", "secret"], prompts
        assert "passphrase" in prompts[1]["prompt"].lower(), prompts
        write_remote("new-between-requests.txt", "new remote observation")
        updated, repeated = completion(
            client, uri, source, password, expected_prompt_kinds=()
        )
        assert not repeated, repeated
        assert any("new-between-requests.txt" in item["label"] for item in updated), (
            updated
        )
        authenticated: list[str] = lines(log)[before:]
        assert sum("Accepted publickey for " in line for line in authenticated) == 1, (
            authenticated
        )
        report: dict[str, Any] = {
            "native_prompt_kinds": [item["kind"] for item in prompts],
            "retained_authenticated_sessions": 1,
        }
    finally:
        client.close()
    cancelled: Client = Client(binary, cwd=directory, environment=environment)
    try:
        cancelled.request(
            "initialize",
            {"capabilities": {}, "initializationOptions": {"sshPrompt": True}},
        )
        open_document(cancelled, uri, source)
        _, abandoned = completion(
            cancelled,
            uri,
            source,
            password,
            cancel_secret=True,
            expected_prompt_kinds=("secret",),
        )
        assert [item["kind"] for item in abandoned] == ["secret"], abandoned
        report["cancelled_native_question"] = 1
    finally:
        cancelled.close()
    dismissed: Client = Client(binary, cwd=directory, environment=environment)
    try:
        dismissed.request(
            "initialize",
            {"capabilities": {}, "initializationOptions": {"sshPrompt": True}},
        )
        open_document(dismissed, uri, source)
        _, rejected = completion(
            dismissed,
            uri,
            source,
            password,
            dismiss_secret=True,
            expected_prompt_kinds=("secret",),
        )
        assert [item["kind"] for item in rejected] == ["secret"], rejected
        report["dismissed_native_question_without_reprompt"] = 1
    finally:
        dismissed.close()
    return report


def run(binary: Path, distribution: str | None) -> dict[str, Any]:
    password: str = "msync actual native prompt fixture"
    if distribution is not None:
        from wsl_auth_fixture import WslAuthFixture

        with WslAuthFixture(distribution, binary, password) as guest_fixture:
            return exercise(
                guest_fixture.binary,
                guest_fixture.root,
                guest_fixture.environment(),
                guest_fixture.remote,
                guest_fixture.log,
                guest_fixture.write_remote,
                password,
            )
    sys.path.insert(0, str(ROOT / "legacy" / "benches"))
    from ssh_v065 import Fixture

    with Fixture(ROOT / ".temp" / "completion-auth") as fixture:
        subprocess.run(
            [
                "ssh-keygen",
                "-q",
                "-p",
                "-P",
                "",
                "-N",
                password,
                "-f",
                str(fixture.root / "client"),
            ],
            check=True,
            capture_output=True,
        )
        fixture.config.write_text(
            fixture.config.read_text()
            .replace("BatchMode yes", "BatchMode no")
            .replace("StrictHostKeyChecking yes", "StrictHostKeyChecking ask")
        )
        (fixture.root / "known_hosts").write_text("")
        environment: dict[str, str] = fixture.environment()
        for name in ("SSH_ASKPASS", "SSH_ASKPASS_REQUIRE", "SSH_AUTH_SOCK"):
            environment.pop(name, None)
        remote: Path = fixture.root / "remote"
        remote.mkdir()

        def write_remote(name: str, content: str) -> None:
            (remote / name).write_text(content)

        return exercise(
            binary,
            fixture.root,
            environment,
            remote.as_posix(),
            fixture.log,
            write_remote,
            password,
        )


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument(
        "--wsl-distro",
        help="Use this caller-owned disposable WSL distribution with the native Windows SSH client",
    )
    arguments: argparse.Namespace = parser.parse_args()
    if os.name != "posix" and arguments.wsl_distro is None:
        raise SystemExit(
            "This real loopback OpenSSH server fixture runs on POSIX; the actual askpass helper probe is cross-platform."
        )
    print(json.dumps(run(arguments.binary.resolve(), arguments.wsl_distro), indent=2))


if __name__ == "__main__":
    main()
