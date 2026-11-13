"""Focused release-container tests; executable behavior is in smoke.py."""

from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from typing import TypedDict, cast
from unittest.mock import patch

import build
from language_upgrade import product_assembly_files


class LockedPackage(TypedDict):
    type: str


class LockedGraph(TypedDict):
    dependencies: dict[str, dict[str, LockedPackage]]


def direct_packages(path: Path) -> frozenset[str]:
    graph: LockedGraph = cast(LockedGraph, json.loads(path.read_text(encoding="utf-8")))
    return frozenset(
        name
        for target in graph["dependencies"].values()
        for name, package in target.items()
        if package["type"] == "Direct"
    )


class DependencyLockTests(unittest.TestCase):
    def test_global_aot_probes_cannot_replace_production_graphs(self) -> None:
        compiler: str = "Microsoft.DotNet.ILCompiler"
        linker: str = "Microsoft.NET.ILLink.Tasks"
        projects: tuple[str, ...] = (
            "src/Msync.Cli",
            "src/Msync.Core",
            "src/Msync.Hir",
            "src/Msync.Compiler",
            "src/Msync.Runtime",
            "src/Msync.Optimization",
            "vendor/PrettyPrompt",
        )
        for rid in build.RID_NAMES:
            for project in projects:
                directory: Path = build.ROOT / project
                with self.subTest(rid=rid, project=project):
                    production: frozenset[str] = direct_packages(
                        directory / f"packages.{rid}.lock.json"
                    )
                    probe: frozenset[str] = direct_packages(
                        directory / f"packages.aot.{rid}.lock.json"
                    )
                    self.assertEqual(compiler in production, project == "src/Msync.Cli")
                    self.assertTrue({compiler, linker}.issubset(probe))
                    if project in {"src/Msync.Core", "src/Msync.Runtime"}:
                        self.assertNotIn(linker, production)


class PackageTests(unittest.TestCase):
    def test_host_rids_keep_arm64_linux_local(self) -> None:
        for system, machine, expected in (
            ("Linux", "x86_64", "linux-x64"),
            ("Linux", "aarch64", "linux-arm64"),
            ("Linux", "ARM64", "linux-arm64"),
            ("Windows", "AMD64", "win-x64"),
            ("Windows", "ARM64", None),
        ):
            with (
                self.subTest(system=system, machine=machine),
                patch("build.platform.system", return_value=system),
                patch("build.platform.machine", return_value=machine),
            ):
                if expected is None:
                    with self.assertRaises(ValueError):
                        build.host_rid()
                else:
                    self.assertEqual(build.host_rid(), expected)

    def test_component_provenance_uses_actual_project_assemblies(self) -> None:
        temporary: Path = build.ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            root: Path = Path(name)
            for relative, properties in (
                (
                    "src/Msync.Cli/Msync.Cli.csproj",
                    "<AssemblyName>msync</AssemblyName>",
                ),
                ("src/Msync.Optimization/Msync.Optimization.csproj", ""),
                (
                    "src/nested/Other.csproj",
                    "<AssemblyName>Other.Runtime</AssemblyName>",
                ),
            ):
                project: Path = root / relative
                project.parent.mkdir(parents=True, exist_ok=True)
                project.write_text(
                    f"<Project><PropertyGroup>{properties}</PropertyGroup></Project>",
                    encoding="utf-8",
                )
            self.assertEqual(
                product_assembly_files(root),
                frozenset({"msync.dll", "msync.optimization.dll", "other.runtime.dll"}),
            )

    def test_version_requires_editor_agreement(self) -> None:
        temporary = build.ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            root = Path(name)
            (root / "Directory.Build.props").write_text(
                "<Project><PropertyGroup><Version>0.8.0</Version></PropertyGroup></Project>",
                encoding="utf-8",
            )
            (root / "editors/vscode").mkdir(parents=True)
            manifest = root / "editors/vscode/package.json"
            manifest.write_text(json.dumps({"version": "0.8.0"}), encoding="utf-8")
            self.assertEqual(build.version(root), "0.8.0")
            manifest.write_text(json.dumps({"version": "0.7.3"}), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "does not match"):
                build.version(root)

    def test_native_payload_keeps_runtime_libraries_not_debug_symbols(self) -> None:
        temporary = build.ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            root = Path(name)
            publish = root / "publish"
            (publish / "nested").mkdir(parents=True)
            for relative in (
                "libe_sqlite3.so",
                "e_sqlite3.dll",
                "libdependency.so.1",
                "nested/library.dylib",
                "msync.pdb",
                "msync.dbg",
                "msync",
            ):
                (publish / relative).write_bytes(relative.encode())
            destination = root / "bin"
            build.copy_native_libraries(publish, destination)
            actual = {
                path.relative_to(destination).as_posix()
                for path in destination.rglob("*")
                if path.is_file()
            }
            self.assertEqual(
                actual,
                {
                    "libe_sqlite3.so",
                    "e_sqlite3.dll",
                    "libdependency.so.1",
                    "nested/library.dylib",
                },
            )

    def test_archives_are_repeatable_and_keep_payload_paths(self) -> None:
        temporary = build.ROOT / ".temp"
        temporary.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as name:
            root = Path(name)
            output = root / "dist"
            output.mkdir()
            for rid, name in build.RID_NAMES.items():
                stage = root / f"msync-0.8.0-{name}"
                (stage / "bin").mkdir(parents=True)
                (stage / "bin/msync").write_bytes(b"native payload")
                first = build.archive_payload(
                    stage, output, rid, 1700000000
                ).read_bytes()
                second = build.archive_payload(
                    stage, output, rid, 1700000000
                ).read_bytes()
                self.assertEqual(first, second)
            files = sorted(output.iterdir())
            build.write_checksums(files, output / "SHA256SUMS")
            lines = (output / "SHA256SUMS").read_text().splitlines()
            self.assertEqual(len(lines), 3)
            for file, line in zip(files, lines, strict=True):
                self.assertEqual(
                    line,
                    f"{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.name}",
                )


if __name__ == "__main__":
    unittest.main()
