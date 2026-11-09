# Native 0.8 compiler and bytecode component costs

## Question and reusable probe

The current CLI comparison in [the end-to-end note](performance-v080.md) measures
the delivered C# implementation against optimized Rust. This probe separates
fixed process startup from scaling inside the production C# pipeline. It calls
the real public parser/compiler/Core/runtime APIs, with no alternate evaluator
or benchmark-only production entrypoints.

[`tests/Performance/Components`](../../tests/Performance/Components/README.md)
contains the Native AOT executable and exact workload generator. Fixtures and raw
reports are retained under `.temp/performance-v080/current/components`. Sources
are written there before measurement so the same programs can be passed directly
to the real CLI.

Three source shapes use N = 1, 64, 256, 1024:

| Shape | Purpose | 1024-operation module |
| --- | --- | --- |
| `transfers` | Unique Path constants; no local bindings | 10,240 instructions, 0 locals, 2,049 constants |
| `generic_values` | Typed Path function called into retained bindings | 8,204 instructions, 1,026 locals, 1,026 constants |
| `scope_values` | Same typed function through a lexical Scope | 9,231 instructions, 1,027 locals, 1,026 constants |

The value shapes return the final Path, which is checked before VM timing. No
filesystem observation, transfer, external process or network connection executes
inside their measured VM. The transfer shape is compiled but never executed by
this component probe.

`compile_o0` is cumulative parsing, inference/lowering and block construction;
`compile_o1` includes the ordinary optimizer. The public API has no independent
inference/lowering entrypoint, so no invented per-phase subtraction is reported.
`artifact_decode` includes production validation and block construction;
`artifact_validate` isolates validation. `bind_and_lower`, `execution_lower`,
`form_blocks`, and the real no-I/O VM are measured separately. Cumulative stages
must not be summed as if they were disjoint.

A calibration selects 1–256 operations per sample, targeting about 2 ms. Warmups
are untimed. Reports retain individual samples, nearest-rank p50/p95, managed
allocation traffic from a separate batch, and timed Gen0/1/2 collection counts.
Allocation bytes are not RSS or live heap size: the
[GC API reports cumulative allocation](https://learn.microsoft.com/dotnet/api/system.gc.gettotalallocatedbytes).
Native image hashes and actual assembly build identities are recorded separately
from the checkout commit at execution.

## Frozen current baseline and mechanism preflight

Host: Linux x64, .NET SDK 10.0.403, Native AOT runtime 10.0.14, invariant
globalization. No managed/debug timing is used as production evidence.

Baseline executable SHA-256:
`b615e69441a65b376973abfd4a88045080b45851e242c1d185669b4326d31720`.
Its compiler/Core/runtime assemblies report build `2963723`; intervening commits
from the delivered `364de75` source affect packaging documentation and the
benchmark runner, not those product components. The frozen executable remains at
`.temp/performance-v080/current/components/baseline/msync-components`.

The initial three-sample preflight immediately separated two problems:

1. Plain transfers have no locals, yet compilation grows roughly quadratically.
   `Compilation.Constant` scans the complete constant list for each new value.
2. Typed binding artifacts validate hundreds of megabytes of temporary state
   for a small program, while executing the same value computation takes about
   1–2 ms. `BytecodeValidation` copies every local state twice per instruction,
   retaining an entry state at every bytecode offset even on a straight-line path.

The allocation geometry matches the source: the generic 1024 fixture has 8,204
instructions and 1,026 locals. Two copies of a roughly 40-byte nullable verifier
cell per instruction predict about 673 MB of local-array traffic; the observed
total is about 675 MB. Scope selection adds instructions and produces about
761 MB. These are repeated allocations, not payload bytes or required runtime
memory. A block-entry state model can retain the same control-flow joins without
copying full frames for every ordinary instruction. The
[JVM verification model](https://docs.oracle.com/en/java/javase/26/docs/specs/jvms/jvms-4.html#jvms-4.10.1)
is a mature example of stack/local type frames at basic-block boundaries rather
than a separate persistent full-frame snapshot for every sequential instruction.

## Constant indexing and block-state validation

The compiler now retains its ordered constant list and adds a `Dictionary<Value,
int>` index. First insertion still owns each serialized ID. Failed submissions
remove only newly added keys before truncating their constant suffix, so REPL
retry cannot reuse a stale ID. The normal one-operation control adds 224 bytes
for the small dictionary; this removes the quadratic lookup cost in larger
ordinary scripts.

The separate Core change moves verifier snapshots to control-flow entries and
executes ordinary instructions against one mutable current state. Constant-based
branch pruning and operation type checks remain part of validation. Neither
optimization trusts imported executable metadata or skips semantic validation.

Candidate native image SHA-256:
`a7b06dd12dd7e0da821c7539024b1a95ebd52a53d4c1afc4a70ada4ea6c2cfd7`.
Raw reports:

* `before-compile.json`: 31 samples, three warmup batches;
* `before-validate.json`: seven samples, two warmup batches;
* `after-index-blocks.json`: 31 samples, three warmup batches.

All are below `.temp/performance-v080/current/components`. The reduced baseline
validator sample count avoids repeatedly allocating 0.7 GB after its growth
mechanism is established; every sample is retained with its count in the report.

| Component | Before p50 | After p50 | Before allocation/op | After allocation/op |
| --- | ---: | ---: | ---: | ---: |
| Compile O1, 1 transfer | 8.294 µs | 8.973 µs | 7,184 B | 7,408 B |
| Compile O1, 256 transfers | 3.833 ms | 1.613 ms | 1,010,784 B | 1,058,888 B |
| Compile O1, 1024 transfers | 63.043 ms | 11.077 ms | 4,033,856 B | 4,249,504 B |
| Validate, 1024 generic bindings | 635.936 ms | 0.916 ms | 675,407,064 B | 214,384 B |
| Validate, 1024 Scope bindings | 731.168 ms | 1.478 ms | 760,653,960 B | 223,800 B |

The post-change p95 validation times are 1.226 ms for generic bindings and
1.654 ms for Scope bindings. Whole native artifact decode for the Scope fixture
is 4.292 ms p50; its 13.913 ms p95 includes runtime/GC variation. The unchanged
no-I/O VM is 1.602 ms p50, allocating 778,824 bytes. These separate observations
prevent attributing a serializer/validator gain to faster language execution.

Native and managed `--check-interner` journeys cover duplicate constants,
first-insertion order, String/Path identity, a failed nullable declaration after
creating a new constant, source-free encode/decode/VM execution after recovery,
and persistent runtime values across a failed compilation. Strict builds and
formatting pass. The larger valid/corrupt CFG and source/artifact acceptance
selection belongs to the Core owner; the profiling executable is not a substitute
for those behavior checks.

## Remaining compiler cost

After indexing, 1024 generic bindings still take 33.523 ms p50 and allocate
21,580,232 bytes during ordinary compilation; Scope bindings take 35.684 ms and
allocate 21,860,024 bytes. The index removes lookup CPU cost but does not remove
the independent frame-cloning mechanism: every call clones the entire growing
lexical dictionary in `CloneFrame`, even when it returns an ordinary scalar.

## Persistent lexical roots

The accepted second compiler change uses BCL `ImmutableDictionary<string,
Binding>` roots. Branches, blocks and lazy-call frames retain the current root;
`Frame.Bind` produces a new root for an actual binding update. No overlay chain
is searched and no full dictionary is copied for an ordinary call. The
[upstream immutable collection design](https://devblogs.microsoft.com/premier-developer/read-only-frozen-and-immutable-collections/)
shares unchanged storage between versions; the
[actual dictionary implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Collections.Immutable/src/System/Collections/Immutable/ImmutableDictionary_2.cs)
uses hash buckets over a persistent tree. This is not a promise of O(1) lookup:
the measured trade is tree lookup/update versus repeated full-map copying.

Function capture discovery now looks up the names actually referenced by the
body, rather than scanning every binding in the surrounding environment. Only
the selected captures are sorted by stable slot, with a name tiebreaker. Hash-map
enumeration order therefore cannot leak into serialized closure layouts; lexical
Scope members retain their explicit source-owned key list.

Frozen immutable-root native image:
`650f765def37704aa45570ec51ebd513f932987f5cfecb4c511da5a742136a6a`.
The compiler/Core/runtime build metadata reports `85a10f4`; the report separately
records checkout `81bc7f8` at execution. Both observations compare frozen native
executables, not whichever image a shared build directory currently contains.
`after-immutable.json` retains 31 samples after three warmup batches.

| Compile O1 | Indexed mutable-map p50/p95 | Persistent-root p50/p95 | Allocation before → after |
| --- | ---: | ---: | ---: |
| 1 plain transfer | 8.973 / 13.602 µs | 8.424 / 11.463 µs | 7,408 → 7,296 B |
| 1 generic binding | 24.141 / 41.689 µs | 21.857 / 29.770 µs | 17,312 → 17,072 B |
| 256 generic bindings | 2.764 / 7.890 ms | 2.429 / 7.317 ms | 2.375 → 1.380 MB |
| 1024 generic bindings | 33.523 / 43.245 ms | 18.624 / 38.728 ms | 21.580 → 5.636 MB |
| 1024 Scope bindings | 35.684 / 47.131 ms | 22.646 / 33.416 ms | 21.860 → 5.914 MB |

The generic 1024 case removes 74% of allocation traffic and 44% of median compile
latency relative to the indexed mutable-map version. The small controls improve,
rather than paying an observed normal-path penalty for larger-script support.
Combined with constant indexing, the original current C# generic case improves
from 46.182 to 18.624 ms p50; the 1024-transfer case improves from 63.043 to
10.475 ms. These are warmed in-process compiler costs, not process-startup or
file-copy speedups.

The actual native checker validates captured definition values despite caller
shadowing, two returned closures with distinct arguments, recursive captured
slots, lexical Scope members, deferred Range arguments, and both null branches.
Every case executes after relocatable and executable serialization/import.
The accompanying fully typed Python driver runs two fresh native processes on
the same capture source and compares the complete `.mso` bytes, not merely the
returned value. Both emitted the identical 1,040-byte artifact with SHA-256
`798457e0bf546624d90d79e56a9daf4dd67cd4e75eb54bc7c247173ef5df2f4e`.
Native checks are recorded in `immutable-snapshots.json`; the persisted source
and procedure are under `tests/Performance/Components`.

The persistent-map representation is independent of the REPL's runtime-failure
visibility contract. The product session owner reconciles actually initialized
bindings in source order; changing a lexical root does not undo filesystem work
or revoke a value whose store already executed. Batch compilation does not
allocate that session-only declaration journal.

## Range-of-Scope loop convergence (2026-11-09)

The first source-composed Map represented entries as `Range<Scope>` and replaced
matching rows through a captured `transform` callback. Checking its constructor
did not terminate. An independent nine-line reducer has the same failure:

```msync
let items = [scope { value: 1 }];
for increment in [1, 2] {
    items = $std::ranges::materialize(
        $std::ranges::transform(items, fn(item) {
            scope { value: item::value + increment }
        })
    );
};
$std::ranges::first(items)::value
```

`dotnet-stack` localized the work to `PrepareLoop -> put -> transform callback ->
Conditional -> JoinFacts`, before optimization. Instrumenting the loop solver
then showed the cause: direct Scope facts already used structural signatures,
but nested `Range.Element` facts used `InfoKey`, including the transient
`ScopeInfo` reference hash. Each `Join` produced equivalent new Scope metadata,
so the signature changed forever. The original Map added 26 facts and 12
functions per unsuccessful iteration, although its two member types were stable.
This is a fixed-point correctness defect, not an unusually expensive valid
specialization or a concept-checking bottleneck.

The solver now applies the same structural traversal recursively to Range items
and callable alternatives. General specialization keys retain their original
Scope/callable identities; mutation facts, alias identities, eager snapshot
semantics and effect propagation are unchanged. No iteration cap, widening to
Any, optimizer bypass or mutable-global cache is introduced. This follows the
usual distinction between semantic abstract-state change and implementation
allocation identity: see the [abstract interpretation fixed-point model](https://www.di.ens.fr/~cousot/COUSOTpapers/POPL77.shtml)
and GCC's [change-driven dataflow solver](https://gnu.googlesource.com/gcc/+/refs/heads/master/gcc/df-core.cc).

The local probe froze the compiler sources and changed only this fix. Linux x64,
.NET 10.0.403, managed Debug, O0, tiered compilation disabled, three untimed
warmups and 15 timed compilations per fixture. Allocation is cumulative managed
allocation traffic, not retained heap size. These are compiler component costs,
not Native AOT or process-startup results:

| Workload | Before p50 | After p50 | Before / after allocated |
| --- | ---: | ---: | ---: |
| No-stdlib scalar function | 0.264 ms | 0.208 ms | 32,544 / 32,544 B |
| List constructor/control | 11.914 ms | 10.381 ms | 2.050 / 2.057 MB |
| Set constructor/control | 18.442 ms | 16.982 ms | 3.145 / 3.157 MB |
| Column-oriented Map control | 37.797 ms | 37.021 ms | 5.562 / 5.584 MB |
| Original row-oriented Map | Nonconvergent | 53.988 ms | Unbounded / 6.747 MB |
| Nine-line reducer | Nonconvergent | 7.345 ms | Unbounded / 1.870 MB |

Both old failing programs exceeded a five-second external observation window;
the changing signatures establish nonconvergence independently of that window.
The ordinary controls retain their function counts and add at most 0.5%
allocation for recursive structural signatures. Their timing variation is not a
claimed speedup. The source-library owner separately selected a column-oriented
Map representation; both representations now compile, so that library choice
does not hide a compiler restriction.

Raw probe reports and bounded stack/counter logs are under
`.temp/compiler-map-cost`. The durable regression is
[`tests/CompilerConvergence`](../../tests/CompilerConvergence/Program.cs):
`dotnet run --project tests/CompilerConvergence` checks ordered duplicate Map
replacement, nested Range-of-Scope facts, nullable fields, changing callable
identities and independent anonymous instances. At O0/O2/O3 it executes source,
serialized `.mso`, and context-bound serialized `.mexe` modules: 54 actual-result
checks, with artifacts retained under `.temp/compiler-convergence`.
The original full Map fixture also completed nine CLI executions at those three
levels, writing the expected replacement value `3` in each source/`.mso`/`.mexe`
journey. Artifact execution ran from a different working directory after source
removal; records and binaries are under `.temp/compiler-map-cost/cli`.
