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

## Owned scaffold directories are namespace shells

An invocation may create a configured scratch ancestor that physical capture
correctly prunes while it contains only this invocation. That logical absence
must not prevent the user from later assigning an ordinary child in the same
container. `InvocationRoot::scaffold_directory` grants this narrowly by recorded
created-directory identity, not by `.temp` spelling or a generic exclusion list.
Pre-existing user directories do not receive the grant.

The logical WriteSet and Store remain unchanged. At publication, a verified owned
shell with logical `None <-> Directory` transitions expands into the union of its
before/after direct logical user children, recursively applying the same identity
proof to nested owned shells. Ordinary child subtrees retain normal whole-tree
receipts. The physical shell and private invocation are never exchanged or
deleted; replacing such a shell by an ordinary file is explicitly refused.
Other enrolled roots still use whole explicit replacement, including the existing
refusal to exchange an unsafe unproved ancestor of backing stores.

Each expanded shell receives an ordinary directory metadata receipt. Its first
successful entry into the overlay retains the pre-publication native metadata;
logical absence restores that baseline, while a visible directory uses its exact
candidate metadata. Child data receipts and shell/outer-directory metadata share
the existing compensation guard, and new baselines become durable Manager state
only with the successful Store swap. Failed publication restores both children
and physical before-images without installing an overlay baseline. Normal data
roots bypass before-manifest construction; only invocation ancestors need the
identity-proof/decomposition path.

Physical pruning remains unchanged. During host refresh, an already visible,
identity-proved owned shell is retained as a logical directory even when empty;
otherwise an explicitly published empty user directory would disappear merely
because physical capture hides the invocation-only shell. A logically absent
shell stays absent. The retained overlay, not its filename, supplies this fact.

Direct Manager regressions cover checkpoint/origin undo, unchanged shell/backing
identity, exact shell and enclosing-root timestamps, child-publication failure
compensation, file-replacement refusal and explicit empty-directory survival
across refresh. The workspace owner additionally verified the real reference
and finite-session journeys for a fresh scratch target, source copying and named
undo, plus pre-existing user sentinels and source pruning. Cross-submission copy
uses an independent output parent so the test does not waive the existing strict
named-origin conflict on a shared directory metadata version. Native process
coverage remains an explicitly separate backend gate.

Latest joined Windows scaffold gate: **40 manager tests, four metadata tests and
11 workspace tests passed**, with the two native-backend/child-fixture cases
explicitly ignored. Scoped Clippy reports no Manager/helper diagnostics; strict
whole-library completion still depends on the independently evolving event/pump
module's unused-code/lint work. Owned source/test rustfmt and diff checks pass.

## Disjoint file coverage while an implicit flow is active

Sparse enrollment must not turn an unrelated live TCP flow into a global ban on
reading a second file in an already enrolled directory. The public `enroll` and
`enroll_entry` lifecycle guards remain unchanged. A crate-private runtime path
can import one previously unobserved finite direct-child subtree (or absence) under an
existing sparse namespace when every active Manager owner has a corresponding
retained implicit-flow effect witness. Running/pending native programs, named
contexts, missing witnesses, unknown effects, and overlapping resources do not
qualify. The runtime builds this inventory from the actual old WorkNode plans,
not reused IDs in a new input.

The Manager independently checks the complete active ID set, ReadCommitted
isolation, actual pending writes/dirty parent metadata, observed subtrees and
exact observations in the imported scope. Retained parent-kind lookups do not
become fictional directory enumerations. A directory tree/predicate read, dirty
parent, overlapping byte/stream path or changed native parent attributes fences
the import. Path comparisons use native canonicalization and the existing native
name index, not portable guesses about Unicode or Windows verbatim spellings.
TCP/HTTP byte endpoint names cannot alias native filesystem paths; WSL/SSH and
unresolved endpoint evidence stay conservative.

The observation does not publish a filesystem change or update the directory's
logical mtime. Capture, validation, foreign `seed` operations and coverage update
are installed atomically, and every new version uses its real current epoch.
Nothing is backdated into an older statement or repeatable snapshot. A new RC
statement can see the imported value; an already-open RC statement retains its
older visibility until its explicit statement boundary. RR/Serializable owners
remain outside this narrow private path. New stream/device roots are refused before payload capture; finite directory
imports keep the normal guarded snapshot capture and reject unsupported entries. Parent metadata is checked after applying
only the identity-proved invocation mtime normalization.

Focused units cover safe disjoint import with a retained TCP and filesystem
footprint, unchanged parent metadata and old RC visibility, incomplete/unknown/
overlapping/tree-read inventories, RR/Serializable refusal, actual dirty-parent
and external-parent changes, and finite-directory import visibility. Complete promotion of an existing sparse
namespace computes a semantic import frontier using native directory names only.
Complete known branches are skipped; known metadata-only directories recurse;
an unknown node/tree (including an unknown structural ancestor) is a new
visibility obligation. Owner/observation/write guards apply to these actual
frontiers rather than a fictitious write to the entire existing root. Real
namespace/tree observers and dirty directory metadata remain anchor fences even
when no new keys are found. Thus an already-pinned exact read of known `A/a1`
does not serialize capture of an unrelated unseen `A/nested` tree.
The established `capture_extension` path keeps known branches and metadata
unchanged, while only unseen keys receive current-epoch foreign observations.
It must not substitute a nested child import for an explicit `.` directory
source. A focused promotion test checks that known managed bytes are not refreshed
even if the live host changed, and older statements still see their old subset.
The runtime's real two-directory REPL/TCP regressions are separate gates.
Final compatibility gates for the private path passed on Windows (seven focused
import/visibility/fence units, 46 existing Manager filesystem cases, strict
library Clippy) and in actual WSL private tmpfs (15 ordinary workspace cases and
four native program-view cases). In particular,
`sparse_files_extend_while_an_existing_file_flow_in_another_directory_is_active`
exercises a new flat child, a previously unobserved nested source, explicit
complete-root use and a later literal while an unrelated existing-file TCP job
remains active. `sparse_root_upgrade_preserves_an_active_immutable_reader_of_a_known_file`
holds a real 4-MiB known-file-to-TCP flow against a tiny receiver window during
namespace upgrade and nested-file copying. These are actual held flows, not just
independent path strings. The native cases also verify sparse roots in another
cwd are upgraded before a new program view, without changing parent metadata or
snapshotting unrelated FIFO/socket siblings. Correctness runtimes are not
performance measurements.
