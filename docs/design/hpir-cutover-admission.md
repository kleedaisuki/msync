# HPIR source-cutover admission: unchanged CLI journeys

2026-11-22 · Linux x64, .NET SDK 10.0.403, optimization level 2,
development HPIR wire 7. This is a small cross-section of real CLI
journeys and an inventory of the **first actionable boundary** of each
failure. It is not a feature-completeness score or a benchmark.
The ordered-container, contextual-path and regex-specific gates are
separate; this matrix is intended to find what those gates miss.

## Setup and result

The 24 inputs come from the existing
[language acceptance](../../tests/Acceptance/language_journeys.py) and
[end-to-end](../../tests/Msync.EndToEnd) journeys. Their source text,
input files and exact expected artifacts or stdout were unchanged;
the two existing process fixtures retain their normal substitutions
for executable, script and version. They run from their fixture cwd
when an actual subprocess needs that cwd.

A small same-version producer uses the exact Compiler/Core/Hir/
Optimization assemblies deployed beside the frozen managed product
CLI. For the HIR lane, it writes HIR format 4, deletes the source and
lowers the decoded typed HIR. In the source lane, it analyzes the
original source directly into HPIR. All cases then use public
`msync bind-ir` and artifact `run` with fresh wire-7 objects; selected
cases also run the detached object directly. Compile and bind may not
create any expected output. For the bound run there is no source,
portable object or HIR in its binding directory; it normally launches
from an unrelated cwd. The public source producer is still the old
path and is used as a same-CLI control, not as HPIR.

| Frozen snapshot result | Cases |
| --- | ---: |
| HPIR satisfies original independent oracle | 11 |
| Public source control passes; HPIR reports a first failing frontier | 11 |
| Published and current legacy source disagree with the original oracle | 2 |

Exact passes include generic concept constraints through detached HIR,
live finite callable selection, inferred recursion, nullable match,
escaped locations, same-layout static alias versus distinct fresh
objects, differing field ordinals, one-time named Scope publication,
and choosing a callable before its argument evaluation mutates that
member (`AB` through both portable and bound execution).

## First HPIR-only frontiers on that snapshot

These all fail at target selection, not serialization or execution.
The owner column is a workstream, not a proposed generic fallback.

| Original journey | First concrete boundary | Candidate fix owner and invariant |
| --- | --- | --- |
| Exhaustive `Path or String` type match | `llvm.assume(i1)` reaches HPIR call selection | Selector: no runtime code for the LLVM fact. Keep the selected branch and each producer/effect instruction. |
| Scope member union; same union in a Range of Scopes (detached HIR) | `FileOut` for projected Path-or-String source | Union/transfer selector: carry the selected physical arm into typed Path-copy or String-write semantics, not eager probing of both. |
| Continue inside nested Range loops and `acquire` | `msync.operation.barrier` | Work-frontier runtime/selector: settle the lexical frontier at the actual control exit and preserve the continue target. |
| Unicode scalar directory order | `fs.directory_iterator(Path)` | Filesystem + typed cursor: range over real Path entries, sort with the existing scalar-order contract, and close on actual edges. |
| Lazy Path Range filter/callback | `ranges.filter(Pointer, function)` | Range producer and typed callbacks: specialize the concrete Path item ABI; preserve predicate demand and skip missing path. |
| Unevaluated signature/member reflection (detached HIR) | `ranges.all_of(Pointer, function)` | Range producer: consume the concrete Type vector without running the reflected argument/call. The first boundary does not establish a metadata defect. |
| Snapshot cursors, nullable item and search | `constructor.range(Null, Number)` | Range producer: distinguish live Null item from end of cursor in physical storage. |
| Original sorted algorithm suite | `ranges.merge(Pointer, Pointer)` | Range producer: preserve original key order, duplicates and callback semantics. |
| Affine fold over Scope fields derived from a real file | deferred Number at `msync.value.convert.1` | Readiness/carrier selector: preserve the typed deferred value across actual aggregate fields and demand only at the consuming operation. |
| Native and custom File/Text service policies | `Equal(Path, Path)` | Path physical ABI: compare the two direct path values under the existing equality semantics; do not replace policy calls or value identity. |

Two **separate baseline caveats** have their original oracles intact.
The public source and promoted Linux 0.9.3 both report uninitialized
storage at `format_fields("{pending}", output)`, before the selected
child stdout is published. HPIR currently stops earlier at the
unfinished deferred Text carrier. The standard-data family produces
unexpected Option/Result/variant file values under public source
and the published binary; the HPIR run also writes `missing` rather than
`default` to `result-default.txt`. These are not counted as HPIR-only
regressions and are not credited as passes.

## Selector follow-up: exhaustive type match

The optimized LLVM of the original fixture first executes the
existence query and Bool demand, builds the selected union arm, and
branches on its actual tag. The Path arm has a real Path `file.out`;
the String arm has a real text `file.out`. Only on the latter edge
does the optimized graph contain `llvm.assume`, fed by the known
complementary String tag. Per the
[LLVM Language Reference](https://llvm.org/docs/LangRef.html#llvm-assume-intrinsic),
that intrinsic informs optimization and emits no runtime code.
HPIR now omits the recognized intrinsic at selection, without
touching the operand-producing instructions or either FileOut.
The function selection metric `control.assumptions_omitted` reports
the actual omitted marker (one at O1–O3 and none at O0 in this fixture).

After that fix, the exact unchanged fixture passed through the public
bound artifact route in O0–O3 on both the physical interpreter and
forced x64 emission. Two input setups selected live Path bytes or
literal `fallback`. For the Path arm, rerunning the same executable
after editing the input copied the newly visible bytes. No compile,
bind, or unrelated-cwd output occurred. This closes one of the eleven
first frontiers above; it does not claim the remaining cases were
rerun against the later, concurrently changing workspace.

## Selector follow-up: projected Scope union transfer

`FileOut` now uses the physical `Path | String` tag once, selecting the
typed Path-copy or String-write admission directly. It does not normalize
the source to Path, probe it as a file, or format it as text before the
branch. When observed, both arms publish the actual receipt from the
selected job at the same result location. Optional-member narrowing
remains at the source's Null check rather than becoming a transfer rule.

The unchanged `scope_branch_member_union` and
`scope_range_member_projection` passed public portable and bound routes
at O0–O3 with the interpreter and forced x64. For the Range case, the
producer wrote HIR format 4, removed the source, and lowered the decoded
HIR to HPIR. Bound runs used an unrelated cwd after removing the
source, HIR, and portable artifact from the binding directory. Neither
compile nor bind created the expected output. In every cell a rerun of the
same image after editing the Path input copied the new bytes, while
the literal output was unchanged. The separate unchanged missing-input
control agrees with published Linux 0.9.3, and the maintained
`union-transfer-test` also checks a selected copy failure, the original
source diagnostic, the actual false receipt, and exactly one admission
for each executed transfer.

This closes both original Scope first frontiers in the table. The
separate cohort, oracle receipts and live-rerun evidence are retained in
`artifacts/validation/hpir-cutover-wire7/after-scope-union/`.

### Transfer destinations

The source contract accepts a String or Path on the destination side of
`->`. Normalize the destination once, at HIR-to-LLVM lowering before
`FileOut`: an existing Path retains its provenance and goes straight to the
transfer, while a String or a finite String/Path choice uses the existing
typed path coercion. Do not coerce the source, where String is a literal
payload and Path is a live file. This also applies to already sealed HIR;
no new wire field or runtime descriptor is needed. The
`transfer.destination_path_projections` compiler metric counts inserted
coercions.

The v0.9.5 release editor probe exposed this at unoptimized compilation
because its destination is an absolute String. The packaged CLI journey
also covers a relative String and a live String/Path choice: compile and
bind are inert, and detached execution from another directory writes
literal bytes or copies the live source under the bound directory.

## Selector follow-up: deferred Number from a Scope fold

The original `affine-scope-fields` case exercised two independent
boundaries. The optimized LLVM graph folded the zero-offset address of
`row::x` to the receiver, retaining the producer's declared layout on
the memory operand. HPIR now reads that declaration directly, selecting
the same split payload/completion layout for `x` as for `y`. Looking
only at the surviving pointer had yielded `0` instead of the correct
first fold, even though the second fold already returned `44`. This is
the distinction between pointer equality and access type described in
the [LLVM opaque pointer migration guide](https://llvm.org/docs/OpaquePointers.html#migration-instructions).

For a Number formatted only for a file transfer, HPIR reserves the
destination without awaiting the value. The actual worker request drives
the existing cooperative VM continuation; the VM resolves the original
exact Number cell, and the worker receives only the scalar. Inline values
and already-published cells format directly. There is no guest Text
completion for this single-consumer case. This follows the same ownership
split as a [libuv cross-thread wakeup](https://docs.libuv.org/en/v1.x/async.html):
the request crosses the boundary; the owning executor performs its
own state transition.

The unchanged original program passed public bind and detached execution
in O0–O3 on the physical interpreter and forced x64. Its two independent
outputs were `40` and `44`. Changing only the live seed from `7` to `2`
and rerunning each same image produced `20` and `24`. The maintained
gated control verifies that unrelated guest work and a collection
finish before the input is released, that frame and run joins settle the
effect, and that invalid input retains its original diagnostic and false
receipt. The exact replay cohort and artifacts are in
`artifacts/validation/hpir-cutover-wire7/after-affine/`.

The frozen report, fixture inputs/oracles, per-stage logs, and replay
driver are in `artifacts/validation/hpir-cutover-wire7/`. Run only the
affected original case when testing a fix, then repeat the compact
gate with one final codec cohort before switching the public producer.

## Work frontier follow-up: abrupt acquire exits

The original nested Range fixture now binds once the lexical work
operation is selected. The producer already places iterator cleanup and
acquire closes on the actual falling-through, `continue`, `break`, or
return edge; HPIR preserves those placements instead of rediscovering
region structure. The saved mark is a physical U32 instruction result
in the guest frame, not an abstract binding or a host scope object.

A lexical close publishes the applicable completion predecessor and
leaves pure guest calculation and standard output running. Later finite
file operations and process starts observe that predecessor. The work
owner uses existing admitted job identities, not a replay or a graph
of potential work: unfinished finite transfers are indexed once, and
process-output transfers are selected only when admitted by the closing
region's execution. A prior unrelated background process and its flow
are not joined. Bare `acquire` and `acquire process` have separate
physical service operations; the latter awaits only the selected process
and its connected flows. The standalone marker-only `release` stays
separate from these three operations.

The original unchanged public artifact gate now writes exactly `388`
to `total.txt` after source and object removal. Focused physical/forced
x64 checks at O0–O3 also schedule a gated process-output flow at a
`continue`, hold a subsequent verification process at that real
predecessor, and leave a different gated process/flow live. A second
case crosses two nested acquisitions at `break`. A straight-line
prepared transfer control verifies it still uses the shared prepared
frontier rather than declining to residual lowering. This is not a
global drain at a loop edge: unlike an OpenMP
[taskgroup](https://www.openmp.org/wp-content/uploads/OpenMP-API-Specification-6-0.pdf),
lexical close publishes dependency before the next relevant host effect
instead of suspending the guest on a general task set.

The paired SDK 10.0.403 Linux x64 Native AOT producer and production CLI
also passed the unchanged case at O0–O3 in both physical and forced
x64 execution (distinct portable and bound routes). Every bound
diagnostic recorded six marks and six publications. The focused gated
cases passed using this same assembly cohort. Binaries, references,
replay, disassembly, effects and raw trace are frozen at
`artifacts/validation/hpir-frontier-wire7/`.

## Typed Path equality follow-up

The existing language compares **effective spelling**, not Path
record identity or a resolved filesystem resource. Unqualified and
Local paths select non-null local spelling when available and otherwise
use Raw; non-local paths use Raw. Comparison is exact, with no
case-folding, canonical Unicode normalization, or path normalization.
Directory/home anchors and connection identity are not compared.
This matches both the released ready text fast path and its scalar
comparison. The Linux 0.9.3 release independently confirmed lexical
`./x` versus `x`, case and Unicode distinctions, same-spelling paths
captured across a real shell `/cd`, and same-raw paths with different
SSH/WSL endpoint descriptors without endpoint I/O.

HPIR selects only statically typed `Path == Path` and `Path != Path`
to one comparison import over the two physical records. It reads the
existing NativeText projection and compares UTF-16 spans, without
building host strings or reconsidering endpoint or binding context.
`paths.equality_calls` counts selected sites; `vm.native` retains the
actual `path.Equal` / `path.NotEqual` hits. An exact lexical source, plus
seven codec-roundtripped physical pairs distinguishing local spelling
(including empty spelling), Raw, anchors, and endpoints, agree in
both physical and x64 modes. The focused source journey also passes
O0–O3.

The original File/Text policy journey selects past Path equality.
Its next first boundary is `fs.rename(Path, Path)` in the native
`File::move_to` policy. Treat it as a real two-resource mutation
with ordered source/destination effects, not an equality, generic
value, or method-dispatch problem. The same public source still
passes its independent aliased-file and policy output oracle. The
matched CLI, original fixture, published discriminators and physical
probe are in `artifacts/validation/hpir-cutover-wire7/after-path-equality/`.

## Synchronous file rename follow-up

`fs.rename` now selects two physical Path arguments and no result
storage. Both runtime routes use the same typed filesystem kernel.
The source selects the endpoint, as in published 0.9.3: a local
source uses the destination's local spelling even when its descriptor
names a remote endpoint; a remote source uses that endpoint for an
unqualified destination and rejects an explicitly different endpoint.
Dry-run does not reach the mutation or fail that endpoint check.

The HPIR work owner reserves both actual write locations, including
their lexical/physical aliases, under one frontier lock and one
completion. It waits only for their already-admitted overlaps and
the explicit lexical predecessor. Prepared transfers retain their
shared resource frontier; the rename itself has no bind-time plan.
The guest observes completion (or a source-attributed failure)
before returning from the call. A released experiment confirmed the
otherwise counterintuitive local-source/remote-descriptor behavior and
the remote mismatch/dry-run result without accessing a remote host.

The focused artifact journey checks both prepared predecessor writes
and a prepared copy from the newly moved name. A second journey gates
two writes to the actual operands and a write to an unrelated path:
rename links exactly the first two and completes while the unrelated
writer is still parked. Both physical and x64 execution pass. Public
bound CLI playback also moves real bytes from a detached launch cwd
and leaves both files unchanged under dry-run.

The unchanged service-policy fixture then reached a **Text result
carrier** frontier: specialized `<lambda>.4` in
`stdlib/files.msync` returns a pending `file.in text` result from
`File::read_text` (`self::policy::read(self::path)`). This is an ABI
and ownership boundary, not a missing rename or filesystem kernel.
The matched reader, original replay, optimized LLVM and diagnostics
are in `artifacts/validation/hpir-cutover-wire7/after-rename/`.

## Typed Text result follow-up

An actual Text producer such as `file.in text` now keeps its completion
pointer across direct returns and the producer's finite callable targets.
One returned pointer is sufficient when the selected function always
returns a plain String or always returns a Text completion. Only an
actual merge of the two carries a separate physical Bool lane. Control
edges and finite call arms supply that lane; there is no eager wait or
object wrapping at the call boundary. A file transfer still admits the
effect using the original Text completion when that arm was selected.
The representation choice follows only concrete producers and closed
use edges, keeping the ordinary representation outside that slice;
[Leroy's account of unboxing trade-offs](https://www.cs.tufts.edu/comp/150FP/archive/xavier-leroy/type-based-unboxing.pdf)
is useful context for avoiding an unconditional representation adapter.

The return root transfers the live completion to the caller's work
owner before reclaiming the callee frame. The detached `file-text-test`
holds an upstream write until it observes admission of the downstream
transfer across the real `File::read_text` policy and a direct wrapper.
It checks exact bytes, the work promotion and dependency notifications,
and both arms of a genuinely varying Text result. Plain literal Text
does not need an object-header test.
The focused Linux x64 Native AOT reader
(`5ea6cfc6918d202306953fb6aaa0e21600f70965046018b18b51406eff6e8b46`)
passed physical and forced-machine playback, with native code publication
asserted in the latter run.

The unchanged full fixture now selects past this boundary at O0–O3
and next reports `ranges.concat` in the `eager_chunks` policy
(`<lambda>.25`, source offset 1655).

## Typed reflection follow-up: Type snapshot quantifiers

The unchanged `stdlib-reflection` source first emitted a real Type
snapshot for the reflected parameter list. HPIR consumes that selected
snapshot as a word pointer and a length, with a direct physical call to
the exact Boolean predicate. It does not execute the reflected operand
or the reflected function; the only called source function at this site
is the predicate. There is no generic Range import.

On the checked O2 detached-HIR snapshot the next first frontier moved
from `msync.builtin.ranges.all_of.2` to
`msync.builtin.string.join.4 (Pointer, Pointer) -> Pointer` in `<main>`,
the outer join over the mapped member records. This does not establish
support for the full reflection journey or every callback-body operator:
the original predicate body contains `meta.is_same`, but main selection
stops at the later outer join before visiting that body. At O0, a
mutable reflected signature field can still hide its concrete vector
behind a load; no additional memory or alias proof was added in this
quantifier selector.

A detached control actually runs the reflected Type parameter through
a ready predicate and checks that neither the described callable nor
a separate `members_of` operand creates its marker file. O0–O3 ordinary
and split Type controls verify short-circuit and exhaustion with nine
actual callback invocations, including no visits for empty snapshots.
Both controls use the same physical interpreter and x64 machine image.

The native producer and public wire-7 CLI also run the detached O2
reflection control as a portable object and bound executable. Both
physical and forced x64 write exactly `1`; each bound trace records
one guest predicate invocation. The frozen probe, unchanged HIR,
before/after selector diagnostics, physical control image and raw
traces are in `artifacts/validation/hpir-type-quantifier-wire7/`.

## Typed directory cursor and process controls

The unchanged Unicode scalar directory-order journey passes the public
Linux x64 Native AOT HPIR artifact path after removal of source and
portable input. The Path-producing directory cursor captures its concrete
parent record without enumerating, starts the OS traversal on first pull,
and retains the parent's anchors on each yielded Path. It reuses the
existing local/remote enumerator and filesystem ordering; only actual
Path records cross the guest ABI. A directory Range is not the binder's
frozen selection plan: repeated live cursor opens can observe different
members. The focused O0–O3 test creates the directory after binding and
exercises both normal exhaustion and an early close. See the
[directory evidence](../../artifacts/validation/hpir-directory-wire7/README.md).

Three unchanged stdlib controlled-process examples (`live-pipe`, `kill`,
and `terminate`) pass the same public detached route and the official
v0.9.3 oracle. The `live-pipe` child reports its real process ID and
original Unicode stdin, rather than an unobservable stub. The typed
process job separates startup, controls, and exit; an explicit wait
makes the following stdout projection a direct String. The separate
live-join follow-up verifies the unrelated-reader contract while a
real process-output transfer is still pending.
[Process receipts](../../artifacts/validation/hpir-process-wire7/README.md) ·
[Concurrent live join](../../artifacts/validation/hpir-live-join-20261122/).

## Integrated cutover recheck (18:47:34 CET, same-version managed wire 7)

A fresh managed CLI build from an isolated working-tree capture and a
producer using its byte-identical Compiler/Core/Hir/Optimization
references reran the same 24 original cases. The exact source, initial
input, independent oracles and route were unchanged for every case.
The capture includes the typed Text return implementation above. Both
captures during its preceding compilation window were discarded, not
classified as failed journeys.

| Result | Initial | Integrated |
| --- | ---: | ---: |
| HPIR matches the independent exact oracle | 11 | 19 |
| Public source passes; HPIR stops at a first frontier | 11 | 3 |
| Both have an unresolved baseline/oracle caveat | 2 | 2 |

No original pass was lost. The integrated build reproduces prior
independent exact passes for the exhaustive match, both Scope member
union projections, nested continue/acquire, the lazy Path Range
callback, Unicode directory order and the affine file-derived Scope
fold. It also now completes the original sorted algorithm fixture past
`ranges.merge`, including both its portable and bound runs.

| Remaining HPIR-only journey | Current first boundary | Concrete next decision |
| --- | --- | --- |
| Reflection (detached HIR) | `meta::is_same(type, Number)`, in the predicate over reflected parameter Types (source line 19). | Type selector: consume the two concrete Type values with the existing structural equality contract. The earlier `ranges.all_of` and outer `string.join` selection barriers have been crossed, not yet validated against the whole oracle. |
| Snapshot cursors | `at(self::items, self::position)` in stdlib `ranges.msync`, line 72, selecting a Number. | Range producer and closure/call ABI: retain the snapshot-vector contract across the Cursor record; LLVM loads the vector and Number index from actual fields before `ranges.at`. This is past the original live-Null-item construction failure. |
| Custom service policies | `$std::ranges::concat(chunks, [text])` in the original StringBuilder append callback, line 45. | Range producer and policy carrier: preserve two String snapshot inputs, the actual append-count mutation and the immediate materialization. The run selects past typed Path equality, rename and the pending Text-return boundary. |

The two baseline caveats are unchanged: format still fails at an
unfinished Text carrier on HPIR and at uninitialized storage on public
source; standard data still writes `missing` instead of the oracle's
`default`. Keep those cases visible, separate from the three HPIR-only
failures. This run exercises O2 and the existing adaptive route under
the managed build; it does not replace independent Native AOT platform
gates or switch public source compilation. See the
[matched binary cohort, run receipts and replay](../../artifacts/validation/hpir-cutover-wire7/cohort-1795369654/REPLAY.md).

### Complete optimized reflection journey

Allowing the exact Scope-pointer item ABI in the existing Text map/join
loop moved the original HIR's frontier from `string.join` to the
predicate's `meta.is_same`, and then to the member mapper's
`meta.type_name`. The former compares structurally interned Type IDs;
the latter loads its canonical producer-formatted spelling from the
existing linked Text bank. No new member object or generic value is
allocated, and no change to the wire-7 artifact record layout is needed.

The paired SDK 10.0.403 Linux x64 Native AOT producer and production
CLI pass the unchanged fixture at O1–O3 through detached HIR, the
portable object, and bound executable, on both the physical interpreter
and forced x64. The six independent output files match exactly, including
`file:Path/Path?:true|label:String/String:false`. No reflected call or
operand produces its forbidden file. Each bound run counts four actual
Type name lookups. The O0 exact source still exposes an unforwarded
signature field at `ranges.all_of`; this change does not infer
snapshot shape from arbitrary mutable-field loads.

The separate O0–O3 controls use a wider structural callback over one
known nominal Scope layout and check that a mutation after map
creation and two appearances of the same object produce
`6|7|5`, with exactly three callback invocations. That reads and
mutates the original objects, not reconstructed member records. A
second control drives the same bound image with different input and
gets `Range<Number?>` or `Path?` while checking structural Type equality.
Binaries, frozen assembly references, the original six independent
oracles, reproduction, raw traces and failure frontier are archived at
`artifacts/validation/hpir-member-reflection-wire7/`.

### Complete unchanged File/Text service policies

The eager StringBuilder policy receives its actual stored String
snapshot through a closed guest call. The producer follows the
literal–append–field cycle and publishes the snapshot geometry at
concat and join, with explicit materialization as the only packing
point. Default `append_chunk` shares the snapshot header but grows its
typed String buffer geometrically, or forks when appending to an older
view. The custom policy still sees exactly its original invocations,
and a previously built snapshot does not change when an alias appends.
The same producer mechanism supplies the stored argv snapshot to the
existing typed Process spawn import; it neither starts the process at
binding nor copies it into a generic value array.

The unchanged source passes public portable and detached bound execution
in O0–O3 on the physical interpreter and forced x64. All twelve exact
output files match, including `A🙂` before the alias append,
`A🙂β` afterwards, the independent default builder, and the real
published Linux 0.9.3 child's version match. The custom File policy
never opens its rejected path. The separate long append/fork control
checks real collection and linear copy work. See
[policy receipts and replay](../../artifacts/validation/hpir-cutover-wire7/after-policy-chunks/REPLAY.md).

### Complete snapshot Cursor journey

The by-value function entry now preserves the exact layout domain that
the specialization key already distinguishes. This removes the false
join of the Number and nullable Number `Cursor::items` branches. A
closed Number generating callback supplies its concrete item ABI;
only the existing `materialize` publication turns that fact into the
common snapshot header. Set snapshot generation still invokes the
actual factory three times, and cursor clones only share its owner.

The adjacent sorted check uses non-escaping two-item snapshot windows;
the original input performs two Number comparisons and allocates no
window objects. Focused controls preserve short-input behavior and
the natural ordering of NaN and signed zero.

The unchanged fixture passed O0–O3, portable and separately bound,
with both interpretation and emitted x64. Each execution was detached
from source and HIR and started from an unrelated working directory.
All 13 output files and empty stdout match the independent frozen
oracle. The same 16 combinations passed under a frozen SDK 10.0.403
Linux x64 Native AOT host. This verifies the selected HPIR route,
not a public source-entry switch. See the
[source, profile, output, and replay receipt](../../artifacts/validation/hpir-snapshot-cursors-wire7/README.md).

## Source-entry routing shadow and physical layout identity

A disposable CLI build explicitly selected HPIR for source and HIR,
with the regular public CLI binding, export and execution plumbing
and no legacy retry. On that frozen managed tool, the original
contextual suite passed all 30 source/portable/bound routes; the
original selected process/live-lines suite passed all 20 routes
including detached HIR; and sorted algorithms passed source and both
artifact stages plus their three static rejection cases. This is a
source-route experiment, not a change to the installed default. See
[reports and routing splice](../../artifacts/validation/hpir-source-shadow-wire7/README.md).

The broader HIR semantic journey in that cohort exposed a direct
producer contradiction: when a Scope `{value: String}` and a capture
cell used the same logical layout ID, LLVM lowered them to two
separately named structures. HPIR's reverse enumeration then threw
on a duplicate ID. The producer now emits and retains one
`msync.layout.<id>` structure per layout ID, and HPIR consumes that
map directly. The producer registers the name before field lowering
so recursive declarations retain their usual identity. This shares
geometry, not allocations: a Scope and its unrelated capture cell
still print `object` and `closure` independently, at O0–O3 in both
engines. The broad journey moved to the genuine Range producer
boundary rather than a duplicate-table failure.

The normalized snapshot representation uses one physical header
`[length, data]` for literals, materialized ranges and views. A literal
owns its inline buffer without creating a tracing edge to itself;
views retain the backing owner. An empty snapshot contributes no element
shape. A genuinely live overwrite still prevents snapshot selection.
For the controlled compiler-only prototype A/B on the same Native AOT
runtime, see the [replay and timings](../../artifacts/benchmarks/hpir-cli-ordered-containers/snapshot-header/REPLAY.md);
there was no systematic time difference, and the paired heap and
collection totals were identical.

The unchanged HIR semantic journey on the source-routing shadow then
passed all 12 source, portable and executable routes at O0–O3:
29 outputs per execution, no forbidden type-query effect, and five
negative diagnostics. [The frozen receipt](../../artifacts/validation/hpir-source-shadow-wire7/hir-semantic-o0-o3/report.json)
predates the final O0 canonicalization. O0 now promotes private locals
and runs ordinary dead-code elimination. Producer-marked representation
copies neither observe nor fail, so a discarded result does not create a
Future consumer at any level. An actual live effect stays in the graph.
See LLVM's separation of [local promotion and trivial dead-code elimination](https://llvm.org/doxygen/PromoteMemoryToRegister_8cpp_source.html).

The in-tree Native AOT CLI built with both physical entry properties
matched that matrix again after O0 canonicalization. See the [native
receipt](../../artifacts/validation/hpir-source-shadow-wire7/native-aot-source/hir-semantic.json)
and its executable hash. The adjacent broader-cohort status is a
checkpoint on that particular executable, not an expansion of the
release suite or an assertion that routes fixed by later patches had
already passed there.

## Live Connection and Path display follow-up

The unchanged Path-display journey now passes the source and physical
REPL Native AOT CLI on Linux x64, with both off and forced x64 detached
execution. Its five existing REPL lines distinguish SSH and WSL from
Local, and distinguish qualified presentation from native pathname
Text. `Cannot cast Path 'ssh://forge @ not-a-number' to Number` is exact
in REPL, source-free `.mso` and `.mexe`. JSON disassembly retains
`connection(Ssh, "forge")` and the `connection @ path` / `scope::member`
hover while retaining all HPIR physical graph fields. The test never
connects to that endpoint.

Additional controls mutate one Connection across real REPL
submissions, and select SSH versus WSL from different input behind
a Scope and guest call at O0–O3 on the physical and x64 paths.
The local contextual qualifiers document `$pwd`, `$file`, `$env`,
and `$path` with controlled local inputs. Original receipt, detached
artifact bytes, decoded JSON, replay driver, binary digest and
focused checks are under `artifacts/validation/hpir-connection-display-wire7/`.

## Qualified regex Path iteration follow-up

The original language-upgrade fixture formed its live selection on a
qualified local Path, after real guest work, but stopped at
`msync.iterator.open`. The Path Selection plan now selects the regex
mode of the existing Directory Open. The cursor consumes the same
typed, ordered host path stream used by the static selection binder,
without a generic Range or boxed Path item at the HPIR boundary.

First pull observes its actual resource frontier. A detached control
adds a member after binding and another through a preceding guest
transfer; both are observed and ordered. It also leaves a nonexistent
unused selector unopened, forces GC while the selected plan is still
live, then takes actual continue and break edges with one close.
Existing prepared selection transfers still retain the frozen member
list; the independent frozen/late-selection transfer gates passed.
The old development wire-7 Path Selection recipe must be recompiled
because its Text lanes are borrowed roots, not heap allocation starts.
Other wire layouts and import IDs are unchanged.

The original independent outputs and diagnostic checks pass for all
nine source/portable/bound executions at O0, O2, and O3, plus five
negative language cases. The unchanged test driver subsequently stops
on its explicit retired Native-v2 magic assertion, not a semantic or
selection failure. The receipt runs the original positive and negative
functions and independently checks the HPIR wire's current magic,
wrong-version diagnostics, and bound execution without the source
or portable object. See `artifacts/validation/hpir-selection-iterator-wire7/`.

A separate unchanged static-prefix discriminator documents the remaining
binding frontier. With no preceding guest effect, the released bound
iterator retained the original `old.txt` membership after `late.txt`
was added; the wire-7 residual HPIR iterator observed both. These are
different from the original qualified fixture, which has preceding
work and must observe its live frontier.

Wire 8 fixes that split with an optional producer-owned membership
recipe, independent of transfer recipes. Binding supplies only the
ordered lexical member list; the executor does not infer it from
pointer shape. The unchanged static discriminator now prints only
`./fixture/old.txt` in both engines with the source and portable object
removed. The original qualification journey still has no membership
recipe, passes all nine original positive routes and five diagnostic
cases, and runs alone from an unrelated directory. The existing
contextual and prepared/live-transfer gates pass at O0–O3. See the
[wire-8 replay](../../artifacts/validation/hpir-iterator-membership-wire8/README.md)
for staged output, decoded recipes and actual cursor traces.

## Typed Path transfer callbacks and acquired work (development wire 8)

The original scheduling fixture's first three mapper cases now
complete with the public Native AOT shadow in both execution modes.
A known `Path -> Path` mapper over a physical snapshot stays deferred
until a transfer consumes it. Capture metadata carries the exact
selected mapper through the real cell load; the callback observes
the current alias, not a copied closure value. A generated HPIR worker
iterates fixed 48-byte Path records and gives the callback the real
output address under its aggregate CALL ABI. When a callback releases
work and returns a Path, its precise aggregate return lanes are
published before frame settlement can collect them.

The transfer is one owned finite effect. Bare acquire parks the guest
on the submitting frontier as it existed before the effect admitted
itself, plus the child's own work. It resumes the same PC: no callback
replay or blanket join. A caller already waiting on the batch follows
newly admitted work by admission position, without waiting its
original snapshot a second time. The final I/O boundary receives a
typed Path cohort with the existing basename preflight, overlap and
transport semantics. It does not consume the mapper in dry run.
An unknown callback-produced path set retains the existing dynamic
file-dependency footprint; callbacks that themselves require arbitrary
file effects are outside this selected lane.

The independent [two-gate replay](../../tests/IO/hpir_path_batch.py)
distinguishes a true suspension from eager execution and a late
escaped Process from an incomplete join. The mechanism is analogous
to [Wasmtime's parked guest stack](https://docs.wasmtime.dev/c-api/async_8h.html):
control returns to the owner rather than a host Task executing guest
code. Unlike general continuation/effect systems such as
[WasmFX](https://arxiv.org/abs/2308.08347), this implementation exposes
no new language continuation; it parks only at actual selected awaits.
The [native receipts](../../artifacts/validation/hpir-path-batch-wire8/)
include all three unchanged cases and the separate detached probes.

## Released Process argv and the complete default scheduling gate

Released Process calls capture their executable and typed arguments
at actual submission. A ready argument follows the old fixed physical
lane; a pending String hands the process owner the existing owned Text
parts, not a guest pointer, re-readable location, or generic value.
Admission returns immediately. Only the native startup waits for the
selected text, so the submitting guest can publish the file that
unblocks the original background producer. Exact direct Text
parameters can forward one completion pointer; only a formal reached
by both ready and pending inputs has a separate readiness tag.
In the original `returned-process-submission` fixture the caller
changes its variable before releasing the producer. The newly
submitted child still sees the original captured output.

Bindings of After regions are now checked against the actual physical
plan: each Work mark provides a unique per-function frame operand,
and every publish names one of those same operands, including on an
abrupt edge. This is the same structural assertion as the older
symbol-paired Barrier test, not a restoration of the retired opcode.
The old-format test adapter still validates the published 0.9.3
control without changing product execution.

The ordinary HPIR-default Linux x64 Native AOT CLI passes **all 18
original scheduling cases** in source/interpreter and in forced x64
bound modes. Separately, the two exact argv cases are replayed with
source and object removed and from an unrelated cwd. Each of their
four instrumented runs delivers the original expected output and
records one genuinely suspended positional Text argument. See the
[checksummed native payload and focused replay](../../artifacts/validation/hpir-scheduling-default-wire8/).

## Service policy: retain Text readiness after the actual Process wait

The first wire-8 default build regressed the unchanged
`stdlib-service-policies` fixture at
`string.contains(result::output, "msync 0.9.3")`. Its actual
`Completed::new` implementation calls `process.wait`, reads stdout
and stderr from that same completed handle, and stores two Strings.
This is not a pending guest parameter or an operation requiring a
second language-level observation. Before LLVM promotes the local
handle, the field pass had treated every process projection as a
possible completion and chosen completion-capable fields even here.
The later selector correctly recognized the explicit wait for each
projection, but the field decision had already lost that fact.

The producer now selects the explicitly completed stdout/stderr
projection before field and scalar flow decisions. A private local
load may forward its single dominating stored handle; release,
mutable or opaque handles do not establish completion. The selection
survives LLVM through the physical call identity, so both fields
retain their direct ready String form. The original
`string.contains` uses its ordinary ready-Text operation; no new
runtime demand or general pending-String admission was added.

The same frozen original source reproduced the old diagnostic and
now passes detached O0–O3 in both execution modes, matching all 12
independent output files. A fresh Linux x64 Native AOT default build
also passes both detached O2 modes. The compiler records two known-completed
projections; runtime records one each of ready stdout and stderr and
one `string.contains`. See the
[before/after LLVM, fixed payload and unchanged oracle receipts](../../artifacts/validation/hpir-service-policy-ready-text-wire8/).

## Installed CLI: preserve actual alternatives and observation

The installed package journeys exposed two representation boundaries. Regex
guards on a Path/Null union now select the tag before projecting the Path
record. Mixed String/Path subjects select the corresponding typed service;
non-text alternatives yield false without invoking the regex engine. A
plain Path or String still emits a single typed regex operation.

An stdin null guard can also publish the exact Text completion later used
by a string comparison. The selected call carries that readiness into
physical lowering and reads the completed pointer directly. A Path cast
from still-pending process Text observes its source where the concrete
Path spelling becomes required. It uses the regular Path representation
after that point.

The public telemetry summary still reports actual copy/transfer outcomes,
with source and destination and failure cause. Diagnostic SQLite exposes
the typed file reader's causal link to the actual admitted writer. The
existing tests check that edge and its byte effects in place of Native2
queue, frame, and simulated resource counters.

## Release workflow replay

The versioned release workflow, not just the installed-platform smoke,
consumes `docs/releases/0.9.5.md` and now runs the existing production
HPIR CLI and native Windows NTFS journeys instead of Native2 frame,
bridge and iterator counters. Disassembly preserves its source-free
inspection contract with physical layout and call tables, the literal
pool, source references and bound context; it does not reintroduce
intermediate slots or the old codec.

The release suite surfaced absent carriers rather than new test
requirements. Static Type alternatives become a Range of witnesses in
HIR, and the same typed quantifier loop handles ready Bool, Number and
Type lanes. Numeric plans materialize via the shared word snapshot
collector. An iterator opened on a field uses the producer-selected
snapshot shape, including a completion lane. A lazy vector map of
pending Numbers forwards both lanes through the exact callback ABI and
into the iterator's destination. Advancing the iterator does not
demand the source or duplicate the callback's effects.

The remaining collection-policy case in the existing release matrix
uses a custom `RangeStorage.snapshot` closure while mutating a shared
List. Published 0.9.3 passes the same fixture. The first rejected
consumer is a join over that List's mapped items. The same error occurs
with just construction, storage and join; none of the mutators is needed
to reproduce it. Optimized LLVM keeps the fixed map callback. On the
producer side, `construction_range(initial: Range or Type)` packs and
projects the Range arm on the path from the literal snapshot into the
policy closure. Carrier propagation must follow that payload-preserving
edge even though the LLVM types on the two sides are different. The later
nested concat and callback sort still need their actual plan semantics:
a typed snapshot header after materialization does not justify reading an
unmaterialized concat or sort plan as a snapshot. Keep the policy call
count and comparator timing in the oracle rather than substituting a
plain List.

The subsequent managed replay of the original collection-policy fixture
passes all three staged routes, 39 byte observations, and the three
negative diagnostics. Nested concats retain a shape of typed segments,
read each header without bouncing through an intermediate vector, and
pack once on materialization. The iterator walks one frame-resident
segment table. Sorting carries the selected comparator in a lazy plan;
the observation uses the bottom-up stable merge schedule. A detached
seven-item discriminator reproduced the published comparator count and
output in both HPIR execution modes, including zero calls before
materialization and unchanged original membership.

EqualitySet follows the typed predicate through its source reject wrapper.
Map's empty transforms publish the shared empty header without a
callback. For populated values, the two zip inputs carry their own
snapshot geometry. A fresh literal appended to a nullable Number
snapshot selects the union representation before allocation; a
snapshot that already has other consumers is not rewritten. Bulk
packing uses the selected element layout, including aggregate extent
and pointer lanes. The full run retains the original List policy count,
case-folded equality, table snapshot, nullable Map, Stack and Queue
oracles.
