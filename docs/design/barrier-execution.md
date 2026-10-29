# Barrier-delimited interpretation (language 0.5)

## Scope and current status

The 0.5 language removes named transactions, isolation modes, checkpoints and
rollback. This is a breaking language change, not a compatibility implementation
of the old transaction model. Ordinary file statements have statement-local
publication/recovery ownership. Processes and unbounded streams do not pretend
to be rollback-safe filesystem transactions.

The CLI and REPL accept only language 0.5. Both 0.3 and 0.4 flags or headers are
rejected with migration guidance: a quoted source is literal bytes, an explicit
`$pwd / "filename"` expression denotes a path, and async/await replaces old
transaction scheduling. Keeping the old executable grammar would also retain
its different publication guarantees; silently accepting its header with the new
quoted-source semantics would be data corruption, not compatibility.

The frontend and HIR implementation described below is present in the worktree.
Integrated runtime, crash recovery, CLI and native-platform validation are tracked
separately in `barrier-validation.md`; this document is not a release acceptance.

## Compilation and interpretation contract

```text
source -> parse -> hygienic typed expansion -> HIR + static block graph
                                                    |
                                   exported object -+
                                                    v
                         interpreter takes ONE next basic block
                                                    |
                           contextualize operands / observe current resources
                                                    |
                              guard runtime specialization / optimize graph
                                                    |
                                    bounded scheduling and execution
                                                    |
                               block completion -> take next block
```

Compilation does not inspect source/destination existence, launch endpoint
helpers, create staging areas, or test optional native capabilities. An exported
object carries executable typed IR and debug information, not filesystem facts.
Lexical context substitution is not a filesystem dynamic linker: observing or
resolving a future file cannot happen before its block is interpreted. A prior
program may have created, renamed or removed that path.

`hir::Program` owns exactly one `Region`; that region owns the instruction vector.
`Program::blocks()` and `Region::blocks()` partition it into borrowed `BasicBlock`
slices. This avoids two mutable representations of instruction order. Block IDs
are derived, not persisted runtime identity claims. Each static dependency,
commutation proof and rewrite candidate belongs to exactly one block. Blocks
themselves are interpreted in source order.

## Syntax and lowering

| Source form | Lowered control |
| --- | --- |
| `p => executable(args)` | before fence, launch `p`, await `p` (also the after boundary) |
| `async await p => executable(args)` | exactly the same control as the bare launch |
| `async p => executable(args)` | before fence, launch `p`; no implicit immediate join |
| `await p` | targeted join of `p` |
| `await` | join all pending owned programs and byte flows |
| `async statement` | before fence, then the statement |
| `await statement` | statement, then after fence |
| `async await statement` | before fence, statement, after fence |
| standalone `async` | before fence only |

The prefix words are independent directional ordering requirements, not a
syntactic pair. A before fence forbids previous work from crossing forward; an
after fence forbids following work from crossing backward. A launch ends its
launch block once the owned process has actually started, not when it exits.
An explicit targeted await occupies a control block of its own. Thus:

```msync
async p => $path::bash("ls")
p::stdout -> stdout.txt
await p
```

has three ordered blocks: launch admission, output flow, and process join.
The child can remain alive between them. Program ports have explicit identities
and ownership, so reading one does not imply an unknown whole-filesystem write.

The HIR operation vocabulary is `Transfer`, `Spawn`, `Await`, and
`Barrier { Before | After }`. `Await { process: None }` means a real join-all,
whereas a generic after fence only orders the associated block; these must not
be conflated.

## Macro lifetime

Lowering tracks the set of pending process identities. On macro entry it records
the inherited set; on successful exit it emits targeted awaits for the still
pending identities introduced by that expansion. Explicit targeted or bare await
removes satisfied identities. Nested expansions satisfy their own obligations,
so outer expansions do not insert duplicate joins. Caller-owned processes are
not accidentally joined merely because a macro was called.

Synthetic joins retain a valid definition span and the macro call-site context.
They must not use a byte span from the caller as a span in the definition source.
All control instructions receive ordinary instruction IDs and debug origins.

## Optimization evidence

Static effects describe logical file/resource access and program-port ownership.
They may establish independence, hazards and conditional overwrite candidates;
they do not prove current object kind, aliasing, input byte immutability, or
successful future IO. Runtime observations and retained inputs are scoped to the
current block. A failed guard selects the original pending instructions, without
repeating completed effects. Neither native launches nor HTTP/socket requests
are duplicated by speculative optimization.

Commutation certificates share one immutable effect footprint per participating
node. The graph lazily allocates that footprint on its first independent pair,
then clones only the `Arc` for later pairs; dense conflicting graphs need no
unused footprint copies. Verification still compares the complete values, not
pointer identity, so copy-on-write certificate tampering remains detectable.

### Namespace effects are not a shared parent-directory lock

A statement publishing `/out/a` and one publishing `/out/b` affect distinct
pathname bindings. Their incidental updates to `/out`'s mtime do not belong to
the ordinary exact-file observation contract and must not create a completion
edge between them. Such an edge can be a real deadlock: a child waiting for an
independent marker cannot exit if its exit-code capture prevents that marker
from being admitted.

The resource model therefore distinguishes `DirectoryEntry(path)` from
`DirectoryEntries(directory)` (the complete immediate name set) and
`DirectoryMetadata(directory)` (explicit attributes). Publication writes its
exact binding. If ancestors might need creation, it additionally writes their
bindings, stopping *before* a positively observed existing parent and never
writing a fictitious root binding. Two creations under a shared missing `/out/new`
still conflict on `/out/new`; independent names under an existing `/out` commute.
Directory-tree capture, enumeration and explicit metadata observations still
conflict with the entry changes they actually observe.

On Windows, positively recognized local named-pipe names use exact channel
identity rather than filesystem-ancestor relationships. ASCII pipe names are
case-insensitive; distinct names and normal drive filesystem paths are disjoint.
Unproved Unicode case aliases and remote UNC pipe aliases remain conservative.

The implementation follows the separation emphasized by MLIR's
[Side Effects & Speculation](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/):
resource effects and whether an operation can safely be speculated are different
questions. File effects alone cannot justify executing or eliminating an
external operation. LLVM's
[convergent operation semantics](https://llvm.org/docs/ConvergentOperations.html)
also illustrate why ordering constraints cannot be inferred only from memory
effects. We use explicit DSL fences rather than importing LLVM's unrelated
convergence semantics or its infrastructure.

## Verification responsibilities

Frontend tests cover bare/explicit launch equivalence, independent prefixes,
three-block launch/flow/join partitioning, macro exit joins, nested expansions,
debug-source spans, transaction-syntax rejection and formatting round trips.
Property tests assert that block partitions preserve every original instruction
exactly once and no proof crosses a block boundary. Runtime acceptance additionally
requires deterministic child gates, current-state file creation/deletion,
bounded parallel independent work, real process completion after controller
failure, and crash-injected statement recovery. Unit tests alone do not establish
those guarantees.

### First integrated frontend execution (2026-10-29)

The root-coordinated selected `cargo test --no-run` batch compiled the library,
binary and test executables successfully. Its executable manifest is
`.temp/barrier-second-executables.json`. Direct Windows test execution, one
process at a time with `--test-threads=1`, produced:

| Target | Result |
| --- | --- |
| `language_v4` | 12 passed |
| `language_barriers_v5` | 7 passed, including the 48-case formatting property |
| `version_v4` | 5 passed |
| `frontend_barriers_v5` | 6 passed |
| `relocation_lowering_v4` | 10 passed |
| `hir_plan_v4` | 26 passed, 1 diagnostic-wording assertion failed |

The graph already rejected duplicate IDs and stream ownership in that failing
case; the failure required its explanation to mention uniqueness and one owner.
The messages were clarified after this build. That source edit requires a new
build and rerun and is not counted as a passing result here. Logs are disposable
`.temp/barrier-frontend-<target>.log`; the durable result and its generation
limitation are recorded here so their eventual cleanup loses no finding.

After that first test build, the legacy CLI executor was removed rather than
maintaining a second, weaker statement-publication owner. `Version::Legacy` was
therefore removed as well; both old mode flags and old source headers now fail
with explicit quote/path and barrier migration guidance. The version tests were
updated for that stronger contract and require rebuilding before their earlier
five-test pass can be applied to the current source.
