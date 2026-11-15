# LLVM Core attributes, instruction flags and droppable uses

## Source and ownership

This cohort translates LLVM commit `a55e64432a516fd6507d3ecfba7407cfe425f940`:

- `llvm/include/llvm/IR/Attributes.h`, `Attributes.td` and
  `llvm/lib/IR/Attributes.cpp`: attribute payloads, type incompatibility,
  poison/UB masks, and attribute-set intersection.
- `llvm/include/llvm/IR/InstrTypes.h`, `llvm/lib/IR/Instructions.cpp`:
  callsite/declaration queries, capture and operand-bundle semantics.
- `llvm/lib/IR/Instruction.cpp`, `Operator.cpp`: copy/intersect/drop flags.
- `llvm/lib/IR/Value.cpp`: droppable-use replacement and deletion notifications.
- `llvm/include/llvm/Support/ModRef.h`: capture components and capture info.
- `llvm/include/llvm/IR/CmpPredicate.h`: same-sign predicate matching.
- `llvm/include/llvm/ADT/FloatingPointMode.h`: denormal input/output pairs.

`LlvmConstantRange` and `LlvmKnownBits` are Core data operations, not analyses.
Their pure-data partials live in `Msync.Core.Llvm`. Query walkers and analysis
state remain in `Msync.Optimization.Llvm`. `LlvmKnownBits.MinMax.cs` is split at
that boundary. Capture components, FP class masks and denormal modes likewise
have one Core definition. This avoids a Core-to-Optimization dependency and
reuses existing modular interval operations for range attributes.

These are LLVM integer-analysis ranges, unrelated to the language's ordinary
built-in `Range` object. No source-language capture mode, heap proof graph,
lifetime solver or extra IR opcode is introduced. The three project IR
extensions remain heap allocation, file input and file output.

## Attribute contracts

`LlvmParameterAttributes` carries immutable scalar/type payloads; function and
call own separate sparse parameter dictionaries and a return payload. The flag
enum identifies semantic kinds, not TableGen-generated ABI numbers. Absence
means the LLVM default: unknown capture, unknown memory effects, no asserted
nonnull/noundef/range, and an IEEE denormal environment.

| Query | Source behavior |
| --- | --- |
| `GetParamAttributes(i)` | Own dictionary only, including on calls |
| `ParamHasAttr(i,kind)` | Callsite or declaration; memory attrs account for reading/clobbering bundles |
| `GetParamAlign(i)` | Own callsite alignment only |
| `GetCaptureInfo(dataOperand)` | Site/declaration capture intersection; byval, deopt and assume bundles do not capture |
| `OnlyReadsMemory(dataOperand)` | byval, readonly or readnone; deopt pointer bundle inputs imply readonly |
| `GetRetDereferenceableBytes()` | Maximum site/declaration size |
| `GetRange()` | Intersection of site/declaration modular ranges |
| `GetReturnedArgOperand()` | Site returned argument first, declaration second |
| `GetDenormalMode(semantics)` | Single precision uses F32 environment; others use default |
| `GetVScaleValue()` | Positive singleton vscale range, otherwise zero |

Data operand numbers include operand-bundle inputs and exclude the final callee
operand. Argument numbers are their leading subset. All APIs use the existing
integer operand index; no independent capture-import index space is created.
`DoesNotCapture` is stronger than absence of full-provenance capture. SROA's
full-provenance test uses `LlvmCaptureInfo.CapturesFullProvenance`, not the former.

Call cloning copies its attribute dictionary, rather than sharing mutable
storage. `CopyAttributesFrom` copies attributes/calling convention, not bundles
or tail kind. Arity-changing rewrites construct compatible new parameter
attributes explicitly, as LLVM does.

Attribute intersection follows the source's per-kind rules: conjunction for
ordinary inferred flags, minima for dereferenceable sizes, capture union, FP
class-mask intersection, and range union (dropping a full result). Alignment
uses minimum except that byval alignment is ABI-preserved and must agree.
ABI/type-preserved attributes must match, rather than silently disappear.
`TypeIncompatible` exposes the source's safe-to-drop/unsafe-to-drop distinction.
These names describe LLVM attribute rewrite rules, not a new runtime checker.

## Flags and destruction

`CopyIRFlags`, `AndIRFlags` and `DropPoisonGeneratingFlags` operate on actual
instruction fields. Dropping poison flags retains non-poison fast-math flags.
For registered ctlz/cttz/abs intrinsics it clears the actual poison-flag operand.
`CopyIRFlags` retains the source distinction between conditional binary wrap
copying and unconditional truncation wrap copying, and unions GEP no-wrap flags.
Same-sign predicate info preserves the existing enum API while carrying the
explicit ICmp flag. Matching signed/unsigned relational predicates is allowed
only by that source same-sign rule.

The represented IR has no general LLVM metadata attachment graph. Annotation
helpers edit actual flags and call attributes; they do not invent metadata
presence or a metadata-dependent proof cache. Dropping site attributes does not
mutate declaration attributes inherited by later queries.

Droppable uses are actual assume uses. A dropped condition becomes true; a
dropped bundle input becomes poison of its original type and the owning bundle
tag becomes `ignore`. Bundle views wrap a stable array, so retagging is visible
to subsequent call queries. No fabricated use edge is introduced.
`Instruction.DeleteValue` destroys a detached, unused instruction by dropping
operands, notifying value handles, then destroying its name. `EraseFromParent`
first unlinks and then delegates to it. CLR garbage collection is not an IR
deletion event.

## Telemetry and focused integration

Core scalar queries do not own a process-global sink or a separate stopwatch.
The invoking pass owns phase/function/source context and counts the concrete
query and decision: AA records argument ModRef outcomes, SROA records capture
components and slice decisions, InstCombine records flag/attribute drops and
rewrites, and Calls records range/attribute intersections and compatibility
outcomes. This keeps recording-disabled accessors lean and assigns costs to the
actual caller instead of hiding them in a shared aggregate timer. Existing
value-handle notifications retain their context-owned counters.

`tests/LlvmIr/CoreAttributeProperties.cs` covers payload ownership, declaration
inheritance, capture strength, clone independence, return-range intersection,
annotation drops, attribute-set intersection, byval alignment, same-sign
matching, flag copying, fixed vscale and F32 denormal overrides. Root serializes
builds and integrates these with focused optimizer/user journeys; this document
does not claim a build or execution before that integration completes.
