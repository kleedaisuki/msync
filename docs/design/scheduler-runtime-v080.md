# Runtime scheduling cost in msync 0.8

## Workload and scope

The November 9 follow-up targets the C# runtime scheduler rather than adaptive
VM dispatch. The accepted starting native image is source `ff26545`, Linux
SHA-256 `ca27463e899595a838b27861ef1328ac2f33b413b0d628ae00e58aaf60741d46`
from native run `42582869154`. Its exact path and platform provenance are retained
by the delivery owner. The independent Core call-target correctness repair is
not a scheduler change and must not be mixed into a claimed scheduler speedup.

The [runtime contract](runtime-v080.md) owns resource dependence, directional
barriers, process lifetime and retry semantics. The earlier
[scheduler performance note](performance-scheduler.md) describes the preserved
Rust scheduler, not this implementation. New measurements must distinguish
source compilation, admission, waiting, and actual file I/O; source-inclusive
timings alone cannot establish an interpreter scheduling cause.

## First discriminating question

The existing prefix index gives affine node visits for independent sibling
copies. That does not measure retirement: completed path nodes remain in the
trie, so repeated directory observations can revisit historical subtrees.
The initial concrete probe is an actual copy followed by explicit acquire and
`fs.exists` on its output directory, repeated with fresh destinations. It keeps
byte oracles and counts resource visits rather than introducing timing gates.

The first prepared-executable probe reproduced `9,634` visits for 100 copies and
`546,484` for 1,000: exactly `N² / 2 + 46.5N - 16` for that fixture's path depth.
Independent same-depth copies without the directory observation used `3,684`
and `36,984` visits (`37N - 16`). These are real-copy structural observations,
not accepted Native AOT latency measurements. All 64-byte destinations matched
their own input generation.

Retirement and conflict lookup have different obligations. A read needs writers,
not readers. Scanning all active readers just to clean them during every read
would introduce a new quadratic shared-source workload. Successful completion
can retire dependency bookkeeping, but operation-result ownership remains:
discarded transfers returning false, faulted tasks and cancellation must still
be reported through their established owner/barrier path. After-region boundaries
currently index the owner's task list, so this work does not compress that list
as an incidental cleanup.

The implementation therefore associates dependency registration with the
existing pending-work object. Successful owner/barrier/selected-process joins
can remove that work from its exact reader/writer nodes and prune empty parent
chains on the VM owner. Failed or canceled Tasks remain represented; successful
Tasks returning false may retire resource edges while their operation result is
still retained by the existing failure-accounting path. Owner task-list length
and region indices do not change. This avoids a completion callback per task,
a second Task-to-registration dictionary, or a scan of active readers during
ordinary read/read lookups.

## Engineering and research connections

The [.NET 10 Task implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs)
already specializes `WhenAll` for arrays, lists and spans. Generic result joins
also materialize result arrays. Prefer the existing non-result primitives when
the scheduler only needs completion, and measure the real allocation difference
before introducing a custom completion aggregator or task pool.

[A Granularity Characterization of Task Scheduling Effectiveness](https://arxiv.org/abs/2602.20561v2)
connects scheduling overhead to dependency topology, not only task count. The
useful implication here is to compare independent frontiers, dependent chains,
and completed-subtree observation workloads separately. Its scientific task
runtime model is not evidence that file transfers should be batched across
source-visible errors, resource hazards or acquire/release boundaries.

The normal path remains one owned same-directory stage, atomic replacement and
at most one file fsync. No rollback registry, global drain, polling worker,
filesystem-observation cache or arbitrary task-count quota is added to reduce
the measured cost of scheduling.

## Accepted implementation and measurements

The implementation is `c0b21ab`. Independent focused checks in `1633d78`
exercise actual completed frontiers, retained failure/cancellation dependencies,
late-registration identity, and source/object/executable failure/retry behavior.
The affected selected-process, owner, inherited-acquire, After-region,
acquire-break, deferred-dataflow and native-independent gates passed as source
and bound executable journeys. Their accepted report is reused rather than
turning this change into another full runtime matrix.

The same-probe Native AOT comparison adopts the change for completed-frontier
workloads: 1,000 copy/acquire/directory-observation operations fall from
1,630.91 to 1,440.28 ms median, with resource visits falling from 551,482 to
24,000. This fixture's repeated pruning/reconstruction increases allocation
from 12.44 to 16.10 MB. Independent and dependent-copy controls do not establish
a general transfer-throughput speedup. See the complete hashes, raw samples,
phase definitions and trade-offs in the C# 0.8 section of
[the scheduler performance record](performance-scheduler.md).

The next integration responsibility is the targeted native Linux/Windows
contract selection, not a new object pool or a replacement scheduler. The
allocation trade-off is retained explicitly rather than expanding this bounded
retirement fix into unrelated machinery.
