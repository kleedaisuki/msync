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
are measured separately. The initial round used two warmups and nine samples;
the current harness defaults to three warmups and 31 samples. Individual tables
state their selection. There is no page-cache eviction; subprocess launch and
normal CLI work are included.
The small check versus 128-operation check separates fixed startup from scaling.
Source versus object separates frontend costs from runtime work. Single-file
versus tree distinguishes fixed overhead from per-member I/O and publication.

## Baseline preparation (historical)

The existing root `target/release/msync` initially reported 0.6.6 and was not used
as the 0.7.3 baseline. The preserved tree was rebuilt with:

```sh
cargo build --release --locked --manifest-path legacy/Cargo.toml --bin msync --target-dir target
```

This records the original comparison setup. The Rust checkout and Cargo runner
were retired before 0.8 publication; historical sources remain at
[`v0.7.3`](https://github.com/kleedaisuki/msync/tree/v0.7.3).
Current comparison tools accept frozen C# Native AOT images only.

The resulting optimized 0.7.3 image was retained under
`.temp/performance-v080/baseline/msync`; the benchmark makes another frozen copy
and records its SHA-256 before measuring. A repeated real tree transfer confirms
equivalent container semantics (`input-tree -> output-tree/`) and the intended
destination `output-tree/input-tree`. The native image snapshots and actual
execution results below distinguish the initial rewrite from the subsequently
accepted product baseline and optimized candidates.

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


## Actual native Linux and Windows: final same-host comparisons

[Native platform run 42471357057](https://github.com/kleedaisuki/msync/actions/runs/42471357057)
executes source commit `3a1275da6d2ca4cb589ea61c3e4af49ab25d8541` on **actual**
Ubuntu 22 / Linux x64 and Windows Server 2025 x64 runners. Every comparison pairs
both images on the same OS instance; Linux and Windows absolute times are not
compared as if their CPUs/storage were identical. The pre-optimization C# control
comes from the hosted `622e920` artifact, whose product implementation corresponds
to the final `ca5c2e...` local baseline; native hashes naturally differ by build
host and target. Both old and new C# artifacts are downloaded onto the current
runner before its paired measurements, not compared across separate CI VMs.

The raw JSON, source fixtures, output bytes, per-case checkpoints, image hashes,
platform capabilities and logs are retained under:

```text
artifacts/ci/42471357057/{linux,windows}/native-<rid>-3a1275.../.temp/platform-results/
```

| Role | Linux executable SHA-256 | Windows executable SHA-256 |
| --- | --- | --- |
| Rust 0.7.3 | `f15bea4ad9818613ea6389abbae7a82160b70cf3b245df9f5d5b085f862d800c` | `ccdfa5ef1dc3655147241ea55ad3cfd1197c1bc2e1463ac4e49434666aa83c05` |
| C# pre-optimization | `f6509f5f51e32abd41bf1d4975e985d259bdf19748ef3862595af64d37a93dcc` | `5c96713b8076bdea6e72835978c9196af55f3f86ac2ebd7b25119e1c826ddf21` |
| Current C# Native AOT | `00036104a817b718b921ba088f0a6cf348aaf8da8df14284cb3aebe8035b3b51` | `2cb89065e098c87465d6c8fc23086a1610122b2398492249859ba06b2961938a` |

The Linux runner is image `ubuntu22/20261101.347.1`, kernel 6.8.0-1070-azure,
glibc 2.35. Windows is image `win25-vs2026/20261103.298.1`, build 26100,
AMD64 Family 26 Model 2. Both use Python 3.14.10. Benchmark phases completed on
both platforms with all exact-byte/metadata checks intact. Windows WSL-shell
newline repair and fixture follow-ups are tracked separately in the platform
correctness workstream; the benchmark phases and those acceptance phases have
distinct recorded outcomes.

### Rust versus the current C# product

Values are **p50 / p95 milliseconds**. The standard CLI, 1,024-transfer check and
interactive comparisons use 31 samples and three warmups. Each row uses its own
actual paired phase; a favorable current-image median from another phase is not
substituted into the table.

| Journey | Linux Rust | Linux current C# | Windows Rust | Windows current C# |
| --- | ---: | ---: | ---: | ---: |
| Startup | 1.717 / 2.127 | 4.846 / 6.005 | 4.204 / 4.301 | 6.737 / 6.890 |
| Check one transfer | 1.986 / 2.178 | 4.485 / 5.143 | 4.721 / 5.018 | 7.451 / 9.889 |
| Check 128 transfers | 9.236 / 9.917 | 5.171 / 5.813 | 9.439 / 10.000 | 7.503 / 8.004 |
| Check 1,024 transfers | 406.426 / 410.158 | 8.802 / 10.438 | 195.327 / 202.362 | 10.139 / 11.150 |
| 4 KiB file, source | 4.041 / 4.341 | 6.751 / 8.054 | 7.013 / 7.173 | 8.973 / 9.266 |
| 4 KiB file, object | 4.323 / 9.705 | 6.675 / 7.862 | 7.161 / 7.669 | 8.965 / 9.342 |
| 256 × 4 KiB tree, source | 126.646 / 160.414 | 135.231 / 162.236 | 126.541 / 139.105 | 97.831 / 110.248 |
| 256 × 4 KiB tree, object | 129.769 / 180.783 | 135.399 / 175.981 | 125.926 / 147.170 | 98.345 / 114.116 |
| REPL literal copy | 0.781 / 0.840 | 0.552 / 0.621 | 0.916 / 0.981 | 0.498 / 0.525 |
| LSP initialize including startup | 1.811 / 2.665 | 5.101 / 6.085 | 3.801 / 3.993 | 6.590 / 6.790 |
| LSP hover, 128 bindings | 0.149 / 0.185 | 0.077 / 0.160 | 0.094 / 0.118 | 0.063 / 0.070 |
| LSP tokens, 128 bindings | 0.279 / 0.327 | 0.609 / 0.670 | 0.228 / 0.253 | 0.429 / 0.445 |

The 0.8-only scalar REPL response is 0.052 / 0.077 ms on Linux and
0.039 / 0.053 ms on Windows; 0.7 has no equivalent standalone expression result,
so no fabricated Rust ratio is reported.

The direct-transfer compiler scales substantially better (1,024-transfer checks:
406.4 -> 8.8 ms Linux and 195.3 -> 10.1 ms Windows). Persistent copy and hover are
also lower in these pairs. Startup remains about 2.5–3.1 ms slower than Rust;
one-shot small copies remain about 1.8–2.7 ms slower. Windows tree copying improves
from 126.5 to 97.8 ms, whereas Linux's 126.6 -> 135.2 ms source-tree median remains
slower. Small-document semantic-token responses improve over old C# but are still
slower than Rust. The results support workload-specific gains rather than a
uniform reduction across all execution paths.

### Pre-optimization C# versus current C#, on the same machine

These are separate explicit old/new C# pairs, not ratios of unrelated hosted runs.
The old Windows C# tree implementation failed native directory metadata handling;
no valid before-image tree time exists, so that phase deliberately excludes old
Windows tree comparisons. The current Windows tree is measured above against
Rust and its metadata acceptance is independently checked.

| Journey | Linux C# before | Linux C# current | Windows C# before | Windows C# current |
| --- | ---: | ---: | ---: | ---: |
| Startup | 4.093 / 6.235 | 4.447 / 6.623 | 6.597 / 7.088 | 6.618 / 7.120 |
| Check one transfer | 4.751 / 5.900 | 4.584 / 5.610 | 9.645 / 9.857 | 7.119 / 7.432 |
| Check 128 transfers | 5.772 / 7.179 | 4.998 / 6.217 | 10.188 / 11.828 | 7.457 / 8.079 |
| Check 1,024 transfers | 34.926 / 39.792 | 8.766 / 11.086 | 28.543 / 29.320 | 9.492 / 10.465 |
| 4 KiB file, source | 6.354 / 8.407 | 6.824 / 11.635 | 11.060 / 11.494 | 9.040 / 10.482 |
| 4 KiB file, object | 6.143 / 7.765 | 6.320 / 8.569 | 10.866 / 11.528 | 9.026 / 9.408 |
| 256-file tree, source | 139.104 / 197.655 | 136.483 / 179.805 | — | — |
| 256-file tree, object | 126.710 / 196.270 | 132.404 / 167.314 | — | — |
| LSP tokens, 128 bindings | 2.031 / 3.087 | 0.651 / 0.841 | 1.076 / 1.224 | 0.429 / 0.459 |

The Linux small-file and some startup tails do not improve in this phase; they
remain recorded rather than being replaced by the more favorable local-host
run. Windows source checks/copies improve by about 2 ms, and the large compiler
and verifier gains reproduce on both actual operating systems. The large-document
LSP edit-burst/token measurements belong to the product-latency note: the generic
128-binding token pair here is not substituted for that scaling test.

### Retained function bindings: representation matters

These cases use nine pairs because old C# artifact validation is expensive. All
programs publish the same final String bytes after 1,024 identity-function
bindings. They do not execute equivalent backend instruction counts: inspecting
the actual Rust contextual artifact shows **one literal transfer** of
`payload_1023`, whereas the new first-class-function IR retains runtime bindings
and calls. This explains why the optimized C# representation is dramatically
better than its own previous validator yet can still cost more than Rust's
constant-expanded representation.

| Journey | Linux Rust | Linux current C# | Windows Rust | Windows current C# |
| --- | ---: | ---: | ---: | ---: |
| Check 1,024 bindings | 8.088 / 9.901 | 12.118 / 14.709 | 10.011 / 11.186 | 11.333 / 11.566 |
| Object execution | 7.582 / 8.259 | 11.182 / 11.680 | 9.320 / 9.564 | 11.158 / 11.252 |
| Bound IR execution | 3.967 / 4.165 | 10.106 / 12.684 | 6.490 / 6.692 | 10.162 / 10.261 |

| Journey | Linux C# before | Linux C# current | Windows C# before | Windows C# current |
| --- | ---: | ---: | ---: | ---: |
| Check 1,024 bindings | 33.186 / 35.143 | 12.490 / 15.476 | 25.523 / 25.747 | 11.912 / 12.556 |
| Object execution | 321.684 / 326.476 | 12.008 / 15.033 | 184.829 / 216.483 | 12.255 / 13.567 |
| Bound IR execution | 317.996 / 324.021 | 11.844 / 16.910 | 179.610 / 191.368 | 11.102 / 12.747 |

The before/current C# object-load medians improve 321.7 -> 12.0 ms on Linux and
184.8 -> 12.3 ms on Windows. This is a measured normal typed-program repair,
not a claim that the new function model already residualizes every pure function
as aggressively as the old symbol model. Further partial evaluation/inlining is
a separate compiler question, not a reason to insert a benchmark-specific
identity-function exception into the product.

### Actual platform first-read and component results

The same native probe/image is used within each OS for the first-read modes;
31 fresh-process pairs verify the complete Unicode text and exact source hashes.

| Source size | Linux sync p50 / p95 | Linux async p50 / p95 | Windows sync p50 / p95 | Windows async p50 / p95 |
| --- | ---: | ---: | ---: | ---: |
| 4 KiB | 3.849 / 4.435 | 4.370 / 5.751 | 7.619 / 9.065 | 8.183 / 9.326 |
| 64 KiB | 3.905 / 4.766 | 4.756 / 6.776 | 6.923 / 7.645 | 7.675 / 8.430 |


Both OSes confirm the direction of the synchronous finite-file read change;
absolute savings differ from the local host. Stdin and asynchronous native
copy/process transport retain their distinct lifetime requirements.
The actual current component probe (nine samples, no mock evaluator) reports:

| Generic 1,024-binding stage | Linux p50 / p95 ms | Windows p50 / p95 ms |
| --- | ---: | ---: |
| Parse | 1.097 / 3.676 | 1.240 / 4.109 |
| Cumulative compile O1 | 4.117 / 11.592 | 4.617 / 9.158 |
| Validate bytecode | 0.463 / 0.467 | 0.284 / 0.297 |
| Decode/validate object | 1.419 / 3.697 | 1.074 / 1.936 |
| Bind and runtime lower | 1.143 / 1.410 | 0.806 / 1.005 |
| No-I/O VM | 0.717 / 0.817 | 0.621 / 0.688 |


The per-stage distributions and allocation records remain in `components.json`;
cumulative compilation/decode stages are not summed as disjoint costs. Precise
first-principles before/after memory attribution remains in the local frozen
component study. No acceptance threshold or publication quality gate was added.

## Presentation and semantic-completion follow-up (2026-11-08)

The final accepted native payload has SHA-256
`0245361762f477559a7ba1cf97bb1a86b475a940f69f5df55c63739c813c7bdc`
(source `a4437d625f842f783148735827085e42a63da4d9`). The previous delivery
image `82c91394...` was preserved before package replacement. On this Linux
host, 31 randomized alternating fresh-process samples per workload observed:

| Native product image, ms | Previous p50 / p95 | Accepted p50 / p95 |
| --- | ---: | ---: |
| `--version` startup | 9.299 / 12.190 | 10.600 / 12.123 |
| Check one transfer | 10.484 / 12.130 | 10.772 / 13.068 |
| Check 1,024 transfers | 22.868 / 26.450 | 24.321 / 29.367 |

These are same-host **product-image** observations, not a build-toolchain-
controlled causal experiment: the old image was published locally and the new
image on the accepted Ubuntu runner. They must not be combined with the earlier
per-OS Rust timings as if all runs shared a host. The additional fixed cost is
small in absolute milliseconds, but this follow-up is not a CLI speedup claim.
Raw samples, seed, paths and full executable hashes are retained in
`.temp/product-polish/latency/accepted0245-comparison.json`.

Actual native stdio LSP requests with 128 bindings measured 4.437 ms for an open
followed by a **typed** hover, 2.161 ms for semantic tokens, and 16.613 ms for
sixteen edits plus completion. No compiler or filesystem operation runs on each
REPL highlighting/hint keystroke; explicit Tab uses the isolated semantic query.

The 4,000-binding stress case remains more expensive: typed hover 159.425 ms,
tokens 57.550 ms, and sixteen edits plus completion 373.132 ms. The old image
measured 1.415 / 32.049 / 112.088 ms respectively, but its hover was a generic
placeholder and its completion was a whole-document name scan rather than a
per-caret lexical/type query. These are different functionality contracts, not
proof that stronger analysis is free. Full current-snapshot inference and the
larger typed completion payload explain work absent from the old path. A
remaining large-document optimization target is reusing unchanged analysis and
lazily resolving selected completion documentation rather than describing every
visible binding. The real typed 128-binding journey is the relevant small-script
latency result; the stress-case cost is recorded rather than hidden.

The latency fixture now requests completion **after** the final declaration's
semicolon. Its previous cursor inside the new binding name incorrectly expected
that not-yet-published binding to be visible, contradicting the language's
lexical declaration boundary. Both native images completed the corrected
fixture. LSP records are `.temp/product-polish/latency/accepted0245-lsp128.json`,
`accepted0245-lsp.json`, and `baseline82c-lsp.json`.

## Matched typed-editor latency follow-up (2026-11-09)

The next iteration compares **the same useful typed behavior**, not the old
placeholder hover. Source `1e7e15a` was archived under the repository's `.temp`
and both images were published on the same Linux host, with SDK 10.0.403,
Release Native AOT and the same dependency locks. The candidate product changes
are Core `0204b7e`, compiler `29d6bb5` and LSP `f78cee1`.

| Local native image | SHA-256 |
| --- | --- |
| Baseline | `04e0e7c0e45b11585bf26b8ce04bfbeb47c40f034209a9e7a330e419f5fb52ef` |
| Candidate | `178b3b81ebbe5fa93056b9e4a91c8af0adc92fc37df0fa81bcb3a5e5f8803821` |

`tests/Performance/editor_latency.py` freezes both complete payloads, alternates
image order, checks actual responses, and records seven pairs per case. Its
realistic script includes functions, Scopes, captured paths and cold standard
library completion. Raw samples, request payloads and identities are retained in
`.temp/editor-latency-v080/paired-results.json`; the following are p50 / p95 ms:

| Actual request/workload | Baseline | Candidate |
| --- | ---: | ---: |
| Realistic typed completion | 4.056 / 4.387 | 2.129 / 2.336 |
| 128 bindings: open then typed hover | 4.011 / 4.844 | 1.961 / 2.201 |
| 128 bindings: sixteen edits plus completion | 12.601 / 15.235 | 6.985 / 9.345 |
| 4,000 bindings: open then typed hover | 137.250 / 167.565 | 63.818 / 70.069 |
| 4,000 bindings: first semantic tokens | 52.024 / 57.308 | 47.695 / 62.412 |
| 4,000 bindings: repeated semantic tokens | 26.674 / 60.181 | 11.455 / 12.985 |
| 4,000 bindings: sixteen edits plus completion | 405.326 / 452.397 | 259.972 / 270.010 |

Documentation is now resolved for the selected item when the client advertises
that LSP capability. The 46-item realistic response falls from 7,368 to 2,823
bytes; fetching its selected useful documentation costs an additional
0.504 / 0.624 ms. The 4,000-binding response falls from 752,189 to 384,250 bytes;
its selected resolution costs 3.815 / 5.421 ms. Clients without the capability
still receive complete eager documentation. This is not removal of the help.

The mechanism is reduced repeated representation work:

- Scalar type names no longer enumerate flags and construct LINQ pipelines.
  Immutable no-op nullability/union operations reuse the existing value.
- A single inferred observation is stored directly; only real alternatives
  require an alternative dictionary. Single-value rendering avoids temporary
  arrays and shares full Scope member descriptions.
- Selected completion resolution queries one name in the actual lexical
  context, rather than rendering all visible bindings again.
- Semantic-token data belongs to its exact document generation. An immutable
  JSON element can be serialized repeatedly without rescanning declarations or
  sharing mutable JSON-node parents.

The Core native single-image control measured scalar formatting
647.955 ns / 352.261 bytes to 9.050 ns / zero bytes per operation. All 65,536
underlying flag values, nested Range text and 20,000 recursive union/acceptance
properties retain the old value/string behavior. This is consistent with
[Enum.GetValues returning an array](https://learn.microsoft.com/en-us/dotnet/api/system.enum.getvalues?view=net-10.0),
not an assumption that a cached enum table makes every call allocation-free.

Managed component attribution (fixed `DOTNET_TieredCompilation=0`, nine samples)
shows scalar analysis allocations 34.05 to 12.35 MB, Path analysis 52.74 to
20.09 MB, Scope analysis 90.77 to 36.59 MB, and final Scope-context query 11.05
to 1.61 MB at size 4,000. EventPipe identified descriptor construction and GC
work; these are component/allocation observations, not native user latencies.
An old/current public-semantic snapshot over all UTF-16 offsets of eight mixed
fixtures is byte-identical (1,022,184 bytes each). Existing physical-owner,
malformed-editor recovery and actual accepted-REPL-value contracts also pass.

Not every tail improved: first 4,000-binding token p95 is slightly worse, and a
9.668 ms realistic first-token outlier was retained. Seven pairs are descriptive
observations rather than a universal tail guarantee or a CI timing threshold.
The simpler allocation/caching changes deliver the measured improvement without
an incremental type checker, arbitrary cancellation budget, or weaker inference.

## Staged optimizer cost before self-optimization (2026-11-09)

The graph optimizer must earn its compilation cost on interactive scripts, not
only produce short executable bytecode. This investigation uses the same
measurement-only `Components` probe from `3830291` against two exact archived
product sources, published on this host with SDK 10.0.403, locked dependencies,
Release Native AOT and runtime 10.0.14:

| Component image | Product source | SHA-256 |
| --- | --- | --- |
| Previous C# product | `2b434542e0bd0cc836df07a73386949de36ef41f` | `5e1dd3125df7a4ba56cc8da6a932defa61de9711ca141956e7862f8c7850466f` |
| New basic graph pipeline | `e7be48a7bcaf4bd6d7bc813fd9fbfa8aebb20ec3` | `4162ae53eccc90e581bfa2ac9992264e34f6b951e659e18084464f9ae9dcf6c3` |

These are component images, not packaged CLI hashes. The second source also
introduces mutable locations and directional scheduling. The shared workloads
are valid under both language contracts, but this is a product-cost comparison,
not attribution to a single optimizer change. The second image is the frozen
**before** for subsequent optimizer-self improvements.

`tests/Performance/optimizer_pipeline.py` alternates AB/BA fresh processes and
warms each production API three times inside each process. Source compilation,
optimization of an O0 module, contextual binding, relocatable execution and
executable execution are measured separately. They overlap in responsibility;
relocatable execution includes automatic binding and must not be added to its
cost. Before timing, both artifact stages and their codec roundtrips execute an
independent Path oracle. All checks passed. The run phases have no user I/O.

For one generic Path-returning call or one Scope factory/captured-method call,
15 paired observations at O1 give the following native p50 / p95 milliseconds:

| Workload / phase | Previous C# | Basic graph pipeline |
| --- | ---: | ---: |
| Call: complete source compilation | 0.023 / 0.031 | 0.305 / 0.750 |
| Call: graph optimization alone | 0.006 / 0.009 | 0.262 / 0.328 |
| Call: bind and lower | 0.008 / 0.010 | 0.114 / 0.151 |
| Call: prepared executable run | 0.0030 / 0.0044 | 0.0014 / 0.0018 |
| Scope: complete source compilation | 0.046 / 0.072 | 0.742 / 1.246 |
| Scope: graph optimization alone | 0.009 / 0.022 | 0.537 / 0.817 |
| Scope: bind and lower | 0.011 / 0.015 | 0.333 / 0.460 |
| Scope: prepared executable run | 0.0047 / 0.0074 | 0.0032 / 0.0052 |

Allocation traffic is a clearer first attribution than the noisier pilot tail:
one O1 graph optimization allocates 192,864 bytes for the call workload and
425,664 bytes for the Scope workload, versus 3,832 and 5,432 previously.
Binding the already-optimized module allocates another 81,648 / 258,672 bytes.
Those costs motivate pass-applicability filtering, stopping on established
normal forms, and reducing repeated graph construction before broader caching.

An initial three-pair O1 scaling probe at 1 / 32 / 128 instances isolates the
next question. At 128, raw optimization costs 8.23 ms / 4.20 MB for calls and
10.94 ms / 5.61 MB for Scopes. The resulting relocatable programs have 520 / 524
instructions, principally context-dependent Path joins, while binding reduces
them to 2 / 14 instructions. Prepared executable runs cost 0.0018 / 0.0030 ms
versus 0.2094 / 0.2189 ms previously. This is a real interpreter-work reduction,
but does not make one-shot source execution faster. The residual Scope module
still has three functions and five local slots; it is not advertised as fully
eliminated.

Raw reports are `.temp/optimizer-performance/basic-vs-old-{pilot,tiny,branches-loops}.json`.
The additional branch/literal-loop probe covers O0–O3 at the same three sizes;
its three-sample p95 is descriptive, not a tail guarantee or CI threshold.
No builds or other team benchmarks ran during the coordinated measurement
window. Source archives and full build logs remain beside the reports.

The engineering direction matches upstream practice: LLVM's
[analysis manager](https://llvm.org/docs/NewPassManager.html#using-analyses)
caches analyses with explicit validity and warns that inner passes recomputing
outer/module analyses cause quadratic compile time. Its per-function pass
grouping also improves locality. GCC's
[pass manager](https://gcc.gnu.org/onlinedocs/gccint/Pass-manager.html)
owns ordering and common bookkeeping; merely collecting more pass names does
not supply efficient invalidation. For msync, first measure the simpler feature
gates and normal-form paths against this frozen basic image, retaining identical
residual programs and actual execution results, before introducing persistent
analysis-cache machinery.

### Optimizer-self follow-up: applicability and normal forms

The next exact source is `c75330e3fa4b77b6cd998aa6628c280f69124cb7`, component
SHA-256 `363c68d8c77a5dd7b5164ef7a847a6152ff6c9c49d5e79b4ca8c3d37eb24a4bb`.
It is archived and published independently with the same probe and toolchain.
Compile-time function evaluation and new IPA transformations were not wired into
this frozen pipeline. A concurrent compiler callable-metadata correction could
otherwise confound attribution: the two archived assembly sets were therefore
used to emit all four workloads at sizes 1 / 32 / 128. Their source fingerprints,
typed instruction fingerprints **and complete O0 `.mso` bytes are identical in
all 12 comparisons** (`.temp/optimizer-performance/o0-fingerprints.json`). The
raw-optimizer comparison below has genuinely identical inputs; a full source
compilation timing remains a whole-pipeline observation.

The implementation groups work by function, filters analyses using one opcode
feature summary, recognizes already-normal literal-return functions, and reuses
preparation by function identity. Graph results are reused only within a valid
effect epoch, not across arbitrary mutation. Dead Path joins/conversions whose
constant operand kinds prove them total can be discarded without evaluating
host-specific path separators. This removes work before contextual lowering
rather than hiding it in a later stage.

Fifteen AB/BA pairs at size one, default O1, measured p50 / p95 milliseconds:

| Phase | Basic graph pipeline | Feature/normal-form pipeline | Allocated bytes/op, before → after |
| --- | ---: | ---: | ---: |
| Call: raw optimization | 0.325 / 0.422 | 0.146 / 0.195 | 192,864 → 101,736 |
| Call: bind and lower | 0.120 / 0.148 | 0.040 / 0.062 | 81,648 → 30,232 |
| Scope: raw optimization | 0.623 / 0.881 | 0.374 / 0.468 | 425,664 → 246,752 |
| Scope: bind and lower | 0.350 / 0.748 | 0.196 / 0.585 | 258,672 → 139,520 |

The three-pair O1 scaling probe at 128 instances gives absolute costs:

| Workload / phase | Before p50 ms | After p50 ms | Allocated MB/op, before → after |
| --- | ---: | ---: | ---: |
| Calls: raw optimization | 7.462 | 1.184 | 4.203 → 0.914 |
| Calls: bind and lower | 0.790 | 0.036 | 0.614 → 0.030 |
| Scopes: raw optimization | 10.875 | 2.917 | 5.611 → 1.349 |
| Scopes: bind and lower | 1.183 | 0.374 | 0.791 → 0.140 |
| Branch reuse: raw optimization | 8.965 | 2.285 | 4.910 → 1.176 |
| Finite loop: raw optimization | 7.073 | 2.816 | 3.966 → 1.649 |

MB is decimal allocation traffic, not retained heap or process RSS. Relocatable
call instructions drop from 520 to 12 and encoded bytes from 25,221 to 566;
Scope instructions drop from 524 to 16 and bytes from 28,387 to 684. Executable
instruction counts remain 2 / 14. Thus the improvement is less optimizer and
binding work for equivalent execution, plus earlier elimination of private
unused computations, not an invented further VM speedup. Complete 128-instance
source compilation measured 10.741 → 3.773 ms for calls and 25.998 → 11.905 ms
for Scopes in the same pilot; these include frontend work and are not labeled
isolated pass costs.

Prepared VM allocation rises by 88 bytes per execution in the later runtime,
which evolved independently between these exact source snapshots. The tiny
Scope execution p95 also rises from 0.0051 to 0.0114 ms in this run. No prepared
VM improvement is claimed for this follow-up. Real final CLI/file/tree timings
must use the final native source and remain distinct from component measurements.

Raw records are `.temp/optimizer-performance/feature-vs-basic-{scaling,tiny,branches-loops}.json`.
All source/artifact oracles passed at O0–O3; team builds and CPU probes were paused
for the coordinated window. Remaining ordinary-work costs are the captured-method
Scope graph (about 247 KB and 0.374 ms even at size one) and its contextual bind
(about 140 KB and 0.196 ms). Singleton reaching-definition representation and
more precise changed-function invalidation are subsequent measured questions,
not reasons to replace the working graph with a more elaborate unmeasured cache.

### Reaching-definition representation and ordinary transfer scaling

The representation experiment starts from the exact `c75330e` archive and
overlays only `GraphOptimization.cs` and `DefinitionSet.cs` (subsequently
committed as `f913c80`). The compiler, runtime and all other optimizations remain
identical. Both images use the same transfer-probe extension from `14930a9`;
this is not a comparison against the earlier differently instrumented image.

Reproduction identities under `.temp/optimizer-performance/`:

* Native control: `b123e5654859e81e08a0f6836fec914e17160950fbbade681c07bb82348a6dc8`.
* Native DefinitionSet variant: `ce19c70c6dce44d80b526640d684353e227c26bd078262aeadebd4f6794c5efa`.
* Complete control source TAR: `ab96d9ec4a35d8673db0f80a76d68368b98a4c87aa6ac485885d12178f350889`.
* Complete variant source TAR: `0eac594155ef8ab80fe65024fa73236f9dcf6b43c3abf9e4fb62dd6f079ed9a6`.
* Overlay patch: `b06c5c3ea706a2f879052e38fef0581335f5abeaa68728c75664580869305ed3`.
* Graph source: `52013f004c04c5983abdd9a60a4de2999b5f82934b55d00aa1a921dd75666fb2`.
* DefinitionSet source: `d9f696ad0d53a8f14cb8dea2e920d43d29164ca8d3e2c706fd7a23f70ba0f042`.

Empty and singleton reaching-definition sets now live directly in a value;
larger joins use immutable sorted arrays. State copying shares immutable set
contents instead of cloning a heap HashSet for every local/stack item. The
seven-pair call/Scope comparison and three-pair ordinary transfer comparison,
all at default O1, produced:

| Workload / phase | Before p50 ms | After p50 ms | Allocated MB/op, before → after |
| --- | ---: | ---: | ---: |
| 128 calls: raw optimization | 1.270 | 0.916 | 0.914 → 0.432 |
| 128 Scopes: raw optimization | 2.082 | 1.113 | 1.349 → 0.754 |
| 128 transfers: raw optimization | 6.494 | 4.987 | 2.489 → 1.355 |
| 128 transfers: bind and lower | 12.804 | 11.303 | 2.613 → 1.557 |
| 1,024 transfers: raw optimization | 268.846 | 231.392 | 20.020 → 10.959 |
| 1,024 transfers: bind and lower | 794.607 | 734.799 | 20.867 → 12.435 |

The transfer case is ordinary effectful code, not unused function construction:
`./source_N -> ./destination_N;`. Compiler/bind measurements validate both typed
artifact roundtrips but do not execute nonexistent fixture files; actual I/O
remains in the CLI/scheduler journeys. All value-workload execution oracles pass.
Raw observations are `definition-transfers.json` and `definition-values.json`;
the complete overlay manifest is `definition-source.json`.

This establishes a worthwhile allocation reduction but also exposes a more
important ordinary-work defect: eight times as many transfers costs about
46 times as much raw optimization and 65 times as much binding in the variant.
The graph emitter reinterns every original Constant via
`ValueRepresentation.IndexOf`, scanning the complete constant table each time,
and repeated optimization/binding waves multiply that quadratic work. The next
repair must retain original constant indices or use a compilation-owned hashed
interner with the existing bit-exact representation comparer. Faster dead-pure
examples do not compensate for hundreds of milliseconds spent optimizing an
ordinary 1,024-transfer script.

### Constant indices, shared interning and fixed-point reuse

Source `97f149c857b34a1c45bd37bea10938dbe6c7f522` repairs that mechanism. Existing
Constant instructions retain their original indices. Only newly folded values
enter a lazy, compilation-owned hashed interner using bit-exact representation
equality; graph waves/functions and the other folding passes share it. Stable
adjacent-Pop metadata and explicit graph/effect epochs avoid redundant cleanup
waves. The deterministic 1,024-constant check also proves that loading existing
constants does not initialize the interner at all.

The exact source archive is SHA-256
`3c5459014fab5783e3e1814eed8fa22d8d7b694a3c7a167feb15cfad57444d09`;
its native Components image is
`e38080f355ff3dbf47d7bdb150336346533520580a2f90f3c56b1da35e41b345`.
The measurement probe remains `14930a9`. Source, typed IR and complete O0 object
fingerprints for both transfer sizes remain identical to the control
(`pool-o0-fingerprints.json`). The source also contains unrelated O2+ family
integration; the default-O1 transfer comparison has no function/capture/loop
opportunities and keeps the raw input identical.

Three paired native observations give:

| Ordinary transfer phase | DefinitionSet before p50 ms | Repair after p50 ms | Allocated MB/op, before → after |
| --- | ---: | ---: | ---: |
| 128: source compilation | 5.330 | 2.841 | 1.833 → 1.402 |
| 128: raw optimization | 4.754 | 1.598 | 1.355 → 0.914 |
| 128: bind and lower | 15.934 | 2.062 | 1.557 → 1.188 |
| 1,024: source compilation | 275.595 | 40.056 | 14.809 → 11.326 |
| 1,024: raw optimization | 255.686 | 25.597 | 10.959 → 7.415 |
| 1,024: bind and lower | 864.601 | 26.190 | 12.435 → 9.553 |

Raw records are `pool-transfers.json`, `pool-tiny-transfer.json` and
`pool-values.json`. These are source-free artifact/phase observations, not final
packaged CLI timings. A separate seven-pair real CLI check of 1,024 ordinary
transfer statements compares the accepted `2b43454` product's actual default O1
with the `97f149c` candidate at explicit O2: p50/p95 is 19.306/20.678 versus
32.520/40.699 ms (`preclosing-check1024.json`). That remaining regression against
the current C# product is not hidden by the much larger improvement against the
initial graph implementation. It motivates a bounded linear normal-form path
for literal/effect-only functions, which have no CFG/alias opportunities worth
materializing the complete graph for. Final product comparison follows that
decision and the independently accepted REPL/Windows fixes.

### O1, O2 and O3: cost, live computation and default policy

The same `97f149c` native image was measured in three independent processes with
level orders 1/2/3, 2/3/1 and 3/1/2. Three samples and three API warmups per
process yield nine observations per level; no native image/toolchain change is
mixed into the comparison. Raw data: `final-levels/summary.json`.

| Workload / phase | O1 p50 ms | O2 p50 ms | O3 p50 ms |
| --- | ---: | ---: | ---: |
| 128 calls: complete compilation | 4.537 | 4.908 | 6.552 |
| 128 calls: raw optimization | 0.710 | 0.780 | 0.896 |
| 128 Scopes: complete compilation | 7.890 | 7.562 | 7.503 |
| 128 Scopes: raw optimization | 1.073 | 1.076 | 1.069 |
| 1,024 transfers: complete compilation | 36.000 | 43.136 | 36.951 |
| 1,024 transfers: raw optimization | 18.584 | 19.055 | 19.152 |
| 1,024 transfers: bind and lower | 22.924 | 22.581 | 24.388 |

These common workloads have the same executable shapes at all three levels:
two instructions for calls, 14 for the captured-method Scope, and 514 for the
128-transfer program. Small median differences are not an execution-benefit
claim. O2 adds only about 34 KB of raw-optimization allocation at 1,024 transfers
while enabling the extra CTFE, IPA and global-redundancy families. The product
decision is **default O2**, explicit O1 for latency-oriented experimentation,
and explicit O3 when its larger transformation/growth costs are worthwhile.
Actual older-product default O1 remains labeled as such in comparisons.

To activate the additional families rather than merely time identical residual
programs, the `bd546ce` measurement-only probe adds a live 200-item sum. Its
uncaptured zero-argument function computes 1 through 200 in original order; the
caller returns the expected Path only when the result is 20,100. Both artifact
stages and their roundtrips execute that independent oracle. The same `97f149c`
product code is used at all levels with the identical extended probe.
The extended native image SHA-256 is
`5fe9a7cce2a2e8e44d2d7161def8a130635e37559d733a6c0ab8840d273ff83e`;
the extension/source manifest is `levels-source.json`.

| Consumed finite computation | O1 | O2 | O3 |
| --- | ---: | ---: | ---: |
| Executable instructions / functions / locals | 223 / 2 / 3 | 2 / 1 / 0 | 2 / 1 / 0 |
| Executable artifact bytes | 12,912 | 292 | 292 |
| Complete compilation p50 ms | 1.449 | 0.950 | 0.858 |
| Bind and lower p50 ms | 0.912 | 0.026 | 0.023 |
| Prepared VM p50 ms | 0.1590 | 0.00162 | 0.00159 |
| Prepared VM allocated bytes/run | 108,536 | 1,656 | 1,656 |

This 15-sample native result (`finite-sum-levels.json`) removes a real consumed
computation, not dead loop output. O2/O3 give the same residual program; their
small timing difference is noise rather than a reason to default to O3.

An independent actual CLI IPA example uses
`write(mode: Bool, unused, destination: Path)` with two known-true calls, one
receiving a transfer-producing argument. O1 retains three formal parameters,
one branch and 29 executable instructions. O2 retains the invocation boundary
but specializes a one-argument clone, removes the unselected literal/branch, and
leaves 20 instructions. Source, `.mso` and `.mexe` execution each produce all
three exact file payloads, including the effect of the removed unused formal.
Plans and bytes are under `level-activation/ipa/report.json`. That CLI image is
the `97f149c` candidate (SHA `686743f4e64abb81484ee9ba64f2be5fdbc2641981ac5001a3a7356c7a0b14f8`),
not the later REPL/Windows-fixed closing payload. Its parser still lacked `-O3`;
`1d8839b` repairs that user-facing option and final native acceptance owns its
coverage. The O3 Core pipeline itself is exercised by the native component
comparison above.

## Final default-O2 product: same-host Rust/C# controls

The bounded linear-effect repair is `0c17264`; final package source
`372296b2a72821a888c01502f445376f611fd392` adds only platform workflow selection.
It accepts only a proven straight-line scalar stack with no locals, parameters,
captures, calls, branches or unsupported operations. Typed transfer operands,
original transfer-result Pops, spans and effect order are retained. Other shapes
use the ordinary graph pipeline. In particular, a source spelling that lowers
to PathJoin is not silently treated as the simpler literal-only shape.

Seven paired actual CLI checks of 1,024 literal transfers compare the functionally
accepted pre-repair default-O2 package (`f009`, SHA
`74e62569108e65e42a4076a1ed5559256de70d83f05d175ac064173babd894ee`)
with the final package: **35.508 / 41.940 → 24.404 / 28.127 ms p50 / p95**.
That measured 11 ms median reduction supports adopting the bounded path.
`final-native/nf-profit.json` retains every sample. The final source/object/
executable byte, metadata and error-fallback journeys pass on both native OSes;
their final fixture uses direct filename atoms rather than `./` spellings that
correctly select the full graph fallback.

Final image identities:

| Role | Linux SHA-256 | Windows SHA-256 |
| --- | --- | --- |
| Optimized Rust 0.7.3 | `dc9a283064556fe517682afa5077d0228bb29e4f6c2150f1dbdbdc519634ad44` | `ccdfa5ef1dc3655147241ea55ad3cfd1197c1bc2e1463ac4e49434666aa83c05` |
| Accepted C# `2b43454`, actual default O1 | `4a0a861a3656f69320845e25c72407c1c884c6765df670c76eead58900a05fc8` | `f255f77212e6437503b4358e13a73f1b2ff30c88910ae91bd515f08f22925502` |
| Final C# `372296b`, actual default O2 | `6106c20a0c9e119f917419a07db2e5b7f7e383138e0d4f4ccf7bd65acb0e9225` | `8cb08a0d75c361ccbd5c5481641f818f00d7702509f3d90b40367019ed0affa3` |

The Linux comparison runs all three frozen native payloads on the same Debian
host used above, with Python 3.15.2. Three warmups precede 31 samples per image
for the small/value CLI cases and nine for the 1,024-transfer check and tree.
The six-round ABC/BCA/CAB/CBA/ACB/BAC rotation balances image positions; each
version uses its actual syntax and object format. Exact bytes, tree member
counts, native modes and nanosecond modification times are checked outside
timing. Values are p50 / p95 milliseconds:

| Linux actual journey | Rust 0.7.3 | Current C# O1 | Final C# O2 |
| --- | ---: | ---: | ---: |
| Startup | 3.565 / 4.253 | 10.604 / 12.656 | 11.902 / 13.365 |
| Check one transfer | 4.655 / 5.952 | 11.066 / 13.829 | 12.140 / 16.643 |
| Check 128 transfers | 24.180 / 27.970 | 12.655 / 14.683 | 14.444 / 18.903 |
| Check 1,024 transfers | 1881.037 / 2032.875 | 21.428 / 24.818 | 24.057 / 30.632 |
| 4 KiB copy, source | 9.009 / 10.413 | 15.320 / 17.224 | 16.724 / 20.353 |
| 4 KiB copy, object | 8.936 / 10.244 | 14.870 / 18.945 | 16.167 / 18.987 |
| Check 128 function bindings | 6.818 / 7.916 | 13.406 / 16.561 | 23.038 / 27.988 |
| 128 bindings and write, source | 10.777 / 11.777 | 18.207 / 20.985 | 27.213 / 34.299 |
| 128 bindings and write, object | 10.255 / 11.234 | 16.791 / 18.464 | 15.964 / 18.449 |
| 128 bindings and write, executable | 8.995 / 10.497 | 15.254 / 18.788 | 15.396 / 16.803 |
| 256 × 4 KiB tree, object | 342.106 / 363.451 | 362.966 / 394.853 | 371.705 / 399.982 |

This is not a claim of universal superiority over the previous C# product.
Final startup and one-file source operations cost about 1.3–1.4 ms more on this
host. The 128-binding source/check path costs another 9–10 ms, whereas object
execution improves slightly and executable startup dominates the nearly equal
bound case. The extra cost is observed in compilation/checking, not a measured
new I/O bottleneck; these data do not isolate a single frontend mechanism.
The tree distributions overlap broadly (final range 304–407 ms); their medians
remain reported rather than replaced by a favorable sample. The large Rust
transfer-check result is specific to this explicit 1,024-statement workload,
not an extrapolation to all Rust programs or bulk transfer throughput.

Native Windows Server 2025, four AMD64 CPUs and Python 3.14.10 independently
compare the same product roles with three warmups and nine samples. These
figures are same-runner comparisons, never ratios against the Linux host:

| Windows actual journey | Rust 0.7.3 | Current C# O1 | Final C# O2 |
| --- | ---: | ---: | ---: |
| Check 128 function bindings | 6.387 / 6.997 | 10.056 / 10.685 | 12.018 / 13.070 |
| 128 bindings and write, source | 9.796 / 62.495 | 12.425 / 13.165 | 15.025 / 15.565 |
| 128 bindings and write, object | 8.495 / 9.145 | 11.093 / 11.513 | 10.947 / 11.259 |
| 128 bindings and write, executable | 7.670 / 8.124 | 10.857 / 11.017 | 10.260 / 10.681 |
| 4 KiB copy, source | 8.776 / 8.994 | 12.010 / 12.537 | 12.087 / 12.489 |

The preserved Rust source-run tail outlier is included. Windows reproduces the
source-compilation tradeoff and small object/bound improvement; ordinary file
copy is approximately unchanged against current C# in this phase.

Finally, the real source-inclusive scheduler probe overwrites its 64-byte input
generation before **every** run and checks every destination after completion.
It uses four jobs, one warmup and three measured AB/BA pairs:

| Linux workload | Current C# p50 / p95 ms | Final C# p50 / p95 ms |
| --- | ---: | ---: |
| 100 independent copies | 71.055 / 77.081 | 80.308 / 80.994 |
| 1,000 independent copies | 658.999 / 666.310 | 638.231 / 668.564 |
| 100 dependent-chain copies | 155.384 / 167.862 | 166.299 / 172.749 |
| 1,000 dependent-chain copies | 1439.394 / 1528.183 | 1545.291 / 1596.479 |

The chain cost is retained as an observed tradeoff, not declared a speedup.
This probe includes source compilation, startup, scheduling and I/O, so the
difference is not attributed solely to the resource DAG. Three-sample p95 is
descriptive, not a tail guarantee. The separate structural 100/1,000-file probe
establishes linear resource-index visits; that property does not prove a lower
whole-process latency by itself.

Complete Linux records and frozen origins are
`.temp/optimizer-performance/final-native/{origins,threeway-cli,threeway-check1024,threeway-tree,scheduler}.json`.
Windows records are under
`artifacts/ci/42518195849/windows/native-win-x64-372296b2a72821a888c01502f445376f611fd392/.temp/platform-results/final-threeway.json`.
No further product rebuild is implied by these notes: the accepted package
hashes above, VSIX, real VS Code host and cross-platform functional acceptance
belong to the integration record. For repeated execution, compile/bind once and
reuse `.mexe`; do not infer its latency from the slower complete-source path.

## Cross-block field memory and compact Scope layouts

The November 9 memory study compares the accepted product source `372296b`
with `bb389db` (compact layouts, private cross-block scalar replacement,
physical field forwarding, precise memory summaries and aggregate arguments).
The same measurement-only source from `273ec92` is overlaid on both frozen
source archives. A subsequent, separately measured change `a0bb5bd` skips the
old block-local scalarizer when no Scope allocation remains after rewriting.
These are Native AOT component comparisons on the same Debian host, not Rust
comparisons or whole-CLI startup/I/O measurements.

| Image | SHA-256 |
| --- | --- |
| Previous product plus matched probe | `ba5df4bafe2c5c9a551c1997f21777ca6b30f026719465e5204f861b91e0b5aa` |
| Memory implementation `bb389db` | `37947d76c4b5ff61521ed5e24d06761dfb149e61ff89adf79449851b2a453e02` |
| Applicability repair `a0bb5bd` | `1cca3fa98a97e832123481661d5e3a507070573ee1f21062640fc48aadb97951` |

`tests/Performance/Components/MemoryPipelineWorkloads.cs` supplies three consumed
workloads. `memory_branches` creates distinct objects live across branches and
checks every resulting field. One actual `fs.exists` supplies its runtime Bool;
the run timings include that same metadata query in both images. `memory_loop`
carries fields through a finite loop and returns the expected Path only when its
sequential sum is correct. `layout_factory` returns a lazy Range of fresh Scopes;
the same driver consumes it and reads every file field, so its VM samples
include real physical construction, not only an unevaluated adapter.

The paired driver alternates AB/BA fresh processes, warms each phase three
times, calibrates batches to about 2 ms, and records a separate allocation
batch. Three paired observations per image at sizes 1/32 are a bounded workload
study; the interpolated p95 is descriptive rather than a tail guarantee.
Compile, optimize, bind and prepared VM phases overlap and are not summed.
Every image/level is executed and round-tripped through `.mso` and `.mexe`
before timing. No live object instance or filesystem observation is cached as
an optimization proof.

Selected O2 results against the previous product, before the applicability
repair, are p50 / p95 milliseconds:

| Workload, size 32 | Previous | Memory implementation | Previous / new allocation bytes |
| --- | ---: | ---: | ---: |
| Cross-branch raw optimization | 26.387 / 27.386 | 19.158 / 28.292 | 22,461,728 / 16,944,888 |
| Cross-branch prepared VM | 0.1216 / 0.1647 | 0.0644 / 0.0664 | 70,944 / 34,600 |
| Loop-field contextual binding | 0.875 / 1.196 | 0.295 / 0.327 | 569,096 / 142,208 |
| Loop-field prepared VM | 0.1053 / 0.1128 | 0.0019 / 0.0031 | 52,112 / 1,664 |
| Escaping-factory raw optimization | 0.816 / 0.852 | 1.131 / 1.206 | 616,088 / 618,976 |
| Escaping-factory prepared VM | 0.1436 / 0.1575 | 0.0998 / 0.1290 | 67,008 / 48,720 |

This is not uniform acceleration: the escaping-factory optimizer costs another
0.315 ms in this sample while construction/consumption becomes cheaper. At
size 1, its prepared VM changes from 0.0060 to 0.0069 ms and 4,240 to 4,304
bytes; shared-layout setup does not pay back on every singleton use. The
larger factory is the relevant shape-reuse case.

Structural evidence distinguishes the mechanisms. A one-object branch keeps
27 executable instructions, one MakeScope, two SetMembers and one GetMember in
the previous product; the new pipeline has 18 instructions and no object/member
operations. At 32 objects the new executable has 421 instructions and no
MakeScope/GetMember/SetMember. The loop-field body at size 32 becomes a literal
return after scalar replacement exposes finite evaluation. The escaping factory
retains its MakeScope site and creates distinct instances; fewer allocated bytes
there come from layout/storage, not pretending the objects disappeared.

### Optimizer-cost diagnosis and bounded repair

A size-128 branch pilot exposed a real remaining analysis cost despite lower
allocation than the previous compiler: the memory implementation used roughly
414 MB and hundreds of milliseconds for raw optimization. An isolated managed
driver copied under `.temp` measured allocated bytes around individual passes,
without adding diagnostics to the product hot path. Its largest phase was the
existing block-local scalarizer: **267,299,392 bytes**, versus about 55 MB for
global redundancy, 37 MB for predicates, 32 MB for graph demand and 21 MB for
the new private cross-block analysis. Cold managed timings from that diagnostic
are not used as native speed claims.

The cause was stale applicability information: the outer driver knew the input
contained Scopes, but cross-block scalar replacement had already removed every
allocation. The local scalarizer still built CFG liveness and then returned the
unchanged function. `a0bb5bd` puts the no-MakeScope proof in the scalarizer's own
entrypoint. A structural test verifies one applicability enumeration and no CFG
indexed reads; there is no input-size cutoff or reduced language semantics.

Three paired native observations of the exact repair isolate its effect:

| Branch workload | Before repair p50 / p95 ms | After repair p50 / p95 ms | Allocation bytes before / after |
| --- | ---: | ---: | ---: |
| Size 32, complete compile | 38.527 / 38.899 | 31.418 / 32.122 | 18,142,536 / 12,281,616 |
| Size 32, raw optimize | 17.455 / 21.345 | 20.595 / 21.619 | 16,944,888 / 11,086,240 |
| Size 128, complete compile | 667.168 / 698.249 | 336.823 / 357.859 | 420,560,632 / 153,266,208 |
| Size 128, raw optimize | 546.139 / 556.526 | 252.613 / 261.939 | 413,774,160 / 146,482,104 |

The size-32 raw-time sample did not improve; only its deterministic allocation
reduction is claimed. Binding at size 128 remains approximately 206–207 ms and
148 MB, and the repaired executable's instruction stream/125,224-byte VM
allocation is unchanged. The repair avoids redundant compilation analysis,
not a new execution optimization. A tiny no-Scope call retains exactly 65,520
raw-optimization allocation bytes before/after. The remaining 128-object cost is
still substantial inherited graph/PRE/predicate work; this study does not claim
that all compiler scaling problems have disappeared.

Ordinary no-Scope controls were measured before the repair and are unaffected
by a guard on a Scope-only pass. Seven paired 1,024-transfer observations show
raw optimization **17.774 / 22.780 → 14.610 / 20.835 ms**, with only a constant
allocation change **6,930,320 → 6,931,032 bytes**. Complete compile changes
25.833 → 26.588 ms; binding 17.406 → 16.907 ms. An initial three-pair +5 ms
raw-time fluctuation did not persist in the larger confirmation, so it was not
reported as a new algorithmic regression. This negative control does not establish
a universal no-regression guarantee.

Complete manifests, original samples, shapes and diagnostic records are under
`.temp/memory-performance/`: `baseline-source.json`, `final-source.json`,
`postguard-source.json`, their native-image manifests, `control-calls.json`,
`control-transfers-confirm.json`, `memory-pilot.json`, `memory-128-pilot.json`,
`sra-applicability.json`, `sra-tiny-control.json`, and
`phase-diagnostic/summary.json`. Size-128 pilot observations against the old
product have only one pair and are retained as mechanism/scaling probes, not
presented as a settled latency distribution.

Actual cross-platform user-journey coverage is separate:
`tests/Msync.EndToEnd/memory_layout_journey.py` executes explicit O0/O2 source,
relocatable and bound artifacts with exact binary file payloads, both selected
branch edges, disjoint calls, shared captured fields, same-site live instances,
deferred callback mutation, selected errors and a different launch cwd. Its
default includes two newly fixed frontend regressions; the frozen previous
product is exercised with `--baseline-compatible` instead of being credited
with syntax/metadata behavior it did not support.

## Source-backed standard library expansion and selective loading

The stdlib expansion keeps pure source composition and module/capture identity,
while instantiating only the requested modules and their source dependencies.
The comparison deliberately uses O0 so DCE cannot hide unwanted module work.
`tests/Performance/stdlib_load.py` alternates seven fresh Native processes per
image/common case; each records a cold compilation, three warmups and three
warm observations. Times exclude process startup and filesystem IO. Allocation
is managed traffic, not live heap, and no threshold is a CI gate.

Both images use SDK 10.0.403 and exactly the same StdlibLoad measurement source
(`40a1ba5f70b782266cf679daeb2ef3150fe4a0249f6aac37ee01ea65ea26575d`).
Baseline source is `a8c71875e5f7bd2b5a628a8bcdece3f8b72ec4d1`, Native SHA
`c0decade49374bd7232f3e7e53d4f394fb3de563a3cf4d924ddfb78bb99510c2`.
Candidate source is `b3c0f9f875e3d0f022f5f853a9f8f6b5e197ac65`, Native SHA
`6be44d30bdb34dcafdcf32da41fca4fe3a33191c724705ab3f08d2572e41e973`.

| Common source | Cold p50/p95 before → after (ms) | Warm p50/p95 before → after (ms) | Warm bytes before → after | Raw functions before → after |
| --- | --- | --- | ---: | ---: |
| No stdlib reference | .381/.442 → .415/.496 | .021/.036 → .026/.045 | 7,336 → 7,328 | 1 → 1 |
| `bool::both(true, false)` | 3.449/4.069 → 1.550/1.804 | 1.411/1.897 → .266/.326 | 442,256 → 72,104 | 23 → 7 |
| `ranges::first(ranges::take([1, 2, 3], 2))` | 3.863/5.828 → 4.946/5.328 | 1.596/2.043 → 2.247/3.161 | 442,360 → 616,384 | 24 → 53 |

The library is not universally cheaper to initialize: the expanded Range module
itself costs about 0.65 ms and 174 KB more for this warm unoptimized compilation.
That tradeoff is accepted for its substantially larger source expression layer.
The unrelated Bool path gets a large reduction, proving that library growth no
longer requires instantiating all other modules. The no-stdlib shape and
allocation are unchanged; its few-microsecond timing delta is not evidence of a
meaningful product win or regression. Per-export lazy construction is not added
at the last minute: mutable namespace fields, whole-root construction and shared
module capture locations require a coherent identity model, not independent
detached function caches.

Candidate-only new APIs have separate costs, not invented old equivalents:
root cast has cold p50/p95 **1.372/3.543 ms**, warm **.086/.123 ms**, **22,760 B**;
a composed trim/case closure has cold **4.536/7.435 ms**, warm **1.891/2.886 ms**,
**505,960 B**. Source `size(take(...))` costs cold **5.442/6.236 ms**, warm
**2.220/3.063 ms**, **641,024 B**.

Complete raw observations and native/source manifests are in
`.temp/stdlib-load/paired-final.json`, `baseline-a8-fixed/build.json` and
`candidate-b3/build.json`. The [stdlib contract](stdlib-v080.md) contains the
executable inventory, additional raw-shape fields and composed CLI/editor
acceptance. Functional platform acceptance is recorded independently by the
native delivery owner; a managed CLI journey is not relabeled as Native AOT.

## Contextual binding: one optimizer fixed point

`Binding.Bind` previously ran contextual lowering and the complete optimizer
fixed point twice. The first lowering freezes known endpoint roots; optimization
can then expose more literal paths. The final lowering only selects the native
local-transfer opcode and removes no-ops. It does not introduce a pure
expression, change the transfer's dependency effects, or make another global
optimization fixed point useful. The pipeline is now lower → optimize → lower.
Executable input is already bound and is returned unchanged.

`ExecutionLowering` also retains unchanged functions and modules. It skips
functions without symbolic endpoint roots, generic transfers or no-ops; copies
an instruction array only on its first actual replacement; and builds relocation
tables only when removing no-ops. Unsupported conversions are unknown to its
small constant tracker rather than retaining the pre-conversion value.

The focused executable test and reproducible managed binding probe are:

```sh
dotnet run --project tests/ContextBinding -c Release
DOTNET_TieredCompilation=0 dotnet run --project tests/ContextBinding -c Release -- --benchmark
```

The 38 workflow assertions cover O0–O3 relocatable/executable round trips,
captured cwd, actual transferred bytes, unselected work, a condition changing
between executions, a now-missing selected source returning false, and preserved
completed destination bytes. These checks intentionally observe the filesystem
at execution, not binding. Native cross-platform package acceptance remains the
separate product journey.

For causal measurement, Core sources were frozen at `e7149dd`; the candidate
changed only `Binding` and `ExecutionLowering`. Both Release assemblies used the
same .NET 10.0.403 SDK on Linux. Ten warmups preceded 300 prepared binding samples
per case (40 for 128 conditional transfers). Three process runs per variant
alternated AB/BA; tiered compilation was disabled identically to avoid measuring
promotion during the short probe. The table reports the median of process
medians and median synchronous thread allocations. Source compilation, artifact
decoding and actual I/O are outside this interval.

| Prepared O2 binding | Before ms | After ms | Before bytes | After bytes |
| --- | ---: | ---: | ---: | ---: |
| Literal result | 0.0015 | 0.0010 | 1,648 | 1,056 |
| One local transfer | 0.0188 | 0.0102 | 7,128 | 3,728 |
| 128 local transfers | 0.4634 | 0.2524 | 197,480 | 100,936 |
| 128 conditionally selected transfers | 8.3454 | 4.9693 | 4,512,128 | 2,760,920 |

Frozen Core SHA-256 identities were
`2c068704757a3e44027f11082da9523fc941a692168fd6440e326c67a83b4f11`
(before) and
`5b640548733d370f2d615141adb5ac68acbbba278cd810b3f35c305542477a34`
(after). Raw samples and isolated source/build images are disposable under
`.temp/context-binding/controlled-final.json`. This is a binding-cost result,
not a claim about CLI startup or disk throughput.

## Full Number/concept upgrade: matched native CLI observations

The final language-upgrade publication source is
`a952448083a6f0bea9ee2def0e775dcd804500f7`, compared with the accepted standard-library
baseline `e85e6170598dfd38adefbcb778e43647686c3096`. Both images ran on the same
native Linux runner or the same native Windows runner in
[run 42619095812](https://github.com/kleedaisuki/msync/actions/runs/42619095812).
Each image produced and consumed its own `.mso`/`.mexe`; old IR compatibility was
neither required nor simulated. Version remains 0.8.0 and default O2.

The no-library/Bool O0 checks alternate image order, with two warmups and fifteen
observations. The staged workloads use two warmups and nine observations per
image, with 128 or 1024 statements. Times below are external process p50 in
milliseconds, including startup; they are not managed component timings or CI
thresholds.

| Workload | Linux before → after ms | Windows before → after ms |
| --- | ---: | ---: |
| No-library O0 check | 3.959 → 3.791 | 14.747 → 14.847 |
| Bool module O0 check | 4.605 → 4.646 | 15.904 → 15.833 |
| 128 transfer statements, check | 4.571 → 4.444 | 14.478 → 14.224 |
| 1024 transfer statements, check | 6.242 → 6.707 | 19.350 → 19.082 |
| 128 generic value bindings, check | 7.037 → 6.677 | 19.381 → 19.362 |
| 1024 generic value bindings, check | 65.182 → 66.321 | 120.575 → 122.094 |
| 128 generic values, object execution | 6.006 → 5.170 | 16.018 → 16.102 |
| 1024 generic values, object execution | 5.001 → 5.272 | 16.537 → 15.982 |
| 128 generic values, bound execution | 5.120 → 4.953 | 15.877 → 15.819 |
| 1024 generic values, bound execution | 4.992 → 5.087 | 15.946 → 15.870 |

The expanded grammar/type contract does not show a general CLI speedup. Small
commands remain in roughly the same startup-dominated interval. The observed
1024-binding checks cost about 1.1 ms more on Linux and 1.5 ms more on Windows;
the samples do not isolate a causal regression. Linux transfer checking is
0.465 ms higher in this cohort while Windows is 0.268 ms lower. Object/bound
execution differences are small and mixed. Retain those observations rather than
advertise only the favorable cells.

The separately isolated [contextual binding result](#contextual-binding-one-optimizer-fixed-point)
measures the useful internal improvement: one optimizer fixed point and
copy-on-change lowering reduce prepared 128-transfer binding from 0.4634 to
0.2524 ms and synchronous allocations from 197,480 to 100,936 bytes. That managed
component result is not substituted for the actual native CLI numbers above.
The [compiler component record](performance-components-v080.md) also documents
the repaired Range<Scope> loop-shape convergence, rather than hiding a previously
nonterminating Map workload inside average timings.

Raw per-image identities, samples and p95 observations are under
`artifacts/ci/42619095812/{linux,windows}/download/.temp/platform-results/language-upgrade/`:
`loading-latency.json`, `latency-128.json` and `latency-1024.json`. The final Native
images are Linux SHA-256 `c828e4135c837da84b77f39c5117f9167742d27ef77830c170de59242772d69e`
and Windows `a76b773498662ef3034221d339bbce5f18ee020c6aca50f7ab804d430f6d3d81`.

## Numeric operands and object ownership after the library expansion

The next optimization target is the interpreter representation, not another
adaptive compilation tier. Ordinary library code now combines short Number
expressions, materialized Range records and captured operation dictionaries.
The existing static inliner and cross-block scalar replacement already eliminate
many private objects; residual runtime allocations therefore need separate
measurements instead of assuming that every source-level Scope survives.

The design follows two related lessons from upstream work. The
[CoreCLR object-stack-allocation design](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md)
ties allocation removal to escape/lifetime information, and observes that
inlining exposes opportunities unavailable at opaque call boundaries.
[Partial escape analysis](https://www.ssw.uni-linz.ac.at/Research/Papers/Stadler14/Stadler2014-CGO-PEA.pdf)
distinguishes the branches on which an object must exist from those on which
scalar fields suffice. msync already applies static private-Scope replacement;
it does not adopt speculative heap materialization or JVM deoptimization merely
because its host runtime supports related optimizations.

The same distinction applies to functions. The
[lambda-set-specialization work](https://www.languagesforsyste.ms/publication/lss/)
tracks higher-order function flow to specialize calls. msync already specializes
its known callable identities; retaining an integer selected target instead of
allocating a new adjusted Function value is a smaller implementation consequence,
not a claim to implement that paper's type system.

The real-process acceptance fixture is
[`number_allocation_journey.py`](../../tests/Msync.EndToEnd/number_allocation_journey.py).
It reads its numeric seed from a file, executes 512 arithmetic-chain iterations,
materializes 64 escaping Scope/Fn objects and checks shared versus independent
captured locations. Runtime-derived negative zero, NaN, infinity and
callee-before-argument mutation are checked as well. Each O0/O2/O3 source,
source-deleted `.mso` and source-deleted contextual `.mexe` route checks six
actual output files. The accepted baseline Native image above passes all nine
routes; the fixture is shared with Windows acceptance. These checks establish
semantics and staging, not a throughput measurement.

### Allocation boundaries

Number expression operands use an internal reference stack with a lazy double
sidecar. A null internal reference marks a computed double, not the source
`null` value (`Value.Null` remains an ordinary reference). Loading an existing
Number preserves its reference; arithmetic chains, negation and comparisons can
consume doubles without constructing intermediate `NumberValue` objects. A
reference consumer such as a local/capture store, function argument or returned
value materializes the ordinary representation. Captured cells and mutable Scope
fields therefore retain their existing ownership and aliasing contracts. Pops
clear references so a reused stack does not retain dead objects; discarding a
numeric result does not box it just to throw it away.

A first prototype used a 16-byte tagged entry for every operand. It needlessly
doubled storage while constructing large reference-only Range literals: the
1024-element probe allocated another 32 KiB through array growth. The sidecar
instead grows only when a computed Number reaches that operand depth. It avoids
charging String, Path and Scope stacks for a parallel double in every slot.

The arithmetic/conversion/comparison helpers also separate scalar dispatch from
deferred task construction. Previously, lambdas captured the loop's instruction
variable, causing a display-class allocation on every instruction iteration,
even for opcodes that did not need a callback. Deferred operations still retain
their dependencies and selected exception span; the ordinary scalar path no
longer creates those closures.

Object ownership has similarly narrow boundaries. An invocation's work-owner
record is an inline value whose optional task/process collections remain owned
by that invocation. Selected call dispatch passes an integer target instead of
cloning an otherwise unchanged Function value. Real captured Function values,
fresh anonymous Scopes and independent empty capture arrays keep their identity;
the implementation does not pool user objects or merge equal-looking instances.
Ordinary empty argument arrays may be shared, and already-contextualized
immutable Path constants need not be cloned again.

### Matched runtime measurements

The Number component probe is
[`NumberExecution`](../../tests/Performance/NumberExecution/Program.cs), built
against frozen before/after managed Release assemblies on .NET 10.0.14. Four
processes run in ABBA order with tiered compilation disabled equally, nine
observations per process and eight executions per observation. Each numeric
case consumes 1024 values; source, serialized `.mso` and contextual `.mexe`
results are checked at O0/O2/O3 before measurement. Bound artifact hashes match
between images, including residual numeric opcode sites: the measured loops
have not become constant returns. The before runtime SHA-256 is
`928646f328b31cb3e7b9f0e5bfff3dc98ff49d88f81bf0b0370477cfe75bebbc`.
The runtime change is `68f6717`; the checked-in probe and RID locks are `c629458`.
Raw samples and after-image identity are in `.temp/number-runtime/paired.json`.

| Prepared VM, 1024 elements | Before → after p50 ms | Before → after allocated bytes |
| --- | ---: | ---: |
| Sum | 1.0106 → 0.9157 | 559,032 → 66,720 |
| Arithmetic chain | 2.4034 → 1.4392 | 1,542,072 → 66,720 |
| Power chain | 2.4580 → 1.0494 | 1,296,312 → 66,720 |
| Numeric comparisons | 1.9947 → 1.6186 | 1,181,752 → 59,424 |
| Captured mutable accumulator | 2.2608 → 1.2133 | 1,542,200 → 66,672 |
| Last element, no arithmetic | 0.9677 → 0.5593 | 362,424 → 42,088 |

These are component observations, not Native AOT or whole-process startup
claims. Concurrent development activity produces broad timing ranges; retain the
raw per-process observations rather than treating the displayed medians as a
portable latency guarantee. The arithmetic-chain allocation reduction is 95.7%.
The no-arithmetic control also improves because it previously paid for dispatch
display classes. Literal-return execution changes from 440 to 264 allocated
bytes; sub-microsecond timings for that control are not interpreted.

The final numeric checker additionally covers 3813 exact-bit and staged
contracts, including signed zero and NaN payload preservation, original
Number references and the seven probe scenarios. Native `math.pow`/`**` can
consume raw double operands through the same `NativeRegistry.Power` calculation
used by its ordinary native entrypoint. No reassociation, implicit fused
multiply-add or algebraic-law assumption is introduced.

The independent object probe is
[`ObjectAllocations`](../../tests/Performance/ObjectAllocations/Program.cs).
A frozen object-only overlay isolates invocation ownership, target selection and
Path recapture from numeric stack changes: direct/captured 256-call workloads
save approximately 47 KiB, an escaping-factory workload saves approximately
61 KiB, and all executable artifact hashes match. Its paired timing samples are
mixed; allocation reductions alone are attributed to this overlay. A work-owner
struct does not imply a fixed per-call saving on every asynchronous route:
an actually suspended state machine may carry a larger saved inline caller.
Raw isolated evidence is under
`.temp/object-allocations/{object-only-paired,object-only-tier0}/`.

Combined runtime observations are under
`.temp/object-allocations/combined-tier0/`. Direct/captured calls retain their
real invocation/capture paths; object factories retain fresh instances rather
than being optimized out. The probe separately exercises real nested transfer
owners and barriers, zero-capture callable identity, cross-origin session paths,
native dispatch and callee-before-argument mutation at O0/O1/O2/O3. These focused
checks accompany the real CLI fixture instead of a blind full-suite rerun.

## Final Native library and VM observations

Final-source images from native run
[42654041790](https://github.com/kleedaisuki/msync/actions/runs/42654041790)
use source `768dfe45a2c09a8eac5779fb8dc466cae9f2df9b`, .NET 10 Native AOT,
default O2. Linux image SHA is
`79d8aa922cc8472dd1423c5ff23bb66615c03465807a91c00db595b045fb52b3`;
Windows is `673a0f863b534c0bf1d7758069ee82c9c113bf5bbf54fc9e1c4d323fdf9861c4`.
These are absolute per-platform observations, not a before/after comparison to
managed JIT or to different CI hosts.

| Prepared Number VM, 1024 elements | Linux p50 ms / bytes | Windows p50 ms / bytes |
| --- | ---: | ---: |
| Arithmetic chain | 0.6250 / 66,696 | 0.6115 / 66,696 |
| Power chain | 0.5044 / 66,696 | 0.5568 / 66,696 |
| Captured accumulator | 0.6387 / 66,664 | 0.5931 / 66,664 |

Each case uses nine observations and eight executions per observation. The
probe verifies exact-bit/staged results and records residual numeric sites and
artifact hashes; the loops are not constant-return timing stand-ins. It reuses
a prepared interpreter. The separate object probe creates a fresh interpreter
for each operation and intentionally includes different ownership overhead.

| Fresh object VM, 256 elements | Linux p50 ms / bytes | Windows p50 ms / bytes |
| --- | ---: | ---: |
| Escaping objects / methods | 9.8237 / 953,586 | 28.2708 / 1,477,496 |
| Captured callbacks | 0.4996 / 122,384 | 0.5153 / 124,432 |
| Path observations | 9.3369 / 788,488 | 27.6559 / 1,306,832 |

OS-specific filesystem and suspended-state-machine costs make the path/object
figures platform-dependent. They must not be presented as a portable speedup.
The no-native fresh-interpreter literal control allocates 1,304 bytes on both
platforms; the reused Number VM literal control allocates 248 bytes. The earlier
matched managed measurements isolate the numeric, ownership and callback
changes; these current Native values describe the shipped execution path.

The bounded cold CLI probe launches fresh processes against warm OS caches,
performs one untimed launch and keeps three raw observations. Terminal timings
use actual Linux PTY / Windows ConPTY, not redirected input or artificial sleeps.
The first List namespace Tab is verified by completing and evaluating real code.

| Current product p50 milliseconds | Linux | Windows |
| --- | ---: | ---: |
| `--version` | 7.462 | 9.580 |
| Check without stdlib | 8.167 | 12.292 |
| Check Bool / Range | 9.981 / 12.082 | 12.176 / 13.375 |
| Check List constructor / size | 25.457 | 23.118 |
| Check meta / shell | 9.315 / 11.098 | 11.517 / 12.695 |
| Actual first REPL prompt | 75.580 | 80.406 |
| First typed prefix repaint | 0.239 | 3.179 |
| First List namespace Tab repaint | 27.400 | 23.984 |

Three observations are a focused latency observation, not a statistical product
limit. Raw p95 values, samples, terminal logs, interpreter allocation reports and
exact images are retained under
`artifacts/ci/42654041790/{linux,windows}/download/.temp/platform-results/`.
`stdlib-cost-work` retains transcripts and semantic completion output. The
corresponding current-source/demand-loading mechanism and matched causal
results are documented with
[`StdlibLoading`](../../tests/StdlibLoading/DemandLoadingChecks.cs) and
[`StdlibLoad`](../../tests/Performance/StdlibLoad/README.md) where applicable.
No CI timing threshold or new runtime speculation tier was added.
