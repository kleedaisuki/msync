# Finite remote publication acceptance

The v0.5 interpreter must retain ordinary uploads, downloads, recursive trees,
explicit `/.` replacement and trailing-`/` container behavior after removing the
transaction manager. These are statement operations, not distributed transaction
scopes. Remote user programs and physical streams have their separate external
effect contract.

## Publication and restart ownership

`execution::endpoint::execute` distinguishes finite remote outputs from ports
and physical channels before opening payload data. The finite path captures an
invocation-owned source, then uploads only into an exclusively allocated remote
same-parent journal. No user destination is used as an upload staging area.

Before destructive publication, a local `.msync-remote-recovery-v1` record binds
the endpoint, remote journal and destination. A fully synced temporary record is
renamed into the registry; the writer retains its OS file lock. Startup recovery
contacts only recorded pending endpoints, skips another live writer's locked
record, and settles pending remote journals before admitting new effects.
Ordinary local invocations have no SSH/WSL dependency. An unavailable pending
endpoint is an actionable recovery failure, not permission to silently proceed.

Remote helpers lock a read-only descriptor for the parent directory itself,
then the journal lock. They neither create a reserved sidecar filename nor
truncate a user's pre-existing `.msync-publication.lock`. The platform mechanism
is documented by upstream [util-linux flock](https://man7.org/linux/man-pages/man1/flock.1.html);
filesystems such as NFS/CIFS may reject the required locking capability.
Complete staged
data, target intent, before/after device-and-inode identities and the existence
of a before-image are persisted before destructive renames. Existing ordinary
files retain an atomic final replacement. Existing trees may have a namespace
gap, but an undecided journal restores the complete old root during recovery;
a persisted commit receipt retains the complete new root and only cleans backing
data. Foreign target identity changes fail recovery while retaining evidence.
Object identity is a recovery guard, not a claim of immutable bytes under
uncooperative external writers.

### Read-only directory reparenting

On a real non-root native-ext4 probe, moving an owned `0500` directory within
one parent succeeded, while both `rename` and GNU `mv -T` across parents failed
with `EACCES`. Installing `journal/next` at a sibling public destination is a
cross-parent move: preserving real directory mode therefore requires the same
mode-intent discipline as the [local durable adapter](../../src/filesystem/durable.rs),
not a fallback data copy or chmod of the shared original source.

Before admitting any destructive transition, publication records before/after
root identities and optional `before-mode`/`after-mode` directory modes alongside
the intent, then syncs the journal filesystem. Only an identified selected old
destination and an invocation-owned new root may temporarily gain owner write
permission for reparenting. The installed root's recorded mode is restored and
synced **before** `COMMITTED`. Owned private payload promotion uses the same
bounded permission admission only when its parent actually changes.

Undecided recovery restores the old root and its recorded mode. It also handles
the important no-before-image case: the old root may still have its original
identity after permission admission but before backup, or after a recovery
rename interrupted before mode restoration. In either case the retained mode
record restores the original permissions idempotently. Foreign target or old
directory-image identities retain the journal instead of being chmod'ed or
deleted. Older journals without the optional mode files keep their existing
recovery behavior.

Permission admission checks the current actor's effective write access, not
the inode owner's write bit. A non-root probe with a root-owned `0570`
directory and writable current-user group proved that rename could succeed
although changing the owner bits returned `EPERM`. Restoration likewise skips
chmod when the recorded mode already matches: an unchanged old state must not
require ownership merely to conclude recovery. Directory cleanup leaves
already-effective read/write/traversal access alone for the same reason.

Cleanup only grants traversal/removal rights to owned directories, without
following symlinks. It never chmods regular files: a removed old tree may contain
files with hard-link aliases outside the selected root, whose metadata must not
change merely to remove one link. Installed new roots are cleaned only after
their recorded identity is proven. The normal and crash-cut protocol tests must
run as non-root on native ext4; root or Windows-only execution would conceal
this reparenting constraint.

After commit or complete rollback and mode restoration have been synced, the
helper **unlinks and syncs the intent before any destructive journal GC**.
Otherwise, a legal recursive-deletion prefix could remove `COMMITTED` and part
of the before-image while leaving `intent`, making a restart roll a complete new
root back to a partial old tree. An independent non-root actual-helper probe
reproduced that failure (`.temp/mode-review-cleanup-prefix.log` while retained).
Once intent is durably retired, every later cleanup interruption is GC-only;
no remaining before-image may be reinterpreted as recovery input. Failed
identity checks, mode restoration or rollback never retire intent. This mirrors
the local durable adapter's separation of outcome resolution and backing-data
retirement, without introducing language-visible transaction scopes.

An independent corrected gate exercised the actual shell helpers as UID `1000`
against source file SHA-256
`38E6C02D35D087FF7B2DFA294FB5A7EDFD36453701057A2B37C7A4DB54EA8FA3`.
A root-owned, current-group-writable `0570` old directory successfully published
a new `0500` directory. An unsupported non-owned `0555` old directory failed
before logical changes, then recovery retained its complete bytes/mode and
retired the stale journal. Separate actual `SETTLE` and `RECOVER` executions
were killed after durable intent retirement and a legal destructive GC prefix
(removing `COMMITTED` and one before-image child). Fresh recovery kept the full
new `0500` tree and its inode, left the external old-file hard-link alias at
`0400`, and completed GC. Evidence is retained locally in
`.temp/mode-review-group-write-fixed.log` and `.temp/mode-review-fixed-results.log`.
These tests validate the helper protocol under process interruption, not the
unexecuted full Rust adapter gate or a physical power-loss experiment.

The server requires GNU-compatible `sync -f`, which flushes the containing
filesystem, not merely one payload file. This is a correctness-first fallback
for an agentless transport and can flush unrelated writes; it is not a latency
optimization. See the primary [GNU sync documentation](https://www.gnu.org/software/coreutils/manual/html_node/sync-invocation.html)
and [POSIX rename specification](https://man7.org/linux/man-pages/man3/rename.3p.html).
The isolated SSH chroot explicitly installs `sync`; no existing SSH config is
changed. Persistence still depends on the server filesystem and storage honoring
its synchronization contract. Program and stream side effects are not rolled
back by this protocol.

## Real workflow tests

### Selection precedes acquisition

A regex source is a sparse snapshot of matching direct children, not a full
directory snapshot whose reporting is filtered afterward. Local upload capture
uses `SnapshotStore::capture_selected_in` before materialization. Remote download
first requests only direct basenames with `REMOTE_LIST`, applies the anchored
Rust regex, and invokes recursive validation and transport only for selected
children. A selected directory retains its full subtree and metadata. A no-match
source produces an empty private directory and no public destination effects.

Unselected special files, symlinks and Windows-unrepresentable names do not enter
validation or payload transport. As with local `OsStr::to_str` selection, a
non-UTF-8 direct basename cannot match the Unicode regex and is skipped; a full
unfiltered tree import still rejects names it cannot represent safely. Selected
symlinks and special files remain rejected. `tests/ssh.rs` exercises these
distinctions against the isolated authenticated server; upload material tests
inspect the actual private payload, not merely byte counters. The new selection
regressions were added after generation 5 and require a rebuilt executable.

Finite import carries an `ImportReceipt` with the acquired source basename.
This is acquisition provenance, not an inode identity or an immutable-file
claim. SSH uses the root name already present in `REMOTE_VALIDATE`; the generic
WSL copy helper returns its resolved source name alongside transfer counts in
a NUL-delimited receipt. No separate basename probe is added. Source spellings
such as `~/` and `.` therefore retain their actual name for container insertion
instead of being mislabeled as literals. Selected sources have no root basename:
their sparse envelope is private and only selected child names are public.

Import transport scratch and recursive child staging stay beneath the caller's
registered private owner, so concurrent user-tree capture excludes live payloads
by the existing ownership proof. The native WSL bulk fast path is unchanged.
A launched generic WSL finite acquisition is drained even if normal cancellation
arrives; cancellation is checked before admission and again before adopting the
private payload. Killing the Windows launcher alone would not prove the guest writer has
stopped and would make storage retirement unsafe. The new receipt and contextual
root regressions require the next rebuilt executable; earlier candidate SSH
stream testing failed at the `~/` finite-copy prelude before reaching FIFO/socket
transfers and cannot be cited as stream acceptance.

The import scratch root is on the same filesystem as its caller-owned target.
The complete received file or sparse tree is therefore adopted with the existing
private no-clobber move primitive, rather than recopied through the legacy tree
copy path. This removes a redundant payload pass and preserves acquired mtime,
permissions and object identity. A pre-existing private target is an error;
normal cancellation before adoption leaves it absent. A private move error is
not permission to replay the copy, since it may follow a completed rename or
failed read-only-directory mode restoration. No public destination has changed
at that point, and the enclosing owner retains cleanup responsibility.

SSH uses [OpenSSH SCP's `-p`](https://man.openbsd.org/scp) together with existing
recursive transfer and safe literal-name handling. Portable uploads are clamped
inside private staging as described below before publication. The generic WSL
helper applies timestamps to newly created directories after copying children,
and applies source mode only when backed by native POSIX-mode provenance, using GNU
[`touch -r`](https://www.gnu.org/software/coreutils/manual/html_node/touch-invocation.html) and
`chmod --reference`; it does not overwrite existing merge-directory attributes.
`ssh_finite_transfers_preserve_portable_file_and_directory_metadata` checks actual
file, root, nested and empty-directory mtime in both directions at SCP's supported
whole-second precision. Private-adoption units also check same-object identity,
read-only metadata, no-clobber and cancellation behavior. The metadata changes
require a new build and acceptance run; earlier receipt-only results are not
evidence for them.

#### Mode provenance and Windows permission translation

An observed Unix mode is not always proof of source POSIX permissions.
[Microsoft's WSL permission model](https://learn.microsoft.com/en-us/windows/wsl/file-permissions)
explains that metadata-disabled DrvFS synthesizes owner/group/other bits from
the Windows user's effective access; this is not a Windows ACL-to-POSIX identity
mapping. An owned probe confirmed that referencing a Windows directory observed
as `0777` changed a fresh native directory from `0700` (umask `077`) to `0777`.
The native fixture was removed; the local log is
`.temp/windows-mode-reference-probe.log` while retained.

A separate actual Windows OpenSSH probe used the isolated SSH fixture with
umask `077`, identical Windows source data and raw recursive uploads with and
without `-p`. With `-rp`, the resulting directory/file modes were `0707/0666`;
without `-p`, they were `0707/0600`. Thus adding preservation globally widened
the uploaded file's rights, and the pre-existing directory behavior was already
unsafe as a purported portable permission mapping. Neither mode proves the
original Windows ACL allowed corresponding Unix principals to write. The probe
verified Windows/Linux SSH configuration, passwd, group and shadow snapshots
unchanged and shut the private namespace down through its control pipe. Its
reproducible local script/log are `.temp/ssh-mode-probe.{ps1,log}` while retained.

Permission preservation therefore requires provenance through private staging:
an ext4 inode created by SFTP from a Windows source is not sufficient evidence
that its inherited mode was an original POSIX mode. Timestamps can be preserved
independently. Unknown portable modes must not add group/other access beyond the
destination's own creation policy; source read-only evidence can only attenuate
access. The initial blind-reference metadata patch must not be released without
this policy correction and actual regression evidence.

The implemented bounded model is `RemoteSource { path, permissions }`, where
`Filesystem` and `Portable` describe the origin, not the current staging inode.
Windows-host uploads remain portable after SFTP creates their private Linux
inodes; Linux-host sources may retain filesystem provenance. Within the generic
helper, filesystem provenance permits full mode preservation only for positively
recognized native POSIX filesystems (ext4, tmpfs, XFS, Btrfs and overlayfs), checked
per entry so a nested mount cannot inherit its parent's proof. DrvFS and unknown
filesystems retain the destination's own creation policy. Timestamps are always
preserved. Portable files use GNU
[`cp --preserve=timestamps`](https://www.gnu.org/software/coreutils/manual/html_node/cp-invocation.html)
into an initially `0600` temporary file rather than importing synthetic mode or
execute bits. Before adoption its mode is `0666 & ~destination_umask`, with any
read-only evidence removing write rights afterward. Thus ordinary umask `022`
produces `0644`, while umask `077` produces `0600`; compatibility does not require
unnecessarily removing read rights that the destination operator chose to grant.
New directories likewise retain their creation umask. A source mode with no
write bits can only remove write rights. The uploaded data remains below an
exclusively allocated `0700` staging parent until this clamp has completed.

This does not promise a Windows ACL-to-Unix-principal mapping, or preservation of
Unix group/other mode bits through Windows materialization. Windows read-only
file behavior is retained as permission attenuation, not interpreted as a full
directory ACL. Container publication recursively merges existing directories
without explicitly applying incoming attributes to them; only new subtrees are
copied with their already-clamped metadata. Matching destination symlinks are
rejected before directory tests or traversal, while unrelated symlinks remain
unfollowed in the cloned destination tree. The actual SSH metadata test uses an
isolated daemon with umask `077`, checks writable-file `0600`, directory `0700`,
Windows read-only-file `0400`, existing merge-directory `0710` preservation, and
both untouched and conflicting symlink cases. These final policy corrections
still require the next compiled acceptance gate.

#### Windows SCP read-only timestamp repair

The first full Rust SSH metadata gate exposed a narrower transport defect after
directory metadata had passed: a read-only file received the current mtime.
An isolated raw-client comparison established the exact layer. On
`OpenSSH_for_Windows_9.5p2` / LibreSSL `3.8.2`, `scp.exe` file version `9.5.6.4`,
raw `scp -r -p` returned status zero but printed `local set times ...:
Input/output error` for a source mode `0440` file. Its local read-only flag was
set but mtime changed from `978307207` to transfer time. A writable `0640` file
and directories retained their expected whole-second times. The frozen msync
adapter reproduced the same difference, excluding private adoption and snapshot
publication as its origin. Raw stderr, source stat values, local attributes and
configuration-snapshot checks are in `.temp/ssh-download-mtime-probe.log` while
retained.

The same private read-only local file accepted a timestamp update without
clearing its read-only attribute. This matches the narrow
[`SetFileTime` access contract](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfiletime):
metadata updates require `FILE_WRITE_ATTRIBUTES`, not data-write access. The
existing `NodeMetadata::apply` already uses this access on Windows.

The bounded repair extends the **same existing** `REMOTE_VALIDATE` traversal
with a versioned NUL-delimited receipt of relative path, basename and integer
mtime, using GNU find `%Ts`. Parsing validates framing, root position, host-safe
POSIX wire path components, duplicate/case-colliding names and checked time
ranges before starting SCP. After the client exits, owned captured entries have
their ancestors and leaf kinds revalidated against symlinks/reparse points, then
their received timestamps applied with existing metadata APIs while retaining
their local permissions/read-only flags. Empty-relative-path root files use the
shared root resolver rather than appending a directory separator. The receipt
does not claim an immutable observation of an externally mutating source, full
ACL translation or precision beyond the transport's whole-second contract.
No warning-text heuristic, extra remote helper or global preflight is added.
Parser, root-read-only-file and no-follow tests accompany the repair; the same
strict real SSH metadata test remains the acceptance gate rather than having
its mtime assertion weakened.

The unchanged strict metadata journey then passed on CLI SHA-256
`58B78058477A26241B3BDEB756D52742DCBB3DAA6D2470DA5ADB8E4067132EC9`
with test executable SHA-256
`7AB27ACF764E7DEDCCE8B8885350D3E5AC060E75E63AAC2ABD58C7B0CCE21587`
(1/1, 30.72 seconds). It reached the previously failing read-only file mtime
check and all subsequent assertions: exact/selected root-file timestamps,
read-only flags, both-direction directory/file mtime, umask-`077` modes
`0600/0700/0400`, existing merge-directory `0710`, unchanged unrelated data and
symlinks, and refusal to traverse a matching destination symlink. Existing SSH
configuration snapshots and both executable hashes were unchanged after the
run. Exact local evidence is `.temp/ssh-timestamp-{metadata.log,result.json,hashes.log}`.
The other seven real SSH suites passed on the preceding receipt-independent
image; they are not represented as rerun on this final targeted image.

A real generic-WSL baseline probe with CLI SHA-256
`5408D067A9547B50E5C7F93204F0E3CC22A74856461548A9BCD962EB56B640A8`
correctly copied one 12-byte binary file, an empty directory and the acquired
`source` root into an existing remote container, preserving an unrelated entry.
Its metadata check failed: source-root mtime `978307206` became `1793271132`.
This disproved receipt-only correctness for portable metadata and motivated the
private-adoption and fresh-directory-attribute corrections above. The isolated
`/tmp/msync-remote-v5.*` fixture was cleaned; the local structured probe record
is `.temp/generic-wsl-receipt-baseline-failure.json` while retained.

`tests/remote_publication_v5.rs` adds a portable static compilation check and
five explicit Windows/WSL journeys:

1. A default synchronous external child creates a previously absent local
   source. A later basic block uploads it, then downloads the actual remote
   result. Compilation must not reject the source before the child creates it.
2. A recursive tree with Unicode/quote-bearing names and empty directories is
   uploaded and replaced. Replacement removes obsolete children. An exact
   existing directory is rejected; a later container collision must not expose
   an earlier new child from the failed statement.
3. Two literal writes to one remote file are followed by a readback. The final
   bytes must reflect dependency order, not stale observations or lost writes.
4. A writable parent does not authorize replacement of an existing file whose
   write bits are all zero. Rejection preserves its bytes and `0444` mode and
   leaves no publication journal. A subsequent literal replacement of a writable
   `0640` target retains that mode, and a directory cannot replace an existing
   ordinary file through exact-target syntax.
5. Parent-directory locking preserves a pre-existing user's same-named sidecar
   byte-for-byte, including its mode, mtime and object identity. A second parent
   with no such file remains free of reserved lock artifacts after publication.

The remote fixture is exclusively allocated under `/tmp/msync-remote-v5.*` and
checks `stat -f` for native ext4 (`ef53`). Windows-side files remain on the project
volume. This is intentionally a cross-filesystem functional journey, **not** a
native-Linux performance claim. No existing SSH configuration is changed.

The existing `tests/ssh.rs` owns the isolated SSH service fixture: private
mount/PID namespaces, an ephemeral chroot and host/client keys, and a per-fixture
client config. It checks existing Windows and Linux SSH files before and after
the journey. Its upload/download, recursion, overwrite and conflict tests remain
the real SSH regression gate; the new test does not duplicate that fixture or
substitute mocks for authenticated transfers.

## Running and evidence status

After the unified build window, run the portable target normally. Explicit WSL
execution uses the target with `--ignored --skip remote_source_child`; the child
fixture is launched only by its parent journey. Windows OpenSSH, WSL root,
`sshd` and namespace support are needed for the separate ignored SSH target.

Generation 3 of the coordinated Windows debug build executed the portable test
and all three real WSL journeys successfully (3 WSL tests, 30.51 seconds). Their
fixture asserted native ext4 before executing. The test executable SHA-256 was
`8ECD0A1EE282230BE703E99F0C5C87C89580C86997F3AAC7CE10A46EA185D82C`.
Logs are `.temp/barrier-remote-gen3-{portable,wsl}.log` while retained locally.

The same generation passed 7/8 direct ownership/registry tests. The failing
shared-invocation case exposed a real proof mismatch: the artifact store was
created under a child invocation while capture excluded its enclosing session
invocation. The constructor now retains the same enclosing proof used by
capture; this correction requires a later build and rerun. Generation 3 is not
claimed to validate that correction or the later upload-orphan cleanup change.
Authenticated SSH and a physical power-loss experiment remain separate gates.

A separate shell-protocol probe executed under WSL and passed normal existing
file publication plus simulated interruption after backup and after installation
before the commit decision. All five helper scripts also passed actual `sh -n`.
An additional receipt-lifecycle probe published one file, allocated a second
sibling statement before settling the first receipt, then verified both commit
receipts and payloads. This guards against broad orphan scans deleting a live
client's commit receipt. These probes ran on the project's Windows-mounted WSL
path; they are not native-ext4 performance measurements.
These checks exercise the helper protocol, not the Rust adapter, authenticated
SSH journey, or a physical power-loss experiment.
After generation 3, a further shell probe verified that recovery deletes an
undecided private upload journal while leaving the user target unchanged. The
local coordinator record is removed only after that cleanup is acknowledged;
it no longer loses the orphan's discovery pointer before cleanup succeeds.
The read-only guard also passed a direct helper probe on WSL-native ext4
(`stat -f` type `ef53`): `0444` preserved old data and aborted without a journal;
a subsequent writable `0640` literal replacement preserved that mode. This
isolates shell policy correctness but does not replace the pending fourth Rust
end-to-end journey on a rebuilt executable.

After generation 4 was compiled, a P1 review found that shell parent locking
used a fixed filename with truncating redirection. The four lock sites now open
the parent directory read-only and use `flock` on that descriptor. A transformed
helper probe on native-ext4 WSL passed seeded user-file bytes/mode/mtime/identity
preservation plus absence of new sidecar artifacts through publication and
recovery. The production change and fifth Rust journey require generation 5;
generation 4's binary must not be cited as evidence for this correction.

Generation 4 subsequently passed the portable test (1/1), all direct remote
ownership/registry tests (8/8), and all four compiled native-ext4 WSL journeys
(4/4, 29.86 seconds). This verifies the shared-invocation proof correction,
restart ledger ownership and read-only/literal-mode policy in the Rust adapter.
The test executable SHA-256 was
`AF977DA71C474DE96BA7789272FBE0A80AE553A29CD9D5583C7CF1071608B6E8`;
the library test executable was
`7A9C39FF1F5F7AEA9C6CAA60315D50FC956ACC1B236222172DAEC705BB8FEBF5`.
Evidence and exact executable hashes are retained locally in
`.temp/barrier-remote-gen4-{portable,units,wsl}.log` and
`.temp/barrier-remote-gen4-hashes.log`. The directory-descriptor P1 correction
and fifth journey were added after that generation's compilation and still
require generation 5; authenticated SSH remains a separate gate.

Generation 5 passed the portable check (1/1) and all five real native-ext4 WSL
journeys (5/5, 40.72 seconds), including the directory-descriptor lock regression.
Its CLI executable SHA-256 was
`02AFF035972A273D4550DE699B338574AE8396ADF98FA8192922D76C606D0D18`.
Three isolated authenticated SSH suites passed: recursive merge/conflict
preflight, literal/regex names and conflict/failure/telemetry cleanup.

The fourth SSH suite passed its preceding upload/download/overwrite operations
but exposed a whole-working-directory source bug: remote materialization tried
to capture the local recovery registry's live locked record, producing Windows
error 33. Excluding the shared invocation alone was insufficient because the
persistent control registry intentionally lives outside it. Remote capture now
opens an existing local registry without creating it and retains its exact
identity as a snapshot exclusion. A new unit holds a live registration while
capturing its containing user tree, requires the control root to be absent, and
requires ordinary `.temp` user data and the other writer's record to survive.
This correction and the failed exact SSH suite require generation 6 validation;
generation 5 must not be called fully green. Detailed logs and executable hashes
are retained locally under `.temp/barrier-remote-gen5-*`.

Generation 6 passed all ten direct remote ownership/materialization units and
the exact previously failing authenticated SSH upload/download/overwrite suite
(37.14 seconds). The strengthened literal/regex SSH suite also passed, including
unselected FIFO/symlink, non-UTF-8 and Windows-unrepresentable names, recursive
selected subtrees, no-match no-effect behavior, and rejection of a selected
symlink. Both fixtures verified existing SSH configuration snapshots unchanged.
The CLI SHA-256 was unchanged before and after these serial direct-executable
tests: `914A0B641E8A7B85844B0E6277E0BFE016082323F0058F796A0ABE39F2D19186`.
Exact executable hashes, logs and exit results are retained locally under
`.temp/barrier-remote-gen6-*`. These targeted results validate the source-capture
and selection corrections, not an unexecuted complete Windows/Linux test matrix.
