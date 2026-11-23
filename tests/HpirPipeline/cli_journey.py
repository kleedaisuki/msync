#!/usr/bin/env python3
"""Exercise detached HPIR through the production CLI and editor protocol."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
from pathlib import Path
from typing import Any, cast

ROOT = Path(__file__).resolve().parents[2]
SOURCE = """\
fn child(base: Path, name: String) -> Path { base / name };
let source = child(./input, "payload.txt");
let target = child(./output, "copy.txt");
let copied = source -> target;
$std::io::println(if copied { "copied" } else { "not copied" });
"""
PROMOTION_SOURCE = """\
fn step(n: Number) -> Number { n + 1 };
$std::io::println("start");
let count? = $std::io::read_line();
let limit = if count is null { 0 } else { count as Number };
let i = 0;
loop {
    if i >= limit { break i; };
    i = step(i);
};
$std::io::println(i as String);
"""
Json = dict[str, Any]


def command(program: Path, host: Path | None) -> tuple[str, ...]:
    return (str(program.resolve()),) if host is None else (str(host.resolve()), str(program.resolve()))


def invoke(
    prefix: tuple[str, ...], cwd: Path, *arguments: str, status: int = 0, input_data: bytes | None = None,
    jit: str | None = None,
) -> subprocess.CompletedProcess[bytes]:
    result = subprocess.run(
        [*prefix, *arguments],
        cwd=cwd,
        input=input_data,
        stdin=subprocess.DEVNULL if input_data is None else None,
        env=None if jit is None else {**os.environ, "MSYNC_JIT": jit},
        capture_output=True,
        check=False,
        timeout=30,
    )
    assert result.returncode == status, (arguments, result.returncode, result.stdout, result.stderr)
    if status == 0:
        assert not result.stderr, (arguments, result.stderr)
    return result


def inspect(cli: tuple[str, ...], directory: Path, image: Path, source: Path) -> None:
    structured = cast(Json, json.loads(invoke(cli, directory, "disassemble", str(image), "--json").stdout))
    assert structured["stage"] == "executable" and structured["representation"] == "hpir", structured
    assert any(function["name"] == "child" and function["frameBytes"] > 0 for function in structured["functions"])
    assert any(imp["service"] == "LiveTransfer" for imp in structured["imports"])
    editor = cast(Json, json.loads(invoke(cli, directory, "disassemble", str(image), "--editor-json").stdout))
    assert editor["artifactKind"] == "hpir" and editor["stage"] == "executable"
    lines = str(editor["text"]).splitlines()
    assert any("child" in lines[ref["targetLine"]] for ref in editor["references"] if "targetLine" in ref)
    assert any(ref.get("sourceUri") == source.as_uri() for ref in editor["references"])


def journey(binary: tuple[str, ...], root: Path) -> None:
    cli = (*binary, "--no-config", "--color", "never", "--no-telemetry")
    guest, elsewhere = root / "binding", root / "execution"
    (guest / "input").mkdir(parents=True)
    elsewhere.mkdir()
    source = guest / "detached 𐐀.msync"
    source.write_text(SOURCE, encoding="utf-8")
    portable, bound = (guest / f"detached.{suffix}" for suffix in ("mso", "mexe"))
    invoke(cli, guest, "compile", str(source), "-O2", "-o", str(portable))
    source.unlink()
    invoke(cli, guest, "check", str(portable))
    invoke(cli, guest, "emit-ir", str(portable), "-o", str(guest / "reemitted.mso"))
    assert (guest / "reemitted.mso").read_bytes() == portable.read_bytes()
    invoke(cli, guest, "plan-ir", str(portable))
    invoke(cli, guest, "bind-ir", str(portable), "-o", str(bound))
    invoke(cli, guest, "compile", str(portable), "--prepare", "-o", str(guest / "exported.mexe"))
    inspect(cli, elsewhere, bound, source)
    assert (guest / "exported.mexe").read_bytes() == bound.read_bytes()
    assert not (guest / "output").exists()

    image_bytes = bound.read_bytes()
    corrupt = guest / "invalid.mexe"
    corrupt.write_bytes(image_bytes[:-1])
    assert b"HPIR" in invoke(cli, elsewhere, "run", str(corrupt), status=1).stderr
    corrupt.write_bytes(image_bytes + b"\0")
    assert b"Unexpected data after the HPIR image" in invoke(cli, elsewhere, "run", str(corrupt), status=1).stderr

    payload = guest / "input" / "payload.txt"
    payload.write_bytes("live bytes: 檔案\n".encode())
    target = guest / "output" / "copy.txt"
    dry = invoke(cli, elsewhere, "run-ir", str(bound), "--dry-run")
    assert dry.stdout.decode().splitlines()[-1] == "copied" and not target.exists()
    for verb, image in (("run", bound), ("run-ir", guest / "exported.mexe")):
        success = invoke(cli, elsewhere, verb, str(image))
        assert success.stdout.decode().splitlines() == ["copied"]
        assert target.read_bytes() == payload.read_bytes() and not (elsewhere / "output").exists()
        target.unlink()
    payload.unlink()
    failed = invoke(cli, elsewhere, "run-ir", str(bound), status=1)
    assert failed.stdout.decode().splitlines() == ["not copied"] and not target.exists()
    assert failed.stderr.decode().count("payload.txt") == 1, failed.stderr
    assert source.name in failed.stderr.decode(), failed.stderr


def contextual_branch(binary: tuple[str, ...], root: Path) -> None:
    cli = (*binary, "--no-config", "--color", "never", "--no-telemetry")
    origin, elsewhere = root / "predicate", root / "run-predicate"
    origin.mkdir()
    elsewhere.mkdir()
    source = origin / "branch.msync"
    source.write_text(
        "if exists ./choose-first { ./first.txt -> ./chosen.txt; }\n"
        "else { ./second.txt -> ./chosen.txt; };\n",
        encoding="utf-8",
    )
    portable, bound = source.with_suffix(".mso"), source.with_suffix(".mexe")
    invoke(cli, origin, "compile", str(source), "-O2", "-o", str(portable))
    source.unlink()
    marker = origin / "choose-first"
    marker.write_bytes(b"observed only at bind")
    invoke(cli, origin, "bind-ir", str(portable), "-o", str(bound))
    portable.unlink()
    description = cast(Json, json.loads(invoke(cli, elsewhere, "disassemble", str(bound), "--json").stdout))
    assert [(item["operation"], item["state"], item["value"]) for item in description["contextChecks"]] == [
        ("fs.exists", "frozen", True)
    ]
    chosen = origin / "chosen.txt"
    assert not chosen.exists()
    marker.unlink()
    (origin / "first.txt").write_bytes(b"new live bytes")
    (origin / "second.txt").write_bytes(b"wrong branch")
    invoke(cli, elsewhere, "run-ir", str(bound))
    assert chosen.read_bytes() == b"new live bytes" and not (elsewhere / "chosen.txt").exists()


def promotion(binary: tuple[str, ...], root: Path) -> None:
    cli = (*binary, "--no-config", "--color", "never")
    source, portable, bound = (root / f"adaptive.{suffix}" for suffix in ("msync", "mso", "mexe"))
    source.write_text(PROMOTION_SOURCE, encoding="utf-8")
    invoke(cli, root, "compile", str(source), "-O2", "-o", str(portable))
    source.unlink()
    invoke(cli, root, "bind-ir", str(portable), "-o", str(bound))
    portable.unlink()
    decisions: dict[tuple[str, int], str] = {}
    for policy, count in (("auto", 2), ("auto", 50_000), ("off", 50_000), ("force", 2)):
        trace = root / f"adaptive-{policy}-{count}.jsonl"
        result = invoke(cli, root, "run", str(bound), "--trace", str(trace), "--telemetry-level", "diagnostics",
                        input_data=f"{count}\n".encode(), jit=policy)
        assert result.stdout.decode().splitlines() == ["start", str(count)]
        records = [cast(Json, json.loads(line)) for line in trace.read_text(encoding="utf-8").splitlines()]
        execute, = (item for item in records if item["name"] == "hpir.execute")
        decided = str(execute["attributes"].get("machine.promotion", "off"))
        decisions[policy, count] = decided
        promoted = [item for item in records if item["name"] == "hpir.machine.promote"]
        if decided == "promoted":
            assert promoted and all(span["parent_span_id"] == execute["span_id"] for span in promoted)
            if policy == "auto":
                emitted = [item for item in records if item["name"] == "hpir.machine.emit.function"]
                function_ids = {item["attributes"]["function.id"] for item in emitted}
                assert len(function_ids) == len(emitted) == len(promoted)
            else:
                assert len(promoted) == 1
            engine = "machine.x64" if policy == "force" else "physical.interpreter+machine.x64"
            assert execute["attributes"]["dispatch.engine"] == engine
        elif decided != "unavailable":
            assert not promoted
    assert decisions["off", 50_000] == "off", decisions
    assert tuple(decisions["auto", count] for count in (2, 50_000)) in (
        ("below-budget", "promoted"), ("unsupported", "unsupported"), ("below-budget", "unavailable"),
    ), decisions
    assert decisions["force", 2] == decisions["auto", 50_000], decisions


def buffered_input(binary: tuple[str, ...], root: Path) -> None:
    source = root / "input-buffer.msync"
    source.write_text(
        "for line in $std::io::stdin_lines() { $std::io::println(line); };\n",
        encoding="utf-8",
    )
    line = "a" * 8191 + "𐐀"
    payload = (line + "\r\n\r\nlast").encode()
    consumed = invoke((*binary, "--no-config", "--no-telemetry"), root, "run", str(source), input_data=payload)
    assert consumed.stdout.decode().splitlines() == [line, "", "last"]


def named_scope_tail(binary: tuple[str, ...], root: Path) -> None:
    cli = (*binary, "--no-config", "--color", "never", "--no-telemetry")
    fixture = Path(__file__).with_name("fixtures") / "named_scope_tail.msync"
    source = root / fixture.name
    source.write_bytes(fixture.read_bytes())
    expected = [b"1", b"7", b"7", b"discarded init", b"true", b"2", b"before", b"init", b"11", b"11"]
    assert invoke(cli, root, "run", source.name, "-O0").stdout.splitlines() == expected
    formatted = invoke(cli, root, "fmt", source.name, "--stdout").stdout
    source.write_bytes(formatted)
    assert invoke(cli, root, "fmt", source.name, "--stdout").stdout == formatted
    assert invoke(cli, root, "run", source.name, "-O2").stdout.splitlines() == expected
    invoke(cli, root, "compile", source.name, "--emit", "hir", "-o", "scope-tail.mhir")
    invoke(cli, root, "compile", source.name, "-o", "scope-tail.mso")
    source.unlink()
    invoke(cli, root, "bind-ir", "scope-tail.mso", "-o", "scope-tail.mexe")
    for artifact in ("scope-tail.mhir", "scope-tail.mexe"):
        assert invoke(cli, root, "run", artifact).stdout.splitlines() == expected


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Production CLI under test")
    parser.add_argument("--dotnet", type=Path, help="Optional host for the managed CLI DLL")
    args = parser.parse_args()
    parent = ROOT / ".temp" / "hpir" / "cli"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="detached-", dir=parent) as directory:
        binary = command(args.cli, args.dotnet)
        journey(binary, Path(directory))
        contextual_branch(binary, Path(directory))
        promotion(binary, Path(directory))
        buffered_input(binary, Path(directory))
        named_scope_tail(binary, Path(directory))
    print("PASS production CLI: detached artifacts, contextual decisions, native admission, and named Scope results")


if __name__ == "__main__":
    main()
