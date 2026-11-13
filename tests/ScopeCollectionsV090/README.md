# Ordinary Scope collections, v0.9.0

The source collections use logical Scope allocation and ordinary links. There
is no Storage carrier, native arena schema, slot registry or allocator family.

* Set is a red-black tree. A shared black NIL namespace has a Boolean marker;
  parent-direction fields avoid requiring source-level Scope equality.
* Heap is a complete binary tree. A numeric breadth-first position is resolved
  by its binary path, then sifts follow parent/child links. Point operations
  remain O(log n), construction uses O(n) Floyd heapification, and snapshots
  retain the previous breadth-first membership order.
* B/B+ retain degree-eight page algorithms, but child links are actual page
  references. Their bounded page sequences, traversal stacks and snapshot
  assembly are ordinary source-linked sequences; no native vector or page owner
  is involved. Page indexing is bounded independently of collection size.

Run the public behavior journey against a current executable:

```sh
python3 tests/ScopeCollectionsV090/journey.py --binary PATH_TO_MSYNC
```

`--source-module` places the actual `stdlib/containers.msync` declarations in
the test source. This can isolate source-library work from an older frozen
host's embedded standard module; it is not a compatibility path in the product.
`--family`, `--route`, `--optimization` and `--seed` select a focused
real-process check. Defaults cover O0/O2 and source, detached HIR, portable and
bound native artifacts. Every
route is scored against a Python set/map/heap oracle. Detached HIR and binary routes remove the
source, and the executable route also removes the portable artifact before use.
The journey retains exact stdin/stdout and structured failures under root `.temp`.
Its report checkpoints the active route before execution and records executable
and adjacent dependency hashes. Completion also verifies that the fixed payload
did not change during the matrix, including managed CLI dependencies.

`fresh_process_views.py --binary PATH_TO_MSYNC` is a smaller process-invariance
property. It analyzes the same public numeric/nullable Set program in sixteen
fresh CLI processes: randomized map traversal must not change namespace schema
knowledge or whether valid source is accepted. It preserves every process result
and the fixed payload identity; rerunning a failed process until one succeeds is
not acceptance. Wrong scalar writes and missing members through a checked Scope
view remain negative controls: improved schema retention must not admit them.

The current migration also requires recursive static Scope-layout and
Type-witness propagation in semantic analysis. An opaque Scope from an empty
child sequence or a key widened through NIL links is not an acceptable reason
to reintroduce native storage owners or dummy element exemplars.

## Checked integration window

The fixed managed CLI in `artifacts/collections-validation-v090-physical` passes
all **32** seed-17 cases: four families × source/detached-HIR/object/bound × O0/O2.
The policy/identity extensions produce 1,357 Set lines, 1,639 Heap lines and
3,528 B/B+ lines, all byte-exact against their independent oracles. All 24
non-source routes delete their source, and all eight bound routes also delete
the portable input. The complete executable/dependency hash manifest remains
unchanged throughout. Its canonical JSON SHA-256 is
`e769ccf1829529effd3ea09e9c1d932209d555fcf1e9911ce7ad393178c0a2c4`.

The single-payload receipt is
`.temp/scope-collections-v090/final-tree-binary-physical/result.json`; its
`result-binary12.json` preserves the initial binary-only checkpoint before
completing the other routes. `tiering-pause.json` records a brief functional-only
suspension to isolate a separate experiment. This is correctness evidence, not
a latency result or a final release claim. Earlier failed payloads/artifacts and
the logical-view-only windows remain separate, rather than being rewritten as
successful current results.
