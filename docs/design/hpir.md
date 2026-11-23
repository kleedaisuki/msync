# HPIR High-Performance Aggregate IR

2026-11-22 · Design draft for the bound `.mexe`. HPIR is physical guest
code: specialized primitive operations, pointer operations and calls over
a VM-owned native stack and heap. SSA and source aggregates belong on
the producer side. They are not a second abstract machine inside HPIR.
This replaces the earlier proposed SSA/place surface for `.mexe`; its
ordering, identity and effect obligations still apply.
See the [implementation and acceptance plan](hpir-roadmap.md) for phase
dependencies, exit criteria and delivery gates.

## The boundary

```text
LLVM-derived typed computation + producer contracts
  -> .mso: relocatable input
  -> select layouts, calls and contextual effects
  -> .mexe: HPIR + layouts, relocations, root/unwind data and bound resources
  -> execute over the guest stack and heap
```

HPIR does not carry a parallel portable instruction stream or a generic
slot for each SSA definition. The producer selects actual object field
offsets, frame sizes, calling conventions and where live values have to
be materialized. Program-relative entry, constant and layout references
are relocatable; process pointers are not serialized. Loading resolves
those references and checks the artifact boundary. It does not re-prove
aliasing, reclassify calls, rediscover layout or replan effects.

For example, a string literal has a program-relative ID. The execution
owner links one nonmoving constant bank for its image, storing UTF-16
code units preceded by their length. Instructions obtain an address from
that bank. A dynamic Number-to-String conversion instead writes the same
layout into the guest heap. Both use raw text addresses, and the bank
is released with the execution rather than traced as individual objects.
Ready String queries and transformations run over these guest UTF-16 spans.
Indices and length count Unicode scalars, not UTF-16 units or grapheme
clusters. Ordered comparison uses scalar ordinal order, and casing uses the
shared [Unicode 17.0 default casing](https://www.unicode.org/versions/Unicode17.0.0/core-spec/chapter-3/)
tables rather than the host locale. Immutable inputs can be reused when a
transformation has no effect; ordinary queries do not enter allocation or
collection.
Ready Regex predicates consume guest UTF-16 directly, using the Path's
native spelling for a Path subject. Compiled-in patterns retain their
program-relative string IDs; dynamic pattern strings enter only at the
host regex boundary. Both use the established culture-invariant
[.NET engine and its static cache](https://learn.microsoft.com/dotnet/api/system.text.regularexpressions.regex.ismatch?view=net-10.0),
not a second parser or a changed matching policy. Escaping and replacement
publish a String only when its value changes.

`regex.find` uses the existing `RegexMatch` and `RegexGroup` schemas. The
host result is a nullable pointer; HPIR writes the guest's ordinary union
tag and publishes the successful record graph at one safepoint. The
group snapshot has the exact engine cardinality and typed pointer backing.
Offsets and lengths count Unicode scalars, including groups captured before
the match by lookbehind; an unmatched group has index `-1` and length zero.
A direct local group projection keeps the producer's vector shape only
while the writable group field cannot be replaced through that object.
General lazy `regex.matches` and `regex.split` are separate producer contracts.
The live Text Range uses a distinct inert record for its source. A file
record captures a Path value without opening it. A typed Open/Next/Close
call protocol owns the host cursor; the first pull opens a file or waits
for stdin, and each successful pull publishes one immutable UTF-16 String.
Only the host reader receives the cancellation token; it never holds a
guest pointer. Explicit loop exits close the cursor, and execution unwind
closes any still owned by the invocation.

`string.join` over a known live Text plan consumes that same cursor directly.
The terminal does not build a guest vector or publish per-line guest Strings.
It stores no builder for zero or one line; multi-line joins accumulate host
chunks and copy once to their final guest UTF-16 result. Each join opens
and closes its own cursor, so reuse observes later file contents. At the
first pull, only the exact file resource frontier and explicit language
ordering apply; an unrelated pending Process is not an implicit barrier.

`.mso` retains its relocatable role. Independent `bind-ir FILE.mso` must
receive the facts HPIR needs without the source. Today's `.mso` already
contains slot-oriented code, and moving a lowerer to bind time cannot
recover facts that were discarded or optimized incorrectly upstream.
The development candidate selects the guest program before encoding
either stage. The relocatable image contains that code plus typed
contextual inputs, and the executable contains the same code plus bound
resources. Binding only replaces the effect section; it cannot mutate
function bodies, physical frame geometry, or pointer maps. Source-free
round-trips use separate processes in the
[HPIR pipeline journey](../../tests/HpirPipeline/README.md).
The format-8 decoder takes one extent snapshot for a seekable input and
checks table/text bounds against that snapshot and the current position.
Exact reads still reject truncation, and the final position check still
rejects trailing bytes. On non-seekable input, tables and text grow only
as bytes are actually read.
The development codec is not the public wire contract. In particular,
structural decoding is not isolation for hostile physical instructions;
the initial caller must supply a trusted executable. The public cutover
must specify which contracts can execute images and which only inspect
them.

The first detached HPIR implementation exercises that rule with a
straight-line local transfer region. The [producer](../../src/Msync.Hir/Hpir/LlvmHpirLowerer.Effects.cs)
recognizes literal paths and their lexical joins, literal text, or a
literal-path selection in the optimized graph. It emits one service
call per effect at its original instruction, using an indexed typed
input; the path/selection operands do not become physical code.
Unmodeled guest calls, control and working-directory changes do not
enter this region.

The [binder](../../src/Msync.Runtime/Hpir/HpirBinding.cs) anchors paths
and replaces inputs with prepared resource entries, without rewriting
the program or its calls. A selected list records names, not bytes.
An observed empty selection remains empty. An unavailable or affected
observation retains a residual selection against the anchored directory;
a disjoint exact write does not suppress a safe observation. The
[resource frontier](../../src/Msync.Runtime/Hpir/HpirTransferFrontier.cs)
indexes actual nominal/canonical path overlap. An unclassified preceding
tree write can invalidate a later observation through a nested link, so
that observation runs after the relevant unresolved writers.

Execution submits those resource entries through the same
[work owner](../../src/Msync.Runtime/Hpir/HpirWork.Transfers.cs) as other
typed host work. Recorded predecessor IDs link to submitted tasks;
independent work can overlap subject to the real transfer capacity.
A failed predecessor completes its resource obligation, so dependent
work can still observe the actual filesystem; the execution owner
reports the original failure once. The existing host copy and
publication service's managed admission types do not enter the guest ABI.

A straight-line region mixing closed Path/Text effects with modeled file
work emits `SharedTransfer`, an equally void, argument-free call. Both
modes share one execution-owned transfer-capacity gate. A pure prepared
region continues to use its bound predecessor IDs without a runtime file
frontier. In a mixed region, prepared sites register active permits and
query only residual permits; residual sites can query both partitions.
This records actual dependencies in both directions without recomputing
the prepared graph. Mixed admissions retain lexical and current canonical
keys during namespace replacement. An opaque call or a mixed selection
is not a reason to freeze a potentially invalid environmental observation.

A direct live Path selection captures only a Path record and an interned
pattern in a fixed guest plan. A transfer consumes that plan on demand,
reserves a directory read and a destination write in the file frontier, and
enumerates after the overlapping predecessors complete. It shares the host
cohort transport with prepared selections, including replacement and remote
publication semantics. The plan is not a materialized membership vector.

A qualifying `for` consumes the same selector through the typed Path
cursor. Only Open distinguishes a regex selector from a plain directory
plan; Next and Close preserve the normal iterator ABI and actual
continue, break, exhaustion and unwind edges. First pull waits for
overlapping writes, then filters the original ordered directory entries
using their basename without building boxed Range items. A selection
that reaches no Open remains just a plan.

For the entry prefix before a control edge or host effect, the producer
can attach a membership recipe when the directory is a known local Path
and every use of the selected plan is a typed Iterator Open. Binding
observes just that ordered lexical member list, without running guest
instructions. Executable wire 8 carries it as optional metadata: an
empty list is a frozen empty result, while no list means the Open is
residual. Next uses the existing Path record and cursor ABI in both
cases. A frozen cursor performs no filesystem enumeration. A residual
cursor still observes its original first-pull frontier. The separate
prepared file-transfer membership recipe retains its own effect contract.

Path fields in a selector plan borrow their Text. Some pointers can
be directly linked literals or interiors of a projected Text allocation.
The earlier development wire-7 selector recipe incorrectly listed
those lanes as exact heap roots. Wire 8 also uses the final four
previously unused plan bytes for the optional membership index.
Recompile development wire-7 artifacts.

## Primitive execution

The core is fixed-width numeric and Boolean operations, bit operations,
pointer arithmetic and typed dereference, plus calls and the structural
jump, branch and return operations. A dereference operates on actual
guest memory, not on a language-level address descriptor. It can be
encoded as a memory operand or as a separate read/write according to
the executor. HPIR does not require a `FrameAddress` temporary,
`NativeDatum`, width/layout lookup, and separate dispatch for a known
field. SSA names in examples denote intermediate results; they do not
imply that the VM needs a register file or a storage cell for every value.

```text
# Counter.value has a compiled offset of 16.
p = ptr.add counter, 16
x = read.f64 [p]
y = add.f64 x, 1.0
write.f64 [p], y

# If bump mutates the same field, the snapshot must precede the call.
x = read.f64 [p]
d = call @bump(counter)
y = add.f64 x, d
```

The first read/write pair is real: it updates the shared object. The
second read is at its source-visible point; moving it past a call that
changes 9 to 10 and returns 2 changes the result from 11 to 12. A pure
local update needs neither read nor write unless the selected ABI
actually materializes it. SSA block arguments are lowered by the
producer; an edge swap is simultaneous even if its physical move
sequence needs a scratch location.

Every layout and carrier is fixed by a producer or chosen by a real
runtime alternative. A `Number` is binary64 where the language requires
it. Word-sized pointers and Boolean/integer carriers have their own
operations; a finite union only has a tag when the alternatives actually
need one. Pointer arithmetic addresses storage belonging to the VM,
not an exposed source pointer API. Interior addresses retain their
owner across collection, or are formed again from a retained base.
Physical storage must define the same initialized-field behavior as
source; that does not require an initialization guard on a proven
ordinary temporary. Null is not an uninitialized marker.

## Own the stack

The physical interpreter starts a fresh executable. On x64 it charges
interpreted work to the function that executed it, not to the image.
After `max(16,384, 64 × static instructions)` guest operations a function
may publish at a taken backedge or on entry at a direct call. A direct
callee repeatedly reached by already-emitted code also publishes after
four unresolved calls; native code should not bounce through the executor
for each invocation of a tiny helper. Host service time does not count.
The executable's binding and serialized format do not change.

The execution owns its code cache. It publishes one adaptive body per
function, retains its executable pages until the run ends, and keeps the
entry address and local PC table in indexed data. New callers embed the
address of a cached callee; callers emitted earlier read its entry slot.
A miss leaves the original HPIR call untouched for the executor. No
published instruction bytes are rewritten. Nested calls, including
recursion, push the normal guest frame and jump; the return follows the
parent function's PC table. A cold parent, a segment transition or an
owned-work exit uses the existing executor boundary. The native entry
resumes the selected PC with the existing stack, heap, banks, and work
owner. It does not replay effects or re-enter its caller.
The executor caches the current function's machine readiness during
dispatch. Calling, returning, and publishing are the points where it
can change; scalar dispatch does not query the cache repeatedly.

Within a straight-line run of binary floating-point or word operations,
the next operation can consume the previous result in a CPU register.
An intermediate frame store may disappear if the next operation
overwrites the exact same bytes and no other input can observe the
store; an indirect operand in the consuming operation keeps the store.
A branch entry ends the
run. Effect boundaries and physical aliasing retain the same frame
contents; this is local instruction selection, not another runtime IR
or a reconstruction of producer flow facts. See
[Liftoff](https://v8.dev/blog/liftoff) and
[copy-and-patch](https://arxiv.org/abs/2011.13127) for related baseline
code generation techniques.

Short runs and unsupported executable-page platforms keep using the
physical interpreter. `MSYNC_JIT=off` and its `linked` alias retain
physical execution; `force` is the explicit eager measurement control,
using the same emitter and no extra executable representation. The
[whole-image admission evidence](../benchmarks/hpir-platform-gate.md#admission-measure-emission-separately-from-execution)
describes its historical adaptive predecessor; see the
[function-cache comparison](../benchmarks/hpir-machine-blocks.md#function-owned-adaptive-code-cache)
for the current policy.

A closed ready self call with a return-only continuation does not need
another physical frame. Selection recognizes unconditional forwarding
through the exact predecessor's PHIs or a direct return, evaluates the
actual callable environment and arguments, uses the same parallel move
logic as an SSA edge, and emits a normal backedge to the entry body.
One-time literal linking is outside that loop. A frame with local
addressable storage, a hidden result address, a pending ABI, or calls
that can own work retains the ordinary CALL/RET path. A fresh captured
heap allocation still executes on each iteration and can outlive it.
There is no frame reuse test in the runtime and no duplicate tail
instruction stream. Compare
[LLVM's tail-recursion elimination](https://llvm.org/doxygen/TailRecursionElimination_8cpp_source.html),
which also loops over argument PHIs; HPIR does the transfer using
already selected physical placements.

A guest callback that can suspend while delivering a scalar is not a
synchronous call just because its final scalar is ready. For the
producer-selected numeric `front(transform(...))` route, the producer
keeps invocation work separate from descriptor and yielded-item
readiness. The scalar terminal publishes a concrete Number completion
and starts the selected fixed entry on a separate VM-owned stack.
The mutator parks that stack only at an unresolved typed demand, keeps
its exact safepoint roots in the shared heap, and continues the caller.
Its next slice resumes at that same demand with prior writes, launches,
and frame state intact. Host tasks signal their own completions only;
they never execute a guest instruction or touch guest storage.

The ordinary pure transform and cursor calls do not acquire this stack
or scheduler. The development gate admits an entry terminal over a
selected numeric plan whose callback waits only through supported typed
scalar/text demand, including a released process or line read. More
general Range terminal structures and callbacks with other suspension
points need their own producer contracts, not a catch-all async value.
Unlike [LLVM's split coroutine frames](https://llvm.org/docs/Coroutines.html),
the VM stack and physical PC are already explicit here; keeping that
stack parked avoids splitting every transitive guest callee or adding an
alternate continuation instruction stream.

Each guest execution owner holds a nonmoving segmented stack, a guest
stack pointer `sp`, a segment limit, and the information needed to return,
unwind, and find roots. HPIR does not change the host CLR or platform
stack pointer. An ordinary guest call does not use a host call frame or
construct a managed `Activation`, `NativeFrame`, or allocation view.
The callee entry has a constant, prealigned extent. A normal call enters
by advancing `sp`; return restores its saved value:

```text
call @F(args):
  base = sp
  sp = ptr.add sp, @F.frame_bytes   # in the current segment if it fits
  save continuation in the frame header when needed
  enter @F with the fixed argument/environment ABI

ret result:
  restore saved sp and continuation
  deliver result using the fixed return ABI
```

This is one calling convention, not an extra bytecode sequence or
allocator invocation required around every `call`. A frame can be empty
where the generated call/return convention needs no storage. Argument
evaluation and any needed snapshot precede entry; the same physical
location may serve as outgoing and incoming storage if the compiled ABI
permits it. The callee does not revalidate parameter carriers on a known
call. Indirect calls use the ABI of an admitted entry, not a scan of all
parameters and captures.

A segment capacity test is a resource check, not a language type/alias
check. A gap that cannot fit sends the same call to segment growth or
reports stack exhaustion. A sequence with reserved space needs no
repeated individual capacity checks within that reservation. Do not
replace segment semantics with an assumption that a host fault is a
recoverable guest failure. Ordinary frames do not cross segment
boundaries and are never relocated while an interior address is live.
An escaping cell goes on the heap; a pending job may retain its inputs
or a heap cell, not an address into a popped frame.

For collection or unwind, use the frame chain and producer-supplied
locations of live guest pointers and cleanup obligations at defined
execution points. Return and failure release only the resources the
frame owns. Descriptor resolution, host callbacks and error reporting
may have host wrappers at actual boundaries; the guest CALL/RET ABI
does not depend on creating those wrappers first.

This is not hypothetical ground-up infrastructure. The current
[`NativeFrameStack`](../../src/Msync.Runtime/NativeModel/NativeFrameStack.cs#L61)
already owns nonmoving native segments and an explicit cursor; some
[fixed native calls](native-execution-lowering-v090.md#reserve-and-release-a-physical-fixed-frame-without-a-managed-view)
reserve and release frames without a managed view. Those calls still
enter under a set of eligibility conditions and use managed callbacks.
HPIR makes the physical convention the normal execution model, rather
than trying to widen one fast subset through more admission checks.
[LLVM segmented stacks](https://releases.llvm.org/19.1.0/docs/SegmentedStacks.html)
illustrate the capacity/growth split without mandating their host ABI.

## Own the heap

The VM owns guest object allocation, scanning and reclamation. A concrete
layout has fixed fields, size/alignment for fixed records, and pointer
tracing data; variable-sized allocations carry their actual extent.
New records, escaping cells, environments, typed dependencies and
materialized data use that heap. Freshness and singleton publication
remain language behaviors. Heap allocation is a call to the guest
allocator with a compiled layout or a known allocation site, not a
general type test to choose how to allocate each value.

Use the existing [native guest heap](../../src/Msync.Runtime/NativeModel/NativeGuestHeap.cs#L6)
as evidence and a starting point: it already owns native slabs, bump
allocation, reusable blocks and tracing. The design aim is that a guest
allocation is not obliged to publish a CLR `NativeAllocation` descriptor
or allocate managed storage to make every field addressable. Nonmoving
storage plus exact object/root metadata is the baseline; growth, sweep,
large-object handling and host references stay with the owning runtime.
Trace guest pointers only from producer-described roots and object
layouts at real collection points. Host resources use explicit handles
owned at the boundary; no raw pointer to an unpinned CLR object is an
ordinary guest edge. [LLVM statepoints](https://llvm.org/docs/Statepoints.html)
provide a useful comparison for making live roots explicit, not a reason
to require LLVM's collector protocol.

The [HPIR heap substrate](../../src/Msync.Runtime/Hpir/HpirHeap.cs) gives
one mutator native slab allocation, small free bins, and nonmoving
mark/sweep. A block carries its extent and concrete layout ID, not a CLR
object descriptor. Sweeping coalesces holes, resets the unused tail of the
active slab, and releases empty inactive slabs; oversized blocks use
separate slabs. Reused payloads are cleared before publication.

A known base uses the block header directly. A by-location argument or
captured field that may hold an interior pointer or a stack borrow instead
has an explicit borrowed offset in its root map or layout. The
[GC-only resolver](../../src/Msync.Runtime/Hpir/HpirHeap.Borrows.cs) first
indexes slabs; a stack-only borrow cannot trigger indexing of individual
objects. When a borrow does point into the heap, the collector indexes
occupied block bases in slab address order and retains the owner. No
ordinary read, write or call performs that lookup, and no mutator updates a
per-object address index. Guest work crossing an asynchronous boundary
must publish roots through the owner, rather than mutate this heap from a
host worker.

Named Scope declarations are physical globals, not a distinct object ABI.
Each referenced declaration links one allocation of its already selected
layout and a byte for initialization state. The initializer still runs
on the first execution of the declaration. Relocations address the record
or the flag; optimized constant field displacements use ordinary pointer
addition. The execution owns exact global roots across collection (including
anonymous allocations reached only through global fields). Two runs of a
bound image get independent state; persistent interactive ownership is a
separate session obligation, not part of the executable wire.

## Aggregates disappear into layout and calls

| Source fact | Physical HPIR consequence |
| --- | --- |
| Concrete record or cell | Guest pointer; use compiled offsets and typed dereferences. A genuine alternative layout is selected only where it matters. |
| Known function, varying captured environment | Direct entry plus environment pointer. No per-call reconstruction of unused code/name components. |
| Truly varying function target | Compiled entry selection or indirect call with its selected environment and ABI. |
| Known Range producer and consumer | Primitive loop and/or call to a specialized cursor implementation. A live stream or effectful callback retains its demand, ordering and cleanup contract. |
| Known deferred `Number` or `Bool` | Pointer to a concrete completion layout; specialized helper calls implement lifted operations and demand. No `Task<NativeValue>` in the guest ABI. |
| Prepared filesystem work | Calls over linked plan/resource data. Bound member lists are not re-enumerated; payloads, failures and completion are still live. |

A finite ready union carries a byte discriminator, raw payload as needed
by its alternatives, and concrete pointer lanes. A proven refinement
projects the selected payload directly; an actual null observation reads
the tag. Extending a union reserves only the destination's necessary lanes
and zeros unused pointer lanes before those lanes can be traced. A
producer-known pack immediately projected back to its original carrier
can disappear before LLVM optimization.

Contextual control is explicit: a typed straight-line metadata predicate
with a relocatable path can carry a binding recipe into the object file.
Binding observes it and links a Boolean result without interpreting the
branch or executing either arm. A failed optional observation remains
a live typed check at the original control point. A preceding file writer
or opaque resource operation leaves the predicate on the actual execution
frontier, where it can observe and wait for that writer.

A first-class Type witness is a word-sized image-relative ID into an
immutable literal table. Nested type elements are separate table links.
Static type predicates use producer contracts and need no runtime call.
A runtime-selected witness uses the typed comparison service, not a
generic value descriptor. The linker allocates constant-address cells only
for literals whose addresses survive LLVM optimization.

Operations that launch process or I/O work, allocate an object, advance
a nontrivial cursor, or create a dependency can be typed functions
reached through the same CALL mechanism. They need not become a second
semantic opcode family, nor round-trip through a universal native
dispatch by name. A ready add is primitive; a pending add calls a typed
helper and creates the required identity. A later payload demand is
another call or an equivalent specialized operation on that layout.
Never treat passing a deferred value as demanding it.

### Typed scalar work

The scalar completion runtime uses concrete Number and Bool payload fields
and exact dependency pointers. A pure lift is a new guest-heap node, not a
managed job; copies preserve its pointer, even after materialization.
Demand expands a DAG iteratively, caches the typed payload, and removes
the dependency edges once consumed. A true ready/deferred join has a
nullable exact-root lane and a separate typed scalar lane; the ready case
does not allocate a completion. Ordinary ready scalar instructions use
neither lane nor the work dispatcher.

When a scalar can really stay pending inside a finite union, the same
completion travels in one extra exact-root lane alongside the existing
tag and raw payload. The producer selects that capability on a specific
field or function boundary; unrelated ready unions keep their compact
layout. Aggregate copies, direct arguments and caller-owned returns
preserve the lane. A write of Null or a ready scalar clears it. A null
predicate over pending Number or Bool retains the selected dependency:
it does not read its payload, but observing the predicate still propagates
the underlying failure. The Null branch never waits for an unselected
scalar. The existing collector traces the completion just like any other
exact guest root.

Filesystem metadata (`io.file_size` and the `fs` file-kind, emptiness,
and readonly predicates) produces the same mixed Bool or Number carrier.
Uncontended local host queries fill its scalar lane directly, without a
guest completion or managed job; live conflicting writes or remote
endpoints use typed jobs. Reads with outstanding work share the live
transfer resource index: they depend on matching preceding writers, and
the read lease holds a matching later writer only through physical host
completion, not until the guest chooses to observe the value. A local
host error is kept as a deferred failure at the original source site.

`io.read_text` uses a concrete Text completion and admits a live read lease
in that same frontier. A transfer whose source is still pending borrows the
typed host result, not the guest address, and owns the payload dependency.
It can admit and record its destination write before the source is ready;
it acquires a transfer buffer permit only after the payload resolves. The
same completion can still be demanded separately by guest code. Stream
classification releases the namespace read lease before opening a FIFO or
other co-start endpoint so its matching writer is not held by the reader.

An existing demand can advance a concrete carrier without demanding any
other value. A direct null test in the line producer's block publishes
that exact Text completion. A dominated Number or Bool conversion of
the same SSA producer (including a representation copy or one assignment
to a private local in the producer block) reads the published text pointer
and parses at the original conversion site. In a non-entry block this
specialization follows only exclusively reached immediate continuations,
so entering a branch from elsewhere cannot reuse a previous iteration's
observation. The producer
records the ready result before interprocedural scalar propagation and LLVM
optimization, so the callers and their captured Number fields do not
inherit a Future lane. A skipped test, an unrelated read, or a reassigned
location does not establish this fact. This is an observed-value
transition, not a new implicit await or a readiness guard on arithmetic.

Ready scalar casts do not enter the work owner. Number-to-Bool is an
ordered nonzero test, so both zeros and NaN are false. Bool-to-Number is
a byte-to-binary64 conversion; Bool-to-String selects one of two existing
immutable lowercase text literals. Text parsing uses fixed typed helpers.
A Path parse reads its native spelling, not the bound absolute location
or the qualified display form; an error still names the original Path
and its display spelling at the source conversion. An identity cast
uses the existing compatible-representation rule. A cast over a real
union needs selected alternative semantics, not a conversion by raw
width.

A Connection has one physical word: the aligned, immutable endpoint
address Text pointer plus its three-bit endpoint kind. A literal
relocation preserves both pieces without making a host handle. The normal
link bank aligns literal Text to eight bytes; a retained session
Connection borrows its existing heap Text, so the short interior
pointer remains traceable by the existing borrowed-root machinery.
Compare [LLVM's PointerIntPair](https://llvm.org/docs/doxygen/classllvm_1_1PointerIntPair.html)
for the alignment-based packing mechanism. This encoding is only the
Connection lane, not a common representation for guest values.

Qualification installs the actual endpoint on the existing Path record.
SSH/WSL preserve Raw and discard the previous local spelling and
anchors; Local preserves the input anchors, or supplies the current
ones when absent. Pseudo-endpoints use their real local context when
selected. No transport or authentication work happens at qualification.
Path-to-String projects the native pathname; REPL display and parse
errors retain the full endpoint punctuation. The artifact JSON includes
the real relocation and the editor hover alongside the physical graph.

Host work is admitted by one execution owner under a typed job ID. The
host receives copies of its arguments and never holds a guest frame
pointer. The producer describes direct Process argv as fixed Text, Path,
Number and Bool operands; the runtime formats them only for the actual
host launch. A ready `process.spawn` snapshot supplies its existing data
pointer and length with one concrete item representation.

A Process handle refers to one job with distinct startup and completion.
The pipe controls use startup, not child exit; the synchronous path call
and `process.wait` use completion. Before that observation, Number exit
code, Bool success and Text stdout/stderr retain exact edges to the same
handle until demand. After an explicit completion edge, a selected Text
projection writes its guest String directly. Neither form starts another
process or creates a new host output reader for the projection.

A job owned by a child frame is promoted through an exact reachable
completion or process handle at return. An unobserved, unescaped read is
cancelled and joined by its frame; an unescaped Process completes before
returning. A sweep forgets dead weak observations before reusing guest
addresses and releases successfully completed root Process results once
their last guest handle or projection dies. Incomplete or faulted jobs
retain their existing settlement and diagnostic obligations. Execution
teardown cancels on failure and joins host cleanup before releasing VM
state.

A direct transfer receipt formatted solely for a second file transfer
keeps its scalar completion as the physical dependency. The second
effect borrows the original typed byte task at admission and converts
its result to the canonical `true` or `false` text in the file worker.
There is no guest wait, intermediate Text completion or extra host
operation. The same resource frontier still orders any intervening
reads or later writers, and an ordinary false receipt does not duplicate
the originating transfer's failure diagnostic.

`io.write_text` and `io.append_text` are explicit synchronous guest
effects, not transfer arrows. The import receives a concrete Path (or
text spelling anchored to the bound context) and either ready UTF-16
text, a typed Text completion or a direct transfer receipt. It observes
the arguments before reserving the destination on the shared file
resource frontier, and returns only after the write and lease release.
Any asynchronous host operation retains host values, not guest frame
addresses. The shared typed host kernel preserves strict UTF-8 without
a BOM, replacement through symlinks, append flushing and the existing
remote protocol. Dry run retains the logical observation and ordering,
but does not perform the write. An explicit-write failure stops the
guest and keeps its originating source and host error.

Ready, materialized vectors expose their length and element stride
directly. The producer-selected `ranges.at` validates a dynamic Number
as a nonnegative integral ordinal below 2^63. The invalid sentinel is
outside every vector, so one unsigned bounds branch sends both invalid
indices and real misses to source-attributed error handling. The
successful path uses pointer arithmetic and copies only the concrete
element lanes; for a Function those lanes retain its already-selected
entry and capture environment. Literal indices into a known literal
vector need no dynamic check. `front`, `first` and `size` share this
representation without opening a cursor or creating an iterator.
`first` copies the first actual item, including Null, without rerunning
the producer; only the empty path reports its no-match diagnostic.

A selected `Type` vector predicate uses the same physical length and
word pointer. `all_of`, `any_of` / `any`, and `none_of` / `none` call
the fixed Type-to-Bool guest entry with its actual captured environment.
The first decisive result branches directly to completion; neither
the next member nor the callback is touched afterward. Empty snapshots
return the Boolean identity before reading the data pointer or the
capture lanes. Snapshot construction still evaluates its own operands
as specified. No service, iterator object or host delegate represents
this quantifier. This is the same completion rule as
[Rust's short-circuit consumers](https://doc.rust-lang.org/core/iter/trait.Iterator.html#method.all),
applied directly to a selected HPIR snapshot.

A Text-valued map over a concrete Scope snapshot also keeps its pointer
lane. The selected callback's Scope argument receives the existing
member reference; a finite callback view must admit every declared
incoming layout, while a structural view does not add a tag or copy.
Standard join traverses that map only at demand. Repeated references
stay aliases, and record layout and captured-location identity remain
the producer's. Ready Type witness equality compares the existing
interned word. For dynamic `meta.type_name`, the producer appends the
canonical display strings, in witness order, to the existing immutable
Text bank. Execution uses the witness ordinal to return that linked
pointer without constructing a managed Type or allocating a new String.
The indexed shape is analogous to an [ECMA-335 metadata token](https://www.ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf);
HPIR witnesses remain local to their own linked image or session.

For a literal `Range<Number?>`, LLVM production packs each concrete
item into the existing finite Null/Number ABI before the constructor.
The snapshot uses one owner with homogeneous inline items; every cursor
compares its index against length independently of the item discriminator.
An in-range Null is an item, while an out-of-range indexed read takes
the ordinary absent-index path. `builtin.range.nullable_number_items_packed`
counts producer conversions rather than reanalyzing values at a pull.

For a closed producer, the existing concrete field write index retains
the snapshot carrier at direct `Scope` field consumers when all possible
assignments agree on element and header geometry. A sparse producer
worklist propagates seeded layouts through locals, direct calls and field
writes; opaque origins, unseeded components, or incompatible payload
carriers decline this specialization. Literal snapshots and their windows
use the same length/data header; the owning edges can differ. Cursor cloning
forwards the same owner into a new scope with its own position. Compatible
writes through aliases still work. The by-value entry retains the physical
Scope domain already in its specialization key, without making parameter
storage immutable. An explicit selected target keeps the field carrier
through LLVM optimization; HPIR does not rediscover it at binding or runtime.

The standard Set cursor consumes a materialized counted `generate`. This
uses the existing Repeat, index and Transform kernels. The producer retains
the exact closed Number callback's return ABI, but considers that edge a
traversal until `materialize` publishes the snapshot bytes. A lazy Range
is not admitted as a snapshot on the strength of its language type alone.

The adjacent check in `is_sorted_with` uses a two-item view over a ready
snapshot. When the view's actual uses are local `first` and `last` reads,
HPIR emits only a data pointer and remaining count, keeping the owner live.
This is the borrowed view model of [Rust slice windows](https://doc.rust-lang.org/core/primitive.slice.html#method.windows),
with a bounded local [escape check](https://research.ibm.com/publications/escape-analysis-for-java)
to omit the window object entirely. Short input never pulls a view; the
first descending comparison stops the traversal. The natural Number
comparator shares the sorted kernel's order for NaN and signed zero.

Materialization finishes traversal before publishing the snapshot. Its
result has no deferred cursor invocation, but any actual pending item
keeps its identity. The producer graph carries only that item lane
through materialization or a pattern prefix. It does not infer Future
arithmetic from a callback that had to wait before returning ready items.
LLVM can still remove a pending producer after the original evaluation
was protected as a lift. When every surviving operand has a selected raw
physical carrier, HPIR emits the corresponding primitive directly. This
case does not re-run the source readiness flow.

The ready-number Range lane uses a single immutable physical sequence
plan containing first/limit/step and a skip/fence window. The
[producer selection](../../src/Msync.Hir/Hpir/LlvmHpirLowerer.Range.cs)
recognizes direct sequence producers and follows the ready plan through
fixed guest returns and window adapters; an item type alone is not a
producer identity. Drop and take compose the same window without a
runtime plan tree or callback/type reconstruction. A traversal copies
its plan into fresh frame cursor bytes. Pulls are compiled primitive
comparisons, arithmetic, and branches on those frame offsets, not
guest-to-host calls. A cursor has no host close operation and may be
reopened independently after break.

Skip and fence are separate: dropping beyond a preceding take still
pulls up to that take's fence, while take(0) pulls nothing. The cursor
advances by repeated IEEE binary64 addition rather than replacing
several steps with multiplication; a stalled sequence fails only upon
the next demanded advance. Count admission keeps the language's
nonnegative integral Number bound below 2^63, not an Int32 limit.
The source-to-artifact Range control reads actual yield/exhaustion
sites from instruction telemetry, checking descending, very wide,
zero/one-demand, stalled-prefix and break/reopen cases through physical
interpreted and x64 machine execution.

A sequence with pending Number bounds retains exact
`Future<Number>` edges and separate ready payloads in its own plan layout.
Construction does not wait; the first open resolves the needed values,
then drops the retained edges. This also works across a fixed guest
wrapper such as `iota`. A mixed call whose actual bounds are all ready
still validates at construction. Window adapters consume their source
descriptor and count at the adapter call, not at a new generic
per-item demand; they preserve item readiness independently of either
outer dependency. Once opened, all these numeric plans run the same
primitive cursor.

### Typed lazy transformation

A selected numeric transform preserves the guest callback entry and
the evaluated environment. Its concrete plan holds a numeric seed and
one environment pointer, not a callable descriptor or a generic
stream node. Constructing the transform may resolve a pending input
sequence descriptor; it never pulls a Number or invokes the callback.
The callback argument inherits the input's **item** readiness, not the
readiness of that descriptor. Effectful callbacks run at successful
pulls through ordinary guest calls. Reopening copies the seed into a
fresh frame cursor; it does not reuse work from a previous traversal.

Take limits the number of successful pulls and does not invoke the
callback for a zero count. Front pulls one item or reports an empty
Range at the requesting source site. A window that discards transformed
items cannot use the input's numeric skip field: it must still execute
any callbacks required to produce the discarded items.
The separation mirrors the demand boundary in Rust's
[`Map`](https://doc.rust-lang.org/src/core/iter/adapters/map.rs.html) and
[`Take`](https://doc.rust-lang.org/src/core/iter/adapters/take.rs.html).
We use the [stream-fusion](https://www.cs.tufts.edu/~nr/cs257/archive/duncan-coutts/stream-fusion.pdf)
idea of lowering a typed step and its control directly, not the assumption
that an effectful guest step can be duplicated, moved, or removed.
The synchronous front lane is limited to callbacks that do not suspend
on guest host work. A callback can return a ready Number after internally
demanding a host result: that still requires separate stream delivery.
The release's gated-process probe prints the following guest statement
before the callback can finish. Replacing that reduction with a blocking
call silently changes behavior, even though its final Number matches.

A map can also pull from a concrete Number snapshot. Its plan keeps
the snapshot owner, evaluated environment, and demand cap. Opening
adapts the selected snapshot layout once; the frame advances a physical
data pointer and a remaining count. A zero cap never reads a backing
item or invokes a callback. The ordinary source-defined `fold`
receives this recipe through its fixed Range argument, so its existing
loop and reducer call run without a separate folding service.

Materialization runs that same compiled pull/call loop into a concrete
vector. Its header retains a homogeneous backing block of scalar
payloads or guest record pointers. The allocator only grows and copies
the backing block; it never runs a callback or resolves an item.
Retained records keep their identity, and capacity is driven by observed
items rather than the requested limit. Active callback and growth
safepoints retain the partially built vector across collection. The
profile records the callback's actual guest call sites, buffer growth
counts and copied items.

A live Text-to-Number transform and its optional Number predicate share
the same producer-owned host line cursor. Each admitted source line
passes through the compiled map and, if present, predicate. A rejection
continues the current pull; it does not debit the accepted-item quota.
Zero remaining demand leaves the next source line unread. Take(0) does
not open the underlying cursor. The real cursor closes at exhaustion or
break, or during failure unwind; borrowed standard input is not disposed.
The portable plan describes capture edges and the admitted callbacks but
does not hold host cursor IDs.

### Counted ranges and snapshot windows

`empty_of(T)` writes a zero-length snapshot with the evaluated Type
witness. A repeated value and its physical count occupy one plan;
the count is admitted as a nonnegative integral Number below 2^63. A
repeated address denotes the same object in each item, not a copy of
its contents. For `enumerate_from` over a repeated source, the producer
hands HPIR the actual `{index, value}` record layout. The frame holds
the integer count separately from the binary64 index. The first
demand publishes a real record; the next demand fails if the
binary64 increment stalled. Breaking after the last successful item
does not force that failure.

Exact plan facts survive direct closed argument/return edges, and a
merge retains them when the live representations agree. This lets the
ordinary source implementation of `generate` compose a repeated
Range, an indexed Range and a selected callback through its fixed
wrappers. Materialization uses the same physical callback loop as the
numeric transform; it is not a special lowering for `generate`.

A window over an existing snapshot validates its count and produces a
counted view without revisiting elements. Its data pointer can be an
interior borrow of the original backing allocation; only collection
resolves that owner. An empty view keeps no backing pointer. Materialize
of an already frozen snapshot reuses its representation.

A selected enumeration can instead retain a closed Number, String, or
Scope snapshot. Its plan holds the exact snapshot owner. A frame opens
that snapshot's known layout once, advances its word pointer, and
publishes the same concrete `{index, value}` record on demand. Scope
items still alias the original objects; the plan does not copy them.

A declared Range field can also retain an always-empty producer fact.
The source lowerer intersects the actual writes to each physical field
before LLVM combines their loads or erases member offsets. It selects
the empty-result specialization of front, size, or empty only if all
applicable producers agree.
Member access and its failures still occur before the reduction. A
nonempty or opaque write prevents this specialization.
An all-empty merge never opens a backing-data cursor, even when
incoming empty snapshots have different physical header shapes.

### Concatenated snapshots

`ranges.concat` of producer-known word snapshots uses a two-segment
plan. The operands still evaluate in source order. A statically empty
side lets the result reuse the other snapshot; otherwise construction
retains both exact owners without visiting the members. Each frame
traverses the two segments in order, and size or indexing can use the
segment lengths directly.

Only an explicit materialization packs these segments. It allocates
one exact backing block and copies at most two contiguous runs of
words. Number lanes are untraced; Text and Scope lanes retain reference
edges, so copied Scope members still alias the original records.
Snapshot windows can hold interior backing pointers, resolved only
during collection. A mutable field or an opaque caller does not become
a known snapshot just because its language type is Range.

### Persistent Text chunk snapshots

The default StringBuilder policy appends to an immutable view with the
ordinary snapshot header: length, data, and an additional exact owner
edge. The backing buffer owns typed String references. Its published
tail may grow beyond earlier views' fixed lengths; appending from a
past view forks the visible prefix rather than changing history. This
is full persistence in the sense of [Driscoll et al.](https://www.cs.cmu.edu/~sleator/papers/making-data-structures-persistent.pdf),
implemented with contiguous snapshots and geometric growth for the
usual single append chain. The ordinary chain copies a linear total
of reference words; branching from an old view copies that prefix.
Earlier views can retain later entries in the same capacity, which
is bounded in reference count but not by the size of those Strings.

The semantic producer carries the common snapshot layout through a
closed policy call and its actual field writes. A custom policy still
executes its callback and any explicit materialization; it is not
replaced by the default append. Normalized String join, indexing and
cursor consumers do not dispatch on which producer made the vector.

### Pair cursors and Text packing

[Typed zip](../../src/Msync.Hir/Hpir/LlvmHpirLowerer.Zip.cs)
keeps the two original source plans and a demand cap. The producer
supplies the actual `{first, second}` row layout before LLVM erases
it. The frame holds two independent word cursors, each of which may
execute a selected guest Number map. It first pulls the left source,
then the right only if the left yielded. If the right is exhausted
the last left pull has still happened. A zero cap bypasses both;
the row is allocated only when both have yielded. Record fields retain
their original reference identity.

[`string.join`](../../src/Msync.Hir/Hpir/LlvmHpirLowerer.StringJoin.cs)
selects either a known ready String vector or the compiled traversal
of a String-producing map. The latter calls each guest callback once
and retains its returned Text pointers in a rooted typed snapshot.
The Text packing import reads only its data, length, and separator:
it counts UTF-16 units, reserves one exact result, then copies the
items and separators without calling back into the guest. Empty and
singleton packing does not scan the payload. The profile counts the
actual packed items and code units separately from guest callback work.

[Sorted combinations](../../src/Msync.Hir/Hpir/LlvmHpirLowerer.SortedRange.cs)
are selected from closed, homogeneous sources. The source instantiation
key distinguishes the actual combination, source plan and selected
callback signatures through ordinary direct arguments. Calls with the
same recipe reuse one body; different evaluated values, vector lengths
and environment addresses do not create bodies. A genuinely mixed
producer does not gain a known recipe by joining. The recipe is not a
language type, wire tag, or binder input.

The sorted cursor holds two heads with separate present/refill bits.
It opens physical sources once and refills a consumed head only at
the next demand. Merge tests `right < left` and otherwise yields
the left, keeping the original reference identity on equality. Set
variants choose the output for each comparison while advancing the
same head state. Native Number ordering includes NaNs before other
numbers; an explicit comparator is a separate compiled guest call.
Comparison results can affect traversal readiness, but are never
item payloads. Known ready states use the guest frame alone, with no
host iteration or per-item tags.

### File byte demand

An explicit byte write or append over a concrete ready Number snapshot
or a selected Number map over that snapshot compiles a consuming loop
at its FileOut site. The destination lease is admitted first, and the
existing host writer opens the output or replacement stage before
requesting its first bytes. No callback runs at plan construction, on a
failed destination open, or in a dry run.

The loop converts an item to a byte only if it is an integer in
`0..255`, then places it in a reusable 4 KiB frame buffer. It hands off
chunks of bytes rather than making one call per item; the host never
receives a guest pointer. The byte-only rendezvous preserves the
[existing Stream read size and publication boundary](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/Stream.cs):
an incomplete host read cannot flush a prefix on malformed input, and
replacements publish only after completion. On unwind the owner
releases the outstanding read with cancellation, never EOF, so a failed
source cannot publish a partial replacement. Cleanup waits for the
writer to remove its stage and release the original lease.

As in [stream fusion](https://doi.org/10.1145/1291151.1291199), the
producer's selected step stays visible to the compiled consumer. Here
the boundary is the host's actual read request, not a second generic
Range or an eager intermediate snapshot. The profile distinguishes
selected snapshot sites, compiled map traversals, chunks, delivered
bytes, and staged publication or abandonment.

### Standard Text and formatting

Standard input uses the same borrowed reader for line, line-range,
and read-to-end requests. A live Text window stores the source plan
and a count; a zero count never opens the reader. A positive window
stops before requesting another line and closes only its cursor, not
the borrowed standard stream. An explicit flush calls its selected
writer even with no payload.

A source control edge that already observed a line completion can
select String operands directly from its published Text reference.
This is not permission to demand an unresolved pure Text operation
early. A known Scope field can carry its own Text completion pointer
across an ordinary return. The field uses the actual reference leaf
of its layout, including the pointer leaf of a nullable carrier.
Frame cleanup therefore discovers the input job through the returned
record; it does not promote every pending read by default.

A pure `string.concat` with a genuinely unresolved operand creates a
typed Text completion node referencing its two operands. It does not
demand either on construction. Guest observation computes and caches
Text from the retained operands. An actual transfer instead projects
the expression to owned host Text fragments and typed dependencies;
the worker observes them in source order without receiving guest
addresses. Thus the guest can publish a later gate even while a
transfer is waiting for process output.

Positional formatting consumes operands at the format call. The
import carries a fixed typed schema for the physical operands and
uses the shared brace grammar and scalar formatter. Literal named
formats name the actual projected fields; a pending field is observed
only as its placeholder is reached. Unselected fields, deferred process
descriptions, and Range descriptions do not demand their payloads or
iterate a Range. A runtime template retains its source and Unicode
character diagnostics.

## Physical selection and diagnostics

HPIR consumes the optimized LLVM-derived graph, not a portable slot
instruction stream recovered by the binder. It lowers scalar values
directly to primitive operations, shares materialization bytes only
where intrablock def-use lifetimes do not overlap, and places actual
addressable records at fixed offsets. A composite argument passes its
physical leaves; a composite result writes to an explicit caller-owned
address. This lets a non-leaf return carry an escaping environment
without turning it into a host descriptor. PHI inputs become edge
copies selected once, with a scratch location only for real cycles.

Compile traces attribute frame extent and temporary reuse, concrete
memory and import selection, call edges, PHI cycles and safepoint root
work to the actual function. Bind traces attribute frozen and residual
observations to their source operation, with effect counts and concrete
failures. Runtime profiles distinguish fixed guest calls, stack
reservation/growth, bound imports, text linking and GC work; function
and source identities survive the image. Disabled recording requires
no per-frame recorder or managed frame.

The first contextual effect supported on this path is a terminal
literal transfer from the entry, with no unmodeled predecessors.
A successfully observed selection freezes its absolute members, even
when empty; failure to observe remains residual. Nothing publishes
before submission, and data bytes are live even when membership is
frozen. Later phases must extend the effect representation rather than
speculatively running the entire guest program at bind time.

## Producer obligations and review

- Preserve source evaluation order, observable location and pending
  identities, failures, cancellation, and resource lifetimes before
  lowering to offsets. A later HPIR lowering cannot undo EarlyCSE
  merging two future-producing operations presented as pure `fadd`.
- Preserve alias, closure and Range contracts through LLVM optimization.
  Use compile-time facts to select concrete operations once. Analyze
  genuinely new effects or representation choices at their owner; do
  not run a whole-function safety proof at every call or field access.
- Keep one executable instruction representation. Layouts and relocation,
  root/unwind and source-location metadata support it without changing
  the computation. Keep proof or verification work at the producer or
  artifact boundary; segment/heap capacity and actual dynamic source
  cases are the remaining runtime conditions.
- Judge the design against the existing closure/call, alias, future,
  iterator-disposal, deep/tail-call and detached I/O controls. For
  performance, count frame reservations, segment growth, frame wrappers,
  argument copies, guest/host transitions, heap descriptor publication,
  collection work and actual allocations, with no telemetry cost
  required in a disabled path. No HPIR runtime or speedup is claimed
  by this document.

Related: [XIR evidence audit](executable-machine-ir.md) and
[representation audit](xir-representation-audit.md).
