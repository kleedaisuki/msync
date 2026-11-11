"""Exercise lexical path effects with runtime input and real artifact execution."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
DISCARDED: str = """
let path = $std::io::read_text(./input.txt);
$std::fs::filename(path);
$std::fs::has_extension(path);
$std::fs::status_known(path);
"finished" -> ./result.txt;
"""
RETAINED: str = """
let path = $std::io::read_text(./input.txt);
$std::path::native($std::path::filename(path)) -> ./result.txt;
"""
LEXICAL_NAMES: tuple[str, ...] = ("fs.filename", "fs.has_extension", "fs.status_known")


def invoke(
    binary: Path, work: Path, *arguments: str
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=work,
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )


def accepted(binary: Path, work: Path, *arguments: str) -> str:
    result: subprocess.CompletedProcess[str] = invoke(binary, work, *arguments)
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)
    return result.stdout


def journey(binary: Path, work: Path, level: int, retained: bool) -> dict[str, object]:
    work.mkdir()
    (work / "case.msync").write_text(
        RETAINED if retained else DISCARDED, encoding="utf-8", newline="\n"
    )
    accepted(binary, work, "compile", "case.msync", f"-O{level}", "-o", "case.mso")
    document: dict[str, object] = json.loads(
        accepted(binary, work, "disassemble", "case.mso", "--json")
    )
    bytecode: str = str(document["text"])
    calls: int = sum(bytecode.count(f'NativeCall "{name}"') for name in LEXICAL_NAMES)
    if level == 2 and not retained:
        assert calls == 0, bytecode
    accepted(binary, work, "bind-ir", "case.mso", "-o", "case.mexe")
    routes: list[dict[str, object]] = []
    for route in ("msync", "mso", "mexe"):
        payload: str = f"not-an-existing-directory/{route}-名字.txt"
        (work / "input.txt").write_text(payload, encoding="utf-8")
        output: Path = work / "result.txt"
        accepted(binary, work, "run", f"case.{route}", f"-O{level}")
        assert output.read_text(encoding="utf-8") == (
            f"{route}-名字.txt" if retained else "finished"
        )
        output.unlink()
        (work / "input.txt").unlink()
        failure: subprocess.CompletedProcess[str] = invoke(
            binary, work, "run", f"case.{route}", f"-O{level}"
        )
        assert failure.returncode != 0 and not output.exists(), (failure, route)
        assert "input.txt" in failure.stderr, failure.stderr
        routes.append(
            {"route": route, "bytes": "correct", "selected_io_error": "retained"}
        )
    return {
        "level": level,
        "retained": retained,
        "lexical_calls": calls,
        "routes": routes,
    }


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    scratch: Path = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="lexical-effects-", dir=scratch
    ) as temporary:
        work: Path = Path(temporary)
        results: list[dict[str, object]] = [
            journey(binary, work / f"O{level}-{retained}", level, retained)
            for level in (0, 2)
            for retained in (False, True)
        ]
    print(json.dumps(results, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
