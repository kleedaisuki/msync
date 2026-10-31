#!/usr/bin/env python3
"""One matched real-CLI compiler control and candidate-only typed capability cost."""
from __future__ import annotations

import csv
import os
from pathlib import Path
import sys
import tempfile
from typing import TextIO

from config_startup import digest, run


def measure(before: Path, after: Path, output: Path, samples: int) -> None:
    repository: Path = Path(__file__).resolve().parents[1]
    temporary: Path = repository / ".temp" / "v065" / "compiler"
    temporary.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise FileExistsError(f"refusing to overwrite measurement: {output}")
    images: dict[str, Path] = {"before": before, "after": after}
    initial: dict[str, str] = {arm: digest(binary) for arm, binary in images.items()}
    with tempfile.TemporaryDirectory(prefix="cli-", dir=temporary) as owned:
        root: Path = Path(owned)
        (root / "symbols.msync").write_text(
            "symbol child(base):\n    return base / leaf\n"
            + "".join(f"child(root) -> output{index}\n" for index in range(100)),
            encoding="utf-8",
        )
        (root / "typed.msync").write_text(
            "symbol config_child(file):\n    return ~/.config/file\n"
            "boolean enabled(flag):\n    return not flag\n"
            'symbol label(value):\n    return "ok"\n'
            + "".join(
                f"path{index} := $wsl::config_child(config.toml)\n"
                f'range{index} := [path{index}, "payload", enabled(false)]\n'
                f"projected{index} := range{index} | $view::transform(label)\n"
                for index in range(1000)
            ),
            encoding="utf-8",
        )
        environment: dict[str, str] = dict(os.environ)
        environment.update({
            "NO_COLOR": "1", "HOME": str(root / "home"), "USERPROFILE": str(root / "home"),
            "XDG_CONFIG_HOME": str(root / "config"), "APPDATA": str(root / "config"),
        })
        for name in ("MSYNC_TRACE", "MSYNC_STATE_DIR", "MSYNC_CONFIG"):
            environment.pop(name, None)
        stream: TextIO
        with output.open("x", encoding="utf-8", newline="") as stream:
            writer = csv.writer(stream, lineterminator="\n")
            writer.writerow(["case", "entries", "pair", "arm", "milliseconds", "image_sha256"])
            for case, arms in (("symbols", ("before", "after")), ("typed", ("after",))):
                entries: int = 100 if case == "symbols" else 1000
                arguments: list[str] = ["check", f"{case}.msync", "--no-config"]
                for arm in arms:
                    run(images[arm], root, environment, arguments)
                for pair in range(samples):
                    order: tuple[str, ...] = arms if pair % 2 == 0 else tuple(reversed(arms))
                    for arm in order:
                        elapsed: float = run(images[arm], root, environment, arguments)
                        writer.writerow([case, entries, pair, arm, f"{elapsed:.6f}", initial[arm]])
                        if {path.name for path in root.iterdir()} != {"symbols.msync", "typed.msync"}:
                            raise RuntimeError("a compiler check created unexpected filesystem effects")
    for arm, binary in images.items():
        if digest(binary) != initial[arm]:
            raise RuntimeError(f"measured {arm} image changed")
    print(f"Saved real compiler CLI rows to {output}")


def main() -> None:
    if len(sys.argv) not in (4, 5):
        raise SystemExit("usage: compiler_cli_v065.py BEFORE AFTER OUTPUT_CSV [SAMPLES=7]")
    before: Path = Path(sys.argv[1]).resolve(strict=True)
    after: Path = Path(sys.argv[2]).resolve(strict=True)
    output: Path = Path(sys.argv[3]).resolve()
    samples: int = int(sys.argv[4]) if len(sys.argv) == 5 else 7
    if samples < 3:
        raise SystemExit("at least three samples are required")
    measure(before, after, output, samples)


if __name__ == "__main__":
    main()
