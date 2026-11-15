# Constant expressions and target-dependent constant folding

The source pin is LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`.
Constant expressions are immutable, context-uniqued IR constants with actual operand
`Use` edges. They are not delayed language evaluation, runtime calls, closures, or
new Range behavior. A Range remains an ordinary built-in object.

## Source map

| Source | Port | Responsibility |
| --- | --- | --- |
| `llvm/include/llvm/IR/Constants.h`, `llvm/lib/IR/Constants.cpp` | Core `LlvmConstantExpr.cs` | Context uniquing, opcode/flags/source-type/range/mask identity, factories, `getWithOperands`, `getAsInstruction`, operand-change canonicalization |
| `llvm/lib/IR/ConstantFold.cpp` | Core `LlvmConstantExpr.Folding.cs`, `.Vector.cs`, `.Identity.cs` | Context-independent scalar and per-lane folds; supported casts and Add/Sub/Xor; extract/insert/shuffle folding |
| `llvm/lib/IR/Constants.cpp:2793` | Core `LlvmConstantExpr.Gep.cs` | Layout-dependent GEP factory conversion to byte `ptradd`, index sign conversion, sequential strides and struct offsets |
| `llvm/include/llvm/IR/Operator.h` | Core `LlvmGepOperator.cs` | One non-owning view over instruction and constant-expression GEPs |
| `llvm/lib/Analysis/ConstantFolding.cpp` | Optimization `LlvmConstantFolding.Expressions.cs`, `.Expressions.Compare.cs`, `.Expressions.Vector.cs` | Memoized expression/vector folding; symbolic GEP flattening, range/nowrap propagation, integral addresses, casts, global differences, pointer comparisons |
| `llvm/lib/IR/Operator.cpp`, `llvm/lib/IR/Value.cpp` | Optimization `LlvmValueTracking.PointerOffsets.cs` | Shared GEP offset accumulation and pointer stripping for instructions and constant expressions |
| `llvm/lib/Analysis/ConstantFolding.cpp` load folders | Optimization `LlvmConstantFolding.Load.cs` | Constant global loads through GEP/cast expressions and byte offsets |

The pin permits persistent binary expressions only for Add/Sub/Xor and persistent
casts only for Trunc/PtrToAddr/PtrToInt/IntToPtr/BitCast/AddrSpaceCast. This binary
and cast restriction does **not** exclude ExtractElement/InsertElement/ShuffleVector:
the pin has separate factories that intern those expressions when folding fails.
Shuffle masks participate in identity but are not artificial operand `Use` edges.

## Ownership and canonicalization

The context owns the expression table. Type and operand identities are references;
flags, source element type, inrange bounds and shuffle mask complete the key.
Creating a constant cannot stamp a language ABI logical-type hint on its shared
identity. Layout information belongs to explicit layout-taking operations.

Operand RAUW follows the existing aggregate-constant machinery: first attempt a
reduction or an already-interned replacement; redirect users and notify value
handles when the old constant disappears. Otherwise remove the old key, change
all matching operand Uses, and reinsert the same constant under its new key.
ConstantData integer payloads do not acquire imaginary incoming use lists.

`GetAsInstruction` is explicit materialization, not a folding implementation trick:
folding never creates unattached temporary instructions or transient use edges.
Native lowering must materialize a non-literal expression or emit its real global
relocation rather than treating it as a runtime thunk or silently dropping it.

## Folding and costs

`ConstantFoldConstant` recursively visits only ConstantExpr and ConstantVector,
matching the source; arrays and structs are already canonical and do not trigger
an arbitrary recursive whole-heap walk. A per-query reference-keyed memo table
reuses shared child results. Instruction and PHI folding share this table across
all their constant operands. Literal-only calls do not allocate a memo dictionary;
the managed memo is created only when an expression/vector is actually visited.

GEP folding uses the shared layout offset algorithm, canonicalizes to byte
pointer addition, flattens nested constant GEPs, combines nowrap flags, adjusts
inrange bounds, and folds null/literal integral bases when the actual DataLayout
permits introducing IntToPtr. External-state/unstable pointer restrictions come
from the DataLayout owner, not a hardcoded integral-pointer assumption.

Project telemetry is optional and sink-neutral. `LlvmContext.ConstantExpressionMetrics`
records unique-table queries/hits/creations, local factory folds, operand changes,
canonical replacement and rekey outcomes. Optimization metrics record recursive
visits/cache hits/rewrites, offset work, nested GEP folds, nowrap changes,
inbounds inference, numeric addresses, symbolic cast elimination, global pointer
differences, pointer comparisons and constant-global loads. The disabled path
only checks a nullable dictionary; no strings containing operand contents are
constructed for counters.

## Integration dependencies and focused journeys

The vector instruction/carrier cohort supplies ConstantVector, ConstantDataVector,
GetSplatValue and vector instruction construction. The numeric-format cohort
supplies the real APFloat format semantics used by bitcasts. The DataLayout
cohort supplies pointer/index widths and non-integral-pointer queries. These are
real source dependencies, not alternate implementations or fallback pipelines.

Focused acceptance journeys are canonical expression identity and RAUW collision
(including value-handle deletion), structured global GEP to byte-offset folding,
loads through global field pointers, equal-base pointer subtraction/comparison,
layout-sensitive cast pairs, and fixed/scalable vector element operations. Build
and end-to-end execution are serialized by the root integrator; this worker does
not run MSBuild or claim an independent integration build.
