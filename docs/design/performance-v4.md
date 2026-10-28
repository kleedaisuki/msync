# v0.4 performance acceptance plan

Status: not started. Finish all-phase event admission, namespace receipt wiring,
and the actual native backend gates before collecting acceptance benchmarks or
changing performance-sensitive mechanisms. Correctness test runtimes are not
latency measurements.

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
