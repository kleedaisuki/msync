#!/usr/bin/env python3
"""Exercise effective configuration through the real 0.8 CLI."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
import tomllib
from pathlib import Path
from typing import cast

ROOT = Path(__file__).resolve().parents[2]


class Journey:
    def __init__(self, binary: Path, directory: Path) -> None:
        self.binary = binary
        self.directory = directory
        self.home = directory / "home"
        self.canonical = self.home / ".config/msync/config.toml"
        self.fallback = self.home / ".msync/config.toml"
        self.environment = dict(os.environ)
        self.home.mkdir()
        for name in ("MSYNC_STATE_DIR", "XDG_CONFIG_HOME", "MSYNC_LEGACY"):
            self.environment.pop(name, None)
        self.environment.update(
            HOME=str(self.home),
            USERPROFILE=str(self.home),
            XDG_CONFIG_HOME=str(self.home / ".config"),
            APPDATA=str(self.home / ".config"),
        )

    def invoke(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [str(self.binary), *arguments],
            cwd=self.directory,
            env=self.environment,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )

    def config(self, *arguments: str) -> dict[str, object]:
        result = self.invoke("config", *arguments)
        assert result.returncode == 0, (arguments, result.stdout, result.stderr)
        return tomllib.loads(result.stdout)

    @staticmethod
    def write(path: Path, content: str) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")


def section(document: dict[str, object], name: str) -> dict[str, object]:
    value = document[name]
    assert isinstance(value, dict), document
    return cast(dict[str, object], value)


def run(binary: Path, directory: Path) -> list[str]:
    journey = Journey(binary, directory)
    passed: list[str] = []
    defaults = journey.config("--no-config")
    assert section(defaults, "execution")["jobs"] == 4
    assert section(defaults, "language")["syntax"] == "0.8"
    assert section(defaults, "execution")["state_dir"] == str(
        journey.home / ".msync/state"
    )
    assert not (journey.home / ".msync").exists()
    passed.append("default inspection has no state side effects")

    journey.write(directory / "config.toml", "[execution]\njobs = 99\n")
    assert section(journey.config(), "execution")["jobs"] == 4
    passed.append("working-directory policy is not auto-loaded")

    journey.write(journey.fallback, "[execution]\njobs = 3\n")
    assert section(journey.config(), "execution")["jobs"] == 3
    journey.write(
        journey.canonical, "[execution]\njobs = 7\n[progress]\ndelay_ms = 300\n"
    )
    assert section(journey.config(), "execution")["jobs"] == 7
    assert section(journey.config("--no-config"), "execution")["jobs"] == 4
    passed.append("canonical configuration supersedes fallback; bypass works")

    assert section(journey.config("--jobs", "2"), "execution")["jobs"] == 2
    output = journey.invoke("config", "--jobs", "2")
    assert "# source: command line" in output.stdout
    assert "configuration " in output.stdout
    passed.append("CLI precedence preserves configuration provenance")

    policy = directory / "policy/settings.toml"
    journey.write(
        policy,
        '[execution]\njobs = 5\nstate_dir = "state"\n'
        '[telemetry]\nenabled = false\npath = "logs/session.sqlite"\n',
    )
    explicit = journey.config("--config", str(policy))
    assert section(explicit, "execution")["state_dir"] == str(policy.parent / "state")
    assert section(explicit, "telemetry")["path"] == str(
        policy.parent / "logs/session.sqlite"
    )
    assert not (policy.parent / "state").exists()
    assert not (policy.parent / "logs").exists()
    passed.append("relative paths anchor to config without creating destinations")

    journey.environment["MSYNC_STATE_DIR"] = str(directory / "environment-state")
    explicit = journey.config("--config", str(policy))
    assert section(explicit, "execution")["state_dir"] == str(
        directory / "environment-state"
    )
    passed.append("compatibility environment overrides file values")

    assert journey.invoke("config", "--config", str(policy), "--no-config").returncode
    assert journey.invoke("config", "--config", "missing.toml").returncode
    passed.append("conflicting selection and explicit missing file are reported")

    journey.write(journey.canonical, "[execution]\njobs = [\n")
    malformed = journey.invoke("config")
    assert malformed.returncode != 0
    assert str(journey.canonical) in malformed.stderr
    for flag in ("--help", "--version"):
        bypass = journey.invoke(flag, "--config", "missing.toml")
        assert bypass.returncode == 0, bypass.stderr
    passed.append("malformed canonical does not fall back; help/version bypass")

    journey.write(policy, "[execution]\njobs_typo = 8\n")
    unknown = journey.invoke("config", "--config", str(policy))
    assert unknown.returncode != 0
    assert "jobs_typo" in unknown.stderr and str(policy) in unknown.stderr
    assert ":2:" in unknown.stderr, unknown.stderr
    passed.append("unknown-key diagnostics retain actual source coordinates")

    journey.write(policy, "[execution]\njobs = 0\n")
    assert section(journey.config("--config", str(policy)), "execution")["jobs"] == 0
    script = directory / "check.msync"
    journey.write(script, '"not executed" -> ./result;\n')
    invalid = journey.invoke("check", str(script), "--config", str(policy))
    assert invalid.returncode != 0 and "execution.jobs" in invalid.stderr
    overridden = journey.invoke(
        "check", str(script), "--config", str(policy), "--jobs", "2"
    )
    assert overridden.returncode == 0, overridden.stderr
    assert not (directory / "result").exists()
    passed.append("inspection admits repair; effective overrides validate once")

    snapshot = directory / "roundtrip.toml"
    snapshot.write_text(
        journey.invoke("config", "--no-config").stdout, encoding="utf-8"
    )
    assert journey.config("--config", str(snapshot)) == journey.config("--no-config")
    passed.append("inspection TOML round-trips through the actual loader")
    return passed


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    arguments = parser.parse_args()
    temporary = ROOT / ".temp/configuration-v080"
    temporary.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="journey-", dir=temporary))
    passed = run(arguments.binary.resolve(), directory)
    report = {"passed": passed, "fixture": str(directory)}
    (directory / "results.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
