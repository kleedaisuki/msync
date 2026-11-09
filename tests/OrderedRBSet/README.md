# Source red-black Set properties

`dotnet run --project tests/OrderedRBSet/Msync.OrderedRBSet.Tests.csproj`

The production source implementation is exercised at O0/O2/O3 through direct
execution, serialized relocatable IR and serialized contextual executable IR.
Every operation in a deterministic sequence captures fresh node metadata and a
membership snapshot. An independent C# SortedSet supplies mutation and membership
expectations. The checker also walks the actual source-owned arena to check
ordering, parent links, absence of cycles, black roots/NIL, equal black height,
red-parent exclusion and active/free ownership after every mutation. The sequence
includes monotone insertion, alternating deletion, mixed operations, complete
removal and re-insertion, exercising arena reuse rather than only final sorting.

A second real program uses case-insensitive String ordering, shared Scope aliases
and a retained snapshot. This verifies that comparator equivalence controls
uniqueness and snapshot membership does not follow subsequent mutations.
Endpoint operations additionally cover `get`, `first`, `last`, `pop_first`,
`pop_last`, `extend` and `clear`. Lookups/removals that can be absent return the
standard `Option?` representation, so an absent key is not confused with a
present Null payload admitted by a custom carrier.

The native-friendly real-process companion is:

```text
python3 tests/Msync.EndToEnd/rb_set_journey.py --binary /absolute/path/to/msync
```

It uses a narrower 148-mutation sequence at O2 and checks the actual arena after
each mutation, plus selected policy, endpoint and snapshot outputs. Each of its
three routes executes the CLI; binary routes delete the source, and `.mexe`
executes from a different working directory. There is no C# test-runner dependency
in that installed-product journey.

## Accepted local observation

The coherent managed snapshot `ordered-structures/snapshot-2` passed 602,685 C#
checks, including all 356 mutations through nine staged routes. The real CLI
companion passed 444 invariant observations from three parsed model traces and
30 exact output-file checks across three O2 routes. Reports are under
`.temp/rbset/`. These observations
are managed local integration evidence, not a Native AOT throughput claim; the
delivery owner runs the companion against the packaged Linux and Windows images.

The subsequent retirement/reuse change passed 165 targeted checks over three O2
routes, using the exact current `NaturalOrder` and `Set` declarations as ordinary
source input to the already accepted host. This avoids rebuilding unchanged host
code or repeating the full balancing matrix. The declaration SHA-256 was
`7f0ea1faf27dfb92af31c3b51350e3006e29f909b168babfd26fe45b665e7cff`;
the transcript is `.temp/rbset/churn-current-source.log`. Reproduce a source-only
follow-up with `--churn-only --source-containers stdlib/containers.msync`.
Final package journeys use the actual embedded standard module instead.

The actual example `examples/red_black_set.msync` produced:

```text
rb_set_before.txt: Albedo, Klee, Sucrose
rb_set_after.txt: Klee, Sucrose
rb_set_lower_bound.txt: Sucrose
```

## Representation and algorithm

`$std::containers::Set` is a named operation Scope. Each instance owns an arena, a free-index
stack, its root/count and its explicit strict-order policy. Every node is a data
Scope containing child/parent indexes, a color and a one-element payload Range.
The NIL node has an empty Range of the same element schema. This lets an empty
generic set retain its carrier without executing `front` on an empty input or
introducing a recursive runtime type. Removed indexes are reused; there is no
global handle registry or cross-instance allocator state.
Successful deletion immediately materializes an empty typed payload and resets
the retired node's links/color. A deferred empty adapter alone could still retain
the old payload in its source closure. Re-insertion reuses that owned node Scope
in place; it never modifies or repurposes the user's key object. `--churn-only`
checks this release/reuse boundary through three staged O2 routes without
repeating the previously accepted full balancing matrix.

Insertion uses red-parent repair. Deletion transplants the in-order successor
when necessary, then repairs a black-height deficit. Direction-parameterized
child access and rotation share the left/right cases rather than duplicating
the balancing algorithm. Point operations follow one balanced search path;
ordered traversal follows parent/child successors and never sorts a flat Range.
The source implementation owns these algorithms; native Buffer operations only
provide indexed storage and membership snapshots.
The comparison policy and comparison-relevant parts of stored keys must remain
stable until removal. Mutable Scope keys retain their normal identity; the Set
does not silently clone them or scan/reindex the tree after unrelated mutation.

The [Linux rbtree documentation](https://docs.kernel.org/core-api/rbtree.html)
is a production comparison for intrusive node ownership and logarithmic ordered
operations. Its C implementation is not copied: msync uses ordinary Scope data,
stable indexes and statically specialized policy calls instead of C pointers.
