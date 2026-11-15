# Persistent LLVM-derived session lowering

## Existing contracts

* `Compiler/Session/SemanticSession.cs` stages semantic facts, dispatch tables and
  HirLoweringLayout during compilation. Successful compilation commits code
  obligations before execution. Runtime failure does not undo those obligations.
* ReconcileSubmission publishes a new exported name only when its stable entry
  slot is initialized. A pending typed NativeDatum counts as initialized; value
  observation does not demand pending work.
* `NativeSemanticSessionValues` resolves exported slots through Locations,
  callable identities through Callables, and Scope declaration origins through
  AllocationSites. These maps are part of the existing semantic/runtime bridge,
  not optional diagnostics.
* NativeInterpreter.UpdateProgram retains static states by index and replaces
  the execution image. Previously returned closures/Ranges retain callable IDs,
  capture addresses and path literal origins. Earlier instructions with an open
  SessionSlot must consult the latest published dispatch table.
* NativeFrame.Initialize owns canonical persistent entry cells in
  `sessionLocations`; Bind updates that same dictionary for persistent slots.
  GetLocalValue and IsLocalInitialized observe those cells, including pending
  values. SessionRoots determines accepted lexical resource reachability.

These external contracts remain unchanged. SemanticSession's Compile path uses
the same staged graph/target pipeline; the public CompilerSession API and
AcceptSubmission/ReconcileSubmission lifecycle do not change.

## API and generation ownership

`LlvmHirLoweringSession.Lower(HirModule, trace)` produces an ordinary LLVM-derived
graph for the existing pass boundary. `Emit(result, trace)` emits the existing
NativeHirLoweringResult (NativeProgram plus HirLoweringLayout), without invoking
the old HIR lowerer, Native optimizer, or LLVM backend. Lower and Emit are
separate so callers can run the actual new IR passes, not a CanLower fallback.

Each generation has a fresh LLVM module/context. The session retains only
append-only target descriptors, stable HIR-to-native identities and emitted
Native code, not a graph-indexed heap analysis or a table retaining dead SSA
values. A staged generation forks descriptor/identity registries and is committed
after target emission succeeds. A failed source compile cannot poison reused HIR
IDs; a later runtime failure deliberately does not roll back emitted identities.

Functions/templates, native types/layouts, literal constants, statics and source
allocation sites preserve indices across generations. Newly emitted functions
replace their prior code at the same callable ID; unreachable older functions
remain available to already returned values. Target-added descriptor tails are
imported back into the producer registry before the next generation, preventing
an appended target helper type from colliding with a later source type.

## Lexical storage model

Exported lexical locations and prior imports are ordinary one-field LLVM static
cells. Their identities come from existing HirLocationId, not from object
identity tests. Capture environments contain their direct field pointers.

At the Native boundary, the existing stable persistent entry slot can itself be
the canonical cell: GEP(global, 0, 0) becomes FrameAddress(stableSlot). There is
no second semantic cell or entry root-identity guard. Only exported proxies use
persistent Local role; nonexported entry temporaries must not accidentally enter
sessionLocations or collide with a newly added exported slot in a later image.

A naive mapping of all exported cells to NativeProgram.Statics would change
resource ownership: the runtime roots every StaticState allocation, including
shadowed lexical cells. The existing persistent-local cell mapping preserves
current SessionRoots-based lexical lifetime while ordinary singleton globals
continue to use the existing static mechanism. Source nonentry access uses the
existing explicit location-capture prefix, not an entry-frame lookup algorithm.

Session dispatch metadata is copied directly from HirSessionDispatch identities
into the existing runtime tables and Call/AdaptFunction C operands. No runtime
compiler, target discovery, root admission proof or generic HIR evaluator is
introduced.

## Coverage status

The session class, staged registries, fresh source modules, canonical native
root relocation and open dispatch metadata are implemented. SemanticSession
Compile is now wired through LlvmHirLoweringSession.Lower, the shared
Compiler.OptimizeLlvm entry, and session Emit. There is no feature flag,
CanLower probe or Native optimizer fallback. Its compiler.lower and
compiler.optimize phases are preserved; compiler.target records final Native
emission. The accepted-source checkpoint below predates the production switch.
Standalone Compiler.LowerInstructions also uses the LLVM-derived graph and
shared optimizer before the same Native target boundary.

The source harness in `tests/LlvmSession` passed 15 accepted submissions in each
of two modes (unoptimized and explicit mem2reg/DCE). It uses the real source
binder, semantic analyzer, LLVM-derived producer/target and NativeExecutionSession.
It covers mutable captured roots, Scope aliases and member closures, shadowing
while older closures retain their original locations, escaped captures, generic
aliases, deferred callback replacement and genuinely pending process output
retained across accepted submissions. Three pending-root observations per mode
remain undemanded until the final file output consumes the future.

Evidence is `artifacts/diagnostics/llvm-source-session/{build.log,receipt.json}`.
The Release build passed with zero warnings/errors. Harness SHA-256:
`7d07804854f3fcf0c7338f66b6b472af79b583ef6bca7c77c32d77e2aff0e84b`;
HIR assembly SHA-256:
`66307f0c4e6466c5761643a10e48818a98810f7e73f4fc68eff2c74067a2cbbd`.
Run `dotnet run --project tests/LlvmSession -c Release -- --output
artifacts/diagnostics/llvm-source-session` from the repository root. The harness
has its own explicit compiler friend assembly; unrelated historical HirBinding
tests are not its build dependencies.

### Production CompilerSession acceptance

The separate `--production-session` journey uses public CompilerSession Compile,
AcceptSubmission and ReconcileSubmission with NativeExecutionSession. It passed
on the actual migrated production path, without a feature flag or legacy route:

| Optimization | Submissions | Accepted | Reconciled runtime failures | Numeric expectations | Genuinely pending root observations | Checked files |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| O0 | 12 | 9 | 3 | 6 | 3 | 1 |
| O2 | 12 | 9 | 3 | 6 | 3 | 1 |

The failures cover initialized declarations before an error, uninitialized failed
and unreached declarations, a failed shadow initializer retaining the previous
binding, and an initialized pending alias surviving partial failure. Mutable
captured roots, Scope alias mutation and undemanded process output retain their
actual runtime state across Accept/Reconcile boundaries. Trace checks require
the new lower/target phases and reject the old optimizer-function path.

Evidence is
`artifacts/diagnostics/llvm-production-session/{build.log,run.log,receipt.json}`;
the Release build reports zero warnings/errors. Reproduce with
`dotnet run --project tests/LlvmSession -c Release -- --production-session --output
artifacts/diagnostics/llvm-production-session`. This production checkpoint is
separate from the earlier accepted-only 30-submission receipt.

## Captured export initialization

The first real session run exposed a source ownership split: exported `state`
was initialized in a fresh captured heap cell, while the next submission wrote
the canonical session global. Old read/set/make closures consequently retained
1 after `state=3`; combined with the correctly mutated object field (4), the
observed result was 5 instead of 7. The same split affected a mutable callback
export used by a deferred Range.

Captured declaration allocation now uses the session root's canonical Address
when that lexical location is session-owned. Only non-session captured locals
and fresh iteration cells allocate fresh heap storage. This is a compile-time
ownership choice, not an entry identity guard, a value snapshot, a second proxy
or a copy from one cell into another. The declaration store, later assignments
and all capture environments name the same original location.


The production acceptance cohort was rerun after late InstSimplify and per-use
address ABI views were integrated. Both O0 and O2 again passed the same 12
submissions (9 accepted, 3 reconciled failures), 6 numeric expectations,
3 genuinely pending root observations and one checked output file per mode.
The later receipt and build/run logs are retained separately under
`artifacts/diagnostics/llvm-production-session-instsimplify/`.
