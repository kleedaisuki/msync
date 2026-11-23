# HPIR platform gate and native admission

2026-11-22 · Development snapshots, not v0.9.5 product release acceptance.
HPIR interpretation and x64 code generation consume one bound program.
The [0.9.3 baseline](hpir-0.9.3-baseline.md) and the [direct-call pilot](hpir-machine-blocks.md) define the separate release and mechanism lanes.

The final v0.9.5 evidence is separate from the development snapshots below.
See the [Linux release audit](../../artifacts/validation/hpir-v095-release-audit/README.md)
and the [real Windows Server 2022 NTFS receipt](../../artifacts/validation/hpir-v095-windows-native/README.md).
The packaged Windows Native AOT executable passed the existing metadata and
detached HPIR journeys. Both physical modes preserved live default data,
named streams, read-only metadata, timestamps, and unrelated hard links.

## Platform evidence

| Target and environment | Actually executed | Does not establish |
| --- | --- | --- |
| Linux ARM64 Native AOT, Debian 13 cross-toolchain, QEMU user mode | Full frozen `HpirPipeline` O0–O3: nested calls, aliased captures and vector roots, scalar services, numeric iota/drop/take/for, process and input futures, and detached contextual effects. The separate `HpirRuntime` AOT runner passed deep frames, alignment, physical aliasing, heap collection, process/input ownership and cancellation. An x64-produced relocatable image bound and ran on ARM64; the x64 interpreter and Windows x64 Wine runner both ran the ARM64-produced bound signal image. Exact independent source output/file bytes checked. | Physical ARM64 timing, ARM64 HPIR machine code, immutable installed v0.9.5 CLI, or features added after this source freeze. |
| Windows x64 .NET 10.0.14 CoreCLR, Wine 10 | The Windows host used the Microsoft x64 ABI and Windows executable-page publication. Exact interpreter/native signal differential, 4,103 completed native calls and returns, split stack, mutable alias/capture and GC, host failure and cancellation. Also ran typed Range O0–O3 and the physical runtime's typed input and heap controls. Both modes executed the same ARM64-produced bound signal image. | Native AOT of this candidate on Windows, NTFS transfer durability, Windows physical performance, or Windows process-future coverage (the current subprocess fixture is POSIX-specific). |

The ARM lane uses an actual AArch64 ELF rather than a RID-only build.
The local Docker layer contains Clang/LLD 19.1.7, AArch64 libc,
zlib and ICU, and QEMU user mode. The frozen source includes the direct
ABI and current vectors before the later ready String/Path/LIVE transfer
work; it is not the final product source identity.
[Microsoft's Native AOT guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)
supports cross-architecture compilation within Linux, but not Windows
from Linux. The pinned SDK rejects a local win-x64 AOT publish with
`Cross-OS native compilation is not supported`. The release candidate
was therefore built on Windows, not with another Linux cross-publish attempt.

The full Wine pipeline stopped at `FlushFileBuffers: Invalid handle`
on the detached frozen transfer. The promoted Windows Native AOT
0.9.3 executable (SHA-256 `4dc24dd91c2ca67a1d1fe156a93982c90b856437c845ed5fe224b5ceba403313`)
reproduced that error on the same Wine-mapped destination contract.
Leave the real flush intact and run the frozen effect oracle on real
Windows/NTFS. Wine success in the ABI subset is still evidence, but
does not establish package acceptance.

### Repeat ARM execution without host binfmt changes

Inside the cross compilation environment, with the pinned SDK on PATH:

```sh
dotnet publish tests/HpirPipeline/Msync.HpirPipeline.Tests.csproj \
  -c Release -r linux-arm64 -p:PublishAot=true -p:RestoreLockedMode=false \
  -p:CppLinker=clang -p:TargetTriple=aarch64-linux-gnu -p:LinkerFlavor=lld \
  --artifacts-path .temp/hpir-arm64-build -o "$PWD/.temp/hpir-arm64-test"
export QEMU_LD_PREFIX=/usr/aarch64-linux-gnu
export MSYNC_HPIR_TEST_LAUNCHER=/usr/bin/qemu-aarch64
qemu-aarch64 .temp/hpir-arm64-test/Msync.HpirPipeline.Tests
```

The test-only launcher matters: QEMU can run the ARM program, but a plain
child exec still needs a host binfmt handler. Without it the detached
test's subprocess fails with `Exec format error`; the launcher preserves
the separate compile/bind/run processes without registering anything on
the host. The complete test also needs target ICU: the scalar case runs
under a German ambient culture to verify invariant Number output. Compile
the test without the product's invariant-globalization setting, rather
than weakening the culturally independent oracle. See [QEMU's user-mode
documentation](https://www.qemu.org/docs/master/user/main.html).

On the existing Windows release runner, use the same test project,
`-r win-x64 -p:PublishAot=true` and the installed native linker, then run
the resulting test executable and real package journeys. Record target
runtime flavour and architecture next to the verdict. On native ARM
hardware, run the ARM test executable directly with the launcher unset.
Neither run alone can replace the warmed/pinned performance suite.

## Unmodified Unicode path-manifest correctness

The Linux x64 development runner published with SDK 10.0.403 and Native
AOT 10.0.14 compiled the original
[`unicode-path-manifest`](../../tests/Performance/Corpus/text_paths.py)
source at O2. The source copy was removed before binding, the portable
copy after binding, and the one resulting image ran from another
directory in both physical modes. The native mode explicitly compiled
and published x64 code pages; it does not fall back on emitter failure.

| Distribution | Seed / paths | Input SHA-256 | Exact stdout SHA-256 |
| --- | ---: | --- | --- |
| Reference | 17 / 65,000 | `58a5b5f9ea476140dbf11962f01e419bc2d68189fedebaa75f86ff15b506f4db` | `af0bc226b7a8869a2174baf17879a751b390c99ec609ad7201f3c6d457d7b97c` |
| Unicode-heavy | 503 / 65,000 | `f154c662e6e00ffe8978148a52b11f83de5d33f3af9563ddef543f3f376bdc45` | `a66ee9a0c67b4978d08f28b5db971f24eb442066bbb2884a7c7d1b1e3385bea9` |

Each row matched the independent corpus oracle, the promoted v0.9.3
prepared execution, and both HPIR modes byte for byte. Source SHA-256 is
`6a7763061e95694a2357db64532bbcb2a0ccf3ebec9f0e0d0384c6d8ba4cf1fe`;
the tested bound HPIR SHA-256 is
`cd10a9b66ba699a5fd0a7f703dcb2f755902eeb7eadb68a525894cff268d5fc1`.
The development runner SHA-256 is
`017257029e7e22bb12c1bec219be0b0d323c88717ebd9a6f58ca4bd73b845a5a`.

Replay with the corpus generator `_unicode_manifest(directory, 65000,
seed, unicode_mix=1 or 4)` and the test runner's
`hpir-block-compile`, `hpir-block-bind`, and
`hpir-block-run <image> interpreted|native` commands. Feed each
generated stdin file and compare its stdout with the fixture oracle.
The same Native AOT runner passed `live-text-test` for lazy first pulls,
blank versus EOF, explicit/cancelled close, live file bytes and a
resource-ordered read. These runs establish coverage, not a timing
improvement or the installed CLI release gate.

## Unmodified signal, record and sensor corpus correctness

The Linux x64 managed and Native AOT development runners compiled the
unmodified corpus generators at O2, removed the source copy before
binding, then removed the object and ran from an unrelated working
directory. The independent exact stdout oracle, both HPIR execution
modes and the prepared promoted 0.9.3 image agreed for all six cases.
The Native AOT runner (SDK 10.0.403, runtime 10.0.14) has SHA-256
`f465e20da3c729524954c9046bcb5faa16f200304a40aff7a9792929b007e782`.

| Corpus distribution | Seed / requested items | Oracle shape | Exact stdout SHA-256 |
| --- | ---: | --- | --- |
| `signal-hysteresis` reference | 17 / 65,536 | 3,636 detector transitions | `64f7256dcbb8c8332291f30ff083bb6c9ab21945d83d4a55c1d78d310bab9cdb` |
| `signal-hysteresis` long bursts | 503 / 65,536 | 791 detector transitions | `9bb9e449e62bb7d9a918cd27bc681f02a6b8626c7ae547310c84eaa6dedf2947` |
| `numeric-record-stream` reference | 17 / 32,768 | 28,086 selected records | `53cb85e6f0f24e002719c3ed1c0b507272aded441fdadbec926d0ed2a45c2ace` |
| `numeric-record-stream` sparse selection | 503 / 32,768 | 4,682 selected records | `f43fc20b0f317fb41ba55dd20f518bf432afd57334b3491e906000c0b2a4514c` |
| `bounded-sensor-pipeline` reference | 17 / 12,000 accepted | 15,034 demanded lines | `b203d81ce1f80a2965a358027739101bd749b8d6c9bb4d2fca18d2e22280ce2c` |
| `bounded-sensor-pipeline` high rejection | 503 / 12,000 accepted | 40,318 demanded lines | `be705e764558778a7bd6aff1ff9f0489cf8d37ead7ffbbef9a802e379c3e33d6` |

Generate with `_hysteresis` and `_numeric_records` in
[`compute.py`](../../tests/Performance/Corpus/compute.py) and `stream`
in [`pipelines.py`](../../tests/Performance/Corpus/pipelines.py).
Use period/start/end 1024/512/1024 for long bursts, `sparse=True` for
sparse selection, and `reject=7` for high rejection. The sensor demand
column is the generator's expected count; it is not inferred from
stdout. The separately instrumented `transform-test` proves exact
pull demand with a reader that errors on the next unrequested line,
verifies map/predicate call counts, and observes collection while
8,192 materialized records remain live. The corpus run measures output,
not performance.

## Unmodified collections (development wire 6)

The Linux x64 managed development runner compiled the original
collection sources at O2, deleted the copied sources before
binding, deleted the portable objects, and ran the executables from
another directory in both HPIR interpreted and emitted-machine modes.
The independent corpus oracle and both modes agreed byte for byte.

| Fixture | Entries / seed | Exact stdout SHA-256 |
| --- | ---: | --- |
| B+ inventory | 16 / 17 | `567fe900c18ec986b999e4a3da1a147af473801e63700c2badf7222ca59ee51b` |
| B+ inventory | 3,072 / 17 | `95a6e2e893afc3740311fe2fd49f285687ef933100c8ef5fdb4443b3216137f0` |
| Set deduplication, reference | 8,192 / 17 | `a15af5f520fb162edbc7bb0af4ccaa3c5bd52cb16497d177f668eed303bb3fad` |
| Set deduplication, broad expiration | 8,192 / 503 | `60387cee244d1b427991bf0d62867dff592fa1b3ed7c855a9c3981fe6d633477` |

Regenerate the source, standard input and oracle with the
[`_inventory` and `_dedup` fixtures](../../tests/Performance/Corpus/collections_workloads.py)
(`broad=True` for broad expiration).
The actual stdlib `generate` derives its counted input from
`enumerate(repeat(null,count))`, passes a concrete record to its
selected callback and materializes the callback's results. Container
prototype fields retain their guaranteed-empty producer across member
layout selection. These checks are correctness evidence for the
development image, not Native AOT or installed release performance.

## Unmodified lazy stdlib fold (development wire 6)

The original `lazy-state-and-fold` source from
[`stdlib_experience_journey.py`](../../tests/Msync.EndToEnd/stdlib_experience_journey.py)
compiled at O2. Each run used a separate bound directory, deleted the
copied source and portable image, and submitted from an unrelated
directory. The HPIR interpreter and emitted x64 code produced the four
exact oracle files: `size.txt` = 0, `before.txt` = 0, `total.txt` = 3,
and `after.txt` = 2. Neither run wrote into its unrelated working
directory.

The compile path preserves the concrete Number snapshot through its
lazy map and count window, then through the ordinary stdlib `fold`
argument. The focused O0–O3 variant also uses a dynamic nonzero
count and verifies exactly two direct map calls attributed to the
library loop. It does not infer laziness from the output alone.

## Unmodified `local-manifest-copy` correctness

The full 144-row reference fixture (seed 17) was generated by
`_local_manifest_copy`. The existing corpus `reset` seeded each configured
stale destination, and `verify` checked stdout, every output digest, skipped
paths, and the exact output tree. Linux x64 Native AOT HPIR wire 4 matched
both the independent oracle and packaged v0.9.3: **135 copied files**, **33
stale destinations replaced**, **9 skipped paths absent**, and **6,588,849
bytes**. This is a correctness result, not a speed comparison.

| Route | Bound executable SHA-256 | Stdout SHA-256 |
| --- | --- | --- |
| HPIR physical interpreter | `be0c0e51fd88b0fb9f833c3c53b612469dff34e20da2ccdf3dbdbf9a2de1f2e5` | `2f05e14362a2c8b6393fa10accd81923eb200f61f4b4295cf993e1db994a1a89` |
| HPIR emitted x64 | `15168b31b51168ad9a732cffc8d9c9ce32a84469caf66aaeae97d459e043e10e` | same |
| Packaged v0.9.3 | `151952d8565bf950d3486556487c54d31b0cb7b32386b066b51fd3199582dc30` | same |

Source SHA-256: `4aae4be50b8f18050c5bee01db0db1cd685664cccbc3e4c08adf129473d526bb`.
HPIR runner SHA-256: `f465e20da3c729524954c9046bcb5faa16f200304a40aff7a9792929b007e782`.
The bound artifacts have different captured directories. In each route,
remove the source after compiling the object, prepare into a detached
directory, remove the object, and run with that detached working directory.
All effects must resolve to the captured source directory. Replay HPIR
with `hpir-block-compile`, `hpir-block-bind`, and `hpir-block-run`; v0.9.3
used public `compile`, `compile --prepare`, and `run` with `--no-config
--no-telemetry`. The expected tree and byte-for-byte outputs are retained
under `artifacts/validation/hpir-file-metadata-20261122/`.

The same fixture directory contains a wire-5 CLI transcript for the exact
source and filesystem fixtures from `directory_plan_journey.py`. The
managed production command boundary ran a portable artifact or bound it
with `bind-ir` and ran the executable from a different directory. Both
routes preserved sibling/member overwrite order, the final directory mtime
through aliased destinations, and a genuine source/destination overlap.
This tested HPIR artifact intake, not automatic HPIR source selection.

## Unmodified contextual `namespace_read` correctness

The unchanged `namespace_read` fixture in `contextual_io_journey.py`
replaces a symlink path, asynchronously reads that path, and transfers
the resulting String without an intermediate guest demand. In both HPIR
execution modes (managed host and Linux x64 Native AOT) and packaged
v0.9.3, detached bound execution produced
exactly **8,912,896 bytes** (SHA-256
`46dbf2df5072d2a5d47e6a520ccbc007e3e19bf4be127aa8c4eec140f5ba2f32`),
replaced the symlink, and left its former referent unchanged. Source
SHA-256 is `20fa6c0831229d90747999d60cc367e0625c8b0f22defb65d5463da67310b73c`.
Remove source after compile, remove the object after bind, and run with
only the executable in the detached directory. The wire-5 Native AOT
runner SHA-256 is `59d4eb31250d5a874278a6be48d751e0876c6c23cca8a7b01a77aff0f6c08d39`.
The receipts are under `artifacts/validation/hpir-file-text-20261122/`.
The smaller `file-text-test` and `file-metadata-test` passed on that runner.
`file-text-test`
holds the real write before publication to prove that the read lease and
dependent transfer can both admit with the Text payload still pending.

Wire 6 repeats the unchanged journey after selecting a mixed effect
region. The portable artifact explicitly contains `SharedTransfer` and
`LiveTransferFutureText`. Neither binding nor detached execution needs the
source. Both managed and Linux x64 Native AOT runners reproduced the same
output digest and symlink behavior in the physical interpreter and emitted
x64 modes. The focused `shared-transfer-test` also forced a residual-to-
prepared dependency, a disjoint ready metadata read, and a single
sourceful transfer error without disturbing independent effects. Runner
SHA-256 is `b5b18983d9ebaad02e1c96f5c89dbb7b07d24c6ac379f895d341a2492d1b7342`;
the wire-6 receipt and replay are under
`artifacts/validation/hpir-shared-transfer-20261122/`.

## Unmodified compact Regex boundary correctness

The unchanged `tests/Msync.EndToEnd/regex_boundary_journey.py` passed
source, portable, and prepared routes against packaged v0.9.3 and a
wire-6 Linux x64 Native AOT HPIR driver. The HPIR driver checked both the
physical interpreter and emitted x64; it selected three ready Regex
predicates, a fixed Path selection capture and a live selection transfer.
There was no output during binding. The ordinary product CLI also accepted
the compiled HPIR `.mso` and bound `.mexe`; source was absent in both routes,
and `.mexe` ran from a different directory with no object file. All four
expected output files had the exact same bytes, including the selected
member. The CLI receipt records its explicit HPIR disassembly identity;
the separate source driver does not imply that the installed default source
route has changed.

`regex-test` additionally covers O0–O3, String and Path, invariant matching
under Turkish culture, named replacement, malformed-pattern origin, and
directory membership created after binding or updated by an overlapping
writer. Receipts and the Native AOT driver source are under
`artifacts/validation/hpir-regex-20261122/`; runner SHA-256 is
`a1cbd1e902bcb48792707cab5616b702356ef629b5c385bde7ccff0c82063a88`.

The separate wire-7 `regex.find` fixture exercised first-match fields,
named and unmatched groups, lookbehind over a supplementary Unicode
scalar, an absent match, and two exact pattern carriers. Focused Native
AOT coverage also retained a capture vector through actual guest GC. Both
Linux x64 HPIR engines matched packaged v0.9.3 stdout from identical
source, with source removed before binding and the object absent during
detached execution. See `artifacts/validation/hpir-regex-find-20261122/`
for source, disassembly and output checksums; the runner SHA-256 is
`f357a2411d6641eb2e8477871a8182c5b1c61522959600bb8a60203562f21a89`.

## Unmodified I/O live Text join correctness

The original `reusable-lines` and `unrelated-reader` cases in
`tests/StdlibIOProcess/native_journey.py` passed at O0 and O2, through
source, HIR, object and bound routes, with both `MSYNC_JIT=off` and `force`
(32 cases). The producer driver uses the same HPIR compiler for source
and HIR and delegates object/bound execution to a paired Linux x64 Native
AOT product CLI. The packaged v0.9.3 bound cases agree byte for byte.

In a fresh detached bound O2 execution, the trace records a pending
Process stdout transfer first, a file read with zero related outstanding
dependencies, completion of the typed join (one line, five UTF-16 units),
and only then the source's `process.close_stdin`. The execution span
explicitly identifies the emitted x64 engine. `reusable-lines` yields
`first` and `second` from two separate opens of the same live plan.
Receipts and the producer driver are under
`artifacts/validation/hpir-live-join-20261122/`; CLI SHA-256 is
`769e682739a1ff045df8017f8c965e2e5042b14516c2c4e058952f3ae662ef68`.

## Admission: measure emission separately from execution

A second x64 Native AOT probe loaded each bound signal image once, measured
the first emission and publication once, then alternated interpreted and
native runs in the same process. The same independent signal oracle was
checked after each run. CPU 6; three fresh probe processes per fixture,
seven warmed pairs per process; table entries are medians across the
per-process medians. Application time is wall time inside the probe,
not process startup, binding, or the current public CLI. These are the
repeat results with no active build in the before/after checks; the
initial samples had a concurrent build and are retained separately.
The qualitative outcome was unchanged. See the
[fresh-process pairs](hpir-machine-blocks.md) for startup behavior.

| Input | First emit + publish | Warm interpretation | Warm native | Runs to amortize |
| --- | ---: | ---: | ---: | ---: |
| 64, reference | 0.757 ms | 0.112 ms | 0.031 ms | 10 |
| 64, long bursts | 0.703 ms | 0.128 ms | 0.032 ms | 8 |
| 4,096, reference | 0.769 ms | 7.505 ms | 1.862 ms | 1 |
| 4,096, long bursts | 0.717 ms | 7.458 ms | 1.769 ms | 1 |
| 65,536, reference | 0.717 ms | 143.170 ms | 32.547 ms | 1 |
| 65,536, long bursts | 0.652 ms | 136.213 ms | 30.063 ms | 1 |

For a resident image that runs repeatedly, use the measured model
`C + kN < kI`, where `C` is code emission/publication and `I`, `N`
are comparable execution costs. The table is an amortization probe,
not a sample-count heuristic: guest input size and future work are
not generally known at launch. Emission costs roughly 6–10 times the
64-row per-run benefit, whereas the longer cases cover
emission comfortably. The independent fresh-process pairs also show why
warmed timing does not authorize unconditional native-on behavior.

The adaptive artifact route starts fresh runs in the interpreter. It
charges each executed straight-line segment at its existing control
boundary, excluding host calls and waits. Code is emitted only once the
budget is spent and execution takes a guest backedge or enters a direct
callee. The budget is the larger of 131,072 guest operations and 128
operations per statically emitted instruction. The fixed part rounds up
the roughly 70–82 thousand dispatch-equivalents inferred from the first
0.7 ms publication cost and the measured exact original signal/polygon
instruction counts and interpreter/native gap. The second part scales
for larger code images; neither part depends on source names or an input
row heuristic. This is an admission proxy, not a claim to predict the
remaining loop iterations.

The machine resumes the already-selected continuation with the same SP,
arguments, roots, resource ownership and completed effects. Unsupported
hosts or unavailable executable memory stay in the same HPIR interpreter.
There are no per-operation timers, duplicate serialized IR, shape-specific
admission, or old VM fallback. A focused CLI journey keeps one detached
image and changes only stdin to exercise cold/hot decisions, plus the
existing off/force controls. Target platform and release acceptance still
need the corresponding installed-package runs.
[V8's call-boundary publication](https://v8.dev/docs/wasm-compilation-pipeline)
and [Wasmtime's checkpoint-overhead comparison](https://docs.wasmtime.dev/examples-interrupting-wasm.html) are design
precedents, not msync thresholds.

The frozen AOT probe, source manifest, platform transcripts, data generator
and raw policy samples are retained under
`artifacts/validation/hpir-platform-20261122/`.

### Linux x64 production CLI admission

The production CLI's HPIR artifact entry point and a separate producer
ran as Linux x64 Native AOT binaries built with SDK 10.0.403. The CLI
journey checked alternative admission modes, detached artifact inspection,
dry run and source-attributed failures. The runtime control compared the
instruction/edge/call/import census with physical execution and kept a
live named singleton and pending child-owned read through the handoff. It
also cancelled at the handoff without replaying input or output. This
does not claim the product's source or package cutover.

The unmodified signal and polygon sources were compiled once, removed
before the product bound their .mso, and the .mso was removed before run.
For each workload, the same .mexe received both the exact original input
(reference/seed 17) and the 64-row control. Every run matched the
independent stdout oracle. The timings are fresh subprocess user+system
CPU, seven AB/BA pairs after one warmup, pinned to CPU 6, with telemetry
off. Decisions are from separate recorded runs. All percentages are
median paired changes; timing lanes are independent.

| Workload | Auto decision / observed guest ops | Auto vs same CLI off | Auto vs released .mexe | Auto vs released .mso |
| --- | --- | ---: | ---: | ---: |
| 64-row signal | Below budget / 11,528 | +1.3% | — | — |
| 64-row polygon | Below budget / 32,977 | −2.5% | — | — |
| 65,536-row signal | Direct guest call / 131,083 | −77.7% | −94.7% | −39.9% |
| 16,384-query polygon | Taken backedge / 131,079 | −75.7% | −93.1% | −84.2% |

Cold auto/off CPU medians were about 18 ms including CLI startup; the
differences between auto and eager force at this scale were variable
and do not establish a precise cold speedup. The long original routes
retain the substantial native benefit without paying emission for the
64-row runs. In the source-attributed trace, signal resumed at the
start of its already-entered callee; polygon resumed inside `crossed`
at the selected backedge target. Neither case reconstructed guest state
or re-executed the prefix.

Exact binary digests, raw timing pairs, detached images, trace, and the
replay driver are retained under
`artifacts/benchmarks/hpir-adaptive-promotion/`. The old inputs and
outputs come from the [unmodified corpus receipt](hpir-unmodified-corpus.md).
This validates admission for these Linux x64 artifact journeys, not
other hosts or complete corpus/installed-release performance.

### Linux x64 cooperative Number front

A second Linux x64 Native AOT probe freezes the wire-6 assembly references
and the `cooperative-test` driver. At O0 through O3, both physical and
x64-parent executions observed the root marker before releasing the
original shell-process gate (exit 3, exact final Number 7) and the
portable line-input gate. The portable case also forced a collection
with a parked callback stack and retained its captured record (38).
Additional cases checked captured pending Bool, delayed empty failure
without invoking the callback, and cancel-on-discard.

Run `artifacts/validation/hpir-cooperative-20261122/probe` from the
repository root. The exact input sources, executable, pinned SDK version,
build transcript and managed neighbor gates are checksummed beside
the probe. This is a correctness result for the selected typed terminal,
not a timing or general asynchronous Range claim.

The unmodified `structured-range-elements` case from
`tests/Msync.EndToEnd/stdlib_experience_journey.py` compiled to development
wire 7. It uses two String vectors with stdlib zip, and separately
enumerates a Unicode String snapshot. Both map into Text with ordinary
guest functions and join into file text. After the source and then
portable artifact were deleted, interpreter and emitted x64 runs from
separate unrelated working directories reproduced both original UTF-8
oracles exactly; only the two binding directories received outputs.

| Output | Bytes | SHA-256 |
|---|---:|---|
| `pairs.txt` | 5 | `080f61b79d0cbd89cf5060bfa866c1398501422f0e83a02856c1aea205b51cf8` |
| `numbered.txt` | 7 | `69c6464bf33267725e5a84066cb39bca5f17ac3a2fe98c23467a3330b7f6af7f` |

The original `sorted-algorithms` case from
`tests/Msync.EndToEnd/stdlib_iterators_journey.py` also passes on
development wire 7. O2 source was deleted before independent binding,
and both modes executed the detached images from unrelated working
directories. All 15 file oracles match, including the empty take,
cold counters `0,0,0`, first demand `1,1,1`, complete fresh traversal
`3,3,4`, and stable Scope alias mutation `original` → `changed`.
The output manifest digest in both modes
(`LC_ALL=C sha256sum *.txt | sha256sum` from the binding directory)
is `41ab042825847664758fccaa0c465d06a320aa2d3cbba9a2361dadd429b89af3`.
This is an output and demand-order gate, not a timing measure.

The separate O0–O3 `sorted-range-test` checks one instantiated consumer
for three merge sites with different values and lengths, and a second
consumer for union. This control initializes the shared module captures
before entering the consumer so the observed difference is the Range
recipe, not first-time module initialization. It verifies Number
ordering over NaNs in both VM modes. The same build's
`transform-test` passes the earlier VectorFold, record/collection and
live sensor demand regressions.

The unmodified `terminal-callback-effects` case in
`tests/Msync.EndToEnd/stdlib_experience_journey.py` passes on wire 7
with no source or relocatable input present at launch. Interpreter and
emitted x64 execution from unrelated process directories write, exactly,
`join-calls.txt = 2`, `byte-calls.txt = 2` and
`payload.bin = 00 ff` (SHA-256
`06eb7d6a69ee19e5fbdf749018d3d2abfa04bcbd1365db312eb86dc7169389b8`).
The focused byte route adds an O0–O3 overlapping append and callback
demand control, 8,231 streamed bytes in three guest chunks, and a
replacement failure after multiple full host reads. The invalid source
preserves the previous file and removes its replacement stage.

The unmodified `tests/Msync.EndToEnd/hir_semantic_journey.py` source now
passes end to end on wire 7 with the ready-snapshot `first` and `concat`
paths. Interpreter and emitted x64 modes, launched from unrelated
directories after source and portable deletion, match all 29 files.
This includes `snapshot-before.txt = 0` and `typed-write.txt = 7`,
and excludes the forbidden query effect. The output manifest digest
(`LC_ALL=C sha256sum *.txt | sha256sum` in the binding directory) is
`a8ff4fe481a9e904cf4622772ff0ac7465a352774a48d902320da1639d76a5cb`
in both modes.
`concat-range-test` separately verifies O0–O3 two-segment Number/Text/
Scope behavior, including a right-hand snapshot window whose interior
backing pointer remains valid across real collection. It confirms
Scope alias mutation through both the materialized snapshot and the
original segment cursor, with exactly two packs of five words total.

The original `SOURCE` from `stdlib_stdio_journey.py` passes through
the HPIR-source shadow CLI. An open, silent stdin pipe does not block
`check`, `plan`, `compile`, or `bind-ir`. The source run and both
detached execution modes produce byte-identical stdout and stderr,
including the UTF-8 BOM boundary, CRLF, Unicode, raw read-to-end,
and the two bounded line pulls.

The original `program()` from `stdlib_format_journey.py` passes as a
wire 7 detached O2 image in interpreter and emitted x64 modes, with
all thirteen expected files. A released process waits for
`gate.txt = Range<Number> Process(deferred)`; the formatter then
observes its selected Text field to produce `selected.txt = child payload`.
The lazy Range is never enumerated. The focused `text-delivery-test`
also runs a live Text field handoff across a collected frame at
O0–O3 and checks the Unicode character position on a malformed dynamic
template, without publishing the destination file.

The unchanged format CLI script also passes in the wire 8 HPIR-source shadow:
65 exact file observations across source O0/O2, source-free HIR,
relocatable, and prepared routes, with seven negative diagnostics.
The unchanged non-PTY stdio script passes its behavioral assertions,
including the REPL transcript, and reaches its final Native-specific
`vm.storage.pending_index` SQL assertion. That counter is not emitted
by the typed field carrier: the focused frame test verifies retained
ownership by observing real output before supplying the input.

The original prompt and detached pending-Scope pipe cases also pass
byte-exactly on the wire 8 shadow: the visible prompt or return marker
arrives before input is supplied. The frozen `deferred-dataflow` case
of `scheduling_journey.py` passes unchanged in source interpreter and
source-free x64 modes: the file effect observes pending stdout plus
its suffix after the guest publishes the gate, producing
`background tail` and the original completion marker.

### Linux x64 self-tail frame reuse

The unchanged 100,000-step `sum` source from
`tests/TailCallOptimization/cli_journey.py` formerly issued a recursive
HPIR `Call` on the hot branch. The new selected image transfers the actual
environment and two scalar arguments into the current frame and jumps
after the literal prologue. This is the ordinary HPIR `Jump`, not a VM
call mode or a runtime guard.

The SDK 10.0.403 Linux x64 Native AOT producer compiled the source to a
wire-6 relocatable object. The production CLI bound and ran that object
after source deletion, then ran the bound image after object deletion.
All outputs were the exact `5000050000\n`. The diagnostics of each bound
run independently recorded one `sum` frame, 100,000 backedges, and one
total stack segment allocation including the main frame.

| CLI execution policy | `sum` frames | Backedge hits | Machine instructions in `sum` |
| --- | ---: | ---: | ---: |
| off | 1 | 100,000 | 0 |
| linked | 1 | 100,000 | 0 |
| auto | 1 | 100,000 | 1,268,937 |
| force | 1 | 100,000 | 1,400,007 |

The focused managed and Linux x64 AOT gates additionally checked O0–O3,
using physical and x64 execution, in-place exchange of text arguments,
capture mutation, fresh escaping captures, calls through distinct
environments, and terminal transfers on ordinary recursive frames. The
last two retain CALL/RET. This demonstrates fixed-frame reuse and
production artifact compatibility on Linux x64; it is not a timing result
or the public source/package cutover.

The independently runnable producer and controls, frozen references,
source, disassemblies and raw CLI traces are checksummed under
`artifacts/validation/hpir-tail-20261122/`.

## ARM64 Native AOT refresh: wire 7 and cross-architecture bind/run

A fresh Debian 13/Clang AArch64 cross-publish and QEMU user-mode run
passed the complete O0–O3 pipeline gate and focused persistent-session,
byte-write, and RegexFind controls on genuine ARM64 Native AOT binaries.
The Linux x64 native CLI and ARM64 native CLI also consumed opposite
producer architectures on the unchanged Unicode directory-order
journey: x64 portable → ARM64 bind → x64 emitted execution, and ARM64
portable → x64 bind → ARM64 physical execution. Each produced identical
`astral last` bytes with source and portable input absent at run.
[Build, source manifest, binaries, and exact bytes](../../artifacts/validation/hpir-arm64-wire7/README.md).
This extends portable wire and real host-IO coverage; it is not ARM64
performance, an ARM64 machine emitter, or Windows Native AOT acceptance.

## Windows x64 package: isolated Wine capability and pending NTFS gate

The refreshed wire-7 managed shadow executes a detached HPIR scalar
call on Wine 10.0: interpreter and forced x64 both print exact
`21\r\n`; only the latter records 20 machine instructions and two
machine guest calls. Public source compile, bind and a detached dry
run of nonempty and empty file copies also succeed. Actual transfers
fail identically in both HPIR modes and in the promoted Windows
Native AOT 0.9.3 package.

The failure is **missing Wine progress callbacks**, not an invalid
mapped disk handle. A raw Win32 control used the same retain/flush
pattern on both Wine C: and Z:, with readonly/writable and
empty/nonempty files. All eight copies wrote exact bytes but delivered
no callback; the retained handle stayed zero and `FlushFileBuffers`
returned error 6. A directly opened writable handle flushed in every
same-directory control. [Wine 10.0's CopyFileExW](https://github.com/wine-mirror/wine/blob/wine-10.0/dlls/kernelbase/file.c#L563-L580)
explicitly ignores the callback, unlike the [Windows API contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nc-winbase-lpprogress_routine).
No product change or downgrade of the original flush is indicated.

Existing GitHub-hosted Windows workflows can build a remotely reachable
ref, but the local candidate isn't at such a ref and the repository
has no self-hosted runner. This check did not upload or dispatch.
[The focused native NTFS test](../../tests/IO/hpir_windows_detached.py)
is ready for the same source-cutover package on real Windows: it verifies
detached HPIR compile/bind/run, dry-run nonpublication, live source bytes,
readonly/mtime/alternate-stream metadata, hard-link isolation, zero-byte
copy and both execution engines. The native Windows Native AOT package
gate remains open. See the [actual Wine receipts and native replay](../../artifacts/validation/hpir-windows-detached-20261122/REPLAY.md).
