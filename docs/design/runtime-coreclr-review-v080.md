# Independent CoreCLR mechanism review

## Decision

Research snapshot: November 9, 2026. This review independently challenges the
CoreCLR analogies in [the runtime proposal](managed-runtime-optimization-v080.md).
It inspects upstream source and msync's current control flow; it does not claim
new runtime benchmark results or modify production code.

The useful transfer is **guarded resolution with an ordinary fallback**, plus
careful accounting for profile cost. It is not an automatic transfer of CLR JIT
optimizations to interpreted msync programs. Predecoding target tables and
passing a selected target separately from the actual closure is the least
speculative first experiment. A cache should follow only if retained O2 sites
still pay material resolution cost.

## Version and evidence classification

The [official support table](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
reports .NET 10.0.14 as the supported LTS servicing release and .NET 11 RC3 as a
go-live prerelease at this snapshot. The source anchors below deliberately use
`v10.0.0` as a reproducible .NET 10 baseline; they do not imply that every detail
of the latest servicing build was benchmarked.

Some tagged design documents retain historical proposal language:

| Reference | What it establishes | What it does not establish |
| --- | --- | --- |
| [GuardedDevirtualization.md](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/coreclr/jit/GuardedDevirtualization.md) | The checked direct-call/general-call design and its cost trade-off | Its opening still describes a .NET Core 3.0 proposal; tagging this text v10 does not make all proposals current deployment facts |
| [indirectcalltransformer.cpp](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/jit/indirectcalltransformer.cpp) | Actual `GuardedDevirtualizationTransformer` checks, argument spilling, direct/fallback construction | No msync speedup follows from CLR interface-call measurements |
| [DynamicPgo.md](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/features/DynamicPgo.md) | Collection/representation/consumption model and sparse instrumentation rationale | Historical prototype statements and suggestions are not all shipped guarantees |
| [fgprofile.cpp](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/jit/fgprofile.cpp) | Actual profile-source distinctions and rejection of mismatched/failed profile reconstruction | Profile observations are neither semantic truth nor permission to remove a required fallback |

This distinction matters when deciding which mechanism to prototype: use the
implementation for present behavior and the design documents for rationale.

## Checked calls: preserve data and evaluation, not just the destination

`GuardedDevirtualizationTransformer::CreateCheck` can guard a method table or an
actual method target. Before the guard it spills effectful arguments and a
complex receiver so that the transformed branches do not reevaluate them. Its
last-check elision is conditional on an explicit exhaustive-candidate proof
(`GTF_CALL_M_GUARDED_DEVIRT_EXACT`), not a favorable observed histogram.

For msync the corresponding requirements are:

* A Function storage type is not a callable identity. Guard the already evaluated
  actual function value's code identity and use its current captures.
* If argument evaluation changes the binding holding the callee, this invocation
  still uses the callee value evaluated before those arguments. Do not reread the
  binding to obtain a supposedly more current target.
* Native functions retain their native identity; `Call.B` alone is not proof of
  a nonnative callee. A missing specialization retains its selected error.
* A source site is not closed-world merely because the current image has a short
  target list: later REPL submissions can add code and replace Function values.

The same upstream file explains Native AOT fat function pointers as a method
pointer paired with its instantiation argument, avoiding generated runtime
stubs. This is a useful analogy for closures: retaining a cached code target
does not justify retaining another invocation's environment.

Locally, `Interpreter.cs` currently constructs adjusted `FunctionValue` records
in the `Call` branch and `Specialize`. Removing that temporary can be sound if
the selected function index is passed separately while the actual `Captures`
and `NativeName` continue through the existing invocation/owner path. It is not
necessary to speculate on field contents to remove this allocation.

## Native AOT PGO is a different plane

[Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
explicitly compiles IL at publish time and has no runtime CLR JIT. Consequently
`DOTNET_TieredPGO` is not a way to recompile guest bytecode.

The [Native AOT build targets](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/nativeaot/BuildIntegration/Microsoft.NETCore.Native.targets)
set `IlcPgoOptimize` for the Speed preference, include runtime-package MIBC files
under that setting, and pass `MibcFile` items through `--mibc` to ILC. This is
evidence of publish-time profile consumption, not evidence that the app has
already collected a representative application profile. A host-PGO experiment
must hold guest source/artifacts constant and record the actual MIBC inputs and
build flags; do not attribute its results to a new guest execution tier.

## OSR, fallback and working-set costs

[The CLR OSR design](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/features/OnStackReplacement.md)
distinguishes migration from an unoptimized frame from deoptimization of an
optimized one. It describes patchpoints and live-state correspondence, while
more general deoptimization remains a larger problem. A normal GDV else branch
does not reconstruct eliminated objects or an arbitrary active frame.

msync's `Interpreter.SessionImage.cs` already replaces code/constant projections
at function entry, scoped by image generation and binding context. That is a
useful invalidation hook, not an OSR implementation. New caches/plans must be
discarded or rebuilt together with `dispatchTables` in `RefreshSessionImage`.
Old deferred callers retain their origin context; changing code and constants
as an unmatched pair is incorrect. A real mid-loop transition would additionally
map operand stacks, Cells, iterator cursors and task ownership.

[Virtual Stub Dispatch](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/coreclr/botr/virtual-stub-dispatch.md)
records why lazy resolution can lower global working set yet increase per-site
startup cost; the documented design is interface dispatch rather than universal
virtual-call replacement. For millisecond scripts, a preallocated cache array
for every instruction can lose even if its hot-hit path is faster. Site-local
lazy metadata must be measured against the existing parsed-table and field-hint
paths, not against an artificially uncached baseline.

## Three falsifiers for the experimental owners

These are semantic and economic discriminators, not an extra test matrix.
Confirm the intended site remains shared after O2 cloning/inlining/unrolling;
two source calls do not automatically exercise the same runtime cache.

1. **Closure and argument ordering.** Repeatedly select two closures with the
   same code identity but different captured Cells. Swap a same-typed callable
   mid-run; also let an argument mutate its binding. Results must use each
   invocation's evaluated callee/captures, evaluate arguments once, and use the
   replacement only on subsequent appropriate calls. A saved FunctionValue
   snapshot or a type-only key fails this discriminator.
2. **Layout and image lifetime.** Read the same field site from equal layouts
   with different values, mutate through an alias/captured method, then rebind
   to reordered/different layouts. Refresh the REPL image while an old deferred
   range retains directory A and a new captured Path retains B. Cache hits read
   current payloads; layout mismatch uses normal lookup; image changes clear
   dispatch plans without rebasing A. Existing valid slot hints are a control,
   not a claimed new IC benefit.
3. **No-work and observable-failure economics.** Include a literal-return
   executable and one small copy as cold controls. In the retained hot case,
   compare stable/bimorphic/high-diversity targets, selected missing-member or
   dispatch errors, and owner/barrier completion. Report preparation plus first
   execution separately from prepared VM p50/p95 and allocations. Reject a
   proposal that wins only after static work was artificially disabled or by
   moving an error/effect across its original boundary.

These tests challenge the proposed implementation. Upstream GDV, PGO and OSR
provide mechanisms and warnings, not a correctness or profitability certificate
for msync.
