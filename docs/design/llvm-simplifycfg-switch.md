# LLVM SimplifyCFG switch family

This source port follows LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`, primarily
`llvm/lib/Transforms/Utils/SimplifyCFG.cpp:6207–8765`. The source snapshot is kept
under `vendor/llvm-reference/llvm/`; its Apache-2.0 WITH LLVM-exception license
also applies to the translated partials. This document describes the switch
family owned by the `LlvmSimplifyCfg.Work` partial implementation, not another
optimization driver or LLVM backend.

## Source mapping

| Managed partial | Source mechanisms |
| --- | --- |
| `Switch.cs` | `SimplifyCFGOpt::simplifySwitch`, preserving source family ordering and option gates |
| `Switch.Range.cs` | `findContiguousCases`, range-to-ICmp conversion, unreachable defaults, known-bit/sign-bit/possible-value dead cases, condition-to-PHI forwarding |
| `Switch.Select.cs` | constant-pool instruction folding, per-case PHI results, unique result grouping, masked/two-result select construction and CFG cleanup |
| `Switch.Replacement.cs` | `SwitchReplacement`: single-value, affine arithmetic, packed integer bitmap, private constant table; source legality/density/register-fit policy |
| `Switch.Lookup.cs` | case/default collection, table indexing/range growth, hole bitmaps, range guards, PHI reconstruction and table-compare reuse |
| `Switch.Transforms.cs` | sparse-case rotate reduction, power-of-two population-count/trailing-zero transformation, UMin saturation defaults, concrete defaults, SCmp/UCmp switch-to-branch |
| `Switch.Duplicates.cs` | `EqualBBWrapper`, cached PHI-input hashing, `mergeIdenticalBBs`, duplicate arm/predecessor wrappers |

These are optimizer integer interval facts and control-flow transformations.
They do not change the language's built-in Range object or its object layout.

## Representation and integration

The family shares the driver's `Work` state, options, tuning values, transform
builder, telemetry, loop-header weak handles, and real `LlvmDomTreeUpdater`.
Every source CFG insertion/deletion update is retained. The implementation does
not replace incremental DT updates with whole-function rebuilding. Common PHI
removal and block splitting use the source-ported `LlvmBasicBlockUtils` methods.

The switch condition and case constants retain their arbitrary-width APInt
representation. Range conversion uses unsigned sorting and wrapping complements,
not host floating-point values. Sparse range reduction is limited to legal
condition widths at most 64 bits exactly where the source imposes that limit.
It emits the standard `llvm.fshl` intrinsic. Power-of-two reduction emits standard
`llvm.ctpop` and `llvm.cttz` calls, including the source's `i1 true` poison-on-zero
argument. SCmp/UCmp consume their actual registered intrinsic identities, and
UMin matching does not guess semantic behavior from ordinary function names.

Target decisions use the source generic TTI implementation and the DataLayout's
explicit legal integer widths. An empty legal-width set stays empty: the port
does not assume that the host has legal 64-bit integers. Density thresholds are
the source's 10%/40% policies, not a replacement heuristic. Arithmetic-only
conversion still rejects bitmap, table and hole-mask representations when the
lookup-table option is disabled. `HasOptSize` and `NoJumpTables` are actual
represented function attributes.

Table construction preserves source poison/undef hole behavior, signed-wrap
calculations for affine maps, bitmap packing, table element narrowing, private
constant globals, global unnamed-address state, preferred alignment, inbounds
GEPs, and load/extension types. `LlvmGlobalObject.Alignment` carries the source's
optional global alignment. Switch case mutation and default-unreachable queries
are exposed as thin Core IR operations.

Profile branch-weight metadata and ConstantExpr/vector constant forms are not
present in this IR's represented scalar switch inputs. No synthetic metadata or
fake constants are introduced for those absent forms; source profile-weight
arithmetic is not claimed by this slice. The source-supported scalar constant
kinds, registered intrinsic calls and CFG updates are implemented directly.
Assumption-aware dead-case/default queries use the shared actual assumption
cache API; the default query explicitly allows ephemerals as in the source.

## Observability and focused execution

The driver's detailed scope retains source/function/block context. Family
metrics count visited cases/instructions, cached PHI inputs, emitted expression
forms, pruned cases, inserted guards/masks, redirected duplicates, and successful
rewrites. Retention outcomes distinguish range shape, missing constant results,
result limits, legality/cost rejection, option/attribute restrictions, and sparse
power-of-two/range policies. The recording-disabled path does not build dynamic
retention keys.

`SimplifyCfgSwitchChecks` exercises the actual shared driver with real CFGs:
contiguous range comparison, known-bit dead-case/default elimination, nested
selects, affine/bitmap/global-table replacement, sparse rotate reduction,
power-of-two block splitting, UMin saturation defaults, and incremental DT use.
The integration owner serializes build and execution with the whole SimplifyCFG
cohort and the compiler's existing end-to-end user journeys.
