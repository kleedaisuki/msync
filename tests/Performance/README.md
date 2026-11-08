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

To compare legacy Rust, the accepted C# product and a new candidate in the same
measurement window, add `--control-aot CURRENT_CS` beside `--rust RUST --aot NEW`.
The optional three-image mode rotates `ABC/BCA/CAB/CBA/ACB/BAC`, balancing every
image's position and both order directions. The default two-image AB/BA behavior
is unchanged. Source/object preparation still uses each image's real syntax and
artifact format; exact-byte and metadata checks run independently for all three.

For actual CLI optimization-level decisions, use the same native binary for all
three roles with `--previous-aot-level 1 --control-aot-level 2 --aot-level 3`.
The driver passes each recorded level directly to the native product during
source/object preparation and timed commands; there is no script-wrapper launch
inside the measured interval. Existing calls without these options retain the
product's default. Component phase and residual-IR comparisons remain separate
from these whole-process source/object/bound timings.

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

## First source-file read

Publish `tests/Performance/Components/Msync.Components.csproj` once with Native
AOT on the target OS, then run:

```sh
python3 tests/Performance/source_read.py --binary /absolute/path/to/msync-components \
  --samples 31 --warmups 3 --output .temp/performance-v080/source-read.json
```

The same executable has synchronous and asynchronous first-read modes. Each
timed sample launches a fresh process; 4 KiB and 64 KiB valid UTF-8 fixtures
contain Chinese and astral characters. Both modes print the actual UTF-16 count.
Full decoded text equality and SHA-256 are checked separately, outside timing.
This isolates first BCL read/worker/IOCP costs without compiler or configuration
work. It does not claim to measure physical cold-disk latency.
The optional CLI cases `--case check-source-4k --case check-source-64k` then
measure the full product pipeline with exact-size Unicode-comment sources,
separately from the same-image primitive probe.

## Native compiler preference experiment

`aot_preferences.py --ref COMMIT --measure` snapshots only the product build
inputs from an exact Git commit under root `.temp`, pins `SourceRevisionId`,
publishes the CLI project with default blended / `Size` / `Speed` preferences,
then runs matched 31-pair startup, small check, 1,024-transfer check and file-copy
controls. Runtime IDs and NuGet locks remain explicit; diagnostics are not
stripped, and the source checkout/product settings are never changed.
This is an optional mechanism study, not a release prerequisite or CI gate.

## Typed editor response comparison

`editor_latency.py` compares two frozen native payloads on the same execution
host. Build both revisions with the same pinned SDK/toolchain for a causal
optimization comparison; a different runner's image is a product control, not
an interchangeable source-only baseline.

```sh
python3 tests/Performance/editor_latency.py \
  --baseline /absolute/path/to/before/msync \
  --candidate /absolute/path/to/after/msync \
  --baseline-source BEFORE_REVISION --candidate-source AFTER_REVISION \
  --samples 7 --case realistic --case flat128 --case flat4000 \
  --output .temp/editor-latency-comparison/paired.json
```

Each sample creates an actual stdio LSP process. Baseline/candidate order is
randomized within each pair, and complete adjacent payloads are frozen and
fingerprinted. The realistic case includes a first-ever canonical standard
library query, typed functions/Scopes, repeated tokens, selected completion
details, shadows and close/reopen isolation, without executing source effects.
The flat cases retain the 128/4,000-binding edit-burst workload and negotiate
documentation resolution like the editor. Selected-item resolution is timed
separately from the initial candidate response. Legacy clients without the
capability remain covered by `lsp_response_journey.py`.

Reports retain raw samples, p50/p95, payload sizes, source labels and the last
active case if a later observation fails. With seven samples, p95 is a
descriptive interpolated tail observation, not a confidence bound or a quality
threshold. Use `--case` to avoid rerunning unaffected workloads. Do not run an
editor host, compiler build or another benchmark concurrently with measurements.

The verification scripts use fully typed protocol helpers in the end-to-end
directory; static checking the cross-directory runner uses
`MYPYPATH=tests/Msync.EndToEnd mypy --strict tests/Performance/editor_latency.py`.

## Immutable type-value operations

The component image includes a focused compatibility/property check and an
optional same-image comparison with the previous type-value algorithms:

```sh
dotnet run --project tests/Performance/Components/Msync.Components.csproj -c Release -- --check-type-values
/path/to/native/msync-components --check-type-values --benchmark-type-values > .temp/type-values-native.json
```

The check covers the complete 16-bit Kind formatting domain, recursive Range
contents, null admission/removal, union structure and input admission. The
benchmark uses mixed ordinary scalar types, nine warmed 20,000-operation samples,
and reports all samples plus allocated bytes. Old and current algorithms execute
in the same Native AOT image; this isolates primitive formatting/no-op allocation
costs, not complete editor or CLI latency. JSON goes to stdout and the check's
status goes to stderr. No performance threshold is an acceptance gate.
