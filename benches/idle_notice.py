#!/usr/bin/env python3
"""Independent exact-image EOF-to-idle-notice observations; no cross-method ratio."""
from __future__ import annotations

import csv
import hashlib
import os
from pathlib import Path
from queue import Empty, Queue
import socket
import subprocess
import sys
import tempfile
from threading import Thread
from time import monotonic, perf_counter_ns
from typing import BinaryIO, TextIO

DEADLINE: float = 15.0
PAYLOAD: bytes = b"notice payload"


def output_lines(source: BinaryIO, lines: Queue[str]) -> None:
    for line in source:
        lines.put(line.decode("utf-8", errors="strict").rstrip("\r\n"))


def error_bytes(source: BinaryIO, collected: list[bytes]) -> None:
    collected.append(source.read())


def until(lines: Queue[str], needle: str) -> None:
    deadline: float = monotonic() + DEADLINE
    observed: list[str] = []
    while True:
        try:
            line: str = lines.get(timeout=max(0.0, deadline - monotonic()))
        except Empty as cause:
            raise RuntimeError(f"waiting for {needle}: {observed!r}") from cause
        observed.append(line)
        if needle in line:
            return


def observe(binary: Path, root: Path) -> float:
    root.mkdir()
    environment: dict[str, str] = dict(os.environ)
    environment.update({
        "NO_COLOR": "1", "HOME": str(root / "home"), "USERPROFILE": str(root / "home"),
        "XDG_CONFIG_HOME": str(root / "config"), "APPDATA": str(root / "config"),
    })
    for name in ("MSYNC_TRACE", "MSYNC_CONFIG", "MSYNC_STATE_DIR"):
        environment.pop(name, None)
    listener: socket.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    listener.settimeout(DEADLINE)
    child: subprocess.Popen[bytes] = subprocess.Popen(
        [str(binary), "repl", "--color", "never"], cwd=root, env=environment,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    assert child.stdin is not None and child.stdout is not None and child.stderr is not None
    lines: Queue[str] = Queue()
    errors: list[bytes] = []
    reader: Thread = Thread(target=output_lines, args=(child.stdout, lines))
    error_reader: Thread = Thread(target=error_bytes, args=(child.stderr, errors))
    reader.start()
    error_reader.start()
    peer: socket.socket | None = None
    try:
        address: tuple[str, int] = listener.getsockname()
        child.stdin.write(f"wire => tcp://{address[0]}:{address[1]}\nwire::/ -> output.bin\n".encode())
        child.stdin.flush()
        until(lines, "job 1: running in background")
        peer, _ = listener.accept()
        peer.sendall(PAYLOAD)
        output: Path = root / "output.bin"
        if output.exists():
            raise RuntimeError("a stream published its destination before EOF")
        while not lines.empty():
            if "job 1: completed" in lines.get_nowait():
                raise RuntimeError("a stream reported completion before EOF")
        started: int = perf_counter_ns()
        peer.shutdown(socket.SHUT_WR)
        # No stdin write occurs between the peer EOF and this completion observation.
        until(lines, "job 1: completed")
        elapsed_ms: float = (perf_counter_ns() - started) / 1_000_000
        if output.read_bytes() != PAYLOAD:
            raise RuntimeError("the idle completion notice preceded correct publication")
        if child.poll() is not None:
            raise RuntimeError("the REPL exited rather than remaining available at its prompt")
        child.stdin.write(b"/jobs\n/quit\n")
        child.stdin.flush()
        child.stdin.close()
        until(lines, "no active jobs")
        if child.wait(timeout=DEADLINE):
            raise RuntimeError("the observed REPL did not exit successfully")
        reader.join(timeout=DEADLINE)
        error_reader.join(timeout=DEADLINE)
        if reader.is_alive() or error_reader.is_alive() or any(errors):
            raise RuntimeError(f"REPL observation threads/diagnostics did not settle: {errors!r}")
        return elapsed_ms
    finally:
        listener.close()
        if peer is not None:
            peer.close()
        if child.poll() is None:
            child.kill()
            child.wait(timeout=DEADLINE)
        reader.join(timeout=DEADLINE)
        error_reader.join(timeout=DEADLINE)
        child.stdout.close()
        child.stderr.close()


def main() -> None:
    if len(sys.argv) not in (3, 4):
        raise SystemExit("usage: idle_notice.py EXECUTABLE OUTPUT_CSV [SAMPLES=9]")
    binary: Path = Path(sys.argv[1]).resolve(strict=True)
    output: Path = Path(sys.argv[2]).resolve()
    count: int = int(sys.argv[3]) if len(sys.argv) == 4 else 9
    if count < 5:
        raise SystemExit("at least five observations are required")
    digest: str = hashlib.sha256(binary.read_bytes()).hexdigest()
    temporary: Path = Path(__file__).resolve().parents[1] / ".temp"
    temporary.mkdir(exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    samples: list[float] = []
    with tempfile.TemporaryDirectory(prefix="idle-notice-", dir=temporary) as owned:
        for index in range(count):
            elapsed: float = observe(binary, Path(owned) / f"sample-{index}")
            samples.append(elapsed)
    stream: TextIO
    with output.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["sample", "milliseconds"])
        for index, elapsed in enumerate(samples):
            writer.writerow([index, f"{elapsed:.6f}"])
    if hashlib.sha256(binary.read_bytes()).hexdigest() != digest:
        raise RuntimeError("the observed executable changed")
    ordered: list[float] = sorted(samples)
    print(f"summary,eof-to-idle-notice,python-independent,{count},{ordered[(count-1)//2]:.6f},{ordered[(count-1)*95//100]:.6f}")
    print(f"image_sha256,{digest}")
    print("validation,no-input-after-EOF=true,no-destination-before-EOF=true,exact-bytes=true,repl-alive=true,clean-exit=true")


if __name__ == "__main__":
    main()
