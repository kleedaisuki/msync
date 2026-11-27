"""Mixed collection workloads with independent Python data-structure oracles."""

from __future__ import annotations

import heapq
import random
from collections.abc import Iterator
from functools import partial
from pathlib import Path

from model import Case, Fixture

RANDOM_MODULUS: int = 2_147_483_647
CHECKSUM_MODULUS: int = 1_000_003
CHECKSUM_FACTOR: int = 65_599


def _seed(seed: int) -> int:
    return seed % (RANDOM_MODULUS - 1) + 1


def _draws(state: int) -> Iterator[int]:
    # All products are exact in binary64; the oracle uses Python integers.
    while True:
        state = state * 48_271 % RANDOM_MODULUS
        yield state


def _checksum(current: int, value: int) -> int:
    return (current * CHECKSUM_FACTOR + value) % CHECKSUM_MODULUS


def _lines(values: tuple[int, ...]) -> str:
    return "".join(f"{value}\n" for value in values)


INVENTORY: str = """let Index = $std::containers::BPlusTree;
let index = Index::of_types(Number, Number);
let initial = INITIAL_SIZE;
let operations = TRACE_SIZE;
for slot in $std::ranges::iota(0, initial) {
    let key = if slot % 2 == 0 { slot / 2 } else { initial - 1 - (slot - 1) / 2 };
    Index::insert(index, key, (key * 17 + 23) % 10007);
};

let input? = $std::io::read_line();
let rng = if input is null { 1 } else { input as Number };
let hits = 0;
let misses = 0;
let additions = 0;
let replacements = 0;
let deletes = 0;
let trace = 0;
for _ in $std::ranges::iota(0, operations) {
    rng = rng * 48271 % 2147483647;
    let action = rng % 10;
    rng = rng * 48271 % 2147483647;
    let key_limit = if rng % 10 < 7 { initial / 4 } else { initial * 3 };
    rng = rng * 48271 % 2147483647;
    let key = rng % key_limit;
    rng = rng * 48271 % 2147483647;
    let value = rng % 10007;
    if action < 6 {
        if Index::contains(index, key) {
            hits = hits + 1;
            trace = (trace * 65599 + key + 5) % 1000003;
        } else {
            misses = misses + 1;
            trace = (trace * 65599 + key + 3) % 1000003;
        };
    } else {
        if action < 9 {
            if Index::insert(index, key, value) {
                additions = additions + 1;
                trace = (trace * 65599 + key + value * 7 + 11) % 1000003;
            } else {
                replacements = replacements + 1;
                trace = (trace * 65599 + key + value * 7 + 13) % 1000003;
            };
        } else {
            if Index::remove(index, key) {
                deletes = deletes + 1;
                trace = (trace * 65599 + key + 17) % 1000003;
            } else {
                trace = (trace * 65599 + key + 19) % 1000003;
            };
        };
    };
};

let inventory = $std::ranges::size(Index::entries(index));
let hot = $std::ranges::size(Index::range(index, 0, initial / 4));
let cold = $std::ranges::size(Index::range(index, initial, initial * 3));
$std::io::println(hits as String);
$std::io::println(misses as String);
$std::io::println(additions as String);
$std::io::println(replacements as String);
$std::io::println(deletes as String);
$std::io::println(Index::size(index) as String);
$std::io::println(trace as String);
$std::io::println(inventory as String);
$std::io::println(hot as String);
$std::io::println(cold as String);
CHECKPOINTS
"""


def _inventory(_directory: Path, size: int, seed: int) -> Fixture:
    # Multiple real pages even at tiny smoke-test sizes, with exact quartering.
    initial: int = max(16, (size // 16) * 4)
    state: int = _seed(seed)
    draws: Iterator[int] = _draws(state)
    items: dict[int, int] = {key: (key * 17 + 23) % 10_007 for key in range(initial)}
    hits: int = 0
    misses: int = 0
    additions: int = 0
    replacements: int = 0
    deletes: int = 0
    trace: int = 0
    for _ in range(size):
        action: int = next(draws) % 10
        key_limit: int = initial // 4 if next(draws) % 10 < 7 else initial * 3
        key: int = next(draws) % key_limit
        value: int = next(draws) % 10_007
        if action < 6:
            if key in items:
                hits += 1
                contribution = key + 5
            else:
                misses += 1
                contribution = key + 3
        elif action < 9:
            if key in items:
                replacements += 1
                contribution = key + value * 7 + 13
            else:
                additions += 1
                contribution = key + value * 7 + 11
            items[key] = value
        elif key in items:
            del items[key]
            deletes += 1
            contribution = key + 17
        else:
            contribution = key + 19
        trace = _checksum(trace, contribution)
    hot: int = sum(key < initial // 4 for key in items)
    cold: int = sum(key >= initial for key in items)
    checkpoints: tuple[int, ...] = (
        0,
        initial // 8,
        initial // 4,
        initial + initial // 8,
    )
    reads: str = "\n".join(
        f"let checkpoint{ordinal}? = Index::get(index, {key});\n"
        f'if checkpoint{ordinal} is null {{ $std::io::println("-1"); }} '
        f"else {{ $std::io::println(checkpoint{ordinal}::value as String); }};"
        for ordinal, key in enumerate(checkpoints)
    )
    source: str = (
        INVENTORY.replace("INITIAL_SIZE", str(initial))
        .replace("TRACE_SIZE", str(size))
        .replace("CHECKPOINTS", reads)
    )
    return Fixture(
        source=source,
        stdin=f"{state}\n",
        stdout=_lines(
            (
                hits,
                misses,
                additions,
                replacements,
                deletes,
                len(items),
                trace,
                len(items),
                hot,
                cold,
                *(items.get(key, -1) for key in checkpoints),
            )
        ),
    )


SCHEDULER: str = """let Heap = $std::containers::Heap;
let Item = decltype(object {
    ticket: 0,
    request: object { lane: 0, rate: 0, units: 0 }
});
let pending = Heap::with_policy(Item, object {
    Self: Item,
    sample?: null,
    less: fn(left, right) { left::ticket < right::ticket }
});
let stats = object {
    consumed: 0,
    sequence: 0,
    work: $std::ranges::materialize([object { value: 0 }, object { value: 0 }, object { value: 0 }, object { value: 0 },
        object { value: 0 }, object { value: 0 }, object { value: 0 }, object { value: 0 }])
};

fn account(stats, entry, stride: Number) {
    let ticket = entry::ticket;
    let serial = ticket % stride;
    let priority = (ticket - serial) / stride;
    let request = entry::request;
    let work = request::rate * request::units;
    let lane = $std::ranges::at(stats::work, request::lane);
    lane::value = lane::value + work;
    stats::consumed = stats::consumed + 1;
    stats::sequence = (stats::sequence * 65599 + serial * 31 + priority * 13
        + request::lane * 101 + work) % 1000003;
};

let input? = $std::io::read_line();
let rng = if input is null { 1 } else { input as Number };
let arrived = 0;
let epoch = 0;
let peak = 0;
let total = JOB_COUNT;
# A ticket orders by priority, then by arrival; the queued record owns its request.
let stride = total + 1;
loop {
    if arrived == total { break; };
    let batch = 13 + (epoch * 7) % 27;
    for _ in $std::ranges::iota(0, batch) {
        if arrived == total { break; };
        rng = rng * 48271 % 2147483647;
        let priority = rng % 53;
        rng = rng * 48271 % 2147483647;
        let lane = rng % 8;
        rng = rng * 48271 % 2147483647;
        let rate = rng % 251 + 1;
        rng = rng * 48271 % 2147483647;
        let units = rng % 83 + 1;
        Heap::push(pending, object {
            ticket: priority * stride + arrived,
            request: object { lane: lane, rate: rate, units: units }
        });
        arrived = arrived + 1;
    };
    let depth = Heap::size(pending);
    if depth > peak { peak = depth; };
    let quota = if epoch % 8 == 0 { 0 } else { 16 + (epoch * 11) % 27 };
    for _ in $std::ranges::iota(0, quota) {
        let ticket? = Heap::pop(pending);
        if ticket is null { break; };
        account(stats, ticket, stride);
    };
    epoch = epoch + 1;
};

let backlog = Heap::size(pending);
loop {
    let ticket? = Heap::pop(pending);
    if ticket is null { break; };
    account(stats, ticket, stride);
};
$std::io::println(stats::consumed as String);
$std::io::println(stats::sequence as String);
$std::io::println(peak as String);
$std::io::println(backlog as String);
for lane in $std::ranges::iota(0, 8) {
    $std::io::println($std::ranges::at(stats::work, lane)::value as String);
};
"""


def _scheduler(_directory: Path, size: int, seed: int) -> Fixture:
    state: int = _seed(seed)
    draws: Iterator[int] = _draws(state)
    pending: list[tuple[int, int, int, int, int]] = []
    lane_work: list[int] = [0] * 8
    sequence: int = 0
    consumed: int = 0
    peak: int = 0

    def account() -> None:
        nonlocal sequence, consumed
        priority, serial, lane, rate, units = heapq.heappop(pending)
        work: int = rate * units
        lane_work[lane] += work
        consumed += 1
        sequence = _checksum(sequence, serial * 31 + priority * 13 + lane * 101 + work)

    arrived: int = 0
    epoch: int = 0
    while arrived < size:
        arrivals: int = min(size - arrived, 13 + epoch * 7 % 27)
        for _ in range(arrivals):
            heapq.heappush(
                pending,
                (
                    next(draws) % 53,
                    arrived,
                    next(draws) % 8,
                    next(draws) % 251 + 1,
                    next(draws) % 83 + 1,
                ),
            )
            arrived += 1
        peak = max(peak, len(pending))
        quota: int = 0 if epoch % 8 == 0 else 16 + epoch * 11 % 27
        for _ in range(min(quota, len(pending))):
            account()
        epoch += 1
    backlog: int = len(pending)
    while pending:
        account()
    source: str = SCHEDULER.replace("JOB_COUNT", str(size))
    return Fixture(
        source=source,
        stdin=f"{state}\n",
        stdout=_lines((consumed, sequence, peak, backlog, *lane_work)),
    )


_DEDUP: str = """let Set = $std::containers::Set;
let seen = Set::new(Number);
fn read_number() -> Number {
    let line? = $std::io::read_line();
    if line is null { return -1; };
    line as Number
};
let accepted = 0;
let duplicates = 0;
for _ in $std::ranges::iota(0, EVENT_COUNT) {
    if Set::insert(seen, read_number()) {
        accepted = accepted + 1;
    } else {
        duplicates = duplicates + 1;
    };
};
let before = Set::snapshot(seen);
let hits = 0;
let misses = 0;
let expired = 0;
let absent = 0;
for _ in $std::ranges::iota(0, QUERY_COUNT) {
    let encoded = read_number();
    let key = $std::math::floor(encoded / 2);
    if encoded % 2 == 0 {
        if Set::contains(seen, key) { hits = hits + 1; }
        else { misses = misses + 1; };
    } else {
        if Set::remove(seen, key) { expired = expired + 1; }
        else { absent = absent + 1; };
    };
};
fn checksum(values: Range<Number>) -> Number {
    let result = 0;
    for key in values { result = (result * 65599 + key) % 1000003; };
    result
};
$std::io::println(accepted as String);
$std::io::println(duplicates as String);
$std::io::println(hits as String);
$std::io::println(misses as String);
$std::io::println(expired as String);
$std::io::println(absent as String);
$std::io::println(Set::size(seen) as String);
$std::io::println(checksum(Set::snapshot(seen)) as String);
$std::io::println($std::ranges::size(before) as String);
$std::io::println(checksum(before) as String);
"""


def _dedup(_directory: Path, size: int, seed: int, *, broad: bool = False) -> Fixture:
    generator: random.Random = random.Random(seed)
    universe: int = max(32, size * 4 if broad else size // 2)
    hot: int = max(4, universe // 8)
    events: list[int] = [
        generator.randrange(
            hot if generator.randrange(10) < (0 if broad else 7) else universe
        )
        for _ in range(size)
    ]
    initial: set[int] = set(events)
    retained: tuple[int, ...] = tuple(sorted(initial))
    seen: set[int] = initial.copy()
    trace: list[int] = []
    hits = misses = expired = absent = 0
    for _ in range(max(1, size // 2)):
        key: int = generator.randrange(universe + universe // 4)
        remove: bool = generator.randrange(2 if broad else 4) == 0
        trace.append(key * 2 + int(remove))
        if remove:
            if key in seen:
                expired += 1
                seen.remove(key)
            else:
                absent += 1
        elif key in seen:
            hits += 1
        else:
            misses += 1

    def checksum(values: tuple[int, ...]) -> int:
        current: int = 0
        for key in values:
            current = _checksum(current, key)
        return current

    return Fixture(
        source=_DEDUP.replace("EVENT_COUNT", str(size)).replace(
            "QUERY_COUNT", str(len(trace))
        ),
        stdin=_lines(tuple(events + trace)),
        distribution={
            "event_key_space": universe,
            "events": size,
            "distinct_inserts": len(initial),
            "duplicate_events": size - len(initial),
            "lookup_hits": hits,
            "lookup_misses": misses,
            "expiration_hits": expired,
            "expiration_misses": absent,
        },
        stdout=_lines(
            (
                len(initial),
                size - len(initial),
                hits,
                misses,
                expired,
                absent,
                len(seen),
                checksum(tuple(sorted(seen))),
                len(retained),
                checksum(retained),
            )
        ),
    )


def cases() -> tuple[Case, ...]:
    return (
        Case(
            "set-dedup",
            "collections",
            "Runtime-fed event deduplication, membership queries, expiration and retained membership snapshots",
            8_192,
            _dedup,
            ("broad-expiring-window", partial(_dedup, broad=True)),
        ),
        Case(
            "bplus-inventory",
            "collections",
            "B+ inventory with hot/cold reads, upserts, deletes, and ordered range checks",
            3_072,
            _inventory,
        ),
        Case(
            "heap-scheduler",
            "collections",
            "Bursty arrivals, stable priority dispatch, and nested Object accounting",
            4_096,
            _scheduler,
        ),
    )
