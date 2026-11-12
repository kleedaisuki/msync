#!/usr/bin/env python3
"""Build and stage Native AOT packages."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import time
import xml.etree.ElementTree as ET
import zipfile
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RID_NAMES = {
    "linux-x64": "linux-x86_64",
    "linux-arm64": "linux-aarch64",
    "win-x64": "windows-x86_64",
}


def command(arguments: Sequence[str], *, capture: bool = False) -> str:
    result = subprocess.run(
        arguments,
        cwd=ROOT,
        check=True,
        text=True,
        encoding="utf-8",
        stdout=subprocess.PIPE if capture else None,
    )
    return result.stdout if capture else ""


def version(root: Path = ROOT) -> str:
    value = ET.parse(root / "Directory.Build.props").getroot().findtext(".//Version")
    if value is None or re.fullmatch(r"\d+\.\d+\.\d+", value) is None:
        raise ValueError("Directory.Build.props must declare a numeric Version")
    editor = json.loads(
        (root / "editors/vscode/package.json").read_text(encoding="utf-8")
    )
    if editor["version"] != value:
        raise ValueError(
            f"Editor version {editor['version']} does not match msync {value}"
        )
    return value


def host_rid() -> str:
    system = {"Linux": "linux", "Windows": "win"}.get(platform.system())
    architecture = {
        "x86_64": "x64",
        "amd64": "x64",
        "aarch64": "arm64",
        "arm64": "arm64",
    }.get(platform.machine().lower())
    rid = f"{system}-{architecture}"
    if rid not in RID_NAMES:
        raise ValueError("Packaging supports Linux x64/ARM64 and Windows x64 hosts")
    return rid


def tracked_files(pattern: str) -> list[Path]:
    names = (
        subprocess.check_output(["git", "ls-files", "-z", "--", pattern], cwd=ROOT)
        .decode("utf-8")
        .split("\0")
    )
    return [Path(name) for name in names if name and (ROOT / name).is_file()]


def epoch() -> int:
    supplied = os.environ.get("SOURCE_DATE_EPOCH")
    return (
        int(supplied)
        if supplied
        else int(command(["git", "log", "-1", "--format=%ct"], capture=True).strip())
    )


def copy_file(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)


def native_libraries(publish: Path) -> list[Path]:
    return sorted(
        path
        for path in publish.rglob("*")
        if path.is_file()
        and (path.suffix.lower() in {".dll", ".dylib", ".so"} or ".so." in path.name)
    )


def copy_native_libraries(publish: Path, destination: Path) -> None:
    for library in native_libraries(publish):
        copy_file(library, destination / library.relative_to(publish))


def stage_payload(
    publish: Path,
    stage: Path,
    rid: str,
    package_version: str,
    runner: Path | None = None,
) -> None:
    binary = publish / ("msync.exe" if rid.startswith("win-") else "msync")
    prefix = [str(runner)] if runner else []
    reported = command([*prefix, str(binary), "--version"], capture=True).strip()
    if reported != f"msync {package_version}":
        raise ValueError(f"Published executable version mismatch: {reported}")
    copy_file(binary, stage / "bin" / binary.name)
    copy_native_libraries(publish, stage / "bin")
    docs = stage / "share/doc/msync"
    for license_file in (ROOT / "packaging/licenses/native").rglob("*"):
        if license_file.is_file():
            copy_file(
                license_file,
                docs
                / "native-dependencies"
                / license_file.relative_to(ROOT / "packaging/licenses/native"),
            )
    for relative in tracked_files("vendor/PrettyPrompt"):
        copy_file(
            ROOT / relative,
            docs
            / "native-dependencies/prettyprompt-6.0.5/source"
            / relative.relative_to("vendor/PrettyPrompt"),
        )
    copy_file(
        ROOT / "vendor/unicode/LICENSE.txt",
        docs / "native-dependencies/Unicode-LICENSE.txt",
    )
    for relative in [Path("README.md"), Path("LICENSE"), *tracked_files("docs")]:
        copy_file(ROOT / relative, docs / relative)
    libraries = tracked_files("stdlib/*.msync")
    missing = {"fs", "path", "bool", "string", "ranges", "process"} - {
        path.stem for path in libraries
    }
    if missing:
        raise ValueError(
            f"Tracked standard-library modules are missing: {', '.join(sorted(missing))}"
        )
    for relative in libraries:
        copy_file(ROOT / relative, stage / "share/msync/std" / relative.name)
    sdk_version = command(["dotnet", "--version"], capture=True).strip()
    sdk_line = next(
        line
        for line in command(["dotnet", "--list-sdks"], capture=True).splitlines()
        if line.startswith(sdk_version + " ")
    )
    sdk_root = Path(sdk_line.split("[", 1)[1].rstrip("]")).parent
    for name in ("LICENSE.txt", "ThirdPartyNotices.txt"):
        copy_file(sdk_root / name, docs / "dotnet" / name)
    install = {
        "win-x64": "windows/portable-readme.txt",
        "linux-x64": "linux/INSTALL.md",
        "linux-arm64": "linux/INSTALL-arm64.md",
    }[rid]
    copy_file(ROOT / "packaging" / install, stage / "INSTALL.md")
    payload = sorted(path for path in stage.rglob("*") if path.is_file())
    manifest = "".join(
        f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(stage).as_posix()}\n"
        for path in payload
    )
    (stage / "manifest.sha256").write_text(manifest, encoding="utf-8")


def archive_payload(stage: Path, output: Path, rid: str, stamp: int) -> Path:
    files = sorted(path for path in stage.rglob("*") if path.is_file())
    if rid.startswith("win-"):
        destination = output / f"{stage.name}.zip"
        date = time.gmtime(max(stamp, 315532800))[:6]
        with zipfile.ZipFile(
            destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
        ) as archive:
            for path in files:
                info = zipfile.ZipInfo(
                    f"{stage.name}/{path.relative_to(stage).as_posix()}", date
                )
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                archive.writestr(info, path.read_bytes())
        return destination
    destination = output / f"{stage.name}.tar.gz"
    with (
        destination.open("wb") as stream,
        gzip.GzipFile(
            filename="", fileobj=stream, mode="wb", mtime=stamp
        ) as compressed,
        tarfile.open(fileobj=compressed, mode="w") as archive,
    ):
        for path in files:
            tar_info = archive.gettarinfo(
                str(path), arcname=f"{stage.name}/{path.relative_to(stage).as_posix()}"
            )
            (
                tar_info.mtime,
                tar_info.uid,
                tar_info.gid,
                tar_info.uname,
                tar_info.gname,
            ) = stamp, 0, 0, "", ""
            tar_info.mode = 0o755 if path.parent.name == "bin" else 0o644
            with path.open("rb") as source:
                archive.addfile(tar_info, source)
    return destination


def write_checksums(files: Sequence[Path], destination: Path) -> None:
    destination.write_text(
        "".join(
            f"{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.name}\n"
            for file in sorted(files)
        ),
        encoding="utf-8",
    )


def build(
    rid: str,
    output: Path,
    *,
    skip_build: bool = False,
    iscc: Path | None = None,
    smoke: bool = False,
    runner: Path | None = None,
) -> list[Path]:
    host = host_rid()
    cross_arm64 = host == "linux-x64" and rid == "linux-arm64"
    if cross_arm64:
        if not skip_build or runner is None:
            raise ValueError(
                "Cross-packaging Linux ARM64 needs --skip-build and --runner"
            )
        if runner.is_file():
            runner = runner.resolve()
    elif rid != host or runner is not None:
        raise ValueError(
            "Use the target host to build packages; --runner is for Linux ARM64 cross-packaging"
        )
    package_version = version()
    publish = ROOT / "artifacts/publish" / rid
    if not skip_build:
        command(
            [
                "dotnet",
                "publish",
                "src/Msync.Cli/Msync.Cli.csproj",
                "-c",
                "Release",
                f"-p:RuntimeIdentifier={rid}",
                "--self-contained",
                "true",
                "-p:RestoreLockedMode=true",
                "-o",
                str(publish),
            ]
        )
    stage = ROOT / "artifacts/package" / f"msync-{package_version}-{RID_NAMES[rid]}"
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True)
    output.mkdir(parents=True, exist_ok=True)
    stage_payload(publish, stage, rid, package_version, runner)
    binary = stage / "bin" / ("msync.exe" if rid.startswith("win-") else "msync")
    if smoke:
        command(
            [
                sys.executable,
                "packaging/smoke.py",
                "--binary",
                str(binary),
                *(["--runner", str(runner)] if runner else []),
            ]
        )
    files = [archive_payload(stage, output, rid, epoch())]
    if rid.startswith("win-"):
        compiler = (
            iscc
            or Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)"))
            / "Inno Setup 6/ISCC.exe"
        )
        command(
            [
                str(compiler),
                "/Qp",
                f"/DAppVersion={package_version}",
                f"/DSourceDir={stage}",
                f"/DOutputDir={output}",
                f"/DOutputBaseName={stage.name}-setup",
                str(ROOT / "packaging/windows/msync.iss"),
            ]
        )
        files.append(output / f"{stage.name}-setup.exe")
    write_checksums(files, output / f"{stage.name}-SHA256SUMS.txt")
    return files


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=RID_NAMES)
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--iscc", type=Path)
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument(
        "--runner",
        type=Path,
        help="Launcher executable for a prebuilt Linux ARM64 payload on Linux x64",
    )
    parser.add_argument("--version", action="store_true")
    args = parser.parse_args()
    if args.version:
        print(version())
        return
    for file in build(
        args.rid or host_rid(),
        args.output.resolve(),
        skip_build=args.skip_build,
        iscc=args.iscc,
        smoke=args.smoke,
        runner=args.runner,
    ):
        print(file)


if __name__ == "__main__":
    main()
