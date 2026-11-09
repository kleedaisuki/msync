#!/usr/bin/env python3
"""Exercise width-aware formatting through the actual CLI, preserving effects."""

from __future__ import annotations

import argparse
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def invoke(binary: Path, arguments: Sequence[str], cwd: Path) -> str:
    result = subprocess.run(
        [str(binary), "--no-config", *arguments],
        cwd=cwd,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    assert result.returncode == 0, (arguments, result.stdout, result.stderr)
    return result.stdout


def formatted(binary: Path, directory: Path, name: str, source: str) -> str:
    path = directory / f"{name}.msync"
    path.write_bytes(source.encode("utf-8"))
    invoke(binary, ["check", str(path)], directory)
    invoke(binary, ["fmt", str(path)], directory)
    first = path.read_bytes()
    invoke(binary, ["fmt", str(path), "--check"], directory)
    invoke(binary, ["fmt", str(path)], directory)
    assert first == path.read_bytes(), f"Formatting {name} is not idempotent"
    invoke(binary, ["check", str(path)], directory)
    return first.decode("utf-8")


def run(binary: Path, directory: Path) -> None:
    screenshot = (
        "scope MyClass { new: fn() { scope { x: ~/.codex } }, "
        "get: fn(some) { some::x } }; let my_instance = MyClass::new(); "
        "let c = $std::bool::both(MyClass::get(my_instance) == ~/.codex, "
        "MyClass::get(my_instance) != ./);\n"
    )
    result = formatted(binary, directory, "screenshot", screenshot)
    assert "$std::bool::both(\n" in result, result
    assert max(map(len, result.splitlines())) <= 80, result

    for depth in (0, 3, 8, 12):
        nested = "scope { child: " * depth
        source = (
            "let value = "
            + nested
            + "["
            + ", ".join(str(n) for n in range(24))
            + "]"
            + " }" * depth
            + ";\n"
        )
        result = formatted(binary, directory, f"nested-{depth}", source)
        assert max(map(len, result.splitlines())) <= 80, result

    comparisons = " and ".join("true == true" for _ in range(18))
    result = formatted(binary, directory, "operators", f"let result = {comparisons};")
    assert max(map(len, result.splitlines())) <= 80, result

    payload = "  first\n second " + "可莉" * 60 + "\r\n  last  "
    literal = '"' + payload.replace('"', '""') + '"'
    source = (
        "# Keep this explanation with the source.\n"
        f"{literal} -> formatted payload.txt; // Keep the trailing comment.\n"
        "let matched = match ./abc { <[a-z]{3}> => { true }, _ => { false } };\n"
        "let endpoint = ssh://user@[2001:db8::1]:2222/work/tree;\n"
    )
    result = formatted(binary, directory, "literals", source)
    assert literal in result, "A literal's bytes changed"
    assert (
        "<[a-z]{3}>" in result and "ssh://user@[2001:db8::1]:2222/work/tree" in result
    )
    assert (
        "# Keep this explanation" in result
        and "// Keep the trailing comment." in result
    )
    invoke(binary, ["run", str(directory / "literals.msync")], directory)
    assert (directory / "formatted payload.txt").read_bytes() == payload.encode("utf-8")

    long_name = "x" * 110
    result = formatted(binary, directory, "indivisible", f"let {long_name} = true;")
    assert long_name in result, "Long names must remain intact"
    deep = "let value = " + "scope { child: " * 22 + "true" + " }" * 22 + ";"
    result = formatted(binary, directory, "deep", deep)
    assert max(map(len, result.splitlines())) > 80, (
        "Deep structural indentation must not be flattened"
    )
    print(
        "PASS: 80-column calls/ranges/operators, deep indentation, literal/comment preservation, check and execution"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    arguments = parser.parse_args()
    temporary = ROOT / ".temp"
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="formatter-journey-", dir=temporary
    ) as name:
        run(arguments.binary.resolve(), Path(name))


if __name__ == "__main__":
    main()
