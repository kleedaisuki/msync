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
