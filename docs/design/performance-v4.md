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
