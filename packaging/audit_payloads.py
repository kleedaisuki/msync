#!/usr/bin/env python3
"""Audit native manifests, corresponding standard sources and final VSIX bytes."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import tarfile
import zipfile
from pathlib import Path, PurePosixPath

from build import version as package_version

ROOT = Path(__file__).resolve().parents[1]


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def source_files(revision: str) -> dict[str, bytes]:
    names = subprocess.check_output(
        [
            "git",
            "ls-tree",
            "-r",
            "--name-only",
            revision,
            "--",
            "stdlib",
            "vendor/PrettyPrompt",
        ],
        cwd=ROOT,
        text=True,
    ).splitlines()
    return {
        name: subprocess.check_output(["git", "show", f"{revision}:{name}"], cwd=ROOT)
        for name in names
        if name.startswith("vendor/PrettyPrompt/") or name.endswith(".msync")
    }


def archive_files(archive: Path) -> dict[str, bytes]:
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as zipped:
            entries = {
                entry.filename: zipped.read(entry)
                for entry in zipped.infolist()
                if not entry.is_dir()
            }
    else:
        entries = {}
        with tarfile.open(archive) as tar:
            for member in tar.getmembers():
                if not member.isfile():
                    continue
                stream = tar.extractfile(member)
                assert stream is not None, member.name
                entries[member.name] = stream.read()
    manifests = [name for name in entries if name.endswith("/manifest.sha256")]
    assert len(manifests) == 1, manifests
    prefix = manifests[0].removesuffix("manifest.sha256")
    assert all(name.startswith(prefix) for name in entries), archive
    return {name.removeprefix(prefix): data for name, data in entries.items()}


def audit_native(
    archive: Path, source: dict[str, bytes], windows: bool
) -> dict[str, object]:
    files = archive_files(archive)
    manifest = {
        name: checksum
        for checksum, name in (
            line.split("  ", 1)
            for line in files["manifest.sha256"].decode().splitlines()
        )
    }
    assert set(manifest) == set(files) - {"manifest.sha256"}, archive
    for name, checksum in manifest.items():
        assert digest(files[name]) == checksum, (archive.name, name)
    counts = {"stdlib_sources": 0, "modified_prompt_sources": 0, "crlf_variants": 0}
    for name, expected in source.items():
        if name.startswith("stdlib/"):
            target = "share/msync/std/" + PurePosixPath(name).name
            counts["stdlib_sources"] += 1
        else:
            target = (
                "share/doc/msync/native-dependencies/prettyprompt-6.0.5/source/"
                + name.removeprefix("vendor/PrettyPrompt/")
            )
            counts["modified_prompt_sources"] += 1
        actual = files[target]
        if actual == expected:
            continue
        assert not name.startswith("stdlib/"), (
            archive.name,
            target,
            "Standard source literals must retain the authored bytes",
        )
        assert windows and actual == expected.replace(b"\n", b"\r\n"), (
            archive.name,
            target,
        )
        counts["crlf_variants"] += 1
    binary = "bin/msync.exe" if windows else "bin/msync"
    return {
        "manifest_entries": len(manifest),
        "binary_sha256": digest(files[binary]),
        **counts,
    }


def audit_vsix(archive: Path, version: str) -> list[str]:
    sources = {
        "LICENSE.txt": "LICENSE",
        "readme.md": "README.md",
        "language-configuration.json": "language-configuration.json",
        "package.json": "package.json",
        "snippets/msync.code-snippets": "snippets/msync.code-snippets",
        "syntaxes/msync.tmLanguage.json": "syntaxes/msync.tmLanguage.json",
        "syntaxes/msync-ir.tmLanguage.json": "syntaxes/msync-ir.tmLanguage.json",
        "out/THIRD_PARTY_NOTICES.txt": "out/THIRD_PARTY_NOTICES.txt",
        "out/extension.cjs": "out/extension.cjs",
    }
    with zipfile.ZipFile(archive) as zipped:
        files = {
            name
            for name in zipped.namelist()
            if name.startswith("extension/") and not name.endswith("/")
        }
        assert files == {"extension/" + name for name in sources}, sorted(files)
        for name, source in sources.items():
            assert (
                zipped.read("extension/" + name)
                == (ROOT / "editors/vscode" / source).read_bytes()
            ), name
        metadata: dict[str, object] = json.loads(zipped.read("extension/package.json"))
        assert metadata["version"] == version, metadata["version"]
    return sorted(files)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-source", required=True)
    parser.add_argument("--version", default=package_version())
    parser.add_argument("--dist", type=Path, default=ROOT / "dist")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    directory: Path = args.dist.resolve()
    names = (
        f"msync-{args.version}-linux-x86_64.tar.gz",
        f"msync-{args.version}-windows-x86_64.zip",
        f"msync-{args.version}-windows-x86_64-setup.exe",
        f"msync-language-{args.version}.vsix",
    )
    checksums = {
        name: checksum
        for checksum, name in (
            line.split("  ", 1)
            for line in (directory / "SHA256SUMS").read_text().splitlines()
        )
    }
    assert set(checksums) == set(names), checksums
    assets = []
    for name in names:
        payload = (directory / name).read_bytes()
        assert digest(payload) == checksums[name], name
        assets.append({"name": name, "size": len(payload), "sha256": checksums[name]})
    source = source_files(args.native_source)
    report: dict[str, object] = {
        "native_source": args.native_source,
        "version": args.version,
        "linux": audit_native(directory / names[0], source, False),
        "windows": audit_native(directory / names[1], source, True),
        "vsix_members": audit_vsix(directory / names[3], args.version),
        "assets": assets,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
