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

## Binary64 FCmp instruction selection

The first actual polygon bound comparison after default InstCombine integration
exposed a target coverage gap: essentially unchanged total VM work
(6,486,814 versus 6,486,823 instructions), but 196,608 newly normalized
`MachineCompare` executions and 196,577 fewer warmed direct/raw tier operations.
The scalar target's six source comparison mappings did not cover all legal LLVM
floating predicates. Its remaining exact `MachineCompare` representation was
correct, but execution routed ordinary source doubles through APFloat and the
tier treated those opcodes as generic bridges. A normalized predicate must not
lose the native binary64 path merely because its spelling is unordered.

The repair keeps the existing `MachineCompare` opcode and exact LLVM predicate
immediate. The LLVM optimizer and scalar lowering are unchanged. For statically
Number operands, tier instruction selection now maps all 16 floating predicates
to direct native comparison operations. Existing ordered source comparisons
keep their prior operation identities; the other direct operations are local
tier classifications, not new artifact opcodes or language operators.

- x64 uses `UCOMISD` and its ZF/PF/CF conditions. Ordered equality/less/less-equal
  mask unordered with NP; unordered not-equal/greater/greater-equal include P.
  Ordered not-equal uses NZ, unordered equality Z, and ORD/UNO use NP/P.
- AArch64 uses `FCMP` and `CSET`. Ordered not-equal combines NE with VC;
  unordered equality combines EQ with VS. The other predicates use the
  corresponding single condition. Both platform mappings preserve the unordered
  outcome; there is no non-NaN inference or replacement by an ordered predicate.
- Generic ready or resolved-pending Native Number operands use ordinary C#
  binary64 comparisons for the same predicate. Narrow and descriptor-backed
  machine floats continue through APFloat. Pending operations retain the
  original two-operand dependency handling rather than demanding an operand
  during construction.

The underlying four-way truth table is the LLVM predicate bit mask: equal=1,
greater=2, less=4, unordered=8. This also gives the focused fixture an independent
oracle from APFloat's four-way result, instead of checking the new C# predicate
implementation against itself. The hardware flag meaning is specified by the
[Intel instruction manual](https://cdrdv2-public.intel.com/671200/325462-sdm-vol-1-2abcd-3abcd.pdf);
[LLVM's AArch64 lowering](https://llvm.org/doxygen/AArch64InstructionSelector_8cpp.html)
likewise distinguishes ordered and unordered conditions rather than adding an
optimizer precondition; the [Arm instruction reference](https://documentation-service.arm.com/static/6245c734b059dc5ff9a8bdab)
specifies the hardware unordered outcome.

`tests/NativeTiering/FloatingPredicateChecks.cs` is selected by
`--floating-predicates`. It serializes real Native2 functions for all 16
predicates, compares a cross product of finite numbers, both zeros, infinities,
quiet NaN and signaling-NaN bits under Off/Linked/Force, and exercises real
pending process-exit numeric operands plus the retained-future path. Root owns
its build/execution receipt and polygon remeasurement. The x64 host execution
is not evidence that AArch64 machine code was executed on an ARM host.

Existing instruction telemetry retains each concrete LLVM predicate and source
span. `jit_double_compare_sites` records actual selected direct comparison sites
when detailed code-generation recording is enabled, while existing raw/direct
operation and generic machine-operation counters distinguish the execution
outcomes. The additional site enumeration is absent when recording is disabled.
No wall-time benefit is claimed by this source publication before the focused
journey and matched polygon comparison run.

### FCmp tier-layout integration correction

The first focused predicate execution exposed an integration error in this
fast-path patch: it crashed in generated x64 code before producing a test result.
Root's actual gdb receipt is
`artifacts/diagnostics/llvm-instcombine/floating-predicate-gdb.log`. The failing
instructions loaded the second scalar descriptor at offset `-0x20`, then
attempted `movsd` through the resulting invalid address. This is scalar index
`-1`, not an IEEE predicate condition-code failure.

`NativeTierLayout.Create` had collected the second operand only when the Native
opcode was `Binary`. A newly direct `MachineCompare` also has two scalar operands,
but its B slot was omitted from the layout. Instruction selection was extended
without completing this existing arity-dependent layout seam. The correction
uses `NativeFastOperation.TakesSecondOperand()` for collection, so all selected
two-input operations follow the same rule rather than adding a MachineCompare
exception or a runtime guard. The focused predicate journey now checks the A/B
and Bool-result mappings before entering native code; its IEEE matrix and pending
journeys remain intact. This omission affects ordinary direct MachineCompare
operands too and must not be described as inherently a pending-value defect.
Root owns the corrected build/run receipt; no passing claim is made solely from
this source correction.

Root rebuilt the coherent NativeTiering target after the arity fix and ran
`--floating-predicates`: **3,201 binary64 predicate Native2/pending properties
passed** on linux-x64. Receipt:
`artifacts/diagnostics/llvm-instcombine/floating-predicates-arity.log`. This
establishes the emitted predicate/operand-layout behavior; the matched final
AOT application timings are reported separately in the performance follow-up.
