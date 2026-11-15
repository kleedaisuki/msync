# LLVM SROA source translation

## Source and objective

The inspected source is LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`:
`llvm/lib/Transforms/Scalar/SROA.cpp` and
`llvm/include/llvm/Transforms/Scalar/SROA.h`. Copies are under
`vendor/llvm-reference/`; the license is Apache-2.0 WITH LLVM-exception.

This is a new source translation, not the quarantined Native aggregate prototype
and not a renamed mem2reg pass. SROA transforms actual LLVM alloca memory using
DataLayout byte slices and partitions, rewrites the accesses, and only then
hands the eligible resulting allocas to the existing source mem2reg port.
HeapAlloc is not an alloca, and no heap-to-stack/escape solver is introduced.

The source options are PreserveCFG by default and AggregateToVector=false.
ModifyCFG paths must use the actual shared CFG/DT updater utilities. No custom
eligibility gate based on a convenient Native numeric representation is added
to change the upstream partition/type choices.

## Source families and seams

| Family | Source anchors | Shared state |
| --- | --- | --- |
| Central data and partition iteration | Slice 556–623; AllocaSlices 625–755; Partition/partition_iterator 757–1020 | Actual Use, byte begin/end, splittability, dead users/operands, split tails |
| Slice construction | SliceBuilder 1047–1464 | PtrUseVisitor offsets, escaping/readonly-escaping instruction, ordered slices |
| PHI/select accesses | 1515–2080 | SpeculatablePHIs, PHIsWithStoreToRewrite, SelectsToRewrite, CFG update option |
| Type and scalar/vector conversion | 2082–2855; getTypePartition 4878 onward | DataLayout, integer/vector viability, adjusted pointer/alignment, exact bit extraction/insertion |
| Slice rewriting | AllocaSliceRewriter 2857–4315 | New alloca, partition interval, use rewriting, promotion queues |
| First-class aggregate load/store rewriting | AggLoadStoreRewriter 4317 onward | Actual struct/array/vector values and memory accesses, not field-name matching |
| Presplit and per-partition selection | SliceSplitter 4989 onward; presplitLoadsAndStores 5154; rewritePartition; splitAlloca 6094 | New slices and stable resorting, partition types, new alloca/fragments |
| Driver and promotion | clobberUse 6298; runOnAlloca; deleteDeadInstructions 6505; promoteAllocas 6556; runSROA 6574 | Worklist, post-promotion worklist, dead instruction handles, selected promotable allocas |

The managed seam is `LlvmSroa`, with a partial State and shared Slice,
AllocaSlices, Partition and AllocaSliceRewriter types. The coherent source family
is now published as `src/Msync.Optimization/Llvm/LlvmSroa*.cs` plus
`LlvmPtrUseVisitor.cs`. Publication used an explicit source-file whitelist;
staged Core copies were not overlaid on current shared helpers. No placeholder
implementation returns success or silently substitutes another algorithm.

Slice ordering is Begin ascending, then unsplittable before splittable, then End
descending. Partitions retain the source iterator's overlapping splittable tails
and empty tail-only regions. They are not replaced by a list of semantic field
ordinals. Partition views are ephemeral during traversal, as upstream's iterator
reference is; consumers must not retain a mutable current partition as a
persistent result.

## Concrete representation dependencies

The source can choose arbitrary-width integer partitions (including widths
above 64), integer bitcasts/shifts/masks/insertion, and vector/shuffle forms.
Native language Number is binary64, not an arbitrary integer bit container;
encoding i64/i128 payloads as Number would corrupt values and is not an accepted
implementation. The target also needs exact support for split function-descriptor
code/environment/name fields while its external payload remains the existing
integer function index plus environment/name references. These are target
representation dependencies, not reasons to quietly drop source transforms.

Memcpy/memmove/memset, lifetime and invariant intrinsics require actual IR
representation and their source operand/effect contracts. Alloca allocation-size
helpers and mem2reg lifetime/droppable-user handling must match the source uses.
Optional LLVM debug assignment fragments and unsupported platform facilities
must be recorded explicitly rather than disguised as implemented behavior.

## Observability and coverage

Caller/pass timing and source/function identity accompany work counts for
allocas visited, escapes and exact aborting instruction/opcode, slices/dead
uses, partition spans/tails, rewrite kinds, new allocations, promotions,
post-promotion requeues, speculation/predication and CFG changes. Disabled
recording must not allocate per-use diagnostic dictionaries.

At this checkpoint the central implementation is published: actual source slice ordering,
partition iteration (including split tails), presplitting orchestration and boundary
classification, per-alloca first-class aggregate rewriting, deferred WeakVH deletion,
post-promotion queues, and selected-alloca mem2reg. The conversion, partition,
rewriter, first-class aggregate, PHI/select speculation/predication and pointer-use
families are published with their actual intrinsic/attribute/type dependencies.
Selected-alloca promotion forwards the caller's AssumptionCache to the shared
mem2reg implementation. No SROA build, runtime benefit or complete executable
target coverage is claimed by this publication checkpoint; root owns integration
and execution. The compiler default pipeline is not changed by this cohort.

The readonly-escape branch (SROA.cpp 6313–6401) is published in `LlvmSroa.ReadOnly.cs`.
`BasicLoadAndStorePromoter` derives from the existing source LoadAndStorePromoter,
uses the real SSAUpdater to propagate loads, and retains stores and the original
alloca. Ordinary mem2reg is not substituted for this algorithm. Assumption-like
intrinsic classification uses the source intrinsic identity trait.

The central entry returns both Changed and CFGChanged and creates the source Lazy
DomTreeUpdater. Source defaults `PreserveCFG=true` and `AggregateToVector=false`
are direct options, not target-ABI eligibility filters. The absent ScalarOptions
hidden `sroa_skip_mem2reg` switch follows its default false; no new language switch
is introduced. LLVM debug assignment records are not represented in this IR slice;
source spans remain instruction metadata, and the source debug-fragment migration
code is not presented as implemented.

## Type, byte-partition and conversion family

The type family is published in `LlvmSroa.Types*.cs` with these direct source seams:

| Managed file | Pinned SROA.cpp algorithms |
| --- | --- |
| `Types.cs` | `getAdjustedPtr`, `getAdjustedAlignment`, `canConvertValue` (2082–2190) |
| `Types.Vector.cs` | per-slice vector viability, candidate collection, integer-element ranking/deduplication and deferred pointer candidates (2197–2514) |
| `Types.Integer.cs` | per-slice and per-partition integer viability, endian-aware `extractInteger`/`insertInteger` (2521–2700) |
| `Types.VectorValues.cs` | `extractVector`, `insertVector`, `mergeTwoVectors` (2704–2850) |
| `Types.Partition.cs` | `stripAggregateTypeWrapping` and `getTypePartition` (4840–4980) |

Types are chosen from actual load/store types, byte spans and DataLayout. Pointer
vectors remain pointer vectors when the source requires it. Partial pointer uses
are deferred until ordinary vector candidates have been tried. Candidate shapes
are ranked by lane count, not by target convenience. Integer widening still
requires the source whole-allocation operation and exact conversion predicates;
no Native-specific maximum-width or numeric-double substitution is introduced.
The source 65535 vector-lane limit and LLVM maximum integer width are retained.

Byte partitioning descends arrays/vectors and struct layouts. It can form an
array subsection or a natural struct subsection, but does not invent a field for
alignment padding. Integer extraction and insertion use actual wide integer
shifts, masks, truncation/zero-extension and DataLayout endianness. Vector
extraction/insertion and balanced vector-tree merging emit real extractelement,
insertelement and shufflevector operations, with poison shuffle lanes where the
source uses them. Publication requires those shared instruction/builder carriers;
these are not emitted as opaque target pseudo-operations.

The supporting Core source family includes `AllocaInst` constant allocation size
queries, `VectorType::getWithSizeAndScalar`, `commonAlignment`,
`typeSizeEqualsStoreSize`, and `getIndexedOffsetInType`. GEP struct offsets come
from the physical layout; sequential indices retain signed offsets and vector
steps use element store size. Alloca size queries return a nullable `TypeSize`,
retain scalable shape and zero-sized allocations, and report unknown dynamic
counts or unsigned overflow instead of narrowing them.

Telemetry under `sroa.types.*` records vector/integer queries, candidates examined,
regular slices and split tails, selected viability and concrete rejection causes:
offset alignment, conversion, pointer-candidate conflicts, volatile accesses,
aggregate accesses, bit padding, unsupported use kinds, unsplittable memory,
dynamic memory length, maximum size and missing covering operations. Type
selection passes its existing per-function metrics dictionary into these helpers;
recording-disabled calls return before constructing telemetry keys.

### Actual pointer layout properties

`LlvmDataLayout.Pointer.cs` ports the pinned pointer-specification parser and
carrier, including independent representation/index widths, ABI/preferred
alignment, `e` external-state and `u` unstable-representation flags, zero/all-ones
null flags, symbolic names and fallback to the address-space-zero specification.
`LlvmDataLayout.NonIntegral.cs` ports the legacy `ni` form and the actual queries.
Legacy `ni` entries are applied *after* parsing all pointer specifications and
set unstable representation true and external state false, as upstream does.
Pointer specs participate in layout equality/hash, not their textual spelling.

The distinctions matter to the shared optimizer:

| Pointer property | Non-integral | Must not introduce inttoptr | Must not introduce ptrtoint |
| --- | --- | --- | --- |
| Representation wider than index/address width | yes | no, by this property alone | no |
| Unstable representation (`pu` / legacy `ni`) | yes | yes | yes |
| External state (`pe`) | yes | yes | no, by this property alone |

No query silently equates external state with instability. For example,
`e-p:64:64:64:64-pe1:64:64:64:64-A1-G1` expresses a 64-bit external-state data
address space while code address space zero remains integral. Choosing that
producer layout and the corresponding Native CLR-root carrier is a target ABI
decision, separate from this source algorithm port.

The supporting Core layout family is now published independently of the staged
SROA driver. Existing native-integer layout parsing remains unchanged. Pointer
queries use each address space's actual specification; an unlisted address space
falls back to the complete address-space-zero specification, including its
symbolic name. Deferred legacy-ni processing copies that same name and null
pattern before setting the source's unstable/external flags.

`tests/LlvmIr/SroaLayoutProperties.cs` exercises public Core properties for padded
struct and nested-array GEP offsets, signed indices, vector storage, shape
rescaling, common alignment, constant/dynamic/overflow alloca sizes, NI equality,
external-state versus instability, pointer fallback, null pattern and delayed
legacy-ni application, named default-pointer fallback and order-independent
layout equality/hash. The class is published for root-owned runner integration;
it has not been registered or executed by this publication owner. No build or
runtime result is implied by this note.

## Coherent source publication

The publication includes the full represented source pass, not a field-name
rewrite or a mem2reg wrapper: SliceBuilder, stable slice/partition iteration,
load/store presplitting, selected partition types, alloca slice rewriting,
vector-tree merging, first-class aggregate loads/stores, PHI/select
speculation/predication, readonly-escape load promotion, deferred WeakVH deletion,
worklist/requeue processing and selected-alloca mem2reg. Type-selection callers
forward the pass metrics dictionary, so the detailed vector/integer rejection and
selection counters correspond to actual decisions rather than unused helper
instrumentation. Source defaults remain PreserveCFG=true and
AggregateToVector=false. Neither Native integer width nor a language Range/object
identity is an eligibility condition.

`LlvmTransformBuilder.CreateBitPreservingCastChain` now has the actual
`IRBuilder.cpp` 109–184 fixed/scalable branches. They emit standard
`llvm.vector.insert`/`llvm.vector.extract` calls with their source overload names,
real result/operand types and i64 immarg indices; they do not throw or select a
different SROA partition because a shape conversion is inconvenient. These
intrinsics have actual Core descriptors and declaration lookup, and remain
ordinary Call instructions rather than project extension opcodes. Source
`intrinsicPropagatesPoison` does not classify either intrinsic as propagating a
whole-value poison; the descriptors retain that distinction. Generic builder
memory-family calls use their actual declared result type, notably void for
`llvm.fake.use`, rather than inferring the result from the aggregate argument.

The new source scalar/vector graphs are not evidence of complete Native target
execution. Native exact machine scalars, split aggregate ABI, raw byte memory and
vector/scalable execution remain separately owned target integration. The SROA
entry point was initially published for focused graph/property execution. It is
now selected in Compiler's default O2 pipeline; the integration receipt below
distinguishes accepted source journeys from application benchmark findings.

Five public-pass/helper property classes are now in `tests/LlvmIr/`:
`SroaDriverChecks`, `PtrUseVisitorProperties`,
`SroaAggregateSpeculationProperties`, `SroaPartitionChecks` and
`SroaRewriterProperties`. They cover actual dead-allocation worklist draining,
HeapAlloc preservation, per-Use traversal/APInt offsets, first-class aggregate
splitting and GEP unfolding, overlapping-copy presplits in both byte orders,
partial integer writes, scalar promotion and volatile nonzero address spaces.
This publication owner did not edit Program or start MSBuild; root owns their
runner integration and execution receipts. There is no unit-count claim of
end-to-end acceptance at this checkpoint.

## Default-pipeline application comparison

Use the existing [Corpus runner](../../tests/Performance/Corpus/README.md), not
an optimizer-specific timing harness. The first comparison selects three
independent workload shapes:

| Case | Relevant mechanism | Independent result |
| --- | --- | --- |
| `live-policy-dispatch` | A materialized Range stores three Function objects; one dynamic call site selects them while captured bias and correction locations change. Exercises split code/environment/name carriers and genuine shared captures. | Python stateful rule evaluation checks processed count, corrections and ordered checksum. |
| `numeric-record-stream` | A callback updates captured PRNG state and constructs Scope records, materialized before their numeric/Bool fields are consumed. Exercises local scalar promotion alongside escaping object references; materialization must remain ordinary object storage. | Python exact integer recurrence checks selection count, interval widths and ordered checksum. |
| `unicode-path-manifest` | Path construction and lexical operations over ASCII, BMP, astral and combining text. A non-hit control for Function/record scalarization; not a promise that no scalar optimization can affect it. | Python POSIX splitting and Unicode/UTF-8 counts check six output lines. |

Both routes use O2: artifact preparation explicitly passes `compile -O2`;
source execution uses the CLI's default `optimization = 2`
(`Commands/CommandOptions.cs`). `--no-config` prevents user configuration from
changing the workload. Compare matched published Native AOT payloads, never a
managed development process against the baseline AOT image.

Initial command after the candidate passes the existing real source journeys:

```sh
python3 tests/Performance/Corpus/run.py \
  --baseline artifacts/native/llvm-foundations/msync \
  --candidate artifacts/native/llvm-sroa/msync \
  --case live-policy-dispatch --case numeric-record-stream \
  --case unicode-path-manifest \
  --scale small --seed 17 --pairs 3 --warmups 1 \
  --route both --profile off \
  --work .temp/benchmarks/llvm-sroa-paired-01
```

Use a new work directory for each invocation and run after MSBuild/ILC have
finished. The runner freezes each payload and fingerprints fixtures, alternates
AB/BA, times telemetry-off fresh processes, and checks the oracle after every
invocation. Source and bound timings answer different questions: source includes
compilation; bound includes launch/load/execute but excludes preparation. Keep
`preparation[].compile_ms` and `bind_ms` as individual preparation observations,
not paired compilation medians. Report each route's raw samples, per-image
`p50_ms`, and `paired_median_percent_change` (negative means less elapsed time).
Do not subtract route medians or combine the three cases into a universal score.
A failed oracle or invocation invalidates that route; retain its failure receipt
and fix the product rather than removing the case. No numerical benefit is
claimed by this comparison plan. Extend a useful initial comparison with a
second seed and standard scale before making a broad performance claim.

## Initial default-pipeline AOT integration receipt

The first integrated AOT candidate is retained as
`artifacts/native/llvm-sroa-initial/msync`, with SHA-256
`a9219001d31d279becf76e8470cbe36261f85a00a33dda471311f7854ae2adb7`.
It combines the default SROA selection, exact machine-scalar target execution
and Function leaf SSA; this is a cohort comparison, not isolation of SROA alone.
The accepted control remains `artifacts/native/llvm-foundations/msync`
(`aaf100d5999642f8566761e86c37b31ed0d71d4f8cf87e5e36f3cb845f175ff6`).

Existing managed and AOT midend journeys pass 29 exact output files through
O0/O2 source, detached HIR-to-Native2 and detached bound execution, including
observable failing-cast cases. AOT Range initialization passes 32 exact files
on the same six routes. Receipts reside in
`artifacts/diagnostics/llvm-sroa/` and
`artifacts/diagnostics/llvm-sroa-source/`. The target PHI-copy correction is
explained in [the Function SSA note](llvm-function-ssa-lowering.md): a typed raw
copy cannot also be a monomorphic-to-nullable carrier conversion.

The initial application comparison uses small scale, seed 17, one warmup and
three alternating pairs, telemetry disabled; all measured invocations below
pass their independent oracles. Times are fresh-process p50 milliseconds; the
percentage is the median of paired changes, not a ratio of the two medians.

| Workload | Route | Control p50 | Candidate p50 | Paired change |
| --- | --- | ---: | ---: | ---: |
| live-policy-dispatch (7000) | bound | 181.186 | 237.837 | +32.65% |
| live-policy-dispatch (7000) | source | 209.755 | 247.080 | +17.79% |
| unicode-path-manifest (16250) | bound | 784.702 | 895.050 | +14.06% |
| unicode-path-manifest (16250) | source | 880.597 | 932.429 | +4.09% |

Raw samples and frozen-payload identities are retained in
`artifacts/diagnostics/llvm-sroa/benchmark-initial-results.json`. The Unicode
measurements are visibly noisier; they are not a precise universal slowdown.
This candidate does not establish a performance improvement.

`numeric-record-stream` fails during candidate bound warmup, before timed
samples: `make_row` expects its captured Number location but the composed
environment exposes the original unspecialized template's Any contract. The
actual producer capture row contains the concrete pointer type. Its failure
receipt is `artifacts/diagnostics/llvm-sroa/benchmark-initial-failure.json`. Preserve this case;
repair environment-layout ownership rather than narrowing the benchmark set or
removing the existing call behavior.

## Integrated application result after target/runtime fixes

Current published image: `artifacts/native/llvm-sroa/msync`, SHA-256
`3c821875c8c954db6f2e1c70c7eb47bc69228e32ad0c572c1453b58f8dceb410`.
The same accepted `llvm-foundations` control and matching Native AOT toolchain
are used. The initial failed/slow candidate remains separately preserved.

Three concrete integration defects were repaired without adding an optimizer:

1. PHI predecessor copies select an actual carrier conversion when the source
   and destination physical representations differ, instead of claiming a raw
   typed copy. This includes aggregate leaves backed by monomorphic registers.
2. Function environments retain the producer capture-row schema, cached by
   actual image layout. An unspecialized template's signature is not the
   storage layout. Existing frame-interior layout mapping is reused; captures
   remain shared locations. Production composition no longer constructs or
   passes unused template-environment arrays.
3. Target-created immutable Function leaf literals reuse their typed prelude
   registers, and FunctionComponent is connected to the existing tier's direct
   execution path. Ready projection avoids an unnecessary deferred-value path;
   genuinely pending projection keeps its existing dependency semantics.

The final AOT midend and Range journeys both pass their six O0/O2 routes:
29 and 32 exact files per route respectively, including detached Native2/bound
execution and the midend's observable cast failures. Midend VM instructions are
3061 at O0 and 2446 at O2 (the accepted older cohort was 3140/2489); calls remain
58 and O2 logical allocations 88. Invariant arithmetic remains 16/2. These are
VM work observations, not wall-time speedups.

Final application experiment: standard scale, seed 29, two warmups and five
alternating pairs; telemetry disabled in all timed processes. All application
outputs pass independent oracles. Times below are fresh-process p50 ms; paired
changes are medians of per-pair relative changes.

| Workload | Route | Control p50 | Candidate p50 | Paired change |
| --- | --- | ---: | ---: | ---: |
| numeric-record-stream (32768) | bound | 332.394 | 329.553 | +1.71% |
| numeric-record-stream (32768) | source | 342.272 | 344.017 | +3.66% |
| live-policy-dispatch (28000) | bound | 443.223 | 460.597 | +1.70% |
| live-policy-dispatch (28000) | source | 505.049 | 530.360 | +5.01% |
| unicode-path-manifest (65000) | bound | 3049.088 | 3161.940 | +3.97% |
| unicode-path-manifest (65000) | source | 3270.939 | 3367.297 | +7.69% |

This tranche is functionally integrated, not an established overall performance
improvement. The remaining positive elapsed-time changes are retained as open
performance work rather than hidden by omitting cases or selecting a different
control. Raw samples, oracle manifests and payload hashes are in
`artifacts/diagnostics/llvm-sroa/benchmark-tier-standard.json`. The preceding
literal-cache cohort is separately recorded in `benchmark-final-small.json` and
`benchmark-final-standard.json`; do not mix observations across image hashes.

Compilation has its own measurement rather than subtracting source/bound times.
The existing `HirCompilation/native_list.py` entry point now reports any supplied
source accurately and explicitly disables config discovery and telemetry. Two
already-accepted source fixtures are retained under this receipt's `inputs/`.
Each image has one untimed launch followed by nine alternating fresh-process
`compile -O2` pairs; output bytes/hashes are read after timing, with the same
output path overwritten. The frozen payloads from the application run are used.

| Compile fixture | Control p50 ms | Candidate p50 ms | Median paired delta ms |
| --- | ---: | ---: | ---: |
| numeric-record-stream | 31.044 | 33.181 | +0.370 |
| existing midend journey | 92.125 | 97.010 | +4.738 |

Reports: `compile-numeric.json` and `compile-midend.json` in the same diagnostic
directory. This does not establish that O2 compilation is faster; the midend
fixture still shows added cost. SROA remains the actual upstream pass, not a
replacement for the separately unfinished InstCombine/full-pipeline work.
