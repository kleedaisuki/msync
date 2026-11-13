"""Real child-process behaviors for the execution framework acceptance journey."""

from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

PAYLOAD: bytes = b"raw\x00\xff\n" + bytes(range(256)) * 513


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser()
    parser.add_argument(
        "mode", choices=("stream", "wait", "tree", "detached", "guest-error")
    )
    parser.add_argument("--binary", type=Path)
    args: argparse.Namespace = parser.parse_args()
    if args.mode == "stream":
        Path("report.json").write_text(
            json.dumps({"bytes": len(PAYLOAD), "oracle": "exact raw output"}),
            encoding="utf-8",
        )
        sys.stdout.buffer.write(PAYLOAD)
        sys.stderr.buffer.write(b"concrete stderr\x00\xff\n")
        time.sleep(0.2)
    elif args.mode == "wait":
        time.sleep(0.2)
    elif args.mode == "tree":
        subprocess.Popen(
            [sys.executable, str(Path(__file__).resolve()), "detached"],
            start_new_session=True,
        )
        while not Path("descendant.pid").exists():
            time.sleep(0.01)
        print("detached descendant is ready", flush=True)
        time.sleep(120)
    elif args.mode == "detached":
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        Path("descendant.pid").write_text(str(os.getpid()), encoding="ascii")
        time.sleep(120)
    else:
        Path("invalid.msync").write_text("let =;", encoding="utf-8")
        os.execv(
            str(args.binary),
            [
                str(args.binary),
                "--no-config",
                "--no-telemetry",
                "check",
                "invalid.msync",
            ],
        )


if __name__ == "__main__":
    main()
