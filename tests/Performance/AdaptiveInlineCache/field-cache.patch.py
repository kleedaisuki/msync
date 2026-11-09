"""Prepare the isolated interpreter field-cache experiment; never edit product src."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import shutil
import subprocess
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
FROZEN = "887fc6230fa26bd8ed4c9ae39ff76471d78f8b99"
PROBE = ROOT / "tests/Performance/AdaptiveInlineCache"
WORK = ROOT / ".temp/runtime-adaptive/inline-cache"

HELPER = """using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private FieldSite?[][]? fieldSites;

    private sealed class FieldSite
    {
        private ScopeLayout? first;
        private ScopeLayout? second;
        private int firstOffset;
        private int secondOffset;

        internal Value Read(ScopeValue scope, string name)
        {
            if (ReferenceEquals(scope.Layout, first)) return scope.GetField(firstOffset);
            if (ReferenceEquals(scope.Layout, second)) return scope.GetField(secondOffset);
            if (!scope.TryGetFieldOffset(name, out var offset)) return ScopeMember(scope, name, 0);
            if (first is null) { first = scope.Layout; firstOffset = offset; }
            else if (second is null) { second = scope.Layout; secondOffset = offset; }
            return scope.GetField(offset);
        }
    }

    private Value CachedMember(Value receiver, string name, int hint, int functionIndex, int pc, int length)
    {
        if (receiver is not ScopeValue scope) return Member(receiver, name, hint);
        if (hint > 0 && scope.IsField(hint - 1, name)) return scope.GetField(hint - 1);
        var functions = fieldSites ??= new FieldSite?[module.Functions.Count][];
        var sites = functions[functionIndex] ??= new FieldSite?[length];
        return (sites[pc] ??= new FieldSite()).Read(scope, name);
    }
}
"""


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise RuntimeError(f"Frozen source no longer matches experiment anchor: {path}")
    path.write_text(text.replace(old, new), encoding="utf-8")


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare() -> None:
    WORK.mkdir(parents=True, exist_ok=True)
    data = subprocess.check_output(
        [
            "git",
            "archive",
            FROZEN,
            "src",
            "stdlib",
            "vendor",
            "Directory.Build.props",
            "global.json",
        ],
        cwd=ROOT,
    )
    for variant in ("control", "candidate"):
        source = WORK / f"{variant}-source"
        if source.exists():
            shutil.rmtree(source)
        source.mkdir()
        with tarfile.open(fileobj=io.BytesIO(data)) as archive:
            archive.extractall(source, filter="data")
        destination = source / "tests/Performance/AdaptiveInlineCache"
        shutil.copytree(
            PROBE,
            destination,
            ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"),
        )
    candidate = WORK / "candidate-source"
    interpreter = candidate / "src/Msync.Runtime/Interpreter.cs"
    replace_once(
        interpreter,
        "RunBlockAsync(function, locals, stack, pc, block, cancellationToken)",
        "RunBlockAsync(functionIndex, function, locals, stack, pc, block, cancellationToken)",
    )
    replace_once(
        interpreter,
        "RunBlockAsync(FunctionCode function, Value[] locals",
        "RunBlockAsync(int functionIndex, FunctionCode function, Value[] locals",
    )
    replace_once(
        interpreter,
        "case OpCode.GetMember: stack.Add(Member(Pop(), instruction.Text!, instruction.A)); break;",
        "case OpCode.GetMember: stack.Add(CachedMember(Pop(), instruction.Text!, instruction.A, functionIndex, pc, code.Count)); break;",
    )
    refresh = candidate / "src/Msync.Runtime/Interpreter.SessionImage.cs"
    replace_once(
        refresh,
        "dispatchTables.Clear();",
        "dispatchTables.Clear();\n        fieldSites = null;",
    )
    (candidate / "src/Msync.Runtime/Interpreter.AdaptiveField.cs").write_text(
        HELPER, encoding="utf-8"
    )
    manifest: dict[str, object] = {
        "source_ref": FROZEN,
        "source_archive_sha256": hashlib.sha256(data).hexdigest(),
        "probe_files": {
            path.name: digest(path) for path in PROBE.iterdir() if path.is_file()
        },
        "candidate_files": {
            str(path.relative_to(candidate)): digest(path)
            for path in (
                interpreter,
                refresh,
                candidate / "src/Msync.Runtime/Interpreter.AdaptiveField.cs",
            )
        },
    }
    (WORK / "source.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(WORK)


def build(variant: str) -> None:
    source = WORK / f"{variant}-source"
    subprocess.run(
        [
            "dotnet",
            "publish",
            "tests/Performance/AdaptiveInlineCache/Msync.AdaptiveInlineCache.csproj",
            "-c",
            "Release",
            "-r",
            "linux-x64",
            "-m:1",
            "-v:q",
            "-p:RestoreLockedMode=false",
            f"-p:SourceRevisionId={FROZEN}",
            "-o",
            str(WORK / f"{variant}-native"),
        ],
        cwd=source,
        check=True,
    )
    binary = WORK / f"{variant}-native/msync-inline-cache-probe"
    (WORK / f"{variant}-native.json").write_text(
        json.dumps(
            {
                "source_ref": FROZEN,
                "binary": str(binary),
                "sha256": digest(binary),
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    subprocess.run([str(binary), "--check"], cwd=ROOT, check=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("prepare", "control", "candidate"))
    arguments = parser.parse_args()
    if arguments.mode == "prepare":
        prepare()
    else:
        build(arguments.mode)


if __name__ == "__main__":
    main()
