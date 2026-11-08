# Relocatable and executable artifacts

`.msync` is source, `.mso` is typed relocatable bytecode, and `.mexe` is
context-bound executable bytecode. Both binary stages run without the original
source. Binding freezes path context, not live credentials or filesystem state.

```sh
msync compile job.msync -o job.mso
msync run-ir job.mso
msync bind-ir job.mso -o job.mexe
msync run-ir job.mexe
```

A relocatable `.mso` selects runtime context when loaded. A `.mexe` captures
working-directory, home and optional source-file context during `bind-ir`;
its paths do not silently rebase when the launch directory later changes.
Use `--source-file PATH` when an explicit source-relative context is needed.

Select optimization according to compilation latency and expected reuse:

| Level | Policy |
| --- | --- |
| `-O0` | Keep unoptimized typed bytecode; useful for comparing behavior. |
| `-O1` | Low-latency dataflow cleanup, local redundancy and inlining. |
| `-O2` | Default: global redundancy, finite pure evaluation, call specialization. |
| `-O3` | Allow larger profitable inline and finite-range expansion budgets. |

Machine-independent transformations run on `.mso`. Binding adds context-dependent
lowering and runs the selected optimization level again on `.mexe`. Native AOT
compiles the host executable; the language runtime executes typed binary
instructions and scheduled blocks, never embedded debug source.

`-o -` writes binary output to stdout without help or status text. Redirect
that stream as bytes using your shell's byte-preserving facilities; do not
capture it through a text transcoding pipeline.

Old explicit source/object formats remain under the preserved Rust baseline
through `msync legacy ...` or `MSYNC_LEGACY`. Current skill examples use 0.8.
