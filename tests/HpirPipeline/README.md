# HPIR source-to-physical candidate

Run `dotnet run --project tests/HpirPipeline -c Release` from the repository
root. The runner exercises real source through the normal HIR and LLVM
optimizer, physical HPIR selection, portable encoding, binding, bound
encoding and the guest executor. The product CLI compiles to HPIR and
does not decode or execute older Native images.
Run `python3 tests/HpirPipeline/cli_journey.py --cli PATH`
with the built CLI (or its DLL and `--dotnet PATH`) to exercise
the product artifact entry points, editor navigation, dry run and detached
execution without source.

`global-test` checks that a named declaration initialized in a repeatedly
called guest function remains a singleton, that the image never shares the
state across runs, and that a child object reachable only from a global
survives collection in both physical execution modes. `path-test` uses
both arms of an actual nullable Path across a call and checks dynamic
transfers from the original bound directory.

`session-test` runs successive source submissions through the physical
session compiler and the same HPIR executor, using exact initialized-prefix
reconciliation and stable captured addresses. It checks text held past its
literal table, mutation through aliases and old closures, a callable target
change including a failed submission's actual prefix, persistent Type
witnesses, typed Path/Function and nested Scope results, collection, and
session reset.
It also checks that a known-condition arm still evaluates its side effect,
that native/guest alternatives keep their signatures and labels, and that
the full production-shell prelude's `pwd()` and `home()` observe a canonical
module cell lazily initialized in a later submission. This is an in-process
development journey, not a replacement for the installed REPL acceptance route.
On Linux it also runs a real gate-held process, retains stdout through a
failed submission's initialized prefix, checks the exact `io::write_text`
bytes, and resets with a real child still running.
The directory-change cohort verifies an old literal versus a new occurrence
with identical spelling and checks actual file destinations through both
explicit writes and transfers in older code.

`directory-authority-test` executes a source-free artifact with the
actual shell `cd`/`pwd`. It checks bound literal versus live
String-relative effects, newly admitted process CWD, the selected binding
frontier, and session state after a failed second change. Its cooperative
child is held until the parent changes directory, then resolves its own
relative `cd` under its original snapshot without modifying the parent.

The O0–O3 controls check ordinary non-leaf calls, parallel edge moves in
a loop swap, two genuine call entries, two concrete scope layouts, a
read before aliased mutation, literal-vector membership and field aliasing,
and closures/vector cursors surviving a real guest collection. Typed host checks exercise actual math, invariant Number
formatting, standard output/error, literal linking, and source-attributed
failures.

Use `string-test` for ready String through both wire stages at O0–O3.
The control separates Unicode scalar count, UTF-8 bytes, ordinal ordering
and non-normalized equality, and checks supplementary and expanding/contextual
case mappings under a Turkish ambient culture. It asserts exact output,
source-owned invalid scalar indices, a live String through real guest
collections and the actual casing/immutable-result counters. Lazy String
Range operations and nullable search results are not included in this control.

Use `live-text-test` for typed `io.stdin_lines` and `io.read_lines`.
It gates a real input reader to separate construction from demand, checks
that break does not prefetch or dispose borrowed stdin, and distinguishes
blank lines from EOF. The file cases read live bytes from detached anchors,
close after failure, and order the first demand after a live writer to
the selected path. Cancellation closes the active cursor during unwind.

`text-observation-test` verifies that a dominating null-test demand on the
same input completion makes a subsequent Number parse ready, without
infecting its caller with a Future ABI. It covers a representation copy,
EOF, parse-error order and source attribution, a branch that skips
the observation, and new input both after and inside a loop.
`process-source-test` gates one real released child to
distinguish promotion from waiting, private frame joins, independent
stdout/stderr payloads and reclamation after the last live projection.
Both controls exercise O0–O3 with the interpreter and x64 machine path
where supported.

`transfer-receipt-test` replays the original contextual result observer
through both artifact stages and physical engines. The gated control
returns a pending receipt across a guest frame while a live process
still holds the actual copy. The caller admits the later writer before
observing the receipt. A failed first copy writes `false`, preserves
the original diagnostic, and does not stop the later writer.

`union-transfer-test` reads an optional member from a runtime-selected
Scope, narrows Null out, and transfers its `Path | String` payload.
Both typed arms publish the real receipt from the one selected transfer;
the missing Path case reports its original error and publishes `false`.
The absent member submits no transfer. The O0–O3 control round-trips the
portable and bound images and runs both physical engines where supported.

`number-effect-test` gates a released subprocess behind a real file.
A Number fold reads two fields of each Scope and admits the destination
before the subprocess supplies its seed. Guest allocations collect while
the effect owns the last exact root. Controls cover the inline zero arm,
root and nested-frame work joins, cancellation, and a failing parse whose
original diagnostic and false receipt are preserved in both engines.

`file-write-test` runs detached explicit replace and append between
prepared copies, checks byte-exact Unicode output and bound anchors
when the execution directory changes, and observes typed file content
and a transfer receipt. A live gated copy exercises the wait, source
ordering and cancellation. Further controls cover failure before
admission, fatal writer diagnostics, finite Path/String destinations,
symlink following and dry-run non-mutation.

`vector-access-test` exercises source-level `ranges.at`, `front`, `first`
and `size` on concrete vectors through the image boundary. `first`
reuses a once-evaluated wrapper, preserves a Null item, and reports its
own error for a statically empty snapshot and an exhausted split view.
Valid hot lookups
remain primitive, while the failure controls cover signed zero, NaN,
infinity, fractions and both sides of the binary64 2^63 ordinal boundary.
They check the same error text and source in both engines at O0–O3.

`vector-quantifier-test` checks concrete Type snapshots across detached
HIR/object/executable, interpreter and x64 at O0–O3. A captured counter
distinguishes the exact short-circuit prefix, an exhausted snapshot, and
empty inline/split snapshots. A separate O2 reflection control checks a
declared Number parameter and verifies no reflected body or operand effect.

`connection-display-test` varies only stdin after binding one image.
The runtime-selected SSH or WSL Connection is carried through a Scope
and guest call, then used for lexical Path construction. The native
String output stays endpoint-free while the invalid Number cast and
qualification trace name the actual endpoint, at O0–O3 on both engines.

`selection-iterator-test` checks a typed qualified regex Path cursor
using real directory entries. Members appearing after bind or from a
preceding guest transfer are observed together. It also checks that an
unopened missing directory is never enumerated, the retained plan
survives collection, and actual continue/break edges close it once.

`member-join-test` checks Text join over an actual Scope snapshot,
including a late mutation and repeated references to the same Scope.
A second detached route supplies two different runtime Type witnesses
to the same image, checking structural equality and the exact nullable
and nested display spellings on both engines at O0–O3.

`nullable-vector-test` builds `[null, 5]` as one concrete inline
snapshot, opens it twice at O0–O3, and distinguishes an item that is
Null from a nonempty snapshot's absent index in both engines. It also
collects between two successful pulls with the cursor still open.

`cursor-field-test` exercises the public standard-library `Cursor` through
both artifact stages at O0–O3. It checks independent clone positions,
nonempty nullable items, same-geometry reassignment through an alias, and
a split snapshot view. Literal and view writes use the same header; a
genuine live overwrite verifies that the producer declines a snapshot
assumption. The adjacent sorted check covers early exit, empty/singleton
ranges, NaN, signed zero, and a collection between borrowed-window reads.

`scalar-cast-test` checks ready Number/Bool/String/Path casts across both
artifact stages and supported engines at O0–O3. Controls cover signed
zero, NaN, infinities, a subnormal, immutable lowercase Bool spelling,
case and whitespace independent of the host culture, already-observed
input and source-owned errors before later effects.

On x64 the native check runs one bound signal image with both the HPIR
interpreter and the direct guest ABI, and checks output, guest census,
segment crossing, live captures through collection, and cancellation
inside a native callee after one real output effect.
Use `hpir-block-check` for just these controls. The
[paired benchmark](../../docs/benchmarks/hpir-machine-blocks.md) explains
the Native AOT runner, variant commands and what this test does not measure.

Use `promotion-test` for interpreted-to-machine handoff. One bound image
varies the guest work with input. The short run never emits; the long
x64 run resumes an active guest frame, preserves live singleton fields
and a pending child-owned read, and matches the control's exact output
and instruction/edge/call/import census. A separate run cancels at the
promotion boundary. The CLI journey also asserts the default cold/hot
decision with explicit diagnostics on a detached bound image.

Use `cooperative-test` for genuine typed scalar Range suspension. At
each optimization level and in both available entry modes, an independent
root output is observed before releasing the process or line gate. The
child returns a real Number, and the line case forces a collection
while the child stack is parked. Separate cases check a captured pending
Bool, an empty callback-free failure at demand, and cancel-on-discard.

Use `tail-test` for the original 100,000-step self-call workload.
Across O0–O3 and both available execution modes, the detached image
prints the exact sum, enters one sum frame, and takes 100,000 physical
backedges without growing the stack. Parallel String argument exchange,
mutable capture writes and a fresh escaped location keep their identity.
The negative controls preserve exact closure environments and visible
file effects with ordinary CALL/RET.

Use `frontier-test` for detached prepared transfers and the gated
process-output acquire on actual Range continue and nested break edges.
It runs O0–O3 on the interpreter and the x64 backend where available.


The transfer journey runs compile, bind and submit in separate processes.
It deletes the input source before binding, submits the prepared image
from another directory, mutates selected file bytes and adds another
candidate after binding. It covers successful empty observations,
residual observations, path/text transfer, and two actual transfers
sharing a resource frontier. An affected selection waits for its
preceding writer; an unrelated write does not unfreeze a selection. A
failed writer reports once and does not discard separately admitted copy
work. Use `effect-test` for this detached control. The fixtures stay
under the repository's `.temp/`.

For an emulated Native AOT runner without a host binfmt handler, set
`MSYNC_HPIR_TEST_LAUNCHER` to an emulator or launcher script that accepts the
test executable as its first argument. Detached child processes then use
the same emulator as their parent. For QEMU user mode, set the target
sysroot with `QEMU_LD_PREFIX`.

The `range-test` route exercises ready numeric iota/drop/take/for through
both wire stages and interpreter/machine execution. It checks producer
selection, physical cursor pulls, exact result and source-attributed
yield/exhaustion, including a zero-demand stalled sequence, the count
limit, and cursor reuse after break. On POSIX a gated child supplies an
actual pending Number bound or take count. The source constructs the
sequence before the child can exit, survives collection while its plan
holds the bound, and resolves it once across two opens. A mixed call
with an actual-ready invalid bound still reports its construction error
without observing a separate pending sequence.

The `transform-test` route exercises an effectful selected callback over
a ready numeric Range. It checks zero, two and one callback invocations
at actual take/materialize/front sites, fresh traversal after break,
and source-attributed failure on empty front. The record-stream control
feeds the guest through typed input, validates results against an
independent integer recurrence, and forces a real collection while
the materializer retains thousands of guest records. Both wire stages
and both execution modes are used where the machine backend is supported.
The live sensor control gives the guest a reader which fails if asked for
one line beyond the accepted quota. It checks both zero demand and a mix of
accepted/rejected rows, including the actual map/predicate call counts and
exactly one cursor open/close when input is demanded.
It also drives the real stdlib `fold` over a lazy snapshot map. A zero
window is sized without a pull; a subsequent dynamic window drives
exactly two callbacks attributed to the library's consumption site.

The `counted-range-test` route covers typed empty snapshots, repeated
scalar and aliased record values, fractional enumerator starts,
very wide counts without speculative iteration, and the real
`stdlib/ranges.msync` implementation of `generate` through fixed
guest wrappers. It verifies the stalled-index failure on the next
demand, not on a successful yield, and collects while a window
owns an interior pointer into a vector backing allocation.

`zip-test` exercises actual independent pair pulls (including the
asymmetric final pull when the right side ends first), an empty window,
guest String callbacks, and aliased Scope row fields. The same route
checks Text joins over direct snapshots and a guest map: empty/singleton
output, supplementary Unicode separators, exactly one traversal over
4,096 callback results, and the actual item/unit census across collection.

`sorted-range-test` compares natural Number merge and multiset union
across direct calls with different values and lengths. It checks natural
NaN ordering and the compiler census: one concrete consumer body for
each admitted recipe rather than one body per call site.

`concat-range-test` checks the known-empty shortcut, direct size and
indexing, UTF-16 Text and iteration over two retained Scope segments.
One segment owns an interior vector window; actual collection and
post-materialization mutation must preserve both aliases. The runtime
census counts only the two requested packs and five copied words.

`text-chunks-test` exercises the default StringBuilder through one real
builder and its alias, clearing after append, retaining an earlier chunk
view, and appending separately to that view. A 12,000-element live
append chain builds heap Strings through actual collection. The test
checks exact Unicode and digit output, one typed fork, and linear total
copy work in both physical engines at O0–O3.

`file-metadata-test` reads both Path and String filesystem queries from a
detached executable, observes bytes changed after binding, orders file
size after a live replacement of the same destination, and checks that
the uncontended operations complete without a job. It also observes a
missing-file query after a printed marker, with recording disabled and
the original source site and host exception retained.

`file-text-test` holds an actual replacing writer before publication until
the guest admits a separate transfer whose Text source is still pending.
The typed read returns through the actual File policy and a direct wrapper
without observing its payload. The detached reader checks positive work
promotion and dependencies, exact replacement bytes, and an unchanged former
symlink referent. It checks both plain/deferrable arms across same-function
control flow and finite captured-call dispatch. A missing source returned
through a guest function reports exactly once at its origin, with no output.

`file-byte-test` appends a lazy Number map after a real overlapping
transfer, verifies demand count and bound cwd, and checks a ready
8,231-byte snapshot across multiple VM chunks. A malformed replacement
after two full host requests preserves the old bytes and removes its stage.
The same compiled map does not run during a dry run.

`text-delivery-test` blocks input across a Scope return, observes the
real flush before replying, and checks the selected named Text field
in both engines at O0–O3. Its detached positional template is read at
execution; a malformed replacement reports the original source
and template character without creating the destination.

`live-text-join-test` delays the first stdin pull, checks the distinction
between blank lines and EOF, verifies one exhausted close or cancellation
unwind, and observes a missing-file error without recording enabled. The
unchanged standard library I/O journey additionally checks reopened file
contents and an independent join whose following stdin-close operation
must not be blocked by an already pending Process stdout transfer.

`shared-transfer-test` holds the original filesystem operations long enough
to expose both directions of a prepared/residual resource edge. An unrelated
metadata read remains ready, the bound call stays void with no arguments,
and a known transfer failure reports once while independent work completes.

`regex-test` checks ready String/Path matching, named replacements, escaping,
invariance under Turkish locale and exact malformed-pattern lineage at
source. A residual regex selection waits for an actual overlapping writer,
sees files created after binding, and transfers only matched members.

`regex-find-test` checks the standard library first-match result, including
named and unmatched groups, lookbehind across supplementary Unicode
characters, and an actual guest collection with the capture vector still live.

Use [HpirRuntime](../HpirRuntime/README.md) for isolated physical stack,
relocation, reclamation and cancellation mechanics. These controls do not
establish release coverage or performance against 0.9.3.
