# Native end-to-end performance comparison

`journeys.py` compares the preserved optimized Rust 0.7.3 CLI with the new Native
AOT 0.8 CLI. It does not compare Rust release against managed/debug C#.

```sh
cargo build --release --locked --manifest-path legacy/Cargo.toml --bin msync --target-dir target
dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release -r linux-x64 -o .temp/performance-v080/aot
python3 tests/Performance/journeys.py \
  --rust target/release/msync --aot .temp/performance-v080/aot/msync
```

The harness freezes both binaries and their adjacent native dependencies, records
versions and SHA-256, and generates equivalent old/new syntax. The default is
three warmups and 31 measurements per image per case, alternating AB/BA order.
It reports p50, linearly interpolated p95, all raw samples and min/max. Cases cover process startup, small and
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

The CLI report is checkpointed after each completed case, outside timing. If a
later native operation fails, the JSON retains all earlier raw samples plus the
failed command, cwd, exit status, full stderr and partial samples for that case.
The report's `status` is `running`, `complete` or `failed`; a failed run does not
erase accepted observations or turn a partial distribution into a completed one.

The same command runs on native Windows with optimized `msync.exe` images and
Python 3.11+. Unix modes are checked only on Unix; byte counts, exact payloads and
native modification timestamps are checked on both. `--files` selects the tree
size and `--bytes` its member size. `--file-bytes` independently selects the single
file size; `--statements` selects the batch-check source size. Use `--case` to
avoid repeating unaffected work when changing one component.

To expose retained-local/function costs rather than only zero-local transfer
lists, select `--case check-values`, `values-source`, `values-object` or
`values-bound` with `--statements 1024`. Both versions define a generic identity
function and 1,024 bindings, then publish the last String as actual file bytes.
The old version uses symbol/typed aliases and its own object/contextual IR;
0.8 uses typed functions, `.mso` and `.mexe`. The default seven cases are unchanged.
These optional cases construct their artifacts once outside measured intervals.

For a controlled optimization within 0.8, replace `--rust OLD` with
`--previous-aot BEFORE` and keep `--aot AFTER`. Both images then use the same
0.8 source and `.mso` format; only the changed runtime contract needs measuring.

## Persistent interactive requests

```sh
python3 tests/Performance/interactive.py \
  --rust /absolute/path/to/legacy-release/msync \
  --aot /absolute/path/to/native/msync --samples 31 --warmups 3
```

This portable harness measures native stdio LSP initialization, steady hover and
semantic-token requests over 128 real bindings, plus persistent REPL literal
overwrites with exact output bytes checked after each response. Both language
versions use their actual syntax and protocol responses. The 0.8-only scalar REPL
response is measured separately, not represented as a capability of 0.7.
Only LSP initialization includes process creation; other requests use live
sessions. Native stdout is parsed as the real protocol, never a timing stub.
Use `--case`, `--bindings` and `--previous-aot` for affected-contract comparisons.
