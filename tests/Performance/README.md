# Native end-to-end performance comparison

`journeys.py` compares the preserved optimized Rust 0.7.3 CLI with the new Native
AOT 0.8 CLI. It does not compare Rust release against managed/debug C#.

```sh
cargo build --release --locked --manifest-path legacy/Cargo.toml --bin msync --target-dir target
dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release -r linux-x64 -o .temp/performance-v080/aot
python3 tests/Performance/journeys.py \
  --rust target/release/msync --aot .temp/performance-v080/aot/msync
```

The harness freezes both binaries, records versions and SHA-256, and generates
equivalent old/new syntax. The default is two warmups and nine measurements per
image per case, alternating AB/BA order. Cases cover process startup, small and
128-operation checks, ordinary 4 KiB file replacement, and a 256-file tree of
4 KiB files. Transfers are measured both from source and source-free compiled
objects (`.mobj` versus `.mso`). Compilation of those objects is outside timing.
Exact copied bytes, file count, modification times and native Unix modes are
checked outside timing. There is no page-cache eviction, so this is a warm-cache
interactive-use comparison, not storage bandwidth or WAN performance.

Use `--case NAME` to rerun only the contract affected by a change. Avoid building
or running another benchmark while collecting measurements. Retain raw results
under the repository-root `.temp`; reusable conclusions belong in
[`docs/design/performance-v080.md`](../../docs/design/performance-v080.md).
No benchmark thresholds are CI quality gates.

For a controlled optimization within 0.8, replace `--rust OLD` with
`--previous-aot BEFORE` and keep `--aot AFTER`. Both images then use the same
0.8 source and `.mso` format; only the changed runtime contract needs measuring.
