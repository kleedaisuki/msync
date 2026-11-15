# Function descriptor SSA at the Native boundary

The Function SSA tranche is published in product source; its publication
manifest is `.temp/llvm-function-ssa/published.json`. Root owns the serialized
build and execution acceptance, recorded in `artifacts/diagnostics/llvm-function-ssa/`. It does not adopt an LLVM backend or add an
optimization algorithm. Existing Native format 2 / compact revision 4 records
remain unchanged; two appended execution opcodes select direct target operations.

## State and representation

A canonical `msync.function` LLVM aggregate has three fields: code, environment
and native name. Its target representation is three SSA leaf registers:

| LLVM field | Physical Native leaf |
| --- | --- |
| Code pointer | Exact machine i64 token: null is zero; source function index plus one otherwise |
| Environment pointer | Nullable existing Native allocation/interior address |
| Native-name pointer | Nullable String root |

The image owns the function table. Environment addresses retain the actual
capture storage; name leaves retain the existing string. Code-token conversion
preserves the existing signed 32-bit public Function code field, including the
unused code field of a named-native callable. It never reinterprets a CLR object
address as an integer or invents a pointer identity solver.

The compiler owns aggregate shapes and leaf-register arrays for the duration of
one lowered body. Shape leaves follow LLVM struct index order. Insert/extract
operations alias the selected leaf registers; selects select each leaf; PHIs use
one incoming register per leaf, with predecessor copies and block-entry copies
as in the existing pinned LLVM `PHIElimination.cpp::LowerPHINode` translation.
There is no runtime tuple allocation for an aggregate instruction.

Unnamed/generic structs, including integer overflow `{iN, i1}` results, use the
same leaf mechanism. Overflow instructions select the actual machine arithmetic
value and flag primitives. Source builtin `msync.path` and `msync.union.*` have
separate packed Native ABIs and are not silently treated as Functions. Their
component-boundary work is distinct. Generic aggregate memory/function-return
ABIs also require their actual target layout bridge; the leaf mechanism alone
is not a claim about those surfaces.

## Existing Function ABI boundaries

`FunctionComponent = 55` projects a whole Function datum into one leaf.
`ComposeFunction = 56` constructs the existing Function value from three leaves.
Both preserve Native opcodes 0–46 and the separately assigned machine opcodes.
Whole Function parameters, calls and loads retain their original whole-value
ABI register. A direct `extractvalue` projection is emitted at that LLVM
definition, preserving its existing producer/use ABI carrier. Actual aggregate
PHI/select/insert consumers receive canonical leaf projections at the whole
value's definition. Backend selection emits only fields required by those
aggregate operations. Repeated whole-value ABI uses retain their original
register. A Function assembled from SSA leaves is packed at a call/store/return
boundary; no cross-branch first-use cache is used.
Context-unique constants keep their existing per-use ABI policy and constant
materialization cache.

A pending projection uses `MapMachine` on the same original datum. Composition
retains three operand datums and their future dependencies, not the submitting
frame. It does not synchronously demand an operand or snapshot a capture.
Existing source Function/Number/Range interfaces remain unchanged.

## Environment ownership and public compatibility

`NativeEnvironment.View` is internal. It stores the already-owned NativeAddress
and existing capture contract. `CaptureAddress(i)` adds the compiled storage
field offset to that base, preserving interior addresses. It creates no new
NativeAllocation and copies no capture contents. Source captures remain locations;
mutation through the original location remains observable after packing.

Public environment constructors retain their contracts. Their own immutable
`NativeLayout` storage descriptor retains the existing `NativeEnvironmentLayout`
schema through an internal init-only reference. A named-native Function can
therefore carry its pre-existing environment without a source function-table
entry: packing recovers the schema from its owned storage descriptor. This is
owner layout information retained at creation, not a per-allocation registry,
heap analysis or source capture-mode addition.

## Context binding origin

Existing contextual preparation recognizes literal readonly `fs.*` callables
through the native name on a MakeFunction instruction. Splitting that descriptor
must not erase the binding entry point. Compiler-local native-name origin
breadcrumbs therefore move with the name leaf through copies, PHIs and selects.
At final instruction selection, a ComposeFunction may carry a Symbol marker
from an actual existing readonly fs literal. Execution still reads the actual
selected name leaf. Unknown incoming callables are not classified by guessing
names, and ordinary source Functions are not marked contextual.

The marker preserves the existing preparation rule only. It is not another
effect analysis or execution fallback. The graph is local to one target body,
has no heap objects as keys, and is discarded with target emission.

## Telemetry and focused acceptance

The enclosing compiler function/source trace records leaf copies/selects,
component projections and boundary compositions. Runtime counters distinguish
ready and pending projections/compositions; instruction observations retain the
component number and any actual contextual origin. Contextual preparation counts
its actual component/compose computations and unknown operands. Disabled counters
return immediately and allocate no telemetry state.

Published focused checks:

- `FunctionEnvironmentProperties`: direct/interior capture-row ownership,
  mutable location identity, code/name leaves, existing named-native environment
  contracts and the public unused code-field range.
- `FunctionSsaJourney`: actual source Function selection/mutable captures at O0/O2
  and direct/Native2 execution; explicit LLVM PHI, all three extracts and dynamic
  descriptor reassembly through the real target and interpreter.
- `FunctionPartsPendingChecks` in the existing Runtime-friend test project:
  pending whole-datum projections retain the same dependency, do not force it,
  and compose without snapshotting captures.

Root's serialized build passed with zero warnings/errors. Its `executions.log`
records seven environment checks, 16 Function source/IR direct-and-Native2
executions, 16 managed-layout checks and four storage-view source executions.
The separate Runtime-friend pending checks remain prepared, not claimed executed
by that receipt. Original-storage partition
metadata and nonintegral managed-pointer DataLayout are coordinated separately;
neither adds an alias-identity eligibility condition to SROA.
## Call-site position ownership after leaf materialization

Target operand selection can emit instructions. In particular, selecting an SSA
Function at a call boundary emits `ComposeFunction`. The call-site storage plan
must therefore capture the instruction position **after** selecting its callee,
arguments and result register, immediately before emitting the `Call` itself.
Its position is not the beginning of the source call's operand-materialization
sequence.

The existing runtime linker stores the call-site ordinal in the executable
`Call` immediate and uses that ordinal to select the compiled argument-source
plan. Recording the earlier `ComposeFunction` position left the real Call's
ordinal at zero. Repeated one-argument calls accidentally used a compatible first
plan, while the existing midend integration journey's later three-argument
`common(enabled, 3, 2)` indexed beyond that plan's one-element borrowed array.
The defect is target metadata ownership, not a capture-layout or lifetime issue.
Mixed-arity calls and the actual plan-to-instruction association form the focused
regression case; no extra runtime analysis or check is needed.

## Physical carriers of scalarized pointer SSA

SROA may replace an aggregate slot with a fresh opaque-pointer PHI or select.
These values need not have a producer `LogicalType`: LLVM's pointer type alone
cannot distinguish a Function environment address, name root, or code token.
Native lowering selects their carrier within the current function using the
existing operand ABI hints, incoming SSA values, allocated storage views, typed
pointees, and canonical Function field shapes. `AllocaStorageType` remains the
single owner of the source-storage-to-partition mapping. Carrier queries do not
emit instructions or mutate source values, including context-uniqued constants.

The PHI preparation phase uses this query before instruction emission; the
selected carrier is also used for the destination and incoming copies. Active
SSA recursion is cut at the current query, so a loop's external incoming value
can establish the representation. Successful top-level queries are memoized for
the body. This is target register/storage selection, not an optimization test or
heap analysis. Telemetry records carrier queries, cache hits, and selected
physical representations in the existing per-function target trace.

The integration failures motivating this path are the Range journey's scalar
select and the mixed-arity midend journey's preparation of a scalar pointer PHI
under default SROA. Their actual execution acceptance is owned by the serialized
CLI integration run, not by a substitute synthetic instruction-count assertion.

### Scalar PHI edges may change the carrier encoding

A Function environment PHI can merge an initial null with an allocated Scope
address. The incoming address register is monomorphic; the PHI register uses
`Null | Scope`, whose physical representation includes a union tag. These two
registers do not admit the same raw byte copy, despite both carrying LLVM `ptr`.
The edge copy must use Native's existing carrier-moving Copy when the physical
types differ, and may specify a raw typed Copy only for equal physical types.
The comparison uses actual slot physical types, including aggregate PHI edges:
an aggregate leaf can reuse a monomorphic producer register even though its
canonical shape declares a nullable component. Equal physical types retain
their fixed typed copies.

The reduced real source journey in
`.temp/function-environment-fix/iteration/phi.msync` assigned a capturing closure
inside a one-element loop. Calling it inside the loop wrote `inside.txt = 1`;
calling the same closure after the loop failed when entering its capture frame.
The original three-closure journey exhibited the same failure on its first
post-loop call. Relocatable and prepared disassemblies differed only in artifact
stage, excluding contextual preparation as the cause. The issue is the target
copy's representation contract, not capture lifetime or code/environment
association, and requires no runtime fallback.

The existing `tests/Msync.EndToEnd/native_midend_journey.py` contains the durable
regression: three closures capture distinct per-iteration `item` locations and
their post-loop calls produce `321` in `iteration-captures.txt`. Its existing
source, HIR/object, and bound execution routes at O0/O2 exercise this target
copy contract through the actual user pipeline. The reduced shape is:

```msync
let first = fn { 0 };
for item in [1] {
    first = fn { item };
    (first() as String) -> ./inside.txt;
};
(first() as String) -> ./outside.txt;
```

Both outputs are `1`; no separate test runner is needed for the reduction.

## Environment schemas belong to the allocated capture row

The original Function code may identify an unspecialized template whose capture
signature is `Any`, while its actual closure environment contains a pointer to a
`Number` location. The producer's existing `NativeCodeLayout` fields preserve
that concrete pointee. As with the older explicit-layout `MakeFunction` path,
Function composition uses those allocated storage declarations, not the code
template's capture signature, to interpret the environment row.

`NativeImage` owns a lazy schema cache indexed by the existing compiled layout
identity. The existing identity mapping resolves frame-placed interior regions
as well as heap allocations. A single image-owned resolver delegate is shared by
ready composition, pending composition, and contextual preparation; composition
does not recreate schemas or capture storage. Public `NativeEnvironment` storage
retains its own schema. The public four-argument `NativeFunctionParts.Compose`
entry point retains its explicit caller-supplied schema-array contract; product
composition supplies the actual-storage resolver. No artifact section, field
encoding, source capture mode, or runtime fallback is added.

The actual `numeric-record-stream` benchmark exposed this difference on its
`make_row` closure: a concrete `Number` location was previously mislabeled `Any`
when packing the Function, causing its specialized invocation to reject the
capture. Schema publication emits a sink-neutral `function-environment-schema`
observation containing layout identity, capture/location counts, and the
producer-storage ownership. Preparation records storage-schema compositions in
the existing function-parts trace.

## Literal register pressure at the target boundary

The first matched AOT Corpus comparison of `live-policy-dispatch/small/17`
used 7,000 input records and three alternating pairs, with telemetry disabled
for timing. Baseline `llvm-foundations` (`aaf100d5999642f8566761e86c37b31ed0d71d4f8cf87e5e36f3cb845f175ff6`)
and initial SROA candidate (`a9219001d31d279becf76e8470cbe36261f85a00a33dda471311f7854ae2adb7`)
both passed the independent output oracle. Bound p50 was 181.186 versus
237.837 ms; paired median change was +32.654%. The original comparison receipt
is `.temp/benchmarks/llvm-sroa-paired-02/result.json`.

Separate instruction-recording diagnostic executions of those frozen bound
artifacts are in `.temp/function-perf/{baseline,candidate}.jsonl`; these are
causal work observations, not replacement timing samples. They show:

| Observation | Baseline | Initial candidate |
| --- | ---: | ---: |
| Dynamic Native instruction executions | 289,465 | 289,630 |
| Main-body target slots | 975 | 1,140 |
| Main-body target instructions | 1,324 | 1,489 |
| Total Constant executions | 13,243 | 13,690 |
| Function constructions (MakeFunction / ComposeFunction) | 164 | 164 |
| Managed allocated bytes in execute span | 24,070,840 | 36,736,000 |
| Tier register-allocation diagnostic span, ms | 29.596 | 51.460 |

The loop and called numeric kernels execute the same instruction work. The
new descriptor form instead inflated the cold target graph: additional literal
slots make downstream per-slot register work and its allocations more expensive.
These observations identify a concrete contributor; they do not assign all wall
time regression to one span or assert significance for a single diagnostic run.

`ConstantSlotValue` previously allocated a fresh register for every Function
leaf literal, including identical null environments/names and repeated code
tokens. It now reuses the body's existing prelude materialization for the exact
`(NativeConstant, NativeType)` pair. All these values were already unconditional
immutable prelude definitions; reuse neither moves an instruction nor changes
LLVM SSA, evaluation order, captures, or object lifetime. This extends the
existing LLVM-constant/type target materialization cache to target-created
literal leaves, rather than adding an optimization pass. The general engineering
mechanism is also used by LLVM's [FastISel value-register cache](https://llvm.org/doxygen/classllvm_1_1FastISel.html),
although this change remains the Native target's existing prelude ownership model.
Telemetry records actual `constants.leaf_materializations` and
`constants.leaf_materialization_hits` per lowered function. Root owns the
post-change AOT acceptance and same-workload paired comparison; the measurements
above describe the initial candidate, not a claimed benefit of the cache patch.

Integration acceptance for the storage-schema fix: the republished Native AOT
binary executed the previously failing bound
`numeric-record-stream/small/17/candidate.mexe` with its recorded `stdin.utf8`,
and its output matched the benchmark oracle byte-for-byte. Reusing that existing
bound artifact exercises image-time schema recovery without relying on
recompilation to repair producer metadata. The subsequent cleanup removes the
unused template-schema array construction from production composition and
preparation; explicit schema-array API callers retain their existing contract.

## Function projection in the existing tier bridge

After literal reuse and actual capture-row schema integration, the candidate
`b1126f6e69c6d3f4784b2795f59c4d67af55d9b406c2978672ea05417367c983`
passed the three selected Corpus workloads. The standard-scale, seed-29,
five-pair receipt is `.temp/benchmarks/llvm-sroa-paired-04/result.json`.
It still showed +7.10% bound elapsed time for `unicode-path-manifest`; source
elapsed change was +0.59%. These are different routes, not a compilation-time
difference obtained by subtraction.

Separate frozen-artifact instruction profiles are in
`.temp/function-perf/final/{live-policy-dispatch,unicode-path-manifest}-{baseline,candidate}.jsonl`.
The Unicode profile exposes a specific backend integration omission:

| Unicode standard/29 work | Baseline | Candidate before bridge integration |
| --- | ---: | ---: |
| Total executed Native instructions | 6,355,934 | 6,355,874 |
| EnvironmentAddress | 195,000 | 0 |
| FunctionComponent | 0 | 195,000 |
| Tier bridge direct operations | 1,646,249 | 1,451,486 |
| Tier bridge fallback operations | 584,301 | 779,064 |

The existing tier bridge handled `EnvironmentAddress` directly, but the new
Function projection opcode unnecessarily went through its generic interpreter
handler path. The 194,763 warmed tier executions account exactly for the
opposite direct/fallback count changes; the remaining projections execute before
tier entry. This is missing integration of an actual target opcode, not
additional required analysis or a need for another optimization pass.

The regular handler and tier bridge now call the same
`ProjectFunctionComponent` implementation. A ready Function projects directly
and writes the selected value. A pending Function retains the original
`MapMachine` dependency, source and physical result descriptor, without demand
or copying capture contents. Both paths preserve the existing actual
component-ready/pending counters; the tier direct/fallback counters expose which
execution route ran. No LLVM graph, artifact identity, resource lifetime or
Native opcode identity changes.

The same diagnostic live-policy profile has unchanged hot tier operation
counts (1,088,487) and 114 fewer total Native instructions after literal reuse.
Its main target body shrank from 975 slots/1,324 instructions to 861/1,210.
Register-allocation diagnostic times still differ (31.399 versus 37.619 ms),
but a single recorded execution does not establish that this explains the
remaining +3.56% paired bound change. The projection fix targets the exact
Unicode missing-bridge mechanism; it is not claimed to resolve all residual
wall-time variation. Root owns post-change AOT acceptance and paired timing.

### Final same-artifact bridge observation

One final instruction-recording execution with published AOT
`3c821875c8c954db6f2e1c70c7eb47bc69228e32ad0c572c1453b58f8dceb410`
reused the **same standard/29 bound artifact and input** as the earlier Unicode
profile, rather than recompiling or changing the workload. The prior artifact
SHA-256 is `aba4916ead981c115b77c6fc1221e4f14abe7d81d8dbf9791d2853d59288e067`;
input SHA-256 is `01f8eabaed1b876ec13e4c4e00f779a2c44949af11b84bdea544b298463d6494`.
The execution exited zero and its six output lines matched `expected.utf8`
byte-for-byte.

From the repository root, the exact diagnostic invocation is:

```sh
root=$PWD
work=$root/.temp/benchmarks/llvm-sroa-paired-04/unicode-path-manifest/standard/29
trace=$root/.temp/function-perf/final/unicode-path-manifest-tier-integrated
(
  cd "$work" &&
  "$root/artifacts/native/llvm-sroa/msync" --no-config \
    --trace "$trace.jsonl" --telemetry-level instructions run candidate.mexe \
    < stdin.utf8 > "$trace.stdout" &&
  cmp "$trace.stdout" expected.utf8
)
```

| Same-artifact diagnostic work | Before tier integration | Final AOT |
| --- | ---: | ---: |
| Executed Native instructions | 6,355,874 | 6,355,874 |
| Ready FunctionComponent projections | 195,000 | 195,000 |
| Pending FunctionComponent projections | 0 | 0 |
| Tier bridge direct operations | 1,451,486 | 1,646,249 |
| Tier bridge fallback operations | 779,064 | 584,301 |

The expected 194,763 warmed projections moved exactly from fallback to direct
bridge execution. This confirms the missing-opcode-integration mechanism while
holding artifact bytes, input and Native instruction work fixed. It is a
telemetry-enabled causal observation, not a timed performance sample or a claim
of an overall workload speedup. The independent final telemetry-off paired
comparison remains `.temp/benchmarks/llvm-sroa-paired-05/result.json`.

The before/after/baseline traces and the unchanged bound artifact, input and
expected output are retained under
`artifacts/diagnostics/llvm-sroa/unicode-tier/`, so disposable work can be cleaned
without losing this comparison. Final telemetry-off timing is preserved as
`artifacts/diagnostics/llvm-sroa/benchmark-tier-standard.json`.

## Independent residual-cost review

Eight independent reviewers examined the final cohort without changing product
code or timing it again. The [integrated review](llvm-performance-regression-review.md)
connects entry-prelude code/name lifetimes to3x Unicode liveness-set traffic in a
zero-consumer register analysis, and identifies lost exact environment carriers
and widened ordinary initialization bookkeeping as separate executed costs.
It also records exclusions, evidence limits and prioritized follow-up work.

## Preserve the existing environment-extraction ABI

The review of the final projection profile found a remaining representation
loss, separate from the repaired tier dispatch. `LlvmHirLowerer.SourceCall`
already assigns the concrete environment storage's `LogicalType` to the
Function environment `extractvalue`. Source operand ABI annotations also survive
on `LlvmUse` across replacement. The target instead projected every whole
Function environment into the canonical `Scope | Null` leaf at the whole
Function definition, then aliased the extract to that nullable slot. This erased
the existing exact-address representation even at a known source-call boundary.

The target now distinguishes an existing whole-value ABI register from a
Function assembled only from SSA leaves through `AggregateSlots.BoundarySlot`.
A direct extract from a whole ABI value emits its projection at the extract's
own LLVM definition, using its existing environment ABI hint. It does not
speculate a branch-local projection at the earlier whole-value definition or
cache its first emitted use across branches. Canonical eager leaf projections
remain for actual aggregate PHI/select/insert consumers. A leaf-only extraction
aliases an already matching physical carrier; when a preserved exact ABI differs
from its nullable source slot, ordinary existing carrier-copy encoding removes
the union representation at that extraction boundary.

`FunctionProjectionType` consumes the value's explicit ABI annotation first,
then an explicitly retained operand-use annotation, retaining canonical nullable
storage when neither provides an environment carrier. It recognizes the existing
Scope/Null environment family, not a machine code token or a stale whole-Function
hint. `FunctionComponentLoad` and pointer-carrier selection consume the same
helper. No new nonnull, escape, heap, alias or path solver is introduced, and
no nonnull conclusion is inferred just because a pointer is later dereferenced.
The source graph, public Function value ABI and Native artifact records remain
unchanged. The exact literal materialization cache and its inherited placement
are unchanged by this repair.

The ready runtime projection uses the existing `Frame.SetPointer` path when its
actual selected destination has the exact Address storage representation.
Genuinely nullable destinations still receive the tagged value write; the code
does not raw-store an address into union bytes. The following `Frame.Pointer`
therefore also regains its existing exact-address read. Pending projections keep
the original `MapMachine` dependency and selected physical result descriptor;
they are not forced by this change. Interpreter and tier retain the same shared
projection helper.

Compiler per-function/source telemetry records actual exact versus nullable
projection instructions, explicit value/use/canonical ABI-hint query outcomes,
and carrier-copy emissions. Runtime telemetry retains total ready/pending
counts and adds `function_components_ready_address` for actual ready pointer
writes. Recording-disabled counters keep their existing immediate return. Root
owns the focused Function/Range/Unicode journeys and post-change timing. The
expected concrete Unicode criterion is three monomorphic environment result
slots, 195,000 ready exact-address projections for the retained standard fixture,
and identical output; this section does not claim those new executions have
already occurred.
