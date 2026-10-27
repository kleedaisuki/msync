# v0.4 structured compiler and execution architecture

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
Destination and alias expression contexts retain path interpretation where the
language requires it. The old parser remains available to callers requiring
legacy quoted-operand semantics; the v0.4 frontend must not silently erase the
distinction by storing only the decoded strings.

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
| Typed IR and proofs | `hir`, `hir::{effects, graph}`; transaction regions, process events, unknown-effect barriers and provider-bound refinement | 20 targeted cases; independent small-state model enumerates admitted schedules |
| Atomic frontend submission | `pipeline::compile`; parse, candidate expansion, effect obligations, then lexical-state commit | Three integration cases, including proof failure without leaked bindings or IDs |
| Version visibility | `transaction::Store<V>`; immutable references, four visibility policies, atomic logical checkpoints, retained origin | 17 cases including write skew, absence, directory phantoms and rollback properties |
| Filesystem publication | `transaction::filesystem`; independent disk artifacts, tree manifests and retained compensating publication journals | Separate real-filesystem target; not an OS-wide atomicity or crash-recovery claim |
| Process ownership | `process`; exact argv, concurrent bounded-memory output drains, replayable scalar ports, Job/group ownership | Separate actual-child target; not a transaction sandbox |

Local integration runs are restricted to affected targets plus formatting and
strict library/changed-target Clippy. The CI selector in `scripts/ci-select.ps1`
maps component edits to meaningful test targets and keeps broad compatibility
coverage for shared transport, dependency, or workflow changes. Design-only edits
do not start Cargo jobs. The seven selector checks distinguish these cases.

Remaining delivery obligations are the filesystem-backed execution manager,
verified Linux/Windows program-resource views, runtime coordination of overlapping
program pipelines, CLI/REPL block accumulation and diagnostics, end-to-end
transaction journeys, and then measured latency/concurrency optimization. A
provider-name/view-ID effect certificate only records the compiler's premise;
its constructor does not establish that an OS sandbox actually exists. See the
concrete provisioning and mediation plan in [transactions-v4.md](transactions-v4.md).
