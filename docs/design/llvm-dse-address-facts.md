# DSE invocation-local address facts

The InstCombine aggregate-store expansion makes DSE compare many component
stores against the same pointer expressions. The `90857bf3` midend workload
performs 11,128 overwrite queries and 21,290 constant-offset decompositions in
`<main>`, without deleting any instructions. See the
[compile-cost investigation](../reviews/llvm-performance/o2-compile-cost.md).

`LlvmDeadStoreElimination.AddressFacts.cs` reuses three lazy structural facts:
pointer-cast stripping, underlying object, and constant-offset base/displacement.
These are not alias-query results or memory contents. DSE retains its original
overwrite decision procedure, budgets and policy.
The pinned LLVM `DeadStoreElimination.cpp::isOverwrite` supplies the unchanged
decision procedure; only repeated decomposition is shared.

The table belongs to one DSE `State`, hence one function and fixed data layout.
Each field has exactly one helper/query mode (the existing default arguments).
Pointer identity keys retain address-space/type distinctions, and the pointer's
immutable type determines its index width. Displacements remain `LlvmAPInt`;
negative offsets, width truncation and original base identity are not converted
to host integers. Partial decomposition results are reusable structural facts
too, not promises that every GEP was constant.

Deletion can poison instruction operands, including dependencies of other
cached pointers. Each deletion clears the table before rewriting operands, and
store merging clears it before changing the wide store's value. This coarse
invalidation is local and inexpensive on the actual no-deletion workload;
there is no global revision graph or dependency solver. A fresh invocation
always starts empty. Delayed physical erasure follows the operand rewrite and
cannot create new reusable pointer facts.

Detailed DSE telemetry distinguishes `address.{casts,underlying,offsets}`
requests, hits and misses, invalidation causes and removed entry counts.
Uncached helper metrics retain their concrete stopping/failure causes;
`address.offsets.residual_gep` counts incomplete GEP decompositions. Ordinary
recording-disabled operation does not allocate metric keys or records.

`DseAddressReuseProperties` exercises unchanged distinct visible fields,
negative offsets, a nondefault 32-bit pointer index width, overwritten-store
deletion with recursive address deletion, and partial-store merging. The
existing DSE rotation journey supplies cross-block/loop and real execution
coverage. End-to-end performance comparison uses the same pinned workload,
without disabling DSE or changing pass ordering.

## Disjoint store-run candidate indexing

`LlvmDeadStoreElimination.Candidates.cs` additionally indexes the constant byte
ranges of the current forward, same-block store run. Each known independent
allocation/static root owns a sorted interval union. Unknown roots can extend
a run only at the exact same base; known independent roots can share a run.
The incoming range is queried only against its own partition.
A disjoint incoming store starts its candidate walk at the run's boundary,
instead of asking the same no-overlap question once per preceding field.
Overlapping ranges still enter the existing DSE walk, including partial merges.

Runs partition ranges by distinct identified object, using the same direct
object-identity rule as BasicAA. Unknown bases retain a single-base run and
cannot share this partition. Index width belongs to each partition. Runs require
ordinary unordered stores and a consecutive MemorySSA defining-access chain.
Every extension examines the preceding definition's uses and intervening IR.
An unordered load of an identified object absent from all written partitions
cannot observe any indexed store and need not end the run. A read of a written
object, unknown/atomic reads, non-store effects, throws, cross-block/phi uses
and opaque ranges end the run;
optimized MemoryDef operand-1 references are cache dependencies, not observers.
Signed, nonwrapping intervals through 64 bits use the local interval union;
other address forms retain the ordinary memory walk. This is an online index,
not a second CFG analysis. Actual deletion or merge resets it together with
address facts, without rescanning the function.

`dse.candidates.*` telemetry records queries, runs, effect/use boundaries,
unrepresented or wrapping ranges, overlaps and skipped runs/definitions. Keys
are static full strings, so detailed instrumentation does not allocate strings
per query. The distinct-field property expects zero overwrite queries and 66
skipped candidate visits for twelve stores; the observing-load property keeps
the read and its required earlier store.

The final real midend journey reduces main overwrite queries 11,128→94 and
definition visits 11,637→603. A single-base prototype only reached 10,601
queries: it restarted at independent objects and unrelated literal reads.
The multi-root/read partitioning addresses that measured workload, not only a
synthetic field list. See the [comparison](../reviews/llvm-performance/o2-compile-cost.md#candidate-selection-repair).
