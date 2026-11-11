"""CPython collection and SHA-256 kernels for the library matrix."""

from __future__ import annotations

import heapq
import sys
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path


@dataclass(slots=True)
class Node:
    key: int
    left: Node | None = None
    right: Node | None = None
    red: bool = True


def is_red(node: Node | None) -> bool:
    return node is not None and node.red


def insert(node: Node | None, key: int) -> Node:
    if node is None:
        return Node(key)
    if key < node.key:
        node.left = insert(node.left, key)
    elif key > node.key:
        node.right = insert(node.right, key)
    right = node.right
    if right is not None and right.red and not is_red(node.left):
        node.right = right.left
        right.left = node
        right.red, node.red = node.red, True
        node = right
    left = node.left
    if left is not None and left.red and is_red(left.left):
        node.left = left.right
        left.right = node
        left.red, node.red = node.red, True
        node = left
    if (
        (left := node.left) is not None
        and left.red
        and (right := node.right) is not None
        and right.red
    ):
        node.red, left.red, right.red = True, False, False
    return node


def contains(node: Node | None, key: int) -> bool:
    while node is not None:
        if key == node.key:
            return True
        node = node.left if key < node.key else node.right
    return False


def validate(node: Node | None, lower: int, upper: int) -> int:
    if node is None:
        return 1
    assert lower < node.key < upper and not is_red(node.right)
    if node.red:
        assert not is_red(node.left)
    left = validate(node.left, lower, node.key)
    assert left == validate(node.right, node.key, upper)
    return left + (not node.red)


def key(index: int) -> int:
    return 2 * (index * 40503 % 65536)


def tree(size: int, *, check: bool = False) -> int:
    root = None
    for i in range(1, size + 1):
        root = insert(root, key(i))
        root.red = False
    if check:
        validate(root, -1, 131072)
    score = size
    for i in range(1, size + 1):
        k = key(i)
        score += 3 * contains(root, k) + 5 * (not contains(root, k + 1))
    return score


def hash_set(size: int) -> int:
    values = {key(i) for i in range(1, size + 1)}
    score = len(values)
    for i in range(1, size + 1):
        k = key(i)
        score += 3 * (k in values) + 5 * (k + 1 not in values)
    return score


def heap(size: int, ordered: list[int]) -> int:
    values: list[int] = []
    for i in range(1, size + 1):
        heapq.heappush(values, key(i))
    score = 0
    for expected in ordered:
        score += values[0] == expected
        heapq.heappop(values)
    return score


MASK = (1 << 32) - 1


def rotr(value: int, count: int) -> int:
    return (value >> count) | ((value << (32 - count)) & MASK)


def sha256(input_words: list[int]) -> list[int]:
    state = [
        0x6A09E667,
        0xBB67AE85,
        0x3C6EF372,
        0xA54FF53A,
        0x510E527F,
        0x9B05688C,
        0x1F83D9AB,
        0x5BE0CD19,
    ]
    w = [0] * 64
    for offset in range(64, len(input_words), 16):
        w[:16] = input_words[offset : offset + 16]
        for i in range(16, 64):
            x, y = w[i - 15], w[i - 2]
            s0 = rotr(x, 7) ^ rotr(x, 18) ^ (x >> 3)
            s1 = rotr(y, 17) ^ rotr(y, 19) ^ (y >> 10)
            w[i] = (w[i - 16] + s0 + w[i - 7] + s1) & MASK
        a, b, c, d, e, f, g, h = state
        for i in range(64):
            s1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)
            t1 = (h + s1 + ((e & f) ^ (~e & g)) + input_words[i] + w[i]) & MASK
            s0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)
            t2 = (s0 + ((a & b) ^ (a & c) ^ (b & c))) & MASK
            a, b, c, d, e, f, g, h = (t1 + t2) & MASK, a, b, c, (d + t1) & MASK, e, f, g
        state = [
            (left + right) & MASK
            for left, right in zip(state, (a, b, c, d, e, f, g, h))
        ]
    return state


def main() -> None:
    kind, size_text, warmups_text, samples_text, batch_text, expected = sys.argv[1:7]
    size, warmups, samples, batch = map(
        int, (size_text, warmups_text, samples_text, batch_text)
    )
    run: Callable[[], int | list[int]]
    match kind:
        case "tree":
            assert str(tree(size, check=True)) == expected
            run = lambda: tree(size)
        case "hash":
            run = lambda: hash_set(size)
        case "heap":
            ordered = sorted(key(i) for i in range(1, size + 1))
            run = lambda: heap(size, ordered)
        case "sha256":
            words = [int(word) for word in Path(sys.argv[7]).read_text().split()]
            run = lambda: sha256(words)
        case _:
            raise ValueError(kind)
    for sample in range(-warmups, samples):
        start = time.perf_counter_ns()
        result = run()
        for _ in range(1, batch):
            result = run()
        elapsed = (time.perf_counter_ns() - start) / 1_000_000 / batch
        value = (
            "".join(f"{word:08x}" for word in result)
            if isinstance(result, list)
            else str(result)
        )
        if value != expected:
            raise AssertionError((kind, expected, value))
        if sample >= 0:
            print(elapsed)


if __name__ == "__main__":
    main()
