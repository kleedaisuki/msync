"""Numeric and stateful control-flow workloads with exact independent oracles."""

from __future__ import annotations

from collections.abc import Iterator
from fractions import Fraction
from functools import partial
from itertools import islice
from math import floor
from pathlib import Path

from model import Case, Fixture


def _states(seed: int) -> Iterator[int]:
    state: int = seed % 65536
    while True:
        state = (25173 * state + 13849) % 65536
        yield state


def _stdin(size: int, seed: int) -> str:
    return f"{size}\n{seed % 65536}\n"


_INPUT: str = """\
fn read_number() -> Number {
    let line? = $std::io::read_line();
    if line? { return 0; };
    line as Number
};
let count = read_number();
let state = read_number();
fn draw() -> Number {
    global state;
    state = (25173 * state + 13849) % 65536;
    state
};
"""

# A simple, concave polygon with mixed edge directions and horizontal edges.
_POLYGON: tuple[tuple[int, int], ...] = (
    (-910, -400),
    (-550, -850),
    (50, -850),
    (760, -620),
    (930, -100),
    (450, 80),
    (870, 540),
    (450, 900),
    (30, 490),
    (-380, 900),
    (-890, 580),
    (-400, 130),
)


def _polygon(directory: Path, size: int, seed: int) -> Fixture:
    del directory
    edges: tuple[tuple[tuple[int, int], tuple[int, int]], ...] = tuple(
        zip(_POLYGON, (*_POLYGON[1:], _POLYGON[0]), strict=True)
    )
    rng: Iterator[int] = _states(seed)
    hits: int = 0
    checksum: int = 0
    for index in range(1, size + 1):
        x, y = next(rng) // 32 - 1024, next(rng) // 32 - 1024
        # Reference uses exact rational intersection coordinates. The guest
        # uses signed cross-products and never divides by a segment's rise.
        crossings = sum(
            Fraction(ax) + Fraction((y - ay) * (bx - ax), by - ay) > x
            for (ax, ay), (bx, by) in edges
            if min(ay, by) <= y < max(ay, by)
        )
        if crossings % 2:
            hits += 1
            checksum = (checksum + index) % 1000003
    rows: str = "\n".join(
        f"    scope {{ ax: {ax}, ay: {ay}, bx: {bx}, by: {by} }},"
        for (ax, ay), (bx, by) in edges
    )
    source: str = (
        _INPUT
        + "let edges = [\n"
        + rows
        + "\n];\n"
        + """\
fn crossed(edge, x: Number, y: Number) -> Bool {
    let ay = edge::ay;
    let by = edge::by;
    if (ay > y) == (by > y) { return false; };
    let ax = edge::ax;
    let dy = by - ay;
    let lhs = (x - ax) * dy;
    let rhs = (edge::bx - ax) * (y - ay);
    if dy > 0 { lhs < rhs } else { lhs > rhs }
};
let hits = 0;
let checksum = 0;
for ordinal in $std::ranges::iota(1, count + 1) {
    let x = $std::math::floor(draw() / 32) - 1024;
    let y = $std::math::floor(draw() / 32) - 1024;
    let inside = false;
    for edge in edges {
        if crossed(edge, x, y) { inside = not inside; };
    };
    if inside {
        hits = hits + 1;
        checksum = (checksum + ordinal) % 1000003;
    };
};
$std::io::println(hits as String);
$std::io::println(checksum as String);
"""
    )
    return Fixture(
        source=source, stdin=_stdin(size, seed), stdout=f"{hits}\n{checksum}\n"
    )


_POLICIES: tuple[tuple[int, int, int], ...] = (
    (4, 1240, 1040),
    (8, 1540, 1170),
    (16, 1810, 1280),
)


def _signal(seed: int, size: int, period: int, start: int, end: int) -> list[int]:
    return [
        draw % 101
        + (96 if start <= ordinal % period < end else 0)
        + (54 if draw % 37 == 0 else 0)
        for ordinal, draw in enumerate(islice(_states(seed), size))
    ]


def _detector(samples: list[int], divisor: int, high: int, low: int) -> tuple[int, int]:
    average: int = 0
    active: bool = False
    transitions: int = 0
    exposure: int = 0
    for index, sample in enumerate(samples, start=1):
        # This form computes the complete weighted numerator, unlike the
        # correction-to-previous-value form in the guest.
        average = ((divisor - 1) * average + 16 * sample) // divisor
        updated = average >= (low if active else high)
        if updated != active:
            transitions += 1
        active = updated
        if active:
            exposure = (exposure + average + index) % 1000003
    return transitions, exposure


def _hysteresis(
    directory: Path,
    size: int,
    seed: int,
    *,
    period: int = 128,
    start: int = 64,
    end: int = 96,
) -> Fixture:
    del directory
    samples: list[int] = _signal(seed, size, period, start, end)
    expected: str = "".join(
        f"{changes}\n{exposure}\n"
        for changes, exposure in (_detector(samples, *policy) for policy in _POLICIES)
    )
    definitions: str = "\n".join(
        f"let detector_{index} = detector({divisor}, {high}, {low});"
        for index, (divisor, high, low) in enumerate(_POLICIES)
    )
    calls: str = "\n".join(
        f"    detector_{index}::ingest(sample, ordinal + 1);"
        for index in range(len(_POLICIES))
    )
    summaries: str = "\n".join(
        f"detector_{index}::report();" for index in range(len(_POLICIES))
    )
    source: str = (
        _INPUT
        + """\
fn detector(divisor: Number, high: Number, low: Number) {
    let average = 0;
    let active = false;
    let changes = 0;
    let exposure = 0;
    fn ingest(sample: Number, ordinal: Number) {
        global average, active, changes, exposure;
        average = average + $std::math::floor((sample * 16 - average) / divisor);
        if active {
            if average < low {
                active = false;
                changes = changes + 1;
            };
        } else {
            if average >= high {
                active = true;
                changes = changes + 1;
            };
        };
        if active { exposure = (exposure + average + ordinal) % 1000003; };
    };
    fn report() {
        $std::io::println(changes as String);
        $std::io::println(exposure as String);
    };
    scope { ingest: ingest, report: report }
};
"""
        + definitions
        + """
for ordinal in $std::ranges::iota(0, count) {
    let draw_value = draw();
    let phase = ordinal % 128;
    let burst = if phase >= 64 and phase < 96 { 96 } else { 0 };
    let spike = if draw_value % 37 == 0 { 54 } else { 0 };
    let sample = draw_value % 101 + burst + spike;
"""
        + calls
        + "\n};\n"
        + summaries
        + "\n"
    )
    source = source.replace("ordinal % 128", f"ordinal % {period}").replace(
        "phase >= 64 and phase < 96", f"phase >= {start} and phase < {end}"
    )
    return Fixture(
        source=source,
        stdin=_stdin(size, seed),
        stdout=expected,
        distribution={
            "burst_period": period,
            "burst_length": end - start,
            "detector_transitions": sum(int(n) for n in expected.splitlines()[::2]),
        },
    )


_NUMERIC_RECORDS = """\
let input? = $std::io::read_line();
let state = if input is null { 1 } else { input as Number };
let limit? = $std::io::read_line();
let count = if limit is null { 4096 } else { limit as Number };
fn make_row(tick: Number) {
    state = (state * 48271) % 2147483647;
    let low = state % 4096;
    let high = low + (state % 31);
    scope { low: low, high: high, keep: tick % 7 != 0 }
};
let rows = $std::ranges::materialize($std::ranges::transform($std::ranges::iota(0, count), make_row));
let selected = 0;
let widths = 0;
let checksum = 0;
for row in rows {
    if row::keep {
        selected = selected + 1;
        widths = widths + row::high - row::low;
        checksum = (checksum * 65599 + row::low + row::high) % 1000003;
    };
};
$std::io::println(selected as String);
$std::io::println(widths as String);
$std::io::println(checksum as String);
"""


def _numeric_records(
    directory: Path, size: int, seed: int, *, sparse: bool = False
) -> Fixture:
    del directory
    initial: int = seed % (2_147_483_647 - 1) or 1
    state: int = initial
    accepted: list[tuple[int, int]] = []
    for tick in range(size):
        state = state * 48_271 % 2_147_483_647
        if (tick % 7 == 0) == sparse:
            lower = state % 4096
            accepted.append((lower, lower + state % 31))
    checksum: int = 0
    for lower, upper in accepted:
        checksum = (checksum * 65_599 + lower + upper) % 1_000_003
    width: int = sum(upper - lower for lower, upper in accepted)
    return Fixture(
        source=_NUMERIC_RECORDS.replace("tick % 7 != 0", "tick % 7 == 0")
        if sparse
        else _NUMERIC_RECORDS,
        distribution={"materialized_rows": size, "selected_rows": len(accepted)},
        stdin=f"{initial}\n{size}\n",
        stdout=f"{len(accepted)}\n{width}\n{checksum}\n",
    )


def _bands(directory: Path, size: int, seed: int) -> Fixture:
    del directory
    base: tuple[tuple[int, int, int], ...] = (
        (-921, -610, 3),
        (-650, -470, 7),
        (-505, -190, 2),
        (-240, -20, 5),
        (-90, 248, 4),
        (170, 480, 6),
        (431, 750, 9),
    )
    moving: tuple[int, int, int] = (510, 790, 8)
    state: Iterator[int] = _states(seed)
    hits: int = 0
    checksum: int = 0
    for ordinal in range(1, size + 1):
        sample: int = next(state) // 32 - 1024
        if ordinal % 31 == 0:
            moving = (sample - 111, sample + 233, 8)
        scores: list[int] = [
            upper - lower + (sample - lower) * (gain - 1)
            for lower, upper, gain in (*base, moving)
            if lower <= sample < upper
        ]
        hits += len(scores)
        for score in scores:
            checksum = (checksum * 131 + score + ordinal) % 1_000_003
    rows: str = "\n".join(
        f"    scope {{ lower: {low}, upper: {high}, gain: {gain} }},"
        for low, high, gain in base
    )
    source: str = (
        _INPUT
        + "let moving = scope { lower: 510, upper: 790, gain: 8 };\nlet bands = [\n"
        + rows
        + "\n    moving,\n];\n"
        + """\
fn classify(band, sample: Number) -> Number {
    let low = band::lower;
    let high = band::upper;
    if (low > sample) == (high > sample) { return 0; };
    let gain = band::gain;
    (sample - low) * gain + (high - sample)
};
let hits = 0;
let checksum = 0;
for ordinal in $std::ranges::iota(1, count + 1) {
    let sample = $std::math::floor(draw() / 32) - 1024;
    if ordinal % 31 == 0 {
        moving::lower = sample - 111;
        moving::upper = sample + 233;
    };
    for band in bands {
        let score = classify(band, sample);
        if score > 0 {
            hits = hits + 1;
            checksum = (checksum * 131 + score + ordinal) % 1000003;
        };
    };
};
$std::io::println(hits as String);
$std::io::println(checksum as String);
"""
    )
    return Fixture(
        source=source, stdin=_stdin(size, seed), stdout=f"{hits}\n{checksum}\n"
    )


def _segment_screening(directory: Path, size: int, seed: int) -> Fixture:
    del directory
    static: tuple[tuple[int, int, int, int], ...] = (
        (-610, -910, -360, 760),
        (-421, 790, -230, -680),
        (-215, -614, -28, 711),
        (97, 550, 206, -482),
        (260, -533, 420, 834),
        (518, 853, 692, -710),
        (99, 0, 650, 0),
    )
    moving: tuple[int, int, int, int] = (713, -713, 802, 661)
    state: Iterator[int] = _states(seed)
    hits: int = 0
    checksum: int = 0
    for ordinal in range(1, size + 1):
        x: int = next(state) // 32 - 1024
        draw: int = next(state)
        y: int = draw // 32 - 1024 if ordinal % 4 == 0 else draw % 256 - 128
        if ordinal % 23 == 0:
            moving = (x - 127, -713, x + 191, 661)
        passed: int = sum(
            Fraction(x0) + Fraction((y - y0) * (x1 - x0), y1 - y0) > x
            for x0, y0, x1, y1 in (*static, moving)
            if min(y0, y1) <= y < max(y0, y1)
        )
        hits += passed
        checksum = (checksum * 131 + passed * ordinal) % 1_000_003
    rows: str = "\n".join(
        f"    scope {{ start_x: {x0}, start_y: {y0}, stop_x: {x1}, stop_y: {y1} }},"
        for x0, y0, x1, y1 in static
    )
    source: str = (
        _INPUT
        + "let moving = scope { start_x: 713, start_y: -713, stop_x: 802, stop_y: 661 };\n"
        + "let edges = [\n"
        + rows
        + "\n    moving,\n];\n"
        + """\
fn screen(edge, x: Number, y: Number) -> Bool {
    let start_y = edge::start_y;
    let stop_y = edge::stop_y;
    if (start_y > y) == (stop_y > y) { return false; };
    let start_x = edge::start_x;
    let rise = stop_y - start_y;
    let left = (x - start_x) * rise;
    let right = (edge::stop_x - start_x) * (y - start_y);
    if rise > 0 { left < right } else { left > right }
};
let hits = 0;
let checksum = 0;
for ordinal in $std::ranges::iota(1, count + 1) {
    let x = $std::math::floor(draw() / 32) - 1024;
    let observation = draw();
    let y = if ordinal % 4 == 0 {
        $std::math::floor(observation / 32) - 1024
    } else { observation % 256 - 128 };
    if ordinal % 23 == 0 {
        moving::start_x = x - 127;
        moving::stop_x = x + 191;
    };
    let passed = 0;
    for edge in edges {
        if screen(edge, x, y) { passed = passed + 1; };
    };
    hits = hits + passed;
    checksum = (checksum * 131 + passed * ordinal) % 1000003;
};
$std::io::println(hits as String);
$std::io::println(checksum as String);
"""
    )
    return Fixture(
        source=source, stdin=_stdin(size, seed), stdout=f"{hits}\n{checksum}\n"
    )


def _literal_segment_parity(directory: Path, size: int, seed: int) -> Fixture:
    del directory
    segments: list[tuple[int, int, int, int]] = []
    for i in range(19):
        x0: int = -884 + 87 * i
        x1: int = x0 + 99 - 97 * (i % 3)
        low: int = -940 + 41 * (i % 7)
        high: int = 680 + 27 * (i % 6)
        if i % 7 == 0:
            low = high = 64
        y0, y1 = (low, high) if i % 2 == 0 else (high, low)
        segments.append((x0, y0, x1, y1))

    rng: Iterator[int] = _states(seed)
    hits: int = 0
    checksum: int = 0
    for ordinal in range(1, size + 1):
        x: int = next(rng) // 32 - 1024
        sample: int = next(rng)
        y: int = sample // 32 - 1024 if ordinal % 4 == 0 else sample % 256 - 128
        count: int = sum(
            Fraction(ax) + Fraction((y - ay) * (bx - ax), by - ay) > x
            for ax, ay, bx, by in segments
            if min(ay, by) <= y < max(ay, by)
        )
        if (count + (ordinal % 5 == 0)) % 2:
            hits += 1
            checksum = (checksum * 131 + ordinal) % 1_000_003

    rows: str = "\n".join(
        f'    scope {{ west_x: {ax}, west_y: {ay}, east_x: {bx}, east_y: {by}, label: "row-{i}" }},'
        for i, (ax, ay, bx, by) in enumerate(segments)
    )
    source: str = (
        _INPUT
        + "let segments = [\n"
        + rows
        + "\n];\n"
        + """\
fn screens(row, x: Number, y: Number) -> Bool {
    let west_y = row::west_y;
    let east_y = row::east_y;
    if (west_y > y) == (east_y > y) { return false; };
    let west_x = row::west_x;
    let rise = east_y - west_y;
    let left = (x - west_x) * rise;
    let right = (row::east_x - west_x) * (y - west_y);
    if rise > 0 { left < right } else { left > right }
};
let hits = 0;
let checksum = 0;
for ordinal in $std::ranges::iota(1, count + 1) {
    let x = $std::math::floor(draw() / 32) - 1024;
    let reading = draw();
    let y = if ordinal % 4 == 0 {
        $std::math::floor(reading / 32) - 1024
    } else { reading % 256 - 128 };
    let odd = ordinal % 5 == 0;
    for row in segments {
        if screens(row, x, y) { odd = not odd; };
    };
    if odd {
        hits = hits + 1;
        checksum = (checksum * 131 + ordinal) % 1000003;
    };
};
$std::io::println(hits as String);
$std::io::println(checksum as String);
"""
    )
    return Fixture(
        source=source, stdin=_stdin(size, seed), stdout=f"{hits}\n{checksum}\n"
    )


def _ordered_calibration(directory: Path, size: int, seed: int) -> Fixture:
    del directory
    coefficients: tuple[tuple[float, float, float], ...] = tuple(
        ((895 + i % 7) / 1024, ((31 * i) % 151 - 75) / 256, ((19 * i) % 97 - 48) / 128)
        for i in range(129)
    )
    state: Iterator[int] = _states(seed)
    positives: int = 0
    checksum: int = 0
    for ordinal in range(1, size + 1):
        sample: float = next(state) / 65536 - 0.5
        accumulator: float = ordinal % 17 / 32 - 0.25
        for decay, offset, gain in coefficients:
            retained: float = accumulator * decay
            correction: float = (sample - offset) * gain
            accumulator = retained + correction
        bucket: int = floor(accumulator * 1048576)
        positives += bucket >= 0
        checksum = (checksum * 131 + bucket + 5_000_000) % 1_000_003

    rows: str = "\n".join(
        f"    scope {{ decay: {decay!r}, offset: {offset!r}, gain: {gain!r} }},"
        for decay, offset, gain in coefficients
    )
    source: str = (
        _INPUT
        + "let coefficients = [\n"
        + rows
        + "\n];\n"
        + """\
let positives = 0;
let checksum = 0;
for ordinal in $std::ranges::iota(1, count + 1) {
    let sample = draw() / 65536 - 0.5;
    let score = (ordinal % 17) / 32 - 0.25;
    for row in coefficients {
        score = score * row::decay + (sample - row::offset) * row::gain;
    };
    let bucket = $std::math::floor(score * 1048576);
    if bucket >= 0 { positives = positives + 1; };
    checksum = (checksum * 131 + bucket + 5000000) % 1000003;
};
$std::io::println(positives as String);
$std::io::println(checksum as String);
"""
    )
    return Fixture(
        source=source, stdin=_stdin(size, seed), stdout=f"{positives}\n{checksum}\n"
    )


def cases() -> tuple[Case, ...]:
    return (
        Case(
            "polygon-queries",
            "compute",
            "Seeded point-in-polygon queries over a concave Scope-edge polygon",
            16384,
            _polygon,
        ),
        Case(
            "signal-hysteresis",
            "compute",
            "Three fixed-point EWMA detectors with independent mutable captures",
            65536,
            _hysteresis,
            ("long-bursts", partial(_hysteresis, period=1024, start=256, end=768)),
        ),
        Case(
            "numeric-record-stream",
            "compute",
            "Build and consume numeric interval rows with a nonnumeric filter flag",
            32768,
            _numeric_records,
            ("sparse-selection", partial(_numeric_records, sparse=True)),
        ),
        Case(
            "band-classification",
            "compute",
            "Classify seeded samples against eight calibrated bands including a moving Scope",
            8192,
            _bands,
        ),
        Case(
            "segment-screening",
            "compute",
            "Screen open sloped segments against biased samples with live endpoint edits",
            6144,
            _segment_screening,
        ),
        Case(
            "literal-segment-parity",
            "compute",
            "Fold crossing parity over nineteen closed literal rows with a dynamic initial flag",
            4096,
            _literal_segment_parity,
        ),
        Case(
            "ordered-calibration",
            "compute",
            "Apply a varying binary64 recurrence over 129 literal calibration rows",
            2048,
            _ordered_calibration,
        ),
    )
