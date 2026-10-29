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
