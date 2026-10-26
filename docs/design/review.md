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
