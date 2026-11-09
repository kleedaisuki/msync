# Typed indexed storage and dynamic aggregate facts

The ordered containers need constant-time indexed reads and writes. Repeatedly
materializing or enumerating an ordinary Range would hide a linear factor in
heap operations and tree arena access. `storage::Buffer` therefore owns an
explicit mutable Range backing store; it does not introduce another language
value kind or a process-wide handle registry. Source containers own the
algorithms, layouts, policies and node indices. The native storage operations own
only backing-list access, mutation and snapshot publication.

A snapshot copies membership, not the objects referenced by that membership.
Aliases of a stored Scope still refer to one Scope instance. Ordinary Range
materialization is unchanged. The compiler retains the inferred element type,
required Scope fields and callable signatures across indexed reads, and checks
replacement/insertion against that fixed schema. An empty untyped Buffer solves
its carrier on the first write; `Buffer::empty(prototype)` supplies it explicitly.
Only an actual top-level Path/String store conversion changes representation;
structural record checks do not silently rewrite nested field representations.

## Runtime carrier resolution

An initially untyped empty Buffer starts with a `Never` item carrier. Its first
successful write resolves the carrier on the existing Range object; aliases,
including those retained between REPL submissions, observe the same solution.
Clearing the contents does not reopen that solution. A snapshot made before the
write remains an independent empty `Range<Never>`; subsequent snapshots publish
the solved carrier.

`RangeValue.ItemType` remains an ordinary backing-field getter. No dynamic type
provider, virtual call or additional field is added to ordinary Range reads.
Only Buffer writes may perform the one-time carrier solution. An owned buffer
retains its immutable initial carrier for equality and hashing through
`IRangeTypeOrigin`; that interface is consulted only by Range equality/hash,
never by iteration or item-type access. Existing ordinary Range descriptor
comparisons are preserved, ownership-retention bookkeeping is excluded, and a
live Buffer stays in its original hash bucket after inference.

The compiler also records eager Buffer snapshot calls with point-in-time semantic
argument metadata. Later solving the source Buffer's inference cell must not
retroactively change the result contract of an earlier immutable snapshot.

## Values are not prototype locations

A prototype is used to infer an element schema, not to identify the location of
an eventual stored element. In particular, a Number copied from a nullable
parameter is a Number value, not an alias of that nullable parameter's storage.
Keeping its `Info.Storage` caused a real B-tree range traversal to acquire a
spurious `Number?` child index after other generic calls. Indexed element
construction removes that top-level value-flow location. Scope field locations
and the Buffer's own callable element domain remain explicit.

Nor can one prototype Scope identity stand for every concrete stored object.
After an object escapes into indexed storage, the compiler uses a dynamic-memory
view: its field types remain available, but its initial scalar values are no
longer permanent facts. Joined element schemas retain provenance to their input
Scopes, so mutation through a selected element also invalidates stale facts on
an original source alias. Each selected aggregate read has its own abstract read
identity; that is not a claim that the runtime objects are disjoint.

## Flow proofs and callable contracts

Memory versions are immutable tokens in `Frame.Facts`, rather than one mutable
global counter. Control-flow forks keep their own tokens. A possible indexed or
escaped-field write changes the token and invalidates earlier member-value
refinements. An unmodified alternative branch retains its refinement. A cached
function specialization's memory effect produces a fresh token on each call,
so a second invocation cannot accidentally reuse the first invocation's proof.
These are compiler facts only; no version checks are added to executing bytecode.

Actual function values retain their runtime identities and captures. A mutable
callable element uses the established callable-storage domain and dispatch
machinery. A signature's declared callable parameter determines whether a native
operation invokes a callback; passing a Function as a Buffer element does not
invoke it merely because the first argument is a Range.

Possible Scope field locations connected by indexed selection also retain
concept contracts. These memory-alias links are separate from ordinary callable
value-flow links. Checking a selected function field against a concept cannot
subsequently be invalidated by assigning an incompatible function through an
original object alias. The ordinary non-indexed path does not traverse an alias
graph when there are no such links.

An already checked concept also retains its associated-type proof when a
dynamic read can no longer treat an ordinary Type-valued witness field as a
constant. The required member storage contracts still enforce that proof; an
unrequired mutable witness is not a runtime type declaration. This keeps, for
example, a present-Null Option distinct and well typed after indexed storage.

Failed submissions restore newly exposed views, first-write element solutions
and alias links. Editor and metaprogramming-query snapshots copy the mutable
metadata graph, including shared storage domains and alias links. A failed or
successful `is_invocable` probe cannot constrain a later real Buffer write.

## Focused integration evidence

`tests/IndexedStorage` executes the production compiler, binary artifact codec,
context binder and interpreter. Its source/serialized `.mso`/serialized `.mexe`
cases at O0/O2/O3 distinguish independent elements, repeated-index aliases,
original aliases, nested objects, callable replacement, nullable member proofs,
snapshot membership and actual Path/String conversion. Negative cases cover
fixed carrier/field schemas, invalidated null proofs and concept-violating alias
writes. A selected metadata-query case verifies both rejection and isolation of
the indexed callable-alias contract; a session case checks failed first-write
rollback. Ordered-container owners additionally exercise the complete source
algorithms against their independent models.
