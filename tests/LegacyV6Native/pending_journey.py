"""Compare frozen pending-value identity and non-demanding range journeys."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import uuid
from dataclasses import dataclass
from pathlib import Path

from cli_journey import ROOT, command, run

CHILD: str = 'let child = release /bin/sh("-c", "while [ ! -f ready ]; do sleep 0.01; done; exit 7");\n'
FINISH: str = '$std::io::write_text(./ready, "go");\n$std::io::println($std::ranges::front(snap) as String);\n'


@dataclass(frozen=True)
class Fixture:
    name: str
    source: str
    stdout: bytes


def fixtures() -> tuple[Fixture, ...]:
    identity: dict[str, tuple[str, bytes]] = {
        "binary": (
            "let r = child::exit_code; let a = r + 1; let b = r + 1;\n",
            b"2\n8\n",
        ),
        "copy": ("let r = child::exit_code; let a = r; let b = r;\n", b"1\n7\n"),
        "projection": (
            "let a = child::exit_code; let b = child::exit_code;\n",
            b"2\n7\n",
        ),
    }
    result: list[Fixture] = [
        Fixture(
            name,
            CHILD
            + body
            + "let snap = $std::ranges::materialize($std::ranges::unique([a, b]));\n"
            + "$std::io::println($std::ranges::size(snap) as String);\n"
            + FINISH,
            expected,
        )
        for name, (body, expected) in identity.items()
    ]
    result.append(
        Fixture(
            "materialize",
            CHILD
            + "let snap = $std::ranges::materialize([child::exit_code]);\n"
            + FINISH,
            b"7\n",
        )
    )
    result.append(
        Fixture(
            "vector",
            CHILD
            + "let owner = $std::storage::vector_new([child::exit_code]);\n"
            + '$std::io::write_text(./ready, "go");\n'
            + "$std::io::println($std::storage::vector_get(owner, 0) as String);\n",
            b"7\n",
        )
    )
    result.append(
        Fixture(
            "type-test",
            CHILD
            + "let kind = if $std::io::read_line() is null { Number } else { Bool };\n"
            + "let value = child::exit_code;\n"
            + "$std::io::println((value is kind) as String);\n"
            + '$std::io::write_text(./ready, "go");\n'
            + "$std::io::println(value as String);\n",
            b"true\n7\n",
        )
    )
    return tuple(result)


def execute(cli: Path, directory: Path, artifact: Path) -> bytes:
    process: subprocess.Popen[bytes]
    with subprocess.Popen(
        command(cli, str(artifact)),
        cwd=directory,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    ) as process:
        try:
            output: bytes
            errors: bytes
            output, errors = process.communicate(input=b"", timeout=10)
        except subprocess.TimeoutExpired as error:
            # Release this test's gated child before reporting the eager-demand bug.
            (directory / "ready").write_text("unblock for cleanup", encoding="utf-8")
            try:
                process.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.communicate()
            raise AssertionError(
                f"{artifact.name}: payload was demanded before the ready-file effect"
            ) from error
        if process.returncode != 0:
            raise AssertionError(
                f"{artifact.name}: exit {process.returncode}: {errors.decode(errors='replace')}"
            )
        return output


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frozen-cli", required=True, type=Path)
    parser.add_argument(
        "--native-cli",
        type=Path,
        default=ROOT / "src/Msync.Cli/bin/Debug/net10.0/msync.dll",
    )
    parser.add_argument(
        "--case", action="append", choices=[fixture.name for fixture in fixtures()]
    )
    options: argparse.Namespace = parser.parse_args()
    if not Path("/bin/sh").exists():
        raise SystemExit("This optional differential journey requires POSIX /bin/sh.")
    frozen: Path = options.frozen_cli.resolve()
    native: Path = options.native_cli.resolve()
    base: Path = ROOT / ".temp/legacy-v6-pending-journeys"
    base.mkdir(parents=True, exist_ok=True)
    directory: Path = base / uuid.uuid4().hex
    directory.mkdir()
    report: dict[str, object] = {"status": "running", "cases": []}
    records: list[dict[str, object]] = []
    report["cases"] = records
    try:
        for fixture in fixtures():
            if options.case and fixture.name not in options.case:
                continue
            for level in (0, 2):
                identity: str = f"{fixture.name}-o{level}"
                path: Path = directory / f"{identity}.msync"
                artifact: Path = path.with_suffix(".mso")
                path.write_text(fixture.source, encoding="utf-8")
                path.with_suffix(".source.txt").write_text(
                    fixture.source, encoding="utf-8"
                )
                run(
                    frozen,
                    directory,
                    "compile",
                    f"-O{level}",
                    str(path),
                    "-o",
                    str(artifact),
                )
                path.unlink()
                for label, cli in (("old", frozen), ("native", native)):
                    working: Path = directory / f"{identity}-{label}"
                    working.mkdir()
                    record: dict[str, object] = {
                        "fixture": identity,
                        "image": label,
                        "artifact_sha256": hashlib.sha256(
                            artifact.read_bytes()
                        ).hexdigest(),
                        "status": "running",
                    }
                    records.append(record)
                    (directory / "result.json").write_text(
                        json.dumps(report, indent=2) + "\n", encoding="utf-8"
                    )
                    actual: bytes = execute(cli, working, artifact)
                    (working / "stdout.bin").write_bytes(actual)
                    if actual != fixture.stdout:
                        raise AssertionError(
                            f"{identity} {label}: expected {fixture.stdout!r}, received {actual!r}"
                        )
                    record["status"] = "complete"
        report["status"] = "complete"
    except BaseException as error:
        report["status"] = "failed"
        report["error"] = str(error)
        raise
    finally:
        (directory / "result.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8"
        )
        print(directory / "result.json")
    print(
        f"PASS {len(records)} frozen/native O0/O2 pending-value checks; source removed before execution"
    )


if __name__ == "__main__":
    main()
