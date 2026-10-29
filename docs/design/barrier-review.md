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
