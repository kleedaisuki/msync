# 0.8 native user-journey performance

## Question and workload

The C# rewrite changes frontend, artifact format and host runtime together.
Performance acceptance must therefore compare actual user operations with
equivalent effects, not interpreter-op microbenchmarks or managed/debug code
against optimized Rust. The relevant first questions are CLI latency for small
scripts and warm-cache ordinary file/tree transfers. SSH latency and very large
files require separate evidence and are not inferred from this workload.

[`tests/Performance/journeys.py`](../../tests/Performance/journeys.py) generates
equivalent 0.7/0.8 scripts and source-free objects, freezes both executable images,
records their versions/hashes, and alternates AB/BA process invocations. It checks
bytes and native file metadata outside each timed interval. The maintained
[normal-user contract](normal-user-validation.md) remains authoritative: atomic
same-parent replacement and at most one file fsync, without recovery registries
or filesystem-wide syncfs. A faster copy achieved by removing that contract is
not an equivalent result.

Default cases are startup, check of one/128 transfers, 4 KiB ordinary file
replacement, and a 256-file tree with 4 KiB per file. Source and object execution
are measured separately. There are two warmups and nine samples, with no
page-cache eviction; subprocess launch and normal CLI work are included.
The small check versus 128-operation check separates fixed startup from scaling.
Source versus object separates frontend costs from runtime work. Single-file
versus tree distinguishes fixed overhead from per-member I/O and publication.

## Baseline preparation

The existing root `target/release/msync` initially reported 0.6.6 and was not used
as the 0.7.3 baseline. The preserved tree was rebuilt with:

```sh
cargo build --release --locked --manifest-path legacy/Cargo.toml --bin msync --target-dir target
```

The resulting optimized 0.7.3 image is retained under
`.temp/performance-v080/baseline/msync`; the benchmark makes another frozen copy
and records its SHA-256 before measuring. A repeated real tree transfer confirms
equivalent container semantics (`input-tree -> output-tree/`) and the intended
destination `output-tree/input-tree`. The comparison waits for an actual native
published 0.8 image. The harness's existence is not a performance result.

## Initial native comparison

Host: Linux 6.12.113+deb13-amd64, Debian 13, glibc 2.41, native ext4 filesystem;
.NET SDK 10.0.403, optimized Rust release and Native AOT C#. Neither image is a
managed/debug build. Raw results:
`.temp/performance-v080/comparison-initial.json`.

Frozen image identities:

* Rust 0.7.3, 19,931,640 bytes,
  SHA-256 `dc9a283064556fe517682afa5077d0228bb29e4f6c2150f1dbdbdc519634ad44`.
* Initial 0.8 AOT, 5,799,648 bytes,
  SHA-256 `bf89e052167ff056fff0755ec8020283781cd1b875ea46153f3d3757d6097fb8`.

| Journey | Rust median ms | Initial AOT median ms |
| --- | ---: | ---: |
| Startup (`--version`) | 3.542 | 12.537 |
| Check one transfer | 4.611 | 14.306 |
| Check 128 transfers | 20.219 | 17.046 |
| File replacement, source | 8.964 | 17.229 |
| File replacement, object | 9.061 | 17.867 |
| 256-file tree, source | 361.166 | 393.080 |
| 256-file tree, object | 347.480 | 374.836 |

All output bytes, native modes and modification timestamps matched. The initial
AOT implementation has a material fixed startup regression and an approximately
8% tree-copy regression. Faster 128-operation checking does not erase those
costs. The investigation below targets their mechanisms rather than dismissing
the differences as a host-language migration cost.

Compiled artifact sizes for these fixtures were 3,929 versus 453 bytes for the
single copy, 3,938 versus 675 bytes for the tree, and 268,010 versus 51,809 bytes
for 128 transfers (`.mobj` versus `.mso`). These include absolute diagnostic source
filenames; they vary with filename length. They establish compactness for this
workload, not universally smaller objects or faster execution.

## Startup: globalization, not async entrypoint

`strace -f -c` on `--version` recorded 477 total syscalls in the initial AOT image
versus 79 in Rust. AOT performed ICU version probing/loading, cgroup/sysfs
inspection and GC/runtime initialization even though help/version bypassed
configuration and telemetry. A disposable native entrypoint isolated the cost
using identical source with and without invariant globalization:

| Minimal native program | ICU median ms | Invariant median ms |
| --- | ---: | ---: |
| Return immediately | 4.308 | 4.378 |
| Write through raw Console stream | 9.464 | 4.606 |
| `Console.WriteLine` | 9.835 | 4.821 |
| Async method writing Console | 10.254 | 5.086 |

There were three warmups and 15 alternating paired samples. Probe source, build
logs and binaries are under `.temp/performance-v080/entry-probe`. This isolates
about 5 ms of globalization initialization; async Main itself is not the cause.

The production flag is `InvariantGlobalization=true`. msync's string operations
are explicitly ordinal/invariant and no locale/culture API is exposed. Contrary
to the original .NET Core invariant mode, [.NET 6 and later retain full Unicode
case mapping](https://learn.microsoft.com/en-us/dotnet/core/compatibility/globalization/6.0/culture-creation-invariant-mode).
Actual 0.8 scripts produced identical ICU/invariant output for Greek sigma,
Turkish dotted/dotless I, sharp-s, accented letters, Deseret astral letters,
Unicode regex `\w`, astral regex matching and UTF-8 literal transfers. This
comparison establishes that removing ICU does not introduce a new difference
between the two 0.8 builds; it does not by itself prove legacy Rust full-string
case-expansion compatibility, which is a separate language-contract question.

A second experiment moved help/version into a synchronous entrypoint before the
large async CLI body. It did **not** improve startup: paired medians were 7.630 ms
without the split and 8.083 ms with it, with overlapping ranges. The extra layer
was removed. The functional repair that routes `legacy --help/--version` before
new-product flags is retained independently of this rejected optimization.

The full-app invariant run is retained as
`.temp/performance-v080/comparison-invariant.json`. It is not an exact-source
before/after comparison because other integration work landed between native
images. Its startup/check/file medians were about 10.2/10.1/12.8 ms against Rust
3.0/3.7/8.0 ms in that run. A residual fixed runtime cost remains; this work does
not claim startup parity with Rust.

## Tree copy: one Unix worker, one reusable buffer

[The upstream FileStream engineering account](https://devblogs.microsoft.com/dotnet/file-io-improvements-in-dotnet-6/)
distinguishes native Windows overlapped I/O from Unix file operations scheduled
on workers. The initial Unix tree loop awaited individual asynchronous reads and
writes for every small file. A targeted trace made that scheduling cost visible.

The accepted candidate owns one worker for a whole Unix local tree, using a
single pooled configured-size buffer recursively. File reads/writes are
synchronous within that owner, with cancellation checked per buffer. Windows
retains native overlapped file I/O, and remote/stream writes keep their async
path. Cached per-node filesystem metadata avoids repeated source stats; directory
parents are created once by traversal, and a successful consuming rename is not
followed by a pointless unlink of its now-absent stage name. File staging,
metadata, one fsync and atomic replacement are unchanged.

Controlled 0.8 native before/after command:

```sh
python3 tests/Performance/journeys.py \
  --previous-aot .temp/performance-v080/split-aot/msync \
  --aot .temp/performance-v080/local-sync-aot/msync \
  --case copy-tree-source --case copy-tree-object \
  --output .temp/performance-v080/comparison-local-sync.json
```

Both images use invariant globalization and the same disposable entrypoint split
(subsequently rejected), so that split does not explain this tree comparison.
Frozen hashes: before `85ed19d571943291fa37942b23abc8b63e98571474689065ad09e3c214ee4f74`,
after `668e9897c8f82e593a586b7634448232109adb6cb2fbea1da6618f649b8b001d`.

| Tree journey | Before median ms | After median ms | Change |
| --- | ---: | ---: | ---: |
| Source | 370.986 | 350.112 | -5.6% |
| Source-free object | 377.485 | 354.235 | -6.2% |

All nine paired object samples improved (median paired reduction 31.489 ms);
six of nine source samples improved (median paired reduction 16.785 ms).
Filesystem latency still produces broad ranges, so the causal syscall result is
important alongside those medians:

| Syscall, 256-file tree | Before count | After count |
| --- | ---: | ---: |
| `sched_yield` | 8,490 | 148 |
| `futex` | 860 | 11 |
| `lstat` | 1,319 | 789 |
| `fsync` | 256 | 256 |
| `unlink` of consumed staging paths | 256 | 0 |

The traced timing is not used as a throughput measurement. Traces and summaries
are `.temp/performance-v080/sync-{before,aot}-tree-syscalls.txt`. Output checks
passed for both source and object tree journeys. Focused local transfer tests
also passed bytes, modes, literal replacement, dangling symlinks, self-copy,
descendant and ancestor overlap. No persistence protocol, reduced fsync contract,
or silent cancellation weakening was used to obtain the improvement.

This is an incremental native result for ordinary warm-cache Linux trees, not a
native Windows, WSL, WAN or release-wide throughput claim. The initial broad
comparison and targeted mechanism probes remain distinct from a final package
acceptance snapshot.

## Current product baseline: frozen final package, not the initial rewrite

The follow-up performance work starts from source `364de751a36145baa463e3fb95c1e22d4c52d94c`
and the actual final packaged AOT executable
`ca5c2eb2aebe1397f4ec3a745a6e76dcfcf598723ba1046523f183012263f38b`.
That image includes full Unicode 17 string casing, scalar ordering, source-free
artifact fixes, normal transfer scheduling and the first Unix worker optimization.
It is frozen under `.temp/performance-v080/current/before`; the older
`.temp/native-v080/msync` is an initial rewrite image and is not this baseline.
The optimized Rust control remains SHA-256
`dc9a283064556fe517682afa5077d0228bb29e4f6c2150f1dbdbdc519634ad44`.
The frozen AOT payload includes its native SQLite sidecar and per-file hashes.

Each result below uses three warmups and **31 alternating paired samples** on
the same Linux host. p50 is the median; p95 is the linearly interpolated empirical
quantile. File bytes, native modes and modification timestamps are checked
outside timing. There is no comparison between different hosted CI machines.
Raw results: `.temp/performance-v080/current/baseline-cli.json`.

| Current CLI journey | Rust p50 / p95 ms | Current AOT p50 / p95 ms |
| --- | ---: | ---: |
| Startup (`--version`) | 3.500 / 3.940 | 8.942 / 9.952 |
| Check one transfer | 4.345 / 5.237 | 10.256 / 11.981 |
| Check 128 transfers | 24.564 / 31.334 | 13.220 / 14.446 |
| 4 KiB file replacement, source | 8.669 / 9.851 | 13.226 / 15.853 |
| 4 KiB file replacement, object | 9.070 / 13.374 | 12.486 / 14.610 |
| 256 × 4 KiB tree, source | 322.977 / 368.746 | 347.307 / 385.992 |
| 256 × 4 KiB tree, object | 296.820 / 321.752 | 313.360 / 350.765 |

The current product therefore still has approximately 4–6 ms of additional fixed
cost on startup/small one-shot operations. Batch checking is faster, while small
tree copying remains slower than Rust in this paired run. Earlier improvement
against the first C# implementation is not a claim that current C# already beats
the Rust baseline.

### Persistent REPL and editor round trips

[`tests/Performance/interactive.py`](../../tests/Performance/interactive.py)
uses actual native REPL/LSP processes on Windows or Linux. Requests are
serialized and replies are parsed through real stdio. LSP initialization
includes process creation; steady requests exclude startup. REPL copy samples
overwrite the same file using each version's source syntax, await its actual
completion response, then compare exact bytes outside timing. A separate scalar
expression response is a new 0.8 capability, not an invented old-language oracle.

The same 31-pair method gives:

| Persistent operation | Rust p50 / p95 ms | Current AOT p50 / p95 ms |
| --- | ---: | ---: |
| REPL literal replacement and completion | 2.455 / 2.965 | 1.737 / 2.240 |
| REPL scalar expression response (0.8 only) | — | 0.092 / 0.139 |
| LSP initialize, including process creation | 3.810 / 4.790 | 9.000 / 10.343 |
| LSP hover over a bound Path | 0.361 / 0.483 | 0.194 / 0.337 |
| LSP semantic tokens, 128 bindings | 0.951 / 1.131 | 5.058 / 7.294 |

Raw results: `.temp/performance-v080/current/baseline-interactive.json`.
These small-document requests distinguish healthy persistent execution from
one-shot startup cost. They do not hide editor scaling: the product-latency
workstream separately measures large-document tokens and edit bursts, which
expose synchronous diagnostics and quadratic coordinate conversion. Its focused
before/after evidence must be reported alongside this baseline, not replaced by
the favorable small hover measurement.

The portable CLI harness also accepts `--file-bytes`, `--files`, `--bytes` and
`--statements` independently, so large-file I/O, small-file count and compiler
scaling are not conflated. Native Windows/Linux execution and same-host old-C#
controls are coordinated by the cross-platform acceptance workflow. No timing
threshold is a CI quality gate.

### Artifact validation: state belongs to CFG boundaries

The native component probe in `tests/Performance/Components` separates parsing,
cumulative compilation, optimization, encoding/decoding, binding and no-I/O VM
execution. Its realistic dense-binding inputs exposed a cost hidden by the
direct-transfer script, which has no locals: the verifier cloned its full
abstract locals array at **every instruction**. At 1,024 generic Path bindings,
one validation allocated 675 MB even though the no-I/O VM took only about 1.6 ms
for the analogous scoped workload.

The replacement keeps mutable abstract stack/locals within a control-flow basic
block and snapshots/joins only at outgoing CFG edges. Block boundaries are
targets, conditional/unconditional fallthrough, iterator branches and returns,
not the runtime scheduler's effect boundaries. The opcode transfer rules,
constant branch pruning and structural local/iterator checks remain shared.
This follows the block transfer/worklist model in
[Clang's dataflow introduction](https://clang.llvm.org/docs/DataFlowAnalysisIntro.html)
and the fixed-point treatment in
[Møller and Schwartzbach's static-analysis notes](https://www.cs.au.dk/~amoeller/spa/).
No persistent proof cache, trusted hash or reduced validation bypass is added.

Actual native component measurements (seven slow baseline samples, 31 candidate
samples; allocation measured separately from time) give:

| 1,024-binding validation | Before p50 ms | After p50 ms | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| Generic Path function | 635.94 | 0.916 | 675.4 MB | 214 KB |
| Scope-owned Path function | 731.17 | 1.478 | 760.7 MB | 224 KB |

The component report records exact native and assembly identities; these are
component costs, not whole-process timings. The slow instruction-level verifier
is retained **only in tests** as a propagation oracle. Generated control-flow
tests cover 6,000 joins, loops, constants, undefined locals and iterator paths;
all previously admitted programs remain admitted and structural rejections are
retained. Ten manually assembled invalid finite-type `Not` unions expose an old
queue-order-dependent rejection: they may now reach the VM's precise Bool check
instead of failing import. The source compiler already rejects those programs.
The importer is not expanded into a second predicate/alias type checker merely
to preserve that invalid-input traversal quirk.

The changed verifier passes 234 runtime assertions and eight actual source ->
`.mso` -> `.mexe` journeys covering nullable `?`/`is not`/match narrowing, union
type match, runtime-selected functions, recursion and native range callbacks.
Their byte-level result is retained in
`.temp/performance-v080/current/block-validation-journeys.json`. A normal-path
artifact load now pays for its actual CFG rather than for a locals snapshot at
each straight-line instruction.

### Compiler snapshots and the actual cold-CLI result

The [native component investigation](performance-components-v080.md) identified
two independent compiler costs after the validator fix: linear constant-pool
searches on every insertion and whole lexical Dictionary copies on every call.
Constants now retain their first-insertion vector IDs with a hash index, whose
new entries are removed on a failed compilation. Lexical frames share persistent
BCL immutable roots, with one binding-update path. Captures are discovered from
actual referenced names and ordered by slot, never by randomized hash-map
enumeration. The root changes are independent of runtime failure handling: the
REPL retains its actually executed declaration prefix rather than rolling back
filesystem work or initialized bindings.

Across 31 native component samples, the combined compiler changes reduce the
1,024-transfer compile median from 63.04 to 10.48 ms and the generic-binding
median from 46.18 to 18.62 ms. The environment change alone reduces generic
allocation from 21.58 to 5.64 MB and scoped allocation from 21.86 to 5.91 MB.
One-operation controls do not regress. Seven actual source-free closure,
recursion, Scope, lazy Range and nullable snapshot journeys pass, and two fresh
native processes produce byte-identical capture artifacts. Exact build hashes,+p95 distributions, allocations and commands belong in that focused note.

The optional `check-values` / `values-source` / `values-object` / `values-bound`
CLI cases then exercise an actual generic identity function, 1,024 retained
String bindings and publication of the last String as real file bytes. This is
not a no-output timing stub. The frozen final package `ca5c2e...` is compared with
the coherent integrated native candidate
`c3c87eb1b644f9edfff225ee59306656ee2d46f6ad6c9ad990bd1004837ab377`.
All cases verify exact output bytes outside timing; seven alternating paired
samples and one warmup are used because a baseline artifact load consumes about
700 ms and hundreds of MB, not because after-image samples are selectively timed.

| Actual 1,024-binding CLI | Before p50 / p95 ms | After p50 / p95 ms |
| --- | ---: | ---: |
| Check source | 72.430 / 78.017 | 29.354 / 30.884 |
| Execute source and publish result | 76.244 / 86.360 | 35.850 / 39.835 |
| Load `.mso` and publish result | 724.137 / 740.491 | 25.657 / 28.669 |
| Load `.mexe` and publish result | 707.671 / 740.469 | 21.544 / 29.080 |

Raw results: `.temp/performance-v080/current/dense-cli-before-after.json`.
The integrated image also includes editor/session/path work, so whole-process
ratios are not attributed solely to one patch. The isolated native component
measurements establish the validator and compiler mechanisms independently.
The direct user result is approximately 28× faster relocatable-object execution
and 33× faster bound-object execution for this retained-binding workload.

### First source read: an avoidable asynchronous boundary

`source_read.py` launches synchronous and asynchronous first-read modes in the
**same** Native AOT component image. The timer includes process creation through
the real UTF-16 count response. No compiler or configuration stage runs. Full
decoded String equality, Unicode content and SHA-256 are checked outside timing.
The files are warm in the OS cache; the BCL file reader/worker state is new in
each native process.

Linux, 31 alternating pairs, three warmups, image
`2b7f1b93a5938d8cb23b63e54485ba633760e803425d3e12e318a7e6ba409d7e`:

| UTF-8 source size | Sync p50 / p95 ms | Async p50 / p95 ms |
| --- | ---: | ---: |
| 4 KiB | 7.864 / 9.088 | 9.512 / 10.710 |
| 64 KiB | 8.362 / 10.076 | 10.461 / 11.799 |

Raw results: `.temp/performance-v080/current/source-read-linux.json`.
This demonstrates 1.65–2.10 ms of avoidable first-read cost in this process shape.
It is not a claim that the whole CLI's 4–6 ms Rust gap has one cause. The product
owner can use a synchronous ordinary-file read because compilation cannot overlap
that read and the execution cancellation handler is not installed until later;
stdin remains asynchronous. Native Windows uses the same published probe and
runner so IOCP first-use behavior is measured rather than inferred from Linux.

The actual whole CLI was then measured with 31 alternating pairs, old integrated
image `c3c87e...` versus the source-read candidate
`b89d9da5687c8ef526e7659f1c99e53cb21beb03f16305d776e94f07dbc39794`.
The added `check-source-4k` / `check-source-64k` cases contain the same ordinary
transfer plus Unicode comments, padded to exact UTF-8 sizes:

| Whole CLI control | Async-before p50 / p95 ms | Sync-after p50 / p95 ms |
| --- | ---: | ---: |
| Version/startup | 9.868 / 11.703 | 9.284 / 11.270 |
| Tiny source check | 10.955 / 12.312 | 9.691 / 12.283 |
| 4 KiB source check | 11.378 / 12.765 | 9.763 / 11.167 |
| 64 KiB source check | 12.288 / 14.646 | 10.734 / 13.068 |

Raw results: `.temp/performance-v080/current/cli-source-read-before-after.json`.
The full product saves about 1.55–1.62 ms in the source-size controls. The startup
control also shifts slightly with integrated-image layout/noise, so those totals
are not treated as a source-read-only decomposition. The independent same-image
primitive experiment supplies that causal check. Unicode/BOM/CRLF, stdin, legacy
stdin delegation and actual read-error journeys passed in the final native image.

## Native code-generation preference: retain the blended default

The official [Native AOT optimization settings](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/optimizing)
describe a blended default and optional `OptimizationPreference=Size` / `Speed`.
`tests/Performance/aot_preferences.py` reproduced all three from the **same** Git
archive `a2ee7ffae0adec096bb7a27105ebddc24dede08d`, SDK 10.0.403, linux-x64,
pinned source revision and locked dependencies. Only the CLI project was
published; no global AOT flag, diagnostic stripping or language-semantic change
was applied. These variants include the accepted source, compiler, verifier,
editor/session and I/O fixes.

| Native image | Bytes | SHA-256 |
| --- | ---: | --- |
| Blended | 8,112,880 | `e76a94a9863f81734c49008d8bb5140c2f4d2ca88012aa2554a934ba66d2ccc7` |
| Size | 7,830,640 | `c296dbe4d2f478ebc3a7c393aa04398c3726674a229c54f7566af5d717def4b1` |
| Speed | 8,587,232 | `731ac4c702dfd75df868c48d551b62de59e0166db236d478cedb751ceeabbdce` |

Each variant was paired against the blended image with 31 samples and three
warmups. Raw build logs, image manifest and full distributions are under
`.temp/performance-v080/preferences/current-a2ee7ff`.

| Size comparison, p50 / p95 ms | Blended | Size |
| --- | ---: | ---: |
| Startup | 8.247 / 9.754 | 8.802 / 10.261 |
| Small source check | 9.367 / 10.539 | 9.402 / 12.203 |
| 1,024-transfer check | 16.826 / 20.619 | 18.287 / 20.931 |
| 4 KiB file source execution | 13.036 / 17.450 | 13.367 / 15.522 |

| Speed comparison, p50 / p95 ms | Blended | Speed |
| --- | ---: | ---: |
| Startup | 8.331 / 9.758 | 8.334 / 9.970 |
| Small source check | 9.419 / 10.829 | 8.889 / 10.207 |
| 1,024-transfer check | 18.467 / 22.567 | 18.150 / 22.964 |
| 4 KiB file source execution | 13.613 / 15.614 | 13.034 / 15.440 |

Size reduces executable bytes by 3.5% but does not improve these latency
controls. Speed increases bytes by 5.8%, leaves startup unchanged, and shows
about 0.5 ms on the small check/copy medians with mixed p95 results. The blended
batch control itself moves by about 1.6 ms between the two paired runs, larger
than those small Speed shifts. There is no consistent, practically compelling
overall win to justify changing the deployed default. **Retain blended.**

The bounded I/O work has its own native Windows measurements, cancellation/ADS
checks and rejected Linux copy-offload experiments in
[performance-io-v080.md](performance-io-v080.md). Its filesystem metadata,
source-overlap and actual stage-lifetime acceptance is recorded separately in
[io-metadata-v080.md](io-metadata-v080.md). Those results are not inferred from
this code-generation preference experiment or from the small-file CLI controls.
