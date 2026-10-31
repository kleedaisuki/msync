#!/usr/bin/env python3
"""Real loopback OpenSSH command-latency probes; no host SSH configuration edits."""
from __future__ import annotations

import argparse
from contextlib import suppress
from dataclasses import asdict, dataclass
import hashlib
import json
import os
from pathlib import Path
import pwd
import resource
import shlex
import signal
import socket
import statistics
import subprocess
import tempfile
from time import monotonic, perf_counter_ns, sleep
from types import TracebackType
from typing import Mapping


ROOT: Path = Path(__file__).resolve().parent.parent
CASES: tuple[str, ...] = ("upload", "download", "exists", "range", "five-files", "missing-download")
PAYLOAD: bytes = bytes(range(256)) * 16
MTIME_NS: int = 1_700_000_000_000_000_000


def owned_run(
    arguments: list[str], *, cwd: Path, environment: Mapping[str, str] | None = None,
    source: bytes | None = None, timeout: float = 60.0,
) -> subprocess.CompletedProcess[bytes]:
    child: subprocess.Popen[bytes] = subprocess.Popen(
        arguments, cwd=cwd, env=environment, stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True,
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


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def lines(path: Path) -> list[str]:
    return path.read_text(errors="replace").splitlines() if path.exists() else []


@dataclass(frozen=True)
class Observation:
    case: str
    arm: str
    sample: int
    milliseconds: float
    local_user_cpu_ms: float
    local_system_cpu_ms: float
    ssh_clients: int
    scp_clients: int
    authentications: int
    remote_sessions: int


class Fixture:
    def __init__(self, parent: Path) -> None:
        parent.mkdir(parents=True, exist_ok=True)
        self.root: Path = Path(tempfile.mkdtemp(prefix="s-", dir=parent)).resolve()
        self.server: subprocess.Popen[bytes] | None = None
        self.log: Path = self.root / "sshd.log"
        self.calls: Path = self.root / "client-calls.log"
        self.config: Path = self.root / "ssh_config"
        self.port: int = 0

    def __enter__(self) -> Fixture:
        try:
            self.start()
        except BaseException:
            self.close()
            raise
        return self

    def __exit__(
        self, kind: type[BaseException] | None, value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        self.close()

    def start(self) -> None:
        for name in ("host", "client"):
            checked(owned_run(
                ["/usr/bin/ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(self.root / name)],
                cwd=self.root,
            ))
        (self.root / "authorized_keys").write_bytes((self.root / "client.pub").read_bytes())
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
        checked(owned_run(["/usr/sbin/sshd", "-t", "-f", str(server_config)], cwd=self.root))
        with self.log.open("wb") as log:
            self.server = subprocess.Popen(
                ["/usr/sbin/sshd", "-D", "-e", "-f", str(server_config)],
                cwd=self.root, stdin=subprocess.DEVNULL, stdout=log, stderr=log,
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
                    raise RuntimeError(f"sshd readiness timeout: {self.log.read_text()}")
                sleep(0.01)
        response: bytes = checked(owned_run(
            ["/usr/bin/ssh", "-F", str(self.config), "Perf_Fixture", "printf connected"], cwd=self.root,
        ))
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
                f"exec /usr/bin/{program} \"$@\"\n"
            )
            wrapper.chmod(0o700)

    def environment(self) -> dict[str, str]:
        result: dict[str, str] = dict(os.environ)
        for name in ("MSYNC_CONFIG", "MSYNC_TRACE", "MSYNC_STATE_DIR"):
            result.pop(name, None)
        result.update({
            "MSYNC_SSH_CONFIG": str(self.config), "NO_COLOR": "1",
            "PATH": str(self.root / "bin") + os.pathsep + result.get("PATH", ""),
        })
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


def prepared_file(path: Path, payload: bytes = PAYLOAD, *, mtime_ns: int = MTIME_NS) -> None:
    path.write_bytes(payload)
    path.chmod(0o640)
    os.utime(path, ns=(mtime_ns, mtime_ns))


def verified_file(
    path: Path, payload: bytes = PAYLOAD, *, metadata: bool = True, mtime_ns: int = MTIME_NS,
) -> None:
    if path.read_bytes() != payload:
        raise RuntimeError(f"wrong published bytes: {path}")
    if metadata:
        actual: os.stat_result = path.stat()
        if actual.st_mode & 0o777 != 0o640 or actual.st_mtime_ns != mtime_ns:
            raise RuntimeError(f"wrong published metadata: {path}: {actual!r}")


def observe(
    fixture: Fixture, binary: Path, case: str, arm: str, sample: int, *, mtime_ns: int = MTIME_NS,
) -> Observation:
    work: Path = fixture.root / f"{CASES.index(case)}-{arm[0]}-{sample}"
    local: Path = work / "l"
    remote: Path = work / "r"
    local.mkdir(parents=True)
    remote.mkdir()
    remote_path: str = "remote::" + json.dumps(str(remote))
    before_calls: int = len(lines(fixture.calls))
    before_log: int = len(lines(fixture.log))
    if case == "upload":
        prepared_file(local / "source", mtime_ns=mtime_ns)
        source: str = f"source -> {remote_path}/target\n"
    elif case == "download":
        prepared_file(remote / "source", mtime_ns=mtime_ns)
        source = f"{remote_path}/source -> target\n"
    elif case == "exists":
        prepared_file(remote / "source", mtime_ns=mtime_ns)
        source = f"if exists {remote_path}/source:\n    \"present\" -> marker\n"
    elif case == "range":
        for index in range(5):
            prepared_file(remote / f"file-{index}.txt", PAYLOAD + bytes([index]), mtime_ns=mtime_ns)
        (local / "received").mkdir()
        source = f"for file in {remote_path}/<.+\\.txt>:\n    file -> received/\n"
    elif case == "five-files":
        for index in range(5):
            prepared_file(local / f"source-{index}", PAYLOAD + bytes([index]), mtime_ns=mtime_ns)
        source = "".join(f"source-{index} -> {remote_path}/target-{index}\n" for index in range(5))
    elif case == "missing-download":
        prepared_file(local / "target", b"preserve previous target", mtime_ns=mtime_ns)
        source = f"{remote_path}/missing -> target\n"
    else:
        raise ValueError(case)
    source = "remote => ssh://Perf_Fixture\n" + source
    cpu_before: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    started: int = perf_counter_ns()
    result: subprocess.CompletedProcess[bytes] = owned_run(
        [str(binary), "--no-config", "run", "-"], cwd=local,
        environment=fixture.environment(), source=source.encode(),
    )
    elapsed: float = (perf_counter_ns() - started) / 1_000_000
    cpu_after: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    if case == "missing-download":
        if result.returncode == 0 or b"missing" not in result.stderr:
            raise RuntimeError(f"missing remote source did not produce a located error: {result!r}")
    else:
        checked(result)
    if case == "upload":
        verified_file(remote / "target", mtime_ns=mtime_ns)
    elif case == "download":
        verified_file(local / "target", mtime_ns=mtime_ns)
    elif case == "exists":
        verified_file(local / "marker", b"present", metadata=False)
    elif case == "range":
        if sorted(path.name for path in (local / "received").iterdir()) != [f"file-{index}.txt" for index in range(5)]:
            raise RuntimeError("range did not return the exact source selection")
        for index in range(5):
            verified_file(local / "received" / f"file-{index}.txt", PAYLOAD + bytes([index]), mtime_ns=mtime_ns)
    elif case == "five-files":
        for index in range(5):
            verified_file(remote / f"target-{index}", PAYLOAD + bytes([index]), mtime_ns=mtime_ns)
    else:
        verified_file(local / "target", b"preserve previous target", mtime_ns=mtime_ns)
    calls: list[str] = lines(fixture.calls)[before_calls:]
    log: list[str] = lines(fixture.log)[before_log:]
    return Observation(
        case, arm, sample, elapsed,
        (cpu_after.ru_utime - cpu_before.ru_utime) * 1000,
        (cpu_after.ru_stime - cpu_before.ru_stime) * 1000,
        calls.count("ssh"), calls.count("scp"),
        sum("Accepted publickey for " in line for line in log),
        sum("Starting session:" in line for line in log),
    )


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--after", type=Path)
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--mtime-ns", type=int, default=MTIME_NS,
                        help="exact signed source mtime for metadata assertions (whole-second default)")
    parser.add_argument("--case", action="append", choices=CASES)
    args: argparse.Namespace = parser.parse_args()
    before: Path = args.before.resolve(strict=True)
    after: Path | None = args.after.resolve(strict=True) if args.after else None
    if args.samples < 1:
        parser.error("--samples must be positive")
    images: dict[str, Path] = {"before": before}
    if after is not None:
        images["after"] = after
    hashes: dict[str, str] = {arm: sha256(binary) for arm, binary in images.items()}
    harness_hash: str = sha256(Path(__file__))
    observations: list[Observation] = []
    with Fixture(ROOT / ".temp/v065") as fixture:
        for sample in range(args.samples):
            order: list[tuple[str, Path]] = list(images.items())
            if sample % 2:
                order.reverse()
            for case in args.case or CASES:
                for arm, binary in order:
                    observation: Observation = observe(fixture, binary, case, arm, sample, mtime_ns=args.mtime_ns)
                    observations.append(observation)
                    print(json.dumps(asdict(observation)), flush=True)
        if hashes != {arm: sha256(binary) for arm, binary in images.items()}:
            raise RuntimeError("an input executable changed during the measurement window")
        if harness_hash != sha256(Path(__file__)):
            raise RuntimeError("the probe source changed during the measurement window")
        result: dict[str, object] = {
            "method": "sequential real loopback OpenSSH, alternating paired AB/BA, verified bytes/mode/mtime",
            "images": {arm: {"path": str(binary), "sha256": hashes[arm]} for arm, binary in images.items()},
            "harness_sha256": harness_hash,
            "fixture_mtime_ns": args.mtime_ns,
            "ssh_version": owned_run(["/usr/bin/ssh", "-V"], cwd=ROOT).stderr.decode().strip(),
            "sshd_version": owned_run(["/usr/sbin/sshd", "-V"], cwd=ROOT).stderr.decode().strip(),
            "fixture": str(fixture.root), "client_config_sha256": sha256(fixture.config),
            "observations": [asdict(observation) for observation in observations],
            "medians_ms": {
                f"{case}:{arm}": statistics.median(observation.milliseconds for observation in observations if observation.case == case and observation.arm == arm)
                for case in args.case or CASES for arm in images
            },
        }
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
