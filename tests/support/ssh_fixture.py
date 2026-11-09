"""Own a real loopback OpenSSH server without changing host SSH configuration."""

from __future__ import annotations

import os
import pwd
import shlex
import signal
import socket
import subprocess
import tempfile
from collections.abc import Mapping
from contextlib import suppress
from pathlib import Path
from time import monotonic, sleep
from types import TracebackType
from typing import Self


def owned_run(
    arguments: list[str],
    *,
    cwd: Path,
    environment: Mapping[str, str] | None = None,
    source: bytes | None = None,
    timeout: float = 60.0,
) -> subprocess.CompletedProcess[bytes]:
    child: subprocess.Popen[bytes] = subprocess.Popen(
        arguments,
        cwd=cwd,
        env=environment,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        start_new_session=True,
    )
    try:
        output, errors = child.communicate(source, timeout=timeout)
    except BaseException:
        with suppress(ProcessLookupError):
            os.killpg(child.pid, signal.SIGTERM)
        try:
            child.communicate(timeout=2.0)
        except subprocess.TimeoutExpired:
            with suppress(ProcessLookupError):
                os.killpg(child.pid, signal.SIGKILL)
            child.communicate()
        raise
    assert child.returncode is not None
    return subprocess.CompletedProcess(arguments, child.returncode, output, errors)


def checked(result: subprocess.CompletedProcess[bytes]) -> bytes:
    if result.returncode:
        raise RuntimeError(
            f"command failed ({result.returncode}): {result.args!r}\n"
            f"stdout={result.stdout.decode(errors='replace')}\n"
            f"stderr={result.stderr.decode(errors='replace')}"
        )
    return result.stdout


def lines(path: Path) -> list[str]:
    return path.read_text(errors="replace").splitlines() if path.exists() else []


class Fixture:
    def __init__(self, parent: Path) -> None:
        parent.mkdir(parents=True, exist_ok=True)
        self.root: Path = Path(tempfile.mkdtemp(prefix="s-", dir=parent)).resolve()
        self.server: subprocess.Popen[bytes] | None = None
        self.log: Path = self.root / "sshd.log"
        self.calls: Path = self.root / "client-calls.log"
        self.config: Path = self.root / "ssh_config"
        self.port: int = 0

    def __enter__(self) -> Self:
        try:
            self.start()
        except BaseException:
            self.close()
            raise
        return self

    def __exit__(
        self,
        kind: type[BaseException] | None,
        value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        self.close()

    def start(self) -> None:
        for name in ("host", "client"):
            checked(
                owned_run(
                    [
                        "/usr/bin/ssh-keygen",
                        "-q",
                        "-t",
                        "ed25519",
                        "-N",
                        "",
                        "-f",
                        str(self.root / name),
                    ],
                    cwd=self.root,
                )
            )
        (self.root / "authorized_keys").write_bytes(
            (self.root / "client.pub").read_bytes()
        )
        listener: socket.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.bind(("127.0.0.1", 0))
        self.port = int(listener.getsockname()[1])
        listener.close()
        account: str = pwd.getpwuid(os.getuid()).pw_name
        server_config: Path = self.root / "sshd_config"
        server_config.write_text(
            f"ListenAddress 127.0.0.1\nPort {self.port}\nHostKey {self.root}/host\n"
            f"PidFile {self.root}/sshd.pid\nAuthorizedKeysFile {self.root}/authorized_keys\n"
            f"AllowUsers {account}\nStrictModes yes\nUsePAM no\nPasswordAuthentication no\n"
            "KbdInteractiveAuthentication no\nPermitUserRC no\nPermitUserEnvironment no\n"
            "DisableForwarding yes\nPermitTTY no\nPrintMotd no\nPrintLastLog no\nUseDNS no\n"
            "Subsystem sftp internal-sftp\nLogLevel VERBOSE\n"
        )
        (self.root / "known_hosts").write_text(
            f"[127.0.0.1]:{self.port} {(self.root / 'host.pub').read_text()}"
        )
        self.config.write_text(
            f"Host Perf_Fixture\n HostName 127.0.0.1\n Port {self.port}\n User {account}\n"
            f" IdentityFile {self.root}/client\n UserKnownHostsFile {self.root}/known_hosts\n"
            " GlobalKnownHostsFile none\n IdentityAgent none\n IdentitiesOnly yes\n"
            " BatchMode yes\n StrictHostKeyChecking yes\n UpdateHostKeys no\n"
            " ControlMaster no\n ControlPath none\n RequestTTY no\n"
            " ConnectTimeout 3\n ConnectionAttempts 1\n LogLevel ERROR\n"
        )
        self.write_wrappers()
        checked(
            owned_run(["/usr/sbin/sshd", "-t", "-f", str(server_config)], cwd=self.root)
        )
        with self.log.open("wb") as log:
            self.server = subprocess.Popen(
                ["/usr/sbin/sshd", "-D", "-e", "-f", str(server_config)],
                cwd=self.root,
                stdin=subprocess.DEVNULL,
                stdout=log,
                stderr=log,
                start_new_session=True,
            )
        deadline: float = monotonic() + 5.0
        while True:
            assert self.server is not None
            if self.server.poll() is not None:
                raise RuntimeError(f"sshd exited: {self.log.read_text()}")
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.1):
                    break
            except OSError:
                if monotonic() >= deadline:
                    raise RuntimeError(
                        f"sshd readiness timeout: {self.log.read_text()}"
                    )
                sleep(0.01)
        response: bytes = checked(
            owned_run(
                [
                    "/usr/bin/ssh",
                    "-F",
                    str(self.config),
                    "Perf_Fixture",
                    "printf connected",
                ],
                cwd=self.root,
            )
        )
        if response != b"connected":
            raise RuntimeError(f"unexpected real SSH response: {response!r}")

    def write_wrappers(self) -> None:
        directory: Path = self.root / "bin"
        directory.mkdir()
        for program in ("ssh", "scp"):
            wrapper: Path = directory / program
            wrapper.write_text(
                "#!/bin/sh\n"
                f"printf '%s\\n' {shlex.quote(program)} >> {shlex.quote(str(self.calls))}\n"
                f'exec /usr/bin/{program} "$@"\n'
            )
            wrapper.chmod(0o700)

    def environment(self) -> dict[str, str]:
        result: dict[str, str] = dict(os.environ)
        for name in ("MSYNC_CONFIG", "MSYNC_TRACE", "MSYNC_STATE_DIR"):
            result.pop(name, None)
        result.update(
            {
                "MSYNC_SSH_CONFIG": str(self.config),
                "NO_COLOR": "1",
                "PATH": str(self.root / "bin") + os.pathsep + result.get("PATH", ""),
            }
        )
        return result

    def close(self) -> None:
        if self.server is None:
            return
        if self.server.poll() is None:
            with suppress(ProcessLookupError):
                os.killpg(self.server.pid, signal.SIGTERM)
            try:
                self.server.wait(timeout=3.0)
            except subprocess.TimeoutExpired:
                with suppress(ProcessLookupError):
                    os.killpg(self.server.pid, signal.SIGKILL)
                self.server.wait(timeout=3.0)
        self.server = None
