internal static class ArenaObservation
{
    // Walk the actual row links without allocating guest node objects. A broken
    // cycle cannot strand the test before the independent host oracle sees it.
    internal const string Source = """
        fn observe_indices(owner, count: Number) {
            let output = $std::storage::vector_empty(Number);
            let index = $std::arena::root(owner);
            let prior = 0;
            for _ in $std::ranges::iota(0, 2 * count + 1) {
                if index == 0 { break; };
                let parent = $std::arena::parent(owner, index);
                let left = $std::arena::child(owner, index, true);
                let right = $std::arena::child(owner, index, false);
                let next = parent;
                if prior == parent {
                    $std::storage::vector_push(output, index);
                    if left != 0 { next = left; } else {
                        if right != 0 { next = right; };
                    };
                } else {
                    if prior == left and right != 0 { next = right; };
                };
                prior = index;
                index = next;
            };
            scope { indices: $std::storage::vector_take(output), finished: index == 0 }
        };
        """;
}
