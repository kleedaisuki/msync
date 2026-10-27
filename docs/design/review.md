# Independent implementation review

Reviewed the executable paths in syntax, compiler, CLI, telemetry and file/transport execution against `docs/dsl/spread.msync` and the implementation contract. Review concentrated on unintended writes, literal path semantics and error reporting rather than adding unrelated features.

## Confirmed findings and regression evidence

1. **Windows staging escape (high impact).** `receive` originally split the logical remote basename on `/` and passed the result directly to `PathBuf::join`. A remote path such as `folder/D:\project\.temp\unrelated.txt` therefore replaced the local scratch prefix. A real localhost HTTP request overwrote an unrelated existing file before the DSL destination policy ran. `tests/review.rs::http_remote_basename_cannot_escape_scratch_directory` reproduces this with all files confined to project `.temp`. The regression accepts either a safe rejection before network I/O or a safe opaque staging representation, and requires the unrelated file to remain unchanged. The runtime owner is validating native basename components before staging.

2. **SFTP still performs globbing (material correctness).** SCP's SFTP mode avoids a remote shell but does not make source paths literal. OpenSSH `sink_sftp` calls `sftp_glob(..., GLOB_NOCHECK | GLOB_MARK, ...)`. If both `a[1].txt` and `a1.txt` exist, validating the former literally and then passing its unescaped name to SCP can download the latter. Escape SFTP glob metacharacters for downloads, without applying the same escaping to literal upload destinations. Primary implementation reference: [OpenSSH portable scp.c](https://github.com/openssh/openssh-portable/blob/master/scp.c), `sink_sftp` and `sftp_glob`.

3. **Formatter cleanup ownership.** On `create_new` failure, unconditional cleanup of the proposed temporary pathname can delete an existing file the invocation never created. Cleanup must begin only after successful exclusive creation. Related telemetry must mark formatter output/write failures, not only parse or formatting-difference errors.

4. **Relative SSH upload source.** The SSH uploader derived its basename from the unresolved local `Path`; `.` and `..` have no `file_name`, unlike their absolute resolved directory. Normalize before deriving the transport basename so normal DSL directory operands retain the same semantics across backends. Local-to-SSH should not allocate a local staging directory unless actually needed, particularly when syncing the current project.

5. **Directory copies into an ancestor mutate the source (high impact).** A one-directional containment check rejects copying a directory into itself but allowed its parent as the destination. With `P/nested/item` holding one value and `P/nested/nested/item` holding another, `P/nested -> P` maps the deeper file onto `P/nested/item`, destroying a source value during traversal. Reject overlapping directory trees in either direction, for both native and remote execution. `tests/review.rs::directory_copy_to_ancestor_does_not_mutate_its_source` is the real-filesystem regression; the pre-fix implementation incorrectly succeeds and mutates the source.

6. **Windows lexical containment misses verbatim-prefix aliases.** Lowercasing covers ordinary ASCII case differences but does not reconcile native `D:\...\source` with `\\?\D:\...\source`. A non-mutating probe on the actual workspace volume confirmed both spellings read the same `identity.txt`, while the lowercased lexical prefix test is false. A missing target `\\?\D:\...\source\new` therefore bypasses the self-copy guard. Recursive copying was intentionally not executed. Resolve existing filesystem identity (canonical source and nearest existing destination ancestor, or ancestor identity checks) before enforcing two-way containment; do not treat Unicode lowercasing as a filesystem canonicalizer.

These findings were sent immediately to the production owners for correction. The runtime owner added native-component staging validation, SFTP glob escaping, absolute source normalization, conditional local scratch allocation and two-way overlap rejection; the root owner repaired formatter cleanup ownership and output-error span status. The Windows clobber and ancestor-copy reproductions failed before correction and now both pass (`cargo test --test review`); the review test target also passes Clippy with warnings denied. Live SSH authentication was not available in this review, so the SFTP finding is based on the exact upstream implementation rather than claiming an authenticated transport test.

## Effect planner and asynchronous flow redesign review

Three independently reproduced gaps in the redesigned pipeline are retained in
`tests/review_v2.rs`:

- **WSL/native aliases must share hazards.** A real WSL test writes a new value to
  `/mnt/d/.../shared` through the WSL backend, then reads the same file through its
  native `D:\...\shared` name. With two copy jobs, missing RAW ordering produced
  the old value in the second destination even though the shared file ultimately
  contained the new value. This is an observed wrong result, not just a theoretical
  endpoint-alias concern. Hazard-only resource projections should unify proven
  drvfs/native aliases without rewriting execution backends or granting additional
  dead-store/forwarding proofs. The same projection must also *disqualify* a
  rewrite that would hide or introduce a potential same-file copy: an operation
  from native `shared` to WSL `/mnt/d/.../shared` is not an eliminable assignment
  merely because its endpoint spelling differs. This negative proof does not
  establish path equality for elimination.
- **Unresolved remote basename cannot narrow a container effect.** Remote `~/`
  expands to a home directory whose basename is not `~`. Treating its container
  write as `container/~` omitted the dependency on `container/<actual-name>/file`.
  The deterministic graph regression never reads or copies a real home directory;
  it checks the conservative broad-container footprint directly.
- **Destination close must be observed while source is idle.** A TCP source held
  open without data, with a sink that accepted then closed, retained the flow and
  source handle until explicit cancellation. The pump only observed remote child
  exit while waiting on source reads. Native destination close notification must
  participate in that wait. A TCP FIN describes peer sending-half closure, not
  proof that it cannot receive; the chosen one-way DSL policy must state explicitly
  whether that notification ends the logical arrow flow. The root chose that it
  does, avoiding an accidental claim of generic full-duplex semantics.

Reproduction commands are `cargo test --test review_v2` for graph/native TCP
checks, and `cargo test --test review_v2 wsl_mounted -- --ignored --nocapture` for
the live Windows/WSL check. All fixtures remain under project `.temp` and the TCP
failure probe cancels and joins its task after a bounded timeout.

The three initial redesign regressions were independently rerun after their
owners' corrections and passed, including the real WSL reproduction. The
additional potential-same-file/DSE regression was then reported as a related
conservative-eligibility issue rather than weakening resource identity rules.
It too now passes: all three normal `review_v2` tests pass, and the explicitly
executed Windows/WSL test passes independently. The planner disqualifies
overlapping source/destination resources from scalar rewrites, using conservative
hazard information only as a reason not to optimize.

### Windows pipe-state query FFI

Reviewed the native pipe close watcher and its speculative `STATUS_PENDING`
fallback. Microsoft documents `ZwQueryInformationFile` and
`ZwSetInformationFile` as synchronous APIs even when their file object was
opened for asynchronous I/O: the I/O manager marks these operations with
`IRP_SYNCHRONOUS_API`. See the Remarks of
[IoIsOperationSynchronous](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ioisoperationsynchronous)
and the final-completion output contract of
[NtQueryInformationFile](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntqueryinformationfile).

Consequently the simplest supported implementation uses stack-owned output
structures for this small local NPFS metadata query. It does not need to expose
pending state, block the reactor on a subsequent file-handle wait, leak heap
buffers, or invent a cancellation subsystem. The kernel may pend its internal
IRP, but the synchronous API completes that wait before returning to user mode.

The old fallback's generic completion argument was not valid: with concurrent
overlapped operations, a shared file-object signal does not identify which
operation completed. Microsoft explicitly describes this limitation in
[Synchronous and asynchronous I/O](https://learn.microsoft.com/en-us/windows/win32/fileio/synchronous-and-asynchronous-i-o).
That is a reason to remove the unsupported fallback, not evidence that the
documented synchronous API leaves the buffers pending on conforming Windows.

## Indexed planner, namespace contexts and native I/O review

`tests/review_v3.rs` adds independent checks beyond scalar final-state examples:

- 96 generated mixed programs (up to 74 operations) check that **every raw hazard
  between retained operations remains reachable** after frontier retirement and
  sparse transitive reduction. Cases include hierarchical paths, unknown/missing
  classes, unresolved ENV/PATH wildcard resources, real hardlink identity, and
  Windows/native versus WSL `/mnt/<drive>` hazard projections. This checks the
  planner's public effect contract rather than repeating the index implementation.
- A 24-wave co-start pipeline checks ordering through each completed file snapshot
  before the next atomic stream group. A separate finite copy must retain an empty
  predecessor set instead of inheriting a global stream fence.
- A real piped REPL loads definitions from one script, changes working directory,
  then loads another script. Dynamic `$file` namespace aliases resolve the latter
  source unit, while `:= $file::this` retains the first script. Direct REPL input
  rejects missing source-file context instead of silently reusing the last load.

Two native data/lifetime issues were confirmed and corrected:

1. **Source reset was treated as successful EOF.** A loopback TCP server wrote an
   incomplete prefix then closed with `SO_LINGER=0`. The stream pump classified
   `ConnectionReset` as normal source closure and atomically published that prefix
   over an existing complete file. The regression failed with the old target
   replaced by the prefix. Only successful source EOF may publish a file stage;
   source read errors now fail and discard it. The remote Python source helper
   likewise must not convert reset/abort into exit status zero. Destination-close
   handling remains a distinct one-way flow outcome.
2. **A duplicated Windows source handle shared its mutable cursor.** The public
   file-to-TCP API initially ignored a nonzero source cursor. After correcting its
   explicit transfer offset, an actual seek-to-31 probe still observed the caller's
   cursor advance to 32799 during a native attempt that requested fallback. The
   buffered fallback delivered the correct suffix, so this was not reported as
   duplicated network data. `File::try_clone` did not isolate the file object.
   The native backend now opens an independent overlapped file object from the
   existing handle rather than reopening a pathname, preserving both identity
   and the caller's cursor through native attempts, fallback and future drop.
   [ReOpenFile](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-reopenfile)
   provides the handle-based reopening mechanism and explicit overlapped flags.
   [TransmitFile's documented offset contract](https://learn.microsoft.com/en-us/windows/win32/api/mswsock/nf-mswsock-transmitfile)
   is the relevant API boundary.

All five V3 review tests were independently rerun successfully after those fixes.
The cursor test permits a capability fallback, resumes on the same connection and
source identity, and checks exact suffix bytes; it does not claim every provider
or file shape used the native fast path. The Windows backend's separate capability
test explicitly verifies a completed native transfer before making that claim.

The owned native completion design was also inspected: the blocking worker owns
its file/socket/event and OVERLAPPED record until that operation's private event
is drained; dropping the async future requests stop without freeing worker-owned
memory. A tagged private event suppresses an alien completion packet on Tokio's
IOCP. Source/file stages must remain owned until Tokio's pending physical file I/O
has drained, not merely until a future or cancellation token changes state.

Fallback is authorized only by a pre-submission unsupported result. After
TransmitFile accepts an operation, a failed completion query cannot authorize a
retry of that request: Microsoft's
[WSAGetOverlappedResult contract](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-wsagetoverlappedresult)
does not supply an updated transferred-byte count on failure. The backend now
fails such an ambiguous completed request instead of risking a duplicated
socket prefix. An unsupported *next* submission may still resume buffered I/O
after the exact prefix of earlier confirmed successful requests.

The private completion event is independently owned and valid throughout its
infinite wait. If that invariant nevertheless fails, returning or panicking
could release a still-pending stack OVERLAPPED. The narrowly scoped policy is
fail-stop, not unchecked completion-query fallback or speculative leak machinery;
ordinary I/O failures and cancellation still follow normal drained error paths.

The final independent rerun also passed all 11 Windows interruption tests,
including abort after real file writes, existing/new target preservation,
native file-handle release and write-only named-pipe disconnection. Strict Clippy
also passes for `review_v3` with warnings denied after the integration settled.

### Follow-up: Unix metadata compatibility and remote commit authorization

The two explicit Unix review regressions were executed on a **private tmpfs
mounted at the project's `.temp`**, not on DrvFs. The test process ran as UID/GID
1000 with cleared supplementary groups/capability bounds; its recorded effective
capability mask was `0000000000000000`. This matters because a root process with
DAC override cannot establish ordinary write-permission behavior.

- Copying a mode-0755 source over an existing mode-0644 file preserved the new
  content and final mode 0755. The current finite-copy stage commit does not
  silently restore the old target's mode.
- For a mode-0400 existing target in a writable parent directory, an actual
  non-truncating write-open first returned `PermissionDenied`. Executing the
  finite copy likewise failed and left the original bytes intact. Thus atomic
  replacement no longer bypasses the established target write-access contract.

Reproduction setup: use the cached Linux `target/wsl-performance` build with
`cargo test --offline --test review_v3 --no-run`; enter a private mount namespace,
make mounts private, mount tmpfs with `size=128m,mode=1777` at `.temp`, then run the
resulting test binary through
`setpriv --reuid=1000 --regid=1000 --clear-groups --bounding-set=-all`.
The exact ignored test names are
`finite_copy_preserves_source_execute_mode_over_existing_target` and
`atomic_file_publication_does_not_bypass_an_unwritable_existing_target`.
Both passed independently.

The exact production remote Python helper was also exercised directly under WSL,
with its unchanged `path mode capacity` arguments and a 20-second process deadline.
`remote_file_helper_requires_an_explicit_complete_finish_frame` passed all four
wire cases: complete DATA followed by raw EOF, truncated DATA, and an oversized
header all failed without changing the old target; complete DATA plus explicit
zero-length FINISH published the new bytes. Every helper exit left no unpublished
stage behind. This is executable evidence for the decoder and commit gate, not
a claim that ordinary stdin closure authorizes file publication.

The protocol audit also required two distinctions: the commit fence applies only
to a remote regular-file sink (otherwise a finite producer feeding an unattached
FIFO becomes uncancellable), and a fully authorized remote commit may have an
ambiguous acknowledgement after transport loss. Post-FINISH errors must not claim
that the target was rolled back. The implementation uses typed readiness,
bounded frames, an explicit commit acknowledgement and a bounded finalization
deadline rather than treating a disconnected SSH process as proof of rollback.

### Final cache, deferred-kind and pathname-publication audit

The preparation cache is scoped to one plan. Source-sensitive ENV/PATH resolution
is keyed separately from physical endpoint classification; after resolution,
the latter can safely deduplicate the same location across operands. Remote
classification retains ordered, individually framed results, and operand-span
selection happens after retrieval rather than being cached with a previous
caller. Offline preparation does not launch a remote client. No substantive
remaining defect was found in these paths during this review.

A directory-producing predecessor may leave a descendant's pre-execution kind
unknown. Execution admits that deferred finite case only behind a preceding
finite write overlapping the resource; it does not globally equate unknown
metadata with a usable stream. The actual finite executor revalidates the path
after the dependency completes. The focused scheduler regression executes a real
directory production followed by its child read, rather than merely checking
that the planner accepts a graph.

Atomic regular-file publication has an intentional, observable identity contract:
assignment replaces the **destination pathname binding**, not the contents of
every other hardlink to its old inode. Other hardlink aliases retain the old
complete bytes. This differs from the earlier in-place copy implementation and
must be documented as a migration boundary, not hidden as an optimization.
Keeping in-place alias mutation would conflict with per-file interrupted-write
rollback and with queued Linux sendfile references to old source pages.
Existing same-file source/destination rejection remains in force, and planner
identity hints remain conservative dependency evidence, not positive rewrite
proofs.

`publishing_a_file_replaces_its_path_not_other_hardlink_bindings` establishes real
hardlink identity before copying, then verifies new destination bytes, untouched
old alias bytes, distinct final identities, unchanged source and absence of leaked
stage files. On Windows/NTFS, the final focused command
`cargo test --locked --test review_v3 --test scheduler` passed six review tests
and all eight scheduler tests. One explicitly ignored WSL helper audit is not
counted in those six; its separately executed protocol evidence appears above.
The scheduler set includes actual deferred-child execution, finite-flow shared
capacity and remote framing-capacity rejection before any connection.

## v0.3.1 independent frontend and terminal review

Scope: the new CLI presentation/error boundary, editable REPL, display-only lexer,
shell shortcuts, named diagnostic rendering, and native PTY test ownership.
The I/O engine and optimizer were not re-audited for this UI-focused release.
The review follows the product contract in `cli-ux-v0.3.1.md`, particularly
recoverable failures, exact input ownership, plain payloads and terminal handoff.

### Resolved: keep context-capture failures inside the input loop

The initial `repl_inner` DSL branch constructed `SourceUnit::repl(&line)?`
outside the per-submission result. If the current directory disappeared, that
question-mark operator returned from the entire REPL instead of printing the
failure and allowing `/cd` recovery. The new explicit shell shortcut makes this
a practical interaction: a child can remove an empty directory which remains
the parent process's current directory.

An actual Linux process in a private native tmpfs reproduced this sequence:

```text
keep := saved
/bash rmdir -- "$PWD"
next := value
/cd ABSOLUTE_EXISTING_PARENT
/bindings
/quit
```

The application reported `cannot determine working directory` with native
`ENOENT` and advice to use `/cd`, then exited with status 1 before processing
that recovery command. The experiment used only an owned project `.temp`
fixture and an ordinary noninteractive child; no profile/history or user file
was changed. The disposable reproduction is `.temp/review-cwd-031.sh`.

The correction is to compose context capture with submission as one recoverable
result (`and_then` or an explicit match), preserving the existing environment
when capture fails. The regression should assert the error is visible, `/cd`
then succeeds, the earlier `keep` binding remains, the failed `next` declaration
does not leak, and the session exits normally.

The integrated source now uses `SourceUnit::repl(...).and_then(...)` inside the
common per-input result. Independent regression
`tests/review_ui_031.rs::a_deleted_working_directory_does_not_end_the_repl_or_lose_bindings`
passed on actual WSL/Linux with native private tmpfs fixtures. The focused
`cargo test --locked --test review_ui_031 -- --nocapture` process exited 0;
the test observed the deleted original directory, visible cause/help, successful
absolute `/cd` recovery, retained earlier binding, no failed-declaration leak and
normal REPL exit. This verifies recovery rather than merely changing the advice.

No additional substantive defect was found in the inspected editor/shell/display
paths. In particular, the review checked raw shell-tail preservation, no-argument
validation before mutations, original signal-disposition restoration, child-only
Unix signal resetting, the non-inheritable Windows console callback, memory-only
history, source-name/control escaping, UTF-8 span handling and test process/job
ownership. Buffer-redux read-ahead retention and real before-Enter coloring are
covered by the separate actual-terminal validation rather than inferred from a
string-coloring unit test.

The Windows signal reasoning was checked against Microsoft's
[SetConsoleCtrlHandler contract](https://learn.microsoft.com/en-us/windows/console/setconsolectrlhandler):
process-local callbacks do not set the inheritable ignore-Ctrl-C attribute.
The checked-in editor notes also retain Rustyline's equal-display-width contract;
diagnostic escape expansion must not be substituted into the editor's cursor
geometry model.
