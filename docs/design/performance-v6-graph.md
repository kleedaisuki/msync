# v0.6 logical graph conflict indexing

## Workload and contract

The current structured HIR graph builder evaluates every earlier/later node pair
with `Effects::conflicts`. Each call visits the Cartesian product of resource
accesses, including repeated negative path-prefix and endpoint comparisons.
Independent synchronization statements and shared-source fan-out therefore pay
for unrelated paths repeatedly. This is separate from the already indexed flat
`plan::Plan` implementation and from preparation's typed rewrite sweeps.

The public `PlanGraph.commutations` vector retains one proof per independent
instruction pair. Existing callers inspect those proofs and verification rejects
tampering with their shared immutable effect footprints. Consequently an N-node
independent block has N(N-1)/2 public proof records: changing its total space or
time to linear without changing that representation is impossible. This work
preserves the representation, exact vector order, dependencies and explanations;
it targets the avoidable resource-comparison multiplier, not that output bound.

## Mechanism and soundness

`hir::effects::CandidateIndex` keeps read and modifier histories separately. A
new read queries only modifiers; a new modifier queries both histories. It reuses
the existing generic `plan::index::PathIndex` prefix trie, not a second path
normalizer or an alternate overlap predicate. A candidate only authorizes calling
the existing `Effects::conflicts`; it never establishes equality, removes an edge
on a positive alias guess, or creates a rewrite proof.

Filesystem-like resource kinds share conservative prefix candidates. This may
include a false positive for metadata or deep descendants, but cannot exclude
an actual overlap. Endpoint/root handling retains these conservative domains:

- unresolved ENV/PATH addresses overlap every filesystem address;
- native Local and WSL addresses remain mutually uncertain;
- uncertain normalized paths overlap all paths at that endpoint;
- different roots at one endpoint remain candidates because the logical model
  cannot prove relative/absolute or root-escaping disjointness;
- equal-root, certain paths use prefix queries, preserving Windows comparison
  components and case treatment already computed by `FileResource`.

Program ports, physical stream identities, HTTP origins and retained snapshot
versions have separate domains. Optional HTTP origins retain the existing
wildcard semantics. Snapshot candidates deliberately use provider/version only;
the existing exact predicate still validates the actual source identity.

The graph still enumerates pairs in original right-major order. An epoch-marked
candidate vector makes the exact-conflict decision O(1) per enumerated pair.
Boundary and unknown-external-effect reasons are evaluated independently, so
an empty candidate set never permits speculation past control or process effects.
Index state is owned by one block build and is dropped before returning its graph.

A property test compares candidate membership with actual conflicts over 512
random mixed-resource histories (all access kinds, eight backend forms, path
roots, uncertain traversals, Windows/Unicode names, ports and scalar domains).
Any actual conflict omitted by the candidate filter is a test failure. A focused
shared-source/sibling-write case checks that ordinary read/read sharing does not
reintroduce unrelated history candidates. Existing full graph and certificate
checks remain the output/provenance acceptance gate.

## Measurement setup

`benches/graph_v6.rs` constructs HIR and supplied regular-file facts outside the
timer and performs no filesystem/network inspection. It measures graph build and
destruction, with one warmup, seven samples by default, median/p90, plus one
untimed Rust allocation/peak/retained-heap pass. Retained graphs are checked for
the expected dependency/proof count. The cases are independent files,
shared-source fan-out, and a dense same-destination control.

```sh
MSYNC_V6_GRAPH_SIZES=10,100,500,1000 MSYNC_V6_GRAPH_SAMPLES=7 \
  cargo bench --locked --bench graph_v6
```

Equivalent frozen baseline/candidate executables must run serially under the
root's timing window. Allocation tracking is disabled in timed samples. Dense
output and full public proof materialization remain intentionally unchanged;
this benchmark does not claim filesystem throughput or remote transport gains.
Measurements and selected test results will be added after the integration window.

## Prior work informing the design

[LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) documents quadratic
historical-clobber walks and separates indexed/cached clobber queries from the
alias analysis that proves a particular relationship. We adopt that separation,
not LLVM's CFG/SSA machinery. [LLVM Alias Analysis](https://llvm.org/docs/AliasAnalysis.html)
likewise distinguishes MayAlias from stronger positive identity; conservative
candidates are not an equality proof.

[Chalupa et al., Fast Computation of Strong Control Dependencies](https://arxiv.org/abs/2011.01564)
shows both asymptotic improvements and incorrect results in earlier dependence
algorithms, motivating differential/property validation of the relation rather
than accepting matching final file bytes alone. Its NTSCD/DOD algorithms are not
directly used here: this DSL's graph is resource/ordering dependence within a
linear admitted block, not a general control-flow dependence graph.

## Frozen pre-change baseline (2026-10-31)

Root froze the complete pre-optimization v0.6 source and release library under
`target/performance-v6/baselines/`; its maintained graph harness executable has
SHA-256 `1c090a7601ebe82981feb0c04b535d01cca2aad81c4df9310374fc72f540f50a`.
The machine is native Linux x86-64 (`6.12.113+deb13-amd64`), Rust/Cargo 1.99.1,
LLVM 23.1.1. The graph benchmark does not perform endpoint IO, so its timing is a
CPU/allocation result, not an ext4 or transport throughput measurement.

The uncontended baseline run uses seven samples per row. Independent and shared
cases use N=10,100,500,1000. Dense is capped at N=500: 124,750 fully explained
dependencies already retain 282 MB of Rust heap, and N=1000 would mostly measure
the required quadratic public output. Earlier overlapping diagnostic runs were
discarded and are not included. Raw clean output is disposable
`.temp/performance-v6/graph-before.jsonl`; the reusable summary is retained here.

| Case | N | Median ms | p90 ms | Rust allocations | Peak bytes | Retained bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| independent | 10 | 0.051547 | 0.058584 | 362 | 23,284 | 20,876 |
| independent | 100 | 2.389533 | 2.696053 | 3,514 | 623,000 | 353,872 |
| independent | 500 | 50.647406 | 63.215795 | 17,455 | 9,200,352 | 4,973,360 |
| independent | 1000 | 218.924712 | 241.759736 | 34,889 | 34,792,736 | 17,948,488 |
| shared-source | 10 | 0.088376 | 0.100022 | 362 | 23,484 | 21,076 |
| shared-source | 100 | 2.776009 | 4.127741 | 3,514 | 624,640 | 355,512 |
| shared-source | 500 | 50.130302 | 60.574965 | 17,455 | 9,206,792 | 4,979,800 |
| shared-source | 1000 | 201.867601 | 222.033309 | 34,889 | 34,805,176 | 17,960,928 |
| dense | 10 | 0.446922 | 0.452220 | 1,932 | 117,030 | 113,086 |
| dense | 100 | 37.465939 | 47.220572 | 190,214 | 11,748,808 | 11,283,072 |
| dense | 500 | 1027.372042 | 1322.619069 | 4,750,955 | 289,387,680 | 282,014,960 |

These establish the pre-change scale only. Candidate measurements, paired
comparisons and correctness evidence are not inferred from this baseline.

## Deferred API-scaling hypothesis

Whole-program `build_with_context` currently constructs per-block graphs whose
full copied context fields are discarded when their nodes/edges are appended to
the outer graph. With many blocks and a large explicitly supplied facts map,
that can multiply map-cloning work by block count. A shared internal block
builder could preserve standalone public context capture while supplying an
empty per-block result during whole-program aggregation. This is **not selected
or measured in this wave**: ordinary offline contexts are usually empty, while
runtime preparation admits one block, so current workload evidence does not yet
justify prioritizing that API corner over conflict-query and rewrite costs.

## First candidate discriminator (not final acceptance)

After the initial correctness/strict-Clippy gates, root froze candidate CLI
SHA-256 `7fc6ff13304151ff22b1822d0b0da80a339d1a10009c6f7bf7a1998df642e220`.
The graph probe uses that candidate's release library; probe SHA-256 is
`7fde0b2a8f255d201d215a0386c7f77a331cab7b14124d7628d3af02cfeb0edc`.
A serial three-sample paired discriminator is summarized below (the clean
seven-sample baseline table above is not substituted into these comparisons).

| Case | N | Paired baseline median ms | First candidate median ms | Baseline/candidate |
| --- | ---: | ---: | ---: | ---: |
| independent | 10 | 0.066677 | 0.096906 | 0.688x |
| independent | 100 | 3.035161 | 1.321119 | 2.297x |
| independent | 500 | 53.957159 | 10.985106 | 4.912x |
| independent | 1000 | 232.125327 | 51.051622 | 4.547x |
| shared-source | 10 | 0.063680 | 0.074859 | 0.851x |
| shared-source | 100 | 2.925152 | 1.112026 | 2.630x |
| shared-source | 500 | 52.302286 | 13.626112 | 3.838x |
| shared-source | 1000 | 229.243257 | 48.193137 | 4.757x |
| dense | 10 | 0.194912 | 0.263351 | 0.740x |
| dense | 100 | 34.886037 | 39.256775 | 0.889x |
| dense | 500 | 975.892917 | 938.358534 | 1.040x |

Interpretation: indexed negative queries materially reduce large independent and
fan-out CPU work while retaining identical public output. The first candidate
adds 11–68 microseconds in the tiny controls; dense N=100 is also slower in this
small sample. Dense output still retains exactly 282,014,960 bytes at N=500 and
requires 4,773,126 allocations. This is evidence to investigate immutable
normalized-path sharing before accepting the final graph design, not a claim
that the first candidate has solved dense graphs or all small-script latency.
The two candidate-index tests, including the 512-case conflict-superset property,
passed in the root's selected unit gate. Broader graph/proof acceptance and final
measurements belong to the final selected source snapshot.

## Selected second-wave representation change

The first candidate exposed a different cost in dense explained dependencies:
each `Conflict` clones two accesses, and every cloned `FileResource` previously
copied its normalized root and component strings even though they are immutable.
The selected second wave changes only the private normalized-path field to
`Arc<LogicalPath>`. Public location/scope fields, equality/debug output, path
normalization, overlap predicates and serialized objects are unchanged. The one
mutating private operation, constructing a parent resource, uses `Arc::make_mut`
before removing its final component so existing child resources cannot change.

This is shared immutable compiler analysis, not filesystem-observation caching:
it cannot keep a stale host type alive or weaken an execution guard. It also
requires no interning table, global cache or new dependency. A focused unit test
checks pointer sharing and value/debug equality after clone, then checks that
parent derivation detaches while both child copies remain unchanged. Candidate
index properties and public proof/tamper tests remain required after this change.
Final timing and memory results are recorded only for the rebuilt second-wave
artifact, not inferred from the representation change.

## Second-wave measured result (component acceptance; release integration pending)

The rebuilt second-wave component snapshot is associated with frozen CLI SHA-256
`015645dc073986de9743c082ade38c6361cbd385ce754a8fed261065358a6b97`.
Its graph probe SHA-256 is
`1ea4b37186d5fc253dfc24177023d078ce60796b8faff9463f70a77ff30eca51`.
Root ran a fresh, serial same-window baseline/candidate comparison on the dense
N=500 discriminator, with three samples each and the unchanged maintained
harness. This comparison uses the fresh baseline below, not the earlier
seven-sample or first-candidate windows.

| Dense N=500 measurement | Fresh baseline | Second-wave candidate | Change |
| --- | ---: | ---: | ---: |
| Median build + destruction | 949.446969 ms | 455.955078 ms | 2.082x faster |
| Observed p90 (3 samples) | 1206.881748 ms | 533.918728 ms | Descriptive only |
| Rust allocation count | 4,750,955 | 1,779,126 | 62.55% fewer |
| Peak tracked Rust heap | 289,387,680 B | 186,542,180 B | 35.54% lower |
| Retained graph Rust heap | 282,014,960 B | 179,169,460 B | 36.47% lower |
| Explained dependencies | 124,750 | 124,750 | Identical count |
| Commutation proofs | 0 | 0 | Identical count |

The dense result is the combined candidate graph path (indexing plus immutable
normalized-path sharing), not a separately compiled Arc-only experiment. The
allocation and footprint reductions agree with the eliminated per-conflict
normalized component/root copies and the smaller private resource layout. The
first candidate had left this dense footprint unchanged; the representation
repair therefore addresses a different demonstrated cost from negative-query
indexing. Three samples do not establish a tail-latency distribution; the listed
p90 is effectively the observed maximum.

A companion complete object-contextualization discriminator at 100 static
entries measured 223.072728 -> 98.450461 ms. Its Rust allocation count fell from
3,251,969 to 729,143 and cumulative allocated-byte traffic from 416,795,329 to
285,884,145 B. Those are **allocation traffic**, not retained heap or filesystem
bytes. This companion result includes the candidate's other object/context
changes and must not be attributed solely to Arc sharing. Its maintained probe
and broader results belong to the object workstream.

Root's rebuilt second-wave gates passed the clone-sharing/parent-independence
unit, both candidate-index tests, directional barrier graph tests, the immutable
proof-sharing/tamper-rejection test, selected preparation/runtime journeys, and
strict all-target Clippy. Root still owns final release integration, including
a later pure-control runtime repair; this component snapshot is not a claim
that every later source change has been benchmarked or that the complete
release gate has finished.

The public dense explanation and independent commutation vectors remain
quadratic-size outputs. These results remove unnecessary comparison and cloning
work; they do not establish universal linear graph construction. The second-wave
sparse/tiny controls were not rerun in this dense discriminator, so the first
candidate's small-control overhead remains recorded rather than assumed fixed.
Raw same-window rows are disposable `graph-wave2-before.jsonl` and
`graph-wave2.jsonl` under `.temp/performance-v6`; this table retains their setup
and interpretation after cleanup.

## Late small-journey work elimination

The root's actual final-startup control still showed a roughly 0.35 ms increase
for one-operation `check`, so large-plan wins were not treated as permission to
ignore unattributed small-journey cost. Source inspection found unnecessary
prefix-index construction: the pair loop queried the first node against an empty
index and inserted the final node although no later node could consume it. In a
singleton block that meant building the entire resource trie for zero pairs.

Pair enumeration now starts at right index 1 and inserts exactly its immediately
preceding node before querying the current node. At every query the index still
contains precisely nodes `[0, right)`, so pair/proof/dependency order and complete
public output are unchanged. Empty and singleton blocks naturally execute no
index query or insertion, without thresholds or a separate tiny-plan policy.
All per-instruction validation and effect retention precede this loop. A focused
empty/singleton regression retains the invalid-singleton process diagnostic.

This source change does not by itself establish that all startup regression is
removed; the integrating owner measures the rebuilt final executable and
attributes configuration/embedding costs separately. No new speedup percentage
is inferred from the removed work.
