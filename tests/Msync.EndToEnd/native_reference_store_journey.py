"""Exercise native heap link replacement, removal, and alias identity in a detached artifact."""
from __future__ import annotations

import argparse
import os
import platform
import sqlite3
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory

from native_physical_call_journey import bind

ROOT: Path = Path(__file__).resolve().parents[2]
SOURCE: bytes = b"""
fn new_root(seed: Number, initial) { scope {value:seed, child?:initial} };
let node=scope {value:0};
let root=new_root(0, node);
let i=0;
let cleared=0;
loop {
    if i>=8192 { break; };
    root::child=node;
    root::value=root::value+1;
    let linked?=root::child;
    if linked is not null { linked::value=linked::value+1; };
    root::child=null;
    root::value=root::value+1;
    if root::child is null { cleared=cleared+1; };
    i=i+1;
};
$std::io::println(root::value as String);
$std::io::println(node::value as String);
$std::io::println(cleared as String);
"""


def exercise(binary: Path, work: Path) -> None:
    cli: list[str] = [str(binary), "--no-config", "--color", "never"]
    program: str = bind(cli, work, "links", SOURCE, "-O2")
    native: bool = platform.machine().lower() in {"x86_64", "amd64"}
    for tier in ("off", "auto", "force"):
        database: Path = work / f"{tier}.sqlite"
        result: subprocess.CompletedProcess[bytes] = subprocess.run(
            [*cli, "--telemetry-db", str(database), "--telemetry-level", "profile", "run", program],
            cwd=work, env={**os.environ, "MSYNC_JIT": tier}, capture_output=True, check=True, timeout=30,
        )
        assert result.stdout == b"16384\n8192\n8192\n" and not result.stderr, result
        with sqlite3.connect(database) as connection:
            assert connection.execute("SELECT status,exporter_error FROM sessions").fetchall() == [("complete", None)]
            stores: int = connection.execute("""
                SELECT coalesce(sum(m.value_int),0)
                FROM metrics m JOIN records r ON r.id=m.record_id
                WHERE r.name='vm.function' AND m.key='jit_guest_reference_stores'
            """).fetchone()[0]
        if tier != "off" and native:
            assert stores > 8193, (tier, stores)
        else:
            assert stores == 0, (tier, stores)
        print(f"{tier}: link replacement and removal preserve aliases; {stores} machine reference stores")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    options: argparse.Namespace = parser.parse_args()
    with TemporaryDirectory(dir=ROOT / ".temp", prefix="native-reference-store-") as temporary:
        exercise(options.binary.resolve(), Path(temporary))


if __name__ == "__main__":
    main()
