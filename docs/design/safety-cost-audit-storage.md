# Storage safety and cost audit (2026-11-01)

## Decision context

This audit treats msync as a single-user synchronization/programming tool, not a
sandbox for hostile scripts. The user chooses the source, destination and remote
endpoints. Process interruption, ordinary I/O failure, concurrent msync owners,
path aliases and existing hard links are real operating conditions. A hypothetical
attacker with equivalent write authority over all managed directories is not a
reason to reject normal user workflows.

The storage implementation contains valuable data-loss prevention and substantial
protocol/validation amplification. These are different findings: retain the
observable old-or-new statement outcome while replacing layers that repeatedly
establish the same ownership and persistence facts. Do not interpret a test that
asserts rejection as proof that rejecting the user is necessary.

Scope: `src/filesystem.rs`, `src/filesystem/{durable,recovery,invocation}.rs`,
`src/execution/{ordinary,ordinary_cohort,ordinary_fold,assignment,transfer,path_policy}.rs`.
The SSH publication protocol has its own concurrent audit and is not counted as
local storage here. Existing context used includes
[statement atomicity](statement-atomicity.md),
[durable publication](durable-publication.md),
[crash validation](storage-crash-validation.md), and the concrete previous clobber
and hard-link failures in [review](review.md) and [foundation review](review-v4.md).

Disposition vocabulary: **REMOVE** an unnecessary restriction/work item;
**SIMPLIFY** retain the invariant with fewer facts/owners;
**REPLACE** change the mechanism or representation; **KEEP** has a stated user
failure it actually prevents. Priorities describe user impact, not vulnerability
severity. These are recommendations unless implementation is explicitly linked.

## Direct user-journey evidence

The immutable v0.6.5 delivery executable used here has SHA-256
`f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`.
All fixtures, logs and the typed Python reproduction are under
`.temp/storage-cost-audit`. No host account/configuration was changed.

1. **Ordinary user alias is refused.** `alias` is a directory symlink to `real`,
   containing an ordinary 17-byte file. `alias/source -> target` exits 1 with
   `refusing symbolic link`; `real/source -> target` succeeds with the same bytes.
   This is a selected source-ancestor alias, not a recursive cycle, destination
   escape or attack. `results.json` preserves both outcomes.
2. **Group-read permission blocks even pure declarations.** After a normal copy
   leaves an empty `.temp/.msync-recovery-v1`, changing only its mode `0700` to
   `0750` makes `value = true` exit 1 at startup: the registry requires
   current-user-only permissions. Restoring `0700` makes the declaration succeed.
   The same user still owns the directory; no other user gains write permission.
3. **Six bytes require 39 synchronous flush calls.** With the valid empty registry
   already present, `source -> second-target` copies a six-byte ordinary local
   file to an absent target and exits successfully. An actual
   `strace -f -qq -yy -e trace=fsync,fdatasync,rename,renameat,renameat2` records
   **39 `fsync` calls**. The trace is `local-copy.strace`. This is a syscall-count
   observation, not an accepted latency benchmark; tracing changes elapsed time.
   In particular, the unchanged registry `.lock` and registry directory are
   synced twice, and invocation, publication-stage and coordinator-stage
   registration each have their own durable protocol.

Reproduce the first two cases by creating the paths above under a fresh project
`.temp` directory and running the same frozen executable with `--no-config run -`.
Use `MSYNC_STATE_DIR` pointing to a fixture-owned fallback path, and restore the
registry to `0700` after the permission comparison. For the flush count, first
perform one ordinary copy to create/empty the registry, then trace one further
copy to a new target. Exact syscall counts are image/lifecycle-specific, not a
promised constant for all copies.

## Inventory and disposition

| ID / priority | Mechanism and location | Actual benefit or missing threat | Normal-user cost | Decision and simpler direction |
| --- | --- | --- | --- | --- |
| ST01 / P1 | Blanket source/destination/ancestor symlink refusal: `filesystem.rs::safe_metadata`, `check_path`, `absolute`; `ordinary.rs::metadata` and `Ready::inspect` callers | Avoids accidentally traversing a link during capture/delete; old basename/alias bugs caused real wrong-file writes. That does **not** establish that a user-selected symlinked source ancestor is an attack. | Symlinked homes/workspaces and ordinary linked project trees cannot sync; Windows additionally rejects every reparse-point kind. Every ancestor is repeatedly observed. Real alias rejection reproduced above. | **REPLACE** with explicit link semantics: resolve selected root/ancestor aliases to an owned physical root once, represent recursive link entries as links rather than following them, and keep cleanup bounded by retained identity. Do not merely delete checks while `Entry` still cannot represent links. |
| ST02 / P1 | Snapshot model contains only `Directory`/`File`: `filesystem.rs::Entry`, `capture_entry`, `materialize`, `validate_tree` | The model makes finite payload handling straightforward; streams genuinely need a separate lifetime. It has no representation for an ordinary symlink. | Any selected recursive symlink makes the whole statement fail; an existing target tree containing one can also prevent replacement because `prepare_inner` recursively validates it. | **REPLACE** the two-case model with typed link entries and per-platform materialization. Reject actual unsupported stream nodes when finite capture requires it, not all filesystem indirection. Mature `cp -a` preserves links as data. |
| ST03 / P1 | Recovery owner/mode gate: `recovery.rs::validate_owner`, `validate_directory`, `validate_private_file` | Private defaults avoid exposing staging. A check that an existing registry is not writable by an untrusted actor can have a specific shared-directory threat model. Group-read/execute bits alone do not grant record modification, and same-UID hostile processes already have equivalent authority. | `0750` registry or `0640` records are rejected; an empty readable registry prevents declaration execution. Restored backups, group-managed workspaces, `sudo`/UID changes and network-mounted modes can stop all work. | **REMOVE** the requirement for zero group/other bits as an admission invariant under the single-user model; keep private creation defaults, record kind/identity and live-owner locks. If shared-writer protection is desired, state that threat explicitly and check actual write authority instead of treating readability as corruption. |
| ST04 / P1 | Multiple independently durable registration layers: runtime invocation, publication stage, coordinator stage, full coordinator; `RegistryLease::register_stage`, `persist_registration`, `Batch::prepare_registered`, `ordinary.rs::prepare_registered/publish` | Exact registration lets restart reclaim abandoned private data without scanning arbitrary similarly named directories. It prevents deleting another owner's live stage. | A six-byte local copy issues 39 `fsync`s; every private stage receives marker+record+directory barriers, followed by transfer/retirement barriers. | **REPLACE** layering with one statement owner and explicit stage-to-published state transitions under that owner. Keep exact durable discovery before destructive public writes; permit stage cleanup obligations to be coalesced instead of creating a standalone coordinator protocol for every nested private resource. Derive the crash cuts first, then count calls in the same real journey. |
| ST05 / P1 | Re-syncing an unchanged recovery gate on every `Registry::new`: lines around `file.sync_all()` and `sync_directory(directory)`; `ordinary.rs::Storage::new` recreates its handle each completed statement | Creation of a new gate/directory needs persistence if it is recovery authority. Reopening the same unchanged existing `.lock` does not create new content or a namespace entry. | Two unconditional flushes per open; observed twice in one six-byte copy lifecycle. | **REMOVE** unchanged-existing-gate flushes. Distinguish exclusive creation from existing validated gate; persist only newly created control state. Preserve the existing incomplete-initialization behavior deliberately rather than replacing it with a new early refusal. |
| ST06 / P1 | Capture local source into immutable asset, then copy asset again into publication staging: `SnapshotStore::capture_entry`, `materialize`, `copy_new`; remote import can add another intermediate | Immutable acquisition prevents later writes/source aliases from changing a running statement; a private same-volume replacement prevents partial destination visibility. Both outcomes matter. | Two payload copies and two temporary payload allocations for an ordinary one-consumer local copy, in addition to source and old target. Container merges also capture old destination data. | **REPLACE** duplicate materialization with a single statement-owned stage that doubles as captured input when ownership is unique; adopt/move it into publication, use reflinks where available, and retain reusable immutable artifacts only for actual fan-out. Never replace the capture with a mutable hard link to a live user source. |
| ST07 / P2 | Recursive `validate_tree(target)` before replacement: `SnapshotStore::prepare_inner` | Detects nodes that current cleanup cannot handle. Needed failure is cleanup capability, not trust of every unrelated old leaf. | A whole-tree replacement must scan the entire old tree before the actual rename/cleanup; existing links are refused even when replacing the root could simply retain/unlink them. | **SIMPLIFY** once cleanup is typed/no-follow: validate the selected target root and actual operations; safely unlink link nodes in owned old trees instead of requiring every old leaf to be an ordinary file. Keep errors for nodes whose operation genuinely cannot be completed. |
| ST08 / P2 | Repeated path-wide metadata/identity verification: `check_path`, `ensure_identity`, `SnapshotStore::ensure_open`, `Registry::verify/contains_private/excludes`, `InvocationRoot::verify` | A retained owner must not delete a replaced directory. Existing prior tests demonstrate same-named user data must not be hidden/deleted. No series of path checks is a full adversarial TOCTOU sandbox. | Per-file traversal repeatedly rechecks stable owner roots and ancestors; directory capture invokes registry/invocation verification again at many nodes. On network/DrvFs paths this is nontrivial metadata I/O. | **SIMPLIFY** into retained directory handles/capabilities and operation-boundary identity checks; cache only immutable owner metadata, not stale public filesystem truth. Measure syscall counts for a large ordinary tree, not just parser/plan CPU. |
| ST09 / P1 | Parent-directory-wide publication lock: Unix `flock` on directory; Windows global mutex keyed by parent identity; `durable.rs::lock_parent`, `Batch` | Concurrent msync publishers/recovery must not interleave compensation so one destroys the other's installed root; this is a concrete concurrency failure, not a hostile actor. | Independent sibling-file statements serialize while the lock covers staging sync, intent, install, commit and cleanup. Waiting is currently unbounded at the kernel API. | **SIMPLIFY** lifetime first: avoid holding the parent lock during work that cannot affect other owners, and retire independent GC after outcome resolution where identity permits it. Target-scoped locks require ordered multi-target acquisition; do not replace one clear lock with a global lock service. Add observable contention/cancellation before new lock machinery. |
| ST10 / P2 | Uncancellable lock acquisition: `File::lock`, `WaitForSingleObject(INFINITE)`; recovery index locks | Waiting preserves another live owner's state; deleting/stealing its lock is wrong. Unbounded blocking is not necessary for that property. | `/stop` or cancellation can remain waiting on another long-lived publisher/filesystem operation; a blocked copy is difficult to distinguish from transport delay. | **REPLACE** with cancellable ownership-aware waiting and phase/owner diagnostics while retaining the live lock. The response is to wait/help, not report a false file conflict or kill a different process. |
| ST11 / KEEP | Same-file/overlap protection: `transfer.rs::relation`; root/store/private-storage exclusion in `prepare_inner` and `ordinary.rs::check_private_destination` | Prior executable regression showed directory-to-ancestor traversal destroyed a source; active journals cannot be renamed underneath ongoing writers. | Some apparently idempotent self-copies are errors, and overlapping trees are unavailable even after immutable capture. | **KEEP** actual active-owner disjointness; **SIMPLIFY** ordinary file self-copy into a defined no-op if the language contract chooses it, and revisit overlap only with typed captured-source semantics. Do not remove the entire category as "security". |
| ST12 / KEEP | No-replace native creation/rename and cleanup only after exclusive creation: `copy_new`, `move_new`, `create_owned_directory` | Prevents truncating or deleting a pre-existing file the invocation never created; previous formatter and staging reviews contain concrete clobber failures. | A few system calls and collision retry; allocator retries are capped at 128. | **KEEP** exclusive creation/identity ownership. The cap is allocation collision handling, not a reason to reject normal user path lengths. A random/scalable allocator can remove pathological collisions without weakening ownership. |
| ST13 / KEEP | Exact live-record locks and exact journal references: `recovery.rs::load_registration`, `recover`, `record_exists` | Prevents recovery from collecting a writer's live payload; avoids arbitrary path scans and similarly named user-directory deletion. Actual concurrency/cleanup tests reproduce this class. | File handles, metadata and registration work. | **KEEP** facts; **SIMPLIFY** repeated serialization and barriers as ST04/ST05, without replacing exact ownership with a filename pattern. |
| ST14 / KEEP | Before-image and complete commit-decision validation: `durable.rs::{publish_member,decision,recover_intent}` | One interrupted statement must restore complete old or preserve complete new state. A torn marker must not be accepted as a commit. | Old root retained briefly; hard-link backup is cheap for ordinary files, rename gap exists for trees. | **KEEP** recoverable outcome. Ordinary files already use an atomic replace with a hard-linked before-image where supported; do not impose a whole-tree transaction engine or retained undo history. |
| ST15 / KEEP | Durable intent retirement before commit-record/private-tree GC: `durable.rs::recover_intent` | A legal partial deletion can remove the commit record before the old data/intent and make restart restore a partial old tree; a prior actual-helper reproduction establishes this failure. | Extra namespace barrier during successful cleanup. | **KEEP** the ordering, but coalesce its concrete directory flushes where the durability model permits. Do not replace it with unordered recursive delete merely because it occurs after visible success. |
| ST16 / KEEP | Cleanup must not chmod ordinary files: `filesystem.rs::remove_owned_entry/remove_readonly_link`; temporary directory reparenting rights | Old data may have hard-link aliases outside the selected tree. Prior Windows/Unix repros showed cleanup changed the unrelated alias's readonly/execute bits. | Platform-specific unlink and narrow directory-mode recovery code. | **KEEP** inode-vs-name distinction; it protects unrelated user metadata. Retain kernel-operation-first permission admission and fallback only for demonstrated directory rename failure. |
| ST17 / P2 | Restoring scratch-parent mtime: `InvocationRoot::{parent_stamp,cleanup,logical_metadata}`, `set_modified` | Avoids incidental scratch creation appearing in logical source metadata. There is no data-integrity/security requirement to forge a pre-operation parent timestamp after cleanup. | Additional metadata opens/set-times, requirement to set arbitrary timestamps on a parent the user may only have create/delete access to, and a race between timestamp observation and restoration under concurrent namespace changes. | **REMOVE** physical parent-mtime restoration unless a concrete user contract requires it; maintain selected source-tree metadata inside captured manifests. Source-predicted shared-scratch permission issue, not reproduced here. Natural namespace timestamps are already accepted for publication parents. |
| ST18 / P2 | Fixed registry limits: `MAX_RECORDS=16384`, `MAX_RECORD_BYTES=64KiB`; `durable.rs::MAX_MANIFEST=16MiB` | Bounded reads avoid runaway allocations on malformed data, but these are locally generated private control records, not an internet protocol requiring an arbitrary workload cap. | Large abandoned sibling sets can make recovery refuse before processing any record. Byte-vector JSON encoding expands valid long Windows paths and member lists. No supplied workload justification for the exact caps. | **REPLACE** whole-index allocation/count rejection with bounded-memory record iteration and explicit coordinator-before-stage processing; retain structurally bounded per-record decoding tied to actual representable paths. Do not merely increase a magic limit. |
| ST19 / P2 | Rejecting any unknown registry entry/name: `claim_records`, `validate_record_name`; strict `deny_unknown_fields` and platform/version checks | Schema/platform checks prevent interpreting an incompatible intent as cleanup authority. An unrelated metadata file in a user-managed registry is not itself authority to touch anything. | One `.DS_Store`, backup sidecar or foreign file can prevent all startup recovery. Unknown future fields can block downgrade recovery. | **SIMPLIFY** enumerate exact owned record candidates and ignore/report non-record entries without deleting them. Keep explicit unsupported-version outcome for records that really are authority; migration needs known semantics, not blind guessing. |
| ST20 / KEEP/REPLACE | Native Windows case-insensitive-ancestor requirement: `execution/path_policy.rs`, `io/windows::require_case_insensitive_ancestors` | Current resource keys/collision model assume insensitive identity; allowing sensitive directories without changing that model could merge distinct names and lose data. | Entire legitimate case-sensitive Windows directory trees are refused. | **REPLACE** resource/name representation with actual per-directory case policy where demanded. **KEEP** current refusal only as an explicit implementation limitation until the representation supports both; it is not proof the user's filesystem is unsafe. |
| ST21 / KEEP | Explicit exact/container/replace-tree modes and container collision errors: `assignment.rs::assign` | Defines what operation user requested and prevents implicit overwrite of multiple selected children on a later collision. This is language semantics, not an attacker model. | Users cannot expect trailing `/` insertion to overwrite existing files; repeated sync scripts need the correct explicit replacement mode. | **KEEP** coherent modes; improve diagnostics/documentation or add an intentional merge/update operation rather than labelling ordinary overwrite "unsafe". Do not smuggle a new default overwrite rule into performance cleanup. |
| ST22 / KEEP/SIMPLIFY | Fresh optimizer stamps, raw traversal guards, deoptimization: `ordinary_fold.rs::Guard/SourceStamp/Stamp` | Adjacent-write elimination must preserve failures/metadata/source bytes that the original program observes; public filesystem state can change between preparation/acquisition. | Repeated metadata/identity reads and complex special-case machinery can exceed the work saved on short runs. | **KEEP** observational equivalence; **SIMPLIFY** by narrowing specialization to dominant stable shapes or folding within the already-owned snapshot model. Never preserve an expensive optimization merely because its rejection guards pass tests. |
| ST23 / KEEP | Recovery before successful executable submission, including empty/declarations: `execution::recovery_required`, `Registry::open_existing/recover`, retained runtime roots | Existing agreed invariant: restart must not report success while leaving an interrupted statement unresolved; current process cwd is not necessarily the old owner's root. | An unavailable pending endpoint or damaged authoritative record can block unrelated work. | **KEEP** exact invariant and no-state read-only discovery. Improve in-owner repair/reconciliation and actionable diagnostics; do not invent an explicit recovery ritual, scan arbitrary directories, or interpret inaccessible state as absence. |

## What to change first

1. Remove unchanged-existing registry/gate flushes (ST05), recording the same
   actual syscall-count journey before/after. This is the narrowest proven
   redundant work; a new durability mode or database is unnecessary.
2. Redesign statement registration/capture ownership jointly (ST04/ST06), rather
   than adding a fast path that skips crash discovery for "small files". A
   single-consumer captured stage can be adopted; fan-out continues to retain
   immutable artifacts. Preserve process-restart and partial-GC outcomes.
3. Replace blanket user-path link refusal (ST01/ST02) with representable link and
   root-alias semantics. This is a functional gap, not a request to ignore cleanup
   ownership. Unix links and Windows reparse tags need separate explicit behavior.
4. Remove permission-readability admission overreach (ST03) while retaining
   private creation defaults. A group-readable directory is not a writable
   recovery record. Do not broaden this to a speculative security subsystem.
5. Add lock-wait and local persistence phase observations (ST09/ST10) and eliminate
   incidental metadata restoration (ST17) when no selected-source contract needs it.

The directory-wide lock, old/new receipt, exact owner identity and hard-link-aware
cleanup are not equivalent to "39 flushes are necessary". Their desired outcomes
can survive a much smaller execution model. Conversely, deleting guards without
changing the two-entry snapshot model or recovery order would only turn explicit
refusals into silent wrong-file operations.

## Relevant production and research mechanisms

- [GNU cp link semantics](https://www.gnu.org/s/coreutils/manual/html_node/cp-invocation.html)
  separates following a selected link from preserving recursive links. It provides
  a mature behavioral baseline for ST01/ST02 rather than a blanket ban.
- [Linux fsync(2)](https://man7.org/linux/man-pages/man2/fsync.2.html) distinguishes
  file persistence from containing-directory persistence. This explains why
  some namespace barriers exist, not why unchanged control files should be
  flushed repeatedly. Process-kill tests do not simulate loss of device caches.
- [Pillai et al., OSDI 2014](https://www.usenix.org/conference/osdi14/technical-sessions/presentation/pillai)
  shows that application update protocols depend on filesystem persistence
  properties. Apply it by specifying the few required orderings and testing
  those cuts, not by accumulating conservative sync calls around every layer.
- [Pillai et al., FAST 2017](https://www.usenix.org/conference/fast17/technical-sessions/presentation/pillai)
  demonstrates that preserving ordering locally to relevant application streams
  need not impose unrelated ordering globally. This supports reducing parent-wide
  convoying and coalescing equivalent barriers without claiming a drop-in ccfs
  integration for msync.

## User-directed contract replacement (same investigation, later decision)

The user subsequently rejected journal/backup/global-recovery and filesystem-wide
synchronization overhead and explicitly accepted retransmission after failure.
This is a changed product requirement, not a reason to retain the earlier
recoverable-atomicity contract under a less visible name. It supersedes the
contract-dependent recommendations above as follows:

| Earlier IDs | New disposition | Consequence |
| --- | --- | --- |
| ST04/ST05/ST13/ST23 | **REMOVE** persistent local recovery registry, per-stage markers, registration/retirement, startup recovery and recovery gate ownership | An abandoned partial transfer is retried; no future empty/declaration submission contacts destinations or validates stale registries. Normal operation must not create control state whose recovery later blocks unrelated work. |
| ST14/ST15 | **REMOVE** before-images, complete statement commit journals, rollback/compensation state machine, commit-marker and intent-GC persistence barriers | No multi-root old-or-new guarantee. Successfully installed earlier members remain installed if a later member fails; report what completed and let the user retry. A single ordinary file can still get a complete final name through one same-directory temporary plus replacement rename. |
| ST09/ST10 | **REMOVE** parent-wide publication locks and their recovery mutexes | Internal scheduler ordering handles the application's actual dependencies. Independent external writers follow ordinary filesystem last-operation/error behavior; no project-global coordinator is introduced. |
| ST06 | **REPLACE** unconditional snapshot/materialize double-copy with direct owned destination staging for simple single-consumer files | Retain immutable capture only when actual fan-out, logical value reuse or selected-tree assembly requires it. Do not rename a shared live source merely to avoid a copy. |
| ST01/ST02/ST07/ST08/ST20 | **REPLACE/REMOVE** blanket link/reparse and uncertain-path refusal; remove repeated defensive preflights | Selected ancestor aliases are ordinary paths; resolve existing physical roots using OS semantics. Recursive links need a link entry representation so copying them does not imply following cycles. Actual I/O errors carry the affected path. |
| ST03/ST17/ST18/ST19 | **REMOVE** obsolete registry permission/name/count rules and physical scratch-parent timestamp restoration | The registry disappears rather than acquiring a more permissive validator. Ordinary OS permissions determine whether real file operations succeed. |
| ST11/ST12/ST16 | **SIMPLIFY** retain only ordinary operation semantics and own-resource lifetime | Avoid actually copying a file onto its own opened/truncated inode, retain exact source/destination semantics, allocate an unused temporary name, and do not chmod unrelated hard-linked files during cleanup. These are successful-copy correctness, not a persistent security boundary. |
| ST21/ST22 | **KEEP** declared language modes and optimization observational equivalence, not defensive infrastructure | Exact/container/replace success behavior and metadata remain consistent. Optimize using real types/ownership; failed multi-member publication is now retryable partial work, not a trigger for rollback. |

### Concrete implementation handoff

The new storage owner is `storage_rewrite`; the audit author remains an explorer
and changes documentation only. The intended source deletion map is:

- `ordinary.rs::Storage.registry`, initialization/exclusion, `publish_checked`'s
  registry lease/stage promotion/coordinator Batch, `prepare_registered`,
  `retire_stages` and statement-wide compensation;
- `filesystem::{durable,recovery}` implementations, registry fields in
  `SnapshotStore`, and registered materialization APIs;
- `Publication`'s old-root backup, phase-driven rollback/recover and journal
  finalization. Merely replacing `Batch` with the old `Publication::publish`
  would keep the backup machinery and is not the requested redesign;
- `InvocationRoot` physical parent timestamp save/restore and crash-registration
  obligations, while preserving an ordinary temporary directory lifetime for
  live output spools/assets;
- root-owned integration in `execution/live.rs`: `Retiring.registration`,
  `RuntimeSession.stage_registration`, `ensure_invocation` registry creation and
  close-time retirement, plus startup recovery entry points;
- remote-owned integration: local-registry exclusion in
  `execution/remote.rs::execute`, and the separate SSH/remote journal protocol.

`execution::domain::normalize` also checks every ancestor through
`runtime::check_ancestors` and a `symlink_metadata(...).is_dir()` test **before**
its final `canonicalize`. Changing only `filesystem::safe_metadata` therefore
cannot make the demonstrated `alias/source -> target` journey succeed. This
cross-domain dependency was sent to the runtime/root owners.

The acceptance journey is a normal local ordinary-file copy with at most one
file flush and zero registry/backup/journal-directory flushes; exact bytes,
permissions, nanosecond mtime, selected aliases and ordinary successful
container/replace operations remain observable requirements. Failure/retry tests
must assert the new partial-progress behavior, not keep old all-or-nothing tests
as quality gates for a contract the user has removed.
