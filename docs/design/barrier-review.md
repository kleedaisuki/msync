# v0.5 independent barrier/runtime review

This review concerns the in-progress breaking barrier implementation, not the
accepted v0.4 artifact. Findings below are source-traced; no Cargo or execution
gate was run by this reviewer. Line numbers may move during parallel integration.

## Necessary corrections

### P1: scalar-port waiting blocks independent ready work

`src/execution/live.rs::start_flow` awaits `Program::read_scalar` inline before
returning to the ready frontier. Consider an explicitly asynchronous child that
waits for `marker`, followed in one block by `P::exit_code -> status` and
`"go" -> marker`. The scalar and marker writes are independent in the graph, but
the interpreter waits for child exit before admitting the marker producer. This
deadlocks and violates the requested ability to schedule scalar reads without
blocking unrelated files.

Correction: model scalar availability as a retained asynchronous dependency or
task, and keep admitting other ready operations. A scalar read must not become a
whole-block join. Reported to runtime and independent validation owners; pending
implementation and a gated real-child regression.

Follow-up source inspection: `Program::scalar_reader` now owns an independent
completion subscription and `start_flow` no longer awaits scalar completion.
The reported implementation defect is addressed in source; the real-child
regression remains the authoritative acceptance gate.

### P1: admission is incorrectly treated as physical flow completion

`live.rs::interpret` completes a transfer node immediately after `start_flow`,
which merely spawns its I/O future. `ActiveFlow` retains only its output path,
and `join_conflicting_flows` checks only that path. For
`source -> P::stdin` followed by `"new" -> source`, the active flow has no file
destination and the overwrite does not wait. The flow can open its source only
after the overwrite, giving the child new bytes rather than the preceding
statement's input. The graph's completion hazard has been bypassed.

Correction: retain complete flow read/write effects, distinguish admission from
completion, and honor physical completion dependencies; alternatively acquire a
valid owned source before treating that read as discharged. Reported to runtime
and independent validation owners; pending regression and implementation.

Follow-up source inspection: `flow::Source::acquire` captures finite native input
before returning to the ready frontier and the flow retains that snapshot until
cleanup. This addresses the finite-local-file case. Physical FIFO admission and
identity require the separate integration correction below.

### P2: resource limits are not aggregated across execution lanes

`copy_capacity` and `flow_capacity` separately derive limits from the same total
memory capacity. Native children additionally retain the six-buffer capacity
documented by `ProcessConfig::payload_capacity`, without reserving that capacity
from either lane. The configured aggregate payload-buffer bound can therefore
be exceeded even when each individual lane obeys its own count.

Correction: one ownership-based payload budget shared by finite copies, physical
flows, and retained native output drains; retain reservations until their actual
owners settle. Reported to runtime owner; pending integration and bounded-lane
tests. Disk spools have an explicitly optional quota, which is a separate
resource and must not be advertised as memory-bounded total output.

Follow-up source inspection: the shared `execution::scheduler::Admission` now
reserves copies/programs/flows against one semaphore, including six program
buffer units. The original separate-budget source defect is addressed; capacity
and connected-flow behavioral tests remain necessary.

### P1: FIFO scheduling evidence and native generation pins are disconnected

The inspected runtime never constructs `FifoEndpointProof` or
`FifoRendezvousProof`. Its block context therefore lacks verified opposite-end
co-admission evidence and retained generation identities. Flow adapters call
`streams::transfer` with default pins, independently rediscovering each FIFO at
open time. An unlink/recreate between observations can attach opposite endpoints
to different FIFO generations. Moreover, treating a real completion dependency
as completion rather than admission can deadlock the producer/consumer pair.

Correction: observe only the current block, retain metadata-only native
generation leases, derive ordered writer/reader wave pairs by native identity,
reserve each connected cohort's full budget before starting it, and pass the
same endpoint pins to the actual native open verification. Do not remove the
unused pin/proof methods merely to silence warnings. The isolated nontransactional
algorithm is available in the previous `src/execution/physical.rs` implementation;
it can be adapted without restoring Manager or transaction histories.

Reported to runtime/root/validation owners. Existing
`tests/physical_execution_v4.rs` covers two- and three-flow co-admission,
hard-linked FIFO wave ordering, impossible cohort budgets, and a reader-first
native-program stdin chain; these are relevant v0.5 tests, not obsolete
transaction guarantees. Pending integration and actual Unix execution.

Follow-up source inspection: the runtime now owns `fifo::FifoState`, passes its
pins through `Flow::start_with_pins`, reserves ready connected groups, and keeps
stream nodes running until physical completion. Ordered wave and generation
tests remain the relevant acceptance evidence; the original disconnected-pin
finding is addressed in the inspected source.

### P2: partial submission errors bypass background job registration

`src/structured.rs::execute_unit` returns through
`outcome.map_err(...)?` before computing newly retained `WorkId`s and attaching
them to a `Job` with the compiled source. A submission can launch async program
A and its output flow, then fail the later async B launch because `--jobs 1` is
saturated. The runtime deliberately preserves A on this capability error, but
the CLI skips registration: `/jobs` can claim there are no jobs and later
background failures lose their originating `ExecutionUnit`.

Correction: reconcile and register all newly retained work, together with its
source, before returning either execution or presentation errors. Preserve the
original failure instead of declaring the submission successful. Reported to
CLI/root owners; pending source fix and a gated partial-submission REPL journey.

## Final ownership follow-up

The inspected `RuntimeSession::Drop` transfers finite workers, active flows,
native program records, leases, its invocation, and its recovery registration
into one `Retiring` owner. That owner joins work before cleaning the invocation
and retiring the registration. No concrete `programs`/`program_leases` divergence
was found in the inspected poll/await/force-stop paths.

Normal CLI quit explicitly awaits `shutdown`, which cancels physical flows but
preserves selected program-to-file output drains. The fallback Drop path instead
cancels the session token unconditionally and therefore also cancels those output
forwarders. Its spawned asynchronous retirement additionally requires the Tokio
runtime to remain alive. These are scope boundaries: do not advertise Drop as a
universal durable child supervisor, or as equivalent to fully awaited shutdown.
If full forwarded-output preservation is intended for Drop, use the same
selective flow cancellation as shutdown. Reported to runtime/root owners.

## Integration checks still needed

- The inspected interpreter builds a graph with empty fresh type facts and
  dispatches finite statements individually. The new `prepare_block` and guarded
  overwrite-fold APIs were not yet called by that path. Verify actual runtime
  specialization/optimization integration, not only stand-alone API tests.
- `planning_context.live_processes` is currently populated only by running
  children, although completed retained programs still expose valid spools and
  scalar ports. A subsequent REPL submission must accept these references;
  distinguish known program identities from currently running budget owners.
- The ordinary cancellation path must retain native ownership and output drains
  until natural exit. The process implementation uses `kill_on_drop(false)` and
  keeps drainers running after a spool failure; this is aligned source structure,
  not an end-to-end acceptance result.

## Agreed boundaries

The user confirmed that normal cancellation must not truncate an admitted
external program; machine power loss or killing msync is not a promise of atomic
external side effects. Multi-path/tree statement publication may use automatic
startup recovery, provided recovery yields the complete old or complete new
statement state before new execution. Neither boundary authorizes transactions,
retained undo histories, or eager filesystem binding of future blocks.

## Current lifecycle audit (2026-10-29)

Source review of the post-generation-5 changes confirms that submission entry
reaps the retained finite JoinSet before allocating a new submission/frontier.
`a_dropped_submit_cannot_complete_a_new_submission_with_reused_ids` gates the
actual old and new publication workers, queues the old receipt, and checks the
new destination remains absent until its own gate opens. This is a discriminating
regression for receipt-ID reuse, rather than a timing-only assertion. It still
requires execution against the newly built source.

Flow construction now runs under `lease.context()`, so the asynchronous task's
linked span inherits its own schedule rather than a surrounding execute span.
Physical nodes remain running after admission; captured finite input releases
only its read hazards. Explicit Spawn does not globally join old flows. The
existing two-gated-program test requires B to launch while A's stdout is still
open. Normal cancellation is separately tested by holding a child behind a
network gate and requiring its natural completion marker after cancellation has
begun. These tests cover the intended behavior, but were not executed in this
source-only review.

### Resolved P2: abandoned flow-source acquisition was not retained by the session

Original finding (before the retained-acquisition fix):

`execution/live.rs::start_flow` acquires a flow lease and then awaits
`flow::Source::acquire`. That method creates a `spawn_blocking` worker and awaits
its local JoinHandle. Unlike finite publication workers, this acquisition worker
is not inserted into any RuntimeSession-owned JoinSet before the await.

Dropping a submit future during a large regular-file-to-program/stream source
capture therefore detaches that worker and drops its admission lease. The worker
continues reading/copying bytes with no charged payload budget. A subsequent
shutdown or session Drop can clean the outer invocation, since neither `finite`
nor `flows` records this in-progress acquisition. The worker retains only the
scratch pathname, not the outer lifetime owner. The capture can race private-root
cleanup and leave unobserved cleanup errors; repeated abandoned acquisitions can
also bypass the intended resource bound. The new literal-publication gate test
does not cover this separate admission/acquisition path.

Correction: retain acquisition ownership and its budget in the session before
its first cancellable await, and reap or retire it before cleaning the outer
invocation. Treat acquired-but-not-dispatched sources as an explicit pending
owner; do not merely make the local JoinHandle abort-on-drop, since already
running blocking work cannot be aborted. Add a gated real regular-file source
capture test that drops submit while acquisition is running, invokes shutdown or
Drop, and verifies retirement waits, budget remains charged, and backing/registry
cleanup completes. Reported to root for integration.

The ownership analysis follows Tokio's documented contracts: dropping a
[JoinSet](https://docs.rs/tokio/latest/tokio/task/struct.JoinSet.html) aborts its
members, while dropping a spawned-task handle detaches it; running blocking
workers require retained join ownership. Native
[Child](https://docs.rs/tokio/latest/tokio/process/struct.Child.html#caveats)
continues by default when its handle is dropped. The existing Retiring owner is
therefore necessary but must include the acquisition stage as well.

Follow-up review of the integrated fix: `RuntimeSession::acquire_source` now
registers an `Acquisition` before its first await. Its blocking worker owns both
its flow lease and a clone of the outer invocation until worker completion. A
successful capture and its lease remain in the retained JoinHandle result until
the session consumes them. The normal continuation transfers that ownership to
`ActiveFlow` without another intervening await. Abandoned acquisition is instead
joined and discarded before admitting a new submission, during settlement, or
by `Retiring` before outer backing/registry retirement. The outer invocation's
strong-owner cleanup guard supplies a second defense against premature removal.
No remaining substantive defect was found in these inspected lifecycle paths.

Actual Windows generation-6 logs were inspected for this follow-up:
`.temp/barrier-runtime-gen6-cleanup.log` records 5/5 passing tests, including all
three new capture-abandonment paths and the finite-receipt-ID regression;
`.temp/barrier-runtime-gen6-flow.log` records 7/7 passing flow tests, and
`.temp/barrier-runtime-gen6-program.log` records 13/13 passing program tests
(with one subprocess fixture deliberately ignored). The capture tests gate a
worker after real capture, require its admission budget and backing to remain
owned, and verify shutdown/Drop/new-submit retirement never connects the
abandoned destination. This closes the reported P2 on the inspected Windows
source and execution evidence. Cross-platform acceptance remains a separate
Linux gate; this review does not infer it from Windows results.

## Directional-marker normalization: mathematical review

The user clarified that generic `Before` (`async`) and `After` (`await`)
markers are directional, not symmetric walls. The following model concerns
ordinary transfers between opaque native `Spawn` / actual `Await` anchors.
Those anchors are not movable and are not generic markers. Transfer order is
preserved by this pass; any subsequent parallel execution or reordering still
requires the resource/dependency graph's ordinary hazard checks.

### Exact legal-slot model

Number the generic markers in source order `0 .. k-1`; slot `s` lies after
exactly `s` markers. Marker order is retained. For transfer `i`, define:

```text
L_i = 1 + index of its nearest preceding After, or 0 if none
U_i = index of its nearest following Before, or k if none
```

Its legal slots are precisely the integer interval `[L_i, U_i]`. A preceding
After forbids hoisting the transfer across it; a following Before forbids
sinking across it. A preceding Before supplies no lower bound, and a following
After supplies no upper bound. Both endpoint sequences are nondecreasing in
transfer source order, and each interval contains the transfer's original slot.
Consequently, a nondecreasing slot assignment inside these intervals gives a
stable merge of transfers and markers satisfying every original directional
edge. Conversely, any such stable legal ordering must obey these bounds.

Compute nearest following Before indices by a reverse scan, preceding After
indices by a forward scan. No filesystem observation, alias premise, or native
program speculation is used in this calculation. At an opaque anchor, terminate
the segment; in particular, retain the terminal Before needed before a Spawn.

### Minimum occupied transfer groups

Within this model, choose the first uncovered interval's upper endpoint `p`.
Assign consecutive intervals to `p` while their lower endpoint is at most `p`;
the monotone upper endpoints ensure that they also contain `p`. Repeat at the
first remaining interval. Chosen points increase, so transfer order is retained.

The exchange argument is short: a first group can use some point `q <= U_0`.
Moving that point right to `U_0` preserves every covered interval, because each
of their lower endpoints is at most `q` and each upper endpoint is at least
`U_0`. Thus an optimum exists using the greedy first point; apply the same
argument to the suffix. This minimizes the number of nonempty consecutive
transfer groups in linear time. It does **not** claim global optimality over
arbitrary I/O permutations, graph contractions, resource limits, or scheduling
around opaque native-process anchors.

An independent exact oracle for a small transfer interval list is:

```text
best[0] = 0
best[e] = min(best[s] + 1)
          over s < e with max(L_s .. L_(e-1)) <= min(U_s .. U_(e-1))
```

The oracle enumerates consecutive groups by interval intersection rather than
implementing the greedy rule. An executed JavaScript enumeration compared the
two on every word over `{Transfer, Before, After}` of lengths zero through
eleven: **265,720 cases, no mismatch**. This validates the small abstract model,
not the Rust implementation or runtime integration; those need their own tests.

### Why an arbitrary local fixed point is weaker

The initially proposed two scans, sliding Before right and then After left,
preserve legality but can choose a locally irreversible, suboptimal placement.
Here `T` means a transfer, `B` Before, and `A` After:

| Original word | Two-scan result | Better legal grouping |
| --- | --- | --- |
| `B T1 A T2` | `T1 B A T2`: two groups | `B A T1 T2`: one group |
| `T0 B A B T1 A T2` | `T0 B A T1 B A T2`: three groups | Place `T1` with `T2`: two groups |

The first counterexample has intervals `[0,2]`, `[2,2]`. The second has
`[0,0]`, `[2,4]`, `[4,4]` and survives stripping all leading and trailing generic
markers. Hoisting a transfer across Before first can manufacture a hard
Before-to-After placement that was not forced by the original marker edges.
Merely reaching a local fixed point therefore does not establish the intended
block-elimination result. These examples and the independent oracle were sent
to the implementation leader and test owner before accepting minimum claims.

The production distinction between effect ordering and permission to speculate
also matches [MLIR's side-effect/speculation rationale](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/).
Its interfaces are useful engineering context, not a theorem imported into this
DSL: native effects, nontermination, and contextual file observations still
need their separate runtime contracts. LLVM's acquire/release terminology is
only an analogy; no LLVM memory-model synchronization guarantee is asserted here.

Follow-up source review: the implementation in `hir::rewrite` now uses the
original-slot interval algorithm rather than the two-scan candidate. Its reverse
upper-bound table and forward preceding-After bound match the definitions above.
After placement, an interior marker-only run keeps just its first Before and
first following After, if that pair exists. Such a pair carries the remaining
hard cut; other markers in a run with no intervening transfer cannot distinguish
another transfer group. Prefix and suffix runs disappear except for the Before
retained at a Spawn anchor. This also removes the empty control-only round in
the interior counterexample. The canonical generic segment consists only of
transfer groups separated by Before/After pairs, making a second normalization
unchanged. The independent dynamic-programming test's indexing was reviewed and
matches the oracle above. No further substantive issue was found in these source
paths; execution of the actual Rust tests remains the root integration gate.

## Final completion audit (2026-10-29, source `97c8e8b`)

This supersedes the earlier incomplete checkpoint, not its historical failure
records above. The production revision is
`97c8e8b32d15fcc55b5b245cf2676c0cf6635df2`. This auditor inspected the current
sources, final CI job logs and terminal JSON, actual source/image manifests,
performance results, installed smoke assertions and payloads, and directly read
the installed executable's version/hash. No production changes, Cargo builds or
new functional tests were performed by this auditor.

| Requirement | Authoritative evidence inspected | Final assessment |
| --- | --- | --- |
| Remove transaction language and execution machinery | Active HIR consists of Transfer, Spawn, Await and directional Barrier operations. `src/transaction` and native view providers are removed; obsolete provider-name searches find negative tests and historical explanations, not executable backends. Final full CI includes language/version rejection tests. | Satisfied; no active transaction grammar, MVCC engine or native transactional provider was found. |
| Finite statement old/new recovery without undoing earlier completed statements | `filesystem::durable::Batch`, identity-checked Stage/Coordinator registration and startup recovery; full CI crash/stage/registry tests; actual capability-free readonly directory and remote partial-GC crash journeys in `storage-crash-validation.md`. Crash fixtures now use a real await join to establish prior completion. | Satisfied for the accepted same-scratch process-crash recovery contract, not instantaneous multi-path visibility or universal hard-power-loss durability. |
| Normal cancellation must not truncate admitted external programs | Runtime cancellation settles retained owners and naturally awaits programs. Native Process uses `kill_on_drop(false)`, independent stdout/stderr drainers and process groups; destructive force-stop is separate. Final CI contains actual ConPTY cancellation, retained large output, descendant-held pipes, spool failures and abandoned acquisition tests. | Satisfied within the agreed normal-cancellation boundary; SIGKILL, executor destruction, explicit `/kill` and power loss remain excluded. |
| One-way async/await motion and elimination of unnecessary blocks | Original legal-slot interval normalization, canonical control cuts, preserved IO order/IDs and native observation anchors. Final CI passes directional examples, independent-DP minimum-block properties, idempotence/conservation and hard-cut cases. The separately executed abstract oracle covered 265,720 words. | Satisfied: minimum groups for fixed IO order, without claiming a global optimum over arbitrary IO permutations or authorizing speculative effects. |
| Bare calls and macro-local async joins | Compiler pending-process ownership emits default/implicit joins without joining caller-owned processes or duplicating an explicit await. Real-child and source/object cases preserve the user's three-block launch/output/join example. | Satisfied. |
| Pure compilation and exportable typed objects | Source compiler and pure object contextualizer share normalization, retain origins, and leave future paths as obligations. Format-2 objects execute typed IR rather than debug source. Final codec/integrity/context/phase suites pass; installed object execution succeeds after the original source is moved away. Static control traces contain no runtime/helper/publication effects. | Satisfied; no source-reparse fallback, compile-time endpoint probe or native-provider startup requirement remains. |
| Current-block observation, guarded optimization and bounded scheduling | `live::interpret` admits one block at a time; graph completion/admission gates, exact FIFO generation proofs, copy/program/flow slots and aggregate payload-buffer leases remain owned. Final full CI passes future-file creation/deletion, independent scalars, flow EOF, stale-generation, resource and dropped-future regressions. Local-only preparation keeps remote endpoints Unknown until acquisition; both platforms pass exact all-local facts/context/graph/program equality against live preparation. | Satisfied; buffer limits are not misrepresented as whole-process RSS, and remote Unknown is not an independence proof. |
| Files/directories/streams, container intent and selected payloads | Exact existing-directory refusal plus `/` container and `/.` replacement; native physical suites; local/WSL/SSH sparse acquisition before payload reading. Retained real SSH suites cover upload/download, recursion, overwrite conflicts and rollback; current release `677de9...` was actually observed in metadata, acquired-name and FIFO/socket/cancellation SSH processes. Native-ext4 WSL publication and projected-Windows alias cases pass. | Satisfied; existing SSH configurations were checked unchanged and remote streams are not treated as finite rollback-safe files. |
| Useful diagnostics, formatting and persistent REPL | Final CI includes logical native-port job grouping and per-work origins. Installed assertions verify ANSI help, in-place formatting/check, persistent bindings, `/help`, `/?`, `/limits`, native async/await output, and an intentional existing-directory error with execution phase, exact token underline and constructive container guidance. | Satisfied. The smoke's exit 1 is the asserted negative diagnostic case, not an unexamined test failure. |
| Mature dependencies and primary engineering practice | Cargo uses Tokio/tokio-util, serde/serde_json, regex, rustyline, anstream and native bindings. The design records process lifecycle/cancellation constraints, side-effect versus speculation distinctions and ordered recovery durability. | Satisfied without introducing an unjustified execution framework or ring engine. |
| Complete Windows/Linux source acceptance | [Full run 40888509441](https://github.com/kleedaisuki/msync/actions/runs/40888509441) targets the exact `97c8e8b` revision. Actual changes output is `full:true`. Terminal JSON records all three jobs successful. Independently aggregating actual result lines yields Windows **803 passed, 0 failed, 56 ignored** and Linux **825 passed, 0 failed, 20 ignored**, 77 result groups each; strict all-target Clippy, formatting and doc commands also complete successfully. Zero-case doctests add no behavioral coverage. | Satisfied; this is a completed full matrix, not a focused run or a live watcher. Environment-specific SSH/WSL journeys are established separately, not inferred from ignored CI entries. |
| Performance optimization after correctness | Corrected causal images `755a5d6f...` and `677de910...` share correctness fixes; independently comparing their actual manifests finds only `src/preparation.rs` and its tests differ. Five alternating pairs per existing/missing state preserve exact data and show mean mixed-run reductions **14.0% / 14.1%**, with a fixed **six to four** WSL helper reduction. The eleven-pair local extension independently recomputes **+0.84% mean**, with the documented **+1.62% p50** trade-off; no universal/no-regression claim is made. | Satisfied: an actual production optimization and causal measurement round, not merely baseline research. The small measured local-command trade-off was investigated and explicitly accepted for the dominant mixed workload. See `performance-v5.md` and retained `target/performance-v5/causal-*` artifacts. |
| Final usable installed release | Direct probe reports `D:\SystemScripts\msync.exe` **0.5.0**, SHA-256 `677DE91009C7322A753BA02DF5A131C7C0703C8BCCA8A5F518F79606690967DB`, matching the final frozen image and provenance for `97c8e8b`. Atomic installation retained the old 0.4.0 backup, whose original hash was independently checked. Installed native-ext4-to-NTFS macro control verifies **28 files / 126,083 bytes**, four helpers and pure static commands. Installed UI/format/source/object/native/REPL/diagnostic smoke assertions all pass. | Satisfied against the installed path, not merely a build-tree binary. |

Final evidence locations include `.temp/barrier-ci-97c8e8b-full-{final.json,
changes.log,windows.log,ubuntu.log}`, `.temp/perf-release-ssh-{results,
process-images}.json`, `.temp/final-install-result.json`,
`.temp/installed-smoke-b5c28f9e2ddb44bf929888337568c6b3/result.json`, and managed
`target/performance-v5/installed-functional-control*` and causal comparison
artifacts. Disposable raw logs are summarized here and in the maintained design
notes so their eventual cleanup does not lose the conclusion or its scope.

**The technical completion audit passes for the original objective and the
subsequent directional clarification.** No required implementation, correctness,
measured-optimization or installed-artifact item remains unverified in this
matrix. The coordinator owns final documentation/commit delivery and goal status;
this review does not expand the accepted recovery/process guarantees beyond
normal cancellation and same-scratch process-crash recovery.
