# C# typed bytecode and execution (0.8.0)

## Representation and ownership

`Msync.Core` owns the type/value algebra and stack bytecode contract. A module
contains typed function signatures, a constant pool, source spans, and numeric
local/function references. Functions are specialized by the compiler; new
closures capture lexical locations and carry a function-table identity. Mutable
values retain fixed inferred/declared storage types. Calls never send source or
syntax trees to the interpreter. Recursion uses specialized forward references.
The `Null` value is distinct from Bool. Iterator state, live processes, handles,
transport sessions and lazy enumerators cannot be embedded as constants.

The binary container has independent format version **4** and language-semantics
version **0.8.0**. Only the current container and execution contract are accepted;
older formats report their version and ask the user to recompile the original
source. There is no legacy scheduler or snapshot-capture execution mode in the
current interpreter. The semantics header byte identifies the fixed mutable
location and directional scheduling contract, not optional execution switches.
The decoder validates external table references, layouts and reachable
stack/control-flow shape before execution. Bytecode dataflow rejects stack
underflow, mismatched join heights, uninitialized locals, invalid iterator
ownership, invalid closure layouts and impossible operand-kind combinations.
No arbitrary instruction or filename quotas exist. Artifacts contain typed binary
operations, not JSON or serialized source.

The full state identity, nullable-field, optimization and directional barrier
contract is in [mutable state and effects](mutable-state-and-effects-v080.md).
Live cells and process completion owners are runtime state, never constants
serialized as portable execution proofs.

`Artifact`, `Optimizer`, `Binding` and `Interpreter` are separate APIs:

```
source -> typed specialization -> relocatable Module (.mso)
                                  | pure constant/branch folding, block formation
                                  v
                         symbolic optimized bytecode
                                  | bind captured cwd/HOME/source context
                                  | resolve fixed host builtins, specialize local I/O
                                  | compact no-ops and relocate jump targets
                                  v
                         executable Module (.mexe)
                                  | actual selected operands and filesystem kinds
                                  v
                    dependency scheduling -> native operations
```

Relocatable source execution takes the same binding/lowering route as `.mso`.
Executable modules keep captured context when run from another directory.
Context binding does not authenticate or inspect remote endpoints. Raw path
spelling remains available to lexical standard-library operations; actual local
I/O prefixes the captured cwd without erasing native `symlink/../file` traversal.
`PathSemantics` chooses separators from endpoint plus platform, not host alone:
a backslash remains a POSIX filename character, including SSH/WSL on Windows.

### Symbolic paths and local native spelling

An unbound Path carries source-owned `Raw` spelling, captured cwd/home anchors,
and an optional `LocalSpelling`. A source `/` join remains `/` in the symbolic
spelling. On Windows its local projection uses native separators, so a binding
of `~/.codex` can be used locally as `~\\.codex` and later qualified onto SSH/WSL
as `~/.codex`. Qualifying before or after that binding yields the same remote
Path. Qualification discards the local spelling and local anchors, not arbitrary
backslashes in the source. A literal POSIX filename such as `left\\` still joins
to `left\\/leaf` remotely; a global backslash replacement cannot express this.

`PathSemantics.JoinValue` and `Qualify` are shared by the VM and executable
lowering. Native Path-to-String conversion, regex matching and local I/O consume
the local spelling; generic lexical inspection and remote operations use Raw.
Derived path operations use `WithRaw` so a changed pathname cannot retain the
previous path's local projection. Windows lexical operations and directory
iteration derive both spellings together; POSIX joins allocate no second string.
The public four-argument constructor and deconstruction remain available.

The current format serializes this optional local spelling, including nested
Scope values and function captures. The retained format-1 fixture now checks the
explicit obsolete-format diagnostic rather than a second execution contract.
`tests/PathProjection` checks simulated Windows grammar on every host, physical
projected I/O, nested current-format round trips, obsolete-format rejection and
source/object/executable qualification. The native Windows platform journeys
separately execute the same language-level distinction.

## Execution and effects

The interpreter traverses the actual selected BasicBlocks using a precomputed
PC-to-block lookup, not a flat source replay or planner-only block annotation.
Each selected effect block lowers runtime-known local transfer operands to the
local executable operation; jump/loop/return outcomes select the next block.
The interpreter is async stack bytecode, not a JIT: Native AOT disallows runtime
IL generation. Pure constant folding handles boolean operations, arithmetic,
string concatenation and type identity. Basic blocks split at control-flow and
effect boundaries; successors and effect flags are inspectable in `plan`.
Machine binding specializes provably local transfers to a dedicated local fast
path and resolves static connection roots. Other operands remain late-bound.

Discarded scalar transfers run as dependency-owned asynchronous work. Admission
captures scalar operand values; the VM alone executes mutable stores and closure
bodies. A per-endpoint path-prefix index records source reads and destination
writes. RAW, WAR and WAW dependencies retain source order, including directory
subtree overlap; independent siblings do not conflict merely because publication
changes their parent mtime. Finding predecessors visits path ancestors and actual
overlapping descendants, rather than scanning every previously queued copy.
The index owns one registration per admitted scalar transfer. Successful owner,
explicit-transfer and selected-process joins retire that registration on the VM
control thread, unlinking its reader/writer memberships and empty ancestors.
Read observations do not scan unrelated active reader sets to discover garbage.
Faulted or cancelled tasks remain dependency entries; an operational `false`
still remains in the original owner's failure accounting after its completed
resource registration is retired. Work-owner entries are not compacted, because
After-region boundaries refer to their stable admission positions.

Local identity resolves existing filesystem aliases. A selected Path-copy producer
may publish a directory or link used by a later descendant operand. Admission
waits for that specific predecessor, then resolves the descendant's new physical
footprint before later work is admitted; it does not drain unrelated jobs. Same-
file dependencies and independent-copy frontiers need no extra stat or graph
scan. This preserves WAR ordering when a new directory link points outside the
copied tree. Unknown remote projections
conflict only within their selected endpoint: discovering independence must not
authenticate an endpoint merely mentioned by an unselected branch.

Current-semantic branches, loop edges and ordinary lexical calls no longer drain
unrelated copies. Native filesystem observations wait only for conflicting path
writes; native mutations wait for conflicting reads and writes. Opaque native
operations retain an observation boundary. Range transfers keep their finite
operation/preflight semantics instead of becoming an implicit source loop.
`Jobs` and the configured payload budget bound finite-copy admission; a deferred
process-output dependency does not occupy a copy slot while waiting for exit.
Format-1/2 legacy modules retain the original broader control-flow draining.

`release`, `acquire` and `acqrel` preserve the Rust baseline's directional
ordering, not CPU atomic-memory synchronization. Before/release constrains earlier
finite work; After/acquire constrains later finite work. Internal paired region
markers retain the acquired statement's own process-dependent flows without
joining unrelated background captures. Standalone release is
not a sticky background mode. An unmarked external call starts and waits;
`release executable(...)` returns a live Process, `acquire process` joins only
that process and its connected flows, and bare acquire joins the interpreter
session's pending frontier (including inherited caller work). A lexical function
joins its own admitted work on return, never the caller's unrelated released processes. Optimizer inlining must
preserve that boundary; job-owning callees cannot be flattened as pure values.

Process stdout/stderr retain the 0.8 `String`-at-completion API; they are not the
legacy live spool/stream-port capability, and stdin streaming is not implied by
these scheduling changes. Their values and pure derived calculations remain
deferred until demanded. For example, reading stdout and transferring it before
an independent gate-file write must not block that gate behind the child waiting for it. Ordinary process
launch fences exclude live process-connected flows, so a released server may be
completed by a later client. Completion tasks retain natural process lifetime;
cooperative cancellation of an observer does not kill an admitted child. Optional
output-spool errors stop retaining bytes but continue draining native pipes,
preserving the error rather than turning quota enforcement into process death.
The gate-driven CLI acceptance is `tests/Msync.EndToEnd/scheduling_journey.py`;
it exercises source and bound binary artifacts with the exact test interpreter
reported by the harness, never as a product dependency.

The scheduler reuses [.NET Task.WhenAll](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.whenall?view=net-10.0)
for owned completion and platform async I/O rather than allocating a custom
worker executor. Resource ordering is distinct from speculation permission, as
in [MLIR's effect model](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/):
no dependency proof authorizes authenticating or executing an unselected branch.
The directional analogy follows [LLVM's motion rules](https://llvm.org/docs/Atomics.html)
without adding CPU atomic pairing or importing a memory model into file copying.

Symbolic-link publication renames the owned same-directory stage as an entry;
public `File.Move` first tests `File.Exists`, which treats a link to an existing
directory as a non-file. Unix uses native `rename`. Windows initially uses
`MoveFileEx`, but an existing directory-link destination requires the same
handle-based fallback as [Rust std](https://github.com/rust-lang/rust/blob/master/library/std/src/sys/fs/windows.rs#L1256-L1326).
Native Windows run 42514423642 proved that initial link publication and privilege
succeeded while retransmission failed with `ERROR_ACCESS_DENIED`.

On that failure, open the staged entry with `DELETE`, all three sharing flags,
`OPEN_REPARSE_POINT | BACKUP_SEMANTICS`; apply `FileRenameInfoEx` (Win32 class 22)
with replacement and POSIX flags. The typed Unicode header supplies the actual
FileName offset (20 bytes on x64/ARM64, 12 on x86); UTF-16 length excludes the
NUL while the buffer includes it. Capture the native result before disposing the
handle. This is an atomic entry replacement, not a delete-then-move operation.
Link targets and their contents are never removed or modified to publish a link;
ordinary real-directory destination checks remain distinct. The focused
source/object/executable journeys in `tests/SchedulingGraph/PathNamespaceChecks.cs`
(`--namespace-only`) cover retransmission, retargeting, obstruction/retry, future
alias ordering, and Unicode/spaced native paths without skipping symlinks.
The closing [native Windows run 42516326981](https://github.com/kleedaisuki/msync/actions/runs/42516326981)
(source `f0099d3`, including `0bfb6d0`) passed that complete lifecycle through
source, relocatable and executable artifacts, including the original second-copy
failure and future-alias WAR case. The accepted Linux namespace journey remains
unchanged; no unrelated optimizer matrix was repeated for this platform fix.

A consumed `->` yields Bool. Operational I/O/permission/native-process failures
yield false, preserve source/destination/cause diagnostics, and increment failed
transfer accounting. Cancellation and malformed typed operations do not become
false. Failed work is visible to CLI exit status even if language code handles
the false result. Completed directory/cohort members remain in place for retry.

Range plans use `IAsyncEnumerable<Value>`. Native adaptors and selectors poll
only on demand. `MakeLazyRange` stores already-pure values directly and explicit
zero-argument closures for effectful elements; a compact mask distinguishes them.
`DeferRange` invokes a range-producing closure on first demand, including its
arguments. The source compiler therefore does not spend effects on `take(0)`.
A REPL session retains local values and stable function identities. Deferred
callbacks may observe a callable assigned in a later submission, so their code
linkage follows the session's latest typed image while their original physical
context remains fixed. The latest code and constants are projected together for
that origin, never mixed with a later submission's bound constant table. An old
range created in directory A still evaluates uncaptured path literals in A even
after `/cd B`; a replacement callback's already-evaluated captured Path from B
retains B. This is caller-context evaluation, not a new function-definition
context. Projections are shared per exact binding context and image generation;
they contain typed code templates, not live lexical cells or Scope instances.
Standalone artifact interpreters remain frozen. Deferred call resolution
memoizes the resulting range on first demand, so repeated consumers do not
reevaluate the source call's arguments/body.

`RangeValue.Selection` retains `PathSelection` origin (directory, .NET regex,
recursive traversal and composed native adaptor projection). A deferred range's
`Resolve` exposes that origin only when selected work demands it. The transfer
owner discovers names, runs the same .NET regex/adaptors/callbacks and sends its
chosen paths over one native SSH duplex channel, then receives the archive on
that channel. A session-scoped query service lets filesystem predicates use the
same selected channel rather than separately authenticate. Zero-demand `take(0)`
does not resolve its source, enumerate or authenticate. See the real native SSH
fixture beside `tests/Msync.Transfer.Tests`.

Path calls return a completed Process value with exact argv, captured stdout,
stderr and exit code. Remote calls share the native SSH/WSL command builder with
file transport, including aliases, SSH config and endpoint-native HOME expansion.
The optional combined output-spool budget applies to bytes before text decoding.
Optional runtime observations are per execution; disabled observation does not
construct event records. Native failure and transfer/process lifecycle events
retain actual outcomes rather than generic error labels.

## Verification

`tests/Msync.Runtime.Tests` is an executable contract harness: 100 deterministic
Unicode binary round trips, pure-fold equivalence, branch-target compaction,
source/object/executable cwd ownership, read-after-write and symlink-alias
scheduling, Bool failure + existing-target preservation, Process stdout and actual
spool budget, lazy `take(0)` and lazy values escaping a REPL submission. The current checkpoint passes **234 assertions**. Run with isolated
build outputs when other agents are compiling:

```
dotnet run --project tests/Msync.Runtime.Tests/Msync.Runtime.Tests.csproj \
  --artifacts-path .temp/build-runtime -m:1 -nr:false
```

Native and transport journeys are documented beside their executable harnesses;
CLI language/source-free artifact journeys belong to the integration suite.
Neither an SDK build nor a shell-backed launcher is evidence of real SSH
authentication or native Windows acceptance.

## Engineering and research basis

- [Native AOT limitations](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
  require avoiding runtime code generation/reflection-dependent serialization.
  Explicit opcodes and binary tables satisfy that deployment constraint directly.
- [MLIR bytecode versioning](https://mlir.llvm.org/docs/BytecodeFormat/) separates
  container encoding from dialect semantics. The same distinction motivates the
  independent format/language versions here; this project does not embed MLIR.
- [Jones, Gomard and Sestoft, Partial Evaluation and Automatic Program Generation](https://raspi.itu.dk/people/sestoft/pebook/pebook.html)
  describes specialization as separating known inputs from residual work. Here
  type/function specialization is static, contextual path/endpoint lowering is
  binding-time, and filesystem facts remain selected runtime inputs. This is a
  concrete staging boundary, not permission to evaluate effects during `check`.

## POSIX program source versus endpoint data

A Windows checkout can compile C# raw multiline literals with CRLF bytes. POSIX
`sh` then reads `set -eu\r` as an invalid option, even though the native WSL/SSH
endpoint itself starts successfully. `ShellProgram.Source` canonicalizes only
source-owned control-program literals to LF. Transfer, selector, query and native
filesystem fragments normalize before composition with runtime values; process
launch source and the generated download preamble follow the same boundary.
Escaped protocol newlines are already host-independent. Normalized immutable
fragments are cached on demand, so ordinary local transfers do not initialize
unused remote programs.

The generic command/transport boundary must **not** normalize its assembled
string: quoted filenames, arguments and payloads may legitimately contain CRLF.
The repository's `*.cs` LF rule is a source convention, not the correctness
mechanism. `tests/IO/shell_source_newlines.py` builds a disposable copy with every
C# source forcibly converted to CRLF, then checks real authenticated transfers
and native process calls with actual CRLF filenames, payloads and arguments. It
also runs the six source/object/executable selected-query journeys on that image;
each actual selected transfer keeps its one SSH client/authentication/exec session.

## Adaptive execution research

[HotSpot/CoreCLR runtime research](managed-runtime-optimization-v080.md) separates
host Native AOT optimization from guest-bytecode feedback. The proposed first
experiments are cheaper call resolution, guarded site caches and precompiled
fused handlers; no native JIT, general OSR, custom GC or speculative filesystem
proof is implemented or implied by that research.

### Prepared execution metadata

Executable projection builds the instruction-to-basic-block index once per
immutable Module identity. A weak-key `ConditionalWeakTable` shares that prepared
metadata between Interpreter instances without keeping discarded modules alive.
The cached record contains only bytecode and block lookup arrays: singleton
objects, captured cells, transfer results, filesystem observations and credentials
remain owned by the interpreter or its explicit ExecutionSession. REPL image
publication creates a new module identity; contextual projections continue to use
the closure's captured BindingContext.

This is static execution preparation, not a JIT or adaptive specialization. The
runtime still admits each selected operation and handles its actual result.
`tests/ArtifactCurrent` executes one cached image repeatedly to check that stores
still run and separate interpreters do not share singleton state.

The implementation uses the platform's [weak-key identity table](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.conditionalweaktable-2)
rather than a global strong module registry. A warmed constructor probe with
1/128/1024 instruction bodies allocated 1,152/2,168/9,317 bytes before sharing and
984 bytes at every size after sharing. Concurrent product builds made those first
timing samples unsuitable for a causal latency claim; the allocation reduction
isolates the eliminated per-PC index and temporary LINQ allocations.
