# LLVM machine scalars and split Function values at the Native boundary

Status: numeric type/value/storage/codec/readiness foundation published,
2026-11-15; instruction and target integration is tracked independently below.
This document distinguishes implementation from execution receipts. It follows the existing
[Native model](native-model-v090.md) and
[LLVM target adapter](llvm-native-target-lowering.md), not an LLVM backend or
LLVM pass-manager adoption. The source language and its Number ABI do not change.

## Decision and current obstruction

Use exact, internal machine-scalar types for optimizer-produced integers and
floating formats. Flatten LLVM aggregate SSA values into their component slots;
pack a Function only at the existing Function ABI boundary. Describe managed
pointer external state using the pinned LLVM DataLayout model instead of changing
SROA to protect an accidental target representation.

These are target representation tasks, not new alias, escape, heap or lifetime
solvers. A Range remains an ordinary builtin object reference. Neither a Range
initializer nor an internal scalar operation invents deferred source evaluation,
a call to a stored Function, or a capture mode.

The current obstruction is concrete:

| Inspected implementation | Present contract / missing capability |
| --- | --- |
| `NativeValue.cs` | Number is IEEE binary64, not an integer bit carrier. |
| `NativeCode.cs`, `NativeType.cs` | Code types and storage types describe source kinds and addresses, not arbitrary `iN` or FP formats. |
| `NativeStorage.cs` | Allocation owns raw bytes, typed CLR roots, and initialization bits separately. |
| `NativeImage.cs` | Every code type currently becomes a source `TypeRef`; machine types must not take that path. |
| `NativeResult.cs` | Futures have source logical and physical types; machine results need exact physical types without fabricated source types. |
| `LlvmNativeLowerer.Aggregates.cs` | Function assembly tracks compile-time insertvalue fragments; standalone code/name extraction and arbitrary component PHIs are not represented. |
| `NativeArtifact.Compact.*.cs` | Native format 2 / compact revision 4 has fixed type/constant records and exactly twelve sections. New sections or appended record fields are not compatible. |

Reusing Number loses integer precision above 2^53. Wrapping every LLVM value in
an arbitrary object would discard the closed Native representation. Neither is
an acceptable shortcut.

## 1. Types, ownership and scalar storage

Keep `TypeKind` unchanged. Add an internal discriminated machine descriptor to
`NativeCodeType` and `NativeType`, with interned descriptors owned by the image:

```text
NativeMachineType = Integer(bitWidth, abiAlignment)
                  | Floating(format, abiAlignment)
                  | CodeReference
                  | EnvironmentReference
                  | NativeNameReference
```

The public source constructor remains unchanged. A machine-type factory creates
a code type with no source kind. Machine descriptors are not source `Never`
values: the typed alternative, not a sentinel source-kind interpretation,
distinguishes them. `NativeType` equality includes the descriptor; an `i32` and
an `i64` are different physical types. Unions of source kinds keep their existing
behavior. Internal types do not enter `TypeRef.Any`, source reflection, conversion,
layout-domain checks, or source builtin enumeration.

Append internal `NativeKind` alternatives; preserve every existing numeric ID.
Replace `Enum.GetValues<NativeKind>()` as the source-builtin list with an explicit
source-kind list. Otherwise extending a physical enum accidentally extends the
language.

### Proposed value API

```text
NativeValue.FromMachineInteger(LlvmAPInt bits)
NativeValue.AsMachineInteger() -> LlvmAPInt
NativeValue.FromMachineFloat(format, LlvmAPInt bits)
NativeValue.MachineType -> NativeType
```

`AsMachineInteger` returns exact width, not `long`, `double` or a source Number.
An explicit implementation may share the existing Bool carrier for `i1`;
its accessor then returns `APInt(1, 0|1)`. Other widths remain machine integers.
The operation declares signedness, just as LLVM `iN` does; the value does not
carry a separate signed/unsigned language type.

* Up to 64 integer/FP payload bits use the existing inline `ulong` bits. A typed
  root points to the cached immutable descriptor; no per-arithmetic object is
  allocated.
* Wider transient values retain a typed immutable wide-bits payload wrapping
  the existing `LlvmAPInt` storage, not arbitrary `object` or boxed source values.
  Add the typed root alternative to `NativeRoot`.
* A monomorphic stored integer uses `ceil(bitWidth/8)` raw bytes, zero roots,
  and the exact Native target byte order. Its width comes from its slot type,
  not a tag repeated in storage. Bits outside the width are masked once at the
  producing primitive. Non-byte-sized integer memory accesses follow LLVM
  store size, with padding bits not made into source semantics.
* Machine FP storage is the exact format bit pattern with zero roots. Number
  remains binary64 at every existing language/builtin ABI. A binary64 internal
  value may share its payload implementation, but that does not make an i64
  a Number.
* Alignment comes from the producer DataLayout/compiled layout. Do not infer a
  new integer alignment from CLR host object layout.

`LlvmAPInt` is already a readonly small-inline/wide-word implementation. Reuse
its source-port arithmetic rather than introducing BigInteger or another
integer engine. Runtime FP formats depend on the exact APFloat format work;
adding an enum entry alone does not implement rounding or conversions.

### Readiness is independent of source type

Make the datum/future result contract distinguish optional source logical type
from mandatory physical type. Preserve existing language constructors/defaults;
add a machine-result constructor or factory whose physical descriptor is explicit
and whose logical type is absent. Do not use fake Number or `Any` metadata.

`NativeImage` constructs physical types for all indices, source types only for
source alternatives. Existing language-only `SourceType` callers stay on that
branch. Machine slots bypass reflection/narrowing rather than adding repeated
runtime checks. `NativeDatum.PhysicalType` reads the descriptor from the ready
machine value, not `NativeType.Of(value.Kind)` alone.

Pure machine operations follow the existing ready / `Map` / `Combine` pattern.
Ready <=64-bit operands take the direct word path. Pending inputs produce a
physical-typed future; they do not force a call or await during compilation,
initialization or descriptor decomposition. Such continuations retain their
operand data and existing dependencies, not an entire frame. Resource execution
and its existing lifetime graph are unchanged.

## 2. Instruction boundary

Append opcodes; do not renumber 0–46. `FloatingIntrinsic = 47` is reserved by the
concurrent floating-intrinsic work. Allocate subsequent IDs centrally during
integration. Suggested families use the existing generic instruction record:

| Family | Operands and result |
| --- | --- |
| Integer operation | `Inputs` are typed integer SSA slots; operation enum in `Immediate`; destination slot fixes width. Includes arithmetic, shifts, bitwise and signed/unsigned divide/remainder. |
| Integer comparison | Same typed operands; predicate in `Immediate`; result existing Bool/i1. |
| Machine cast | One typed operand; source/destination slots fix widths/formats; source LLVM cast opcode selects trunc/zext/sext, FP cast, numeric conversion or bitcast. |
| Floating intrinsic | Exact FP inputs plus ordinary typed integer inputs where required; operation enum in `Immediate`. |
| Function component / composition | Component extraction from an existing Function datum, or pack code/env/name slots to existing Function datum. |
| Raw scalar load/store | Address plus compiled scalar type; direct byte view, not a repeated declared-field search. |
| Memory fill/copy | Address, typed integer byte count, fill byte or source address; memory semantics described below. |

Schema `Values` contains actual SSA operands only. A true LLVM `immarg`, such as
`is_fpclass`'s mask, may occupy encoded `A` while being excluded from `Values`;
ordinary constant and dynamic `powi`/`ldexp` exponents both use integer input
slots. Never hide an ordinary integer operand in a special constant-only route.

Port source primitives and use their operation identities. Machine operations
are not source `Binary` calls, and target lowering must not reject an upstream
optimization merely because its result needs an i64 slot. Source arithmetic
flags/poison behavior are handled by the existing LLVM graph contract; this
extension is not a runtime safety checker for every arithmetic instruction.

## 3. Split Function descriptor

The producer's descriptor becomes:

```text
{ ptr addrspace(0) code,
  ptr addrspace(1) environment,
  ptr addrspace(1) nativeName }
```

Its three SSA fields are not one raw C struct in the Native runtime. The existing
public Function representation stays a 32-bit function index and two typed roots
(environment and native name). Internal components are:

| Component | Native carrier / owner |
| --- | --- |
| Code | `NativeCodeToken`: unsigned token, null=0, source function index+1 otherwise. Image owns the stable function table; no host executable pointer or symbol registry. |
| Environment | Existing allocation/interior-address representation, null where absent. Address retains its storage and typed roots. |
| Native name | Nullable typed string root for the existing native export name, not a new source String conversion. |

Use flattened component slots for aggregate PHIs, select, insertvalue,
extractvalue and SSA copies. The existing PHI incoming-register construction
runs separately on each leaf. No tuple object is allocated per instruction, and
no environment is reconstructed merely to move an SSA field.

At an existing Function parameter/return/store/call boundary, pack the leaves.
Unpack an incoming Function using pure component projections. For a pending
Function, each projection maps the same whole datum; it must not reinterpret a
pending whole-Function memory cell as an already-written env/name subfield.

Add an internal `NativeEnvironment` storage-view constructor/factory taking the
already-owned capture region and layout. Packing an env address retains that
region rather than copying captured contents into a second allocation. Existing
source captures remain locations; subsequent mutation through an original
address is observed by the function. No by-value capture semantics are added.
Public environment constructors remain usable for their existing callers.

A native export with null code and a name keeps its existing dispatch. A source
function with nonnull code and environment keeps its existing invocation path.
Descriptor assembly is bit/reference movement, not a call, heap proof or alias
constraint. No no-LTO global code analysis is required.

## 4. Managed pointer DataLayout

Native addresses include CLR roots outside their raw offset bits. This is actual
external pointer state, not a source safety property. The pinned LLVM revision
`a55e64432a516fd6507d3ecfba7407cfe425f940` supports `e` and `u` pointer flags:
`DataLayout.cpp::parsePointerSpec`, and `DataLayout.h`'s pointer-state queries.
The `sroa_types` source-port work owns those exact parser/query implementations.

The published managed-data producer layout is:

```text
e-p:64:64:64:64-peu1:64:64:64:64-A1-G1
```

Code/program pointers remain AS0. Managed data, environments, native-name
references, ordinary builtin references, frame/heap addresses and global data
use AS1. Set these through central producer type/address factories and explicit
module defaults; do not scatter casts to repair mismatched pointers. A global's
storage address and its loaded reference value are separate pointer types.
Project `HeapAlloc` continues to be the existing explicit instruction, in AS1.

For `pe1`, pinned queries return external=true, unstable=false,
nonintegral=true, `mustNotIntroduceIntToPtr=true`, and
`mustNotIntroducePtrToInt=false`. `ni:1` is not equivalent: the pinned parser
sets unstable=true and external=false for that older spelling. The published
producer uses `peu1` to describe both actual external GC-root state and moving-GC
representation instability, disallowing introduction of both casts.

Pinned SROA `canConvertValue` already refuses pointer/integer reinterpretation
for nonintegral pointers. Use that upstream behavior. Do not add a custom
"Function is unsafe to split" gate, conservative alias domain, or an
independent heap proof.

LLVM requires pointer loads/stores and memcpy/memmove to carry external state;
integer loads cannot replace them. Its `e` flag deliberately differs from
unstable representation. [LLVM pointer reference](https://llvm.org/docs/LangRef.html#non-integral-pointer-type)

The target representation distinction remains explicit: existing NativeAddress raw
bits pack offsets, while allocation identity is a root. Reading those bits is
not a complete implementation of general `ptrtoint` or ordered pointer
comparison across allocations. `pe1` alone does not forbid introduced
`ptrtoint`; the actual producer now supplies both `e` and `u` rather than treating
those offsets as integral addresses. Never pretend packed offsets are globally unique addresses or
create an unrequested global heap solver. Existing pointer identity continues
to be allocation plus offset. No source pointer-to-integer API is added here.

## 5. Byte memory is not ClearMemory

Existing `ClearMemory`/`ClearRegion` erase initialization state. LLVM memset
writes initialized bytes. Mapping memset(0) to Clear is incorrect.

The first scalar-memory slice needs explicit trusted primitives:

```text
ReadMachineScalar(type, address) -> NativeValue
WriteMachineScalar(type, address, value)
FillBytes(address, byteValue, byteCount)
CopyBytes(source, destination, byteCount, overlapMode)
```

A compiled raw-numeric region owns bytes, not one immutable declared scalar view.
SROA may load an i64 bit pattern from storage originally written as double, or
combine adjacent small integer fields. The scalar type tells the primitive how
many bytes to access; NativeImage need not rediscover the original field.
`FillBytes` sets the affected initialization bits. A byte store initializes its
written bytes. Ordinary typed scalar reads retain the existing field readiness
contract; widened loads do not scan padding bytes or acquire a new runtime
initialization proof. LLVM undefined/padding bits are not a source admission
condition.
Memmove uses overlap-correct span copying. Pending scalar writes remain ordered
by the existing memory dependency path; a byte-copy primitive cannot bypass an
uncompleted producer stored in the same region.

For current compact Native layouts, LLVM byte offsets and physical Native
byte/root offsets are not interchangeable: a stored String is root-only, and
a Function occupies 4 raw bytes plus roots rather than LLVM's three pointer
fields. Known whole typed regions continue to copy their raw bytes, roots and
initialization state together using the existing layout. Numeric partial
regions use direct byte operations. Target lowering emits the known physical
mapping, not runtime recursive layout admission.

General byte operations on mixed managed-reference regions need a separate,
concrete memory-layout slice: compile an LLVM-byte extent with reference-slot
segments and map it to Native raw/root extents. A copy transfers the matching
reference segments as external state; zero fill that produces null references
also clears the relevant roots and marks the storage initialized. Store that
static mapping in compiled layout data, not a per-execution analysis. A byte
range cutting an external-state pointer is not made correct by copying only its
raw bytes. This remaining target capability is not solved by the integer carrier,
and must not be advertised as implemented by typed CopyMemory. No new source
allocator, free operation, universal object payload or safety solver follows.

## 6. Native format 2 / compact revision 4

Preserve the existing twelve-section order and exact old records. The compact
reader checks both the section count and complete record consumption. Therefore
neither append a thirteenth machine-type section nor silently append fields to
the old type records.

The existing fixed type record is sufficient for an additive tagged alternative:

```text
ushort Kind; int Element; optional int[] Layouts; int Pointee
```

Reserve explicit wire-only tags outside source `TypeKind.Any` and retired
Storage. Suggested allocation (integration owns final constants): `0x1000`
Integer, `0x1001` Floating, `0x1002` CodeReference, `0x1003`
EnvironmentReference, `0x1004` NativeNameReference. For these alternatives only,
`Element` carries integer bit width or explicit FP-format ID; the existing
optional integer-array field carries the compiled ABI alignment for numeric
alternatives (rather than a source Scope layout domain). `Pointee` retains its
empty default. Reference alternatives need no numeric parameters. Decode the tag
first to construct the
typed machine descriptor. Do not reinterpret existing Range Element, Scope
Layouts or address Pointee records, or invent negative-field encodings.

The in-memory optional descriptor need not be a new serialized field. Existing
source records round-trip byte-for-byte. Serialization and type admission
switch on the tagged alternative before applying existing source-kind rules.

Machine constants append internal NativeKind tags and can use the existing
optional Text field for a canonical width/format plus hexadecimal bit pattern,
with a factory that exposes typed bits in memory. Parse once when linking the
image. Do not pass their data through the existing double-valued Number field.
This avoids changing the fixed constant record. Instructions already encode
their optional operands and support appended opcode IDs without a new section.

The new executable reads existing format-2/revision-4 source-only artifacts.
An old executable cannot execute new machine types/opcodes and rejects their
unknown tags; that does not justify renumbering or changing the established
wire contract. There is no old-optimizer execution fallback or loader-side
migration. A future compiled mixed-memory mapping must likewise use an explicit
existing-record alternative, or receive a deliberate artifact-version decision;
it must not be smuggled in as trailing revision-4 bytes.

## 7. Coherent implementation order

1. **Type/value/storage foundation:** internal descriptors, explicit source-kind
   list, exact integer carrier/accessors, inline and wide storage, image physical
   types and absent source type, typed pending results, fixed-record encoding.
   Keep existing source Number/Function records untouched.
2. **Integer target surface:** operation/cast/comparison dispatch, constant
   materialization, byte scalar load/store, integer GEP indices, and exact
   endian bitcasts. Enable optimizer-produced iN results, not a new source kind.
3. **Managed pointer producer:** central AS1 factories and real DataLayout
   flags/queries, coordinated with alloca/global/heap/capture emission. This is
   representation information for upstream passes, not a pass-specific gate.
4. **Function leaves:** component slot mapping/PHIs/selects, pack/unpack and
   environment storage views. Exercise actual captured mutation and native
   export invocation through the existing ABI.
5. **FP/intrinsic integration:** exact non-binary64 formats and casts, then
   typed integer powi/ldexp operands. Binary64 noninteger intrinsic work can land
   earlier with its reserved opcode 47.
6. **Memory intrinsics:** initialized byte fill and numeric byte copies first;
   explicit compiled reference-segment mapping for general mixed regions.
   Do not claim full arbitrary LLVM memory lowering before this exists.

Each slice changes its owning models, serialization, runtime and target adapter
coherently. Do not make full SROA conditional on a new alias "proof" or hide
unsupported output behind a lower optimization level.

Use existing sink-neutral telemetry: record machine widths/formats, inline/wide
paths, target cast/op identities, ready/deferred outcomes, aggregate leaf counts,
pack/unpack counts, typed/raw memory bytes and reference copies, and concrete
unsupported target operation/type with function/source context. Disabled
recording must not allocate per instruction. These are causal implementation
counters, not a second validation pass.

Focused acceptance is end-to-end source -> O2 -> artifact round trip -> execution,
plus direct IR journeys for optimizer-only types: integer values above 2^53,
i128 carry/shift, signed casts, NaN/signed-zero bitcasts, endian subword memory,
initialized zero fill and overlapping memmove, split Function PHIs and captured
location mutation, and a pending Function/scalar whose projection does not
start an extra call. Keep existing accepted source/bound object journeys as the
compatibility control; do not substitute a blanket regression run or unit-test
count for these changed workflows.

## References and code anchors

* `src/Msync.Core/Types/TypeKind.cs`; `src/Msync.Core/NativeModel/NativeKind.cs`.
* `NativeCode.cs`, `NativeType.cs`, `NativeValue.cs`, `NativeRoot.cs`,
  `NativeStorage.cs`, `NativeDescriptors.cs` in `src/Msync.Core/NativeModel/`.
* `src/Msync.Runtime/NativeModel/NativeImage.cs`, `NativeResult.cs`,
  `FrameAllocation.cs`, `NativeFrame.cs`.
* `src/Msync.Hir/Lowering/LlvmNativeLowerer.Aggregates.cs` and LLVM HIR producer.
* `src/Msync.Core/Llvm/LlvmAPInt*.cs`, `LlvmAPFloat*.cs`.
* Pinned `vendor/llvm-reference/llvm/lib/Transforms/Scalar/SROA.cpp`,
  `lib/IR/DataLayout.cpp`, `include/llvm/IR/DataLayout.h`.

## Published numeric foundation

The numeric foundation is implemented in `NativeMachineType.cs`,
`NativeValue.Machine.cs`, `NativeCode.Machine.cs`, `NativeStorage.Machine.cs`,
`NativeResult.Machine.cs` and their owning model/codec integration. It adds no
source `TypeKind`, instruction opcode, Range behavior or capture mode.

- `NativeKind.MachineInteger = 11` and `MachineFloat = 12` append to the existing
  enum. The source universe uses an explicit existing-kind list; extending the
  physical enum cannot extend source `Any`.
- `NativeMachineType.Integer(width, alignment)` and `.Floating(format,
  alignment)` intern immutable numeric descriptors. `NativeType.Equals` includes
  the physical descriptor, while value admission uses class/width/FP semantics,
  not a slot-alignment eligibility rule. Layouts have zero reference slots for
  these numeric fields. Up to 64 payload bits are inline with a cached typed
  descriptor root; wider values use a typed immutable APInt payload temporarily
  and store only their raw bytes in a field.
- `NativeValue.FromMachineInteger/AsMachineInteger` and
  `FromMachineFloat/AsMachineFloat` preserve exact bits. Existing Bool can project
  to i1, and existing Number to binary64 APFloat. An integer never passes through
  Number. Machine values have `OptionalSourceType = null`, while `PhysicalType`
  carries their exact representation.
- `NativeCodeType.ForMachine` uses an optional typed alternative in memory.
  The existing four-field compact type record writes explicit tags `0x1000`
  (integer) or `0x1001` (floating), width/format in `Element`, and one ABI alignment
  in the existing optional integer array. Source alternatives retain exactly
  their previous field encoding. Machine constants use the existing Text field
  with canonical `width-or-format:alignment:hex-bits`; source constant records
  are unchanged. No section or trailing field is added to format 2/revision 4.
- `NativeImage` links numeric constants once and constructs physical types for
  every code type, with optional source projections only for source alternatives.
  Source reflection does not manufacture a builtin signature for a machine-only
  callable. `NativeDatum`/`NativeFuture` distinguish this absent logical type from
  their mandatory physical descriptor. The old source constructors keep their
  defaults. Generic `MapMachine` and `CombineMachine` run ready operands directly
  and retain the actual dependencies for pending operands. Source union results,
  such as nullable native-name projections, use cached actual union TypeRefs.
- Existing Select carries optional source metadata. Pending machine storage and
  typed copies forward the same future; they do not eagerly invoke a Function,
  demand an unselected operand, or publish raw bytes before completion.
- Ready numeric byte views support explicit little/big endian byte movement.
  Complete typed stores mark their actual written byte extent. Ordinary typed
  reads retain the existing readiness check, and compiled reads retain their
  existing trusted path; no per-byte initialization scan was introduced.

Artifact telemetry records integer/floating/wide type counts and total represented
bits through the existing trace owner. Pure descriptor/value helpers add no global
sink, per-query stopwatch, analysis cache, or recording-disabled allocation.
Instruction-width, operation and ready/deferred counters belong to the corresponding
operation-family runtime/target owner.

### Focused executable properties and remaining seam

`tests/LlvmIr/NativeMachineScalarProperties.cs` covers i1 through i257 raw storage,
zero-root layouts, alignment-independent value movement, integer values beyond
binary64 precision, signed-zero/NaN raw payloads in all four IEEE formats, endian
byte views and source-universe separation. Its artifact journey runs real Native
identity, Select and constant functions both directly and after Native2 decoding.
Passing an accepted source-only artifact also compares complete original bytes
with decode/re-encode output, rather than only checking a round-trip object model.
`tests/NativeTiering/MachineScalarReadinessChecks.cs` covers actual pending maps,
combines, physical-typed memory copies/materialization, unchosen Select operands,
real source unions and preserved source-future defaults. The coordinator owns
runner integration and execution receipts; file publication alone is not a pass.

This foundation does **not** implement a pending raw bitcast adapter for arbitrary
source memory. A pending memory location is keyed by its typed offset; viewing a
pending Number as i64 requires the future's actual bitcast projection, not merely
ready byte reinterpretation. Mixed-reference byte copies and byte operations
crossing pending producer extents remain the separately scoped memory target
work. Existing generated scalar-tier initialization emission also retains its
own protocol; a partial raw projection of such storage needs the compiled view
contract, not an additional runtime proof. No heap or alias solver follows from
these representation seams.

The published ready storage path uses platform `BinaryPrimitives` endian
loads/stores for one-, two-, four- and eight-byte payloads. Other one-word byte
extents use a short byte loop; multiword extents use the actual APInt word/tail
representation. Narrow non-byte-aligned loads mask once when constructing the
APInt. The common <=64-bit scalar path does not allocate a temporary array.


### Coherent managed-data producer and SROA storage-view publication

The managed producer is now published from the coherent patch under
`.temp/llvm-managed-layout/` using
`e-p:64:64:64:64-peu1:64:64:64:64-A1-G1`. Both external GC-root state and
unstable representation are explicit. Code remains AS0; builtin references,
locations, environments, names, union/Path root fields and frame/heap/global data
are AS1 through central factories and actual module defaults. It changes neither
TypeKind nor Native2 wire ABI. The publication applied minimal shared-file hunks,
not stale whole-file overlays, and leaves the default SROA pipeline unchanged.

The Function SSA and layout owners agreed on
`LlvmStorageView(LlvmType StorageType, int LogicalType, ulong ByteOffset)` plus
nullable `LlvmAllocaInstruction.StorageView`. Fresh source SROA partitions carry
the original allocated layout/logical ABI index and compose the new partition's
byte offset with any previous view. Cloning preserves the immutable metadata.
This is target representation information, not an alias identity, equality or
partition eligibility constraint. Native lowering can recognize a Function's
code/environment/name field in the original LLVM layout without pretending its
LLVM byte offset is the compact Native byte offset.

The exact source rationale, patch hashes and unexecuted public source/partition
properties are recorded beside the patches. `ManagedLayoutProperties.cs` is
published in `tests/LlvmIr/`; root owns runner integration and end-to-end execution
with Native Function leaf-slot support. No Range or capture
semantics, new heap proof, address-space-disjoint assumption or optimizer decision
is introduced by the publication. This owner did not run a build or claim that
default O2 SROA target execution is already complete.

### Ordinary compiled locations versus public raw views

The initialization policy now follows the entrypoint's represented contract,
not every byte that a generic source carrier happens to occupy:

| Entry point | Readiness maintenance |
| --- | --- |
| Public NativeStorage/NativeAddress stores, copies and clears | Existing full-extent behavior, including Number-write then interior OffsetBy machine reads |
| Internal compiled ordinary source storage | One readiness flag at the declared location start |
| Machine storage, including compiled writes/copies/clears | Actual machine byte extent |

The internal compiled entrypoints share payload/root writes and copy routines
with public operations. They do not scan consumers, reconstruct a heap model or
add runtime admission checks. Number/Function/Address/Path payload bytes, union
tags and CLR roots are unchanged; initialization arrays are unchanged in size.
Explicit public raw writes still mark their observable interior views. Pending
source stores clear the location's start flag and retain the same future;
completion materializes through the corresponding compiled entrypoint.

`NativeMemory` uses the compiled entrypoints, as do exact-address writes,
scalar returns and the existing ready Number handlers. Public environment/range
construction APIs retain their raw behavior. Frame reset and bulk region bitmap
copy are not replaced by per-field readiness guesses. This does not introduce
the still-separate pending raw-bitcast adapter described above.

With profiling enabled, `vm.storage.initialization` reports actual writes,
copies, clears and logical readiness flags written/avoided versus the former
full-extent path, grouped by carrier kind and policy. Its attribution is
explicitly RuntimeProfile-wide, since runtime memory can serve multiple session
generations. Existing function/site telemetry remains intact. No storage counter
object, per-write allocation or dictionary lookup is created when profiling is
disabled. Flag counts are work counts, not elapsed-time attribution.

`tests/NativeTiering/OrdinaryInitializationChecks.cs` covers public raw-interior
compatibility, ordinary carriers/unions, exact addresses, machine extents,
pending values and exact recording. Its real source journey runs capture
mutation, Function calls and Scope stores through public Compiler O0/O2 and
detached Native2, with result 24. Root's serialized run passed 57 ordinary
initialization properties and four source/artifact journeys; see
`artifacts/diagnostics/llvm-instcombine/ordinary-init.log`. This establishes the
focused behavior, not a measured application speedup. Paired performance
comparison remains separate from that execution result.
