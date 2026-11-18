"""Check bound machine CALL/RET preserves reference identity and adopts a read-only window."""
from __future__ import annotations

import argparse
import os
import platform
import sqlite3
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: str = """
fn relay(node) { BODY };
let node=scope { value: 0 };
let parent=scope { child: node };
let i=0;
loop {
    if i>=8192 { break; };
    let alias=relay(INPUT);
    alias::value=alias::value+1;
    i=i+1;
};
$std::io::println(node::value as String);
"""


def metrics(path: Path) -> dict[str, int]:
    with sqlite3.connect(path) as connection:
        assert connection.execute("SELECT status,exporter_error FROM sessions").fetchall() == [("complete", None)]
        return dict(connection.execute("""
            SELECT m.key,sum(m.value_int) FROM records r JOIN metrics m ON r.id=m.record_id
            WHERE r.name='vm.function' AND m.key IN (
                'jit_machine_address_results','jit_machine_argument_window_calls'
            ) GROUP BY m.key
        """))


def exercise(binary: Path, work: Path, shape: str, body: str, argument: str) -> None:
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    source: Path = work / f"{shape}.msync"
    source.write_text(SOURCE.replace("BODY", body).replace("INPUT", argument), encoding="utf-8")
    obj: Path = source.with_suffix(".mso")
    executable: Path = source.with_suffix(".mexe")
    for arguments in (
        ["compile", "-O2", source.name, "-o", obj.name],
        ["bind-ir", obj.name, "-o", executable.name],
    ):
        subprocess.run([*cli, "--no-telemetry", *arguments], cwd=work, check=True, timeout=30)
    source.unlink()
    obj.unlink()

    for tier in ("off", "auto", "force"):
        database: Path = work / f"{shape}-{tier}.sqlite"
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            [*cli, "--telemetry-db", str(database), "--telemetry-level", "profile", "run", executable.name],
            cwd=work, env={**os.environ, "MSYNC_JIT": tier}, check=True, capture_output=True, timeout=30,
        )
        assert result.stdout.splitlines() == [b"8192"] and not result.stderr, result
        counts: dict[str, int] = metrics(database)
        returns: int = counts.get("jit_machine_address_results", 0)
        windows: int = counts.get("jit_machine_argument_window_calls", 0)
        if tier != "off" and platform.machine().lower() in ("amd64", "x86_64"):
            assert returns > 0 and windows == (returns if shape == "identity" else 0), (shape, tier, counts)
        else:
            assert returns == 0 and windows == 0, (shape, tier, counts)
        print(f"{shape}/{tier}: {returns} native reference returns; {windows} borrowed windows")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    options: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="native-reference-call-") as temporary:
        work: Path = Path(temporary)
        exercise(options.binary.resolve(), work, "identity", "node", "node")
        exercise(options.binary.resolve(), work, "projection", "node::child", "parent")


if __name__ == "__main__":
    main()
