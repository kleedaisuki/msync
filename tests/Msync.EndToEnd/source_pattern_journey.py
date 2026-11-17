#!/usr/bin/env python3
"""Run pattern and generic inference cases through detached native artifacts."""

from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory

from source_sugar_journey import ROOT, invoke

FIXTURES: Path = Path(__file__).parent / "fixtures/source_patterns"
OUTPUT: dict[str, list[str]] = {
    "match": ["6", "other", "empty", "3", "-1", "content", "missing", "12", "0",
              "3", "bound", "1", "1", "missing", "present"],
    "generic": ["0", "kept", "number", "37", "mixed-number", "41", "hint"],
    "prefix": ["3", "2", "3", "true", "2", "false"],
    "coverage": ["ok", "failed", "yes", "no", "empty"],
}
REJECTIONS: dict[str, tuple[str, str]] = {
    "nan": ("fn f(x: Number) { match x { >= 0 => 1, < 0 => 0 } }; f(4);", "NaN"),
    "guard": ("fn f(x: Number) { match x { Number if x > 0 => 1 } }; f(0);",
              "Non-exhaustive match"),
    "fallthrough": ("fn f(x: Number or Null) { let Number as n = x else { 0 }; n }; f(null);",
                    "must exit"),
    "different_bindings": ("let (Number as n or String as text) = 1 else { return; };",
                           "bind the same names"),
    "old_union": ("let item: Path | String = \"hello\";", "Use 'or'"),
    "wrong_range_element": ("let xs = [1]; match xs { Range<String> => 0 };",
                            "Non-exhaustive match"),
    "nested_missing": ("let item = { box: { ok: false } }; match item { { box: { ok: true } } => 1 };",
                       "Non-exhaustive match"),
}


def check(binary: Path, work: Path) -> None:
    for name, expected in OUTPUT.items():
        unit: Path = work / f"{name}.msync"
        shutil.copyfile(FIXTURES / unit.name, unit)
        assert invoke(binary, work, ["run", unit.name, "-O0"]).splitlines() == expected
        assert invoke(binary, work, ["run", unit.name, "-O2"]).splitlines() == expected
        invoke(binary, work, ["compile", unit.name, "--emit", "hir", "-o", f"{name}.mhir"])
        invoke(binary, work, ["compile", unit.name, "-o", f"{name}.mso"])
        unit.unlink()
        invoke(binary, work, ["bind-ir", f"{name}.mso", "-o", f"{name}.mexe"])
        assert invoke(binary, work, ["run", f"{name}.mhir"]).splitlines() == expected
        assert invoke(binary, work, ["run", f"{name}.mexe"], "force").splitlines() == expected
    for name, (source, diagnostic) in REJECTIONS.items():
        bad: Path = work / f"{name}.msync"
        bad.write_text(source, encoding="utf-8")
        result: subprocess.CompletedProcess[str] = subprocess.run(
            [binary, "--no-config", "--no-telemetry", "check", bad.name],
            cwd=work, capture_output=True, text=True, timeout=30, check=False,
        )
        assert result.returncode != 0 and diagnostic in result.stderr, (name, result)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(prefix="source-pattern-", dir=ROOT / ".temp") as work:
        check(args.binary.resolve(), Path(work))
    print("Source patterns: interpreter, optimized source-free artifacts and diagnostics passed.")


if __name__ == "__main__":
    main()
