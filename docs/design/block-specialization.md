# Block-local runtime specialization

## Boundary and ownership

Version 0.5 removes user transactions and whole-document resource-view admission.
The compiler produces typed operations and directional barriers without inspecting
files. `Region::blocks()` is the sole partition definition. The interpreter must
complete one block before admitting the next block for contextual evaluation.

Preparation is deliberately smaller than execution: it observes endpoint kinds,
resolves the currently admitted PATH operand, and builds candidate graph rewrites.
It does not execute a source-producing operation, acquire immutable source bytes,
or prove that an observation remains true until publication.

`preparation::prepare` rejects multiple blocks before constructing observations.
`prepare_block` accepts the currently admitted borrowed block. A dependent source
created by an earlier transfer in that block is inferred from its typed
postcondition rather than rejected solely because it does not exist yet.
`resolve_instruction` creates a fresh observation set for one dependency-ready
operation. A program-generated file can therefore appear, and a subsequently
deleted file can fail, at the correct interpretation point.

## Evidence and invalidation

| Evidence | Permitted use | Does not prove |
| --- | --- | --- |
| Pure typed path / literal | Dependency candidate and operand role | Existence, physical alias freedom |
| Current metadata | Current kind and candidate rewrite premises | Future kind or immutable bytes |
| Earlier finite transfer postcondition | Infer produced file kind within this block | That an external actor cannot change it |
| Open source snapshot / publication lease | Runtime-owned bytes or identity under its actual contract | Arbitrary external program effects |
| Async external process | Retained process/port identity | Bounded filesystem effect set |
| Awaited process | Process has exited and outputs can settle | That any old pre-spawn metadata is current |

`Spawn` invalidates old host facts. Known process IDs only establish port
availability: even a still-running explicitly async child does not forbid fresh
current-block metadata observations or erase existing-parent frontiers. Those
observations guide guarded scheduling, not an isolation guarantee against an
unmanaged concurrent writer. Explicit pending finite-write footprints still
invalidate their overlapping properties. No synthetic sandbox certificate is
required. All non-transfer operations end the optimizer's candidate run, so dead
write elimination, forwarding and creation coalescing cannot cross barriers,
process launch or await. A runtime-owned guarded overwrite run still rechecks raw
traversal, aliases and destination publication properties at dispatch. Failed
proofs execute the original pending operations; already completed prefixes are
never replayed.

The rewrite certificate compares barrier direction and await target exactly.
Changing either is a semantic change, not display-only metadata.

## Prior work informing this choice

[MLIR Side Effects & Speculation](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
separates effect descriptions from permission to speculate. We apply that
separation to filesystem observations: a logical read/write graph is not a
license to pre-execute an operation that may block, fail, or communicate.
[LLVM Language Reference](https://www.llvm.org/docs/LangRef.html#volatile-memory-accesses)
likewise makes ordering an explicit contract rather than a scheduling accident.
Our barriers are language execution boundaries, not CPU memory fences and not
LLVM volatile annotations.

## Validation

`tests/block_specialization_v5.rs` covers pre-inspection rejection of a
multi-block module, current dispatch observing file creation and deletion, and
same-block producer type inference without executing the producer.
`preparation::certificate` unit tests reject changed barrier direction and await
target. Cargo execution is owned by the root integration window; source-only
review and rustfmt are not reported as passing execution tests.

The root GEN3 test build was exercised directly on Windows on 2026-10-29,
sequentially with `--test-threads=1`, without an additional Cargo invocation:

| Executable target | Result | Time | SHA-256 |
| --- | --- | --- | --- |
| `preparation_v4` | 35 passed | 0.71 s | `A14EDD15FCB028E20AA87E89DEB5C0DCB48BDDF6E5E4FF2B6D06D65AC10F6A13` |
| `planner_optimization_v4` | 8 passed | 0.99 s | `42E3B6816340CEE162751690EC9F000BFF98798DD357B34496738B66DD061832` |
| `hir_plan_v4` | 27 passed | 0.02 s | `BE5EE3A0ED83E4645652E10E827397661DC09747A8ABD6362CF1E943110D2BAB` |

These retain finite assignment and independent schedule model properties, source
version pinning, readonly failure preservation, raw `..` traversal obligations,
non-replayable HTTP GET effects, FIFO admission ordering, and an actual optimized
missing-target assignment with final metadata. The old preparation suite's
transaction undo and sandbox-private-write expectations were removed or replaced
with current-block/finite pending-effect assertions. This gate does not establish
crash recovery or external-process lifecycle correctness; those have separate
runtime/storage owners. New block-specialization and certificate targets require
the root's own selected test evidence rather than being inferred from this table.

### Consuming endpoint inspection invariant

A Windows named pipe is a typed channel namespace, not a file under a directory
called `pipe`. Preparation recognizes `\\.\pipe\...` without filesystem ancestor
inspection, canonicalization or an identity handle. Named pipe operands are not
included in native/WSL filesystem-separation probes. All observed stream classes,
including Unix FIFOs and sockets, use purely logical planner resources; the
planner must not admit a peer connection merely to obtain an alias hint.

The Windows-only regression in `block_specialization_v5` creates two real server
instances, prepares both source and destination pipe operands, checks that neither
server has accepted any connection, then connects two actual clients. This
separates metadata preparation from physical endpoint admission. At source-change
time the test is authored but not yet executed; root integration owns the next
build and runtime gate.

### Fresh observations while a process is known

`block_specialization_v5` now includes a known-ID parent-frontier test and a real
child handshake test. The latter observes a file while the child is genuinely
running, releases it to self-exit, reuses the known ID, then deletes the file and
checks that re-dispatch rejects stale type facts. These tests target the former
MVCC-era mistake of treating every retained process ID as a permanent global
unknown-filesystem premise. New directory-entry and entry-set footprints remain
part of finite pending-write invalidation. Execution of these additions is
pending the root's next selected build; prior GEN3 results predate this change.
