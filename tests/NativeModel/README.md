# Complete native value and logical storage contracts

This is the v0.9 representation foundation, not a new guest allocator or a
claim that the CLI has already migrated. The implementation is under
`src/Msync.Core/NativeModel`; Core's old `Value` paths remain until root-owned
cutover removes them.

`NativeValue` is a closed value-type sum of the actual builtins and an IR-only
address alternative. String, Path and Connection do not inherit from a generic
object. Addresses identify logical allocations and carry computed raw/reference
offsets. A static layout, not the input payload, determines field storage.
Address values also preserve an optional immutable physical NativeType. This
second typed root survives memory, region, environment and Range copies, while
untyped namespace bases remain untyped and address identity ignores this metadata.
It replaces the VM's per-register pointer-value cache, which could return stale
addresses after an indirect Store or BindLocation.
Monomorphic fields have no value tag; true sums have exactly their declared
alternatives. Reference-only union fields overlap GC references, never primitive
bits or untracked pointers.

`NativeStorage.Load/Store` and `Read*/Write*` admit checked host/test accesses.
Verified execution uses `LoadTrusted/StoreTrusted/ClearTrusted` or direct typed
`LoadNumber/LoadPath/...` entrypoints with precomputed offsets. These perform no
reverse field lookup or per-load type admission. Address arithmetic is integer
coordinate arithmetic, not a dictionary/member protocol. Initialization state
is separate from language Null; even zero-payload fields have distinct logical
locations. The compiler owns whether moves, clearing or physical elimination
are legal under remaining aliases.

Builtin Range snapshots store repeated native layouts rather than retaining an
array of universal value slots. A snapshot freezes membership, not referenced
allocations. Root inspection uses explicit retained values, not producer
iteration. Function environments use a supplied static `NativeEnvironmentLayout` with
native value fields or typed address fields, never layout inference from the
capture payload. Uninitialized capture storage supports runtime-owned pending
work without a future value kind or dummy Null. Process state retains actual
asynchronous completion/control. `NativeType.Never` is uninhabited metadata,
not another value alternative.

```sh
dotnet run --project tests/NativeModel/Msync.NativeModel.Tests.csproj -c Release
dotnet publish tests/NativeModel/Msync.NativeModel.Tests.csproj -c Release \
  -r linux-x64 -p:NativeModelAot=true -o .temp/native-model-v090/aot
.temp/native-model-v090/aot/Msync.NativeModel.Tests
```

The probe exercises every builtin in monomorphic and union storage, all IEEE
Number bits over seeded data, Path provenance, aliases, recursive references,
value/location captures, copy/move boundaries, actual incomplete Process
completion, and GC survival/release. It checks representation equality; source
numeric equality still follows IEEE (`+0 == -0`, NaN unequal) in the executor.

## Verified deployment probe

The Linux x64 managed and Native AOT probes both pass 25,880 contracts on the
current model. The seeded IEEE check transfers 4,096 representations through
monomorphic and union storage, with explicit signed-zero/infinity/NaN-payload
edges. The GC checks establish that the overlapping-reference carrier is
accepted by both runtimes, preserves moving-GC roots and releases obsolete
roots on union replacement. These are representation/ABI checks, not a claim
about complete source-to-CLI migration or an end-to-end speedup. Disposable
publish logs are `.temp/native-model-v090-aot.log`.

## General repeated memory

`NativeAllocation(layout, layoutId, elementCount)` retains the element schema and
logical layout identity. Its storage uses a compressed repeated-layout view;
metadata does not expand into one field descriptor per element. Empty static
element layouts reserve one byte so repeated namespace identities remain distinct. `NativeAddress.Index`
adds the static layout extent in both byte and GC-root coordinates. Checked
region admission validates concrete bounds and every physical field schema.
`CopyRegion` uses overlapping-safe copies of bytes, typed roots and initialization
bits; it does not load fields or invent Null for uninitialized storage.
`ClearRegion` drops roots and initialization independently of the language value.
A memory-backed Range snapshots membership immediately while retaining aliases to
referenced allocations. Function environments expose the same ordinary base
address model instead of a capture-specific allocation operation.

The managed and Linux x64 Native AOT probes now pass 25,880 contracts, including all-builtin repeated
layouts, overlapping raw/root copies, uninitialized copies, bounds/schema rejection,
union replacements, compacting GC and frozen Range membership. Artifact probes
also roundtrip runtime-count memory instructions and full Path literal provenance.

Path literals additionally retain definition-site `OriginId`/`OriginOffset` through
portable encoding, JSON inspection and optimization identities. A session may
capture different working directories for identical raw spellings submitted at
different definition sites; optimizer representation equality must therefore
include both full Path provenance and origin coordinates. Non-Path constants
use only the default origin, and malformed negative coordinates are rejected.

AoS-backed memory ranges select a field ordinal from an ordinary stride layout.
The default operation copies membership. The explicit `move` IR mode transfers
exclusive ownership of the backing region: `FromOwnedMemory` retains its typed
allocation and field stride without per-row descriptors or payload copying;
callers must relinquish all subsequent mutation. Runtime pending-work side tables
are handled by the VM, not encoded as fake native values. The model probe checks
nonzero field selection, uninitialized state, and constant-size ownership-transfer
allocation for 16,384 rows.

Format-6 compatibility retains retired Storage descriptions only inside constant
Type metadata, including nested element descriptors. This preserves historical
`type_name` and Type equality without admitting a Storage native kind, storage IR
type, source allocator, or generic runtime payload. Other metadata consumers keep
the ordinary closed builtin contract.

`NativeUnary.HasLayout` tests allocation layout identity, not structural layout
compatibility. Its `B` operand is a layout metadata index rather than a value
register; flow and optimizer register rewriting must read/rewrite only `A`.
Non-Address values have no namespace layout tag and fold to false when constant.

`NativeUnary.ValueType` observes the evaluated payload's Type descriptor, not its
slot's declared union or source `decltype`. `NativeOp.MakeType` constructs an
immutable descriptor from a closed kind mask and optional element-Type register.
Retired Storage bits remain descriptor metadata only. These operations neither
retain the observed allocation nor form a memory barrier; only known evaluated
portable literals may fold ValueType, and dynamic union payloads stay dynamic.
`MakeFunction.C = 0` denotes a plain zero-capture reference with no environment;
the normal owned-environment mode remains `-1`.

## Scope carrier admission and detached cyclic references

Physical Store admission checks every selected projection field. Builtin kinds,
Null admission and invariant IR pointer contracts remain mandatory. A Scope
field's referent-layout domain is a logical dependent-refinement constraint,
not part of the address carrier ABI; comparing that domain during physical
Store admission incorrectly rejects cyclic references. Range element checks
remain unchanged. Logical instruction-level assignment checks still apply.

The two-node source cycle (`first::next = second; second::next = first`) exposed
this distinction at a projected Store: its logical source admitted several
refined namespace layouts, while one possible selected field carried the same
nullable Scope representation but a narrower referent domain. The actual nodes
used the original allocation layout. `NativeArtifact.Pointers.PhysicalAssignable`
now separates those responsibilities rather than weakening projection safety.

`tests/NativeArtifact` covers referent-domain variation alongside rejected
Number-to-Scope, Null-to-nonnull, widened scalar and incompatible-pointer stores.
A forged projection omitting an admitted layout remains rejected. Projection
coverage errors include function, instruction, base register and both layout
sets. `tests/NativeHirLowering` exercises the two-node cycle through source and
source-free HIR, checked portable native artifacts and real execution at O0/O2,
with an independent four-hop sum of 6.

## Logical reflection and deferred scalar observations

`NativeFunctionCode.ReflectionSignature` optionally preserves a closed logical
parameter/result descriptor independently of the executable register ABI. It
uses `TypeRef` metadata, not payload objects. Imported historical allocator
functions can therefore describe Storage while executing ordinary typed address
headers. Portable encoding and JSON inspection retain this signature; admission
checks fixed arity, closed descriptor masks and legal nested elements. Source
functions without an override derive reflection from their execution signature.

`NativeBehavior.DeferredScalar` identifies immutable completion observations
whose demand may remain pending. Process stdout/stderr/success/exit_code retain
their Read effect and MayFail behavior without claiming to invoke a process.
The flag is explicit signature metadata, not a runtime process-name exception;
`process.wait` remains an explicit selected observation.

## Range logical descriptors versus physical item ABI

`NativeRange.ItemType` is logical Type metadata; `PhysicalItemType` is the closed
native carrier ABI used for allocation and loads/stores. Metadata refinement via
`WithItemType` preserves backing storage and the physical ABI, including across
deferred resolution. The explicit `Snapshot(values, logicalItemType,
physicalItemType)` overload materializes with that supplied static ABI and checks
actual builtin carrier membership. It never recreates a layout from a historical
Storage description or discovers namespace fields from a payload. Ordinary
2-argument snapshots keep their existing logical admission checks.

Native constructors optionally consume a logical element-Type register: `C` for
MakeRange/MakeLazyRange/DeferRange, and the sole optional `Inputs[0]` for
RangeFromMemory. Omission preserves source behavior. These operands are normal
initialized Type registers, not layout identifiers or serialized live handles.
Portable admission checks their register/type/arity contracts independently of
the Range's physical element contract. Core probes cover metadata relabeling,
copying/materialization, nested descriptors, shared aliases, deferred demand,
empty membership and rejection of incompatible physical carriers.

`CallNative.C` follows the same optional logical result-element Type-register
contract. It is accepted only when the destination is exactly Range with a
statically declared physical element ABI, never a broad Any, nullable/scalar
union, or IR pointer. The Type operand participates in definite assignment,
liveness and register rewriting but does not alter builtin argument arity or
physical snapshot storage. The existing instruction wire field is unchanged.
