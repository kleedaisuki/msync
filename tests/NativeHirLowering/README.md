# Direct native HIR lowering

`dotnet run --project tests/NativeHirLowering -c Release` analyzes real source,
exports HIR without source text, lowers both forms directly to native IR, and
passes each result through the actual artifact validator/codec and native VM,
checking independent numerical/path results at O0 and O2. Generic closures are
instantiated with unrelated builtin and namespace capture ABIs, and shared
mutable captures verify that adaptation preserves the original locations. It additionally
checks exact floating-point literal bits, empty logical-allocation freshness,
finite mixed-field projections and captured-parameter location ownership.
These are source-to-native execution checks, not a claim of complete installed
product acceptance. The CLI journey owns host integration and platform coverage.

`NativeHirLowerer.Lower` returns `NativeHirLoweringResult(Program, Layout)`.
`NativeHirLoweringSession` retains callable, local, allocation-site, type/layout
and static declaration identities across persistent submissions. Field names
are resolved here; the emitted runtime operations use addresses and finite
layout/ordinal tables. Captures occupy the first slots, followed by parameters.
A capture slot's type is its payload contract; a location-mode capture binds
the incoming typed address instead of copying the current value.

## Native operand contract

| Operation | Operands |
| --- | --- |
| Constant | A literal index -> Result |
| FrameAddress / BindLocation | A canonical local slot -> Result pointer / A local slot, B pointer register |
| Allocate | A static layout, B source allocation-site identity (or absent for compiler location storage) -> Result logical allocation reference |
| AddressOffset / ProjectAddress | A base address, B layout, C field ordinal -> Result typed pointer / A base, B finite projection -> Result pointer |
| Load / Store | A pointer, Type declared storage contract -> Result / A pointer, B value, C initialization 1 or assignment 0 |
| Narrow / Convert | A value, Type target -> Result; Narrow only extracts a closed alternative; Convert Immediate 0 implicit or 1 explicit |
| Unary / Binary | A operand, B optional Type-valued operand for IsType / A left, B right; Immediate selects the operator |
| Jump / Branch | A target PC / A Bool register, B true PC, C false PC |
| MakeFunction | A callable identity (or -1 with Symbol native name), B explicit environment layout, Inputs value/address capture registers -> Result |
| Call / AdaptFunction | A evaluated callable, B direct target or -1, C session dispatch slot or -1, Symbol optional finite dispatch map; Call Inputs arguments |
| CallNative / ProcessCall | Symbol native identity, Inputs arguments / A evaluated path, B scheduling mode, Inputs arguments |
| MakeRange / MakeLazyRange / DeferRange | Inputs values / Inputs values or closures, Symbol deferred bit flags / A producer closure |
| OpenIterator / NextIterator / CloseIterator | A Range, B cursor identity / A cursor, B exhaustion PC -> Result item / A cursor |
| TestStatic / StaticAddress / PublishStatic | A static identity -> Result Bool / -> Result namespace address / mark initialization complete |
| SelectPath / Transfer | A source, Symbol regex / A source, B destination -> Result |
| Barrier / Acquire | A original fence direction, B region-enter 1 or exit 0, Symbol region / A Process or -1 for all; optional Result acquired value |
| Return / Unreachable | A result value / Symbol original diagnostic reason |

Logical field-pointer contracts can be unions while the selected physical
field is monomorphic. Address formation retains its actual declared native
representation; Load copies into the declared logical result before Narrow.
Neither narrowing nor namespace aliasing reinterprets field memory. Ordinary
declarations captured by location allocate fresh one-field storage, including
each captured loop item. Scope initializer fields bind to the actual namespace
allocation before initialization; they do not allocate a second cell graph.
Captured parameters/by-value environments are promoted before nested closures,
while incoming location captures preserve their original address.

Each closure constructor describes its physical environment independently of
its callable identity. Environment fields contain the actual emitted register
type: a pointer to fixed storage for a location capture, or a value type for a
value capture. A generic template can therefore produce Number, String, Path,
Connection and namespace environments without imposing a universal payload ABI.
Template signatures describe abstract capture contracts; specialized executable
bodies retain their concrete contracts. Environment layouts are not namespace
layouts and never enter structural namespace projection alternatives.

Persistent lowering records physical storage types, source-allocation layouts
and entry-root capture provenance separately from semantic flow types. A first
non-null write may refine later semantic reads without changing an already
allocated nullable location's ABI. Later specializations reconnect to those
original root contracts even when their HIR capture source denotes the local
capture slot and the original constructor is absent from the new submission.
Namespace projections retain the historical physical layout alternatives.

Structural Scope type fingerprints include their field schema, not merely the
Scope kind. Explicit shape casts carry the finite compatible native layouts;
compatibility checks field names and possible builtin carriers. Bare Scope
remains opaque. A Scope field stores an address, not a nominal or invariant
referent graph: nested obligations are checked at dependent dereferences, after
the actual allocation tag selects a compiled field offset. Loads use the selected
physical field format and then narrow into the logical flow type. A finite-sum
field can therefore participate when the required builtin alternative is
possible; the actual payload is checked when read. Mutations through aliases
cannot invalidate an earlier unchecked host offset. Casts do not eagerly scan
cyclic graphs. This is typed layout admission, never runtime named-object lookup
or a field-memory reinterpretation.

Finite HIR construction domains without a single Shape use the same schema
admission as shaped values: a domain row is not a nominal allocation identity.
This matters for conditional returned links after an ordered collection mutates.
Call result registers admit every selected target's concrete return ABI, then
narrow to the caller's flow type. Hidden HIR Temporary locations remain temporary
native slots rather than persistent session namespace roots.

Both explicit and implicit returns first admit the declared function-result ABI;
the bridge Copy must survive propagation when its source has a different static
type, even if both use the same physical carrier. A bottom-typed unreachable
tail has no return payload and ends in `Unreachable`, rather than fabricating a
value and narrowing it into the declared result. The natural-order regression
checks these exits through source-free HIR, O0/O2, strict artifact validation and
actual execution.

Physical namespace candidates exclude logical-only flow schemas. The cycle
regression checks that projection cases belong to actual constructor/static
layouts. Host-created zip/enumerate/regex records use the same Core ABI schemas
as runtime factories, including different item carrier variants after a Range
join. A manually widened typed-HIR member write exercises both successful
String admission and rejected Number payloads through strict codec roundtrips
at O0/O2. The rejected write retains the original assignment span.

`AllocateScope` is also checked independently of standard-library loading: a
captured mutable field is initialized after its namespace allocation/binding,
then its retained closure returns 42 through stripped HIR and strict native
roundtrips at O0/O2. Malformed initialized `Scope` and `AllocateScope` arities are
rejected by both strict writer and reader. Explicit `$std` alias mutation and a
user-namespace callee-before-mutating-argument result of 299 exercise canonical
module cells and input-versus-final invocation graphs through the same routes.
