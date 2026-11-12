# Source red-black Set properties

`dotnet run --project tests/OrderedRBSet/Msync.OrderedRBSet.Tests.csproj`

The production source implementation is exercised at O0/O2/O3 through direct
execution, serialized relocatable IR and serialized contextual executable IR.
Every operation in a deterministic sequence captures metadata directly from
the live arena's row indices and a membership snapshot. An independent C#
SortedSet supplies mutation and membership expectations. The checker walks
the captured links to check
ordering, parent links, absence of cycles, black roots/NIL, equal black height,
red-parent exclusion and live row reachability after every mutation. The sequence
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

It uses a narrower 148-mutation sequence at O0/O2 and checks the actual arena after
each mutation, plus selected policy, endpoint and snapshot outputs. Each of its
three routes per level executes the CLI; binary routes delete the source, and `.mexe`
executes from a different working directory. There is no C# test-runner dependency
in that installed-product journey.

## Current arena checks

The migrated managed host passed 602,685 checks: 356 mutations across all nine
level/stage combinations, plus the policy and endpoint cases. The focused
`--churn-only` path separately passed 33 checks across direct source lowering,
relocatable round trip, and bound round trip. Both used the embedded standard
library.

## Historical local observation

The pre-arena managed snapshot `ordered-structures/snapshot-2` passed 602,685 C#
checks, including all 356 mutations through nine staged routes. The real CLI
companion passed 444 invariant observations from three parsed model traces and
30 exact output-file checks across three O2 routes. Reports are under
`.temp/rbset/`. These observations
are managed local integration evidence, not a Native AOT throughput claim; the
delivery owner runs the companion against the packaged Linux and Windows images.

The historical retirement/reuse change passed 165 targeted checks over three O2
routes, using the then-current `NaturalOrder` and `Set` declarations as ordinary
source input to the already accepted host. The declaration SHA-256 was
`7f0ea1faf27dfb92af31c3b51350e3006e29f909b168babfd26fe45b665e7cff`;
the transcript is `.temp/rbset/churn-current-source.log`. Current runs always
use the actual embedded standard module.

The actual example `examples/red_black_set.msync` produced:

```text
rb_set_before.txt: Albedo, Klee, Sucrose
rb_set_after.txt: Klee, Sucrose
rb_set_lower_bound.txt: Sucrose
```

## Representation and algorithm

`$std::containers::Set` is a named operation Scope. An instance has a row arena
and its explicit strict-order policy. The arena owns root, count, and an
intrusive free-list head. A row contains the key and its child/parent indices
and color; row zero is black NIL. There is no global handle registry or
cross-instance allocator state. Deletion releases any retained key reference;
re-insertion reuses its row index without modifying the user's key object.
`--churn-only` verifies five complete fill/erase cycles through three staged
O2 routes: every fill reuses the same 16 indices, the final owner has no key
references, and a retained membership snapshot still holds the original keys.

Insertion uses red-parent repair. Deletion transplants the in-order successor
when necessary, then repairs a black-height deficit. Direction-parameterized
child access and rotation share the left/right cases rather than duplicating
the balancing algorithm. Point operations follow one balanced search path;
ordered traversal follows parent/child successors and never sorts a flat Range.
The source implementation owns these algorithms; native operations manage
arena rows and publish dense-vector membership as Range snapshots.
The comparison policy and comparison-relevant parts of stored keys must remain
stable until removal. Mutable Scope keys retain their normal identity; the Set
does not silently clone them or scan/reindex the tree after unrelated mutation.

The [Linux rbtree documentation](https://docs.kernel.org/core-api/rbtree.html)
is a production comparison for intrusive node ownership and logarithmic ordered
operations. Its C implementation is not copied: msync uses typed rows, stable
indices and statically specialized policy calls instead of C pointers.
