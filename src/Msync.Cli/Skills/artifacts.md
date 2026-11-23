# Save and run compiled programs

`.msync` is source, `.mso` is relocatable HPIR, and `.mexe` is
context-bound executable HPIR. Both binary stages can be submitted without the
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

Optimization defaults to `-O2`. `-O0` skips the LLVM-derived middle-end
passes. HPIR code generation still canonicalizes local storage where
required. The current HPIR compiler runs the same middle-end passes at
`-O1`, `-O2`, and `-O3`; it records the requested level.

Machine-independent transformations run before `.mso` emission. Binding adds
context-dependent computation and prepares I/O execution graphs without replaying
source or invoking the compiler's optimizer from the VM.
Native AOT
compiles the host executable; the language runtime executes typed binary
instructions and scheduled effects, never embedded debug source.

## Stop and resume compiler stages

```sh
msync analyze job.msync
msync lower job.mhir
msync optimize job.mllvm -O2
msync disassemble job.opt.mllvm
msync codegen job.opt.mllvm
msync bind job.opt.mso
msync run job.opt.mexe
```

A `.mllvm` checkpoint contains lowered SSA and its producer facts. An
optimized checkpoint resumes at its recorded level without rerunning the
middle end. Passing a lowered checkpoint straight to `codegen` means O0.
Use `disassemble` to view its SSA; formatted text is not a compiler input.

## Inspect without executing

`msync disassemble FILE.mso` and `msync disassemble FILE.mexe` reconstruct a
read-only typed IR document from the binary file. The view includes the actual
format and language versions, optimization level, functions, captures, local
slots, constants, control-flow blocks and retained source spans. Bound artifacts
also show their captured path context and execution graphs: concrete transfer
kinds, resource reads/writes, predecessor edges, batches and eliminated writes.
No code is run or rebound, and no SSH/WSL
connection is opened. Inspection does not load project configuration.
Checkpoint inspection can opt into telemetry explicitly.

Use `--json` for structured output, or `-` for binary stdin.
The original source may have been optimized away; disassembly does not invent
missing expressions or require the source file to remain available.

`-o -` writes binary output to stdout without help or status text. Redirect
that stream as bytes using your shell's byte-preserving facilities; do not
capture it through a text transcoding pipeline.

`msync plan SOURCE.msync` and `msync plan FILE.mso` perform contextual preparation
before printing the graph, without executing transfer writes. Planning may read
the selected filesystem. `msync plan FILE.mexe` prints its already prepared
operations without refreshing them. Use `disassemble` for passive inspection.

Current commands use HPIR format 8 for `.mso` and `.mexe`, HIR format 4 for
`.mhir`, and checkpoint format 1 for `.mllvm`. Recompile older artifacts from source. Language semantics and
embedded stdlib URIs remain versioned at 0.8.0.
