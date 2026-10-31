# v0.6 interpreter and runtime performance

The final accepted image and measurements are recorded in [Accepted final image](#accepted-final-image-physical-hotcold-split). Earlier rejected candidates remain as causal investigation evidence, not current performance references.
## Baseline and workload decomposition

The pre-optimization release is frozen at
`target/performance-v6/baselines/msync-v060-before`, SHA-256
`ede9b2b020a21f96cb6149866d3f9ac98e1d6189481fe2bcccea470734ec06ca`.
The matching source archive, environment and rlib are saved in the same directory.
All fixtures and disposable measurements belong under repository `.temp`.

Separate four costs instead of attributing every improvement to file throughput:

1. Pure control interpretation (a typed string range, branch, and same-typed bool
   update) without filesystem payload work.
2. Repeated directory selectors: fresh enumeration every invocation, stable regex
   program, one validated `config.toml` entry. This distinguishes pure regex/plan
   overhead from metadata observations, which must not be cached across effects.
3. Current-block ready-frontier administration, measured against the actual old
   scheduler source with equal dependency graphs and four simulated workers.
4. The production file-to-TCP path, including open/connect/EOF, with kernel and
   buffered mechanism crossover measured independently before choosing policy.

The corresponding reproducible probes are `benches/control_runtime_v6.rs`,
`scripts/bench-block-frontier.sh`, and `benches/io_v6.rs`. Real CLI object/IR,
publication, FIFO, cancellation and process journeys remain acceptance evidence;
a fast no-op graph alone cannot establish correct transfer behavior.

## Borrow immutable control plans

Previously every `control_region` entry deep-cloned its entire `Region`, even
when no launch identity needed to change. Every range cursor also cloned its
source values and predicate/transform expression trees. These are immutable
plans, not invocation-owned state.

The interpreter now borrows a region by default. Only a body containing a direct
program launch uses the existing fresh-process rewrite; nested bodies make that
decision when actually selected. Range cursors borrow source/stage plans and own
only cursor progress, buffered reverse values and other execution state. This
keeps static types, body-local lifetimes, per-iteration process identity and
cancellation semantics unchanged.

A bounded 16-entry cache reuses only compiled full-name regex automata. Directory
entries, path existence and filesystem snapshots are never cached here. The
cache is runtime-session-local, bounded independently of dropped language
bindings, and lazily populated only when a range is first polled. `take(0)` still
does not initialize its source. This follows the regex project's explicit
[compile-once guidance](https://docs.rs/regex/latest/regex/struct.Regex.html#method.new),
without introducing a second regex engine or a new dependency.

## Completion-driven idle waiting

See [runtime activity contract](runtime-activity-v6.md). Readiness subscriptions
reuse completion tokens from existing process/flow/acquisition/finite-publication workers. A program
has a distinct fully-drained completion signal rather than treating leader exit
as completion. Waits are cancel-safe, non-consuming, and resolve on **any** ready
work, not whichever child was launched first. There is no observer thread/task
per operation and no periodic timer in the runtime readiness API.

This uses the existing Tokio primitives rather than implementing an executor:
[CancellationToken futures](https://docs.rs/tokio-util/latest/tokio_util/sync/struct.CancellationToken.html)
retain completion state, so registration/notification races cannot lose an event.
`tests/runtime_activity_v6.rs` exercises real child processes, delayed inherited
output and canceled readiness subscriptions.

## Causal observability

Linked, poll-scoped spans provide the hierarchy `interpreter.block` ->
`interpreter.prepare` / `interpreter.schedule` / `interpreter.instruction` ->
existing copy/task/transport/publication spans. Block and instruction IDs remain
semantic identities; fresh runtime program identities map back to launch nodes.
`scheduler.ready` records ready/running counts at scheduling decisions, never
per-byte events. `runtime.wait` distinguishes finite capacity, flow completion,
program completion and background readiness subscriptions. Failed speculative
preparation records its fallback reason without falsely reporting a successful
user operation as a terminal execution failure.

Disabled tracing bypasses the future poll wrapper directly and does not format
attributes or allocate active spans. Linked spans, unlike TLS guards, can safely
cross suspension points; context attachment covers only each actual future poll.

## Filesystem control footprints

Dependency comparison does not turn a metadata/unlink operand into a managed
transfer target. With no active flows it performs no normalization probe. With
active flows it reuses the planner's existing-prefix canonicalization on a worker,
following symlinked ancestors without importing transfer's no-follow admission
policy. Existence/enumeration compare target identities; unlink compares the
resolved parent plus the untouched final entry. Missing prefixes retain the
planner's conservative key handling; actual operation errors belong to metadata
or unlink, not speculative footprint construction.

The initial reuse of transfer normalization had rejected a valid `delete link`;
a subsequent parent-only variant still rejected valid symlinked ancestors only
when unrelated flows were live. A bounded CLI discriminator established the
inconsistency before this shared observation-key fix. Ordinary transfer
publication policy remains unchanged. The held-two-socket readiness regression
also exercises final-link unlink and ancestor-link existence/deletion while the
unrelated streams remain live.

## Results

Mechanism-specific IO evidence is recorded in
[Linux IO](io-linux-v3.md#v06-native-linux-dispatch-crossover-probe) and
`io-linux-v6-crossover.csv`. Frontier evidence belongs in
[frontier investigation](block-frontier-performance-v6.md).
The shared integration window verified the 13 frontier tests, three new native
IO policy/stream tests, three original activity tests plus the two-held-flow
identity case, the abandoned-finite readiness case, and two keyed resource-limit
tests. Existing selector/control CLI journeys also passed incrementally, including
the repaired symlink-unlink regression. These are correctness gates, not speedup
measurements.

Final interpreter paired before/after measurements will be appended after the
frozen candidate build; exploratory timings are not release acceptance claims.

### First frozen candidate: accepted mechanisms, rejected pure-control regression

On the documented native Linux/ext4 guest, the first optimized image
`7fc6ff13304151ff22b1822d0b0da80a339d1a10009c6f7bf7a1998df642e220`
was measured with CPU affinity 0-3 and no other builds/probes running. All image,
rlib and probe hashes matched at the beginning and end of the exclusive window.
This candidate is diagnostic evidence, not the final acceptance image.

Frontier-only measurements use the actual preserved old and new source in one
optimized process, sparse instruction IDs, four simulated workers, two warmups
per arm, seven alternating AB/BA samples, and completion/checksum validation.
Initialization is excluded from the frontier column:

| Shape | Nodes | Before frontier median (ms) | Candidate (ms) |
| --- | ---: | ---: | ---: |
| Independent | 100 | 0.289855 | 0.016463 |
| Independent | 1,000 | 49.315898 | 0.177985 |
| Independent | 10,000 | 6,663.798202 | 1.980673 |
| Chain | 100 | 0.463763 | 0.021767 |
| Chain | 1,000 | 104.710817 | 0.194708 |
| Chain | 10,000 | 12,957.402783 | 2.064400 |
| Fanout | 100 | 0.398064 | 0.022890 |
| Fanout | 1,000 | 90.091632 | 0.262630 |
| Fanout | 10,000 | 10,793.409726 | 2.916860 |

At 10,000 nodes, initialization also fell from 1.184/6.778/4.023 ms to
0.364/2.242/1.319 ms respectively. The dominant result is eliminating repeated
whole-frontier/state scans: the old independent frontier grew about 135 times
when N increased tenfold, while the candidate grew about eleven times. This is
scheduler administration, **not thousands-fold real-file copy throughput**.
Seven samples do not establish a tail distribution; the candidate independent
10,000-node maximum included a 12.336 ms outlier despite a 1.981 ms median.

The real interpreter probe uses exactly the same typed workloads and before/after
rlibs, seven alternating AB/BA process pairs, 2,000 iterations, one untimed warmup
per case/image invocation, and a runtime result invariant:

| Case | Before median (ms) | Candidate median (ms) | Median paired ratio | Candidate wins |
| --- | ---: | ---: | ---: | ---: |
| Pure control | 7.764128 | 12.638445 | 1.581874 | 0/7 |
| Fresh repeated selector | 314.734500 | 145.012451 | 0.460745 | 7/7 |

The selector improvement is real, but the 58% pure-control regression is not
acceptable as a disabled-telemetry cost. An early return inside an async tracing
wrapper does not erase the wrapper's physical future layout: its inline optional
recording state still occupies every containing boxed future. The next causal
probe compares guard size, allocation requests and requested bytes using
`scripts/build-control-alloc-probe.sh`. Recording state moves out of line only
when active; the disabled guard becomes pointer-sized. Enabled tracing pays one
explicit recording-state allocation per linked span. This preserves all trace
phases, typed failures, causal ancestry and the separate non-Send TLS guard.
Final measured results for that repair must follow before claiming the default
interpreter path improved.

### Causal allocation probe: a thin guard alone is insufficient

The second frozen image is
`015645dc073986de9743c082ade38c6361cbd385ce754a8fed261065358a6b97`.
System-allocator delegation over the same 2,000-iteration constructors produced:

| Pure-control image | Linked guard size | Allocation requests | Requested bytes |
| --- | ---: | ---: | ---: |
| Before optimization | 160 B | 42,164 | 35,004,586 |
| First candidate | 160 B | 26,163 | 112,559,482 |
| Thin linked guard | 8 B | 26,163 | 79,776,714 |

These are allocation requests/bytes, **not peak live memory or RSS**. Borrowing
eliminated many small allocations, but the added async nesting more than tripled
requested bytes initially. The pointer-sized guard reduced that cost without
changing request count; it still left 8,006 allocations in the 4-16 KiB class.
A short three-pair diagnostic still showed a median paired pure-control ratio
of 1.252 (one candidate win), while selectors retained their benefit. This was
not accepted as the final result.

The remaining mechanism is physical future layout: a function containing both
control and ordinary graph interpretation reserves the ordinary branch's state
even for a singleton boolean instruction. The next repair uses a synchronous
thin step factory. It constructs an ordinary graph future only after selecting
an ordinary block; control blocks use a separate small future. Recording state
is likewise constructed only on the active branch, and an untraced control block
directly awaits its work. The pointer-sized guard remains useful globally. The
same allocator probe and timing protocol must validate this hot/cold split;
removing trace coverage or altering IO ordering is not an acceptable substitute.

### Accepted final image: physical hot/cold split

The accepted frozen image is
`aa4749e3759d1cb55f898077e19f32a6cb8d37e8867c0c271f37da9af9adedcf`
with rlib SHA-256
`b36020ff901b728f1ea9914f4704d5300ead3c49f6cb751afcb4fff043c4b9b2`.
The `7fc6...` and `015645...` images above are rejected/provisional diagnostics,
not the current reference. The final measurement window verified all image and
probe hashes at both ends, with no other builds or probes running.

Same-source, 2,000-iteration, seven-pair alternating AB/BA results:

| Interpreter case | Original median (ms) | Accepted median (ms) | Median paired ratio | Wins |
| --- | ---: | ---: | ---: | ---: |
| Pure control | 7.647145 | 3.502327 | 0.457991 | 7/7 |
| Fresh repeated selector | 332.031996 | 125.767173 | 0.373258 | 7/7 |

The improvements are approximately 54% and 63% for these specific interpreter
workloads. Raw pairs are retained in `runtime-control-v6.csv`; frontier summaries
are retained in `runtime-frontier-v6.csv`. The timing probe source SHA-256 is
`b0dbf9059df5b6f2f41e2fa393f5720914ea2934c6cd6d6f83507671077c0637`;
its original/accepted executable hashes are
`09c830bd5b49dbfff0f697093c401ab93390b08c784467ed79f3ae51bec91caf`
and `ad7e8453f644e732c64430e760b5b83d1d172c8bd683412eb53f02763b76d9af`.

The accepted pure-control allocation profile requests 12,496,082 bytes in 30,166
allocations, versus the original 35,004,586 bytes in 42,164 allocations. The
4-16 KiB request class falls from 8,006 in the thin-guard-only image to two fixed
requests: approximately four oversized allocations per loop iteration have
actually disappeared. This is not just fewer small AST clones. Full profiles
for all four images are retained in `runtime-allocation-v6.csv`; requested bytes
are cumulative allocation requests, not RSS or peak live memory.

The decisive representation change is a synchronous thin step factory:

- A control block constructs only its small control future.
- The ordinary graph/scheduler future is boxed only after selecting an ordinary
  block. Its state no longer inflates every boolean/control future.
- Block/instruction recording factories are invoked only with an active context;
  an untraced control block directly awaits its work.
- The generic tracing helper constructs recording async state only on its active
  branch. Typed wait failures remain `runtime.wait`, not duplicate task failures.

The accepted source passed direct untraced iteration/update checks, all 22 v0.6
CLI journeys, four activity journeys, three control-runtime journeys, three
physical-task trace tests, five legacy JSONL trace tests, eleven SQLite tests and
eighteen REPL-job tests in the shared incremental gate. Trace ancestry, source
identity, cancellation, final-link and ancestor-link behavior were preserved.

Final-image production IO measurements are documented separately in
[the existing Linux IO note](io-linux-v3.md). The measured 4 KiB and 64 KiB
aggregate median gains are approximately 12% and 13%, with explicit half-run
drift and CPU/p90 data retained. Large-file results are mixed; no universal
no-regression guarantee or cross-platform speedup is inferred. The actual
kernel mechanism remains selected for large files, and the three native IO
boundary/byte-flow tests passed.
