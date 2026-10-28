# Independent v0.4 foundation review

Reviewed the structured compiler, effect graph, logical version store, disk
publication adapter and process ownership foundation at `5551841`, with focused
follow-up against the filesystem owner's uncommitted correction. This is a
component review, not acceptance of the unimplemented unified executor or OS
resource-view providers. Those remaining obligations are explicitly tracked in
[architecture-v4.md](architecture-v4.md).

## Resolved P1: journal cleanup changed external hard-link metadata

The original `transaction::filesystem::make_removable` recursively applied owner
permissions on Unix and cleared the read-only attribute on Windows to every file
in a retained publication journal. However, an old destination leaf can have a
hard-link alias outside the transaction's managed root. Renaming that leaf into
the journal does not give the manager exclusive ownership of the underlying
inode's permissions.

A real Windows reproduction creates a managed directory containing `old`, links
it to an external sibling, marks the shared file read-only, replaces the managed
directory, then finalizes its journal. At `5551841`, finalization succeeds but
the external alias silently becomes writable (`true -> false` for read-only),
violating the write footprint and weakening an unrelated file's protection.
Ordinary Unix files also acquired execute bits because cleanup added `0700`.

The correction separates directory traversal permissions from ordinary-file
deletion. Unix ordinary files are unlinked without chmod. Windows uses native
file disposition with ignore-readonly semantics; an unsupported capability
returns a visible native failure and retains the journal instead of changing
shared attributes. Directory-specific permission handling remains separate.

Independent regression artifact:

```text
tests/review_foundation_v4.rs
  finalizing_a_replaced_tree_must_not_change_permissions_through_external_hardlinks
  finalizing_an_old_hardlink_does_not_add_execute_bits_to_its_external_alias [Unix]
```

The first test failed on the Windows host before the correction and passed after
it. The Unix mode-bit regression is retained for the remote Unix matrix; it has
not been counted as a local Windows pass. Fixture data and disposable outputs
remain under the project's `.temp`.

Follow-up: the two independent Unix counterexamples and the Windows counterexample
passed at `e7fd5bd` in the targeted two-platform
[Actions run](https://github.com/kleedaisuki/msync/actions/runs/40481406869).

## Other assessed boundaries

The version store validates an entire pending segment before modifying published
epochs and keeps origin retention separate from checkpoint retention. The process
layer distinguishes a native launch from transactional mediation, immediately
drains both output pipes, and documents the limits of Unix process-group ownership.
The graph retains unknown-effect obligations rather than assuming argv proves
purity. No additional concrete P1/P2 counterexample was established in this bounded
review; that statement is not a proof of correctness or completed OS isolation.

## Default-v0.4 integration review (working tree after `8fa0d5c`)

The follow-up assessed actual default CLI journeys, the prepared rewrite path,
and persistent manager ownership. Shared multi-program cohorts were still being
implemented in parallel and were not accepted or rejected as completed behavior.
Only targeted reproduction targets were built (`CARGO_INCREMENTAL=0`, two jobs).

### Resolved P1: authorized shell edits were invisible to retained manager heads

`repl::repl_command_inner` dispatches `/pwsh` and `/bash` directly to the shell,
while `structured::State` retains its `RuntimeSession` and immutable enrolled
versions. A subsequent preparation inspects the new host metadata, but actual
execution reads the old manager snapshot. No external publication is installed
in the version store at that boundary.

Actual Windows CLI counterexamples in `tests/review_foundation_v4.rs`:

1. `source -> first`, then `/pwsh Set-Content ... source ... 'after shell'`,
   then `source -> second`: the host source contains the new bytes, but `second`
   receives the pre-shell bytes. The CLI exits successfully without diagnostics.
2. A named transaction changes `source`, the shell writes a newer value, then
   `rollback A`: rollback reports success and silently erases the newer shell
   value, restoring the original bytes.

The second is destructive, not merely stale presentation. A fix must observe
authorized external changes as foreign heads while preserving named origins;
recreating the manager would instead discard undo history. Apply the same
boundary to formatting and legacy-execution transitions that can alter enrolled
resources. `Manager::refresh_host` now installs changed host bytes/metadata as
foreign versions before quiescent runtime submissions, retaining named history.
Both independent Windows CLI tests pass after the correction (`b26bc09` and
runtime integration `06b4b71`); a named rollback diagnoses the foreign head rather
than overwriting it. The original failing journeys remain regression controls.

### Resolved P1/P2: dead-write elimination lost destination permission provenance

`preparation::optimize::eliminate` considers an earlier exact-file write dead
when a later write replaces the same pathname. However, a literal assignment
intentionally preserves the existing destination's permissions. Those
permissions may have been produced by the supposedly dead file assignment.

An actual Windows default CLI run with a read-only `source` and writable `target`:

```text
source -> target
"literal" -> target
```

The prepared program drops the first assignment, executes only one transfer,
and leaves `target` writable. Unoptimized execution copies the source's read-only
metadata first, so the next literal assignment must not bypass it. The Unix
counterpart starts source mode `0750` and target mode `0600`: a subsequent literal
must retain `0750`, not the original `0600` left by eliminating the metadata
producer. The Windows counterexample failed against the current CLI; the Unix
case remains a cross-platform regression pending its native run.

The existing literal-forwarding restriction correctly recognizes creation-mtime
metadata, but does not protect this dead-store case. Either retain the producer
when a later operation consumes its metadata, or represent payload and permission
versions separately and include that dependency in the rewrite certificate.
The independent regression is
`dead_write_elimination_preserves_metadata_needed_by_a_following_literal`.

The correction in `fe6aa8e` treats only a later file-valued assignment as a
complete metadata kill. A literal is a destination-permission consumer, so it
cannot erase this producer. The independent Windows regression and a separate
actual reference/optimized comparison of bytes, permissions, mtime provenance
and result now pass; the Unix mode branch remains for the cross-platform gate.

## Resolved metadata regression: named rollback lost directory modification times

A separate focused check after the initial review establishes that a named full
rollback restores bytes and membership but not the original directory mtime.
This is an observable resource value, not a request to preserve physical inode
identity: incremental tools and an ordinary child program's `stat` can distinguish
the restored tree from its pre-transaction state.

`tests/review_metadata_v4.rs` creates a root containing one ordinary file and an
empty directory, assigns fixed modification times in 2001, records the timestamps
actually returned by the OS, then executes this real manager journey:

```text
enroll target -> begin serializable -> replace tree -> checkpoint -> finish
-> named rollback_origin -> stat restored root/file/empty directory -> shutdown
```

On the Windows development filesystem, the file-only control passes, and the
tree case fails deterministically: the ordinary file retains its observed 2001
mtime, while the root and empty directory have new 2026 mtimes. Byte contents,
empty-directory presence, deletion of the replacement entry and explicit manager
shutdown all succeed before the timestamp assertion. Setup uses typed
`FILE_WRITE_ATTRIBUTES` / `FILE_FLAG_BACKUP_SEMANTICS` handles on the owned
fixture only; no system paths, ACLs or global policies are changed.

Command: `cargo test --locked --test review_metadata_v4 -- --nocapture` produced
**one pass, one failure**. Strict review-target Clippy passes. The reproduction
compares OS-observed before/after timestamps rather than assuming filesystem
timestamp precision. All disposable data lives below project `.temp`.

The former implementation recorded only permissions. The correction adds common
`NodeMetadata { permissions, modified }` to directory/file version values, captures
the OS timestamp before copying bytes, and reapplies it to privately materialized
files and (after their children) directories. It uses stable Rust
[FileTimes](https://doc.rust-lang.org/std/fs/struct.FileTimes.html), not an assumption
about `fs::copy` on a particular platform. Windows opens directory/file attribute
handles with the documented [FILE_WRITE_ATTRIBUTES right](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfiletime)
and [directory handle flag](https://learn.microsoft.com/en-us/windows/win32/fileio/obtaining-a-handle-to-a-directory).
This portable contract includes modification time and permissions; it does not
claim preservation of atime, physical inode identity, ownership or arbitrary ACLs.

Parent namespace metadata is a real versioned write when a child is added,
deleted or changes kind. Its mtime advances on a normal commit, and named undo
restores the observed origin. Merely overwriting existing file contents does not
invent a logical parent membership change. Directory metadata write conflicts
therefore remain visible to MVCC; the manager does not silently rebase them as
independent child writes. A dirty-read shape fixture now stages the file-parent
replacement after child creation because child creation legitimately contributes
a complete parent-directory metadata version; its no-child-beneath-file
assertions are unchanged.

The Sandboxie logical-tree metadata format introduced in version 2 carries signed epoch
direction, seconds and nanoseconds with bounded decoding. Its in-memory transport
test checks original file, root and empty-directory mtimes without requiring a
driver. Pre-1970 and malformed timestamps have codec checks. Linux view snapshots
use the same values, and both native-view fixtures assert frozen file mtime after
the live host file changes.

After correction, the Windows metadata target has **four passes**, including
child insertion/deletion/kind changes; manager **15**, filesystem **13**, reference
executor **19** and Windows view protocol **seven** focused cases pass. Strict
changed-target Clippy and formatting checks pass. No actual Sandboxie driver
journey was counted locally.

An additional native Linux check ran as WSL UID 1000 with project `.temp`
privately mounted tmpfs using `unshare -Urnm`; source and the existing Rust build
cache stayed outside that mount. The first three metadata cases passed there,
including ordinary-file mtime, and the actual frozen-root Linux program-view
journey passed with its new timestamp assertion. The fourth portable metadata
case was added afterward and remains for the remote cross-platform gate. The
Windows control was not used to infer Linux behavior. Disposable launcher script:
`.temp/check-metadata-native-v4.sh`; no persistent host mounts/policy changes.

## Independent integration acceptance map (2026-10-28)

Scope: current working tree after `b91d7fb`, including the unfinished all-phase
runtime changes. This review traced actual compiler/runtime/store call paths and
read the named regression bodies; it did not rebuild the project or repeat CI.
A test's existence below identifies the right acceptance gate, not a claim that
its current working-tree version has passed. Keep native-provider execution
results distinct from in-memory graph/codec tests.

| Required behavior | Concrete implementation and regression gate | Current assessment |
| --- | --- | --- |
| Typed, hygienic macro parameters (path, namespace, stream/program), declaration-time `global`, no nested macros | `compiler/structured.rs::expand_macro` resolves argument `Value`s in the caller and constructs a new parameter scope with captured declaration globals; `lowering_v4::{user_macro_expands_typed_remote_sources_and_contextual_destination_items,macro_global_uses_definition_snapshot_and_locals_do_not_leak,namespace_arguments_and_stream_arguments_retain_types,manually_constructed_nested_macro_ast_is_rejected_even_if_not_called}` | Implemented. Values include `Process` and `Port`, not textual interpolation. A dedicated `macro_native_v4::whole_program_macro_arguments_execute_and_undo_in_each_isolation_mode` fixture now checks a whole-program argument, formal stdin/stdout, declaration-time global capture, exact UTF-8 bytes and origin undo. It was inspected here; actual execution is a pending native gate. |
| Python-like indentation and precise structured formatting | `language_v4::{canonical_format_is_idempotent_and_preserves_nested_regions,diagnoses_ambiguous_indentation_and_nested_macros}` and `cli_structured_v4::structured_format_edits_the_script_and_stdout_preview_remains_machine_text` | Implemented frontend and actual CLI journey; in-place format is separate from stdout preview. |
| Transactions share lexical environment; cross-transaction local undo forbidden | `compiler/structured.rs` passes the same mutable `Scope` into a transaction body; `lowering_v4::{transaction_checkpoints_named_origin_and_shared_bindings_are_distinct,a_transaction_does_not_create_a_macro_lexical_scope,other_transaction_rollback_is_forbidden_inside_a_transaction}` | Implemented; runtime also checks owner identity before local rewind. |
| Original `copy_codex` replaces destination tree, rather than nesting the source | `execution_v4::typed_macro_copies_a_real_tree_into_explicit_replacement_without_nesting`; Windows ignored `real_wsl_macro_source_replaces_a_native_existing_directory`; default CLI macro/check/plan/run journey | Real local-tree regression and real WSL-source fixture exist. Native WSL fixture is not an ext4 performance measurement: its source fixture is translated from a Windows path. |
| Original A1/checkpoint/scalar/local-rewind/origin-undo then B2/pid and literal lifecycle | Native ignored `program_execution_v4::the_requested_checkpoint_and_program_port_lifecycle_runs_end_to_end` | Covers the requested lifecycle with an owned Rust child fixture instead of depending on an installed Python script. It asserts that output, stderr, direct child file, exit files and pending file disappear after origin undo; B2 pid remains and exact literal bytes are published. Its current event-ledger integration needs the new native gate, not only the earlier baseline pass. |
| Four isolation levels and default serializable | `Store::read`, `Transaction::read_epoch`, `Store::validate`; `transaction_store_v4` dirty-read, RC statement-refresh, RR snapshot, write-skew, absent-read and phantom tests; real disk `transaction_manager_v4::visibility_reads_are_immutable_even_while_other_transactions_stage_and_publish` | Actual Store semantics, not merely accepted spelling. Native launch uses `Manager::read_snapshot` and the declared transaction isolation; frozen native-view tests demonstrate immutable baseline reads. The new `macro_native_v4` fixture loops a complete mediated program/checkpoint/origin lifecycle through all four modes; actual execution is pending. It does not distinguish concurrent native visibility. Conservative native-view conflict ordering is allowed; do not claim a native four-mode visibility experiment from the Store tests alone. |
| `commit` publishes without discarding named origin; local rollback resets only current segment | Store/Manager checkpoint, rollback-pending and origin APIs; `transaction_manager_v4::actual_file_checkpoints_pending_rewind_and_named_origin_are_distinct`; native A1/B2 fixture above | Existing disk/version semantics are implemented. Cancelling an already-admitted long-running native/stdio epoch rather than waiting for EOF is new runtime work and needs its own current native regression. |
| Full origin undo does not erase unrelated or foreign history | `transaction_store_v4::{origin_rollback_never_erases_an_intervening_foreign_publication,origin_rollback_protects_a_foreign_version_from_before_the_first_own_checkpoint}`; Manager disjoint-tree, sparse-namespace, foreign-refresh tests; actual Windows REPL `review_foundation_v4::named_rollback_does_not_erase_a_later_authorized_shell_edit` | Implemented strict conflict behavior. Retained named history is not reset to hide foreign changes. |
| Direct argv, UTF-8/NUL stdin, unique consuming streams, replayable scalars | Lowering typed argv/port tests; actual `process_v4::exact_argv_boundaries_are_not_shell_parsed_and_spools_are_removed`; native `large_stderr_and_stdin_co_start_and_nonzero_exit_remains_replayable_data` | Implemented. Native argv rejects NUL; binary stdin does not. Nonzero status is data, not an automatic failed transaction. |
| External-program filesystem IO is genuinely mediated | Actual Linux/Windows provider tests assert absolute, relative and descendant private writes while host data stays unchanged; runtime imports provider deltas into Manager | Implemented providers, not a rewritten private cwd. Managed roots and provider capability rejection define the boundary; no unisolated fallback. |
| Plan -> Execute, checkable optimization/effect proofs, all-phase admission | Preparation certificate replay, graph effects/cohorts/namespace receipts; new `execution/events.rs` retains submission, owner and view identities | Preparation/proofs implemented. Working-tree runtime now actually consults Start, staged-output Publish and RegionCommit gates, joins/cancels discarded epochs and restores the checkpoint before reporting EpochReset. Source review found no further concrete P1; current native/CI regression gates still determine acceptance, not pure graph tests alone. |
| Configurable bounded asynchronous execution | Admission owns process/stream/buffer permits; native concurrent stdin/stdout/stderr and independent-flow EOF tests | Limits and real async tasks implemented. Over-budget inseparable pipe cohorts deliberately fail before an unsafe serial wait; independent-flow progress must survive a saturated notification channel. |
| Persistent REPL lifetime and diagnostics | Actual CLI/REPL tests retain macros, programs, named origins and source provenance across inputs; runtime owns handles until wait/stop/shutdown | Baseline implemented. Administrative stop now records per-owner typed cancellation and propagates join/cleanup failure; the native `stopping_a_native_owner_cancels_old_dependencies_without_poisoning_the_session` fixture exercises an old scalar alias and fresh subsequent work. |
| Post-integration algorithm/data-structure, contention and millisecond optimization | `performance-v4.md` specifies finite-copy, planner scaling, flow completion/publication and long-session retention workloads | Not started; still a required delivery phase, not replaced by correctness-suite runtimes. |

### Resolved event integration review actions

The follow-up inspected actual current source after the integration owner froze
its changes. No additional build was started by the reviewer.

1. **Resolved: persistent process state dominates lossy wakeups.**
   `RuntimeSession::drain_terminals` now reconciles every persistent cancelled
   `exit_signal` into `ProcessEvent::Exited`, independent of the bounded queue.
   `has_ready_work` also observes an unrecorded persistent exit without turning
   an already-recorded exit into a busy loop. The deterministic unit regression
   `execution::source_tests::saturated_terminal_channel_reconciles_persistent_process_exit`
   fills the queue, proves the process notice cannot enter, drains every hint,
   then checks both event recovery and no repeated-ready spin. The actual native
   `exit_data_can_feed_a_live_peer_without_exporting_the_shared_view_first` fixture
   exercises the consumer which motivated the issue. This follows Tokio's
   [`try_send`](https://docs.rs/tokio/latest/tokio/sync/mpsc/struct.Sender.html#method.try_send)
   and persistent
   [`CancellationToken`](https://docs.rs/tokio-util/latest/tokio_util/sync/struct.CancellationToken.html#method.cancelled)
   contracts: the queue is a hint, not completion state.
2. **Resolved: administrative stop records cancellation and propagates failed joins.**
   `cancel_active` now calls `cancel_transaction` for each implicit owner before
   manager abort. That path records node/process/cohort cancellation, joins real
   native programs and flow tasks, releases namespace permits and removes pending
   owner obligations. Previously successful checkpoint receipts are not rewritten
   as cancellation. `retain_cleanup_failure` retains the typed cause and paths,
   and a failed join makes stop return an error. Ordinary expected cancellation
   remains nonfatal. The native `stopping_a_native_owner_cancels_old_dependencies_without_poisoning_the_session`
   regression checks that an old scalar dependency fails constructively after
   stop, no private bytes publish and a new independent assignment succeeds.

The source review also traced `region` Start admission, `quiesce` staged-file
Publish gates, `settle_region` RegionCommit before manager finish, and the local
rollback sequence: mark the pending epoch discarded, physically join/discard its
IO/native work, restore Manager pending state, then record EpochReset. Canceled
children satisfy region closure only through the successful reset event, never
fabricated LogicalComplete or Committed events. Finite/HTTP/status observations
remain ordered observations, not general time-travel cancellation candidates.
Fresh native launches after staged-file handoff use a new epoch; co-start proof
is limited to an actual shared native view. This bounded re-review found no
additional concrete P1 defect in those paths.

Actual platform regression results remain owned by the integration/CI acceptance
record. This review does not claim a full current-tree pass or performance
acceptance. The suspected nested-reset lookup issue was retracted: graph
construction already flattens child `epoch_resets` into the parent, so the root
lookup is valid under that explicit invariant.
### Resolved P1: native deferred-ancestor gate exposed an owner-binding cycle

The actual WSL `execution_workspace_v4::native_import_gates_future_source_and_destination_traversal`
journey timed out in the integration run while the scratch-user-data journey
passed. The runtime trace showed stdout node 4 admitted/spooled before blocked
finite nodes 1–3. Those finite copies subsequently ran correctly against the
imported pending native view, but stdout retained its earlier immutable plan,
which did not yet bind their owner. Its Publish gate therefore demanded foreign
`NodeCommitted` events instead of same-owner `NodeLogicalComplete`; the named
transaction could not commit until that output published. This is a real
closure cycle, not a filesystem traversal or native export failure.

The integration owner's correction binds every named-body node and each Spawn
actually declared there to its known runtime owner at region begin, before any
child WorkNode can retain a plan. References to foreign process ports are not
relabelled. This uses ownership already determined by the language and preserves
immutable retained witnesses and the stricter foreign-publication gate. The
integration owner then ran the exact actual WSL native fixture successfully,
including raw parent traversal for source/literal/stdout destinations and named
origin undo. Its trace confirmed Imported and same-owner LogicalComplete gates.
This closes the reproduced owner-binding cycle; it is not a full-platform
acceptance claim. No additional reviewer build was launched.

The reaper now drains ready owners to a ledger-progress fixed point before
subscribing for a new event. A later owner's publication in the same pass can
therefore unblock an earlier owner without requiring another external wakeup.
Only newly recorded ledger outcomes advance the progress counter, so rereading
persistent exited states does not create an unbounded fixed-point spin.

## FIFO rendezvous follow-up review (2026-10-28)

Scope: the uncommitted native FIFO refinement after `26b83a7`. This review traced
the graph, retained native identities, flow admission, and stream open paths; it
did not run an additional build. The following concrete defects were sent to the
integration owner and are not considered closed merely by a proposed correction.

1. **Whole-cohort capacity needs whole-cohort prerequisites.** `flow_lease` reserves
   every connected member's slots when the first member starts. Consider writer
   node 0 and reader node 2 sharing a FIFO while TCP node 1 must finish publishing
   the reader's output file first. If node 0 can reserve both stream slots before
   node 1 starts, it waits for its reader while the prerequisite cannot acquire
   capacity. The existing
   `fifo_cohort_waits_for_all_external_prerequisites_before_reserving_slots`
   journey is the discriminating regression. The binding layer must require the
   union of all external Start events before any member reserves capacity, while
   preserving each event's already-bound owner visibility. Reinterpreting a
   dependency under a different target owner or weakening it to admission would
   be unsound.
2. **Physical FIFO aliases require physical channel ordering.** Pair construction
   groups endpoints by retained device/inode, but pathname-only ordinary effects
   cannot serialize separate waves through hardlink aliases. For one FIFO with
   names `a`, `b`, `c`, and `d`, `one -> a; b -> out1; two -> c; d -> out2` can
   otherwise appear as two independent groups. Concurrent readers then split or
   interleave both payloads instead of preserving the logical wave order. This
   differs from managed regular-file writes, which replace path bindings. The
   integration owner's correction should attach the retained physical stream
   identity to every observed FIFO access, including unpaired endpoints. Only
   the certified opposite pair can turn its corresponding Write/Consume hazard
   into admission; same-side and cross-wave channel hazards remain completion
   dependencies. A pure alias graph regression and a real hardlinked-FIFO wave
   journey should verify this distinction.

The opened FIFO descriptor is checked against the retained native inode before
payload IO, and writer retries revalidate the pathname before waiting on a new
reader. Linux metadata-only leases pin unlinked inode generations without acting
as FIFO readers. These are appropriate ownership mechanisms; a pure certificate
alone is not such a lease. Normal EOF must remain different from failure or stop.

Cross-submission FIFO joining is not established by the current same-region HIR
proof. Older flows retain a distinct `NodeKey` and plan, and the external witness
currently requires committed completion for a conflicting new endpoint. A safe
extension can bind a private old-NodeKey/new-endpoint rendezvous from both actual
native leases and unique pair ownership, weakening only the exact opposite FIFO
hazard to old admission. All other effects retain their old bound visibility.
Peer failure before opening also needs scoped counterpart cancellation; successful
writer completion must instead permit the reader to drain buffered bytes to EOF.
This is an explicit integration boundary, not evidence that cross-input FIFO
joining already works or a reason to waive old-flow dependencies.

The release scope keeps the current conservative cross-input conflict contract:
FIFO jobs can wait for external physical peers, but two msync flows that must
self-rendezvous belong in one submission (for example, one macro call or a
`/load` script). Program ports retain their already-supported cross-input
connections. A user who has submitted only one self-FIFO endpoint should first
stop that pending job, then submit both endpoints together; `/wait` cannot create
the missing peer. This guidance must accompany a blocked REPL operation rather
than suggesting that waiting alone necessarily makes progress. A future
cross-input implementation would need the retained native leases, unique
NodeKey-qualified pair registry and scoped peer-failure handling above; it is
not part of the current same-batch proof.

### Platform capability must include a real generation pin

A device/inode observation without a retained non-payload native handle does not
prove generation identity after unlink and inode reuse. Targets whose
`FifoIdentity` has no such lease must not mint the positive rendezvous proof;
their existing external-peer FIFO transport can remain conservative.

The initially proposed macOS `O_EVTONLY` lease is not established as such a
primitive. Inspection of Apple's current
[`fifo_open`](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/miscfs/fifofs/fifo_vnops.c)
shows reader accounting and forced read mode without an event-only bypass;
[`open1`](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/vfs/vfs_syscalls.c)
still passes that mode through normal vnode open. Combined with the proposed
read-open without nonblocking mode, this can obstruct preparation or act as a
real FIFO reader. This is a source-derived inference, not a macOS experiment.
Until a non-reader handle is established, omit that lease path and reject the
positive self-rendezvous capability there rather than making a speculative
cross-platform guarantee. Linux `O_PATH` remains separately verified by the
actual inode-pinning/no-reader fixture.

### Source recheck after correction

The current event binder now unions each physical group's already-bound external
Start events, excluding only the group's own admission facts. The alias repair
adds `StreamIdentity { provider, identity }` effects for all observed endpoints,
not only paired ones, and retains separate `StreamPath` hazards. Physical-byte
writes no longer invalidate filesystem shape merely because they use a pathname.
The structured provider requires a retained generation lease, and the speculative
macOS event-only open has been removed. These are coherent source-level fixes
for the three concerns above. The integration owner's exact external-prerequisite,
hardlink-wave and platform-pin regressions remain the acceptance evidence; this
review did not run an additional build or infer their results from the code.

One further concrete admission hole was found when tracing FIFO input into a
program: `physical_input` spawned and retained its flow without recording
`NodeAdmitted`, unlike `physical_transfer` and the ordinary program-port flow.
With the FIFO outside the program's managed cwd, the reader-first sequence
`fifo -> program::stdin; literal -> fifo` can therefore strand the writer on a
missing admission event while its peer waits for a kernel writer. The correction
must record admission after the real task/flow is owned, not mark completion or
weaken the rendezvous dependency. A writer-first test does not exercise this
direction. This was sent to the integration owner for a native stdin regression.

The integration owner subsequently reported a clean terminal WSL run covering
all 17 ordinary physical cases, both actual native physical cases (including the
reader-first FIFO-to-program stdin direction), 68 graph cases, four FIFO identity
cases, 21 ledger cases and the two focused stream-kind preparation cases. Strict
changed-target Clippy passed, with no production changes after that build. This
closes the reproduced FIFO findings for that tested Linux/provider scope. The
review itself added no duplicate build and does not infer untested-platform or
cross-submission FIFO support from those results.

## Independent-flow capacity audit (2026-10-28)

After the FIFO corrections closed, a separate requested probe tested transient
capacity rather than inseparable co-start feasibility. The actual development
CLI ran this batch against two real localhost TCP listeners, writing into
different existing output directories:

```text
left => tcp://127.0.0.1:<port-a>
right => tcp://127.0.0.1:<port-b>
left::/ -> out1/result
right::/ -> out2/result
```

The first server deliberately sends `one` and EOF after 150 ms; the second sends
`two` and EOF on connection. No future human input or shared byte channel is
required. With `--max-streams 1`, the CLI exits with failure at 82 ms, before the
first EOF, and never connects to the second listener. It reports that a ready
"program stream cohort" exceeds the limit and incorrectly advises that serial
waiting on a connected pipe would deadlock. The same journey with limit 2 exits
successfully at 205 ms, and both output files contain their exact expected bytes.
These are correctness-control runtimes, not performance acceptance measurements.
Disposable scripts and fixture paths are retained under `.temp/review-admission-*`.

`execution::scheduler::Admission::acquire` turns a failed `try_acquire_owned` into
an immediate execution error. The region loop has already selected a Start-ready
node and does not distinguish temporary occupancy by independent work from a
permanently infeasible co-start component. This is a functional bounded-scheduling
gap: the configured concurrent ceiling should allow this batch to serialize,
not cap its total number of independent operations. Keep static inseparable
cohort overflow as an early refusal; when an actual running producer can free
capacity, batch execution should retain the node, pump ready/completing work and
retry admission without starting that node's effects. REPL admission may still
refuse constructively when progress depends on future human input or an absent
external peer. Any queueing correction must preserve atomic whole-cohort
reservation and must not hide a genuinely impossible process/flow component
behind an infinite resource wait.
