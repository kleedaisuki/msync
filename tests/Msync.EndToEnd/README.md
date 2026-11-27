# Real CLI and editor journeys

These scripts exercise the installed language and artifact boundaries, rather
than making old backend mechanisms part of the language contract. Supply a
fixed executable with `--binary /absolute/path/to/msync`; use isolated product
outputs when building concurrently. Functional runs are not latency evidence.

## Native-model migration contracts

| Journey | Retained behavior |
| --- | --- |
| `object_locality_journey.py` | Shared field/capture locations, fresh namespace identity, nullable and mixed carriers, exact IEEE/order behavior, singleton initialization, and snapshots retaining the original instances after producer replacement. |
| `loop_continuation_journey.py` | Zero/nonzero loops, reference fields, metadata after release/acquire, publication before subsequent reads, and a selected mid-loop failure preserving completed members. |
| `affine_scan_journey.py` | Independent and dependent scans, shared namespaces, opaque observations, repeated lazy effects, indirect indexing and floating-point reduction order. |
| `hir_semantic_journey.py` | Higher-order/recursive/captured source semantics, aliases and typed snapshots, source-free artifacts, negative typing and analysis without effects. |
| `hir_editor_journey.py` | Actual LSP edits, source-owned definitions, standard namespace projection, latest-generation results, recovery, UTF-16 positions, fixed native field types and snapshot element types. |
| `stdlib_iterators_journey.py` | Type-witness construction, explicit cursors, lazy sorted algorithms, stable alias identity and typed tree pages through source, detached HIR and native artifacts. |
| `static_optimization_journey.py` | Distinct static field writes, retained aliases and selecting the callee before evaluating an argument that replaces it; structured native-IR inspection and exact file outputs. |
| `managed_word_transport_journey.py` | Exact Word32 results through linked namespaces and numeric joins, signed zero, selected diagnostic spans, deferred Process data and actual native-call telemetry with guest tiers off. |
| `native_owned_call_window_journey.py` | Detached writable parameter handoff, live-after input preservation, a shared Object across active GC, and a pending call that cannot demand stdin ahead of visible output. |
| `native_machine_reference_call_journey.py` | Alias identity through a native reference return, distinguishing caller-owned read-only window reuse from shaped native scratch. |
| `native_reference_store_journey.py` | Detached repeated Object field link/clear, preserving alias identity and measuring machine writes in both directions. |
| `native_fixed_continuation_journey.py` | Fixed captured calls retain the caller's continuation; a bridge-free repeated call attaches its original frame on cold stdin and does not replay a shared-object write. |
| `rb_set_journey.py` | An independent Python set model and red-black graph checks after every mutation: ordering, parent links, unique reachability, black root/NIL, equal black heights and no red-red edges. Also policies, snapshots, nullable keys and retained Object instances. |
| `btree_bplus_journey.py` | Independent map results after mutation, page occupancy/height/leaf-chain validation, ranges, policy keys, snapshots and clearing/repopulation through source and detached artifacts. |

The source fixtures use ordinary `object { ... }` allocations and materialized
Ranges. Materialization fixes membership and instance identity; a lazy sequence
is not a mutable owner or a snapshot. Replacing a producer's Range does not
replace the previously captured instances. No fixture requires the retired
`storage`, `arena` or `slots` source modules.

The Set graph encoder assigns temporary **diagnostic IDs** from its unique
numeric keys, with zero reserved for NIL. These are not allocator handles. The
links are declared as opaque `Object?` values; raw graph inspection guards Null
and applies the logical `decltype(tree::nil)` view before reading node fields,
just as the source tree implementation does. This does not change nodes, IDs or
the independent graph invariants, and is not a host-memory reinterpretation.
The Word32 walk similarly carries an explicit source ordinal so its independent
bitwise oracle does not depend on allocation order or address representation.
B/B+ page churn still checks contents, occupancy and page counts; reuse of a
retired slot allocator's integer handle is intentionally not a language oracle.

The affine journey reports physical HPIR backedges as context, not a fusion
oracle. It checks scan effects and bytes rather than the retired Native2
`NextIterator` operation.
Likewise, static-field checks do not fabricate old `storage.vector_get` call
counts or translate a missing mechanism into a measured zero-cost phase.

Example focused invocations:

```sh
python3 tests/Msync.EndToEnd/object_locality_journey.py --binary PATH --level 2
python3 tests/Msync.EndToEnd/hir_semantic_journey.py --binary PATH --levels 0 2
python3 tests/Msync.EndToEnd/rb_set_journey.py --binary PATH --level 2
python3 tests/Msync.EndToEnd/btree_bplus_journey.py --binary PATH --levels 2
```

For the complementary four-family source/HIR/object/bound matrix and its
executable/dependency identity checks, see
[ScopeCollectionsV090](../ScopeCollectionsV090/README.md). Timing belongs to the
[maintained benchmark suite](../../BENCHMARK.md), with telemetry collected in
separate diagnostic runs.

The retained native-model Set checkpoint is
`artifacts/validation/native-source-journeys-rb-fixed/result.json`: one unchanged
managed Release CLI payload passes O0/O2 source, strict portable and detached
executable routes. Each route checks all 148 mutations against the independent
graph/model oracle and verifies 18 output files (888 graph observations total).
The receipt includes complete executable/dependency and artifact hashes. This
does not certify all journeys on that image, Native AOT throughput, or a release.
On the same unchanged payload, the BPlusTree O2 source/portable/bound checkpoint
passes the original 745-mutation trace and all 11 exact outputs on each route;
its result, payload identity and selection are retained under
`artifacts/validation/native-source-journeys-bplus-fixed/`. The corresponding
BTree frontend-pressure failure remains separate, not counted as a pass from
the BPlusTree result or the smaller four-family matrix.
