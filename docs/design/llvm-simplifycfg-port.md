# LLVM SimplifyCFG source port

## Source and ownership

The source snapshot is LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`.
The source algorithm is [`SimplifyCFG.cpp`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/SimplifyCFG.cpp), with the public option defaults from
[`SimplifyCFGOptions.h`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Transforms/Utils/SimplifyCFGOptions.h).
This is a managed source translation over the LLVM-derived IR, not a dependency
on an LLVM native library, a target backend selection, or a replacement rule list.

The represented scalar/CFG utility cohort is now published under
`src/Msync.Optimization/Llvm/LlvmSimplifyCfg*.cs`, with shared assumption-query
threading in `LlvmInstructionSimplify*.cs`. Disposable source drafts and focused
fixtures remain in `.temp/llvm-simplifycfg`; they are not product dependencies.
The separately owned function wrapper is integrated by the root agent after
the common cohort is ready.

| Source family | Managed files / owner |
| --- | --- |
| `simplifyOnce`, `run`, public per-block `simplifyCFG` | `LlvmSimplifyCfg.Driver.cs` |
| Public option defaults and source command-line defaults | `LlvmSimplifyCfg.Options.cs` |
| Unconditional/conditional driver, nested branch merge | `LlvmSimplifyCfg.Branches.cs` |
| UB-introducing PHI edges, first same-block UB witness | `LlvmSimplifyCfg.Undefined.cs` |
| Unreachable instruction / predecessor simplification | `LlvmSimplifyCfg.Unreachable.cs` |
| Common-destination branch fold, TTI cost and DTU updates | `LlvmSimplifyCfg.cs`, `.CommonDestination.cs` |
| Value-comparison gatherer, equality folding, store merge | `.ComparesGatherer`, `.EqualityComparison`, `.ConditionalStores`, `.ComparisonCloning`; [family note](llvm-simplifycfg-comparisons-port.md) |
| Common successor hoisting | `.Hoist*`; [family note](llvm-simplifycfg-hoist-port.md) |
| Common predecessor sinking and merge compatibility | `.Common.*` |
| Two-entry PHIs and speculative block execution | `.Speculation.*` |
| Known predecessor condition, comparison chains, select threading | `.Conditional.*` |
| Switch range, dead cases, select/arithmetic/bitmap/table, duplicate blocks | `.Switch.*`; [family note](llvm-simplifycfg-switch.md) |

## Driver and graph ownership

`SimplifyCFG(block, targetInfo, updater, options, loopHeaders, trace, tuning)`
corresponds to the upstream **per-block** utility. The function pass wrapper is
separate. The block utility repeats only when the source calls
`requestResimplify`; merely returning `true` does not introduce a new local
fixed-point policy. The source order is preserved: unreachable-block deletion,
constant terminator folding, duplicate PHIs, UB predecessors, merge into
predecessor, optional sinking/duplicates, two-entry PHI folding, then terminator
family dispatch.

`LlvmDomTreeUpdater` owns actual eager/lazy incremental graph updates. Families
emit the upstream ordered insert/delete updates, rather than invalidating and
rebuilding dominators after every rewrite. Shared `LlvmBasicBlockUtils` and
`LlvmLocalCfg` provide the translated PHI/deletion/edge-split operations. A
snapshot is used only where the upstream explicitly builds an ordered set or
vector before mutating use lists or predecessor lists.

The function wrapper supplies actual `LlvmWeakValueHandle` loop-header references.
Their Core deletion callbacks clear a deleted header immediately, including
deletion during a local block iteration; a detached managed block reference is
not used as a substitute for weak-handle semantics.

## Semantics and dependencies

The generic `LlvmTargetTransformInfo` and `LlvmInstructionCost` source models are
used for the real arithmetic/select/GEP/lookup decisions. Empty DataLayout native
integer-width information stays empty; host word size is not substituted for
`fitsInLegalInteger`. Source options and tuning defaults are distinct carriers,
not safety modes. An option being disabled by its upstream default does not
justify omitting its represented implementation.

The undefined-value helper preserves the source's first eligible use-list
witness, same-block instruction ordering, and intervening guaranteed-transfer
checks. Removing a conditional edge retains the surviving condition with a real
`llvm.assume`, registered in the supplied `LlvmAssumptionCache`. This intrinsic
has no runtime assertion or error branch. The nonempty assumption cache also reaches speculative-load/value-tracking
queries. Shared InstructionSimplify public entry points append optional
`assumptions` and `allowEphemerals` inputs, preserving existing positional
callers. The explicit `BinaryQuery` dispatch carries these into integer known
bits/ranges, floating-point classes, PHI poison checks and freeze queries. The
instruction dispatcher supplies the instruction when the incoming query has no
context, exactly as `simplifyInstructionWithOperands` does at source line 7894;
the direct scalar entry points, which have no instruction to supply, keep their
null context. A source `with` query retains the cache automatically.

Not every fact query receives the caller cache: the upstream aggregate-insert
poison checks intentionally call the no-query overload (InstructionSimplify.cpp
5451 onward), and comparison implication constructs a fresh `SimplifyQuery(DL)`
(ValueTracking.cpp 10203). Those paths remain context/cache-free, rather than
introducing extra assumptions that the source algorithm did not request.

Source spans and per-operand language type views remain diagnostic/runtime-ABI
annotations. No canonical constant is rewritten to acquire a use-specific type.
`Range` remains an ordinary runtime object pointer and receives no CFG-specific
rules. Heap/file operations remain the three existing extension instructions,
with the shared effect/speculation classification; this pass adds none.

## Representation boundaries

The current scalar IR represents conditional/unconditional branches, switches,
returns and unreachable. It does not represent indirect branches, EH pads,
invoke/callbr, resume/cleanup/catch dispatch, block-address constants, masked
vector memory intrinsics, debug records, or branch/profile metadata. Source
branches requiring these objects are not relabeled as scalar implementations.
For example, the real generic TTI reports no conditional-faulting support;
there is no invented masked-load lowering behind that result. Unprofiled branch
profitability follows the actual source unknown-probability path, not fabricated
weights. Function attributes and assumption bundles are consumed only where
there is an actual corresponding carrier and source query.

## Telemetry and verification

The block span records function/block/source context and source-local work
counts. Family spans/counters retain decisions such as speculation cost,
compatibility rejection, instruction cloning, PHI edits, assumptions, deleted
cases, merged blocks and requested resimplifications. Recording-disabled callers
do not allocate the metrics map. Shared CFG and value-tracking operations feed
actual work into the same map.

The integrated cohort builds with zero warnings and errors
([build log](../../artifacts/diagnostics/llvm-scalar-cohort/build.log)). The
focused real-IR CFG suite now passes with the source-context, PHI dispatch and
raw range-cache fixes included
([execution receipt](../../artifacts/diagnostics/llvm-scalar-cohort/cfg.log)):

| Exercised family | Passed checks |
| --- | ---: |
| Equivalent integer comparisons | 36,208 |
| Conditional threading/comparison chains | 7 |
| Driver, nested branches, null validity and unreachable | 8 |
| Speculative execution and two-entry PHIs | 8 |
| InstructionSimplify query/cache integration | 3 |
| Switch forms | 9 |
| Integer/definedness assumption facts | 16 |
| Floating-point assumption facts | 7 |
| Pointer facts | 19 |

These are actual executed IR properties, not counts of production user journeys.
The same integrated compiler also passes the separate ADCE journey's four
source and four artifact executions, including prefix IO and pending if/match
failures. Its dead-recurrence execution hits fall from 8 to 0, branches from 1
to 0 and PHIs from 3 to 1, while file reads/writes and ordinary Range calls are
preserved ([ADCE receipt](../../artifacts/diagnostics/llvm-scalar-cohort/adce.log)).
Those rewrite benefits belong to that integrated ADCE journey; they are not
attributed solely to SimplifyCFG. Native AOT / public CLI execution is tracked
separately by the root agent.

### Integration findings retained in the implementation

An initial speculation fixture incorrectly expected assumed-nonzero variable
UDiv to become speculatable. The source accepts only a constant nonzero divisor
for UDiv/URem. The corrected fixture retains that negative case and uses the
actual aligned/dereferenceable load path as the positive cache consumer. No
arithmetic speculation rule or cost threshold was changed to satisfy it.

A second integration probe (`assume(x == 7)` followed by `icmp x, 7`)
identified a genuine missing shared consumer: raw `computeConstantRange` was
forwarding its query cache without applying the final source assumption-range
intersections. The correction ports ValueTracking.cpp 11068–11104 directly,
including range bundles, left-operand comparison matching, validity checks and
RHS recursion at the assumption context. The simplifier continues using raw
`Compute`, not an invented replacement with `ComputeIncludingKnownBits`, and
the original failing assertion is retained. This is why cache-field plumbing
and successful unrelated known-bits checks do not establish end-to-end cache
consumption for every caller. Existing LoopSimplify callers of the public
null-TTI `FoldBranchToCommonDest` remain a supported, separate ABI; the full
per-block utility supplies TTI and DTU instead of silently inheriting that
restricted calling configuration. The root agent serializes cohort builds and actual production journeys. The
passed represented-CFG suite does not imply coverage of the absent LLVM IR
representations listed above or establish a wall-clock speedup.
