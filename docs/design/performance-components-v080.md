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

The next bounded candidate is persistent immutable lexical-map roots. Branches,
blocks and lazy-call frames share a snapshot, while binding updates create a new
root. This avoids a growing overlay chain and removes defensive whole-map copies
from the ordinary call path. It must preserve deterministic captures, slot-based
recursion, null refinement and failed-submission behavior; small-script controls
remain necessary because persistent maps trade O(1) hash lookup for tree lookup.
