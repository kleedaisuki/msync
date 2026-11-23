#!/usr/bin/env python3
"""Exercise detached HPIR through the production CLI and editor protocol."""

from __future__ import annotations

import argparse
import json
import os
import re
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
    jit: str | None = None, environment: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[bytes]:
    result = subprocess.run(
        [*prefix, *arguments],
        cwd=cwd,
        input=input_data,
        stdin=subprocess.DEVNULL if input_data is None else None,
        env=environment if jit is None else {**(environment or os.environ), "MSYNC_JIT": jit},
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


def transfer_destination(binary: tuple[str, ...], root: Path) -> None:
    cli = (*binary, "--no-config", "--color", "never", "--no-telemetry")
    origin, elsewhere = root / "text-destination", root / "run-text-destination"
    origin.mkdir()
    elsewhere.mkdir()
    source = origin / "write.msync"
    source.write_text(
        '"literal payload" -> "./literal.txt";\n'
        'fn copy(text: Bool) {\n'
        '    let target = if text { "./text.txt" } else { ./path.txt };\n'
        '    ./input.txt -> target\n'
        '};\n'
        'copy(true); copy(false);\n',
        encoding="utf-8",
    )
    portable, bound = source.with_suffix(".mso"), source.with_suffix(".mexe")
    invoke(cli, origin, "compile", source.name, "-O0", "-o", portable.name)
    source.unlink()
    invoke(cli, origin, "bind-ir", portable.name, "-o", bound.name)
    assert not list(origin.glob("*.txt"))
    (origin / "input.txt").write_bytes(b"live file bytes")
    invoke(cli, elsewhere, "run", str(bound))
    assert (origin / "literal.txt").read_bytes() == b"literal payload"
    assert (origin / "text.txt").read_bytes() == (origin / "path.txt").read_bytes() == b"live file bytes"
    assert not list(elsewhere.glob("*.txt"))



def checkpoints(binary: tuple[str, ...], root: Path) -> None:
    cli = (*binary, "--no-config")
    origin, detached = root / "checkpoint-source", root / "checkpoint-resume"
    origin.mkdir()
    detached.mkdir()
    source = origin / "capture 𐐀.msync"
    source.write_text("""\
fn choose(flag: Bool) { if flag { [1, 2] } else { null } };
let input? = $std::io::read_line();
let selected? = choose(input is not null);
let offset = 7;
if selected is not null {
    let joined = $std::string::join($std::ranges::map(selected, fn(value: Number) {
        (value + offset) as String
    }), ",");
    joined -> ./result.txt;
    $std::io::println(joined);
} else { $std::io::println("absent"); };
""", encoding="utf-8")
    hir, lower, optimized = (origin / f"{source.stem}{suffix}" for suffix in (".mhir", ".mllvm", ".opt.mllvm"))

    invoke(cli, origin, "compile", source.name, "-O2", "-o", "direct.mso")
    invoke(cli, origin, "compile", source.name, "-O0", "-o", "direct0.mso")
    invoke(cli, origin, "analyze", source.name)
    assert invoke(cli, origin, "compile", source.name, "--emit", "hir", "-o", "-").stdout == hir.read_bytes()
    assert invoke(cli, origin, "codegen", hir.name, "-o", "-").stdout == (origin / "direct.mso").read_bytes()

    invoke(cli, origin, "lower", hir.name)
    environment = {key: value for key, value in os.environ.items() if key != "NO_COLOR"}
    raw = invoke(cli, origin, "lower", source.name, "-o", "-", "--color", "always", environment=environment).stdout
    assert raw.startswith(b"MSYNLLVM") and raw == lower.read_bytes()
    before = invoke(cli, origin, "disassemble", lower.name).stdout
    assert before.startswith(b"; msync checkpoint: lowered, O0") and b"getelementptr" in before and b"file.out" in before
    invoke(cli, origin, "optimize", lower.name, "-O2")
    colored = invoke(cli, origin, "disassemble", optimized.name, "--color", "always", environment=environment).stdout
    preview = invoke(cli, origin, "disassemble", optimized.name).stdout
    assert preview.startswith(b"; msync checkpoint: post-middle-end, O2")
    assert b"\x1b[" in colored and re.sub(rb"\x1b\[[0-9;]+m", b"", colored) == preview
    assert b"\x1b[" not in preview
    assert b"\x1b[" in invoke(cli, origin, "compile", source.name, "--color", "always", environment=environment).stdout
    assert invoke(cli, origin, "compile", source.name, "--color", "never").stdout == (
        f"Wrote {source.stem}.mso (portable program).{os.linesep}".encode()
    )

    invoke(cli, origin, "optimize", lower.name, "-O0", "-o", "post0.mllvm")
    post0 = invoke(cli, origin, "disassemble", "post0.mllvm").stdout
    assert post0.startswith(b"; msync checkpoint: post-middle-end, O0") and post0.splitlines()[1:] == before.splitlines()[1:]
    assert b"needs a lowered checkpoint" in invoke(cli, origin, "optimize", optimized.name, status=1).stderr
    assert b"would overwrite" in invoke(cli, origin, "optimize", lower.name, "-o", f"./{lower.name}", status=1).stderr
    assert b"cannot resume codegen with -O1" in invoke(cli, origin, "codegen", optimized.name, "-O1", status=1).stderr

    assert invoke(cli, origin, "codegen", lower.name, "-o", "-").stdout == (origin / "direct0.mso").read_bytes()
    assert invoke(cli, origin, "codegen", "-", "-o", "-", input_data=(origin / "post0.mllvm").read_bytes()).stdout == (
        origin / "direct0.mso"
    ).read_bytes()

    source.unlink()
    hir.unlink()
    lower.unlink()
    optimized.replace(detached / optimized.name)
    invoke(cli, detached, "codegen", optimized.name, "--trace", "resume.jsonl", "--telemetry-level", "profile")
    portable = detached / optimized.with_suffix(".mso").name
    assert portable.read_bytes() == (origin / "direct.mso").read_bytes()
    records = [cast(Json, json.loads(line)) for line in (detached / "resume.jsonl").read_text(encoding="utf-8").splitlines()]
    names = {str(item["name"]) for item in records}
    assert {"compiler.mllvm.decode.graph", "compiler.mllvm.decode.producer", "compiler.hpir.select"} <= names
    assert not names.intersection({"compile", "source.read", "compiler.parse", "compiler.lower", "compiler.llvm.lower",
                                   "compiler.optimize", "compiler.hpir.canonicalize.values", "bind", "prepare", "runtime"})
    producer, = (item for item in records if item["name"] == "compiler.mllvm.decode.producer")
    assert producer["metrics"]["producer.range_callbacks"] > 0, producer

    invoke(cli, detached, "bind", portable.name)
    executable = portable.with_suffix(".mexe")
    result = detached / "result.txt"
    assert not result.exists()
    assert invoke(cli, detached, "run", executable.name, input_data=b"present\n").stdout.splitlines() == [b"8,9"]
    assert result.read_bytes() == b"8,9"
    result.unlink()
    assert invoke(cli, detached, "run", executable.name, input_data=b"").stdout.splitlines() == [b"absent"]
    assert not result.exists()

    saved = (detached / optimized.name).read_bytes()
    for invalid, reason in ((saved[:-1], b"Regenerate"), (saved[:8] + b"\xff" + saved[9:], b"unsupported checkpoint format")):
        (detached / "invalid.mllvm").write_bytes(invalid)
        rejected = invoke(cli, detached, "codegen", "invalid.mllvm", status=1)
        assert reason in rejected.stderr and not (detached / "invalid.mso").exists(), rejected.stderr

def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Production CLI under test")
    parser.add_argument("--dotnet", type=Path, help="Optional host for the managed CLI DLL")
    parser.add_argument("--checkpoint-only", action="store_true")
    args = parser.parse_args()
    parent = ROOT / ".temp" / "hpir" / "cli"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="detached-", dir=parent) as directory:
        binary = command(args.cli, args.dotnet)
        checkpoints(binary, Path(directory))
        if not args.checkpoint_only:
            journey(binary, Path(directory))
            contextual_branch(binary, Path(directory))
            promotion(binary, Path(directory))
            buffered_input(binary, Path(directory))
            named_scope_tail(binary, Path(directory))
            transfer_destination(binary, Path(directory))
    print("PASS production CLI: detached compiler checkpoints" +
          ("" if args.checkpoint_only else ", HPIR artifacts, contextual decisions, native admission, and named Scope results"))


if __name__ == "__main__":
    main()
