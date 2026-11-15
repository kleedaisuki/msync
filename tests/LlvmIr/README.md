# Source-to-LLVM-derived IR journey

```sh
dotnet run --project tests/LlvmIr -c Release -- --output .temp/llvm-ir/journey
```

This project analyzes actual language sources, lowers their typed HIR directly to
the new LLVM-derived model and emits IR text. It also executes a focused subset
through the new LLVM-graph-to-Native adapter and the existing NativeInterpreter.
This is not an LLVM machine-code backend or a complete language/optimizer port.
The JSON result states `executionIntegration: true` for this specific adapter path.

The source checks cover anonymous Scope allocation/field GEP/store/load and
floating-point member arithmetic, explicit text/line/byte FileIn operations,
write/append/transfer FileOut modes, and the existing Path descriptor ABI. Path
construction calls are identified through the producer's RuntimeCalls mapping;
statically known file operations must not remain generic calls. Missing input
files are not created: analysis and lowering must not execute source IO.

A real-source DCE journey removes discarded arithmetic but retains the returned
result. Its IO source distinguishes unused lazy reader constructors from eager
read_text and active byte readers, and retains every output operation. Before/
after dumps and sink-neutral DCE observations are retained under `--output`.

Small executable model properties accompany the source journey: custom operation
and aggregate cloning owns independent Use nodes; replace-all-uses and reference
dropping update users; instruction append/insert/remove preserves intrusive
head/middle/tail links; and a dead consumer chain cascades through DCE's worklist
in program-independent dependency order. These do not substitute test counts for
source workflow or runtime performance evidence.

The execution journey compares fresh unoptimized graphs with an explicit
mem2reg/DCE sequence (not a newly selected full optimizer pipeline). It checks
loop-carried scalar/branch results, shared Scope field mutation, and actual file
text/byte/line IO, append, early iterator break and Path transfer. Output files
are compared byte-for-byte. A consumed missing-file read must retain its filename and
source span and must not publish its dependent output. A discarded future is
not assumed to be demanded merely because its descriptor was constructed. Optimized scalar and alias programs are also serialized to Native2,
decoded without HIR/source replay, and executed; backend and storage descriptors
must survive. Source, before/after IR, Native artifacts and error diagnostics
are stored under the output directory's `execution/` child.

`CallableObjectJourney` extends this same adapter path with typed calls, escaping
and transitive location captures, recursion, generic specialization/template
identity, dynamic callable selection and native callbacks. Ordinary Range
objects are checked for eager ordered literal effects, stored Function values
that are not invoked by iteration, per-iteration captured locations and shared
Scope element identity. Both raw and explicit mem2reg/DCE graphs execute;
selected optimized programs also execute after Native2 roundtrip. Source and IR
artifacts live in `callables/`. A small companion property checks ordinary
`msync.callee` operand-bundle use ownership, clone/RAUW and DCE liveness.

For a concrete frontend/target attribution problem only, an isolated old-path
comparison can be requested explicitly (it is never an acceptance fallback):

```sh
dotnet run --project tests/LlvmIr -c Release -- --diagnose-callable path/to/source.msync --output .temp/llvm-ir/callable-diagnostic
```

This writes current typed HIR and returns the actual old Native path result at
O0/O2, allowing the same valid source to distinguish existing frontend facts
from new target lowering defects. Main acceptance always uses the new graph
path and its independent expected source result.

The APInt arithmetic support has a small deterministic BigInteger-oracle
property probe over word-boundary widths. BigInteger is only a test oracle;
production arithmetic uses the pinned LLVM word algorithms. These properties
supplement, rather than replace, the real source and detached-execution journeys.

`TypeOperationJourney` covers nullable variables/returns/captured locations and
fields; finite-union storage and real conversions; Type denotations; heterogeneous
regex and Path match arms; Connection conversion without network access; nullable
signed-zero/NaN representation; and named Scope initialization/aliasing. A local
process plus release/acquire, a real file selector and acquire region produce
exact checked output files. Invalid numeric text is consumed and must report its
actual conversion source span. Both raw and mem2reg/DCE graphs execute, with
selected Native2 roundtrips. Fixtures/dumps/diagnostics live in `types-operations/`.

## Focused loop prerequisites

```sh
dotnet run --project tests/LlvmIr -c Release -- --loops-only --output artifacts/diagnostics/llvm-loop-prerequisites
```

Five real sources lower through mem2reg, the applicable dedicated-exit and
header-PHI source helpers, fresh dominator/loop information, LCSSA and the Native
target into actual execution. The fixtures cover loop-carried values consumed
after exit, nesting, early break and per-iteration captured closures. An
independent use-edge check treats PHI operands at their incoming predecessor,
checking actual escaping uses become loop-closed SSA. Dumps and detailed traces
retain the transformations rather than counting a pass invocation as a benefit.

A small supplementary graph exercises shapes the structured source producer
normally already canonicalizes: a missing preheader, two backedges and an exit
shared with an outside predecessor. The exact landed helpers must repair its
PHI edges; SSAUpdater must join distinct available values across that split exit.
This is component acceptance, not a complete LoopSimplify.Run or a new compiler
loop-optimization driver. It does not claim runtime speed from LCSSA formation.

The parent-serialized loop cohort passed: five actual source executions, 12
escaping SSA uses before LCSSA and zero after, 12 inserted exit PHIs, maximum
loop depth two, and three required supplementary canonicalization blocks.
Evidence: `.temp/llvm-ir/loop-cohort-{build,run}.log` and
`artifacts/diagnostics/llvm-loop-prerequisites/`.

## EarlyCSE and full LoopSimplify source journey

```sh
dotnet run --project tests/LlvmIr -c Release -- --early-loops-only --output artifacts/diagnostics/llvm-early-loops
```

The current entry runs nine sources from fresh graphs through mem2reg, then
compares execution with explicit EarlyCSE, the actual full LoopSimplify.Run
using mutable DT/LI, LCSSA and LICM backed by real AA/MemorySSA/updater state. It covers duplicate products in a user function,
Scope load/store snapshots across a mutating callback, nested loops, early exits,
per-iteration captures, an invariant kernel, zero-trip loops and ordinary Scope
accumulation/clobbering across loop iterations. IR dumps and
instruction-site profiles must show both two-to-one multiply elimination and
two-to-one executed multiply reduction for the duplicate expression.

The eight-trip invariant kernel also records the executed arithmetic count
inside `scale * scale + shift`: its baseline must actually be 16. The pre-LICM checkpoint reported the actual after-EarlyCSE count as 16, not
as a loop-invariant-motion benefit. The new LICM route requires 16→2 executed
operations and preserves all source outputs; adding that assertion alone is not
a passing performance receipt. The interpreter tier is
disabled only for these deterministic instruction-site operation counts; this
journey does not measure wall time or claim a production performance speedup.

The earlier parent-serialized EarlyCSE/full-loop run passed with zero build warnings or
errors: 7 source cases / 14 executions, duplicate multiplies 2→1 in both IR and
executed instruction sites, one scalar CSE erasure and six load CSE erasures.
Invariant-kernel arithmetic remained 16→16; no LICM benefit is claimed by this
receipt. Evidence: `.temp/llvm-ir/early-loop-{build,run}.log` and the selected
`artifacts/diagnostics/llvm-early-loops/` output tree.

The subsequent parent-serialized LICM run passed all 9 source cases / 18 actual
executions, including aliased loop storage, callback clobbers, nesting, early
exit, captures and zero-trip behavior. The same invariant arithmetic sites now
execute 16→2 operations with the expected kernel result 108. This is an observed
operation-count reduction, not a wall-clock speed claim. Evidence:
`.temp/llvm-ir/licm-loop-{build,run}.log` and
`artifacts/diagnostics/llvm-licm-loops/`.

## Aggressive dead code elimination through actual source workflows

```sh
dotnet run --project tests/LlvmIr -c Release -- --adce-only --output artifacts/diagnostics/llvm-adce
```

The ADCE component check starts with real typed source plus mem2reg and uses the
source defaults (remove control flow, do not remove loops). A dead finite diamond
and dead loop-carried SSA recurrence must disappear, while the induction/control
that preserves loop execution remains. Independent public Compiler O0/O2 and
Native2-decoded execution compare exact results and instruction-site hits for
the dead recurrence. Another actual source retains calls, stores, ordinary Range
construction and text/line/byte file operations with exact output files.

The pending-Bool check uses actual public CompilerSession submissions, not an
assumption that a typed Bool is ready. A gate-held child stdout is converted to
a Bool future from invalid text and stored while genuinely pending. The next
submission compiles a function whose discarded conditional must still consume
that Bool. After gate release, execution must report the original conversion
failure; removing the conditional must not silently remove the source demand.
The gate is released in `finally`. This makes erased-failure behavior observable
without wall-clock thresholds or process-completion races. Sources, typed HIR,
component before/after IR, compiler traces, Native2 artifacts and diagnostics
stay under the selected output tree. Fixture addition alone is not a pass
receipt or a claim that the full LLVM migration is complete.

Pending-Bool acceptance stays on the live public CompilerSession path: modules
with SessionRoots/open signatures intentionally cannot be exported. The two
self-contained control/effect sources independently cover O0/O2 Native2 export,
decode and execution; no export restriction is relaxed for a test fixture.

## Dead stores and rotation through the default compiler

```sh
dotnet run --project tests/LlvmIr -c Release -- --dse-rotation-only --output artifacts/diagnostics/llvm-dse-rotation
```

Four actual sources exercise a Scope-field diamond followed by a common
overwrite, a callback that observes the earlier field value, returned/escaped
Scope aliases, and zero/one/multiple/nested/early-exit loops. The public Compiler
O0/O2 programs and their self-contained Native2-decoded forms must preserve exact
results. Rotation must preserve the header callback count and ordered append
output `xxxxxnnnnnnee`; no process or IO invocation may be duplicated merely
because a header was cloned.

DSE attribution uses a separate real source graph after the same non-DSE scalar
passes. The two diamond-arm Store objects must still be present before DSE and
must be erased by that pass itself. Before/after LLVM dumps, component traces
and actual field-assignment Store instruction hits distinguish DSE work from
unrelated default-pipeline optimization. Public traces also record actual loops
rotated. This is a focused source workflow, not a blanket suite, wall-clock
benchmark or claim of complete LLVM optimization migration.

The final parent-serialized managed ADCE cohort passed: four public source
executions and four self-contained Native2 executions; dead conditional branches
1→0; PHIs 3→1; actual dead recurrence arithmetic hits 8→0. All three FileIn
formats, seven FileOut nodes and two ordinary Range constructor calls remained.
All four public-session pending-Bool consumers (if/match at O0/O2) retained the
original conversion failure. The user function named
`__llvm_profile_instrument_target` also wrote its expected file in source and
artifact executions rather than being mistaken for compiler instrumentation.
Evidence is in `artifacts/diagnostics/llvm-scalar-cohort/adce.log` and its `adce/`
output tree. This checkpoint exposed and resolved a real lowering issue:
awaiting/failure on a source condition is an ordinary runtime demand effect,
not an effect encoded by a removable LLVM conditional branch alone.

The same managed cohort passed four DSE/rotation sources at O0/O2 (eight source
and eight Native2 executions). With identical pre-DSE passes, stores fell 5→1
and both overwritten diamond-arm Store objects were erased by DSE itself.
Actual selected field-store hits fell 4→0, while callback observations and
escaped aliases retained their expected values. Four loops actually rotated,
with header callback counts and ordered file output preserved. The initial
receipt's DSE overwrite aggregate used an unprefixed metric name; the retained
trace was reaggregated without rerunning source to obtain four overwritten
stores and one end-of-function dead store. See
`artifacts/diagnostics/llvm-scalar-cohort/dse-rotation.log` and
`dse-rotation/dse-rotation/dse-metrics-reaggregated.json`. These are functional
and operation-count results, not wall-clock speed claims or completion of the
full LLVM optimization migration.
