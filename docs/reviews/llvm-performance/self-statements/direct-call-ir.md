# First-person account: early direct HIR-to-LLVM producer

## Basis and ownership limits

I am the worker named `direct_call_ir`. This account uses the assignments and
corrections retained in my context, my implementation handoffs, and the current
review/source files. It reports engineering decisions and their consequences,
not a transcript of private deliberation. The current files have since received
other contributors' changes; a surviving name or file is not an authorship map.
I cannot reconstruct every removed prototype edit or every intervening integration
change from the context that remains.

My relevant delivered ownership was the initial direct HIR-to-LLVM producer and
its ordinary scalar/control/location paths; Loop/Break/For production; the early
source-function and location-closure producer; singleton scope globals; and
physical member-layout selection. I also supplied the early Function descriptor
construction and static-source-call environment extraction in
`LlvmHirLowerer.Calls.cs`, with native builtin Function construction in
`LlvmHirLowerer.Memory.cs`. I did not implement the later target Function leaf
SSA, projection/composition runtime, canonical leaf-carrier selection, machine
register analysis, or widened byte-initialization bookkeeping reviewed here.

Peers owned the LLVM graph foundation and target adapter, the three extension
instruction classes, operand-bundle implementation, representation conversions,
builtin/Range production, runtime value operations, persistent sessions, and
later ABI annotation repairs. The root assigned and integrated those interfaces,
selected the production route and pipeline, serialized builds, and ran the real
execution and timing journeys. I subsequently ported other LLVM source-analysis
and cast families; that does not make me the author of all optimizer work in the
measured candidate.

## What I built, and why

The producer assignment was to construct an actual LLVM-derived graph directly
from HIR, initially without making it the primary caller. It was not permission
to add an LLVM machine-code backend, a second interpreter, an opaque HIR evaluator,
or an old-lowerer fallback. The only project instruction extensions were
HeapAlloc, FileIn and FileOut. I initially delivered partial coverage and then
extended it through concrete source failures rather than claiming the migration
was already complete.

For Functions, the eventual producer representation was a struct containing a
code pointer, environment pointer and native-name pointer. The target adapter
maps this to the established Native Function payload; I did not change that
external runtime payload into LLVM pointers. Native functions and source
functions therefore shared the graph representation without making a native
name into a source body. A generic template retained its own identity declaration
rather than borrowing one specialization's identity.

A closure's environment stores the addresses of the actual captured cells. A
captured source local gets its ordinary heap cell; captured For items get fresh
cells per iteration. There is no value-capture snapshot or per-call clone of a
source location. The producer constructs the environment with HeapAlloc,
GEP/store and the Function aggregate with ordinary InsertValue instructions.

A known source call evaluates its descriptor and arguments first, loads its
capture addresses from the descriptor's environment, and calls the actual LLVM
function with capture-pointer parameters preceding user parameters. The original
evaluated descriptor is also an operand of the standard `msync.callee` bundle,
so the existing Native call can retain the actual Function identity/environment
without reconstructing a closure at every call. Dynamic/session calls use their
explicit callable runtime binding. This interface was coordinated with the
root, bundle owner and target owner, not independently invented by me as a new
runtime ABI.

I annotated the known call's environment ExtractValue with the concrete capture
storage's logical type. That was an existing construction/ABI fact, not a
nonnull or alias proof. The review now identifies that annotation in the current
producer, followed by loss of its precision in later target canonical-leaf
selection. I can account for supplying the fact; I cannot claim authorship of the
later nullable leaf choice.

## Corrections I received and made

Several important constraints were corrections, not insights I should claim to
have established unaided:

- Before this producer task, I was assigned a Native DirectCall/ImportCapture
  prototype. The direction was explicitly rejected and both additions were
  removed; no replacement bespoke Native opcode was installed. Those removed
  drafts are not evidence about the measured current regression. I cannot
  reconstruct their complete implementation history from this retained context.
- The root rejected all-pointer type erasure and corrected the String/Path ABI:
  String is the existing managed string reference; Path retains its two bytes
  and five typed references. The source producer had to preserve that payload,
  not invent a byte-plus-length String or context-only Path.
- The root directed the Function code field away from a VM integer ID to a true
  LLVM function pointer, while retaining target mapping to the old payload. The
  root also required separate template identity and location-only captures.
- Early native Function construction used a temporary alloca and field stores.
  After InsertValue landed, the root requested its replacement with aggregate
  construction. Duplicate instruction-to-HIR source maps were also removed in
  favor of instruction Source attachments. These simplified the new graph;
  neither was proof of runtime performance parity.
- Parent-directed integration checks caught missing source restoration after
  recursive emission, non-dominating location allocas, empty-scope identity,
  and missing logical annotations on partial Function aggregates. I repaired
  the producer rather than adding runtime validation or legacy fallback.

Two real source failures are worth distinguishing. The captured-For journey
initially returned 0 rather than 321 on both old and new routes. The root located
the frontend loop write-event activation defect; my frontend repair activated
that existing flow mechanism at the loop boundary. It was not evidence that a
new closure-copy policy was needed. Separately, my physical-member production
initially treated semantic record field order as the native factory's physical
order. An enumerate index read the item instead, and generate's early-exit
journey returned 55 rather than 21. I corrected the canonical builtin-record ABI
mapping after the actual CLI failure. That was a concrete assumption I got wrong,
not a reason to add whole-object layout admission checks.

## Tradeoffs I did not follow through far enough

The explicit graph construction served the assigned semantics and ordinary LLVM
transform interface. I did not establish that expanding Function aggregates into
InsertValue/ExtractValue and capture loads would retain the mature target's
execution cost after later scalar replacement. Fewer graph or Native instructions
alone would not establish that. In particular, I did not trace the fate of the
precise environment carrier through later canonical Function leaves, nor the
live intervals created when Function components become independent target values.
Those later implementations were not mine, but this was still a missed
cross-boundary assessment of the producer interface I supplied.

The current review gives this a concrete form: source lowering already supplies
the exact environment layout, yet hot target projections become Null|Scope and
miss the exact-address read/write path. It also shows long-lived code/name leaves
amplifying HashSet liveness work in a planner with zero eligible consumers. The
right response is to preserve the supplied ABI facts and demand the existing
consumer condition before doing unused analysis, not to ask the producer for a
new alias/nonnull proof. I did not author those two target/planner policies and
do not know their authors' original rationale.

My early closure choice also made allocation and resource semantics explicit,
but I did not measure it against the dominant existing callback workloads as an
isolated decision. I should not now infer that environments or captured cells
are the measured problem merely because I authored them. The frame review found
no material new capture-lifetime or frame-ownership defect in these workloads;
its diagnostic allocation observations do not isolate producer allocation cost.

The root-run closure, alias, pending-value, session and artifact journeys settled
real behavior and exposed real defects. They did not settle performance. My
handoffs avoided claiming timing gains, but accurate status wording did not
replace the missing end-to-end cost assessment. That is a limitation of my
contribution, not an argument that semantic correctness was enough.

## Present conclusion

I own the early producer decisions and the concrete member-layout mistake above.
I do not attribute the final elapsed-time deltas to myself or another contributor
without the corresponding causal result. I also do not treat inherited target
leaf-SSA work as my own just because my Function producer predates it.

The existing review points to bounded work: retain exact producer environment
carriers through target extraction, avoid register analysis with no consumers,
and compare the same real source/artifact journeys after those changes. None
requires restoring removed opcodes, inventing another heap solver, or changing
capture identity. This statement changes documentation only; I ran no build,
benchmark, timing experiment or commit and edited no product code for it.

## Related records

- [Integrated performance review](../../../design/llvm-performance-regression-review.md)
- [Function projection review](../function-projection.md)
- [Frame and allocation review](../frames-gc.md)
- [Target adapter owner's account](aggregate-memory.md)
