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
