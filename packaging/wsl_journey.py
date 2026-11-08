#!/usr/bin/env python3
"""Exercise the native Windows client against one owned, imported WSL1 distro."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import tempfile
import time
import uuid
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
ALPINE_VERSION = "3.22.2"
ROOTFS_URL = (
    "https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/x86_64/"
    "alpine-minirootfs-3.22.2-x86_64.tar.gz"
)
ROOTFS_SHA256 = "412454ed98025ad9cd910d13f3d448b184e81502baa83180e89f98d0f13674be"


@dataclass(frozen=True)
class Command:
    arguments: list[str]
    directory: str
    returncode: int | None
    stdout: str
    stderr: str
    elapsed_seconds: float
    timed_out: bool = False


class CommandFailed(RuntimeError):
    def __init__(self, command: Command) -> None:
        self.command = command
        status = "timed out" if command.timed_out else f"exited {command.returncode}"
        super().__init__(
            f"{command.arguments!r} {status}: {command.stdout}\n{command.stderr}"
        )


def decode(data: bytes | str | None) -> str:
    if data is None:
        return ""
    if isinstance(data, str):
        return data
    return data.decode("utf-16-le" if b"\0" in data else "utf-8", errors="replace")


class Runner:
    def __init__(self, directory: Path) -> None:
        self.directory = directory
        self.commands: list[Command] = []

    def run(
        self,
        arguments: list[str],
        *,
        directory: Path | None = None,
        environment: dict[str, str] | None = None,
        check: bool = True,
        timeout: int = 120,
    ) -> Command:
        cwd = directory or self.directory
        started = time.perf_counter()
        try:
            result = subprocess.run(
                arguments,
                cwd=cwd,
                env=environment,
                capture_output=True,
                check=False,
                timeout=timeout,
            )
            command = Command(
                arguments,
                str(cwd),
                result.returncode,
                decode(result.stdout),
                decode(result.stderr),
                time.perf_counter() - started,
            )
        except subprocess.TimeoutExpired as error:
            command = Command(
                arguments,
                str(cwd),
                None,
                decode(error.stdout),
                decode(error.stderr),
                time.perf_counter() - started,
                timed_out=True,
            )
        self.commands.append(command)
        if check and command.returncode != 0:
            raise CommandFailed(command)
        return command


def download_rootfs(destination: Path) -> None:
    with urlopen(ROOTFS_URL, timeout=60) as response, destination.open("wb") as output:
        shutil.copyfileobj(response, output)
    actual = hashlib.sha256(destination.read_bytes()).hexdigest()
    if actual != ROOTFS_SHA256:
        raise ValueError(f"Official Alpine rootfs SHA-256 mismatch: {actual}")


def source(distro: str) -> str:
    return f"""let remote = wsl://{distro};
let home = ~/.codex/config.toml;
let relative = ./dir/file;
let home_text: String = remote::home;
let relative_text: String = remote::relative;
home_text -> raw-home.txt;
relative_text -> raw-relative.txt;
home -> remote::home;
relative -> remote::relative;
remote::home -> downloaded-home-after.txt;
remote::~/.codex/config.toml -> downloaded-home-before.txt;
remote::relative -> downloaded-relative-after.txt;
remote::./dir/file -> downloaded-relative-before.txt;
"""


def local_environment(directory: Path) -> dict[str, str]:
    home = directory / "home"
    (home / ".codex").mkdir(parents=True)
    (directory / "dir").mkdir()
    (directory / "dir/sibling").write_bytes(b"keep local sibling")
    environment = dict(os.environ)
    environment.update(HOME=str(home), USERPROFILE=str(home))
    return environment


def payloads(directory: Path, label: str) -> tuple[bytes, bytes]:
    home = f"{label}: Windows home to WSL home 可莉\n".encode()
    relative = f"{label}: relative endpoint payload 雪\n".encode()
    (directory / "home/.codex/config.toml").write_bytes(home)
    (directory / "dir/file").write_bytes(relative)
    return home, relative


def invoke_msync(
    runner: Runner,
    binary: Path,
    directory: Path,
    environment: dict[str, str],
    arguments: list[str],
) -> None:
    runner.run(
        [str(binary), "--no-config", *arguments],
        directory=directory,
        environment=environment,
    )


def verify_outputs(directory: Path, home: bytes, relative: bytes) -> None:
    expected: dict[str, bytes] = {
        "raw-home.txt": b"~/.codex/config.toml",
        "raw-relative.txt": b"./dir/file",
        "downloaded-home-after.txt": home,
        "downloaded-home-before.txt": home,
        "downloaded-relative-after.txt": relative,
        "downloaded-relative-before.txt": relative,
    }
    for name, data in expected.items():
        path = directory / name
        assert path.read_bytes() == data, (name, path.read_bytes(), data)
        path.unlink()
    assert (directory / "dir/sibling").read_bytes() == b"keep local sibling"


def transfer_journeys(
    runner: Runner,
    binary: Path,
    wsl: str,
    distro: str,
    workspace: Path,
) -> list[dict[str, object]]:
    first, second = workspace / "first", workspace / "second"
    first.mkdir()
    second.mkdir()
    environments = {first: local_environment(first), second: local_environment(second)}
    data_a, data_b = (
        payloads(first, "captured-first"),
        payloads(second, "runtime-second"),
    )
    document = first / "main.msync"
    document.write_text(source(distro), encoding="utf-8")
    object_file, executable = first / "main.mso", first / "main.mexe"
    runner.run([wsl, "--terminate", distro], check=False)
    before = runner.run([wsl, "--list", "--running", "--quiet"])
    for arguments in (
        ["check", str(document)],
        ["compile", str(document), "-o", str(object_file)],
        ["bind-ir", str(object_file), "-o", str(executable)],
    ):
        invoke_msync(runner, binary, first, environments[first], arguments)
    after = runner.run([wsl, "--list", "--running", "--quiet"])
    assert distro.casefold() not in before.stdout.casefold(), before
    assert distro.casefold() not in after.stdout.casefold(), after
    assert not (first / "raw-home.txt").exists()
    remote_before = runner.run(
        [
            wsl,
            "--distribution",
            distro,
            "--exec",
            "sh",
            "-c",
            '[ ! -e "$HOME/.codex/config.toml" ] && cat "$HOME/.codex/sibling"',
        ]
    )
    assert remote_before.stdout == "keep remote sibling", remote_before
    reports: list[dict[str, object]] = []
    for stage, artifact, cwd, bound, expected in (
        ("source", document, first, first, data_a),
        ("mso", object_file, second, second, data_b),
        ("mexe", executable, second, first, data_a),
    ):
        invoke_msync(runner, binary, cwd, environments[cwd], ["run", str(artifact)])
        verify_outputs(bound, *expected)
        assert (first / "dir/sibling").read_bytes() == b"keep local sibling"
        assert (second / "dir/sibling").read_bytes() == b"keep local sibling"
        remote = runner.run(
            [
                wsl,
                "--distribution",
                distro,
                "--exec",
                "sh",
                "-c",
                'cat "$HOME/.codex/config.toml"; printf "\\nMSYNC-SIBLING\\n"; cat "$HOME/.codex/sibling"',
            ],
            directory=cwd,
        )
        assert (
            remote.stdout.encode()
            == expected[0] + b"\nMSYNC-SIBLING\nkeep remote sibling"
        ), remote
        reports.append(
            {
                "stage": stage,
                "directory": str(cwd),
                "bound_directory": str(bound),
                "home_sha256": hashlib.sha256(expected[0]).hexdigest(),
                "relative_sha256": hashlib.sha256(expected[1]).hexdigest(),
                "home_and_relative_before_after_qualification": "passed",
            }
        )
        if document.exists():
            document.unlink()
    return reports


def exercise(binary: Path, output: Path) -> int:
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix="wsl-journey-", dir=temporary))
    runner = Runner(workspace)
    distro = "msync-probe-" + uuid.uuid4().hex
    attempted_import = False
    imported = False
    report: dict[str, Any] = {
        "platform": platform.platform(),
        "binary": str(binary),
        "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "workspace": str(workspace),
        "distro": distro,
        "wsl_version": 1,
        "rootfs": {
            "version": ALPINE_VERSION,
            "url": ROOTFS_URL,
            "sha256": ROOTFS_SHA256,
        },
        "outcome": "unavailable",
    }
    try:
        wsl = shutil.which("wsl.exe")
        if os.name != "nt" or wsl is None:
            report["reason"] = "A native Windows host with wsl.exe is required."
            return 0
        report["outcome"] = "setup_failed"
        runner.run([wsl, "--status"], check=False)
        report["distributions_before"] = runner.run([wsl, "--list", "--quiet"]).stdout
        archive = workspace / "alpine-rootfs.tar.gz"
        download_rootfs(archive)
        attempted_import = True
        registration = runner.run(
            [
                wsl,
                "--import",
                distro,
                str(workspace / "installed-distro"),
                str(archive),
                "--version",
                "1",
            ],
            check=False,
        )
        if registration.returncode != 0:
            report["outcome"] = "unavailable"
            report["reason"] = "The host could not import the owned WSL1 distribution."
            report["host_error"] = asdict(registration)
            return 0
        imported = True
        boot = runner.run(
            [
                wsl,
                "--distribution",
                distro,
                "--exec",
                "sh",
                "-c",
                "printf '%s' \"$HOME\"",
            ],
            check=False,
        )
        if boot.returncode != 0:
            report["outcome"] = "unavailable"
            report["reason"] = (
                "The host imported the distribution but could not start WSL1."
            )
            report["host_error"] = asdict(boot)
            return 0
        report["remote_home"] = boot.stdout
        report["outcome"] = "setup_failed"
        runner.run(
            [
                wsl,
                "--distribution",
                distro,
                "--exec",
                "sh",
                "-c",
                (
                    "apk add --no-cache coreutils findutils tar && "
                    "tar --version && find --version && stat --version && "
                    "apk info -v coreutils findutils tar && "
                    'mkdir -p "$HOME/.codex" && printf \'keep remote sibling\' > "$HOME/.codex/sibling"'
                ),
            ],
            timeout=180,
        )
        report["outcome"] = "failed"
        report["journeys"] = transfer_journeys(runner, binary, wsl, distro, workspace)
        report["outcome"] = "passed"
        return 0
    except (
        AssertionError,
        OSError,
        ValueError,
        subprocess.TimeoutExpired,
        CommandFailed,
    ) as error:
        report["error"] = {"kind": type(error).__name__, "message": str(error)}
        return 1
    finally:
        if attempted_import:
            assert wsl is not None
            try:
                cleanup = runner.run([wsl, "--unregister", distro], check=False)
                report["cleanup"] = asdict(cleanup)
                if imported and cleanup.returncode != 0:
                    report["outcome"] = "cleanup_failed"
                runner.run([wsl, "--status"], check=False)
            except OSError as error:
                report["cleanup"] = {
                    "kind": type(error).__name__,
                    "message": str(error),
                }
                if imported:
                    report["outcome"] = "cleanup_failed"
        report["commands"] = [asdict(command) for command in runner.commands]
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, indent=2), flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".temp/platform-results/wsl.json"
    )
    args = parser.parse_args()
    code = exercise(args.binary.resolve(), args.output.resolve())
    report = json.loads(args.output.read_text(encoding="utf-8"))
    raise SystemExit(1 if report["outcome"] == "cleanup_failed" else code)


if __name__ == "__main__":
    main()
