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

`-O0`, `-O1` and `-O2` select machine-independent optimization. Native AOT
compiles the host executable, while the language runtime interprets typed
binary instructions and scheduled blocks; it does not replay debug source.

`-o -` writes binary output to stdout without help or status text. Redirect
that stream as bytes using your shell's byte-preserving facilities; do not
capture it through a text transcoding pipeline.

Old explicit source/object formats remain under the preserved Rust baseline
through `msync legacy ...` or `MSYNC_LEGACY`. Current skill examples use 0.8.
