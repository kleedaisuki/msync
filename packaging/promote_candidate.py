#!/usr/bin/env python3
"""Stage the exact accepted release payloads without rebuilding their bytes."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import zipfile
from pathlib import Path
from typing import Any

from audit_payloads import audit_native, source_files
from build import version as package_version

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = (
    "release-linux-x86_64",
    "release-windows-x86_64",
    "release-vscode",
    "acceptance-linux-x86_64",
    "acceptance-windows-x86_64",
    "acceptance-vscode",
)


def api(repository: str, endpoint: str) -> dict[str, Any]:
    raw = subprocess.check_output(
        ["gh", "api", f"repos/{repository}/{endpoint}"],
        cwd=ROOT,
        text=True,
        encoding="utf-8",
    )
    value: dict[str, Any] = json.loads(raw)
    return value


def validate_candidate(
    run: dict[str, Any],
    jobs: list[dict[str, Any]],
    artifacts: list[dict[str, Any]],
    source: str,
) -> None:
    if run["head_sha"] != source:
        raise ValueError("The candidate was not built from the current main source")
    if run["status"] != "completed" or run["conclusion"] != "success":
        raise ValueError(
            "The candidate release workflow has not completed successfully"
        )
    if str(run["path"]).split("@", 1)[0] != ".github/workflows/release.yml":
        raise ValueError("The candidate is not a release-package workflow")
    native = [job for job in jobs if str(job["name"]).startswith("native (")]
    editor = [job for job in jobs if job["name"] == "editor"]
    if len(native) != 2 or len(editor) != 1:
        raise ValueError(
            "The candidate lacks both native platforms or editor acceptance"
        )
    if any(job["conclusion"] != "success" for job in [*native, *editor]):
        raise ValueError("Native or editor acceptance did not succeed")
    names = {str(item["name"]) for item in artifacts if not item.get("expired")}
    missing = set(ARTIFACTS) - names
    if missing:
        raise ValueError(
            f"Candidate artifacts are missing: {', '.join(sorted(missing))}"
        )


def payload_receipt(directory: Path) -> dict[str, Any]:
    files = list(directory.rglob("payload.json"))
    if len(files) != 1:
        raise ValueError(f"Expected one payload receipt under {directory}")
    result: dict[str, Any] = json.loads(files[0].read_text(encoding="utf-8"))
    return result


def verify_payload(
    path: Path, receipt: dict[str, Any], version: str, source: str
) -> dict[str, object]:
    if receipt["version"] != version or receipt["source"] != source:
        raise ValueError("Candidate payload version/source does not match main")
    entries = {str(item["name"]): item for item in receipt["assets"]}
    expected = entries.get(path.name)
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if (
        expected is None
        or expected["sha256"] != actual
        or expected["size"] != path.stat().st_size
    ):
        raise ValueError(
            f"Candidate payload differs from its accepted receipt: {path.name}"
        )
    return {"name": path.name, "size": path.stat().st_size, "sha256": actual}


def stage(
    download: Path, destination: Path, version: str, source: str
) -> dict[str, object]:
    names = {
        "linux-x86_64": (f"msync-{version}-linux-x86_64.tar.gz",),
        "windows-x86_64": (
            f"msync-{version}-windows-x86_64.zip",
            f"msync-{version}-windows-x86_64-setup.exe",
        ),
        "vscode": (f"msync-language-{version}.vsix",),
    }
    destination.mkdir(parents=True, exist_ok=True)
    assets: list[dict[str, object]] = []
    for platform, files in names.items():
        receipt = payload_receipt(download / f"acceptance-{platform}")
        recorded = {str(item["name"]) for item in receipt["assets"]}
        if recorded != set(files):
            raise ValueError(f"Unexpected payload receipt members for {platform}")
        for name in files:
            payload = download / f"release-{platform}" / name
            assets.append(verify_payload(payload, receipt, version, source))
            shutil.copy2(payload, destination / name)
    sources = source_files(source)
    linux = audit_native(destination / names["linux-x86_64"][0], sources, False)
    windows = audit_native(destination / names["windows-x86_64"][0], sources, True)
    with zipfile.ZipFile(destination / names["vscode"][0]) as archive:
        metadata: dict[str, Any] = json.loads(archive.read("extension/package.json"))
        if metadata["version"] != version:
            raise ValueError("The accepted VSIX has another package version")
        members = {
            name
            for name in archive.namelist()
            if name.startswith("extension/") and not name.endswith("/")
        }
        receipt = payload_receipt(download / "acceptance-vscode")
        if members != set(receipt["members"]):
            raise ValueError(
                "The accepted VSIX payload set differs from its editor receipt"
            )
    return {
        "source": source,
        "version": version,
        "assets": assets,
        "linux": linux,
        "windows": windows,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=int, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / ".temp/promoted-release")
    arguments = parser.parse_args()
    if os.environ["GITHUB_REF"] != "refs/heads/main":
        raise ValueError("Only main can publish verified release packages")
    source = os.environ["GITHUB_SHA"]
    repository = os.environ["GITHUB_REPOSITORY"]
    run = api(repository, f"actions/runs/{arguments.run}")
    jobs = api(repository, f"actions/runs/{arguments.run}/jobs?per_page=100")
    artifacts = api(repository, f"actions/runs/{arguments.run}/artifacts?per_page=100")
    validate_candidate(run, jobs["jobs"], artifacts["artifacts"], source)
    download = arguments.output.resolve() / "download"
    command = [
        "gh",
        "run",
        "download",
        str(arguments.run),
        "--repo",
        repository,
        "--dir",
        str(download),
    ]
    for name in ARTIFACTS:
        command.extend(("--name", name))
    subprocess.run(command, cwd=ROOT, check=True)
    report = stage(download, ROOT / "dist", package_version(), source)
    report["candidate_run"] = arguments.run
    arguments.output.mkdir(parents=True, exist_ok=True)
    (arguments.output / "receipt.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
