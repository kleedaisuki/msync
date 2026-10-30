# Recoverable statement publication

## Contract and scope

`filesystem::durable::Batch` replaces the selected sibling roots of one finite
statement. It retains no language transaction, checkpoint, MVCC history, or user
rollback operation. On normal completion every selected root is new. After an
msync process crash, a fresh recovery invocation restores the entire old set if
there is no complete durable commit decision, or preserves the entire new set
and finishes cleanup if that decision exists. Recovery is idempotent.

This is **recoverable atomicity**, not simultaneous multi-path visibility to
arbitrary external readers. An existing ordinary file uses a hard-linked private
before-image and an atomic replacement rename when supported. Directories and
filesystems without that hard-link capability use retained before-image renames;
readers can observe the rename interval until recovery/publication completes.
Uncooperative writers, hardware lying about flushes, and external processes or
streams are outside this finite-publication guarantee. Foreign root identity
changes block recovery rather than authorize overwriting somebody else's data.

Windows process-crash recovery is supported by synchronous namespace calls and
synced data/intent files. The implementation does not claim a universal Windows
hard-power-loss guarantee: Win32 has no portable directory `fsync` counterpart
in this adapter. On Unix the protocol explicitly syncs affected directories;
hard-power durability additionally requires the filesystem/storage to honor those
sync operations. Process exit injection is evidence about process crashes, not a
power-cut experiment.

## Ownership and durable order

1. Destination-parent ownership uses a directory file lock on Unix and a kernel
   mutex keyed by the physical volume/file identity on Windows. Neither leaves a
   lock artifact in user output or a later copied source tree. Abandoned Windows
   mutex ownership admits recovery, not blind publication. The Windows guard is
   explicitly thread-affine and lives inside a blocking statement worker.
   Contending internal workers wait instead of reporting a false user conflict.
2. Every replacement is privately materialized in a sibling publication journal.
   New file data and private directory entries are synced. Targets remain old.
   Production acquires a scratch registry lease first and registers each empty
   owned stage before filling it. `Batch::prepare_registered` does the same for
   its empty coordinator before writing the intent. Stage registrations can
   therefore clean up interrupted private work without discovering destination
   state from a missing intent.
3. A coordinator `.msync-statement-*` records the complete member set: validated
   target/journal basenames, platform encoding, parent identity and before/after
   node identities. Unix before-image directory modes are also retained: the
   platform rename helper can temporarily add owner-write permission to a
   read-only directory, and a crash between that chmod and its restoration must
   not leave the old public tree with changed permissions. Recovery restores this
   mode even when the original root identity never moved. No serialized absolute
   path can escape the physical parent.
   The intent is synced and installed before a destructive rename is admitted.
4. Each backup/install namespace change is followed by journal and parent sync
   on Unix. Optional observers run only after this recovery cut is established.
5. Once every member is installed, a complete synced `committed` record is the
   decision. A torn or absent decision is not a commit.
6. Cleanup removes obsolete before-images, syncs their removal, then removes and
   syncs the intent **before** it can remove the commit record. Therefore an
   interrupted cleanup cannot reinterpret a partially deleted committed journal
   as an uncommitted statement.

`Batch` owns all publication receipts and the destination lock. Dropping it does
not silently attempt recovery. The caller must `abort` before the decision or
`finalize` afterwards. Startup discovery is an explicit scratch registry owned by
`filesystem::recovery`; its records identify exact coordinators and physical
parents. `recover_journal` validates the recorded basename and takes the same
parent lock. `recover_parent` is the bounded manual/test discovery operation.
The complete coordinator registration becomes durable before its member-stage
and coordinator-stage receipts are retired; only then may publication start.
Recovery processes coordinator records before private-stage records, so a stale
stage receipt cannot discard an admitted before-image. The unregistered
`Batch::prepare` entry point remains available for explicit-parent/manual use;
it does not provide production startup discovery on its own.

The adapter does not create missing public destination ancestors. Its ordinary
statement caller must incorporate the highest missing ancestor into a single
private replacement root, so that a crash cannot leave half-created directory
scaffolding outside the publication decision.

## Encoding and diagnostics

The mature `serde`/`serde_json` stack encodes a versioned, bounded intent with
unknown fields rejected. Unix basenames retain raw bytes; Windows basenames
retain UTF-16LE units. Recovery rejects non-basename paths, duplicate members,
foreign parent/workspace identities, links/reparse points and changed target
identities before changing any member. These checks are ownership protections,
not authentication of journal files against another process with equivalent
write authority over every managed directory.

`FsError` retains the underlying IO error and recovery context. A synchronization
failure is an operation failure; it is not converted to a successful commit.
Post-decision cleanup failure must be presented as committed-with-retained-cleanup,
never as if the statement rolled back.

## Verification seams

`Batch::publish_with` exposes `Prepared`, `AfterBackup(index)`,
`AfterInstall(index)`, `BeforeCommit`, and `Committed`. The real subprocess suite
`tests/storage_crash_v5.rs` exits without unwinding at these cuts and starts a
different process to recover twice. It covers existing directory roots and an
originally absent file in one statement, plus an existing file's atomic replace.
A Unix mode-repair test directly reconstructs the narrow pre-rename chmod crash
cut. These tests are authored; execution
results must be recorded separately when the integrated tree builds.

CLI crash journeys can use the deliberately narrow test seam
`MSYNC_TEST_CRASH_AT` plus `MSYNC_TEST_CRASH_TARGET`: the latter must equal an
actual publication's absolute target. Cuts are `prepared`, `after-backup`,
`after-install`, `before-commit`, and `before-cleanup`; exit status is 86. This
does not change ordinary behavior without both test variables.

## Design sources

- [SQLite atomic commit](https://sqlite.org/atomiccommit.html): order durable
  before-images and the commit decision, including journal-directory persistence.
  Its database format is not imported as an unnecessary dependency here.
- [Linux fsync(2)](https://man7.org/linux/man-pages/man2/fsync.2.html): syncing file
  data does not persist the containing directory entry; directory sync is separate.
- [Rust File locking/sync APIs](https://doc.rust-lang.org/std/fs/struct.File.html):
  reuse standard OS-backed locks rather than PID files or custom stale-lock logic.
  Windows uses the corresponding native named mutex because directory handles
  are not portable byte-range-lock resources.
- [Microsoft FlushFileBuffers](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers):
  a write-capable handle is required. Private read-only staged files temporarily
  regain write access only to acquire that handle, then restore their attributes
  before syncing; published existing source/target files are not chmod'ed.
- [SquirrelFS, OSDI 2024](https://www.usenix.org/conference/osdi24/presentation/leblanc):
  ordering constraints and owned state transitions are the key reusable idea.
  Its persistent-memory filesystem mechanism is not directly applicable to a
  userspace cross-platform file copier.
