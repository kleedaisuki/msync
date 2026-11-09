#!/usr/bin/env python3
"""Exercise the source binary heap against an independent priority-queue model."""

from __future__ import annotations

import argparse
import hashlib
import heapq
import json
import platform
import random
import subprocess
import tempfile
from pathlib import Path

from stdlib_experience_journey import accept, invoke

ROOT: Path = Path(__file__).resolve().parents[2]
INITIAL: tuple[int, ...] = (7, 2, 2, 9, 1, -3, 11)
NEGATIVE: tuple[tuple[str, str, str], ...] = (
    (
        "fixed-element-schema",
        (
            "let heap = $std::containers::Heap::new([1,2]); "
            '$std::containers::Heap::push(heap, "wrong");'
        ),
        "Number",
    ),
    (
        "order-return-contract",
        (
            "$std::containers::Heap::with_policy([1,2], scope { Self: Number, "
            'sample?: null, less: fn(a:Number,b:Number) { "not Bool" } });'
        ),
        "StrictOrder",
    ),
    (
        "record-order-carrier",
        (
            '$std::containers::Heap::with_policy([scope{other:"wrong"}], scope {'
            "Self:Scope, sample?:scope{priority:0}, "
            "less:fn(a:Scope,b:Scope)->Bool{a::priority < b::priority} });"
        ),
        "priority",
    ),
)


def property_program() -> tuple[str, dict[str, bytes]]:
    generator: random.Random = random.Random(0x48454150)
    model: list[int] = list(INITIAL)
    heapq.heapify(model)
    steps: list[str] = []
    trace: list[str] = []
    kinds: tuple[str, ...] = ("push", "push", "pop", "replace", "push_pop")
    for _ in range(96):
        kind: str = generator.choice(kinds)
        value: int = generator.randrange(-16, 17)
        steps.append(f'scope {{ kind: "{kind}", value: {value} }}')
        result: int | None
        if kind == "push":
            heapq.heappush(model, value)
            result = None
        elif kind == "pop":
            result = heapq.heappop(model) if model else None
        elif kind == "replace":
            if model:
                result = heapq.heapreplace(model, value)
            else:
                heapq.heappush(model, value)
                result = None
        else:
            result = heapq.heappushpop(model, value)
        trace.extend(
            (
                str(result) if result is not None else "null",
                str(model[0]) if model else "null",
                str(len(model)),
            )
        )
    expected_sorted: str = ",".join(str(value) for value in sorted(model))
    program: str = """
let Heap = $std::containers::Heap;
let queue = Heap::new(INITIAL);
let trace = "";
let valid = Heap::validate(queue);
fn record(value?) {
    let text = if value is null { "null" } else { value as String };
    trace = $std::string::concat(trace, $std::string::concat(text, ";"));
};
for step in STEPS {
    let actual? = match step::kind {
        "push" => { Heap::push(queue, step::value); null },
        "pop" => { Heap::pop(queue) },
        "replace" => { Heap::replace_top(queue, step::value) },
        _ => { Heap::push_pop(queue, step::value) },
    };
    record(actual);
    record(Heap::peek(queue));
    record(Heap::size(queue));
    valid = valid and Heap::validate(queue);
};
trace -> ./trace.txt;
(valid as String) -> ./valid.txt;
$std::string::join($std::ranges::map(Heap::sorted(queue),
    fn(value: Number) { value as String }), ",") -> ./sorted.txt;
(Heap::size(queue) as String) -> ./size-after-sorted.txt;
let before = Heap::snapshot(queue);
let before_size = Heap::size(queue);
Heap::push(queue, -100);
($std::ranges::size(before) == before_size) as String -> ./snapshot.txt;
let alias = queue;
Heap::clear(alias);
(Heap::is_empty(queue) as String) -> ./alias.txt;
((Heap::pop(queue) is null) and (Heap::peek(queue) is null)) as String
    -> ./empty.txt;
Heap::push(queue, 4);
Heap::extend(queue, [9, 1, 7, 1]);
$std::string::join($std::ranges::map(Heap::drain(queue),
    fn(value: Number) { value as String }), ",") -> ./drain.txt;
(Heap::is_empty(queue) as String) -> ./drained.txt;
let maximum = Heap::with_policy([3, 1, 4, 1, 5], scope {
    Self: Number,
    sample?: null,
    less: fn(left: Number, right: Number) -> Bool { left > right },
});
$std::string::join($std::ranges::map(Heap::drain(maximum),
    fn(value: Number) { value as String }), ",") -> ./reversed.txt;
let words = Heap::with_policy(["tiny", "a", "medium"], scope {
    Self: String,
    sample?: null,
    less: fn(left: String, right: String) -> Bool {
        $std::string::length(left) < $std::string::length(right)
    },
});
$std::string::join(Heap::drain(words), ",") -> ./words.txt;
let comparisons = 0;
let counted = Heap::with_policy($std::ranges::sequence(256, 0, -1), scope {
    Self: Number,
    sample?: null,
    less: fn(left: Number, right: Number) -> Bool {
        comparisons = comparisons + 1;
        left < right
    },
});
(comparisons <= 512) as String -> ./linear-heapify.txt;
comparisons = 0;
Heap::push(counted, 0);
(comparisons <= 9) as String -> ./logarithmic-push.txt;
let counted_top? = Heap::peek(counted);
if counted_top is not null {
    (counted_top as String) -> ./counted-top.txt;
};
let jobs = Heap::with_policy([
    scope { priority: 7, name: "later" },
    scope { priority: 1, name: "first" },
    scope { priority: 4, name: "middle" },
], scope {
    Self: Scope,
    sample?: scope { priority: 0, name: "" },
    less: fn(left: Scope, right: Scope) -> Bool {
        left::priority < right::priority
    },
});
let job? = Heap::pop(jobs);
if job is not null { job::name -> ./record-policy.txt; };
let special = Heap::new([$std::math::nan, 2, 1, $std::math::nan]);
$std::string::join($std::ranges::map(Heap::drain(special),
    fn(value: Number) {
        if $std::math::is_nan(value) { "nan" } else { value as String }
    }), ",") -> ./nan-order.txt;
let optional = Heap::new([2, null, 1]);
let optional_first? = Heap::pop(optional);
((optional_first is null) and Heap::size(optional) == 2) as String
    -> ./nullable-payload.txt;
""".replace("INITIAL", "[" + ",".join(map(str, INITIAL)) + "]").replace(
        "STEPS", "[" + ",\n".join(steps) + "]"
    )
    outputs: dict[str, bytes] = {
        "trace.txt": (";".join(trace) + ";").encode(),
        "valid.txt": b"true",
        "sorted.txt": expected_sorted.encode(),
        "size-after-sorted.txt": str(len(model)).encode(),
        "snapshot.txt": b"true",
        "alias.txt": b"true",
        "empty.txt": b"true",
        "drain.txt": b"1,1,4,7,9",
        "drained.txt": b"true",
        "reversed.txt": b"5,4,3,1,1",
        "words.txt": b"a,tiny,medium",
        "linear-heapify.txt": b"true",
        "logarithmic-push.txt": b"true",
        "counted-top.txt": b"0",
        "record-policy.txt": b"first",
        "nan-order.txt": b"1,2,nan,nan",
        "nullable-payload.txt": b"true",
    }
    return program, outputs


def execute(binary: Path, parent: Path, levels: list[int]) -> int:
    program: str
    outputs: dict[str, bytes]
    program, outputs = property_program()
    executions: int = 0
    for level in levels:
        for stage in ("source", "relocatable", "executable"):
            directory: Path = parent / f"O{level}" / stage
            directory.mkdir(parents=True)
            source: Path = directory / "priority queue 体验.msync"
            source.write_text(program, encoding="utf-8")
            accept(
                invoke(binary, directory, "check", str(source), f"-O{level}"),
                "Heap check",
            )
            if stage == "source":
                result: subprocess.CompletedProcess[str] = invoke(
                    binary, directory, "run", str(source), f"-O{level}"
                )
            else:
                artifact: Path = source.with_suffix(".mso")
                accept(
                    invoke(
                        binary,
                        directory,
                        "compile",
                        str(source),
                        "-o",
                        str(artifact),
                        f"-O{level}",
                    ),
                    "Heap compile",
                )
                if stage == "executable":
                    bound: Path = source.with_suffix(".mexe")
                    accept(
                        invoke(
                            binary,
                            directory,
                            "bind-ir",
                            str(artifact),
                            "-o",
                            str(bound),
                            "--source-file",
                            str(source),
                        ),
                        "Heap bind",
                    )
                    artifact = bound
                source.unlink()
                launch: Path = directory
                if stage == "executable":
                    launch = directory / "different launch directory"
                    launch.mkdir()
                result = invoke(binary, launch, "run-ir", str(artifact))
            accept(result, f"Heap O{level} {stage}")
            for name, expected in outputs.items():
                actual: bytes = (directory / name).read_bytes()
                assert actual == expected, (level, stage, name, actual, expected)
            executions += 1
    return executions


def negative(binary: Path, parent: Path) -> int:
    directory: Path = parent / "negative"
    directory.mkdir()
    for name, source_text, expected in NEGATIVE:
        source: Path = directory / f"{name}.msync"
        source.write_text(source_text, encoding="utf-8")
        result: subprocess.CompletedProcess[str] = invoke(
            binary, directory, "check", str(source)
        )
        assert result.returncode != 0, (name, result.stdout, result.stderr)
        assert expected in result.stderr and "error" in result.stderr.lower(), (
            name,
            result.stderr,
        )
    return len(NEGATIVE)


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", "--cli", type=Path, required=True)
    parser.add_argument("--level", type=int, choices=(0, 1, 2, 3), action="append")
    parser.add_argument("--output", type=Path)
    arguments: argparse.Namespace = parser.parse_args()
    binary: Path = arguments.binary.resolve()
    levels: list[int] = arguments.level or [2]
    scratch: Path = ROOT / ".temp/heap"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="journey-", dir=scratch) as name:
        directory: Path = Path(name)
        report: dict[str, object] = {
            "family": "$std::containers::Heap",
            "binary": str(binary),
            "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "managed_components": {
                path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(binary.parent.glob("*.dll"))
            },
            "platform": platform.platform(),
            "levels": levels,
            "staged_executions": execute(binary, directory, levels),
            "property_operations_per_execution": 96,
            "exact_outputs_per_execution": len(property_program()[1]),
            "negative_diagnostics": negative(binary, directory),
        }
    rendered: str = json.dumps(report, indent=2) + "\n"
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
