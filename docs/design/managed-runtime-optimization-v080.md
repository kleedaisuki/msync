# Runtime optimization lessons from HotSpot and CoreCLR

## Decision and scope

Research snapshot: November 9, 2026. This note answers the request to borrow
runtime optimization mechanisms from the JVM and CoreCLR. It proposes a small
adaptive **bytecode execution layer**, not a replacement JIT or garbage
collector. No production implementation or new performance measurement is
claimed by this research commit.

**Recommendation:** first prototype low-cost call/field dispatch specialization
and a few precompiled fused execution handlers. Keep generic bytecode as the
authoritative fallback. Consider profile-guided bytecode versions only if those
experiments establish a sufficiently long-lived, compute-heavy workload. Do not
start with general OSR, speculative object materialization, or native codegen.

The [runtime contract](runtime-v080.md), [memory model](memory-layout-v080.md)
and [accepted performance measurements](performance-v080.md) are the starting
point. Compile-time monomorphization, scalar replacement, precise memory
summaries and field-slot hints are already implemented. Repeating their work
at runtime is not itself an optimization.

## Reference versions and two independent optimization planes

The current Java feature release is JDK 27, released September 15, 2026;
[JDK 25](https://docs.oracle.com/en/java/javase/25/migrate/significant-changes-jdk-25.html)
is the relevant LTS line for the Leyden AOT-profile mechanisms. Sources below
pin HotSpot implementation examples to `jdk-27-ga` where possible. See the
[official JDK 27 release](https://docs.oracle.com/en-us/iaas/releasenotes/java-management/jdk-27-release-note.htm).

The deployed msync SDK is pinned to 10.0.403 and its Linux lock file resolves
ILCompiler 10.0.14. [.NET 10 is the stable LTS line](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core);
[.NET 11 is still RC](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)
at this research date, with GA scheduled for November 10. Use .NET 10 tagged
design/source references for deployed conclusions, not development-branch
features described as if already shipped.

There are two different programs to optimize:

| Plane | Program | Who can optimize it |
| --- | --- | --- |
| Host | C# interpreter, compiler, scheduler and libraries | Native AOT at publish time; supported offline native profile data can influence a rebuild |
| Guest | The user's msync bytecode and its selected runtime values | The msync compiler/optimizer and any future interpreter execution-plan cache |

[Native AOT does not run the CLR JIT and does not support Reflection.Emit](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).
Turning on `DOTNET_TieredPGO` therefore does not make user bytecode acquire a
CoreCLR optimizing compiler. Conversely, changing a bytecode handler or
execution plan does not require emitting new CPU code: the handler itself can
already be part of the AOT executable.

## What the upstream mechanisms actually do

| Mechanism | Primary implementation/research | Useful lesson for msync |
| --- | --- | --- |
| Tiered execution | [HotSpot compilation policy](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/compiler/compilationPolicy.hpp) distinguishes interpreter, C1 profiling tiers and C2; invocation/backedge rates and compiler queues affect promotion | Spend analysis only where expected future execution can repay it; methods that are trivial or no longer active should not be promoted merely to complete a tier ladder |
| Inline caches | [HotSpot compiled ICs](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/code/compiledIC.hpp) use guarded receiver/target state. [SELF PIC research](https://bibliography.selflanguage.org/pics.html) extends a site to several receiver cases and obtains type feedback from the cache | Cache **resolution**, not mutable values. A small site-local shape/target cache can remove repeated dictionary work without requiring a JIT |
| Guarded devirtualization | [CoreCLR design](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/coreclr/jit/GuardedDevirtualization.md) puts a checked fast call beside the original general call; the [.NET 10 performance report](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/) describes shipped devirtualization/profile work | Test an actual function identity or immutable Scope layout before choosing a specialized handler. Guard failure executes the original operation, not an error |
| Dynamic PGO | [CoreCLR DynamicPgo](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/features/DynamicPgo.md) separates collection, representation and consumption, including sparse probes and approximate type/call histograms | Start with function-entry/backedge and unresolved-site information, not a counter or histogram allocation on every bytecode instruction |
| Lazy dispatch resolution | [CoreCLR VSD](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/coreclr/botr/virtual-stub-dispatch.md) resolves on demand and switches among fast guarded dispatch and general resolution | Lazy metadata helps, but even this upstream design records startup/working-set costs. An IC on an already cheap statically resolved operation can lose |
| Bytecode quickening/fusion | [HotSpot rewriter](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/interpreter/rewriter.cpp), [bytecode table](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/interpreter/bytecodes.cpp) and [x86 template interpreter](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/cpu/x86/templateTable_x86.cpp) contain resolved/fast forms and combined access sequences | A process-local plan can use a precompiled specialized or fused C# handler while retaining original PCs, spans and bytecode for fallback/disassembly |
| OSR | [CoreCLR OSR design](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/features/OnStackReplacement.md) identifies patchpoints, alternative bodies and frame-state transitions; pay-as-you-go and diagnostics are explicit goals | A cold method containing a hot loop is different from a frequently called method. Start with promotion at the next function entry; loop entry switching needs live-state correspondence first |
| Deoptimization | [HotSpot deoptimization](https://github.com/openjdk/jdk/blob/jdk-27-ga/src/hotspot/share/runtime/deoptimization.cpp) reconstructs interpreter state and materializes eliminated objects | Local guard fallback is much cheaper than introducing general speculative bytecode scalarization with object reconstruction. CoreCLR's guarded call fallback is not evidence that it implements HotSpot-style arbitrary deopt |
| Persisted warmup information | [JDK 25 method profiling](https://docs.oracle.com/en/java/javase/25/migrate/significant-changes-jdk-25.html) carries previous-run profiles into startup; [JDK 26 object caching](https://docs.oracle.com/en/java/javase/26/migrate/significant-changes-jdk-26-release.html) separates cache representation from the current GC | If profiles become useful, persist structural hints keyed to code identity, never live Scope instances, cell contents, credentials or filesystem observations |
| Allocation/escape | [.NET conditional escape analysis design](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/coreclr/jit/DeabstractionAndConditionalEscapeAnalysis.md), [Native AOT allocation helpers](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/nativeaot/Runtime/GCHelpers.cpp) | Reduce guest objects/frames first; the host already has GC allocation fast paths. Do not build an unmanaged object arena merely to imitate a TLAB |

These are mechanism references, not transferable speedup percentages. The
SELF programs, CLR interface calls and JVM application profiles are not msync
file-transfer workloads.

## Fit to the current interpreter

### First opportunity: cheaper dispatch, without speculative execution

`Interpreter.cs` already parses each textual specialization table once into
`dispatchTables`. It still performs site/table lookups and creates adjusted
`FunctionValue` records for some calls. `SpecializeBlockInstruction` dynamically
recognizes local transfer operands but does not establish an adaptive tier.
`ScopeValue` already uses shared layouts and guarded one-based field hints;
that is an existing checked specialization, not an absent optimization to
reimplement under another name.

Candidate refinements, in priority order:

1. Predecode stable per-instruction dispatch metadata once and avoid constructing
   a replacement FunctionValue merely to convey a chosen code-table target.
   Preserve the actual closure's captures and native identity. This has no
   speculative value assumption and may be simpler than a general IC.
2. For genuinely unresolved hot calls, keep `(actual function identity, selected
   target)` at that call site. A function stored in a Cell can change while
   retaining the same language type, so a type-only guard is insufficient.
3. For hot field sites **without an effective existing slot hint**, try
   `(ScopeLayout reference, field offset)`. Every hit still reads/writes the
   current field through its captured Cell. Never cache the field payload.
   Compare against the current hint/name guard; adding a second cache to an
   already successful hint is not presumed profitable.

Use a small polymorphic representation only when measured receiver diversity
justifies it, then use normal lookup for highly variable sites. Its capacity is
a cache policy, not a language limit or rejected object shape.

### Second opportunity: precompiled fused handlers

If profiles show dispatch/temporary-stack overhead after O2, a few retained
patterns can execute in one precompiled C# handler: for example a proven scalar
local/field access followed by a scalar operation. This is an execution-plan
optimization, not generated native code. Many attractive patterns have already
disappeared through scalar replacement or CTFE; count residual instructions
before designing handlers.

Keep a mapping from each fused step to original PC/span and do not cross calls,
barriers, iterator callbacks, owner-return joins or observable failures merely
to make a larger pattern. Cancellation and source-owned diagnostics retain
their meaning. Mutable VM evaluation stays on its existing control owner;
independent I/O remains the resource DAG's responsibility.

### Later opportunity: profile-guided bytecode versions

Only if long-lived REPL/embedded workloads demonstrate further headroom, collect
limited function-entry/backedge and call/shape feedback and build an alternative
bytecode version. Reuse the compiler's existing optimization/effect machinery
instead of creating a second semantic optimizer.

The promotion decision is economic. With preparation cost C, probe cost p per
future execution, estimated remaining count n and saving d, the useful question
is whether `n * (d - p) > C`, including metadata and tail-latency effects.
Neither JVM thresholds nor a universal "compile after N calls" follows from
our workload. IO wait time is not evidence that bytecode compilation is hot.

Start by selecting a version only at the next function invocation. At that
boundary fresh local slots can be created normally and the original function
ownership boundary remains intact. General loop OSR would additionally need
maps for live locals, operand stacks, raw Cell aliases, iterator cursors,
pending work ownership and eliminated Scope materialization. An existing
`SessionImage` refresh is code linkage at function entry, not that OSR machinery.

## Ownership and invalidation contract

Proposed adaptive state is separate from serialized Module/Instruction objects:

```text
module code image + generation + binding-context projection
    -> per-function execution plan
        -> per-PC resolution cache / optional counters
```

* Plans belong to the interpreter/image lifetime. A later REPL generation clears
  or rebuilds plans along with dispatch data, as `RefreshSessionImage` already
  does for its current tables. Original bytecode remains readable and immutable.
* A field cache guards immutable layout identity and reads the live field on
  every use. A call cache guards actual callable identity and retains captures;
  it does not use a variable's declared Function type as a target proof.
* Context-dependent plans include the retained binding context. Old deferred
  ranges keep directory A while a later REPL submission may run in directory B;
  latest code and projected constants must stay paired for that origin.
* Aliased field/captured-cell payloads are not immutable just because their
  storage types are fixed. `global` imports those same locations, not a new
  constant namespace. Lazy adapter creation and callback execution stay distinct.
* No cached `exists`, directory listing, symlink target, permission, authenticated
  state or transfer outcome becomes a durable optimizer proof. Native operation
  **selection** can be cached; environmental results cannot.
* Preserve invocation owners, directional barriers and selected error order.
  A guard or fused handler must not authenticate an unselected endpoint or
  globally drain unrelated jobs to recover a missed assumption.

No artifact format change is required for the first two proposals. Persisted
profiles, if later justified, need code-content/semantic/format and relocation
identity plus stable structural layout keys. Process-local object references
and runtime hash codes are not portable profile identities.

## Three discriminating experiments

These are scoped proposals, not extra tests already run for this research.
Use the existing native component/CLI infrastructure and accepted byte-oracles.

| Experiment | Comparison and cases | Measurements / decision |
| --- | --- | --- |
| 1. Resolution cost | Existing interpreter versus predecoded call targets, then optional monomorphic/PIC state. Retained O2 call sites with stable target, two targets, diverse targets and a mid-run Cell reassignment; field sites with absent, correct and repeatedly failing hints; later REPL generation and Scope rebind to reordered fields | Actual lookup/miss counts, allocated bytes, prepared-VM p50/p95, total first-run cost. O0 forced dispatch is only a mechanism discriminator; require benefit on real retained O2 sites before adoption |
| 2. Dispatch fusion | Generic dispatch versus a few precompiled fused handlers on residual scalar/Scope work. Compare cold once, short repeated and long loops; consumed results and branch/error/cancellation/owner behavior must match | Handler-plan construction and bytes, dynamic instruction count, allocations, warmup break-even and whole-command latency. Literal-return `.mexe` and one 4 KiB copy are negative controls; do not charge them a profiling structure they cannot repay |
| 3. Profile economics | Normal O2 `.mexe` versus sparse entry/backedge feedback and one alternative bytecode version selected at the next function entry. Include an IO-dominated range and a compute-heavy changing-shape/callable workload | Collect/prepare/execute phases separately, total latency including failed promotion, p95 and metadata retention. If online preparation does not repay, prefer an explicit offline profile-to-`.mexe` experiment rather than deeper OSR |

For all three, freeze source/native images, alternate compared images on one
machine, verify source/`.mso`/`.mexe` paths as appropriate, and count actual
residual sites. Report cold and prepared-VM costs separately. A handler's
microbenchmark speedup is not a small-file-copy or startup improvement.

## What not to adopt now

* A second native JIT with executable-page management, CPU backends and full
  deoptimization solely because the reference VMs have one.
* Always-on dense PGO, a background optimizing compiler, or persisted training
  IO on every short command before any runtime benefit has been established.
* Speculative deletion of physical stores or Scope identity without a complete
  recovery/materialization model. The static proofs remain preferable.
* A custom GC/TLAB, blanket pooling, or a no-GC region for general scripts.
  Captured cells/deferred objects retain actual ownership, and tiny allocations
  can be cheaper than pool clearing, retention and synchronization.
* Assuming compact CLR object headers or stack allocation can be selected for
  each interpreted user object by a C# flag. Optimize the guest representation
  explicitly; let the supported Native AOT runtime manage physical references.

Offline host PGO is a separate bounded build experiment. The
[Native AOT build targets](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/nativeaot/BuildIntegration/Microsoft.NETCore.Native.targets)
support MIBC consumption, but `dotnet-pgo` ReadyToRun examples are not proof that
an unchanged workflow works for this Native AOT app. The existing blended versus
Speed/Size measurements already found no compelling deployment-wide win; a
future representative profile would need to beat that accepted control, not
silently change the compiler setting while claiming guest-runtime improvement.

The immediate next research contribution should therefore be **experiment 1**:
determine whether unresolved resolution and adjusted-call allocations remain
important after the new static memory optimizer. That result decides whether
quickening/fusion has useful headroom, or whether startup/compiler/IO work is
still the better use of engineering effort.

## Follow-up: source-call frames and numeric local storage

Portable and executable bytecode calls now place evaluated source arguments
straight into the callee's private local slots. The suspended caller retains
its operand slice until the callee's work-owner cleanup is complete. The
callee's parameters can be rebound without losing the original argument
identities: a Process placed into an incoming Scope must move to the caller
even if the parameter subsequently refers to a different Scope. Conversely,
putting a Process only into that different private Scope must still join it.
A captured Cell argument is read once during admission; later writes to the
Cell do not change which argument was originally passed. The retained slice
is consumed only for the owned-Process escape case and is never exposed to a
native operation, callback, or deferred range. Code selection and mutable
closure captures are not cached together.

[Lua's call frame construction](https://www.lua.org/source/5.4/ldo.c.html)
uses arguments already present in its value stack. Here, distinct private
locals and suspended caller storage preserve msync's asynchronous ownership
contract; the optimization removes the intermediate argument array, not the
callee's storage. The bytecode and planning boundary are unchanged.

The managed allocation control executes one verified O0 entry with 256 real
source calls under both assemblies. Alternating ABBA allocated bytes per
entry were 53,272, 45,080, 45,080, and 53,272; the zero-call control was
2,000 versus 1,976 bytes. This separates the roughly 32 bytes removed per
call from the entry baseline.

Preliminary Linux x86-64 Native AOT trials compared the same checksummed Set
and Heap artifacts and the same SHA-256 artifact/digest on CPU 6. Six
alternating pairs per case, with artifacts loaded before the measured
execution, gave paired median deltas of -57 ms (Set), -42 ms (Heap), and
+24 ms (SHA). These elapsed results were noisy; the allocation result is
the reason to retain the direct call path, not a guaranteed speedup on
every workload.

A separate private-frame numeric-local prototype retained raw doubles across
local stores with one lazily allocated sidecar per frame. On the same six
pairs per case against the argument-only variant, median paired deltas were
+60 ms (Set), -70 ms (Heap), and +22 ms (SHA). Do not deploy that general
sidecar: its array and extra load/store decisions did not amortize across
these workloads. A future raw-local representation should select dense
numeric slots from compilation or otherwise avoid charging reference-heavy
frames for a sparse numeric layout. The compiler's existing strict binary64
semantics and stable Cell captures remain the contract.

Raw comparison files, hashes, and focused Process-ownership results:
`artifacts/benchmarks/library-matrix-v086/vm-call-frames/`.

## Production boundaries for the execution-cost ablations

The [release-head ablations](performance-assumption-audit-v086.md#current-vm-cost-fewer-interpreter-actions-before-a-different-dispatcher)
support independent changes, not a new wire format or a second interpreter:

- Fetch from a fixed `Instruction[]` using its length and direct indexing.
  A public `FunctionCode` may contain another `IReadOnlyList<Instruction>`;
  retain its generic access contract and do not copy it in each callee.
  Keep the captured module/function pair across awaits and session refreshes,
  including the Transfer/Pop lookahead, cancellation, and instruction spans.
- Extract the deferred native completion and the capturing range adapters to
  helpers whose arguments are supplied on the selected branch. Keep the
  existing closure read timing: the public NativeRegistry admits
  `IReadOnlyList<Value>`; an adapter that used to read `args[1]` during
  producer invocation should not instead snapshot it at range construction.
  Existing lazy, deferred, retained-root, callback error, and cancellation
  tests cover different sides of this boundary. Microsoft documents the
  [display-class and delegate allocations](https://devblogs.microsoft.com/dotnet/understanding-the-cost-of-csharp-delegates/);
  for this change the matched Native AOT allocation data, not the upstream
  explanation alone, establishes the benefit.
- The same allocation mechanism also exists in `MapValue` and
  `CombineValues`: in the pre-change matrix image, `RhpNewFast` at
  `0xa65a3` and `0xa6696` respectively precedes the deferred type test.
  Immediate Bool Not and path expressions can reach these helpers, whereas
  the raw numeric arithmetic path does not. This is machine-code evidence of
  extra work, not a measured workload share; measure those scalar sites
  independently before drawing conclusions about the numeric loop.

For namespace publication, the old scan matches *exact local entries* after
resolving each ancestor's parent, not arbitrary subtree conflicts. Keep the
reverse task-to-roots relation used by scalar transfer/cohort admission and
index the same forward entries. It must share the live work state's REPL
lifetime and retire only the matching task, including when several producers
or several plan roots share a spelling. Namespace filtering excludes every
completed task; ordinary resource dependencies deliberately retain faulted
or canceled tasks until observed. Do not merge those completion policies.
The [frontier probe and alias controls](performance-scheduler.md#residual-native-observations-scan-unrelated-namespace-producers-2026-11-11)
cover the scale and ordering obligations separately.

## Direct native stack operations for owned indexed storage

A selected direct native call can consume the operand representation the VM
already owns. Buffer get/set/size, owned-snapshot front/first/at and numeric
floor do not need an argument array, native registry dispatch or an async
invocation boundary. Admission is by actual backing representation and ready
operands, not by source type or function spelling alone. A miss leaves all
operands untouched: raw cells, deferred arguments, custom/unowned Range
producers and wrong receivers still use generic normalization and diagnostics.
The interpreter does not memoize a backing list, element, count, or captured
context at a call site. Aliases still access the live list, and an owned
snapshot is proved by its producer identity. Fast and generic Buffer operations
share the index and store helpers, including first-write carrier inference and
target-dependent Path/String conversion before index validation. Successful
reads return the actual stored object.

This follows the narrow guard/fallback pattern used by
[CPython's list-index specialization](https://github.com/python/cpython/blob/main/Python/bytecodes.c)
and the distinction between portable bytecode and ready representation in
[HotSpot's template interpreter](https://github.com/openjdk/jdk/blob/master/src/hotspot/cpu/x86/templateTable_x86.cpp).
The msync path does not mutate portable bytecode or require adaptive guards,
warmup or a JIT. Format 5, native signatures and the general NativeRegistry API
are unchanged. AOT profile-disabled instantiations erase the observer hook;
profile runs count the same logical native attempts and separately report
`fast_calls`. Diagnostic runs retain timed native spans, cancellation, source
locations and the existing native failure event.

An isolated Linux Native AOT comparison uses the same bound O2 image for
each before/after pair. The existing LibraryMatrix inputs (8,192 keys, 1,024
SHA bytes) print the score or all eight unsigned digest words for independent
output checks. Timings use seven interleaved fresh CLI process pairs on CPU 9,
excluding compile/bind and recording; profiles use separate executions.
Wall-clock medians and cumulative execute allocation:

| Fixture | Before → after ms | Faster pairs | Paired median Δ ms | Before → after MB |
| --- | ---: | ---: | ---: | ---: |
| Set | 1690.4 → 1367.1 | 7/7 | −298.0 | 152.38 → 125.50 |
| Heap | 794.8 → 648.9 | 7/7 | −121.6 | 57.15 → 39.77 |
| Hash | 212.0 → 194.7 | 5/7 | −34.5 | 12.89 → 8.78 |
| SHA-256 | 602.3 → 512.2 | 5/7 | −80.3 | 54.08 → 48.57 |

Selected Set reads were **424,304 Buffer gets and 309,586 snapshot front
calls**, all fast after the change and all generic before. Heap kept 224,710
gets and 120,960 sets; SHA kept 136,272 gets and 1,088 sets. This removes
invocation work without eliminating a language-level call or I/O operation.
Run `tests/Msync.Runtime.Tests -- --inline-native` for exact format-5 paths,
raw-cell and lazy fallback, and diagnosed failures. The before/after native
hashes begin `bf99379a5108` / `4d561e807ecf`; raw pairs, SQLite databases
and the reproducer are under `.temp/native-stack-lane/`. Hash and SHA timings
varied on the shared host; do not interpret seven pairs as a tail estimate.

## Borrowed source-call activations

The [CPython frame design](https://github.com/python/cpython/blob/main/InternalDocs/frames.md)
uses contiguous stack storage for the ordinary activation and materializes
longer-lived frame state at a separate boundary. [HotSpot's template
interpreter](https://github.com/openjdk/jdk/blob/master/src/hotspot/cpu/x86/templateInterpreterGenerator_x86.cpp)
similarly realizes the JVM's logical frame layout with addresses inside an
interpreter stack. Neither implies that msync should put async work or escaped
capture state on a native stack. The transferable idea is to separate the
language's private slots from physical storage: allocating a new managed array
for every source call is not part of the language contract.

Exact SQLite profiles of four frozen bound O2 artifacts showed a useful shape.
Set's 478,589 comparator activations use only two parameters; the 309,586
node-access activations and several other read-only helpers add one capture.
Heap has 191,943 comparator calls and a 24,575-call read-only helper; SHA has
130,560 small single-capture Buffer helpers. Hash's hot slot search instead
needs three fresh writable locals beyond its parameters/captures. The new
internal ABI borrows the suspended caller's argument slice only for standalone
source calls that need no additional locals and have at most one captured
value. The consumed callee operand holds that one capture; ordinary arguments
are normalized and materialized once, as before. No name, instruction pattern,
or cached conclusion about a closure's contents admits this route.

The callee owns logical slots but not the physical argument slice. At its
first slot mutation or capture promotion, it detaches into a private array,
retaining any existing Cell reference. The original caller argument slice
stays live through work-owner join and returned-Process classification;
writes to newly private parameter slots cannot alter those roots. Capturing a
private slot yields a stable Cell before the slice can be released. The
caller cannot pop or grow its operand stack until nested execution and its
cleanup complete, even when the callee suspends. Session roots still use
private physical slots. Large frames, extra captures and REPL execution keep
their original paths. No work-owner elimination, frame pool, new artifact
format, or second bytecode interpreter is required.

In the SQLite `vm.function` aggregate, `borrowed_frames` counts actual
admissions and `detached_frames` counts first detachment. Detailed frame spans
also expose `frame.storage` at entry. The disabled profile has no counter work;
detachment's structural branch is charged to local mutation, not every opcode.
The Linux Native AOT CLI on the four checked images records borrowed/detached
counts of 940,146/1 (Set), 241,098/1 (Heap), 1/1 (Hash), and 153,680/0 (SHA).
Each trace completed and produced the exact expected result or digest.

A Native AOT VM-only comparison executed identical frozen Set/Heap/Hash/SHA
artifacts and verified each result. Both variants had the same inline indexed
native operations. Six alternating before/after pairs on CPU 8 recorded
execution wall time; child user+system CPU includes launch/exit and is a
separate measure. Allocation is process-wide cumulative managed bytes in the
execution interval, not RSS.

| Workload | Managed bytes, prior → borrowed | Paired wall delta | Paired process CPU delta |
| --- | ---: | ---: | ---: |
| Set | 124,141,328 → 82,843,096 | −19.2 ms | +8.9 ms |
| Heap | 36,085,392 → 26,441,512 | −56.7 ms | −40.1 ms |
| Hash | 8,758,992 → 8,758,992 | −4.9 ms | −9.7 ms |
| SHA | 48,566,120 → 41,163,368 | −1.5 ms | −15.7 ms |

These controls support the allocation claim and a plausible Heap latency
benefit, not an independent all-four speedup. Hash has no substantial
borrowable call volume; its observed fluctuation is a useful control.
A previous COW-only comparison on the older native-call ABI was mixed.
Standalone small-array pools reduced allocation too, but were not a robust
latency win; adding reusable operand arrays made the cost/noise worse.
Combining locals and the operand stack in one allocation did not help: the
extra base-index work on every operand and growth for deeper stacks made it a
poor fit for this dispatcher. A broad synchronous owner-wrapper split
regressed the Set control with unchanged allocation. Keep those variants out
of production.

The focused profile/format-5 checks exercise live captured writes, a returned
Cell, source slots below the call, nested raw-Cell argument normalization,
session fallback, cancellation and failure on actual asynchronous input.
The ReturnedProcesses journeys separately check process publication,
reassigned arguments and caller/session ownership. Disposable raw sample
rows and exact scratch source for the alternative designs are in
`.temp/frame-lane/`.

## Runtime target boundary (November 11, 2026)

The [tiered execution design](tiered-execution-v086.md) supersedes the
recommendation to start with same-block fusion. It records the production
private-number loop tier and the split between portable artifacts and
process-local execution versions.

### Split borrowed environments and live cells

A standalone, exact-arity source call with no extra local slots can borrow
both the suspended caller's normalized arguments and a VM-owned multi-value
closure environment. Closures with two or more captures use a private
read-only wrapper around the owned references; borrowing never aliases a
host-supplied mutable list. A referenced Cell is not immutable. The old
zero/one-capture representation remains valid, and the caller's argument
roots remain live through work-owner join. Session execution and frames
with additional locals continue to own private storage.

The slot view detaches only when an operation replaces a physical reference
or promotes a previously ordinary slot. Reading or writing through an
existing Cell keeps the same location without copying either environment or
arguments. `InitializeLocal` replaces the reference, including when it is
a Cell, and therefore detaches before the replacement. On detachment the
VM copies original references, not dereferenced payloads. Returned Process
classification still examines original captures and caller arguments, not
the detached or rebound frame. This keeps ownership separate from the
physical frame convention. [LuaJIT upvalues](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_obj.h)
are another example of representing location identity separately from the
payload; msync does not import LuaJIT's stack or open-upvalue lifetime model.

SQLite `split_frames` is a subset of `borrowed_frames`; `detached_frames`
records actual copies. Diagnostic entry spans distinguish
`closure_and_caller` from the existing `caller_slice`. A native numeric
region can now use the same slot view; committing a changed ordinary value
uses the normal detachment path. A straight-line numeric continuation may
read the immediate Number in a Cell it does not write. A writable Cell
still fails the region's readiness guard, including aliases through two
different slots. This works because no host effect executes between the
read and use. It is not general Cell unboxing, native Cell mutation,
or snapshotting a shared location across effects.

The ordinary and split slot accesses specialize the same interpreter
body on a value-type layout marker. The contiguous Native AOT accessor
loads the biased array slot directly; the split accessor alone tests
the capture boundary. Detaching a split frame makes its capture prefix
empty without changing executable versions. The Linux assembly receipt
shows both accessors; compared with the prior unified-layout build,
the two specialized interpreter variants add approximately 56 KiB to
the packaged executable.

The [split frame receipt](../../artifacts/benchmarks/split-frames-v086/README.md)
records the paired corpus, actual Win-x64 Wine replay, Process ownership
tests, and no-telemetry paths. Standard signal takes 196,611 split calls
and 196,482 native suffixes with one total detach; managed allocation
drops from approximately 69.14 MB to 46.64 MB. Same-artifact child CPU
pairs show a benefit on one seed and essentially neutral time on the
other. The initial B+ wall slowdown does not reproduce in that repeat.

### Borrowed numeric arguments

The suspended caller keeps both its reference operands and its numeric
sidecar alive until the callee and work-owner join have completed. In a
borrowed call, a null reference slot still denotes a raw binary64 argument.
An ordinary Cell argument is normalized once, as before; it is not
reinterpreted as a raw numeric location. A distinct value-type frame layout
handles the borrowed raw case, so ordinary contiguous and boxed split
dispatch need no raw-slot tests.

A canonical local load can put the argument straight into the callee's raw
lane, and native readiness can read it without constructing a Value. A
canonical return of that argument materializes it directly without creating
a callee sidecar merely to box it. Storage reads and capture promotion
materialize when they actually need an object; the suspended caller's
representation may become boxed but its original argument value does not
change. Detachment copies still-live inputs to private storage and does not
materialize a dead numeric slot that the operation immediately replaces.
Raw numbers cannot root Processes, so ownership traversal never boxes them
just to check reachability. Reference inputs retain the original root
semantics even when they follow a raw argument or are rebound by the callee.

SQLite `raw_arguments` counts arguments actually lent in the raw lane, not
all numeric arguments or optimized-away predicate calls. No return ABI,
source/HIR requirement, or format-5 representation is changed.

The [raw argument and bound text receipt](../../artifacts/benchmarks/raw-arguments-v086/README.md)
records the combined corpus, Windows-branch replay and process/bit-exact
tests. Signal lends 196,608 raw values per seed and avoids exactly that
many Number boxes, with about 4.72 MB lower managed allocation. The first
integrated same-artifact CPU repeat found a 4.2% / 7.1% Heap non-target cost.
Its raw-argument count is zero. The frame's optional raw carrier now holds
only the caller's sidecar reference. Observation counts the null numeric
markers in the original borrowed slice before dispatch; telemetry-disabled
calls do not maintain a second argument count. A bound Number-text target
is consulted only at a ready text cast, using the active frame's image
even if a nested session call refreshed the interpreter's current image.

### Synchronous frame completion

The interpreter still installs a fresh work owner before starting the
frame. When dispatch completes successfully and that owner has no pending
tasks or Processes, it restores the caller immediately and returns the
original completed `ValueTask<Value>`. The common case does not initialize
an additional async ownership state machine. This is a state reduction,
not a claim that the old synchronous `ValueTask` allocated on the heap;
see the [.NET async builder explanation](https://devblogs.microsoft.com/dotnet/how-async-await-really-works-in-csharp/).

Suspended frames, faults, and successful frames that still own work share
one asynchronous completion path. It transfers reachable Processes,
joins non-escaping work, and restores both the interpreter's and the
session's active-owner state in a finally block. Its roots are still the
result, captures, session roots and **original** caller arguments, not
values after parameter rebinding. A borrowed caller is not resumed
between dispatch completion and this handoff. Neither bytecode format nor
native-region continuation changes.

The subsequent [ordinary-call ABI comparison](../../artifacts/benchmarks/call-abi-v086/README.md)
separates this outer ownership finisher from the dispatcher itself.
The canonical opcode loop is synchronous until a selected operation
actually suspends. It then saves one activation continuation; the driver
awaits the selected operation, updates the saved stack, and resumes at the
original successor without replaying an admission, call, or iterator move.
The same source-call continuation handles a native prefix that reaches
a canonical effect. A separate scratch lazy-owner representation did not
establish a common performance improvement and was not retained.

The [completion receipt](../../artifacts/benchmarks/frame-completion-v086/README.md)
records the final Linux Native AOT build, matching execution counters, and
same-artifact comparisons. Signal keeps its allocation improvement and the
ready text conversion lanes remain active. The fresh-CLI Heap result still
varies by seed (+6.12% / −2.28% process CPU versus the pre-raw build in
the targeted replay); the [attribution probe](../../artifacts/benchmarks/raw-arguments-v086/heap-attribution/README.md)
rules out lost interpreter/JIT paths, not all startup and code-layout
effects. B+ wall results also do not establish a general speedup.

### Private Number local lane

The bound execution targets now select a private numeric lane for standalone
functions with a plausible repeated raw store, or several independent
arithmetic stores. The selection is an amortization heuristic, not a
runtime type proof or artifact restriction. Captured writes do not admit a
frame. The actual stack representation still determines whether a store
can stay raw, and a StoreLocal to an actual Cell always goes through the
ordinary shared location. The contiguous and split boxed dispatchers do
not take the raw-store path.

A selected frame allocates its owned binary64 sidecar on the first raw
write. The null reference marker denotes the raw lane, not language Null.
An ordinary reference store replaces that marker. A borrowed activation
must detach before replacing a local; any live raw arguments then move to
the private copy without changing original caller locations. LoadStorage,
LoadCell and the public Value return materialize when needed. The numeric
interpreter and machine-code exits use the same commit path, including
cancellation, without constructing a Number wrapper for every changed
private slot. Persistent session frames stay canonical so externally
inspectable roots and values need no flush recipe.

Profile telemetry distinguishes `raw_local_writes` from
`numeric_local_commits` (managed or native numeric region exits). Neither
counts Cell writes or merely borrowed numeric inputs. See the
[private-local receipt](../../artifacts/benchmarks/private-number-locals-v086/README.md)
for joint and same-linked measurements. The analogous
[HotSpot double local handlers](https://github.com/openjdk/jdk/blob/master/src/hotspot/cpu/x86/templateTable_x86.cpp)
encode representation directly in their local operations; msync derives
an optional execution layout without adding wire opcodes to format 5.

The owned sidecar uses a separate numeric offset. New private arrays
start at the function's first written local, not at slot zero; the
reference layout and all logical slot IDs stay unchanged. Detaching a
borrowed frame extends that suffix downward if an unboxed caller argument
still lives below it. This copies the old bits without letting a native
commit or a later materialization access a caller location. The opted-in
`number_sidecar_arrays` and `number_sidecar_slots` totals expose carrier
cost independently of Number boxes and logical writes.

### Scope storage consumers

A boxed `LoadStorage` operand is important when a field shares an actual
Cell. But a raw local can carry its binary64 bits directly to the existing
Scope allocator when the operand is used only by eligible Scope
construction. The bound region importer identifies the definition and
its consumers. Use requirements propagate from block parameters back to
their incoming operands, including across backedges; Dup does not create a
new definition. A generic consumer such as Return (including implicit
return) keeps the original materializing path. A singleton Scope also
declines because the allocator cannot use a numeric page for it.

The usual producer shape is cheaper to prove: the final `A` loads of a
consecutive LoadStorage run immediately before `MakeScope A` can be
recognized in one scan, without constructing a CFG. Any earlier operand
stays outside that proof; nonadjacent uses fall back to the region graph
only when needed. The record-stream fixture saves another approximately
49–50 kB of one-time execution allocation on the 512-row cold case.

The load copies the value at the original PC, not at construction time.
Effects and suspension between field evaluations cannot change the
snapshot. Cells and other references remain the same objects and follow the
canonical allocation path. The per-image result is computed only when
an actual raw `LoadStorage` first needs it. No extra check is added to
the boxed dispatch layouts. The `raw_scope_storage_loads` profile metric
counts raw operand transport, not guaranteed box elimination: a multi-field
row with fewer than two direct numeric fields can still use plain storage.

This uses the same incoming-operand relation that
[MLIR describes for block arguments](https://mlir.llvm.org/docs/LangRef/#blocks).
It is not full partial escape analysis or deferred Scope allocation.
The [measured row and corpus journeys](../../artifacts/benchmarks/scope-storage-consumers-v086/README.md)
separate actual transport hits and allocations from fresh-process timing
and from unrelated non-hit workloads.
