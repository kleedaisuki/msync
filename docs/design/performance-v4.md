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

The current preparer retains an initial missing-target creator to preserve its
namespace effect. Closing the one-payload-copy case may require factoring a
file value from its namespace contribution rather than deleting both. Preserve
the original namespace ticket independently of the later byte producer: in
`src1 -> target; "b" -> sibling; src2 -> target`, simply moving the first
insertion's metadata effect to the last instruction changes the parent receipt
winner. An intervening directory/value observer must still prevent erasing a
needed first value. This is an acceptance requirement and an IR design question,
not a claimed implemented optimization or permission to weaken metadata rules.

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
A longer 100-round growth control remains a separate follow-up measurement.

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
