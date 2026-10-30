#!/usr/bin/env python3
"""Collect actual Cargo license texts and the matching Rust library attribution."""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
from pathlib import Path
from typing import TypedDict, cast


class Package(TypedDict):
    name: str
    version: str
    manifest_path: str
    license: str | None
    license_file: str | None
    source: str | None
    authors: list[str]


class Metadata(TypedDict):
    packages: list[Package]
    workspace_members: list[str]


PREFIXES: tuple[str, ...] = ("LICENSE", "LICENCE", "COPYING", "NOTICE", "COPYRIGHT")


def command(arguments: list[str], root: Path) -> str:
    result = subprocess.run(arguments, cwd=root, check=True, capture_output=True, text=True, encoding="utf-8")
    return result.stdout


def license_files(package: Package, workspace: Path) -> list[Path]:
    root = Path(package["manifest_path"]).parent
    candidates: set[Path] = set()
    explicit = package.get("license_file")
    if explicit:
        path = root / explicit
        if not path.is_file():
            raise RuntimeError(f"{package['name']} {package['version']}: declared license_file missing: {path}")
        candidates.add(path)
    # Workspace fixtures/build outputs are not third-party source. Registry/git
    # packages may ship attribution in subdirectories, so retain those too.
    files = root.iterdir() if root.resolve() == workspace.resolve() else root.rglob("*")
    for path in files:
        if not path.is_file():
            continue
        relative = path.relative_to(root)
        if any(part in {".git", "target", ".temp", ".cache"} for part in relative.parts):
            continue
        license_name = path.name.upper().startswith(PREFIXES)
        license_directory = any(part.upper().startswith(("LICENSE", "LICENCE")) for part in relative.parts[:-1])
        if license_name or license_directory:
            candidates.add(path)
    if not candidates:
        raise RuntimeError(
            f"{package['name']} {package['version']}: no actual license/notice text found in {root}; "
            "review the source package rather than substituting an SPDX label for its notices"
        )
    return sorted(candidates, key=lambda path: path.relative_to(root).as_posix())


def rust_attribution(sysroot: Path, destination: Path) -> list[str]:
    documentation = sysroot / "share" / "doc" / "rust"
    report = documentation / "COPYRIGHT-library.html"
    licenses = documentation / "licenses"
    if not report.is_file() or not licenses.is_dir():
        raise RuntimeError(
            f"Rust library attribution missing under {documentation}; use the official matching rustc "
            "distribution containing COPYRIGHT-library.html and licenses/, or supply --sysroot for that toolchain"
        )
    files = [report, *sorted((path for path in licenses.rglob("*") if path.is_file()), key=lambda path: path.as_posix())]
    destination.mkdir(parents=True, exist_ok=True)
    result: list[str] = []
    for source in files:
        relative = source.relative_to(documentation)
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        result.append(f"{hashlib.sha256(source.read_bytes()).hexdigest()}  rust-attribution/{relative.as_posix()}")
    return result


def generate(root: Path, target: str, output: Path, sysroot: Path | None = None) -> int:
    metadata = cast(Metadata, json.loads(command(
        ["cargo", "metadata", "--locked", "--format-version", "1", "--filter-platform", target], root,
    )))
    packages = sorted(metadata["packages"], key=lambda package: (package["name"], package["version"], package["source"] or ""))
    sections: list[str] = [
        "msync distribution: third-party license texts and notices\n",
        f"Cargo target filter: {target}\n",
        "This source-package inventory includes development/transitive and possibly other-target packages.\n"
        "It is a conservative notice superset, NOT an exact linked-binary SBOM or a license-compatibility judgment.\n"
        "Texts below are reproduced from resolved source packages. No credentials or local absolute paths are recorded.\n",
    ]
    for package in packages:
        directory = Path(package["manifest_path"]).parent
        supplement: tuple[Path, str] | None = None
        try:
            files = license_files(package, root)
        except RuntimeError:
            sources_path = root / "packaging" / "license-sources.json"
            sources = cast(dict[str, dict[str, str]], json.loads(sources_path.read_text(encoding="utf-8")))
            selected = sources.get(f"{package['name']}@{package['version']}")
            if selected is None:
                raise
            path = root / selected["path"]
            if hashlib.sha256(path.read_bytes()).hexdigest() != selected["sha256"]:
                raise RuntimeError(f"Pinned license supplement checksum mismatch: {path}")
            supplement = (path, selected["source"])
            files = []
        sections.extend([
            f"\n{'=' * 72}\n{package['name']} {package['version']}\n",
            f"Declared license: {package['license'] or 'not specified; see supplied texts'}\n",
            f"Authors: {', '.join(package.get('authors', [])) or 'see supplied notices'}\n",
            f"Source: {package['source'] or 'msync workspace source (matching release)'}\n",
        ])
        for path in files:
            raw = path.read_bytes()
            sections.append(f"\n--- {path.relative_to(directory).as_posix()} (SHA256 {hashlib.sha256(raw).hexdigest()}) ---\n")
            sections.append(raw.decode("utf-8", errors="strict"))
            sections.append("\n")
        if supplement is not None:
            path, source = supplement
            raw = path.read_bytes()
            sections.append(f"\n--- Exact upstream license supplement: {source} (SHA256 {hashlib.sha256(raw).hexdigest()}) ---\n")
            sections.append(raw.decode("utf-8", errors="strict"))
            sections.append("\n")
    compiler = command(["rustc", "-vV"], root).strip()
    actual_sysroot = sysroot or Path(command(["rustc", "--print", "sysroot"], root).strip())
    # Resolve every crate before writing output so a missing license cannot leave
    # a report that looks complete. The caller uses an exclusive staging folder.
    output.parent.mkdir(parents=True, exist_ok=True)
    copies = rust_attribution(actual_sysroot, output.parent / "rust-attribution")
    sections.extend([
        f"\n{'=' * 72}\nRust standard-library distribution attribution\n{compiler}\n",
        "The official matching toolchain's COPYRIGHT-library.html and referenced licenses are preserved\n"
        "verbatim in the sibling rust-attribution/ directory. Open the HTML report to inspect library\n"
        "component copyright/license attribution. This report does not substitute compiler-version guesses.\n",
        "\n".join(copies), "\n",
    ])
    output.write_text("".join(sections), encoding="utf-8", newline="\n")
    return len(packages)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--sysroot", type=Path, help="matching official Rust toolchain sysroot, if not rustc's default")
    options = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        count = generate(root, options.target, options.output.resolve(), options.sysroot)
    except (OSError, RuntimeError, UnicodeError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"License collection failed: {error}\n")
    print(f"Collected actual notices for {count} Cargo source packages plus Rust library attribution: {options.output}")


if __name__ == "__main__":
    main()
