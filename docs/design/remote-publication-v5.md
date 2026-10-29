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
