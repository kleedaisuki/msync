# Indexed planner proof obligations

This note reviews the indexed replacement for the baseline historical
scans in `src/plan.rs`, rather than prescribing a new language semantics. The
reference is commit `d96724d` and `docs/design/planning.md`.

## Preserve the overlap relation before optimizing queries

An index may exclude a candidate only when `Resource::overlaps` is false. The
complete union of conflict domains is:

1. any unresolved ENV/PATH (`endpoint == "*"`) resource against every resource;
2. prefix overlap of Windows-drive projections, even across endpoints;
3. matching nonempty identity hints within the same endpoint;
4. comparison-component prefix overlap within the same endpoint.

The last domain uses Windows-folded comparison components, not exact proof keys.
Identity hashes and drive projections remain negative/conservative information:
they may retain a dependency or block a rewrite, but never establish `same_path`.

Trie subtree sets keyed only by operation ID require care during removal: one
operation may contribute multiple resources under the same trie prefix. Removing
one resource must not erase another contribution. Unique access IDs, counted
memberships, or retiring an entire access ID together avoid this failure. The
implementation retires a typed ID only when *all* its resources are covered,
then removes all their contributions before another query. The current effect
constructor emits at most one resource per read/write/consume vector, but
combined append-only mutation indexes contain more than one.

## Rewrite-query equivalence

- Classification must ask for the latest overlapping **state event**, not merely
  the latest operation. Container-directory state precedes the effective child
  write in the same operation. Preserve that event order.
- Forwarding asks for the latest alive write overlapping the read. Its candidate
  must still pass every existing assignment, exact-path, origin-version, endpoint,
  HTTP snapshot, and same-file check. A separate latest write-or-consume query
  tests whether an origin was modified after its reaching producer.
- Backward DSE asks for the earliest later alive read/consume and write. A read at
  the same operation as the earliest write wins: `read_id <= write_id` blocks
  elimination. Only exact eligible overwrites kill a write.
- Every alive operation enters these indexes, including ineligible assignments,
  directories, unknown resources, streams, and endpoint barriers. Removed nodes
  do not enter the backward sweep's future indexes.
- Repeat forwarding and DSE until both report no change. Do not replace the fixed
  point with an arbitrary iteration budget.

These transformations preserve the existing scan answers while avoiding scans
over unrelated events. This is analogous to the reaching-definition and clobber
queries described by [LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html), without
importing its control-flow machinery into a linear DSL.

## Frontier-retirement obligation

Retiring an old access is stronger than answering an indexed query. For every
retired access `A` and replacing write/consume `B`, require:

1. a real completion dependency path from `A` to `B`;
2. every future access that could conflict with `A` also conflicts with `B` in a
   way that supplies a completion path;
3. no special co-start exception can suppress that replacement path.

Exact resource keys with equal identity/projection conflict neighborhoods are a
simple sufficient coverage test under the current overlap semantics. The current
implementation additionally allows an ancestor modifier only when its primary,
identity, and projection domains jointly cover the old resource's domains. Directory
ancestor coverage alone is insufficient: a descendant file can have a hardlink
outside the directory, and a later use of that alias still conflicts with the old
file access but not the parent directory write. Paired stream channels should not
use ordinary completion-frontier retirement: their complementary accesses are
explicitly exempted from completion edges.

Ordinary read/read overlap creates no hazard. An index that blindly gathers all
earlier reads for each new read reintroduces quadratic work on the common shared-
source/many-independent-destinations workload. Keep typed read/write/consume
queries aligned with the actual hazard predicates.

## Differential and contract tests

Compare the indexed plan to the baseline on retained/forwarded operations,
elimination provenance, operation dependency closure, hazard explanations,
stream connections/cohorts, and invalid-cohort issues. Final ordinary file values
alone cannot detect lost stream constraints or accidentally serialized siblings.
An exact transitive reduction is unique for a DAG, so matching baseline reachability
should also yield matching reduced edge pairs.

Suggested cases in addition to existing tests:

- many reads of one ordinary source and independent destination siblings;
- writer, multiple readers, next writer, then reader of the overwritten value;
- nested directory/child access plus an existing hardlink outside the directory;
- repeated container insertions and both slash/no-slash spellings;
- shared co-start channels with unrelated file hazards on their members;
- source mutation between producer and later forwarded read;
- unresolved ENV/PATH combined with otherwise independent SSH resources;
- Windows native/WSL drive projections that block proof without proving identity;
- normalized remote relative versus absolute paths (see below).

A disposable probe at `.temp/perf-v3/plan-differential-probe.rs` imports the archived
baseline plan module with the same public compiler types. It generates mixed
classes, endpoint domains, hierarchical paths and container insertions with a
fixed seed, comparing rendered plans and explicit hazard vectors. It is an
investigation companion, not a checked-in dependency on the temporary archive.

## Corrected remote anchoring defect

The baseline `resource()` splits nonlocal paths on `/` and discards empty
components. Consequently a relative SSH/WSL path `out` and absolute `/out` acquire
the same key, although they name different files in general. For example, two
regular writes to those destinations can incorrectly eliminate the first.

The index must not institutionalize that positive-identity mistake. Remote
filesystem keys now retain a relative/root anchor for positive equality and
descendant-kind proofs. Conservative overlap comparisons do not use the anchor:
the remote working directory is unknown and could make relative/absolute paths
overlap. Local paths are already made absolute, and TCP's `/` endpoint marker has
separate exact-channel semantics. Focused negative-proof tests cover both DSE and
kind inference. Exotic root-escaping `..` spellings can still lose optimization;
they do not justify stronger positive identity proofs.
