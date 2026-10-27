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
