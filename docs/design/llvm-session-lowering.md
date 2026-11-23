# Persistent LLVM-derived session lowering

## HPIR incremental target

The HPIR session uses the same staged semantic generations and stable root
identities. Each compiled submission has a new entry and code-entry table;
no previously executed top-level code is replayed. Physical global operands
relocate to the session's existing concrete cells or named declarations, and
the compiler appends new physical layouts without renumbering older layouts.
The logical callable identity in a persisted closure is separate from its
current-image entry index. Current finite session dispatch alternatives come
from the producer's session table and call the selected entry through the
ordinary HPIR ABI. Acceptance observes the stable identity physically stored
in each callable binding, including after failure. In the prefix probe
`action = fn { 5 }; fail(); action = fn { 8 }`, both a call in older code
and a fresh direct call see 5; a separately retained alias still calls 4.
This distinguishes observing the accepted physical binding from using the
unreached 8 in final semantic facts. The allocator's environment pointer
does not enter the compiler's retained observation.

The execution session owns one nonmoving heap and starts a fresh stack for
each submission. A declaration's physical initialization flag is published
after its complete payload store, so failed submissions can reconcile only
names they actually initialized. Existing mutations are retained. An image's
literal address table is temporary; its text payload and captured path
anchors live in the guest heap. They are exact roots while the image runs
and survive a later collection only through normal guest references.
Collection can also reclaim ephemeral literals between submissions.
Type witnesses keep an append-only session intern ID in the physical
payload. Each new generation starts with the earlier meanings; only
addressable immutable Type cells use a per-image relocation. Type
presentation indexes the producer's existing TypeRef rather than scanning
the old image or describing a guest pointer.
Ready Path and Function presentation use the same physical result-record
pointer as an aggregate guest return. Only the embedding caller supplies
the ephemeral record; an ordinary submission still owns its own guest
stack, and no enlarged value carrier is passed through guest calls.
Path observation retains its spelling, endpoint and captured local anchors.
Ready Scope presentation projects the actual heap record using field names
and offsets emitted once for each concrete physical layout. Those plans
stay with the stable layout IDs when the allocating body is absent from a
later image. This is prompt-only metadata: no field scan or value tagging
enters a guest load, store or call. Nullable fields read an existing union
tag; nested aliases are projected on each path, and a true recursive path
has a cycle marker. No guest address escapes into the host presentation.
For the producer-known standard-module field import, the relocation is a
one-pointer alias cell, not a copy of the field. The initializer publishes
the actual field address with an ordinary pointer store; other lexical
cells remain direct payload. Alias layout is fixed when the canonical
root is materialized, not when a particular module is first used. In the
full shell prelude, `pwd` captures the field for a still-dormant
`environment` module. A later generation must initialize the payload of
that same field even though its executable HIR does not contain the
original root allocation. The lowering registry carries the existing
alias declaration forward rather than inferring it from that later HIR.
Conversely, if a closure is published before its first public standard
module invocation, the producer emits the empty standard root skeleton
before retaining its capture. It does not run any module initializer
then. Both paths now name the same field before and after activation.
Session reset releases the owner, including its heap, not a copied value map.

There is no new artifact wire for this in-process contract. The current
focused lane covers completed scalar observations and ready heap/callable
state. Session dispatch carries each native alternative's selected argument
coercions and result type from semantic analysis. The physical descriptor
keeps a stable native token alongside its qualified label; the producer
branches to a typed native operation or the existing guest entry. This
needs neither runtime lookup by implementation name nor a generic adapter.
The full shell prelude, `pwd()` and `home()` use this lane, as does a
retained callable that alternates between `math.floor`, a guest Number
function and `math.ceil`. The String `trim` case also exercises a
branch-local Path-to-String coercion from its selected native signature.
The in-process native alternatives are not persisted in HIR artifacts.

Released Process work keeps its own session admission token and job IDs.
Successful submissions and failures still settle their transient work;
they do not join a retained process. Reset cancels and joins the work
owner before freeing its guest heap. The focused process fixture holds
real stdout behind an OS-side gate, reconciles the prefix of a failing
submission with both a new Text alias and a previously ready String cell
updated to the completion, then checks exact bytes through `io::write_text`.
Acceptance records the initialized storage and leaves its value undemanded.

Only the selected String cells that can receive this completion have a
mixed typed demand. A ready String pointer and a Text completion pointer
share the same physical address lane and exact guest roots. The observing
operation discriminates their two existing object layouts and takes the
ordinary completion demand only when needed. Ready-only String stays
unchanged. The gate allows the same mixed cell to become ready again with
an ordinary pointer store. This clears the stdout/Text part of the
[production session journey](../../tests/LlvmSession/README.md).

Pending closure captures/returns, FileRead and input publications, and
Number/Bool completions are not yet admitted across submissions.
Publishing the wrong physical geometry to an older capture is not a
substitute for a typed closure ABI. Session work ownership also still
needs pruning by the accepted/exported roots and their closure reachability
rather than retaining all historical global cells until reset.

The compiler carries each Path literal's unique source ID and offset into
the in-process global relocations. The first linked image captures its directory
and home in an immutable concrete Path record; later code reads the same
record, even after the host equivalent of `/cd`. A new occurrence with
identical spelling captures its own context. The host directory change uses
the existing local directory-search policy without mutating process CWD.
The session can still prepare transfers made from new-source literals.
If an older body exposes a retained Path during optimization, the effect
is residual and gets the same captured Path as ordinary evaluation.
Metadata checks are likewise residual in the session.

In-language `cd` and the host `/cd` equivalent update one directory authority;
neither touches the process-wide working directory. This is the same
session/process distinction used by
[PowerShell runspaces](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_locations).
The selected Path
operation normalizes its concrete Path or String input under the shared
Native local directory-search policy. Dynamic String-to-Path, working
directory and process/effect admission take the current authority snapshot.
Bound and already materialized Path records retain their original anchors,
including literals first used after the change. The prepared effect pass
stops at an actual directory mutation; a discarded source arm does not
prevent ordinary literal preparation.

A cooperative scalar child gets a private directory snapshot when it
launches. A change in the parent does not retarget an already parked child,
and a change made by that child does not update the parent. Their earlier
Path values can still borrow the owner-managed immutable anchor bytes.
Session anchors use the existing heap Text layout so that a Path displayed
as String can also become the ready alternative of an established mixed
Text/Completion cell; standalone anchors belong to the executing image's
directory owner.
The `directory-authority-test` cohort compares a detached artifact with
Native, invokes the actual shell prelude, checks successful prefix state
after a failed change, and exercises the parked-child distinction. This
does not add admission for otherwise unsupported retained Range callbacks.
The Linux x64 Native AOT gate also exercises a previously mixed session
String assigned a fresh `pwd() as String` while verifying actual machine
publication in forced mode
(`350170c386cd4082f50fd292269c09ca3b3f1806d7a04f4e01111476332a5de9`).

Full editor/display observations (including nested typed Range and Process)
and retained code-page caching are separate work. The public REPL remains
unchanged until its full journey is covered.

When semantic evaluation knows an If arm, the HIR carries that fact
explicitly. The LLVM producer still evaluates the condition and preserves
its demand if a genuine pending Bool can reach it, but emits only the
chosen result. This prevents the discarded Null placeholder from being
lowered as a physically impossible Number. The default unmarked If
continues to lower both actual alternatives.

Diagnostics distinguish inherited compiler layout lookups, current session
cell relocations, allocated/reused runtime globals, temporary literal roots,
and inter-submission collection. `session-test` in
[`tests/HpirPipeline`](../../tests/HpirPipeline/README.md) is the source
boundary for examining this contract.

The focused journey passed O0–O3 under the .NET 10.0.403 managed host
(adaptive, forced machine, and physical) and in a Linux x64 Native AOT
publish (forced machine and physical). It uses real collection after the
original text/Path literal table is gone, verifies older callable identities
after shadowing and a failed rebind, and checks that a known If still emits
the condition's output exactly once. The full shell, concrete Scope
presentation and gate-held stdout path (including reset while a child is
active) also passed in the wire 7 Native AOT test app
(`a5bdfad08e183bc5ce94a9015c7df3961d20b9dab5c4550d3fe42e8e21ee7952`).
That binary runs Path relocation with old/new spelling and actual writes
and transfers in different directories at O0 and O3, and the gate-held
stdout/reset path at O0 and O2, in each physical execution mode.
The existing public Native compiler
session journey separately passed twelve submissions at O0 and at O2,
including its gated pending Text alias and exact file oracle. This is a
compatibility check for the shared producer and session semantics, not a
claim that HPIR already covers those outstanding pending submissions.

This keeps the distinction made by
[LLVM ORCv2](https://llvm.org/docs/ORCv2.html) between an exported symbol
identity and the resources that materialize a body. HPIR does not introduce
an LLVM JIT dependency: the producer selects the current targets, guest
references retain data independently of executable pages, and the session
boundary is not a generic invocation check.

## Existing contracts

* `Compiler/Session/SemanticSession.cs` stages semantic facts, dispatch tables and
  HirLoweringLayout during compilation. Successful compilation commits code
  obligations before execution. Runtime failure does not undo those obligations.
* ReconcileSubmission publishes a new exported name only when its stable entry
  slot is initialized. A pending typed NativeDatum counts as initialized; value
  observation does not demand pending work.
* `NativeSemanticSessionValues` resolves exported slots through Locations,
  callable identities through Callables, and Scope declaration origins through
  AllocationSites. These maps are part of the existing semantic/runtime bridge,
  not optional diagnostics.
* NativeInterpreter.UpdateProgram retains static states by index and replaces
  the execution image. Previously returned closures/Ranges retain callable IDs,
  capture addresses and path literal origins. Earlier instructions with an open
  SessionSlot must consult the latest published dispatch table.
* NativeFrame.Initialize owns canonical persistent entry cells in
  `sessionLocations`; Bind updates that same dictionary for persistent slots.
  GetLocalValue and IsLocalInitialized observe those cells, including pending
  values. SessionRoots determines accepted lexical resource reachability.

These external contracts remain unchanged. SemanticSession's Compile path uses
the same staged graph/target pipeline; the public CompilerSession API and
AcceptSubmission/ReconcileSubmission lifecycle do not change.

## API and generation ownership

`LlvmHirLoweringSession.Lower(HirModule, trace)` produces an ordinary LLVM-derived
graph for the existing pass boundary. `Emit(result, trace)` emits the existing
NativeHirLoweringResult (NativeProgram plus HirLoweringLayout), without invoking
the old HIR lowerer, Native optimizer, or LLVM backend. Lower and Emit are
separate so callers can run the actual new IR passes, not a CanLower fallback.

Each generation has a fresh LLVM module/context. The session retains only
append-only target descriptors, stable HIR-to-native identities and emitted
Native code, not a graph-indexed heap analysis or a table retaining dead SSA
values. A staged generation forks descriptor/identity registries and is committed
after target emission succeeds. A failed source compile cannot poison reused HIR
IDs; a later runtime failure deliberately does not roll back emitted identities.

Functions/templates, native types/layouts, literal constants, statics and source
allocation sites preserve indices across generations. Newly emitted functions
replace their prior code at the same callable ID; unreachable older functions
remain available to already returned values. Target-added descriptor tails are
imported back into the producer registry before the next generation, preventing
an appended target helper type from colliding with a later source type.

## Lexical storage model

Exported lexical locations and prior imports are ordinary one-field LLVM static
cells. Their identities come from existing HirLocationId, not from object
identity tests. Capture environments contain their direct field pointers.

At the Native boundary, the existing stable persistent entry slot can itself be
the canonical cell: GEP(global, 0, 0) becomes FrameAddress(stableSlot). There is
no second semantic cell or entry root-identity guard. Only exported proxies use
persistent Local role; nonexported entry temporaries must not accidentally enter
sessionLocations or collide with a newly added exported slot in a later image.

A naive mapping of all exported cells to NativeProgram.Statics would change
resource ownership: the runtime roots every StaticState allocation, including
shadowed lexical cells. The existing persistent-local cell mapping preserves
current SessionRoots-based lexical lifetime while ordinary singleton globals
continue to use the existing static mechanism. Source nonentry access uses the
existing explicit location-capture prefix, not an entry-frame lookup algorithm.

Session dispatch metadata is copied directly from HirSessionDispatch identities
into the existing runtime tables and Call/AdaptFunction C operands. No runtime
compiler, target discovery, root admission proof or generic HIR evaluator is
introduced.

## Coverage status

The session class, staged registries, fresh source modules, canonical native
root relocation and open dispatch metadata are implemented. SemanticSession
Compile is now wired through LlvmHirLoweringSession.Lower, the shared
Compiler.OptimizeLlvm entry, and session Emit. There is no feature flag,
CanLower probe or Native optimizer fallback. Its compiler.lower and
compiler.optimize phases are preserved; compiler.target records final Native
emission. The accepted-source checkpoint below predates the production switch.
Standalone Compiler.LowerInstructions also uses the LLVM-derived graph and
shared optimizer before the same Native target boundary.

The source harness in `tests/LlvmSession` passed 15 accepted submissions in each
of two modes (unoptimized and explicit mem2reg/DCE). It uses the real source
binder, semantic analyzer, LLVM-derived producer/target and NativeExecutionSession.
It covers mutable captured roots, Scope aliases and member closures, shadowing
while older closures retain their original locations, escaped captures, generic
aliases, deferred callback replacement and genuinely pending process output
retained across accepted submissions. Three pending-root observations per mode
remain undemanded until the final file output consumes the future.

Evidence is `artifacts/diagnostics/llvm-source-session/{build.log,receipt.json}`.
The Release build passed with zero warnings/errors. Harness SHA-256:
`7d07804854f3fcf0c7338f66b6b472af79b583ef6bca7c77c32d77e2aff0e84b`;
HIR assembly SHA-256:
`66307f0c4e6466c5761643a10e48818a98810f7e73f4fc68eff2c74067a2cbbd`.
Run `dotnet run --project tests/LlvmSession -c Release -- --output
artifacts/diagnostics/llvm-source-session` from the repository root. The harness
has its own explicit compiler friend assembly; unrelated historical HirBinding
tests are not its build dependencies.

### Production CompilerSession acceptance

The separate `--production-session` journey uses public CompilerSession Compile,
AcceptSubmission and ReconcileSubmission with NativeExecutionSession. It passed
on the actual migrated production path, without a feature flag or legacy route:

| Optimization | Submissions | Accepted | Reconciled runtime failures | Numeric expectations | Genuinely pending root observations | Checked files |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| O0 | 12 | 9 | 3 | 6 | 3 | 1 |
| O2 | 12 | 9 | 3 | 6 | 3 | 1 |

The failures cover initialized declarations before an error, uninitialized failed
and unreached declarations, a failed shadow initializer retaining the previous
binding, and an initialized pending alias surviving partial failure. Mutable
captured roots, Scope alias mutation and undemanded process output retain their
actual runtime state across Accept/Reconcile boundaries. Trace checks require
the new lower/target phases and reject the old optimizer-function path.

Evidence is
`artifacts/diagnostics/llvm-production-session/{build.log,run.log,receipt.json}`;
the Release build reports zero warnings/errors. Reproduce with
`dotnet run --project tests/LlvmSession -c Release -- --production-session --output
artifacts/diagnostics/llvm-production-session`. This production checkpoint is
separate from the earlier accepted-only 30-submission receipt.

## Captured export initialization

The first real session run exposed a source ownership split: exported `state`
was initialized in a fresh captured heap cell, while the next submission wrote
the canonical session global. Old read/set/make closures consequently retained
1 after `state=3`; combined with the correctly mutated object field (4), the
observed result was 5 instead of 7. The same split affected a mutable callback
export used by a deferred Range.

Captured declaration allocation now uses the session root's canonical Address
when that lexical location is session-owned. Only non-session captured locals
and fresh iteration cells allocate fresh heap storage. This is a compile-time
ownership choice, not an entry identity guard, a value snapshot, a second proxy
or a copy from one cell into another. The declaration store, later assignments
and all capture environments name the same original location.


On a failed submission, a name resolves to the newest lexical declaration
whose slot was actually initialized, including a declaration superseded
earlier in the same submission. Lowering reserves the same canonical session
cells for these shadow candidates and emits their priority paths as live
session metadata, not as portable IR. Work cleanup retains only the selected
root on failure and only the final visible root on success; an otherwise
unreachable shadow does not keep a child process or an input alive.

The production acceptance cohort was rerun after late InstSimplify and per-use
address ABI views were integrated. Both O0 and O2 again passed the same 12
submissions (9 accepted, 3 reconciled failures), 6 numeric expectations,
3 genuinely pending root observations and one checked output file per mode.
The later receipt and build/run logs are retained separately under
`artifacts/diagnostics/llvm-production-session-instsimplify/`.

## HPIR terminal

The CLI cutover uses the same terminal, parser, completion and session
command loop against the physical executor. There is no fallback to Native
when physical selection rejects a submission. For example, build a Linux
candidate with `dotnet publish src/Msync.Cli -c Release -r linux-x64
-o .temp/hpir/repl-cutover` and run
`python3 tests/Msync.EndToEnd/repl_state_journey.py
--binary .temp/hpir/repl-cutover/msync`.

A submission has a fresh guest stack but shares the concrete cells, captured
addresses, heap and process jobs of its session. Its result is projected for
the terminal using the exact physical offsets for that selection. Nullable
values use the existing packed tag and payload; no boxed runtime Value is
inserted into a binding. Observing a pending stored Text binding does not wait
for it. An execution failure retains the initialized prefix and reconciles
facts from actual cells. Exported Scope references additionally recover their
physical instance identity and lexical allocation site on failure, preserving
aliases and distinguishing an earlier nullable declaration from a later
assignment with the same field spelling. The ordinary completion path does not
scan all Scope fields. Only in-process Scope allocators stamp the lexical site;
they reuse the existing single-object heap header and do not change a .mexe
artifact or its allocator.

The shell's directory authority belongs to the session, not the process cwd.
Existing Path values preserve their origin, whereas subsequent `cd`, `pwd`,
String conversions and host operations use the actual directory frontier.
`fs.canonical` executes at the Path demand with the same native filesystem
operation and no binding-time work. A live `spawn` result retains its process
job across input; presentation observes readiness and PID without waiting for
process exit.

A displayed Range is a consumer in the entry execution, not a new host-side
iterator interpreter. It opens the selected Range, advances one typed element,
prints it while its frame and ownership are live, and closes on exhaustion.
This is the same distinction as OCaml's [Seq.iter consumer](https://ocaml.org/manual/5.5/api/Seq.html):
display requests each element instead of materializing all values first.
Lua's [terminal REPL](https://www.lua.org/source/5.4/lua.c.html) similarly
prints while the VM's result stack is still live. HPIR's output sink is typed
by the selected element geometry; a String span can be printed directly, and
only an actual element union needs a runtime tag test. Callback effects occur
before acceptance; interrupts leave the already executed prefix intact.
Selection/runtime telemetry reports the Range output site, item counts,
reconciled Scope instances and fields, and the concrete failing operation.

The current session does not retain executable frames from previous inputs.
A retained ready vector carries its compile-selected immutable geometry in the
session registry. Acceptance observes the actual retained address once; a
later unwritten entry binding can select its typed cursor from that fact
without a runtime representation branch. A retained numeric vector transform
also records its actual outer callback identity in its plan layout. New input
reserves that body from the lexical registry and reads the current inner
call-target alternatives. Thus rebinding a factory does not rebuild its
already-produced vector, while a genuinely open callback can observe a later
replacement. The original terminal checks both behaviors.

The redirected prompt and `io.read_line` borrow the same input reader.
Publication of a line into a canonical nullable session binding makes the
selected String/Null payload ready before handing the input reader back to
the prompt. This is a shared-owner handoff, not an eager evaluation rule for
all text producers; the same stdin line is not subsequently parsed as a
second command.

The shell's `wc` uses typed String reductions rather than building a
character Range, invoking a generic equality callback, filtering, and
counting. The line count remains the number of LF scalars; the word count
uses the same Unicode whitespace predicate as `split_whitespace`. A Path
still gets its byte count from the file metadata operation, independently of
the decoded String. A completion-bearing private result record is observed at
the actual terminal boundary, while its owning work and guest frame are
alive. Only the presented scalar tails are demanded; the record is not
rewritten into a different runtime layout. Its transient layout is not
reused as persistent storage in the next image.

The [Linux x64 Native AOT REPL receipt](../../artifacts/validation/hpir-repl-cutover-20261122/REPLAY.md)
records the unmodified shell (redirected, Process and actual PTY),
all 19 mutable-prefix/recovery examples, and the editor palette journey
against one ordinary v0.9.5 executable. The interactive test launches in
the foreground so it does not inherit Bash's background-job SIGINT policy.
