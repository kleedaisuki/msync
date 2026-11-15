# Generic LLVM target cost model

The managed target-information implementation uses the generic base and CRTP
algorithms at LLVM commit `a55e64432a516fd6507d3ecfba7407cfe425f940`:
[`TargetTransformInfoImpl.h`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Analysis/TargetTransformInfoImpl.h).
It does not link LLVM, adopt its backend, or estimate native machine costs by
guessing from the host. These are the upstream generic target defaults.

## Represented scalar instruction dispatch

`LlvmTargetTransformInfo.Scalar.cs` translates the source instruction dispatcher
and generic arithmetic, cast, control, aggregate, memory, and represented
intrinsic costs. Integer/FP division and remainder cost four; other arithmetic
costs one except the source latency-only FP cost of three. PHIs are free except
for reciprocal-throughput queries. Static entry allocas and freeze are free.
The original aggregate insertion/extraction cost-kind distinction is retained.
Loads retain the source code-size-only truncation-user type query. Logical
selects use their arithmetic cost family. The existing GEP/address-mode model
remains unchanged.

Direct and indirect ordinary calls use the source parameter-count-plus-one
model. `IsLoweredToCall` retains the exact upstream generic symbol-name cost
heuristic; it does not infer library semantics, purity, memory effects, or
attributes for msync functions. TLI remains separately configured for the msync
runtime ABI. The generic conditional-memory and branch-divergence answers are
false, lookup-table defaults are enabled, and type legality defaults to false,
as in the upstream base implementation. These are actual defaults, not missing
analysis success/failure stubs.

`LlvmDataLayout` now parses source `n<size>[:<size>]` specifications and supplies
`IsLegalInteger` / `FitsInLegalInteger`. The default legal-width list is empty,
not the host's integer widths. At this source pin the upstream list stores
`unsigned char`; its documented truncation is preserved. Equality includes the
list, including ordering and duplicates, as in source DataLayout equality.

The represented arithmetic intrinsic registry takes the generic intrinsic cost
default; the subsequently represented `llvm.assume` takes its source zero-cost
branch. Other annotation/lifetime/coroutine/widenable-condition families
and vector instruction carriers are not represented by this scalar tranche;
no invented numeric intrinsic IDs or vector-target cost claims are introduced.
New intrinsic representations must extend the source dispatch where relevant.

The optional metric map records actual cost-family queries and selected
expensive/FP-latency/default branches under the owning pass span. Disabled
recording allocates no metric map. Cost queries do not create an execution-time
admission check or change object representation.

## InstructionCost fixed-point arithmetic

`LlvmInstructionCost` translates
[`Support/InstructionCost.h`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Support/InstructionCost.h)
from the same pin. A signed 64-bit payload stores quarters of a basic cost.
`Value` retains the source truncation toward zero; comparisons and `IsFree`
operate on the complete fractional payload, not the truncated display value.
`Free`, `Basic`, and `Expensive` retain the TTI constants zero, one, and four.

The separate valid/invalid state is preserved through arithmetic. Ordering is
lexicographic by state and then raw value: even the highest valid cost sorts
below every invalid cost, and invalid costs still retain numeric distinctions.
Construction, addition, subtraction, and multiplication saturate signed overflow.
Multiplication saturates the raw product **before** dividing by four, as the
source does. Cost-by-cost division first saturates the rescaling multiplication;
scalar division instead divides the existing raw payload directly. These are
intentionally distinct overloads. Signed multiplication uses `Math.BigMul`'s
high word to detect overflow without allocating arbitrary-precision integers.

`Map` passes the raw scaled payload to its callback, exactly as upstream, and
returns invalid-zero without invoking the callback for any invalid input. It is
not a mapping over `Value`, nor does it retain an invalid input's old payload.
Explicit state setters preserve the payload. Primitive arithmetic has no
per-operation telemetry allocation; its owning TTI query records the decision.
`tests/LlvmIr/InstructionCostProperties.cs` exercises signed saturation boundaries,
quarter-unit distinctions, state propagation/ordering, overload-specific division,
and source `Map` behavior against an independent `Int128` test oracle.

## Quantized branch probabilities

`LlvmBranchProbability` translates
[`Support/BranchProbability.h`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Support/BranchProbability.h)
and
[`Support/BranchProbability.cpp`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Support/BranchProbability.cpp).
All values share the source denominator `1u << 31`. Rational construction rounds
using integer arithmetic; thus `(99, 100)` becomes numerator `2126008812`, not an
unquantized fraction or a floating-point threshold. The source unknown numerator
is `UINT32_MAX`. The CLR storage is XOR-encoded so even zero-initialized struct
storage has the upstream unknown default, without adding a second state field.

Addition and subtraction retain source saturation, multiplication and probability
division retain source rounding, and scalar division retains truncation.
Normalization distributes residual mass to unknown edges before normalizing
known overfull probabilities; all-zero inputs use equal probabilities. Power
uses the source repeated multiplication, preserving each intermediate rounding.
The optional floating constructor is the explicit upstream conversion API, not
an implementation path for cost-model branch decisions.

`Scale` and `ScaleByInverse` retain the source 32-bit split product, middle-word
carry, staged division, and overflow saturation. They compute the full-precision
floor across the entire unsigned 64-bit input domain. Neither uses a floating
approximation or a bounded-integer shortcut. The 64-bit rational constructor
retains upstream's shared right-shift reduction before constructing its 32-bit
fraction. `tests/LlvmIr/BranchProbabilityProperties.cs` covers quantization,
complements, per-operation rounding, unknown/default normalization, and exact
64-bit scaling with an independent `UInt128` test oracle.
