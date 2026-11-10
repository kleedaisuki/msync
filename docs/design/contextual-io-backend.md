# Contextual I/O backend and execution plans

## Contract (2026-11-10)

This work replaces the assumption that executable artifacts are reusable
environment-independent programs. `.mso` retains the machine-independent
program. `.mexe` is an inspectable, resubmittable carrier for a contextualized
execution plan. Its observations may cease to describe the environment at any
time; repeat submission is another attempt to execute its recorded operations.

The contextual backend computes known values and control flow, observes selected
filesystem/endpoint state, expands finite selections, builds effect dependencies,
eliminates dead work and forms executable batches. An opaque external call ends
the region whose effects can currently be computed; continuation inputs describe
what must become available before the next region can be contextualized.

The VM executes received operations and dependencies. It owns parallel execution,
admission, processes, transport lifetimes, cancellation and concrete errors. It
does **not** revalidate the whole plan, compare environment snapshots, classify
plans as stale, automatically replan, or fall back to another program because
an observation changed. Reading/decoding an artifact is not an environment
validity check. Native operations perform the work requested and report actual
failures with their source operation, endpoint and paths.

This separation is the user's explicit correction to the initial proposal of
"validating at submission". It also supersedes earlier design statements that
prohibited all filesystem observations in executable artifacts. There is no
promise to preserve older IR encodings, and no permission to rewrite published
release assets or tags.

## Observable work and dependencies

The motivating case is two discarded transfers to the same exact file:

```msync
~/AGENTS.md -> ~/.codex/AGENTS.md;
~/.temp/AGENTS.md -> ~/.codex/AGENTS.md;
```

When no operation observes the first result or destination between these writes,
the first destination version is dead. Its file transfer disappears, not merely
its scheduling annotation. An eliminated operation does not run and therefore
does not contribute an operational error or transfer count. Operand computations
with separately observable effects are not implicitly discarded with it.

Relevant observations include a demanded transfer Bool, a read of the written
file or affected namespace, an escaping result, and an opaque external effect.
Overlapping directory and alias resources require their actual semantics:
copying two directory trees into one destination is not necessarily complete
overwrite, because the first tree may contribute surviving members. Expand known
members and eliminate overwritten leaf versions, or retain the still-needed
subtree work. Do not treat every same-spelled directory write as replacement.

Model read-after-write, write-after-read and write-after-write edges independently
of execution order. Last-writer elimination operates on destination versions and
their observers. Independent live nodes may reorder and run concurrently; dead
nodes and their exclusively dead edges disappear before scheduling. A batch is
an execution grouping of live graph work, not a new transactional guarantee.

Scope/cell mutation remains under the scalar/control owner. Capturing transfer
operands fixes the values consumed by a task; it does not permit worker tasks to
execute arbitrary mutable source-language callbacks concurrently.

## Directional barriers

The source spellings are `acquire` and `release` (including the user's informal
`aquire` spelling when describing the intent). They are directional scheduling
constraints, not unconditional bidirectional epoch boundaries:

| Marker | Forbidden movement | Movement not forbidden by the marker alone |
| --- | --- | --- |
| acquire | An earlier operation moving after it | A later independent operation moving before it |
| release | A later operation moving before it | An earlier independent operation moving after it |

Data/resource/result dependencies can still forbid the otherwise permitted
movement. Opaque external calls are distinct knowledge/effect boundaries.
Neither a normal arithmetic operation nor every transfer needs its own effect
block. Block coalescing and batching must consume these directional edges rather
than turning both markers into global drains.

## Transport and normal-path cost

Selected finite remote work owns its authentication and channel lifetime. Reuse
the native OpenSSH client's configured aliases, agents and prompts. Do not open
connections for unselected branches, or authenticate separately for the metadata
and payload of one finite operation. Independent batches can share an owned
connection without making remote namespace knowledge globally valid.

Local publication keeps same-directory staging and atomic replacement, with at
most one file fsync. Cohort failure may leave completed files and supports retry;
there is no journal, rollback registry or recovery scan. Grouping small files
should amortize planning, transport and scheduling rather than introduce a
buffer, Task and global dependency scan for every bytecode instruction.

## Engineering and research basis

* [LLVM MemorySSA](https://www.llvm.org/docs/MemorySSA.html) provides the useful
  distinction between resource definitions, uses and reaching clobbers. The
  adaptation here is filesystem namespace/content versions, not pointer memory
  and not a dependency on LLVM itself.
* [MLIR effects and speculation](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
  separates effect ordering from permission to speculate an operation. Endpoint
  discovery/authentication must not be executed just because a graph can express
  its dependency.
* [MLIR async tokens](https://mlir.llvm.org/docs/Dialects/AsyncDialect/)
  distinguish completion dependencies from returned values. The VM needs both
  for demanded Bool results and independent finite I/O.
* [OpenSSH configuration](https://man.openbsd.org/ssh_config) supplies native
  connection multiplexing through ControlMaster/ControlPersist. Ownership and
  cleanup must distinguish msync-created resources from user-owned masters.
* [Diffuse (ASPLOS 2025)](https://research.nvidia.com/publication/2025-03_composing-distributed-computations-through-task-and-kernel-fusion)
  uses an intermediate representation to analyze and fuse distributed tasks
  across library boundaries. The applicable mechanism is an explicit dependency
  graph before task fusion, not its GPU/JIT implementation or its reported
  speedups. For msync, amortize per-file admission/channel overhead without
  collapsing an independent frontier into a single serial worker. VM-wide job
  and buffer budgets still apply inside batches; nested per-batch parallelism
  must not multiply the configured concurrency by the number of batches.

Implementation and acceptance results are recorded below as they become
available. The contract above is not evidence that an optimization is already
on the default execution path.
