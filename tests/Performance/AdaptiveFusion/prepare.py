#!/usr/bin/env python3
"""Freeze one product revision and overlay only the experimental fusion handler."""

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
PROBE = Path(__file__).resolve().parent


def replace_once(text: str, before: str, after: str) -> str:
    if text.count(before) != 1:
        raise ValueError(f"Expected one frozen-source anchor: {before!r}")
    return text.replace(before, after)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(destination: Path, revision: str) -> None:
    archive = subprocess.check_output(
        [
            "git",
            "archive",
            revision,
            "src",
            "stdlib",
            "Directory.Build.props",
            "global.json",
        ],
        cwd=ROOT,
    )
    commit = subprocess.check_output(
        ["git", "rev-parse", revision], cwd=ROOT, text=True
    ).strip()
    destination.mkdir(parents=True, exist_ok=True)
    for name in ("control", "candidate"):
        folder = destination / name
        folder.mkdir(parents=True, exist_ok=True)
        with tarfile.open(fileobj=io.BytesIO(archive)) as source:
            source.extractall(folder, filter="data")
        target = folder / "tests/Performance/AdaptiveFusion"
        target.mkdir(parents=True, exist_ok=True)
        for path in PROBE.iterdir():
            if path.is_file():
                shutil.copy2(path, target / path.name)
    candidate = destination / "candidate/src/Msync.Runtime"
    path = candidate / "Interpreter.cs"
    text = path.read_text(encoding="utf-8")
    text = replace_once(
        text,
        "        (this.module, blocks) = ProjectImage(module);",
        "        (this.module, blocks) = ProjectImage(module);\n        PrepareFusion();",
    )
    text = replace_once(
        text,
        "        RefreshSessionImage();\n        var function",
        "        RefreshSessionImage();\n        PrepareFusion();\n        var function",
    )
    text = replace_once(
        text,
        "        var code = function.Instructions;\n        var pc = startInstruction;",
        "        var code = function.Instructions;\n        var fusion = fusionSites?.GetValueOrDefault(function);\n        var pc = startInstruction;",
    )
    text = replace_once(
        text,
        "            try\n            {\n                switch (instruction.Op)",
        """            try
            {
                if (fusion is not null && fusion[pc])
                {
                    var left = ReadLocal(locals, instruction.A);
                    cancellationToken.ThrowIfCancellationRequested();
                    var right = ReadLocal(locals, code[pc + 1].A);
                    cancellationToken.ThrowIfCancellationRequested();
                    instruction = code[pc + 2];
                    stack.Add(FusedAdd(left, right));
                    pc += 2;
                    continue;
                }
                switch (instruction.Op)""",
    )
    path.write_text(text, encoding="utf-8")
    helper = candidate / "Interpreter.Fusion.cs"
    shutil.copy2(PROBE / "Interpreter.Fusion.cs.template", helper)
    manifest: dict[str, object] = {
        "source_revision": commit,
        "source_archive_sha256": hashlib.sha256(archive).hexdigest(),
        "probe_sha256": digest(PROBE / "Program.cs"),
        "candidate_interpreter_sha256": digest(path),
        "candidate_helper_sha256": digest(helper),
        "modified_product_files": ["Interpreter.cs", "Interpreter.Fusion.cs"],
    }
    (destination / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--revision", default="887fc6230fa26bd8ed4c9ae39ff76471d78f8b99"
    )
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".temp/runtime-adaptive/fusion"
    )
    options = parser.parse_args()
    prepare(options.output.resolve(), options.revision)


if __name__ == "__main__":
    main()
