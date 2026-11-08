"""Own a native Windows SSH client fixture inside a caller-owned disposable WSL."""

from __future__ import annotations

import getpass
import os
import shutil
import socket
import subprocess
import tempfile
import time
import uuid
from pathlib import Path
from types import TracebackType
from typing import Self

ROOT: Path = Path(__file__).resolve().parents[2]


class WslAuthFixture:
    def __init__(self, distribution: str, binary: Path, password: str) -> None:
        parent: Path = ROOT / ".temp" / "completion-auth-windows"
        parent.mkdir(parents=True, exist_ok=True)
        self.root: Path = Path(
            tempfile.mkdtemp(prefix="native prompt auth ", dir=parent)
        )
        self.distribution: str = distribution
        self.original_binary: Path = binary
        self.binary: Path = self.root / "native prompt product" / binary.name
        self.password: str = password
        self.native_ssh: Path = Path(os.environ["WINDIR"]) / "System32" / "OpenSSH"
        identifier: str = uuid.uuid4().hex
        self.guest: str = "/tmp/msync-auth-" + identifier
        self.user: str = "msync_auth_" + identifier[:8]
        self.remote: str = self.guest + "/remote"
        self.config: Path = self.root / "ssh_config"
        self.log: Path = self.root / "sshd.log"
        self.server: subprocess.Popen[bytes] | None = None
        self.port: int = 0

    def guest_run(
        self, program: str, *arguments: str, source: bytes | None = None
    ) -> bytes:
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            [
                "wsl.exe",
                "--distribution",
                self.distribution,
                "--user",
                "root",
                "--exec",
                "sh",
                "-c",
                program,
                "sh",
                *arguments,
            ],
            input=source,
            capture_output=True,
            timeout=30,
            check=False,
        )
        if result.returncode:
            raise RuntimeError(
                f"Owned WSL SSH fixture command failed ({result.returncode}): {result.stderr.decode(errors='replace')}"
            )
        return result.stdout

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
        if value is not None and self.log.exists():
            print(
                "Owned WSL SSH server diagnostics:\n"
                + self.log.read_text(errors="replace"),
                flush=True,
            )
        self.close()

    def start(self) -> None:
        shutil.copytree(self.original_binary.parent, self.binary.parent)
        key: Path = self.root / "client"
        subprocess.run(
            [
                str(self.native_ssh / "ssh-keygen.exe"),
                "-q",
                "-t",
                "ed25519",
                "-N",
                self.password,
                "-f",
                str(key),
            ],
            check=True,
            capture_output=True,
        )
        owner: str = os.environ.get("USERDOMAIN", ".") + "\\" + getpass.getuser()
        subprocess.run(
            ["icacls.exe", str(key), "/inheritance:r", "/grant:r", owner + ":(F)"],
            check=True,
            capture_output=True,
        )
        self.guest_run(
            'set -eu; mkdir -p /run/sshd "$1/home" "$1/remote"; chmod 755 "$1"; '
            'adduser -D -H -h "$1/home" -s /bin/sh "$2"; passwd -d "$2"; '
            'chown "$2:$2" "$1/home"; chmod 700 "$1/home"; '
            'ssh-keygen -q -t ed25519 -N "" -f "$1/host"; cat >"$1/authorized_keys"; chmod 644 "$1/authorized_keys"',
            self.guest,
            self.user,
            source=key.with_suffix(".pub").read_bytes(),
        )
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            self.port = int(listener.getsockname()[1])
        configuration: str = (
            f"ListenAddress 127.0.0.1\nPort {self.port}\nHostKey {self.guest}/host\n"
            f"PidFile {self.guest}/sshd.pid\nAuthorizedKeysFile {self.guest}/authorized_keys\n"
            f"AllowUsers {self.user}\nStrictModes yes\nUsePAM no\nPasswordAuthentication no\n"
            "KbdInteractiveAuthentication no\nPermitUserRC no\nPermitUserEnvironment no\n"
            "DisableForwarding yes\nPermitTTY no\nPrintMotd no\nPrintLastLog no\nUseDNS no\nLogLevel VERBOSE\n"
        )
        self.guest_run(
            'cat >"$1/sshd_config"; /usr/sbin/sshd -t -f "$1/sshd_config"',
            self.guest,
            source=configuration.encode(),
        )
        known: Path = self.root / "known_hosts"
        known.write_text("")
        self.config.write_text(
            f"Host Perf_Fixture\n HostName 127.0.0.1\n Port {self.port}\n User {self.user}\n"
            f' IdentityFile "{key.as_posix()}"\n UserKnownHostsFile "{known.as_posix()}"\n'
            " GlobalKnownHostsFile none\n IdentityAgent none\n IdentitiesOnly yes\n"
            " BatchMode no\n StrictHostKeyChecking ask\n UpdateHostKeys no\n"
            " ControlMaster no\n ControlPath none\n RequestTTY no\n LogLevel ERROR\n"
        )
        with self.log.open("wb") as log:
            self.server = subprocess.Popen(
                [
                    "wsl.exe",
                    "--distribution",
                    self.distribution,
                    "--user",
                    "root",
                    "--exec",
                    "sh",
                    "-c",
                    'printf "%s" "$$" >"$1/owner.pid"; exec /usr/sbin/sshd -D -e -f "$1/sshd_config"',
                    "sh",
                    self.guest,
                ],
                stdin=subprocess.DEVNULL,
                stdout=log,
                stderr=log,
            )
        deadline: float = time.monotonic() + 15
        while True:
            if self.server.poll() is not None:
                raise RuntimeError(
                    "Owned WSL SSH daemon exited:\n"
                    + self.log.read_text(errors="replace")
                )
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.1):
                    return
            except OSError:
                if time.monotonic() >= deadline:
                    raise RuntimeError(
                        "Owned WSL SSH daemon did not listen:\n"
                        + self.log.read_text(errors="replace")
                    ) from None
                time.sleep(0.01)

    def environment(self) -> dict[str, str]:
        result: dict[str, str] = dict(os.environ)
        for key in (
            "SSH_ASKPASS",
            "SSH_ASKPASS_REQUIRE",
            "SSH_AUTH_SOCK",
            "MSYNC_TRACE",
            "MSYNC_STATE_DIR",
            "MSYNC_CONFIG",
        ):
            result.pop(key, None)
        result["MSYNC_SSH_CONFIG"] = str(self.config)
        result["PATH"] = str(self.native_ssh) + os.pathsep + result.get("PATH", "")
        return result

    def write_remote(self, name: str, content: str) -> None:
        self.guest_run('cat >"$1/$2"', self.remote, name, source=content.encode())

    def close(self) -> None:
        try:
            if self.server is not None and self.server.poll() is None:
                self.guest_run(
                    'if [ -f "$1/owner.pid" ]; then kill "$(cat "$1/owner.pid")"; fi',
                    self.guest,
                )
                self.server.wait(timeout=10)
        finally:
            self.guest_run(
                'deluser "$2" 2>/dev/null || :; rm -rf "$1"', self.guest, self.user
            )
