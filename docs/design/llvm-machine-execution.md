# Native execution of LLVM machine scalars

The execution tranche is integrated with the Native machine-type/value/storage
cohort. Source handoff files remain in `.temp/llvm-machine-execution/`; the
production files are authoritative. Build and execution acceptance belong to
the coordinator and are not claimed by this integration note.
Source language Number, builtin enumeration, Range construction and closure
capture semantics do not change.

## Instruction selection and operation identities

The target uses existing Native instruction fields and appended wire identities:

| Opcode | Wire ID | Operands | Operation identity |
| --- | ---: | --- | --- |
| MachineBinary | 48 | A, B; Result fixes physical type | Actual `LlvmOpcode` in Immediate |
| MachineCompare | 49 | A, B; existing Bool Result | Actual `LlvmCmpPredicate` in Immediate |
| MachineCast | 50 | A, Type; Result agrees with Type | Actual `LlvmOpcode` in Immediate |
| MachineIntrinsic | 51 | Inputs; Result fixes physical type | Stable `NativeMachineIntrinsic` in Immediate |
| MachineUnary | 54 | A; Result fixes physical type | Actual FNeg opcode in Immediate |

Existing Native opcodes 0–46 do not move. IDs 47, 52 and 53 belong to independently
coordinated floating-intrinsic and byte-memory tranches, not placeholder
implementations. No new LLVM opcode is introduced; HeapAlloc, FileIn and FileOut
remain the project's three LLVM extensions. The intrinsic enum contains semantic
operation names, never generated TableGen numeric IDs or function-name matching.

Integer arithmetic calls the ported APInt primitives at its exact width. Signed
and unsigned divide, remainder, comparison and extension are chosen by the
instruction, not a value's language type. Min/max, population/zero counts, byte
swap, bit reverse, funnels, saturated arithmetic and high products use the same
source primitives. Overflow aggregate results can be selected as separate
arithmetic-result and Bool-flag leaves, with no tuple allocation or optimizer
transformation in the VM.

FP arithmetic and numeric conversions use the ported APFloat implementation for
half, BFloat, single and double. LLVM FRem maps to APFloat Mod (not IEEE remainder).
FNeg changes the sign bit directly; subtracting from negative zero would not
preserve signaling-NaN payload behavior. FCmp implements all ordered/unordered
predicates, including NaNs and signed zeros. Existing source Number values may
enter or leave the binary64 arithmetic path without reinterpreting an i64 as a
Number. Integer/FP bitcasts move exact bits. This is target execution, not another
optimization algorithm or source-language math validation layer.

## Readiness and ownership

Ready operands execute the primitive immediately. `MapMachine` and
`CombineMachine` carry a small operation/type state with a static delegate;
there is no closure allocation for ready arithmetic. Pending operands preserve
their exact physical destination type and optional source type. Their futures
retain the operand futures, not the frame. Ternary funnel operations have a
corresponding minimal three-operand continuation, created only for pending
inputs. No operation waits during descriptor construction or compilation.

Machine descriptors belong to the shared machine-value cohort; the evaluator
uses `AsMachineInteger`, `AsMachineFloat`, and explicit result descriptors. Bool
remains a valid i1 physical representation, and Number remains the public
binary64 representation. The backend linker must retain an image's exact
machine type descriptor rather than reconstructing a widthless type from a
NativeKind mask. Generic Native Load/Store can use the shared typed scalar
storage; byte reinterpretation and mixed-root copies remain separate work.

## Observability and integration

The existing execution handler linker invokes the five concrete machine
handlers. Preparation evaluates their actual primitives when operands are
known and records computed/unknown decisions; it does not substitute a second
arithmetic implementation. Source/function/site ownership remains in the
existing trace and runtime profile. Runtime counters distinguish operation
family, ready/pending path, wide operations and retained dependencies. Detailed
site records include the concrete opcode/predicate/intrinsic identity. Disabled
recording returns before counter work, and primitives have no per-instruction
stopwatch or global analysis cache.

Artifact validation describes represented instruction arity/classes/widths and
serialized operation identities. It does not add operand-value checks, a heap
proof, an alias-identity eligibility condition, or a global solver. The CLI
renderer exposes operation identities directly. All changes preserve existing
record/section shapes; machine scalar type/constant encoding is owned by the
shared Native2 representation cohort.

`MachineExecutionChecks.RunAsync` exercises actual Native programs and their
serialized Native2 forms: i64 values above 2^53, 257-bit values, arithmetic,
signedness, shifts, casts, half/BFloat/single rounding, signaling-NaN FNeg,
floating predicates, integer intrinsics and overflow leaves. The separate
`MachinePendingChecks` belongs to the NativeTiering friend test assembly and
checks exact physical types, dependency retention and readiness composition.
The coordinator owns the build and real source-to-Native acceptance.

Pointer casts and ptrmask are not represented as arbitrary scalar offset math.
The current NativeAddress includes typed external roots and separate byte/root
offsets; dropping that state or masking only one component would not implement
LLVM pointer semantics. Those operations await the actual pointer/byte-memory
boundary work, and this scalar tranche does not advertise them as supported.
