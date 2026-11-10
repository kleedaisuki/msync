# Relocatable and executable artifacts

`.msync` is source, `.mso` is typed relocatable bytecode, and `.mexe` is
contextual executable IR. Both binary stages can be submitted without the
original source. Binding prepares concrete I/O graphs from the selected context;
it never serializes live credentials.

```sh
msync compile job.msync -o job.mso
msync run-ir job.mso
msync bind-ir job.mso -o job.mexe
msync run-ir job.mexe
msync disassemble job.mexe
```

A relocatable `.mso` selects runtime context when loaded. A `.mexe` captures
working-directory, home and optional source-file context during `bind-ir`;
its paths do not silently rebase when the launch directory later changes.
Use `--source-file PATH` when an explicit source-relative context is needed.

Contextual preparation discovers filesystem kinds, expands known path selections,
and removes unobserved writes replaced by later writes. A `.mexe` records those
concrete operations and dependencies. Its observations may cease to match the
environment immediately; the artifact makes no validity or repeatability
promise. The VM attempts the operations it receives and reports their actual
failures. It does not compare environment snapshots, check plan freshness or
automatically rebuild an expired plan. Repeating `run-ir` retries the same
prepared operations; use `bind-ir` again to prepare a different selection.

Select optimization according to compilation latency and expected reuse:

| Level | Policy |
| --- | --- |
| `-O0` | Keep unoptimized typed bytecode; useful for comparing behavior. |
| `-O1` | Low-latency dataflow cleanup, local redundancy and inlining. |
| `-O2` | Default: global redundancy, finite pure evaluation, call specialization. |
| `-O3` | Allow larger profitable inline and finite-range expansion budgets. |

Machine-independent transformations run on `.mso`. Binding adds context-dependent
lowering, runs the selected optimization level and prepares I/O execution graphs.
Native AOT
compiles the host executable; the language runtime executes typed binary
instructions and scheduled blocks, never embedded debug source.

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

Current commands accept the 0.8 language and format-5 bytecode. Earlier source
and object formats are historical releases, not inputs to this implementation.
