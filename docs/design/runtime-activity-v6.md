# Runtime activity notification contract

Integration contract for the runtime and REPL latency workstreams:

- `RuntimeSession::wait_ready(&self)` is an async, cancellation-safe,
  non-consuming readiness subscription.
- It resolves when any flow, finite publication, retained source acquisition, or fully drained
  program completion is ready for `poll_ready()`. It does not wait for all work
  and does not close program stdin, acquire a barrier, consume a result, or
  settle unrelated work.
- With no active work it remains pending, so an input/event select cannot spin.
- A program-completion signal is distinct from leader exit: output drains must
  finish before it reports completion. Existing scalar exit status keeps its
  earlier-observation contract.
- Completion tokens/wakers reuse existing workers. No polling interval,
  observer thread, or extra observer task per operation is introduced.

The REPL can select input reception against `runtime.wait_ready()`, then call
its existing poll/reconcile/publication logic. Dropping a losing readiness
future cannot cancel the underlying program or byte flow.

## Source and connectivity of fresh runtime identities

`RuntimeSession::work_instructions() -> BTreeMap<WorkId, InstructionId>` maps each
currently active work item to its semantic source node, including a loop-local
program whose runtime `ProcessId` differs from its template ID.
`work_processes() -> BTreeMap<WorkId, Vec<ProcessId>>` gives the actual runtime
process identities attached to retained byte flows. These maps preserve the
existing stable source nodes while avoiding guesses based on spans or template
process IDs when forming REPL jobs.

Finite worker completion is indexed by Tokio task ID. A submission future that
is dropped while publication is running does not make all pending work appear
ready: the completion token wakes the idle observer only once the owned worker
has finished. `poll_ready()` retires signaled results and errors without replaying
an abandoned unsupported/fallback operation or joining unrelated pending workers.

Nested control-body invocations advance the existing runtime execution epoch.
`WorkId::Flow { submission, instruction }` therefore remains distinct for two
simultaneously live iterations of the same static transfer site, without changing
its public enum shape. The epoch is stable throughout each ordinary block, so
rendezvous/admission matching retains its original ownership boundary.

## Editor-reader completion and notice backpressure

The v0.6.5 local compatibility pass exposed a real REPL lifecycle race in
`barrier_terminal_v5::terminal_ctrl_c_drains_a_running_child_until_it_exits_itself`.
The unmodified real-PTY journey can reach a 30-second `/wait` timeout even though
the naturally exited child has already produced the complete 2 MiB capture and
the replayed `exit-code` file contains `0`. At that point the input reader has
returned and the REPL thread is blocked, not waiting for the child or file I/O.
Tracing perturbs the schedule and can hide the failure.

Rustyline 19.0.0 uses a blocking `sync_channel(1)` for external-printer messages
on both Unix and Windows. The printer checks raw mode before sending, but the
reader can leave raw mode between that check and the send. A completion wake may
then publish both a job-status notice and a progress notice after the input
reader has returned; the bounded queue has no consumer to release its sender.
The upstream [Unix implementation](https://github.com/kkawakam/rustyline/blob/44c7025089debc46528fcc17b79d0e30cec0d60c/src/tty/unix.rs)
and [Windows implementation](https://github.com/kkawakam/rustyline/blob/44c7025089debc46528fcc17b79d0e30cec0d60c/src/tty/windows.rs)
make this ownership boundary explicit. A timeout increase or a test sleep does
not repair that cycle. Batching notices and prioritizing completed input can
reduce its frequency, but cannot make a blocking send safe after reader exit.

The same local pass found a separate fixture race in
`latency_v6::a_background_flow_failure_does_not_wait_for_an_unrelated_held_child`:
a TCP connection acknowledges only source admission, not destination staging.
The test now waits for real bytes in the private file under its initially empty
destination parent before replacing that parent, and still asserts the public
output is absent before EOF. This preserves the intended publication-failure
journey instead of racing the existing parent-directory admission operation.

The maintained [Rustyline source correction](../../vendor/rustyline/README.msync.md)
removes the cyclic single-slot backpressure without replacing the editor. Its
standard message channel is unbounded; Unix wake descriptors are nonblocking and
drain their accumulated wake bytes, while Windows resets before a nonblocking
queue drain. Both preserve ordered messages, per-message implicit newlines and
the existing native rendering mechanism. The application coalesces each notice
burst and gives already-ready input priority. No notification thread is added.

Focused local acceptance on the corrected source passed two deterministic Unix
transport tests (departed reader, saturated pipe and stale wake) plus the actual
PTY cancellation/drain case and all six latency journeys. The background failure
case still reports before an unrelated held child exits. The equivalent Windows
queue/event test is present but is not reported as natively executed here.
