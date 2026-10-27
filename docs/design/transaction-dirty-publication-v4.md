# Read domains and dirty publication in v0.4

The transaction manager reads immutable versions from enrolled domains, but its
physical write set is separate. Enrollment of a directory does not authorize
replacing that directory for every child assignment. Doing so would rewrite
untouched sibling identities, fail on unrelated open Windows child files, and
move the manager's own backing stores when the enrolled root contains `.temp`.

## Ownership and state

Each transaction retains two canonical-key sets:

- **data subtrees**: actual explicit replacements/deletions, reduced to minimal
  roots before publication;
- **directory metadata**: namespace times/permissions which must be applied after
  child operations. Existing file overwrites keep their parent metadata value;
  namespace insertion/deletion/kind changes write a new directory version.

Checkpoints publish the pending sets; the origin sets accumulate across all
successful checkpoints. Named rollback obtains its desired values from the
version store and publishes only those origin scopes. It does not swap an entire
read root or erase a later unrelated sibling update. Store conflict validation
still precedes physical preparation.

Data publications reuse retained sibling before-images. Directory metadata
receipts own an open directory handle, a file identity, the old/new metadata and
an owned diagnostic journal beneath the snapshot store. Ordinary shared file
inodes are never chmod'ed or timestamped by these metadata receipts.

## Publication, compensation and cleanup

1. Capture directory before-images **before** allocating sibling data journals.
2. Prepare all dirty data privately, then publish those exact subtrees.
3. Apply directory metadata after the child operations, then atomically install
   the logical candidate store.
4. Finalize data journals, and reapply the final directory metadata last.

The last ordering is necessary: creating and deleting a sibling journal itself
changes the parent directory's mtime. A flat receipt loop which applies metadata
and then deletes data journals silently loses the desired final timestamp.

Failure batches restore data before metadata. Failed compensation retains the
batch and blocks publication; immutable reads remain available. A successful
commit with failed cleanup remains committed and returns cleanup warnings.
Further dirty publication waits for outstanding cleanup repair, so an old
metadata-cleanup retry cannot overwrite a newer directory version. A no-op
`finish` and reads remain usable while cleanup is pending. This is in-process
logical atomicity for manager participants, not OS-wide atomicity or crash
recovery.

## Program views and persistent sessions

`stage_view_delta(id, path, before, after)` compares the exported view with its
actual immutable starting version, rather than comparing an old export with the
current transaction and deleting another program's changes. It uses canonical
resource keys, portable metadata and actual file-byte equality, not size/mtime
as a content proof. Only changed minimal subtrees and metadata are staged;
overlapping newer values fail atomically without losing prior pending changes.
The convenience `stage_delta` uses the current view as its starting version.

Output-delta merging is not read-independence proof. The caller must still
coordinate isolation or supply sound read/effect premises before concurrent
program execution. Two disjoint output filenames do not establish serializability.

Read coverage may expand only between active transactions, retaining finished
transactions' named origins. Resources use stable physical-volume namespaces
and absolute normal component keys; enrollment descriptors do not own identity.
The native name index is shared by physical parent, so a later common ancestor
and its earlier child descriptors resolve to the same versioned keys.

`Manager::new_in` retains an `InvocationRoot` ownership token. Host captures and
assembled snapshots carry its identity-checked exclusion, while attempts to
address the backing subtree as a user resource are rejected. Dirty child
publication leaves that enrolled ancestor and backing workspace in place.

## Focused evidence

The manager target includes real filesystem regressions for an unchanged open
and externally hardlinked sibling, parent-time restoration after compensation
and journal cleanup, disjoint/overlapping program deltas, metadata-only view
changes, retained undo across domain enrollment and an invocation inside the
enrolled read ancestor. The existing multi-root cancellation/recovery, committed
cleanup warning, case-policy and origin/checkpoint tests remain unchanged.
Fixtures are exclusively created beneath project `.temp`; native Unix/cloud
acceptance is recorded separately from the local Windows evidence.

The final local Windows gate passed **23 manager cases and four independent
metadata cases** together. Strict library/manager/metadata-target Clippy passed.
The byte-delta regression deliberately restores an edited file's original mtime
and keeps its size unchanged, so a metadata-only content heuristic cannot pass.
The cleanup-failure regression holds a Windows journal handle without delete
sharing and verifies that later directory publication waits, then succeeds after
explicit repair. No complete unrelated local suite was run for this change.

## Sparse physical namespaces

`enroll_namespace(directory)` records only the immutable directory node, never a
child listing or child payload. `enroll_entry(direct_child)` then captures one
complete ordinary subtree (or its known absence) under that stable resource
namespace. Both require no active transactions; repeated registration preserves
existing versions and named origins. A live FIFO/socket sibling is neither read
nor silently discarded: it remains outside known data coverage.

`read_entry` provides node kind/metadata without a child-listing claim. It can
read the sparse namespace directory itself, allowing destination parent checks.
`read_snapshot`, `replace_snapshot`, and program delta import reject that sparse
root and unknown siblings; only explicitly covered complete child subtrees may
be snapshotted or replaced. Registering a known-missing leaf distinguishes
absence from an unobserved entry. Full ordinary `enroll` remains strict and
rejects a tree containing a FIFO. Sparse-to-complete upgrades and broader
ancestor registration import only previously unknown coverage, never refresh
existing versions or reset saved origins. A partial host walk visits ancestors
of known branches while known complete subtrees reuse immutable manifests and
are not recopied. Nonlexical physical aliases remain explicitly refused.

Windows focused evidence now includes **24 manager cases plus four metadata
cases**, including sparse unknown-read refusal and retained undo after a second
child registration. A native Unix regression creates a real FIFO beside the
ordinary target, verifies strict full capture refuses it, and verifies sparse
publication leaves the FIFO in place; execution of that Unix-only case belongs
to the native Linux/cloud gate. End-to-end FIFO consumption is owned by the
physical execution tests, not inferred from the manager fixture.

The ancestor-expansion regression reproduces two separately loaded context
directories followed by a new output in their common cwd. It checks that host
drift in an already versioned child is not imported, saved named undo still
works, and a view sealed before expansion imports against unchanged resource
identity. On Windows it holds the known child file without any sharing during
expansion, proving the branch is not opened and recopied. A separate sparse
ancestor regression keeps unknown siblings unobservable while retaining child
origin undo. The focused Windows gate now covers **27 manager cases and four
metadata cases**.

The exact default CLI regression `load_source_context_is_scoped_namespace_aliases_are_dynamic_and_values_persist` also passes as a filtered real REPL journey. Root-origin undo additionally preserves parent metadata when that parent was enrolled only after the original write.

## Foreign host heads at quiescent boundaries

`Manager::refresh_host` preserves the version store rather than rebuilding a
session after shell, formatter, legacy execution, or ambient edits. It captures
all minimal complete coverage scopes and sparse namespace node metadata, then
compares native spelling, permissions, modification time, and actual file bytes.
The byte comparison is mandatory even when size and mtime match. Complete trees
observe additions/deletions; sparse unknown siblings remain unknown and live
FIFO/socket siblings are never opened. A sparse namespace that is no longer a
directory is refused rather than implicitly reclassified as snapshot payload.

Every capture and comparison finishes before a cloned Store is seeded and
swapped. `Store::seed` records foreign heads with no managed transaction author,
retaining old immutable versions and named origins. Subsequent reads see the
foreign state; an origin rollback overlapping a foreign head reports conflict
instead of overwriting it. An unchanged observation does not advance the epoch.
Refresh refuses active transactions, recovery, and pending publication cleanup;
in particular, it must not replay old metadata cleanup over newly observed host
changes. This is a coordinated quiescent observation boundary, not an OS-wide
snapshot or a claim to serialize unmediated host mutations racing capture.

The runtime calls refresh before each quiescent structured submission, including
named rollback, rather than trusting only a particular shell command handler.
Windows focused evidence passes **31 manager cases and four metadata cases**,
with strict target Clippy. Four refresh regressions cover identical-stat foreign
bytes and protected origins, directory membership/no-op epochs/active guards,
failed multi-root observation without partial imports, and sparse coverage.
The native Unix FIFO regression also invokes refresh with the live FIFO present.
Two independent real REPL journeys now pass: shell-modified sources are copied
with their new bytes, and named undo refuses to erase a later authorized shell
edit (`review_foundation_v4`, filtered `shell_edit`).

## Certified disjoint child publication

`certify_namespace_children` reserves actually missing direct-child native names
under an enrolled ordinary directory with unchanged writable permissions. Its
`NamespaceMergePermit` supplies the invocation provider, parent epoch, canonical
parent key and NativeNameIndex-derived child keys to the graph certificate.
Same-parent overlapping certificates share an epoch; case/native aliases and
reused reservations are refused. Parent identity and permissions are checked at
certification, before publication, and again before binding the native receipt.
Capabilities require explicit per-child completion/cancellation: Drop does not
silently publish, release a gate, or consume a successful mutation stamp. Closing
the final reservation releases the retained parent identity handle.

Only exact, implicit, ordinary-file insertions may use this capability. The
runtime must route eligible finite/literal and physical-source new children
through the same mechanism, not exempt physical streams from namespace effects.
`read_committed_file` provides a reachable, immutable ordinary-file lease without
observing a parent mtime or directory predicate. Disjoint ordinary-file
`enroll_entry` imports remain possible while the namespace is live. Ordinary
reads/tree replacements/metadata observers and origin rollback that intersect a
live parent are fenced; unrelated normal transactions are not globally blocked.
A strict transaction already observing that parent prevents certification.
Directory/tree/permissions/new-parent operations are never namespace merges.

Native IO can drain without an active Store transaction. At EOF,
`publish_merged_file` uses the runtime's reserved owner ID to begin a fresh
ReadCommitted publication, retaining ordinary Store conflict/undo policy. After
the native child install and before metadata application/journal cleanup it
observes the actual containing-directory modification time. For successful
receipts in that live parent epoch, the directory metadata is:

```text
no successful receipt  -> baseline metadata
otherwise              -> mutation_stamp(arg max successful logical_order)
```

The baseline never competes with a successful stamp. Neither maximum wall-clock
time nor EOF arrival order selects metadata; future-dated baselines and
backwards-moving receipt stamps are valid. A failed or cancelled receipt does
not advance the selection or consume its logical ticket. The returned receipt
carries both the observed mutation stamp and the selected logical order/stamp.

The stamp is unavailable when the private candidate is initially built.
`Store::replace_unpublished_head` therefore has a narrow parent-module-only
contract: the candidate head must be newer than the last published baseline,
non-missing and authored by this publication owner. It replaces only that new
head payload, preserving author, revision, origin and every older version. The
same stamp late-binds `DirectoryReceipt.after` and its journal before applying
metadata. Only after all data/metadata installs succeed does Manager swap the
candidate and advance the successful-receipt ledger. Compensation retains old
metadata and does not select the failed stamp; failed compensation blocks
further publication with the existing explicit recovery journals. Cleanup
reapplies the selected directory stamp after sibling data journals are removed.
General Store isolation levels and exact named-origin undo are unchanged.

Focused Windows evidence includes 37 manager regressions, four independent
metadata regressions, and one private Store receipt-binding test. New cases
exercise out-of-order EOF/source tickets, a future baseline and decreasing native
stamp injection through real directory metadata, cancellation after metadata
installation, failed compensation/foreign identity recovery, native/case alias
refusal, permission/identity revalidation, exact origin undo after lease closure,
unchanged open/hardlinked siblings, scoped barriers, immutable file leases and
disjoint sparse imports. These establish the Manager capability; graph/pump
end-to-end integration is separately owned and is not inferred from these tests.
