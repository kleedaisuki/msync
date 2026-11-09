# Native end-to-end performance comparison

`journeys.py` compares frozen Native AOT 0.8 images on the same execution host.
Both images use the current language and binary formats; no Rust toolchain or
historical-language checkout is required.

```sh
dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release -r linux-x64 -o .temp/performance-v080/after
python3 tests/Performance/journeys.py \
  --previous-aot /absolute/path/to/before/msync \
  --aot .temp/performance-v080/after/msync
```

The harness freezes each binary with its adjacent native dependencies, records
versions and SHA-256, and prepares the same source for each image. The default
is three warmups and 31 measurements per image per case in alternating AB/BA
order. Reports retain p50, interpolated p95, all raw samples and min/max. Cases
cover process startup, small and 128-operation checks, ordinary 4 KiB file
replacement, and a 256-file tree of 4 KiB files. Transfers run from source and
source-free `.mso` artifacts; artifact preparation is outside timing.

Exact copied bytes, file count, modification times and native Unix modes are
checked outside timing. There is no page-cache eviction, so the comparison
measures warm-cache interactive use, not storage bandwidth or WAN performance.
Use `--case NAME` to measure only changed contracts and avoid concurrent builds
or benchmarks. Keep raw results under repository-root `.temp` and reusable
findings in [the performance record](../../docs/design/performance-v080.md).
Timing thresholds are not CI quality gates.

The CLI report checkpoints completed cases outside timing. A later failure
retains earlier samples, the failing command and cwd, exit status, full stderr,
and partial samples. Its status is `running`, `complete` or `failed`; failure
does not erase earlier observations or imply complete acceptance.

The same command runs on Windows with Native `msync.exe` images. Unix modes are
checked only on Unix; exact bytes and native modification timestamps are checked
on both. `--files` sets the tree size, `--bytes` its member size, `--file-bytes`
the independent ordinary-file size, and `--statements` the batch source size.

For retained-local and function costs, select `check-values`, `values-source`,
`values-object` or `values-bound` with `--statements 1024`. Each image evaluates
one generic function over 1,024 bindings and publishes the final String as file
bytes. These cases use `.msync`, `.mso` and `.mexe`, with preparation outside
measured intervals. The default seven cases remain focused on the base CLI and
copy journeys.

Add `--control-aot CURRENT` for a three-image before/control/candidate comparison.
The order rotates `ABC/BCA/CAB/CBA/ACB/BAC`, balancing both direction and position;
bytes and metadata are verified separately for every image. To compare
optimization levels, use the same binary in all three roles and select
`--previous-aot-level 1 --control-aot-level 2 --aot-level 3`. Each recorded level
is passed directly to the product, without a script wrapper inside timing.
Component and residual-IR measurements remain distinct from whole-process
source/object/bound timings.

## Persistent interactive requests

```sh
python3 tests/Performance/interactive.py \
  --previous-aot /absolute/path/to/before/msync \
  --aot /absolute/path/to/after/msync --samples 31 --warmups 3
```

This portable harness measures native stdio LSP initialization, steady hover and
semantic-token requests over 128 bindings, plus persistent REPL literal copies
and scalar responses. Actual output bytes are checked after every response.
Only LSP initialization includes process creation; the other requests use live
sessions and parse the real protocol. Use `--case` and `--bindings` to select the
affected request contract.

Historical Rust-versus-C# observations remain in the dated design records.
They are evidence about those original images, not a requirement to keep the
retired implementation or a second source-language path in the current runner.

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
The flat cases retain the 128/4,000-binding edit-burst workload; `--case flat80
--case flat400` selects ordinary and moderate documents for the Typed HIR
comparison. Defaults are unchanged. All negotiate documentation resolution like
the editor. Selected-item resolution is timed
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
