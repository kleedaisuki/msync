"""Mathematical witnesses for summary ownership, not production language acceptance."""

from __future__ import annotations

import itertools
import json
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class Link:
    value: int
    previous: int | None
    next: int | None


@dataclass(frozen=True)
class Root:
    first: int | None
    last: int | None
    count: int
    item: str


@dataclass
class Heap:
    root: Root
    links: dict[int, Link]

    def clone(self) -> Heap:
        return Heap(self.root, dict(self.links))

    def append(self, value: int) -> None:
        previous: int | None = self.root.last
        if previous is not None and previous not in self.links:
            raise TypeError("An opaque referent fails its checked Link view.")
        fresh: int = max(self.links, default=0) + 1
        self.links[fresh] = Link(value, previous, None)
        if previous is not None:
            self.links[previous] = replace(self.links[previous], next=fresh)
        self.root = Root(
            fresh if previous is None else self.root.first,
            fresh,
            self.root.count + 1,
            self.root.item,
        )


def contract_key(root: Root) -> tuple[str, ...]:
    return (
        "root-schema",
        "first:Object|Null",
        "last:Object|Null",
        "count:Number",
        f"seed:Range<{root.item}>",
        "normal-return:Null",
    )


def exact_previous_key(heap: Heap) -> tuple[int | str, ...]:
    result: list[int | str] = []
    current: int | None = heap.root.last
    seen: set[int] = set()
    while current is not None:
        if current in seen:
            result.append("cycle")
            break
        seen.add(current)
        result.append(current)
        current = heap.links[current].previous
    return tuple(result)


def assert_frame(before: Heap, after: Heap, appends: int) -> None:
    assert before.root.item == after.root.item
    assert after.root.count == before.root.count + appends
    if appends == 0:
        assert before == after
        return
    assert after.root.last not in before.links
    assert after.root.first == (
        before.root.first
        if before.root.last is not None
        else max(before.links, default=0) + 1
    )
    for identity, original in before.links.items():
        actual: Link = after.links[identity]
        assert actual.value == original.value
        assert actual.previous == original.previous
        if identity != before.root.last:
            assert actual.next == original.next
        else:
            assert actual.next not in before.links and actual.next is not None


def finite_heap_frame_cases() -> int:
    references: tuple[int | None, ...] = (None, 1, 2)
    checks: int = 0
    for previous_a, next_a, previous_b, next_b in itertools.product(
        references, repeat=4
    ):
        for first, last in itertools.product(references, repeat=2):
            for initial_count in (0, 2):
                before: Heap = Heap(
                    Root(first, last, initial_count, "Item"),
                    {1: Link(11, previous_a, next_a), 2: Link(22, previous_b, next_b)},
                )
                for appends in range(6):
                    after: Heap = before.clone()
                    for index in range(appends):
                        after.append(100 + index)
                    assert_frame(before, after, appends)
                    assert contract_key(before.root) == contract_key(after.root)
                    checks += 1
    return checks


def alias_result(shared: bool) -> int:
    memory: dict[int, int] = {0: 0, 1: 5}
    actual: dict[str, int] = {"x": 0, "y": 0 if shared else 1}
    memory[actual["x"]] = 1
    return memory[actual["y"]]


def counterexamples() -> dict[str, bool]:
    empty: Heap = Heap(Root(None, None, 0, "Number"), {})
    appended: Heap = empty.clone()
    appended.append(1)
    identity_effect_is_wrong: bool = (
        empty.root.last is None and appended.root.last is not None
    )

    concrete_next: dict[str, str | None] = {"old": None, "new": None}
    concrete_next["new"] = "new"
    bad_strong_summary: set[str] = {"Object"}
    actual_kinds: set[str] = {
        "Null" if value is None else "Object" for value in concrete_next.values()
    }
    weak_summary: set[str] = {"Null", "Object"}
    strong_site_update_is_wrong: bool = (
        not actual_kinds <= bad_strong_summary and actual_kinds <= weak_summary
    )

    heap: Heap = Heap(
        Root(None, None, 0, "Number"), {1: Link(1, None, None), 2: Link(3, None, None)}
    )
    cells: dict[str, int] = {"captured": 1}
    value_pointer: int = cells["captured"]
    primitive_snapshot: int = heap.links[value_pointer].value
    cells["captured"] = 2
    heap.links[1] = replace(heap.links[1], value=9)
    value_scope_is_not_heap_snapshot: bool = (
        heap.links[value_pointer].value == 9
        and heap.links[cells["captured"]].value == 3
        and primitive_snapshot == 1
    )
    environment_cells: dict[tuple[str, str], int] = {
        ("env-a", "x"): 1,
        ("env-b", "x"): 2,
    }
    environment_identity_required: bool = (
        len(environment_cells) == 2
        and len({declaration for _, declaration in environment_cells}) == 1
    )
    compatible_referents: frozenset[int] = frozenset({1, 2})
    null_referents: frozenset[int] = frozenset()
    unknown_referents: frozenset[int] = compatible_referents
    formal_storage: frozenset[str] = frozenset({"Number", "Null"})
    actual_storage: frozenset[str] = frozenset({"Number"})
    current_read: frozenset[str] = frozenset({"Number"})

    result: dict[str, bool] = {
        "unfinished_empty_effects_do_not_preserve_precise_last_null": identity_effect_is_wrong,
        "formal_names_do_not_prove_disjointness": alias_result(True) == 1
        and alias_result(False) == 5,
        "allocation_site_singleton_strong_update_is_unsound": strong_site_update_is_wrong,
        "capture_value_freezes_pointer_not_mutable_heap": value_scope_is_not_heap_snapshot,
        "same_lexical_declaration_can_have_distinct_environment_cells": environment_identity_required,
        "unknown_referents_may_alias_known_inputs_not_empty": (
            bool(unknown_referents & frozenset({1})) and not null_referents
        ),
        "equal_read_facts_do_not_imply_mutable_storage_invariance": (
            current_read <= formal_storage
            and current_read <= actual_storage
            and not formal_storage <= actual_storage
        ),
        "associated_item_obligation_is_not_erased": contract_key(
            Root(None, None, 0, "Number")
        )
        != contract_key(Root(None, None, 0, "String")),
    }
    assert all(result.values())
    return result


def footprint_fixed_point() -> dict[str, Any]:
    direct: dict[str, frozenset[str]] = {
        "recursive": frozenset(),
        "append": frozenset(
            {
                "root.first",
                "root.last",
                "root.count",
                "entry-last.next",
                "fresh.value",
                "fresh.previous",
                "fresh.next",
            }
        ),
    }
    calls: dict[str, tuple[str, ...]] = {
        "recursive": ("recursive", "append"),
        "append": (),
    }
    summaries: dict[str, frozenset[str]] = {name: frozenset() for name in direct}
    rounds: int = 0
    increases: int = 0
    while True:
        rounds += 1
        changed: bool = False
        for name, local_writes in direct.items():
            next_summary: frozenset[str] = summaries[name] | local_writes
            for callee in calls[name]:
                next_summary |= summaries[callee]
            if next_summary != summaries[name]:
                increases += 1
                summaries[name] = next_summary
                changed = True
        if not changed:
            break
    assert summaries["recursive"] == direct["append"]
    for name, local_writes in direct.items():
        assert local_writes <= summaries[name]
        assert all(summaries[callee] <= summaries[name] for callee in calls[name])
    return {
        "rounds_including_stable_round": rounds,
        "strict_increases": increases,
        "writes": {name: sorted(values) for name, values in summaries.items()},
    }


def main() -> None:
    root: Path = Path(__file__).resolve().parents[2]
    heap: Heap = Heap(Root(None, None, 0, "Number"), {})
    exact_lengths: list[int] = []
    keys: set[tuple[str, ...]] = set()
    for size in range(20):
        exact_lengths.append(len(exact_previous_key(heap)))
        keys.add(contract_key(heap.root))
        heap.append(size)
    assert exact_lengths == list(range(20)) and len(keys) == 1
    report: dict[str, Any] = {
        "status": "passed",
        "scope": "toy mathematical witnesses; not production frontend acceptance or proof of arbitrary source footprints",
        "frame_cases": finite_heap_frame_cases(),
        "exact_heap_previous_depths": exact_lengths,
        "distinct_contract_keys": len(keys),
        "counterexamples": counterexamples(),
        "recursive_footprint_fixed_point": footprint_fixed_point(),
    }
    output: Path = root / ".temp/semantic-summary-model/result.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
