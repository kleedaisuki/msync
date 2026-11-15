# InstCombine floating intrinsic call family

The implementation follows LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`, chiefly
`lib/Transforms/InstCombine/InstCombineCalls.cpp`. It extends the shared actual
InstCombine Work driver, builder and mutation/worklist protocol. It is neither
an alternative optimizer nor an LLVM backend integration.

## Source map and represented cases

| Product partial | Pinned source |
| --- | --- |
| Calls.Floating | powi 2653–2696; rounding/fabs narrowing 3449–3459; frexp 4674–4691; dispatch to the source family cases |
| Calls.Floating.MinMax | Six floating min/max cases 3063–3232 |
| Calls.Floating.Arithmetic | fmuladd/FMA 3280–3332; copysign 3333–3398; fabs 3399–3448 |
| Calls.Floating.Scale | sign/denormal helper 995–1000; ldexp saturation bound 1245–1264; sincos pairing 1920–1976; trig/ldexp 3460–3612 |
| Calls.Floating.Classify | foldIntrinsicIsFPClass 1062–1202 |
| InstructionSimplify.FMA | simplifyFMAFMul 6249–6300 in InstructionSimplify.cpp, default FP environment |
| InstructionSimplify.FPCommon | propagateNaN 6030–6072; scalar/fixed/scalable constant matching from PatternMatch.h |

The represented cases preserve the source transform order. FP min/max includes
negation inversion, nested constant merging, extension narrowing, integer-cast
min/max and opposite-argument absolute value. FMF are intersected for nested
calls and preserved where source creates FMF operations; unsigned cast nneg
survives only when both original casts provide it.

FMA uses the actual no-intermediate-rounding FMul simplifier, not ordinary FMul
constant folding. Negative-zero addends permit multiplication without nsz;
positive zero requires nsz. Fmuladd first takes its separate ordinary FMul
simplification path. Copysign propagates known sign, peels nested sign/magnitude
operations, handles floor(fabs(x)) and demands only the actual sign bit of an
elementwise integer bitcast. Fabs distribution through select clears select nsz
as upstream requires.

Trig parity folds retain the source denormal-to-positive-zero restriction for
odd functions. Sin/cos pairing scans the argument's actual users and creates
one aggregate sincos immediately after its definition (or at entry for an
argument); it replaces and erases the matching call through the shared worklist.
No global pairing map or invented profitability model is introduced.

Ldexp retains the source finite power-of-two constant rewrite, nested saturated
exponent addition, one-bit sign/zero-extension and zero-arm select cases.
Saturation adequacy comes from the actual APFloat extremes and exponent width;
sign queries are skipped when both calls already permit reassociation.
The source accepts independently overloaded exponent types; this family does
not impose a new lane-count contract or invent scalar vector-broadcast rules.

Classification preserves strictfp behavior, exact mask negation/inverse-fabs,
NaN/infinity/zero comparison forms, denormal-aware compare predicates and known
class pruning. Frexp recognizes its already-normalized fraction and constructs
the actual aggregate `{fraction, 0}` through insertvalue.

## Shared API corrections and boundaries

Generic builder result inference now uses the pure Core floating-intrinsic
return-type query for aggregate-returning SinCos/Frexp as well as is.fpclass.
It does not create an unused declaration before a successful fold. Aggregate
registry/folding and FPClass predicate helpers are shared work owned separately
from these call visitors.

The shared FP operand folder now quiets NaNs through the operand's actual
APFloat semantics. It does not reinterpret half/single payloads with a binary64
quiet bit. Fixed-vector propagation preserves poison lanes and quiets each NaN
payload; scalable splats keep their shape. The one matcher uses the actual
operand semantics instead of comparing every format to a binary64 constant.
Only the represented default FP environment is exposed by the new FMA helper;
no constrained-FP representation or exception policy is fabricated.

Source identities without a dedicated case in the pinned switch continue to
the shared intrinsic postlude; that is the source control flow, not a missing
family stub. Target-specific matrix/AMDGPU intrinsics have no descriptor in this
runtime's represented registry and are not approximated as generic math calls.
Exactly HeapAlloc/FileIn/FileOut remain the project extension opcodes. Range and
capture semantics are not changed by this family.

## Telemetry and focused acceptance

The existing pass/function trace owns the sink-neutral dictionary. Counters
record actual floating visits, unchanged/rewritten results and concrete powi,
min/max, FMA, sign, trig-pair, ldexp, classification and frexp outcomes. The
source analyses retain their own decision/work counters. Recording-disabled
paths create no dictionaries or reason strings.

`InstCombineFloatingCallProperties.Run()` builds real IR functions and runs the
actual pass/worklist. It distinguishes FMA's two signed-zero cases, powi square
and reciprocal, magnitude and rounding transforms, exact ldexp scale, sincos
aggregate sharing, frexp normalization and strict/non-strict classification.
The root owns Program wiring, serialized builds and execution receipts. The
presence of the fixture is not a claim that it has executed or that Native
aggregate math target support is complete.
