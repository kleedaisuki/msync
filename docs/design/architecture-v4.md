# v0.4 structured compiler and execution architecture

## Current integration checkpoint (2026-10-28)

The development CLI defaults to structured v0.4. Typed lexical macros,
transactions/checkpoints/origin undo, real native process views, persistent
program ports, physical flows and submission-owned Start/Publish/RegionCommit
gates are integrated. The earlier dated foundation tables below retain their
historical counts; they are not the current remaining-work list.

The complete Windows/Linux compatibility matrix and actual native gates passed
at [`1121571`](https://github.com/kleedaisuki/msync/actions/runs/40636799976), then
again with byte-verified file refresh at
[`0b51616`](https://github.com/kleedaisuki/msync/actions/runs/40638558941).
The proof-sharing revision exposed a Windows cancellation/output-channel race
in its native gate. Explicit worker control outcomes and terminal-result joining
repair that race; the integrated compiler/worker checkpoint
[`5d4d99d`](https://github.com/kleedaisuki/msync/actions/runs/40642774605)
passed both ordinary OS jobs and actual native gates. Sparse finite enrollment
and its disjoint active-owner import frontier subsequently passed both ordinary
OS jobs and actual native program/macro/workspace gates at
[`a3d3508`](https://github.com/kleedaisuki/msync/actions/runs/40648300741).
The hierarchical namespace/outer-FIFO compatibility follow-up passed the same
ordinary and actual-native gates at
[`181221e`](https://github.com/kleedaisuki/msync/actions/runs/40651887259).
Named-body concurrency remains separate unfinished work.

Following the user's parallel-performance authorization, committed components
are being measured and optimized independently. Store candidate sharing,
byte-verified artifact reuse and immutable graph-proof sharing have focused
measurements in [performance-v4.md](performance-v4.md). Final performance/release
acceptance remains open; no debug correctness runtime is a latency benchmark.
The single-input opaque-program/external-FIFO bootstrap boundary described at
the end of this note is still an explicit capability limitation, not implemented
support inferred from the split-input native fixture.

## Original design and historical evidence

Status: implementation-facing design, not a claim that the features below are
already implemented. This note integrates the supplied v0.4 example with the
existing v0.3.1 public contracts. See [implementation.md](implementation.md),
[planning.md](planning.md), and [planner-proof.md](planner-proof.md) for the
current system; transaction mediation research belongs in its focused note.

## Decisions and invariants

1. Keep syntax, binding/expansion, effect planning, and execution distinct.
   Parsing, `check`, and formatting never launch a program. Planning may inspect
   capabilities and metadata, but never starts a data-producing process.
2. Introduce a structured document and typed high-level IR. Do not represent
   literal bytes or a child process as fake physical `Backend` variants. A
   namespace lookup and a file path are different values even if both print as
   text. Existing `Script`, `Program { operations }`, `CopyOp`, and the existing
   library entrypoints remain usable through a compatibility adapter.
3. Resolve macro arguments as values in the caller, bind them in a fresh lexical
   scope, and lower the body structurally. Never replace matching substrings in
   the macro source. A formal named `.codex` therefore does not rewrite comments,
   string contents, `.codex-old`, or a similarly named local.
4. Transactions introduce runtime context, not a lexical scope. A declaration in
   a transaction has the containing block's lexical identity. Compile-time
   binding rollback and resource rollback are separate mechanisms.
5. Every optimization carries a precondition and resource/version provenance.
   Unknown effects block only transformations whose proof needs their absence;
   they are not evidence that a program is pure.
6. Managed file versions are immutable. A live child cannot retain a writable
   handle into a version that has already been committed or exposed as a
   repeatable-read snapshot.
7. Isolation applies to resources actually mediated by the manager. A private
   working directory, rewritten argv, or a before/after directory diff alone
   does not mediate arbitrary absolute-path writes by an external program.

## Representative source-to-IR flow

```text
UTF-8 source + SourceId
    | indentation / tokens / precise operand provenance
    v
Document: structured syntax, expression kinds retained
    | binding identities / macro expansion / source map
    v
Module: typed sources, sinks, process specs, transaction regions
    | capability inspection / resource identity / version and effect analysis
    v
Execution graph: ordinary flow DAGs + region/checkpoint/process protocols
    | admission, bounded buffers, process ownership, version store
    v
Execution manager --> existing native file/stream/remote adapters
```

The language is currently straight-line plus structured regions, not general
branching control flow. Start with dense node IDs, region-owned ordered vectors,
resource versions, and def-use edges. A general CFG, phi placement, JIT, or MLIR
dependency is unnecessary to obtain the useful parts of SSA-style reasoning.

### Frontend contract

The proposed AST is additive rather than expanding the exhaustive legacy
`StatementKind` enum in place:

```rust,ignore
struct Document { statements: Vec<Node> }
struct Node { kind: NodeKind, span: Span }
enum NodeKind {
    Endpoint { name: Name, value: Expr },
    Alias { name: Name, value: Expr },
    Copy { source: Expr, destination: Expr },
    Macro { name: Name, params: Vec<Name>, body: Vec<Node> },
    Call { callee: Name, args: Vec<Expr> },
    Global { names: Vec<Name> },
    Transaction { name: Name, isolation: Isolation, body: Vec<Node> },
    Commit,
    Rollback { target: Option<Name> },
    Comment(String),
}
struct Expr { kind: ExprKind, span: Span, provenance: OperandSource }
enum ExprKind {
    Reference { qualifier: Option<Name>, path: String },
    Literal(String),
    Call { callee: Box<Expr>, arguments: Vec<Expr> },
}
```

Keep the quote distinction in the AST: the new source `"success" -> result.txt`
is UTF-8 bytes without an implicit trailing newline, not a filename. An explicit
qualified reference such as `$local::"folder name/input.txt"` denotes a path.
Bare quoted expressions remain literals in destinations and bindings too;
only structural `/` joins and explicit namespace `::` operations coerce their
components into paths. The old parser remains available to callers requiring
legacy quoted-operand semantics; the v0.4 frontend must not silently erase the
distinction by storing only the decoded strings.

Endpoint admission distinguishes logical type from host stability. Preparation
marks future source/destination operands before runtime preflight or enrollment
can inspect them. Enrollment captures an existing directory envelope, retaining
raw parent traversal for the owning version view to validate after its producer
gate. Program argv paths are values, not required source files; enrollment does
not assert that their leaves exist. The native future-ancestor fixture exercises
an ordinary file becoming a directory before file reads, literal writes and
stdout publication, followed by named origin undo.

The v0.4 example deliberately uses `$pwd::des/agents/.` and
`$pwd::prompt.txt`, whereas v0.3 accepted only `$pwd::pwd` and its children.
The new frontend should anchor the structurally resolved item/path in the
invocation cwd and preserve `$pwd::pwd` as the existing cwd spelling. A bare
unbound macro argument such as `wsl` is a path item, not an implicit reference to
an endpoint named `wsl`.

Indentation parsing needs an explicit indentation stack and precise error spans:
unexpected indent, inconsistent dedent, empty required body, mixed ambiguous
tabs/spaces, and nested macro declarations. Canonical formatting uses one
consistent indentation style, retains comments, and is idempotent. The REPL
collects a structured submission before compilation rather than treating each
body line as a separate top-level script. A blank continuation line or an explicit
submission action must finish the block without changing ordinary one-line use.

### Typed HIR contract

The names here are interfaces to agree with implementers, not a required public
ABI. Sources and sinks intentionally differ so illegal directions cannot reach
the runtime:

```rust,ignore
struct Module {
    root: RegionId,
    regions: Vec<Region>,
    processes: Vec<ProcessSpec>,
    transactions: Vec<TransactionSpec>,
    source_map: SourceMap,
}
struct Region { operations: Vec<Instruction> }
enum Instruction {
    Transfer { source: Source, sink: Sink, origin: Origin },
    Spawn(ProcessId),
    Transaction(TransactionId),
    Checkpoint(TransactionId),
    Rollback { transaction: TransactionId, to: RollbackPoint },
}
enum Source {
    File(Location),
    Literal(Arc<[u8]>),
    Port(ProcessId, ReadablePort),
    Scalar(ProcessId, ScalarPort),
}
enum Sink {
    File { location: Location, mode: DestinationMode },
    Stdin(ProcessId),
}
enum DestinationMode { Exact, Insert, ReplaceTree }
enum ReadablePort { Stdout, Stderr }
enum ScalarPort { Pid, ExitCode, Meta }
enum Argument { Text(String), Path(Location) }
```

An internal lowering value can additionally be an endpoint capability, a path
item, or a process capability before it becomes a `Source`/`Sink`. Those are not
interchangeable strings. Process fields are checked by their actual type; a
misspelled `program::stdot` is not treated as a local filename.

`ProcessSpec` owns the executable location, ordered typed arguments, captured
cwd, environment policy, transaction membership, and source origin. Calls pass
an exact argv, never an interpolated shell string. Thus
`bash("python", "run_codex.py", .codex)` literally invokes Bash with those
arguments; shell evaluation needs an explicit `-c` argument. This preserves a
single safe, explainable rule instead of a special-case Bash launcher.

`pid` is an immediate replayable UTF-8 scalar once spawn succeeds; `exit_code`
is a replayable scalar that waits for termination; `meta` is documented structured
UTF-8 metadata. Stdout/stderr are consuming streams, not repeatable scalar
values. Initially give each live port exactly one flow owner; reject duplicate
consumption at compile time unless a future explicit broadcast/replay operator
defines storage and backpressure. An unused stdout/stderr still needs bounded
draining so it cannot deadlock child exit.

`Origin` retains the source identity, exact expression span, macro definition
span, and expansion call chain. A REPL macro defined in an earlier submission
must not report its definition span against the current input buffer. Store
the defining source unit while its macro remains reachable.

## Macro ownership and hygiene

`MacroDefinition` owns its body, declaration-time binding snapshot, definition
source identity, and formal list. `ExpansionScope` owns freshly assigned local
binding IDs plus typed formal values. The caller's mutable binding map is never
borrowed as the macro's local environment.

- Formals shadow the macro's other lexical imports. Duplicate formals are errors.
- `global a, b` imports only bindings present before that macro declaration;
  later declarations with the same spelling cannot be captured accidentally.
- Macro body declarations do not leak into the caller or another expansion.
- Transactions inside a macro use that macro expansion's lexical environment;
  they do not create another environment.
- `:=` imports retain concrete captured values. A captured contextual capability
  such as `=> $pwd` remains contextual rather than being frozen into a string.
- Argument expressions resolve entirely in the caller. Intrinsic `$pwd` inside
  a body uses invocation cwd; lexical `$file` uses the macro definition's source
  identity. This distinction must be pinned in tests, including later REPL `/cd`.
- Nested macro declarations are rejected even if the enclosing macro is never
  invoked. Macro call recursion is diagnosed through an expansion stack; a size
  budget prevents accidental explosive expansion with an actionable call trace.

This is a smaller specialization of context-bearing expansion used by
[rustc hygiene](https://rustc-dev-guide.rust-lang.org/macro-expansion.html): keep
binding identities and expansion provenance, not merely unique textual prefixes.

## Directory replacement is an operation, not normalization

`target/` retains insertion/container behavior. `target/.` requests replacement
of that directory itself. It must survive tokenization and lowering as
`DestinationMode::ReplaceTree`; cleaning it to `target` before mode selection
would fall back to the existing-directory rejection rule.

Replacement has tree read/write effects and a staged tree version. Validate
source/target overlap, native symlink/reparse boundaries, and conflicts before
publication. For a transaction it participates in the same version store as an
ordinary file. Nontransactional tree replacement needs an explicit recovery
protocol on platforms lacking atomic exchange for nonempty directories; do not
claim that a remove-then-recursive-copy implementation is atomic.

## Transactions: context, checkpoints, and origin retention

The manager owns:

```text
VersionedResourceStore
  immutable File / Tree / Absent versions
  committed epoch map
  transaction-owned tentative version map
  retained origin references

TransactionState
  id, isolation, origin epoch, latest checkpoint
  pending overlay, read versions, directory/predicate reads
  write set, process cohort, lifecycle
```

The named transaction records an origin version set on entry. `commit` seals the
current pending epoch and advances the latest checkpoint, but does not discard
the origin versions: the user's later `rollback A1` must undo earlier explicit
commits too. Bare `rollback` inside A1 discards only work since its latest
checkpoint and resumes that same transaction. Block exit implicitly commits.
An explicit cross-transaction rollback inside a transaction is rejected by the
compiler rather than left to accidental scheduler order.

This is savepoint-like origin retention, not SQL's usual final COMMIT meaning.
[SQLite savepoints](https://www.sqlite.org/lang_savepoint.html) give a useful
comparison: releasing an inner savepoint does not make its changes immune to an
outer rollback. The syntax and lifetime are nevertheless msync-specific.

Named rollback must be scheduled as writes to all affected resource versions.
It cannot blindly restore old bytes over a later unrelated owner's write.
Choose and test an explicit conflict rule for that case: require the transaction
to remain the last owner, otherwise report a version conflict; or derive a new
serial schedule that preserves the language's ordered semantics. The supplied
A1/rollback/B2 sequence is straightforward because rollback precedes B2.
Retain journal content until the last statically known rollback, then release
it; interactive retention needs a visible lifetime/resource policy rather than
unbounded hidden backups.

### Four isolation policies

| Policy | Visibility / validation mechanism |
| --- | --- |
| Read uncommitted | May see another transaction's newest complete tentative version; never a half-written byte buffer |
| Read committed | Capture a committed epoch per statement; own pending writes take precedence |
| Repeatable read | Stable transaction-start snapshot plus own writes; retain referenced versions |
| Serializable | Repeatable visibility plus resource/predicate validation or strict conflicting-resource locks |

Directory enumeration is a predicate read: validating only returned files
misses a newly created matching child (a phantom). Ancestor/descendant overlap,
tree replacement, symlink identity restrictions, and executable reads must
participate in the resource model. Two-phase conflict reservation is a practical
first serializable implementation; disjoint resource regions can still run
concurrently without a full SSI implementation. The four policies must be
represented explicitly, not mapped to four spelling aliases for a single global
mutex. Their guarantees follow the anomaly distinctions in
[PostgreSQL's isolation documentation](https://www.postgresql.org/docs/current/transaction-iso.html),
but msync's managed-filesystem scope is different from database relations.

Checkpoint capture waits for all relevant writers to quiesce. A child with
stdin and stdout flows must be co-started with them; a completion edge from the
child to the consumer would deadlock a full stdout pipe. At a checkpoint, close
stdin after its producers finish, drain stdout/stderr under bounded storage, and
join the process cohort before sealing writable versions. An exit-code read
after that checkpoint remains available without respawning the program.

### External effects and capability honesty

The process broker has to mediate accesses to managed resources, including
absolute paths, inherited handles, descendant processes, rename/link/unlink,
mmap-backed writes, and directory enumeration. Do not infer that the argv list
is the complete effect set. Unknown children conservatively have a managed-root
or universe effect until an isolation adapter establishes a narrower boundary.

On Linux a private mount namespace with immutable version roots and a mature
[bubblewrap-style launcher](https://github.com/containers/bubblewrap) can provide
path-preserving views; access restriction and syscall policy remain explicit.
[Landlock](https://docs.kernel.org/userspace-api/landlock.html) restricts authority
but does not itself redirect writes into an overlay; probe the actual ABI rights
needed, including truncate and refer, rather than assuming kernel version alone.
On Windows, a job object controls process-tree lifetime but does not virtualize
file IO. An application broker or an independently verified projection adapter
must fill that gap. Neither platform can undo an already delivered network
message by restoring local files; such effects require a staged/outbox protocol
or an explicit nontransactional boundary, not a false rollback promise.

## Effect and proof model

Use typed `Read`, `Write`, `Consume`, `Spawn`, `Observe`, and `Checkpoint` effects
over interned hierarchical resource IDs. Reads also record version/epoch.
Process-private transient resources are separate from tracked external resources;
clock queries or internal allocator syscalls need not serialize unrelated file
copies just because they are syscalls.

Recommended analysis facts:

```text
Origin(value)          = source/version that defines its bytes
MayAlias(a,b)          = conservative hierarchical/physical overlap
MustSame(a,b)          = positive identity proof, not mere possible overlap
LastDef(resource)      = latest dominating write/checkpoint/rollback
Observed(version)     = stream output, explicit observation, or committed view
Reversible(effect)    = an owned staged version and a valid compensation scope
```

The existing planner's indexed hazard frontier remains useful inside ordinary
flow regions. Generalize its resource accounting once instead of adding a
separate bespoke alias algorithm for transactions. Preserve the distinction
between may-overlap (adds edges) and must-equal (can justify elimination).

[LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) is the production model
for a compact def-use view of memory and alias-aware clobber queries. Here the
analogous objects are file versions, tree predicates, and irreversible consumes.
Cache facts per immutable IR epoch and invalidate on mutation; do not reuse a
proof across a child effect, checkpoint, or rollback without rechecking its
resource/version assumptions.

[MLIR's side-effect/speculation interfaces](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
show why effects and safe speculation need separate models. A copy whose final
write is dead can still perform IO or fail; an exit-code observation or checkpoint
is an observable boundary. v0.3's quiescent-final-filesystem contract must not
justify deleting a transaction's committed intermediate view.

Each rewrite records `(rule, removed/replaced nodes, resource proofs, version
epoch, observation boundary)`. A lightweight verifier checks the certificate
against the current IR. Useful rules are literal propagation, stable source
forwarding, dead writes within an unobserved epoch, unused pure metadata reads,
and disjoint region scheduling. Never common-subexpression-eliminate two spawns
or consuming stream reads because their text matches.

## Research fit, without importing unnecessary machinery

[egglog (PLDI 2023)](https://arxiv.org/abs/2304.04332) combines incremental
Datalog-style facts, lattices, and equality reasoning. It is a plausible later
engine for cooperating resource and rewrite analyses. Start with explicit Rust
worklists and a small verifier: current expressions are simple, while effects,
checkpoint epochs, and dynamic resources are the hard part. Equality saturation
does not establish effect equivalence automatically.

[Relational contextual equality saturation (2025)](https://arxiv.org/abs/2507.11897)
is particularly relevant because a rewrite may be valid only under a particular
context. For msync that context includes transaction epoch and resource/version
proofs. Treat this as a concrete research connection, not evidence that an
unrestricted e-graph is ready to replace the runtime scheduler. First measure
whether the finite worklist misses profitable alternatives.

## Module evolution and integration order

Keep the `msync` library facade stable while creating focused modules:

```text
frontend/{ast,lexer,parser,format,expand}.rs
hir/{values,regions,effects,verify}.rs
transaction/{store,visibility,journal,manager}.rs
process/{spec,ports,broker,platform}.rs
plan/{resource,index,rewrite,proof}.rs
```

These are ownership boundaries, not a demand for a separate Cargo crate per
file. Extract crates only where dependency isolation is useful: a pure frontend
must not pull terminal/OS dependencies, while the native runtime naturally does.
Prefer path re-exports over mass public-API renaming. Do not move every existing
file during feature implementation just to make the tree look more modular.

Implementation slices, each with an actual user journey:

1. Structured syntax/formatting and source maps; macro expansion into existing
   file operations; explicit directory replacement. Check/fmt remain pure.
2. Typed literal and process source/sink adapters; exact argv; bounded child
   port ownership, co-start, cancellation and exit-code replay.
3. Version store and rollback epochs; literal/file/tree transactions including
   checkpoints, full named rollback, and failure restoration.
4. Visibility policies, conflict/predicate detection, and process mediation;
   adversarial child tests verify no managed write escapes the selected adapter.
5. Unified effect scheduling/proof verification, REPL structured submissions,
   user diagnostics, and capability explanations.
6. Only after integration, profile frontend algorithms, version retention,
   process setup, lock contention, and end-user command latency in milliseconds.

Use small targeted local tests for the slice being edited. GitHub Actions runs
the cross-platform feature matrix; avoid repeating unrelated full local suites.
High-value properties include formatting idempotence, hygienic alpha-renaming,
optimized/unoptimized managed-state equivalence, rollback-to-origin equivalence,
checkpoint replay, no lost pipe bytes, resource-budget invariants, and serial
reference equivalence for admitted serializable schedules. Deterministic
interleaving fixtures must distinguish the four visibility policies and include
directory phantoms and child-created files; passing syntax snapshots alone says
nothing about those semantics.

## Implemented foundation checkpoint (2026-10-27)

The development branch is `codex/v0.4.0`; `main` retains the verified v0.3.1
CLI. This checkpoint adds working library components, **not a completed v0.4
release**. The CLI still uses the legacy flat frontend until the transaction and
process-resource execution manager is integrated. Package version is therefore
not advanced prematurely.

| Boundary | Current implementation | Verification |
| --- | --- | --- |
| Physical endpoints | `endpoint.rs`; old `compiler::{Backend, Location}` paths re-export unchanged types | Existing compiler target: 25 passed |
| Structured syntax/format | `language::{Document, Expr, parse, format}`; indentation, contextual string/path types and explicit destination modes | 12 targeted cases, including 64 generated UTF-8 literal round trips |
| Macro expansion | `compiler::structured::Session`; isolated frames, typed arguments, definition-time `global`, source/cwd split, session identities | 22 targeted cases |
| Typed IR and proofs | `hir`, `hir::{effects, graph}`; transaction regions, process events, shared-view epochs, type facts, directory metadata and provider-bound refinement | 47 targeted cases; independent models check bytes, reads, parent metadata, publication phases and request ordering |
| Atomic frontend submission | `pipeline::{compile, compile_with_context}`; parse, candidate expansion, effect obligations, then lexical-state commit; retain adapter proof premises | Four integration cases, including proof failure without leaked bindings or IDs and invalidated resource-view premises |
| Compound input ownership | `language::SubmissionBuffer`; physical lines accumulate until blank line/EOF, cancellation discards the entire prefix | Five buffer cases plus actual default-frontend macro/transaction REPL journeys; compiler sees no partial body |
| Version visibility | `transaction::Store<V>`; immutable references, four visibility policies, atomic logical checkpoints, retained origin | 17 cases including write skew, absence, directory phantoms and rollback properties |
| Filesystem publication | `transaction::filesystem`; independent disk artifacts, tree manifests and retained compensating publication journals | Separate real-filesystem target; not an OS-wide atomicity or crash-recovery claim |
| Filesystem transaction manager | `transaction::manager`; minimal dirty publication, compensating receipts, stable expanding coverage, foreign-head observation and retained origins | 31 Windows real-filesystem cases; independent metadata controls and cloud Unix acceptance are separate |
| File/literal reference execution | `execution::execute`; expanded macros, selectors/containers/replacement, implicit atomic statements, checkpoints and named rollback | 19 Windows journeys, generated file-state model and one actual WSL-to-native directory replacement; sequential oracle, not the production async scheduler |
| Process ownership | `process`; exact argv, concurrent bounded-memory output drains, replayable scalar ports, Job/group ownership | Separate actual-child target; not a transaction sandbox |
| Native Linux resource views | `process::view_linux`; frozen original-path mounts, private temporary/network namespaces, credentialled exec worker, no unmediated fallback | Six real WSL native-tmpfs journeys plus capability-cache regression; explicit cloud namespace gate |
| Native Windows resource views | `process::view_windows` and `windows_view/`; owned Sandboxie box, driver policy readback, authenticated bounded tree/stdio bridge, payload identity and metadata | Local protocol/absence controls; signed driver installation + ABI proven in cloud; actual boxed IO gate still required |
| Production stateful runtime | `execution::RuntimeSession`; real shared program cohorts, physical/finite endpoint adapters, prepared launch admission and owned async diagnostics | Nine actual Linux program journeys, real physical/WSL endpoint gates and Windows file controls; all-phase event scheduling remains open |
| Default CLI/REPL | Structured v0.4 by default, explicit legacy mode, in-place formatter, live job identities and ready-publication pump | 22 existing CLI cases, six structured cases, 12 job cases, 14 Windows UX cases and seven real ConPTY journeys; native cross-input source gate is separate |

Local integration runs are restricted to affected targets plus formatting and
strict library/changed-target Clippy. The CI selector in `scripts/ci-select.ps1`
maps component edits to meaningful test targets and keeps broad compatibility
coverage for shared transport, dependency, or workflow changes. Design-only edits
do not start Cargo jobs. Fourteen selector checks distinguish these cases, including
selector-only edits which run the selector regression rather than unrelated Rust
suites. Review counterexamples are selected with the components they cover.

The first Actions integration exposed an empty-relative-root joining bug: joining
the empty manifest key appended a separator to an ordinary file stage. Linux and
ordinary Windows paths rejected it; local verbatim Windows paths had hidden the
problem. Root entries now retain the root path without joining an empty component.
Linux also required explicit mode preservation when moving a read-only directory
between parents. Independent review separately reproduced cleanup changing the
permissions of an external hard-link alias; cleanup now unlinks files without
changing their inode-wide mode/attributes. See [review-v4.md](review-v4.md) for
the counterexample and remaining recovery-intent hardening before full manager
integration. These findings are why native cloud verification and independent
filesystem journeys remain separate from pure model tests.

The corrected foundation at `e7fd5bd` passed both Ubuntu and Windows in
[Actions run 40481406869](https://github.com/kleedaisuki/msync/actions/runs/40481406869).
The selector reported `full=false`; it ran the library checks and the seven
affected feature/review targets, not another complete compatibility suite. This
includes the native Unix read-only-directory publication and both independent
hard-link permission regressions. It does not certify the later execution-manager
or resource-view work, or the original full v0.4 objective.

The subsequent complete compatibility matrix at `385c104` also passed on both
platforms ([run 40493994178](https://github.com/kleedaisuki/msync/actions/runs/40493994178)).
It covers the first manager/native-rename milestone, not the later code above.
Independent dependency provisioning at `e91efbf` verified the pinned installer
hash/signature, running service/driver and native version `5.73.8`, ABI `356912`
([run 40494475547](https://github.com/kleedaisuki/msync/actions/runs/40494475547)).
It deliberately created no sandbox and did not certify program IO. A setup-only
repair ran selector checks and skipped unrelated Rust tests rather than cancelling
or repeating the already completed compatibility run.

Remaining delivery obligations are the unified structured execution manager,
verified Linux/Windows program-resource views, runtime coordination of overlapping
program pipelines, CLI/REPL block accumulation and diagnostics, end-to-end
transaction journeys, and then measured latency/concurrency optimization. A
provider-name/view-ID effect certificate only records the compiler's premise;
its constructor does not establish that an OS sandbox actually exists. See the
concrete provisioning and mediation plan in [transactions-v4.md](transactions-v4.md).

### Session integration obligations

The manager now connects versions to real publication, but its enrolled domain
still closes before the first transaction. A persistent REPL executor must add
resources without invalidating active snapshots or retained named origins; it
must not silently rebuild the manager and lose a prior submission's undo record.
Shell-driven external edits also need an explicit refresh/conflict boundary so
a named rollback cannot overwrite a foreign newer filesystem head unnoticed.

An invocation's internal stores/imports/views now share an `InvocationRoot`, an
exclusively created, identity-checked directory. `SnapshotStore::new_in` retains
that backing lifetime; cleanup refuses live stores/snapshots/views and is explicit
and retryable. `capture_in` omits only the current invocation's directory identity,
not every `.temp` entry: pre-existing user scratch data remains in the source.
`from_entries_in` retains the exclusion proof and rejects private entries even
through ordinary/verbatim or case aliases. Fresh scratch ancestor directories
with no logical children are not invented as source data; configured physical
scratch directories remain available to existing callers after cleanup.

Logical absence and physical ownership are distinct for newly created scratch
ancestors. `InvocationRoot::scaffold_directory` proves their directory identities;
it does not authorize publishing an entire ancestor over live backing stores.
The manager decomposes those logical subtree changes into user-child publications
and directory-metadata receipts, retaining the physical shell through both
logical absence-to-directory publication and origin undo to absence. Existing
user directories do not acquire this capability merely by being named `.temp`.
File replacement of a live owned scaffold remains rejected.

The reference executor enrolls the existing destination namespace when creating
new children. Its invocation constructor normalizes its own scratch-creation
mtime touch in the logical snapshot, and cleanup preserves subsequent logical
parent timestamps. Focused `execution_workspace_v4` journeys exercise actual cwd
copy with default internal scratch, user `.temp` sentinel preservation, containing
namespace origin undo and refusal to delete a foreign replacement. The Windows
workspace target has eleven passing Windows cases, including fresh default
scratch as an ordinary user destination and later cross-submission copy/origin
undo. A native child actually creating `.temp/user-file` has a separate explicitly
ignored journey requiring a provisioned process-resource backend. Cross-submission
copy uses a different parent namespace: changing the same parent from another
origin must still cause the existing strict rollback conflict, not be waived by
the scaffold capability. The ordinary reference executor's nineteen cases and
native Unix validation remain separate.

This ownership representation also enables ordinary cwd-root process views:
the private source mount can be a descendant of the logical root because the
bind resolves its source before overmounting that root. Inherited control/status
FDs remain usable after the invocation's backing pathname is hidden in the child.
Native providers must retain the snapshot's `invocation_for_path` token while a
prepared/running view uses backing data before lifting their initial disjoint-root
restriction; a lexical prefix alone is not that ownership proof.

Deferred endpoint enrollment is a separate proof from a logical file kind.
`PlanningContext::host_unstable` identifies the exact instruction operand whose
host traversal is invalidated by a producer, including operands with a known
future regular-file kind. `Domain::resolve_deferred` retains that operand's raw
absolute spelling and selects only a verified existing directory envelope. It
does not collapse a `..` behind a currently non-directory component, capture the
future source, establish its final alias, or fall back to a volume root. Actual
source start and destination publication must revalidate native traversal after
their typed import dependency gate. Stable operands keep strict validation;
Windows missing-child results are checked against the original ancestor kinds
before lexical normalization, so a file ancestor cannot disappear behind `..`.

Read enrollment and physical publication are separate domains. The original
prototype republished an entire enrolled root after a subtree change. That
was adequate for isolated finite fixtures, but cannot simply be used
for a cwd root containing its own backing workspace: renaming that root moves
the artifacts on which later statements depend, and rewrites untouched siblings.
The manager now tracks minimal dirty subtree replacements separately from
directory-metadata-only writes, with compensating receipts for both under the same
logical guard. A new
file's containing namespace must be enrolled or journaled too if full origin undo
is to restore that directory's mtime. Do not silently exclude all `.temp` data,
move the backing workspace outside the project, or weaken before-state tests to
work around this ownership issue. Program-view exports likewise need an explicit
delta/publication scope rather than assuming a full read view must be swapped.

The default CLI frontend now retains decoded operand provenance and owned source
identities through cross-submission macro expansion, not only call site byte
spans. Compound-buffer tests alone are not a native terminal integration gate;
the default frontend also passes real ConPTY and native WSL cross-input journeys.
The public `ProcessEffects` premise remains the trusted view provider's
obligation; different argv strings or successful samples cannot establish purity.

Per-instruction `OperationFacts` are equally explicit premises. Unknown filesystem
reads stay consuming until a replayable regular file or directory is established;
HTTP remains observable even if metadata reports a finite regular body. Directory
creation/type changes include namespace-metadata writes. A graph may be a valid
conservative scheduling certificate while `is_execution_ready()` is false: type
inspection, selector expansion and the physical stream scheduler are separate
preparation obligations, not facts invented by pure syntax compilation.

### Stateful live execution checkpoint

`execution::live::RuntimeSession` now owns the file engine, transaction origins,
native program views, unique ports and pending asynchronous flows across source
submissions. `execute` joins a complete document, while `submit` preserves an
unconnected program/its stdin for later REPL inputs; `wait`, `cancel_active` and
`shutdown` provide explicit lifetime boundaries. `execute_prepared` and
`submit_prepared` replay the preparation certificate before using its rewritten
HIR. Runtime construction is lazy and an empty submission creates no scratch.

Every native program's stdin/stdout/stderr handles are extracted before waits.
Input and output flows run as separately admitted tasks; two concurrent drains
inside the native process layer prevent unconsumed stderr from blocking stdout
or an exit-code observation. Admission reserves the process layer's six buffers
and two additional buffers per connected flow, with separate process/stream slots.
An over-budget co-start cohort fails rather than waiting behind its own pipe.
Only actual native providers launch programs; there is no unisolated fallback.

At a checkpoint the session closes untaken stdin, joins all IO and native process
trees, seals private filesystem views, and applies their deltas against the exact
initial snapshots. It then stages stdout/stderr/scalar file assignments in source
order before publishing. Output file staging is deferred until after the program
delta so a full-root export cannot erase its own declared output assignment.
Nonzero process status remains replayable UTF-8 data. Cancellation uses native
`seal_cancellable` ownership APIs, not dropping a consuming seal future; cancellation
therefore joins/discards the real view before origin restoration or cleanup.

Focused actual Linux evidence: nine compiler-to-runtime journeys passed on WSL
UID 1000 with project `.temp` privately mounted native tmpfs (128 MiB). These cover
the requested A1/B2-like checkpoints, local/named rollback and PID files; UTF-8/NUL
stdin with 768 KiB stderr before stdout; program ports connected across separate
submissions; cancellation after an observed private output prefix; and default
cwd-backed `.temp` retaining pre-existing user scratch but removing the owned
invocation after shutdown. Test runtime was 0.76 seconds, not a production latency
benchmark. The extended set also exercises declared stdout overriding the same
filename written directly by its child, a two-child 512 KiB pipeline whose
consumer reads the producer's private file followed by named origin rollback,
connecting that pipeline across separate REPL submissions, and rejecting a
candidate at pre-payload admission without cancelling its existing peer. Strict
library/binary/live-target Clippy passed. The signed-driver Windows CI run
`40542243926` at `b91d7fb` also passed all four resource-view and nine program
execution journeys. Its subsequently added workspace scratch-publication gate
exposed a Windows private-box cleanup sharing violation; that separate regression
is not covered by the earlier program passes. New all-phase event/late-domain
changes still require their own integration evidence.

Opaque programs launched within the same owner/epoch now share the actual native
managed filesystem: Linux namespace mounts bind the same backing tree; Windows
workers join the same exclusive box without reseeding it. Payload identities,
ports and output quotas remain independent. Every cohort payload joins before
the first export; subsequent children release their worker without exporting
the tree again. The epoch rejects launches after export, and its final owner
performs private cleanup. Only one baseline-to-final delta is imported. Ordinary
concurrent file races inside this shared epoch have native OS semantics; shared
storage is not a claim that their writes commute. Changed-file disjointness alone
still does not prove independent reads.

Implicit completion selects ready cohorts rather than awaiting transaction IDs
in sequence, so a held external socket does not suppress a later EOF publication.
Independent finite statements can run while external streams remain open; a
statement touching an active managed path fails before effects with an explicit
wait/stop action. Physical EOF establishes a new read-committed publication
statement so unrelated namespace updates do not create stale sibling conflicts.
At quiescent submission boundaries the existing manager refreshes foreign host
bytes/metadata as foreign versions, preserving named origins and preventing a
later named rollback from overwriting an external edit.

The planner must qualify private-view writes versus pinned immutable
stdin sources by version. Applying a whole-root physical RAW dependency to a
program's own immutable stdin snapshot would incorrectly gate its producer behind
process exit. Shared-cohort graph contraction and production admission must use
actual view identities and owner/epoch proofs, not infer shared storage from
argv or a future port connection. These are unified scheduler obligations, not
a reason to waive certificate checks or call the full v0.4 goal complete.

### Submission-owned event execution

The runtime retains an immutable `SubmissionPlan` with each flow and deferred
assignment. A `NodeKey` includes the submission identity; a separate preorder
`PublicationOrder` determines logical receipt order. Runtime owner bindings are
extended without rebinding an already admitted operation. Start, managed-file
publication and region commit consult their distinct ledger gates. Private
cohort import establishes owner-local visibility, not foreign committed data.
The named region cannot finish until its actual child/reset obligations hold.
At actual named-transaction begin, its whole body and declared process owners
are bound before capturing a child plan. Lexical ownership is already known:
an early private stdout drain may depend on a later-admitted finite statement
of that same transaction. Leaving that future node unbound would conservatively
but incorrectly require its final commit, deadlocking the region that owns it.

The ready frontier may select independent work without awaiting an earlier idle
socket. A bounded notification channel is only a wakeup hint. Flow-owned atomic
readiness and native process exit signals survive saturation; service reconciles
unobserved exits into the ledger before waiting. This matters when one shared
child exits while another still waits for its exit-code bytes. An already
observed exit does not keep an otherwise idle REPL polling.
Ready service advances to a ledger fixed point before subscribing for another
wakeup, so a producer committed later in one pass cannot strand a previously
visited dependent whose gate has just become ready.

Bare rollback uses the certified pending-epoch cancellation cut, joins actual
native/stdio work, restores the Manager checkpoint and only then records the
reset. PID scalar capture records admission/readiness independently of later
file publication, just like a private byte drain. `/stop` uses the same typed
owner cancellation path; it does not leave old prerequisites pending forever or
turn cancelled output into a completed receipt. Already committed status remains
replayable, and historical PID data remains meaningful. Cleanup/join failures
retain their causes and paths and prevent a false successful stop.

Actual native Linux regression evidence covers a managed stdout file handed to
a fresh program view, exit-code bytes feeding a still-live peer, cancellation
and reuse after `/stop`, and pending native/stdio rollback followed by a fresh
epoch and eventual named-origin undo. The program suite passed twelve cases in
one run; its thirteenth, the new pending-epoch rollback case, passed separately
after fixing scalar admission (0.50 seconds). These are correctness fixtures on
private project-local tmpfs, not production latency measurements. The latest
uninstrumented workspace native gate passed both cases in 0.48 seconds. In the
late-domain regression a native child
replaces a host file with a private directory; subsequent source, literal and
stdout destinations traverse raw `node/../node/...`, and named undo restores
the original host file. No pre-import host type probe or unchecked lexical
collapse substitutes for the selected transaction view. The current Windows
signed-driver run remains a separate acceptance gate. Performance acceptance
has not started.

### Pending portal intent: single-input opaque spawn and external FIFO

This is a design investigation, not implemented support. A single batch with
an opaque spawn followed by an external FIFO-to-stdin connection currently
marks that FIFO's type host-unstable before the provider has supplied effects.
The deferred directory envelope is then captured as snapshot data and fails on
the live FIFO. Declaring the program in an earlier submission avoids that
bootstrap problem because its actual mediated footprint is already available.

Do not infer safety from cwd or argv strings. Managed roots also include typed
argv envelopes and other declared finite resources. Preparing a frozen view
before preceding literal assignments would be wrong too: the program must see
the version after those logically prior assignments.

A bounded possible extension is a pending physical-portal intent: hold a native
metadata-only FIFO generation observation without declaring a final Stream type
or enrolling it as snapshot payload. Retain every cwd/argv/finite structural
root. At the actual provider preparation/admission boundary, before launching
payload, validate the complete writable filesystem footprint is disjoint and
the held FIFO still matches; only then refine future endpoint facts and physical
co-start gates. An earlier explicit filesystem write overlapping that endpoint
cannot use this shortcut. A cwd, argv or structural root covering the FIFO must
retain the existing snapshot-capability rejection, not silently lose coverage.

Filesystem-shape stability is not byte-IO purity. A focused WSL experiment in a
private mount namespace remounted the fixture directory read-only and still
transferred `byte-through-ro` through its FIFO successfully. Therefore a readonly
host mount alone cannot prove that a native process cannot consume or write the
external channel. The extension must explicitly preserve the external-portal
semantics or require an enforced channel-access policy; it must not invent an
absence of stream-content effects from a filesystem write bound. No persistent
mount or host configuration changed during this probe.
