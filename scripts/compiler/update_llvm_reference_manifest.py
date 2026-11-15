#!/usr/bin/env python3
"""Record the files present in the pinned LLVM reference snapshot."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
REFERENCE: Path = ROOT / 'vendor/llvm-reference'


def main() -> None:
    path: Path = REFERENCE / 'SOURCE.json'
    manifest: dict[str, Any] = json.loads(path.read_text())
    files: list[str] = sorted(
        str(source.relative_to(REFERENCE))
        for source in (REFERENCE / 'llvm').rglob('*') if source.is_file()
    )
    manifest['files'] = files
    manifest['sha256'] = {
        name: hashlib.sha256((REFERENCE / name).read_bytes()).hexdigest()
        for name in files
    }
    path.write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    main()
