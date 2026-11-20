# Save and run compiled programs

`.msync` is source, `.mso` is typed relocatable native IR, and `.mexe` is
contextual executable IR. Both binary stages can be submitted without the
original source. Binding prepares concrete I/O graphs from the selected context;
it never serializes live credentials.

```sh
msync compile job.msync
msync run job.mso
msync compile job.msync --prepare
msync run job.mexe
msync disassemble job.mexe
```

A relocatable `.mso` selects runtime context when loaded. A `.mexe` captures
working-directory, home and optional source-file context during preparation;
its paths do not silently rebase when the launch directory later changes.
Use `--source-file PATH` when an explicit source-relative context is needed.

Contextual preparation discovers filesystem kinds, expands known path selections,
and removes unobserved writes replaced by later writes. A `.mexe` records those
concrete operations and dependencies. Its observations may cease to match the
environment immediately; the artifact makes no validity or repeatability
promise. The VM attempts the operations it receives and reports their actual
failures. It does not compare environment snapshots, check plan freshness or
automatically rebuild an expired plan. Repeating `run` on a `.mexe` retries the same
prepared operations; compile with `--prepare` again to refresh selections.

Select optimization according to compilation latency and expected reuse:

| Level | Policy |
| --- | --- |
| `-O0` | Keep unoptimized typed native IR; useful for comparing behavior. |
| `-O1` | Local dataflow/dead-memory cleanup, last-use moves, proved allocation placement, CFG compaction and typed temporary-slot allocation. |
| `-O2` | Default: also propagate must-facts across blocks and inline proved transparent static calls; callee budget 48 operations, cumulative growth budget 384 operations per caller. |
| `-O3` | Same proof obligations, with callee budget 144 and cumulative caller growth budget 1536 operations. |

These budgets choose implementation cost, not accepted language features or
weaker semantics. Calls with opaque work ownership, iterator cleanup or unsafe
capture/frame-location proofs remain ordinary typed calls. Temporary-slot
allocation shares nonoverlapping storage of exactly the same static type; it is
not hardware register allocation and preserves canonical/session location IDs.
Higher budgets are not an unmeasured universal speedup claim.

Machine-independent transformations run before `.mso` emission. Binding adds
context-dependent computation and prepares I/O execution graphs without replaying
source or invoking the compiler's optimizer from the VM.
Native AOT
compiles the host executable; the language runtime executes typed binary
instructions and scheduled effects, never embedded debug source.

## Inspect without executing

`msync disassemble FILE.mso` and `msync disassemble FILE.mexe` reconstruct a
read-only typed IR document from the binary file. The view includes the actual
format and language versions, optimization level, functions, captures, local
slots, constants, control-flow blocks and retained source spans. Bound artifacts
also show their captured path context and execution graphs: concrete transfer
kinds, resource reads/writes, predecessor edges, batches and eliminated writes.
No code is run or rebound, and no SSH/WSL
connection is opened. The command does not load configuration or telemetry.

Use `--json` for the versioned editor document model, or `-` for binary stdin.
The original source may have been optimized away; disassembly does not invent
missing expressions or require the source file to remain available.

`-o -` writes binary output to stdout without help or status text. Redirect
that stream as bytes using your shell's byte-preserving facilities; do not
capture it through a text transcoding pipeline.

`msync plan SOURCE.msync` and `msync plan FILE.mso` perform contextual preparation
before printing the graph, without executing transfer writes. Planning may read
the selected filesystem. `msync plan FILE.mexe` prints its already prepared
operations without refreshing them. Use `disassemble` for passive inspection.

Current commands use Native IR format 2 for `.mso` and `.mexe`, and HIR format
4 for `.mhir`. Recompile older artifacts from source. Language semantics and
embedded stdlib URIs remain versioned at 0.8.0.
