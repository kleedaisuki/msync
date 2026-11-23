# Compiler stage commands and resumable checkpoints

Status: implemented. `.mllvm` starts at format 1 with producer contract 1.
The checkpoint is a compiler interchange format, not an executable artifact.

Give each compiler stage a name and an explicit output. The important
addition is a resumable `.mllvm` checkpoint: a developer can inspect the
actual middle-end graph, move it to another process, and continue from the
recorded phase without the original source. The existing one-shot
`compile` workflows remain available and use the same in-memory pipeline.

## Stage commands

| Command | Primary input | Output or action |
| --- | --- | --- |
| `analyze` | `.msync` | Type-check and specialize; write typed HIR as `.mhir`. |
| `lower` | `.mhir` | Produce typed LLVM-derived SSA and its producer facts; write a lowered `.mllvm` checkpoint before middle-end optimization. |
| `optimize` | Lowered `.mllvm` | Run the selected middle-end pipeline and write a post-middle-end `.mllvm` checkpoint. |
| `codegen` | `.mllvm` | Choose physical guest operations, frames, call ABI, and root maps; write relocatable HPIR as `.mso`. |
| `bind` | `.mso` | Capture permitted environment observations and link prepared resources; write bound HPIR as `.mexe`. |
| `run` | `.mexe` | Execute the bound program. Existing source, HIR, and portable-object inputs retain their behavior. |

```sh
msync analyze app.msync
msync lower app.mhir
msync optimize app.mllvm -O2 -o app.opt.mllvm
msync codegen app.opt.mllvm
msync bind app.opt.mso
msync run app.opt.mexe
```

`codegen` generates VM code, not host machine code; x64 JIT remains a
runtime concern. `lower` may also start from source. `codegen` may start
from source or HIR, in which case it runs the missing producer stages with
the same optimization default as `compile`. These are shortcuts through
the same stages, not separate implementations. `bind-ir`, existing
`compile` options and defaults, and all existing binary formats retain
their behavior. A new `--emit` mode is not required for the checkpoint.

Stage commands default to the corresponding extension in the input
directory. `optimize` defaults to `<stem>.opt.mllvm`, so it does not
overwrite the lowered checkpoint. `-o` overrides the destination;
`-o -` writes only the artifact bytes. A stage rejects an output that
would overwrite its input.

## Phase and resume contract

A standalone `.mllvm` records one of two completed boundaries:

- **Lowered:** HIR-to-SSA lowering is complete, and no middle-end
  optimization has run. `optimize` starts here. Passing it straight to
  `codegen` means code generation at O0.
- **Post-middle-end:** the middle-end has completed at the recorded
  optimization level. `codegen` consumes this graph as is. It neither
  repeats the optimizer nor reanalyzes source or HIR.

`optimize -O0` can advance the phase without changing the graph. The
existing target-required O0 canonicalization belongs to `codegen`, not to
lowering or the middle-end checkpoint. To compare optimization levels,
start each run from the same lowered checkpoint. `optimize` does not take
a post-middle-end checkpoint as its input. A conflicting `-O` on
checkpoint-based `codegen` is an error, not permission to re-optimize.

Resuming is an ordinary invocation of the next stage, not an additional
execution engine or a promise to recover from an interrupted pass. The
checkpoint records a completed stage, not a process image or a crash dump.
Version one covers standalone compilation only: no REPL generation,
host handles, environment observations, or VM state. When reading from stdin,
`analyze` accepts source, `lower` accepts HIR, `optimize` and `codegen` accept
checkpoints, and `bind` accepts relocatable HPIR. Use `-o` with stdin.

## What the checkpoint owns

An SSA text listing is not a complete checkpoint. HPIR lowering consumes
[the lowering result](../../src/Msync.Hir/Lowering/LlvmHirLowerer.cs)
and its producer facts, not just `LlvmModule`. The persisted state must
represent both:

| Part | What must survive |
| --- | --- |
| Typed graph | The module, entry, functions, blocks, instructions, types, globals, exact constants, operand edges, and semantically relevant attributes, metadata, and source locations. Include msync-specific operations as first-class records. |
| Producer contracts | The side tables consumed by HPIR lowering, including layouts, native/import identities, capture and callable bindings, Range and iterator shapes, Future/readiness carriers, and source attribution. Preserve the relation between each fact and the graph entity it describes. |
| Header | Checkpoint schema and compatibility version, completed phase, effective optimization level when applicable, and producer/graph context needed to decide whether the next stage can consume it. |

Use deterministic, file-local identifiers for graph entities and
side-table keys. Restore identities and references, including forward
references, before allowing a pass to use the graph. Rebuilding ordinary
graph links such as use lists is part of decoding; recomputing missing
producer facts is not. Do not serialize process addresses, optimizer
worklists, dominance caches, or other transient analyses. Subsequent
passes may construct the analyses they normally need from the restored
graph.

The wire implementation stores module-ordered declarations and instruction
type headers before instruction operands, followed by constants in dependency
order. The reader fixes up forward instruction operands and restores the
original use-list order before running a pass. Producer tables reference the
same decoded IDs; there is no independent layout or call reconstruction.
This follows the staged value/forward-reference approach in
[LLVM bitcode](https://llvm.org/docs/BitCodeFormat.html), without adopting its
format or an LLVM dependency. Like
[GCC's GIMPLE streaming](https://arxiv.org/abs/1010.2196), it carries a compiler
boundary, not source text that would have to be recompiled.
Standard-library layouts use declaration IDs, not randomized immutable-map
iteration, so independent front ends agree on generated field offsets.

Version the checkpoint separately from HIR and HPIR. Decode structurally
before resuming: validate phase, compatibility, lengths, references, and
required producer tables. Reject an incompatible or incomplete checkpoint
with the relevant reason and regeneration guidance; do not fall back to
lowering source or inferring missing facts. Use the existing atomic
artifact publication pattern for files. The ordinary in-memory pipeline
need not encode or decode any checkpoint.

## Inspection and terminal presentation

`disassemble app.mllvm` is the passive view: decode the checkpoint and
render its SSA with the existing
[LLVM-derived writer](../../src/Msync.Core/Llvm/LlvmIrWriter.cs). Show
the saved phase and optimization level. Do not run the optimizer, emit
HPIR, bind resources, or execute anything for inspection. The displayed
text is not an import format or a promise of compatibility with
upstream LLVM tools. `--json` wraps the listing with its phase and level; the
existing editor disassembly/navigation protocol does not consume checkpoints.

Respect the existing color policy. Highlight IR according to its own
syntax, including instructions, types, local values, and global symbols;
do not feed it to the source-language highlighter. Highlight the filename
and artifact kind in the existing `compile` success message and in
equivalent stage messages without changing their plain-text contract.
Automatic color stays off for redirected streams. Highlighting belongs
to presentation only: artifact files and `-o -` never contain ANSI
sequences or status text, even when terminal color is forced.

## Implementation and acceptance

1. Factor the existing compiler entry points at the HIR-to-SSA,
   middle-end, and HPIR boundaries. Keep the normal
   [HPIR entry](../../src/Msync.Compiler/Compiler.Hpir.cs) on that pipeline.
2. Add checkpoint encoding and decoding around the standalone graph
   and its producer tables. Then add the named CLI commands and passive
   inspection; do not change the existing artifact codecs.
3. Extend the existing telemetry with checkpoint encode/decode spans,
   byte and table counts, saved/resumed phase, compatibility rejection
   reasons, and normal stage spans only for work actually performed.
   Attach source/function context where available. No checkpoint work
   belongs on the ordinary recording-disabled compile path.

The release criterion is a detached journey, not just successful
decoding. Compile a source fixture directly to `.mso` and keep it as the
oracle. In another process, save a lowered checkpoint, optimize it, and
save the result. Remove source, HIR, and the lowered checkpoint, then run
`codegen` from the optimized checkpoint in a new process. With identical
options and source identity, the resumed `.mso` must match the direct
artifact byte for byte. Bind and run it against an independent result
oracle. Use one representative program with a captured call and a
producer-specific Range or Future decision so lost side tables cannot
hide behind a scalar-only case.

Check the direct O0 route from a lowered checkpoint, a clear rejection
of a truncated or incompatible checkpoint, and trace evidence that
resumed code generation did not parse, lower, optimize, or bind again.
Verify that terminal color changes only the preview or status message,
not redirected output or artifact bytes.

The maintained process-level journey is
[`tests/HpirPipeline/cli_journey.py`](../../tests/HpirPipeline/cli_journey.py).
Use `--checkpoint-only` to run only this boundary.

Linux x64 Native AOT acceptance covers a captured Range callback, live branch
selection and a file transfer. The detached O2 object and both resumed O0
routes match direct compilation byte for byte. The resumed O2 trace contains
checkpoint decoding and HPIR code generation, with no earlier compilation or
binding phase; the bound program is checked independently on both input arms.
