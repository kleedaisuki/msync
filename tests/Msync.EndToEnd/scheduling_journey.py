#!/usr/bin/env python3
"""Exercise live process ownership and acquire/release through the actual CLI."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
import time
from collections.abc import Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = Path(__file__).resolve()


def literal(value: str | Path) -> str:
    return json.dumps(str(value), ensure_ascii=False)


def child_call(
    directory: Path,
    name: str,
    *,
    gated: bool = False,
    peer: str | None = None,
    required_file: str | None = None,
) -> str:
    arguments = [
        str(SCRIPT),
        "--child",
        "--directory",
        str(directory),
        "--name",
        name,
    ]
    if gated:
        arguments.append("--gated")
    if peer is not None:
        arguments.extend(["--peer", peer])
    if required_file is not None:
        arguments.extend(["--required-file", required_file])
    return f"python({', '.join(literal(argument) for argument in arguments)})"


def child(
    directory: Path, name: str, gated: bool, peer: str | None, required_file: str | None
) -> int:
    (directory / f"{name}.started").write_text("started", encoding="utf-8")
    deadline = time.monotonic() + 15
    required = []
    if gated:
        required.append(directory / f"{name}.gate")
    if peer is not None:
        required.append(directory / f"{peer}.started")
    while not all(path.exists() for path in required):
        if time.monotonic() >= deadline:
            print(f"child {name}: unsatisfied gates {required}", file=sys.stderr)
            return 124
        time.sleep(0.005)
    if required_file is not None and not (directory / required_file).is_file():
        print(
            f"child {name}: required publication {required_file} is missing",
            file=sys.stderr,
        )
        return 125
    (directory / f"{name}.finished").write_text(name, encoding="utf-8")
    print(name, end="", flush=True)
    return 0


def invoke(
    binary: Path, directory: Path, arguments: Sequence[str]
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(binary), "--no-config", "--no-telemetry", *arguments],
        cwd=directory,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
        timeout=40,
    )


def after_regions_in_plan(plan: str) -> tuple[list[str], list[str]]:
    if not plan.startswith("HPIR "):
        markers: list[dict[str, str]] = [
            dict(token.split("=", 1) for token in line.split()[2:] if "=" in token)
            for line in plan.splitlines()
            if "Barrier" in line
        ]

        def references(edge: str) -> list[str]:
            return [
                marker["symbol"]
                for marker in markers
                if marker.get("a") == "2"
                and marker.get("b") == edge
                and "symbol" in marker
            ]

        return references("1"), references("0")

    imports: dict[str, str] = {}
    opens: list[str] = []
    closes: list[str] = []
    owner: str = ""
    for line in plan.splitlines():
        service = re.match(r"^import %(\d+) Work:([01]) ", line)
        if service is not None:
            imports[service[1]] = service[2]
        if line.startswith("function @"):
            owner = line.split(maxsplit=2)[1]
        call = re.match(r"^\s+\d+ Call %(\d+)\(([^)]*)\)(?: -> (\[fp\+\d+\]))?", line)
        if call is None:
            continue
        operation: str | None = imports.get(call[1])
        if operation == "0" and call[3] is not None:
            opens.append(f"{owner}:{call[3]}")
        elif operation == "1":
            closes.append(f"{owner}:{call[2]}")
    return opens, closes


def run_case(
    binary: Path,
    directory: Path,
    name: str,
    body: str,
    expected: dict[str, str],
    *,
    absent: Sequence[str] = (),
    executable: bool = False,
    after_regions: int = 0,
    inputs: dict[str, str] | None = None,
) -> None:
    directory.mkdir()
    for filename, contents in (inputs or {}).items():
        target: Path = directory / filename
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(contents, encoding="utf-8")
    source = directory / "schedule.msync"
    source.write_text(
        f"let python: Path = {literal(sys.executable)};\n{body}\n",
        encoding="utf-8",
    )
    checked = invoke(binary, directory, ["check", str(source)])
    assert checked.returncode == 0, (name, checked.stdout, checked.stderr)
    if after_regions:
        planned = invoke(binary, directory, ["plan", str(source)])
        assert planned.returncode == 0, (name, planned.stderr)
        starts, ends = after_regions_in_plan(planned.stdout)
        assert len(starts) == after_regions and len(set(starts)) == after_regions, (
            name,
            "missing or duplicated After region identity",
            starts,
        )
        assert set(starts) == set(ends), (
            name,
            "unmatched After region identity",
            starts,
            ends,
        )
    arguments = ["run", str(source)]
    if executable:
        obj = directory / "schedule.mso"
        image = directory / "schedule.mexe"
        compiled = invoke(binary, directory, ["compile", str(source), "-o", str(obj)])
        assert compiled.returncode == 0, (name, compiled.stderr)
        bound = invoke(binary, directory, ["bind-ir", str(obj), "-o", str(image)])
        assert bound.returncode == 0, (name, bound.stderr)
        arguments = ["run-ir", str(image)]
    try:
        result = invoke(binary, directory, arguments)
        assert result.returncode == 0, (name, result.stdout, result.stderr)
        for filename, contents in expected.items():
            path = directory / filename
            assert path.is_file(), (name, "missing", filename, result.stderr)
            assert path.read_text(encoding="utf-8") == contents, (
                name,
                filename,
                path.read_text(encoding="utf-8"),
                contents,
            )
        for filename in absent:
            assert not (directory / filename).exists(), (name, filename)
    finally:
        # Test-owned gates also release a child when an assertion interrupts its owner.
        for child_name in ("first", "second", "owned", "background"):
            (directory / f"{child_name}.gate").touch()


def journeys(
    binary: Path, directory: Path, executable: bool, selected_cases: set[str]
) -> int:
    count = 0

    def case(
        name: str,
        body: str,
        expected: dict[str, str],
        *,
        absent: Sequence[str] = (),
        after_regions: int = 0,
        inputs: dict[str, str] | None = None,
    ) -> None:
        nonlocal count
        if selected_cases and name not in selected_cases:
            return
        run_case(
            binary,
            directory / name,
            name,
            body,
            expected,
            absent=absent,
            executable=executable,
            after_regions=after_regions,
            inputs=inputs,
        )
        count += 1

    case(
        "callback-acquire",
        "let items = $std::ranges::transform([./input], fn(path) { acquire; path }); items -> ./out;",
        {"out/input": "payload"},
        inputs={"input": "payload", "out/.keep": ""},
    )
    inherited_child: str = "import pathlib,time; time.sleep(0.2); pathlib.Path('prior.finished').write_text('prior')"
    case(
        "callback-inherited-acquire",
        f'let prior = release python("-c", {literal(inherited_child)});\n'
        "let items = $std::ranges::transform([./input], fn(path) { acquire; ./prior.finished }); items -> ./out;",
        {"out/prior.finished": "prior"},
        inputs={"input": "payload", "out/.keep": ""},
    )
    callback_child: str = "import pathlib,time; time.sleep(0.2); pathlib.Path('child.finished').write_text('finished')"
    case(
        "returned-callback-owner",
        'let state = object { child: python("-c", "pass") };\n'
        "let items = $std::ranges::transform([./input], fn(path) { "
        f'state::child = release python("-c", {literal(callback_child)}); path }});\n'
        "fn submit() { items -> ./out }; let result = submit(); acquire;\n"
        "./child.finished -> ./observed;",
        {"out/input": "payload", "observed": "finished"},
        inputs={"input": "payload", "out/.keep": ""},
    )

    ordinary = directory / "ordinary"
    case(
        "ordinary",
        f"let child = {child_call(ordinary, 'ordinary')};\n"
        "./ordinary.finished -> ./observed; child::stdout -> ./stdout;",
        {"observed": "ordinary", "stdout": "ordinary"},
    )
    combined = directory / "combined"
    case(
        "combined",
        f"let child = acqrel {child_call(combined, 'combined')};\n"
        "./combined.finished -> ./observed; child::stdout -> ./stdout;",
        {"observed": "combined", "stdout": "combined"},
    )
    overlap = directory / "overlap"
    case(
        "overlap",
        f"let first = release {child_call(overlap, 'first', gated=True, peer='second')};\n"
        f"let second = release {child_call(overlap, 'second', gated=True, peer='first')};\n"
        '"open" -> ./first.gate; "open" -> ./second.gate;\n'
        "acquire; first::stdout -> ./first.stdout; second::stdout -> ./second.stdout;",
        {"first.stdout": "first", "second.stdout": "second"},
    )
    port = directory / "deferred-port"
    case(
        "deferred-port",
        f"let background = release {child_call(port, 'background', gated=True)};\n"
        'background::stdout -> ./stdout; "open" -> ./background.gate; acquire background;',
        {"stdout": "background", "background.finished": "background"},
    )
    flow = directory / "deferred-dataflow"
    case(
        "deferred-dataflow",
        f"let background = release {child_call(flow, 'background', gated=True)};\n"
        'let message = $std::string::concat(background::stdout, " tail"); message -> ./stdout;\n'
        '"open" -> ./background.gate; acquire background;',
        {"stdout": "background tail", "background.finished": "background"},
    )
    arguments = directory / "deferred-process-argument"
    case(
        "deferred-process-argument",
        f"let background = release {child_call(arguments, 'background', gated=True)};\n"
        'let consumer = release python("-c", "import sys; sys.stdout.write(sys.argv[1])", background::stdout);\n'
        '"open" -> ./background.gate; acquire consumer; consumer::stdout -> ./forwarded;',
        {"forwarded": "background", "background.finished": "background"},
    )
    returned = directory / "returned-process-submission"
    case(
        "returned-process-submission",
        f"let background = release {child_call(returned, 'background', gated=True)};\n"
        'fn submit(message: String) { release python("-c", "import sys; sys.stdout.write(sys.argv[1])", message) };\n'
        'let message = background::stdout; let consumer = submit(message); message = "changed";\n'
        '"open" -> ./background.gate; acquire; consumer::stdout -> ./forwarded;',
        {"forwarded": "background", "background.finished": "background"},
    )
    native = directory / "native-independent"
    case(
        "native-independent",
        '"known" -> ./known;\n'
        f"let background = release {child_call(native, 'background', gated=True)};\n"
        "background::stdout -> ./stdout;\n"
        'if exists ./known { "open" -> ./background.gate }; acquire background;',
        {"stdout": "background", "background.finished": "background"},
    )
    selected = directory / "selected"
    case(
        "selected",
        f"let first = release {child_call(selected, 'first', gated=True)};\n"
        f"let second = release {child_call(selected, 'second', gated=True)};\n"
        '"open" -> ./first.gate; acquire first;\n'
        '"acquired first" -> ./selected; "open" -> ./second.gate; acquire second;\n'
        "first::stdout -> ./first.stdout; second::stdout -> ./second.stdout;",
        {
            "selected": "acquired first",
            "first.stdout": "first",
            "second.stdout": "second",
        },
    )
    owner = directory / "owner"
    case(
        "owner",
        "fn work() {\n"
        f"let owned = release {child_call(owner, 'owned', gated=True)};\n"
        '"open" -> ./owned.gate;\n};\nwork(); ./owned.finished -> ./observed;',
        {"observed": "owned"},
    )
    inherited = directory / "inherited-acquire"
    case(
        "inherited-acquire",
        f"let background = release {child_call(inherited, 'background', gated=True)};\n"
        'fn join-parent() { "open" -> ./background.gate; acquire; ./background.finished -> ./observed };\n'
        "join-parent();",
        {"observed": "background"},
    )
    after = directory / "after-region"
    case(
        "after-region",
        f"let background = release {child_call(after, 'background', gated=True)};\n"
        '"open" -> ./background.gate; acquire background::stdout -> ./stdout;\n'
        f"let verifier = {child_call(after, 'verifier', required_file='stdout')}; verifier::stdout -> ./verified;",
        {"stdout": "background", "verified": "verifier"},
        after_regions=1,
    )
    broken = directory / "acquire-break"
    case(
        "acquire-break",
        f"let background = release {child_call(broken, 'background', gated=True)};\n"
        'acquire loop { acquire { background::stdout -> ./stdout; "open" -> ./background.gate; break; }; };\n'
        f"let verifier = {child_call(broken, 'verifier', required_file='stdout')}; verifier::stdout -> ./verified;",
        {"stdout": "background", "verified": "verifier"},
        after_regions=2,
    )
    branch = directory / "unselected"
    case(
        "unselected",
        f"if false {{ let background = release {child_call(branch, 'background')}; }};\n"
        '"selected" -> ./observed;',
        {"observed": "selected"},
        absent=("background.started", "background.finished"),
    )
    case(
        "resource-order",
        '"first" -> ./shared; ./shared -> ./first;\n'
        '"second" -> ./shared; ./shared -> ./second;',
        {"shared": "second", "first": "first", "second": "second"},
    )
    return count


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--child", action="store_true")
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--name")
    parser.add_argument("--gated", action="store_true")
    parser.add_argument("--peer")
    parser.add_argument("--required-file")
    parser.add_argument("--executable", action="store_true")
    parser.add_argument("--case", action="append", default=[])
    args = parser.parse_args()
    if args.child:
        assert args.directory is not None and args.name is not None
        return child(
            args.directory, args.name, args.gated, args.peer, args.required_file
        )
    assert args.binary is not None, "--binary is required for the CLI journey"
    scratch = ROOT / ".temp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="scheduling-journey-", dir=scratch) as path:
        count = journeys(
            args.binary.resolve(), Path(path), args.executable, set(args.case)
        )
    print(
        f"PASS: {count} real-process scheduling journeys; "
        f"Python fixture interpreter: {sys.executable}; "
        f"{'bound executable' if args.executable else 'source'}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
