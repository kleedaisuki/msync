# v0.4 performance acceptance plan

Status: active on independently verified, committed components, following the
user's explicit parallel-performance authorization on 2026-10-28. Baseline
`5cafd34` is retained separately; transaction-store, planner and end-to-end latency
investigations have distinct owners. FIFO and capacity/publication corrections
continue independently and are not silently included in that baseline. Current
functional integration and final release acceptance remain separate gates.
Correctness test runtimes are not latency measurements.

Reuse the workload and filesystem distinctions in
[performance.md](performance.md), [performance-planner-v3.md](performance-planner-v3.md),
and [io-linux-v3.md](io-linux-v3.md). In particular, a WSL pathname under `/mnt/d`
is not a native ext4 measurement. Label source, destination, artifact store,
cache state and concurrency independently. Disposable data stays in `.temp`;
retain reproducible procedures and summaries here, not transient raw logs.

## Questions for the first inexpensive probes

| User journey | Measurement | Decision it should inform |
| --- | --- | --- |
| `check` / offline `plan` for independent copies and overwrite chains | Warm and fresh-process milliseconds; scaling with statement count | Locate type/effect/rewrite costs before replacing data structures |
| One finite copy in a large unrelated cwd | Bytes inspected/copied into snapshots, metadata calls, wall time | Decide whether complete read enrollment should become sparse for ordinary file work |
| The user's repeated assignment with both an existing and initially missing target | Logical payload assignments and actual source reads, not just final bytes | Obtain one payload copy without discarding required namespace/metadata effects |
| Repeated unchanged REPL reads | Host-observation bytes and artifact growth per submission | Separate required same-size/same-mtime change detection from avoidable capture/copy |
| Many overwrites in one long-lived session | `Store::retention`, private bytes/files, latency by iteration | Identify version/artifact lifetime growth, not merely initial throughput |
| Two independently completing flows | Actual completion-to-publication and input-response latency | Measure the ready frontier and publication pump, not just byte throughput |
| One and two native children with a small versus large managed root | View creation, join, export, import and publication milliseconds | Attribute sandbox and snapshot costs separately |

The workload should also include the requested macro/checkpoint/rollback script,
physical EOF and cancellation, and real CLI/REPL entry points. Use a reference
or pre-change executable and compare actual outputs, metadata relations and
cleanup state alongside timing. Do not trade away named undo or fixed-version
input proofs for a faster number.

The initial preparer retained a missing-target creator to preserve its namespace
effect. The adjacent-only certified rule below closes the common one-payload
case; general interleavings still need the namespace contribution rather than
blindly deleting it. Preserve
the original namespace ticket independently of the later byte producer: in
`src1 -> target; "b" -> sibling; src2 -> target`, simply moving the first
insertion's metadata effect to the last instruction changes the parent receipt
winner. An intervening directory/value observer must still prevent erasing a
needed first value. Those wider cases remain an IR design question, not permission
to weaken metadata rules or extend the adjacent-only proof.

## Concrete lifetime observation to investigate

At commit `a8ef8ca`, `Store::forget` removes a transaction record but does not
compact the per-resource version vectors. `SnapshotStore::capture_entry` creates
each copied file under one store root; `Artifact` retains that root plus a path,
and individual artifact files are not reclaimed on their last value reference.
Thus a long-lived REPL needs an explicit retention/collection investigation;
successful shutdown cleanup alone is not evidence of bounded live-session disk
use. This is a code observation, not a measured leak-rate claim.

A correct collection design must preserve the committed head, every active
transaction's visible versions/read validation, retained named origins, pinned
file/flow/native-view leases, and unresolved compensation journals. Compiler
reachability matters too: scalar process receipts and named origins may remain
reachable through aliases or captured macro globals even when one user binding
is overwritten. Measure first, then give one component clear collection ownership
instead of adding unrelated cleanup branches to publication paths.

## Acceptance record to add later

Record toolchain/build profile, OS/backend/filesystem, fixture dimensions,
resource limits and cache state; report user-visible milliseconds and retained
resources before and after each chosen change. Use both algorithm/data-structure
and asynchronous-contention probes, but pursue only bottlenecks that matter in
the supplied workflows. No ring engine, watcher cache or general actor rewrite
is justified merely because its API exists.

## Store: structurally shared atomic candidates (2026-10-28)

The measured bottleneck is not byte IO: Manager creates a transactional candidate
by cloning Store before validating and swapping it. Previously that copied every
resource's complete version vector and every retained transaction's pending/read/
origin maps, even for one ordinary read. A long session therefore charged a read
for unrelated committed history. Version payloads remained immutable but all their
reference wrappers were cloned.

`Store` now shares the version table, each version chain, the transaction table
and each transaction record through `Arc`. Mutation detaches the relevant table
and only the touched chain/transaction with
[`Arc::make_mut`](https://doc.rust-lang.org/std/sync/struct.Arc.html#method.make_mut).
Thread transfer/sharing now follows `Arc` auto-traits: a custom value that is
Send but not Sync (or Sync but not Send) no longer gives the corresponding
Store auto-trait. This is an explicit new, unreleased v0.4 API decision; the
legacy v0.3 APIs are unaffected. Thread-local stores still accept other cloneable
values. Production byte Store and filesystem Manager Send+Sync assertions are
compiled as a regression, without any unsafe auto-trait implementation.

There is no lock, changed visibility epoch, weakened validation, deleted history,
new dependency, or public signature change. Candidate commit/receipt mutation
cannot change the old store, including a saved pending transaction. This is
structural sharing, not a claim that histories have been garbage-collected.
The distinction matters: [MVCC reclamation](https://www.postgresql.org/docs/18/routine-vacuuming.html)
must preserve versions still visible to live transactions; this DSL additionally
retains named origins and foreign-author history needed to refuse destructive undo.

### Component method and result

`benches/store_v4.rs` runs via `cargo bench --locked --bench store_v4`. Fixtures
contain actual public Store values (`Arc<[u8]>`, unique eight-byte values), 100
resource keys and 1,000/10,000/50,000 retained versions, plus one active Serializable
reader. Each sample clones the store, reads one key or stages/checkpoints one
replacement, and destroys the candidate. There is one warmup and five samples of
100 iterations; tiny controls use 10,000 iterations. Fixture creation and final
baseline-state assertions are outside timing. A separate untimed pass counts V
clones; no clone-counter operations are inserted into timed values.

Exact Store source blob IDs: baseline `36d233cc7eda49daa127e42d03f56f700f4bdbf4`,
candidate `a50d41c4d3aed36aed84f4adf06affdd76095a7b` (the final additions
are API documentation and production auto-trait assertions, not timing logic).

The first comparable Windows component probe compiled the exact Store source
from archived commit `5cafd34` and the candidate using Rust 1.99.0,
`rustc --edition=2024 -O`, on Windows 11. Existing msync library dependencies
supply only the unchanged TransactionId and Isolation types; the Store operation
body itself is compiled at the same optimization level in both variants. The
checked-in public-API Cargo harness provides the equivalent normal release run.
No filesystem IO, process-loader cost, global cache operation, or network is
inside these component samples. Disposable driver/output files are in
`.temp/perf-store-v4` and are not required to understand the retained result.

| Resources / retained versions | Read candidate baseline -> COW (ms) | Write/checkpoint candidate baseline -> COW (ms) | Untimed V clones, read / write |
| --- | ---: | ---: | --- |
| 1 / 1 | 0.000456 -> 0.000339 | 0.001076 -> 0.001123 | 2 -> 1 / 3 -> 3 |
| 10 / 10 | 0.002130 -> 0.000333 | 0.002863 -> 0.002483 | 11 -> 1 / 12 -> 3 |
| 100 / 1,000 | 0.036805 -> 0.000334 | 0.033907 -> 0.013048 | 1,001 -> 1 / 1,002 -> 12 |
| 100 / 10,000 | 0.145162 -> 0.000389 | 0.243888 -> 0.014045 | 10,001 -> 1 / 10,002 -> 102 |
| 100 / 50,000 | 1.707905 -> 0.000339 | 1.895942 -> 0.018634 | 50,001 -> 1 / 50,002 -> 502 |

The fixed sharing cost can lose on the tiniest mutation: the single-version
write control is 0.047 microseconds slower in this run. It is not presented as
a universal small-script speedup. The useful result is eliminating historical
value cloning on candidate reads, and cloning only the touched chain for writes.
The touched chain remains a vector: writing one extremely hot key with a very
long chain can still copy that chain. No general persistent-tree dependency is
justified by these measurements alone.

### Correctness and remaining lifetime scope

Focused Windows checks passed: Store 17 cases (including property-based arbitrary
checkpoint/undo), Manager 43 real filesystem cases, and four private Store units
(structural-sharing/nonmutation, production auto-traits and receipt binding). Strict changed-
target Clippy passed. The new counted-value unit makes an accidental return to
cloning historical values executable, rather than merely a timing expectation.
All retention counters remain unchanged, including the 50,000-version fixture.
Manager rollback, foreign-edit refusal, sparse namespace receipts, compensation,
case-policy checks and immutable readers remain covered by the existing disk gate.

This change does **not** reclaim old artifact files or forget ended implicit
transactions. A code audit found no Manager/runtime caller of Store::forget;
that lifetime issue is separate and must not be silently fixed by discarding
named origins. End-to-end persistent-REPL latency/artifact-growth measurements
are owned by the latency investigation; the component table is not an end-to-end
speedup or a bounded-disk claim.

### Matched Linux release/public-API control

The public Cargo harness built and ran successfully in WSL (Rust release code,
CPU affinity 0). For the paired control, both drivers were compiled from the
same `benches/store_v4.rs` with `rustc --edition=2024 -C opt-level=3
-C embed-bitcode=no`, explicitly linking either the archived baseline release
rlib or the candidate release rlib and using the same release dependency search
path. The candidate ran first, baseline second; values/counters and all untimed
publication/origin assertions passed. These are heap/CPU operations, not ext4 or
DrvFS throughput measurements; file placement affects loading outside the timer.

| Resources / versions | Read baseline -> COW (ms) | Write baseline -> COW (ms) |
| --- | ---: | ---: |
| 1 / 1 | 0.000231 -> 0.000199 | 0.000728 -> 0.000675 |
| 10 / 10 | 0.000942 -> 0.000230 | 0.001811 -> 0.001332 |
| 100 / 1,000 | 0.017078 -> 0.000218 | 0.017586 -> 0.007887 |
| 100 / 10,000 | 0.123567 -> 0.000209 | 0.118464 -> 0.007409 |
| 100 / 50,000 | 1.148793 -> 0.000197 | 1.174216 -> 0.012368 |

Untimed clone counts match the Windows table exactly. The tiny write difference
changes sign across these environments, reinforcing that there is no established
small-script latency win. The historical-copy elimination is the reproducible
mechanism and large-history benefit. A baseline driver can be rebuilt without
changing archived source files:

```sh
rustc --edition=2024 -C opt-level=3 -C embed-bitcode=no benches/store_v4.rs \
  --extern msync="$BASELINE_RLIB" -L dependency="$RELEASE_DEPS" -o "$BASELINE_DRIVER"
taskset -c 0 "$BASELINE_DRIVER"
```

Use the corresponding candidate rlib with the same flags for the other driver.
The normal candidate smoke remains `cargo bench --locked --bench store_v4`;
`cargo test --bench store_v4` deliberately prints a debug timing skip instead.
## Planner: certified creation fusion and compact proof storage

### Contract and permission correction

The first implementation retains the later instruction ID and operand source
site when fusing **adjacent** complete file assignments to the same initially
missing target. There is no intervening instruction, namespace receipt or value
observer. Moving that insertion across its own overwritten value therefore does
not change its order relative to another namespace participant. The surviving
destination kind is derived as `Missing`, its parent frontier comes from the
creator, and a host-instability marker caused only by that removed creation is
removed. Certificate verification replays these changes from the original owned
program, original context and captured resource premises; it does not stat the
host again. The rule is not extended across sibling writes, reads, checkpoints,
programs, selectors or stream effects.

A permission precondition is observable even when a later file assignment
replaces all final metadata. Copying a read-only file first can make a later
copy fail. Existing DSE and redundant-write removal could incorrectly turn that
failure into success. The planner now captures the local file's writable flag
with its existing resource observation and propagates permission values through
each file-only epoch. A discarded assignment must have an established overwrite
permission and a writable result; unknown permission is not proof. This also
keeps literal inheritance and deferred/remote metadata conservative. A known
writable local creator can still be fused into a filesystem-separated, type-proven WSL source,
without pretending the final source has a local reader capability.

This applies the resource/effect distinction in
[MLIR's side-effect rationale](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
and the overwrite/observation discipline of
[LLVM's MemorySSA-backed DSE](https://llvm.org/docs/doxygen/DeadStoreElimination_8cpp_source.html)
to filesystem metadata and write preconditions, rather than equating byte
liveness with the whole assignment effect.

### Representation and development API

The unreleased v0.4 `Rewrite.epoch` is an `Arc<[InstructionId]>`, shared by all
records of that observation-free epoch. `CommutationProof` left/right effects
likewise use shared immutable `Arc<Effects>` values, created at most once per
node per graph. These are value-checked certificates, not pointer-identity
proofs: mutating a cloned epoch or footprint is rejected by replay. All pair
proofs and their exact effects remain present. The retained legacy v0.3 parser,
compiler and planner APIs are unchanged.

### Measurement boundary and initial probe

`benches/planner_v4.rs` measures public structured compilation, metadata
preparation (including its built-in replay), and a separate pure verification.
It records median/p90 milliseconds, allocation count, additional peak/retained
Rust heap, node/edge/commutation/rewrite counts. Files and source construction
are outside timing. Inputs are existing-target independent copies, existing
file forwarding chains, existing-target overwrite chains, and missing-target
overwrite chains. Baseline is the immutable `5cafd34` archive; the same harness
links baseline and candidate release libraries built with the same toolchain.

The first three-sample exploratory probe used an ext4 bind of the distribution's
`/tmp` into the project fixture path. At N=100 independent assignments it retained
4,950 commutation pairs, allocated 967,034 times, peaked at 37,309,228 additional
Rust bytes and retained 18,487,728 bytes. Preparation was 124.93 ms and separate
verification 24.26 ms. These initial pathname-prefixed timings must not all be
attributed to algorithms: ancestor inspection can still visit DrvFs parents of a
project path, despite the final data filesystem being ext4. The paired acceptance
run therefore uses the same owned fixture's native `/tmp/<unique-name>` alias
for `CompileContext`; the harness verifies that alias has the same device/inode
as the directory created/removed through the project bind. Native-alias and
project-prefix results are separate cells. No candidate speedup is claimed from
this initial probe.
## Refresh: byte-verified reuse of unchanged ordinary file roots (2026-10-28)

The latency probe exposed a different cost from candidate cloning: a quiescent
REPL refresh captured source and destination into fresh artifact files, compared
those copies with immutable values, and retained the unused copied files until
shutdown. Repeating the same 64-KiB assignment therefore created two full payload
copies per round, even when host bytes and attributes were unchanged.

For a completely enrolled ordinary-file root, refresh now opens and compares the
complete host bytes against the previous immutable artifact **before** allocating
an artifact. Equality reuses that artifact but captures fresh permissions and
mtime. Metadata-only changes still publish a foreign version. Changed bytes,
absence or a changed node kind keep the established capture/import path. Complete
directory roots deliberately retain their existing recursive capture behavior;
this is not a directory-wide cache or a general artifact garbage collector.

The file comparison retains normal source/store disjointness, exact invocation
ownership, no-follow/type checks and native IO errors. It reads through the full
file: size and mtime can reject a candidate but cannot prove equality. An opened
file becoming a non-file is rejected before byte reads; immutable artifacts are
never hardlinked to live host files or chmod'ed for reuse. All roots are still
observed before a candidate Store swap, and foreign authorship/origin conflict
checks are unchanged. There is no implicit-history deletion or promise of
bounded storage when contents genuinely change.

### Actual persistent-REPL acceptance

The latency owner's matched native-WSL run used baseline commit `5cafd34` and the
preserved candidate binary SHA-256
`72fc28abcf8656f0e88a386ef50dd71ca6019b230c778c2137be31021e3b0d61`.
The latter includes COW Store and the concurrent planner work; this single-
assignment workload does not exercise adjacent-write fusion. Sparse-destination
changes were not present in that binary. Both variants used the same existing-
target, 64-KiB stable-copy REPL workflow, two warm rounds plus 32 measured rounds,
and CPU affinity 0. Open binary/source/destination/artifact descriptors reported
native ext4 device 2096 and filesystem magic `0xef53`. The binary was outside the
managed cwd to avoid an unrelated payload-enrollment confound. No global cache
flush or storage-durability guarantee is implied.

| 34 total rounds | Baseline | COW + byte-verified reuse candidate |
| --- | ---: | ---: |
| Retained private artifact files | 68 | 2 |
| Retained private payload bytes | 4,456,448 | 131,072 |
| Command latency p50 (ms) | 1.1820 | 1.0127 |
| Command latency p95 (ms) | 1.8436 | 1.3991 |

The executable round-trip and file-content/metadata controls passed for both.
This is one controlled 32-sample latency distribution, not a p99 service promise
or a claim that every script improves. Artifact counts expose the mechanism more
strongly than these sub-millisecond differences: the candidate retains initial
needed immutable values rather than newly abandoned refresh copies.
Reproduction uses `benches/latency_v4.rs` and `benches/latency_v4_linux.sh`; raw
matched-run output is disposable `.temp/perf-v4/results/latency-paired-native-before-sparse.txt`.
The additional REPL-only control passed with 100 measured rounds plus two warm
rounds per variant (102 assignments). Baseline retained 204 files / 13,369,344
bytes; the preserved candidate still retained only 2 files / 131,072 bytes.
Alternating baseline/candidate p50/p95 was 1.867141/3.092892 ms versus
1.231073/1.515107 ms. Source, destination and artifact descriptors again reported
device 2096 and magic `0xef53` under the private native-ext4 bind. This confirms
the avoided-copy growth mechanism for this stable-content workflow, not a
general history-GC guarantee. Raw control: `.temp/perf-v4/results/latency-store-repl100.txt`.
Set `MSYNC_PERF_ONLY_REPL=1 MSYNC_PERF_REPL_ROUNDS=100` for this bounded control
without repeating the CLI-startup cases 100 times.

Focused Windows Manager acceptance passed all 46 cases in one run after the implementation,
including atomic failed refresh and sparse coverage. Two new tests cover 100
unchanged observations with zero additional artifacts and fresh readonly/mtime
metadata, and same-size/same-mtime foreign edits that preserve the old immutable
snapshot and prevent named-origin undo. An additional focused tail test passed
with only the last byte of a 128-KiB+1 file changed while metadata was restored,
proving that comparison cannot stop at one buffer. Strict changed-target Clippy
passed on the final source. These checks are independent of the end-to-end latency samples.
## Latency: real CLI/REPL workloads and pathname controls

### Method and provenance

`benches/latency_v4.rs` is a standalone release probe; it launches actual CLI
children and drives persistent REPL stdin/stdout. It is not a parser-only timer
or a correctness-suite runtime. Baseline `5cafd34` was built from the immutable
project-local archive with Rust 1.97.1's normal release profile. Its preserved
Linux executable has SHA-256
`8f098280c4578b226951c94b2ce64768a66f1f2af7c0fc2b1f0543d4f0db6cec`.
The first combined candidate (`72fc28ab...`, preserved executable SHA-256
`72fc28abcf8656f0e88a386ef50dd71ca6019b230c778c2137be31021e3b0d61`)
contains Store structural sharing, unchanged-file refresh reuse, planner work and
functional stream integration. This is a combined application comparison, not
an attribution of every end-to-end change to Store alone. The one-assignment
cases do not activate overwrite-chain planner fusion.

The Linux runner creates a private root mount namespace, binds the distribution's
existing `/tmp` into project `.temp/perf-v4/ext4`, then drops to UID 1000 before
running the probe pinned to CPU 0. Fixtures are created and cleaned only through
that project mount. The `native` pathname control addresses the same owned
fixtures through `/tmp/<unique-name>`; `project` keeps the project-prefixed
pathname. The distinction matters because ancestor checks of a project-prefixed
path may still visit DrvFs, even when its final data filesystem is ext4.
`findmnt` reports `/dev/sdd[/tmp] ext4`. Opened binary/source/destination/artifact
file descriptors are inspected through `/proc/<probe-pid>/fd` with `stat -f -L`:
all measured native cells reported device 2096 and magic `ef53` (GNU stat names
this filesystem family `ext2/ext3`). Thus data and loader location are verified,
not inferred from a spelling. The WSL launcher and release build are outside
all samples; no global cache flush, persistent mount or SSH change is made.

CLI samples include child creation, stdin, operation, output handling and exit.
Each executable receives two warmups per case, followed by 15 recorded samples
in alternating AB/BA order. Source contents and image pages are warm; these are
fresh-process startup measurements, not storage-cold measurements. Each source
has 65,536 deterministic bytes and every copy is checked. REPL samples time one
assignment plus a `/pwd` output barrier in a persistent process; output is checked
after the barrier. Its first two assignments are warmups and 32 are recorded.
Reported p50/p95 use `floor((n - 1) * p)` order statistics, not confidence
intervals or universal tail bounds.

The independent-flow case admits two TCP-to-missing-file jobs, holds the first
peer open, then closes only the second peer's write half. No stdin command is
sent between that EOF and first observing the complete fast file. A 1 ms polling
observer therefore reports EOF-to-*observable-file*, not an exact internal
commit timestamp. Only after file visibility does the probe send `/pwd` and time
its acknowledgement; the main thread cannot answer that barrier during its
synchronous publication. It then closes the slow peer and verifies final bytes
and preservation of the later logical namespace receipt's parent mtime. Nine
recorded samples follow one warmup. This tests progress without a keystroke and
head-of-line blocking as well as latency.

### Matched native-alias comparison before sparse enrollment

| Journey | Baseline p50 ms | Combined candidate p50 ms | Baseline p95 ms | Candidate p95 ms |
| --- | ---: | ---: | ---: | ---: |
| `--version` | 0.623 | 0.638 | 0.887 | 0.742 |
| `check`, one copy | 0.731 | 0.721 | 1.375 | 1.093 |
| Offline `plan`, one copy | 0.931 | 0.861 | 1.044 | 1.589 |
| Run, initially missing 64-KiB target | 5.044 | 4.915 | 5.799 | 7.495 |
| Run, existing 64-KiB target | 5.098 | 4.817 | 7.720 | 7.374 |
| Existing target, unrelated 1,000 files + 8-MiB ballast in cwd | 5.338 | 4.980 | 7.674 | 6.568 |
| Missing target, the same unrelated cwd contents | 80.921 | 79.581 | 83.093 | 82.082 |
| Persistent REPL overwrite + `/pwd` | 1.182 | 1.013 | 1.844 | 1.399 |
| Second flow EOF to observable file | 1.084 | 1.089 | 1.130 | 1.112 |
| Second flow EOF to REPL acknowledgement | 1.158 | 1.141 | 1.467 | 1.199 |

The warm REPL retained 68 private artifact files / 4,456,448 bytes at the end of
34 assignments on the baseline, versus 2 files / 131,072 bytes on the combined
candidate. This is evidence of successful unchanged-file reuse for this specific
stable-byte, stable-metadata workload, not general history compaction or bounded
storage for arbitrarily changing values. It does not erase named origins or
foreign-conflict history. Raw samples are disposable
`.temp/perf-v4/results/latency-paired-native-before-sparse.txt`.

The important remaining workload effect is missing-target enrollment: unrelated
cwd contents cost about 76 ms beyond the small case, whereas the existing-target
control is nearly unchanged. Inspection confirms `destination_envelope` captures
the entire existing parent for a missing leaf. This motivates explicit payload
coverage versus namespace-metadata coverage, not a new I/O engine or UI timer.
The next experiment will use the existing sparse namespace/complete-child
Manager operations for a proven stable finite exact destination while preserving
full opaque-program and directory coverage.

### A rejected interpretation

An initial pilot placed the staged native executable *inside* the measured cwd.
It observed roughly 22 ms for a missing 64-KiB target and 14 ms for EOF-to-REPL
acknowledgement. Those are not the clean small-workload baseline: the missing
parent capture also enrolled the unrelated multi-megabyte executable. Moving
the executable into a sibling `bin` and making unrelated data explicit removes
the apparent 14 ms acknowledgement delay. The corrected independent-flow result
is about 1.16 ms, already near the 1 ms observation granularity. These controls
do **not** justify changing the REPL's 20 ms service policy or attributing the
pilot to an event-wakeup defect. The old pilot files remain as diagnostic data,
not acceptance speedups.

Reproduce after compiling the std-only probe outside the timed region:

```sh
rustc --edition=2024 -O benches/latency_v4.rs -o target/perf-probe/latency-v4
# Inside WSL as root; the runner self-unshares mounts and measures as UID 1000.
bash benches/latency_v4_linux.sh BASELINE CANDIDATE 15 native moesegfault
bash benches/latency_v4_linux.sh BASELINE CANDIDATE 15 project moesegfault
```

`MSYNC_PERF_REPL_ROUNDS=100` changes only the recorded persistent-session count.
`MSYNC_PERF_PROFILE_DIR` requests untimed real CLI trace controls instead of the
latency matrix. Trace files are intentionally outside the temporary fixture.

### Matched native-alias results (2026-10-28)

Environment: x86_64 WSL2 Linux `6.18.33.2-microsoft-standard-WSL2`, Rust
`1.97.1 (8bab26f4f)`, LLVM 22.1.6, one CPU-affine process (`taskset -c 0`),
UID 1000 for all measurements. Source fixtures reside on `/dev/sdd[/tmp]` ext4.
The drivers use the same inode-checked native alias and fixed-width unique root
names. Input creation is outside timing; source/metadata caches are warm, with
no global cache eviction, output copy or fsync in this component probe. Each
cell has one untimed warmup and seven timed samples; baseline/candidate order
alternates by case/size cell. A separate untimed preparation measures Rust
allocations and peak/retained heap (not process RSS or kernel cache memory).

The candidate release snapshot also contains Store COW/refresh changes, but
these compiler/preparation/verification workloads do not invoke Store or runtime
execution. Thus the independent-copy heap change is attributable to planner
representation/analysis, not a transaction I/O throughput result. The candidate
also includes the permission correction and creation fusion above; overwrite
latency is therefore not an isolated Arc-only comparison.

| Independent existing-target copies | Preparation median ms, base -> candidate | Verify median ms | Peak Rust bytes | Retained Rust bytes |
| --- | ---: | ---: | ---: | ---: |
| 10 | 0.989 -> 0.774 | 0.126 -> 0.086 | 389,752 -> 159,688 | 184,672 -> 69,600 |
| 50 | 18.033 -> 9.289 | 5.119 -> 0.879 | 7,844,928 -> 889,360 | 3,883,496 -> 405,512 |
| 100 | 76.015 -> 36.855 | 26.112 -> 3.712 | 30,533,724 -> 2,051,132 | 15,190,128 -> 948,432 |
| 200 | 198.594 -> 106.820 | 70.345 -> 10.124 | 120,404,408 -> 5,148,120 | 60,047,860 -> 2,418,916 |

All 45/1,225/4,950/19,900 commutation pairs remain present. At N=200, peak heap
falls 95.7%, retained heap 96.0%, and pure verification median 85.6%. Allocation
count falls from 1,941,918 to 755,464. This is not a claim of linear-time graph
construction: the number of explicit pair records and pair checks is still
quadratic. Sharing removes deep copies of immutable footprints, not the proof.

Forward-chain N=200 preparation fell 171.495 -> 72.479 ms, verification
77.413 -> 13.326 ms, peak heap 121,422,712 -> 5,124,168 bytes. Overwrite controls
show the cost tradeoff: existing-target N=10 preparation rose 0.432 -> 0.480 ms,
and N=200 53.479 -> 58.483 ms, while retained heap fell 736,728 -> 418,352 bytes.
The permission guard is required for correctness and this regression is not
hidden. Missing-target overwrite N=200 retains one rather than two payload
instructions (199 rather than 198 rewrite records), with preparation
46.913 -> 46.080 ms and peak heap 1,855,829 -> 1,544,376 bytes. The actual runtime
control additionally verifies one assigned file, final bytes, permissions and
source-mtime correlation. Neither that count nor these metadata timings is a
claim that snapshot storage performs only one physical disk write.

Compilation is a negative control rather than an advertised win: the frontend
was not intentionally optimized and its sub-millisecond results vary in sign
(for example independent N=100 0.239 -> 0.442 ms; N=50 0.206 -> 0.128 ms).
The durable conclusions are the deterministic proof storage reduction, unchanged
certificate information and measured preparation/verification benefit, not an
across-the-board startup or parser speedup.

Reproduction uses the maintained harness and private-mount runner:

```sh
# Build both release libraries with the same toolchain. Point these at their
# immutable saved rlibs and matching Cargo release dependency directory.
rustc --edition=2024 -O -C debuginfo=0 -C codegen-units=1 benches/planner_v4.rs \
  --extern msync="$BASELINE_RLIB" -L dependency="$RELEASE_DEPS" -o "$BASELINE_DRIVER"
rustc --edition=2024 -O -C debuginfo=0 -C codegen-units=1 benches/planner_v4.rs \
  --extern msync="$CANDIDATE_RLIB" -L dependency="$RELEASE_DEPS" -o "$CANDIDATE_DRIVER"
MSYNC_PERF_USER="$USER" sudo --preserve-env=MSYNC_PERF_USER \
  unshare --mount --fork bash benches/planner_v4_linux.sh "$BASELINE_DRIVER" "$CANDIDATE_DRIVER"
# Optional filters: MSYNC_V4_PLANNER_CASE=independent,
# MSYNC_V4_PLANNER_SIZES=10,100, MSYNC_V4_PLANNER_SAMPLES=7.
```

The accepted baseline is `5cafd34`; saved rlib SHA-256 is
`bcfcdc90ba935d69daa52fccfb00bccbfa562a4c297d90c8e4265c5f8d161ac7`, and the
candidate rlib is
`e156590c7c59ce0361a764435212d34f0e35e4ae2ab74424587be1bee44e8861`.
Their same-source drivers are respectively
`429dee4bd9557c6e93e645729238da1d38eb8843eca09dd75ae7aacea40cc10c` and
`1a9c301a271f3419cdc3f759205aab6a9e894cea1ad3796858bb17c76e956045`.
Disposable raw output is `.temp/perf-v4/planner-paired-native.txt`; those archived
paths are not dependencies of normal tests or the maintained Cargo benchmark.

Correctness gates so far: Windows preparation 39/39, independent optimization
controls 6/6, HIR graph 68/68 and effect-sharing/tamper control 1/1; strict library,
binary, new-test and benchmark Clippy. Linux source-level integration and actual
cross-WSL namespace proofs remain separate gates rather than inferred from this
performance experiment.
