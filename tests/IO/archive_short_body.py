#!/usr/bin/env python3
"""Inject a short native archive member and verify publication does not accept it."""

from __future__ import annotations

import argparse
import os
import subprocess
import tarfile
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def header(name: str, size: int = 0, directory: bool = False) -> bytes:
    entry = tarfile.TarInfo(name)
    entry.size = size
    entry.mode = 0o755 if directory else 0o640
    entry.type = tarfile.DIRTYPE if directory else tarfile.REGTYPE
    entry.mtime = 1_700_000_000
    return entry.tobuf(format=tarfile.PAX_FORMAT)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    args = parser.parse_args()
    if os.name != "posix":
        raise SystemExit(
            "This protocol fault fixture uses a POSIX native client launcher"
        )
    binary: Path = args.binary.resolve()
    (ROOT / ".temp").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="archive-short-", dir=ROOT / ".temp"
    ) as temporary:
        root = Path(temporary)
        target = root / "output" / "source"
        target.mkdir(parents=True)
        (target / "broken").write_bytes(b"previous target")
        complete = b"completed member"
        payload = (
            header("source", directory=True)
            + header("source/complete", len(complete))
            + complete
            + b"\0" * (-len(complete) % 512)
            + header("source/broken", 100)
            + b"bad"
        )
        archive = root / "truncated.tar"
        archive.write_bytes(payload)
        launchers = root / "bin"
        launchers.mkdir()
        launcher = launchers / "ssh"
        launcher.write_text(
            "#!/usr/bin/env python3\nimport os,sys\nfrom pathlib import Path\nsys.stdout.buffer.write(Path(os.environ['MSYNC_ARCHIVE_FIXTURE']).read_bytes())\n",
            encoding="utf-8",
        )
        launcher.chmod(0o755)
        (root / "copy.msync").write_text(
            'let remote = ssh://fixture; remote::"/source" -> ./output/;\n',
            encoding="utf-8",
        )
        environment = dict(os.environ)
        environment["PATH"] = str(launchers) + os.pathsep + environment.get("PATH", "")
        environment["MSYNC_ARCHIVE_FIXTURE"] = str(archive)
        result = subprocess.run(
            [str(binary), "--no-config", "run", "copy.msync"],
            cwd=root,
            env=environment,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        if (
            result.returncode != 1
            or "declared 100 bytes, received 3" not in result.stderr
            or "broken" not in result.stderr
        ):
            raise AssertionError(
                f"Short archive did not retain its protocol cause: {result.returncode}\n{result.stderr}"
            )
        if (target / "broken").read_bytes() != b"previous target":
            raise AssertionError("Short archive replaced the previous ordinary target")
        if (target / "complete").read_bytes() != complete:
            raise AssertionError("Transfer rolled back a completed independent member")
        if list(target.glob(".msync-*")):
            raise AssertionError("Short archive retained its live owned file stage")
        print(
            "Short archive rejected before publication; old target preserved, completed member retained, stage retired."
        )


if __name__ == "__main__":
    main()
