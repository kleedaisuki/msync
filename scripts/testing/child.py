"""Enter an owned cgroup and exec; launch failures are not test failures."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path


def main() -> None:
    group: Path = Path(sys.argv[1])
    error_path: Path = Path(sys.argv[2])
    command: list[str] = sys.argv[3:]
    try:
        (group / "cgroup.procs").write_text(str(os.getpid()), encoding="ascii")
        os.execvpe(command[0], command, os.environ)
    except (OSError, ValueError) as error:
        error_path.write_text(
            json.dumps(
                {
                    "type": type(error).__name__,
                    "message": str(error),
                    "command": command,
                }
            ),
            encoding="utf-8",
        )
        raise SystemExit(127) from error


if __name__ == "__main__":
    main()
