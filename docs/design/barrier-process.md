# Native process lifetime at barriers

External commands execute natively against the real filesystem. There is no
Sandboxie/bubblewrap provider and no transactional resource-view promise.
`Process` launches exact argv with Tokio; stdout and stderr drain immediately
into independent, bounded-buffer spools. Disk quota is optional and explicit.

The session retains processes across asynchronous blocks and joins them at an
await barrier or shutdown. Dropping a `Process` observer does not cancel its
independent child-wait worker. Tokio `kill_on_drop(false)`, non-destructive Unix
process-group handles and Windows jobs without `KILL_ON_JOB_CLOSE` avoid
accidentally killing already-started external work on a Rust owner drop.
Windows job attachment is best-effort for explicit force-stop, not a launch
prerequisite; it cannot guarantee capturing descendants spawned before attachment.
Windows commands launch with `CREATE_NEW_PROCESS_GROUP`, which disables inherited
console Ctrl-C delivery in that group; Unix children similarly have their own
process group. Cancellation is therefore delivered to the interpreter without
prematurely terminating admitted external work. This policy owns Windows creation
flags: Rust's standard `CommandExt` exposes no getter to merge arbitrary caller
flags. It does not use `CREATE_NEW_CONSOLE`, which would ignore the group flag.
See Microsoft's [process creation flags](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags).
This is not a power-loss or SIGKILL survival guarantee; normal CLI shutdown must
keep its runtime alive until child workers finish.

`force_stop_and_wait` is explicitly destructive. Ordinary flow EOF, interpreter
errors and cooperative cancellation must not call it. The legacy low-level
`cancel_and_wait` method remains an explicit destructive alias, not a default
cancellation policy. A leader's exit does not kill its descendants: inherited
output pipes drain until their writers close naturally. A detached descendant
that closes its pipes is outside the observable completion boundary.

Spool/quota errors retain the original native cause and drain remaining bytes to
a bounded discard buffer rather than killing the command or blocking its output
pipe indefinitely. This can legitimately wait forever for a nonterminating
command; force-stop is a separate decision. `exit_signal()` is signalled when
the leader wait resolves, before descendant output drain completion, so idle
stdin producers can stop. `wait()` and `is_finished()` include both drains.

Reference: Tokio's documented [process lifecycle](https://docs.rs/tokio/latest/tokio/process/)
and [Child wait semantics](https://docs.rs/tokio/latest/tokio/process/struct.Child.html)
distinguish handle dropping, explicit kill and awaited reaping. We retain a
worker to reap, rather than relying on Tokio's best-effort dropped-child reaper.

Focused acceptance journeys live in `tests/process_v4.rs`: exact argv, late
output, retained output after observer drop, quota error drainage, explicit
destructive tree stop, and natural descendant output closure. Source changes
have not yet passed the serialized integration build; record results separately.

## Bounded native test fixtures

The Windows full CI run for `1a8405e` exhausted its job timeout while the
deferred-exit scalar unit test remained active. This does **not** establish a
lost watch notification: the raw-status and completion observers inspect the
retained value before awaiting changes, and source review found no notification
race in that protocol. The old test did have an infinite PowerShell filesystem
gate, a single scalar timeout, and unbounded physical-drain and late-replay
waits. A panic before releasing that gate could unwind the temporary directory
and remove the very marker the child awaited.

Tokio's Windows subprocess implementation uses `Blocking<ArcFile>` for child
stdio. Thus a test timeout is not itself a bound on runtime destruction: started
blocking pipe reads can outlive aborted async wrappers while the child remains
alive. Libtest captures panic output until the test finishes, so an absent panic
line in the job log cannot identify which phase stalled. See the upstream
[Windows subprocess implementation](https://docs.rs/tokio/1.54.1/src/tokio/process/windows.rs.html)
and [blocking-task lifetime](https://docs.rs/tokio/latest/tokio/task/fn.spawn_blocking.html).

A standalone supervised Windows probe of the old exact PowerShell command,
with redirected stdin deliberately left open, exited naturally with status 7 in
all five trials (213–293 ms). No forced stop or stdin close was needed. That
rejects an unconditional "PowerShell waits for stdin" explanation; it does not
identify the failed CI phase. Probe data is in
`.temp/deferred-powershell-probe-results.json`.

The repaired test uses an exact ignored child test in the same Rust executable,
not a shell startup dependency. It acknowledges startup, waits for the actual
independent file marker and exits 7; a monotonic twenty-second self-deadline
provides a bounded failure exit 124 even if the parent unwinds or removes its
fixture. A release-marker guard also opens the gate during unwinding. Parent
timeouts separately identify startup, raw exit observation, full physical drain
and late scalar replay. The original pending-scalar/independent-marker assertion
is retained, with assertions delayed until after normal child draining where
possible. This is test-supervision repair, not permission to kill production
children on ordinary cancellation. New-image execution and the remote Windows
CI rerun are separate acceptance evidence, not implied by this source diagnosis.

The rebuilt Windows library image was inspected with `--list` to confirm the
new child fixture was actually compiled. The deferred-scalar test then passed
twenty exact sequential invocations (30–88 ms per complete test process). The
entire library also passed with the default parallel harness, matching the CI
execution mode: **139 passed, zero failed, seven ignored**, 0.80 seconds. This
included the new import-receipt/owned-scratch tests in the shared image. CLI
SHA-256 was
`5408D067A9547B50E5C7F93204F0E3CC22A74856461548A9BCD962EB56B640A8`,
with both CLI and library hashes unchanged throughout the gate. Logs:
`.temp/deferred-final-repeat-*.log`, `.temp/deferred-final-repeats.json`,
`.temp/import-receipt-final-full-lib-parallel.log` and
`.temp/import-receipt-final-lib-results.json`. This local execution resolves the
bounded-fixture regression gate; the remote Windows full-source job remains the
distinct release acceptance gate.
