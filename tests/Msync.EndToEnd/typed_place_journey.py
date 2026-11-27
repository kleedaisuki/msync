from __future__ import annotations

import argparse
import json
import sqlite3
import subprocess
import sys
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]
WORK: Path = ROOT / ".temp" / "portable-place-journey"

FIXTURES: dict[str, tuple[str, bytes, tuple[str, ...], tuple[str, ...]]] = {
    "static": (
        """
object State { value: 7, mark: false };
State::value = 41;
if not State::mark { $std::io::println(State::value as String); };
""",
        b"41\n",
        ("LoadStaticField", "StoreStaticField", "PublishStatic"),
        ("LoadStaticScope",),
    ),
    "identity": (
        """
object State { value: 7, mark: false };
let shared = State;
shared::value = 42;
$std::io::println(State::value as String);
State::value = 43;
$std::io::println(shared::value as String);
""",
        b"42\n43\n",
        ("LoadStaticScope", "LoadField", "StoreField"),
        (),
    ),
    "distinct-ordinals": (
        """
fn read(choose: Bool) -> Number {
    let value = if choose { object { other: 0, item: 11 } }
        else { object { item: 22, other: 0 } };
    value::item
};
$std::io::println(read(true) as String);
$std::io::println(read(false) as String);
""",
        b"11\n22\n",
        ("NewScope", "LoadField", ".#1", ".#0"),
        (),
    ),
    "selected-once": (
        """
let initializations = 0;
fn tick(value: Number) -> Number {
    global initializations;
    initializations = initializations + 1;
    value
};
fn start(value: Number) {
    object State { count: tick(value), kind: "counter" };
    State
};
let first = start(9);
let second = start(99);
second::count = 14;
$std::io::println(first::count as String);
$std::io::println(initializations as String);
""",
        b"14\n1\n",
        ("JumpIfStaticReady", "PublishStatic", "LoadStaticScope"),
        (),
    ),
}


def process_fixture() -> tuple[str, bytes, tuple[str, ...], tuple[str, ...]]:
    executable: str = json.dumps(Path(sys.executable).as_posix())
    text: str = f"""
fn child_at(visible: Bool) -> Process? {{
    object Selected {{
        child: $std::process::spawn({executable}, [
            "-c", "import sys; sys.stdout.write(sys.stdin.read())",
        ]),
    }};
    if visible {{ Selected::child }} else {{ null }}
}};
child_at(false);
let retained? = child_at(true);
if retained is not null {{
    $std::process::write_stdin(retained, "static-owner");
    $std::process::close_stdin(retained);
    $std::io::println($std::process::stdout($std::process::wait(retained)));
}};
"""
    return (
        text,
        b"static-owner\n",
        ("LoadStaticField", "PublishStatic"),
        ("LoadStaticScope",),
    )


def run(cli: Path, cwd: Path, *args: str | Path, profile: Path | None = None) -> bytes:
    flags: list[str | Path] = ["--no-telemetry"]
    if profile is not None:
        profile.unlink(missing_ok=True)
        flags = ["--telemetry-db", profile, "--telemetry-level", "profile"]
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [cli, "--no-config", *flags, *args],
        cwd=cwd,
        input=b"",
        capture_output=True,
        check=False,
        timeout=30,
    )
    if result.returncode != 0 or result.stderr:
        raise RuntimeError((str(cwd), args, result.returncode, result.stderr.decode()))
    return result.stdout


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--level", type=int, action="append", default=[])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    cli: Path = args.binary.resolve()
    report: list[dict[str, object]] = []
    for level in args.level or [0, 2]:
        for name, (text, expected, has, lacks) in (
            FIXTURES | {"static-process": process_fixture()}
        ).items():
            cwd: Path = WORK / f"O{level}-{name}"
            cwd.mkdir(parents=True, exist_ok=True)
            source: Path = cwd / "fixture.msync"
            mso: Path = cwd / "fixture.mso"
            mexe: Path = cwd / "fixture.mexe"
            source.write_text(text, encoding="utf-8")
            assert run(cli, cwd, "run", f"-O{level}", source) == expected
            run(cli, cwd, "compile", f"-O{level}", source, "-o", mso)
            listing: str = run(cli, cwd, "disassemble", mso).decode()
            if level == 0:
                assert all(needle in listing for needle in has), (name, has, listing)
                assert not any(needle in listing for needle in lacks), (
                    name,
                    lacks,
                    listing,
                )
            assert "format 6" in listing and "(layout-checked hint)" not in listing
            source.unlink()
            assert run(cli, cwd, "run-ir", mso) == expected
            run(cli, cwd, "bind-ir", mso, "-o", mexe)
            mso.unlink()
            if level == 0 and name in {"static", "identity", "static-process"}:
                profile: Path = cwd / "profile.sqlite"
                assert run(cli, cwd, "run-ir", mexe, profile=profile) == expected
                with sqlite3.connect(profile) as db:
                    metrics: dict[str, int] = dict(
                        db.execute(
                            "select m.key, sum(m.value_int) from metrics m join records r on r.id=m.record_id "
                            "where r.name='vm.function' and m.key like 'static_scope_%' group by m.key"
                        )
                    )
                assert metrics == {
                    "static_scope_publications": 1,
                    "static_scope_materializations": int(name == "identity"),
                }, metrics
            else:
                assert run(cli, cwd, "run-ir", mexe) == expected
            report.append(
                {"case": name, "level": level, "routes": ["source", "mso", "mexe"]}
            )
    result: str = json.dumps(report, indent=2) + "\n"
    if args.output is not None:
        args.output.write_text(result, encoding="utf-8")
    print(result, end="")


if __name__ == "__main__":
    main()
