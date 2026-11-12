"""A bounded-demand telemetry processing workflow using real streamed input."""

from __future__ import annotations

from itertools import pairwise
from pathlib import Path
from random import Random

from model import Case, Fixture

SOURCE: str = """\
let r = $std::ranges;
let header? = $std::io::read_line();
if header? {
    $std::io::println("missing header");
} else {
    let limit = header as Number;
    let readings = r::transform($std::io::stdin_lines(), fn(line: String) { line as Number });
    let accepted = r::take(r::filter(readings, fn(value: Number) { value >= 0 }), limit);
    let previous? = null;
    let count = 0;
    let changes = 0;
    let checksum = 0;
    for value in accepted {
        if previous is not null {
            let delta = value - previous;
            if delta > 4 or delta < -4 { changes = changes + 1; };
            checksum = checksum + ((count % 29) + 1) * delta;
        };
        previous = value;
        count = count + 1;
    };
    $std::io::println(count as String);
    $std::io::println(changes as String);
    $std::io::println(checksum as String);
};
"""


def stream(directory: Path, size: int, seed: int) -> Fixture:
    rng: Random = Random(seed)
    value: int = rng.randrange(250)
    readings: list[int] = []
    for i in range(size * 2):
        if i % 61 == 0:
            value = rng.randrange(250)
        else:
            value = max(0, min(249, value + rng.randrange(-6, 7)))
        readings.append(-1 if rng.randrange(10) < 2 else value)
    accepted: list[int] = [reading for reading in readings if reading >= 0][:size]
    deltas: list[int] = [current - previous for previous, current in pairwise(accepted)]
    changes: int = sum(abs(delta) > 4 for delta in deltas)
    checksum: int = sum((i % 29 + 1) * delta for i, delta in enumerate(deltas, 1))
    return Fixture(
        SOURCE,
        "\n".join(map(str, (size, *readings))) + "\n",
        f"{len(accepted)}\n{changes}\n{checksum}\n",
    )


RULE_SOURCE: str = """\
let io = $std::io;
let r = $std::ranges;
let bias = 7;
let corrections = 0;
let normalize = fn(value: Number) { value + bias };
let correct = fn(value: Number) {
    corrections = corrections + 1;
    if value > 120 { 120 } else { value }
};
let scale = fn(value: Number) { value * 3 + 1 };
let rules = [normalize, correct, scale];
let checksum = 0;
let processed = 0;
for line in io::stdin_lines() {
    let record = line as Number;
    let kind = record % 10;
    let value = (record - kind) / 10;
    if kind == 9 {
        bias = value % 41;
    } else {
        let index = if kind < 5 { 0 } else { if kind < 8 { 1 } else { 2 } };
        let policy = r::at(rules, index);
        let result = policy(value) as Number;
        checksum = (checksum * 131 + result) % 1000003;
        processed = processed + 1;
    };
};
io::println(processed as String);
io::println(corrections as String);
io::println(checksum as String);
"""


def rules(_directory: Path, size: int, seed: int) -> Fixture:
    rng: Random = Random(seed)
    records: list[int] = []
    bias: int = 7
    checksum = processed = corrections = 0
    for i in range(size):
        value: int = rng.randrange(1_000)
        kind: int = 9 if i % 113 == 0 else rng.randrange(9)
        records.append(10 * value + kind)
        if kind == 9:
            bias = value % 41
            continue
        if kind < 5:
            result: int = value + bias
        elif kind < 8:
            result = min(120, value)
            corrections += 1
        else:
            result = value * 3 + 1
        checksum = (checksum * 131 + result) % 1_000_003
        processed += 1
    return Fixture(
        source=RULE_SOURCE,
        stdin="\n".join(map(str, records)) + "\n",
        stdout=f"{processed}\n{corrections}\n{checksum}\n",
    )


def cases() -> tuple[Case, ...]:
    return (
        Case(
            "bounded-sensor-pipeline",
            "lazy-pipeline",
            "Parse, filter and take a bounded prefix of a streamed sensor series",
            12_000,
            stream,
        ),
        Case(
            "live-policy-dispatch",
            "polymorphic-dispatch",
            "Dispatch a mixed rule stream through three live closures and update captured configuration",
            28_000,
            rules,
        ),
    )
