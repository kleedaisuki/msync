# Statement-local atomic publication

## User-visible contract

The breaking 0.5 model has no transaction declarations, isolation levels,
checkpoints, retained undo histories or named rollback. A finite file statement
owns its input capture, private replacement and short-lived recovery journal.
Earlier completed statements are never reverted by failure of a later statement.
External program effects and streaming transfers do not have this file-publication
atomicity guarantee.

For a multi-target selection or directory replacement, the agreed contract is
**recoverable atomicity**: before admitting new runtime effects, the next start
using the same configured scratch/recovery root settles an interrupted statement
to its entire old state or its entire new state. This does not promise that an
uncooperative observer sees all directory entries change at one instant during
publication or before recovery. Ordinary existing files use an atomic replacement
rename with a retained hard-linked before-image where supported.

The process-crash contract is distinct from sudden power-loss durability. Unix
syncs data and affected directories. Windows syncs file records and uses platform
namespace primitives, but this implementation has no portable directory fsync and
does not claim universal hard-power-loss durability across arbitrary storage.

Successful publication follows normal filesystem namespace semantics: the parent
directory's modification time may change when a name is atomically replaced or
journals are created/removed. We do not restore a fabricated historical parent
timestamp, which could erase evidence of a concurrent independent publication.
The copied file or explicitly selected tree retains its portable metadata;
unrelated siblings retain their identity, bytes, permissions and modification
time. Incidental parent-directory timestamps are not part of the rollback promise
under concurrent namespace activity.

## Access proofs and private cleanup

The kernel namespace operation is a stronger access proof than the source's
owner-write bit. `filesystem::move_new` first attempts native no-replace rename.
Success requires no permission probing or chmod. Only a Unix `EACCES` for a
directory lacking owner-write admits the existing temporary owner-write/restore
protocol; `EEXIST`, `EXDEV` and `EPERM` do not trigger it. Groups and ACLs can
already authorize the original operation, even when the actor cannot chmod an
inode owned by somebody else. The saved before-mode intent remains necessary
for a crash during the narrow fallback's admission window.

Permission normalization during owned-directory garbage collection is
preparatory, not the cleanup operation itself. If chmod is denied, cleanup still
attempts the real directory enumeration, recursive unlink and removal; their
actual errors propagate. This permits a non-owner with sufficient group/ACL
access to finish collection without falsely requiring ownership. Other chmod
errors still fail, and regular-file modes are never changed during cleanup:
their inode may have surviving hard-link aliases outside the journal. None of
these private-publication/cleanup operations chmod the original copied source.

The actual non-root mode-`0570` replacement, mode-`0550` refusal, read-only
directory reparenting, interrupted recovery and partial-GC checks are recorded
in [storage-crash-validation.md](storage-crash-validation.md).

## Implementation ownership

| Component | Responsibility |
| --- | --- |
| `execution::ordinary` | Capture only the current finite statement, validate all selected targets and prepare private replacement snapshots. |
| `filesystem::SnapshotStore` | Own immutable copied inputs and same-volume publication staging. It retains no MVCC versions. |
| `filesystem::durable::Batch` | Persist one complete sibling-set intent, publish members, record the decision and clean obsolete before-images. |
| `filesystem::recovery::Registry` | Persist exact coordinator references under configured scratch before destructive publication. |
| `filesystem::recovery::recover` | Preflight registry records and live-owner locks, then recover exact registered coordinators before runtime effects. |

Missing destination parents are not eagerly created. The ordinary interpreter
assembles the highest missing ancestor and every required descendant inside one
private snapshot, then publishes that ancestor. A crash therefore cannot leave
half-created public scaffolding outside the statement decision.
Assembled manifests canonicalize their validated relative keys to normal path
components. This is a tree-node invariant, not an OS pathname spelling detail:
Rust path equality ignores trailing separators, whereas opening `file/` on Unix
requires a directory. Prefixing an empty snapshot-root key must therefore never
persist an accidentally directory-qualified spelling of a finite file.

Concurrent finite workers share one session-owned outer invocation directory.
Each worker owns a child store; logical captures omit the outer directory by
physical identity, so they cannot accidentally copy a sibling worker's payload.
Replacing an ancestor of active backing or the recovery registry is rejected
with an instruction to put `--scratch` outside the destination tree. Moving live
control state along with a destination is not a safe publication optimization.

Fresh identity and type checks protect ownership, not arbitrary external byte
immutability. A retained file handle identifies an object; it does not establish
that another writer cannot modify its bytes. A foreign target identity blocks
recovery instead of authorizing overwriting unrelated data.

Selectors are acquisition boundaries, not merely publication filters. The local
snapshot owner enumerates direct basenames, applies the anchored Rust regex, and
only then inspects or copies matched children. A matched directory includes its
complete subtree. Unmatched symlinks, FIFOs, unreadable trees and payload bytes do
not enter the capture; selected unsupported objects still fail before publication.
No match retains a valid sparse directory capture and publishes no destination.
The same sparse capture API is used for projected WSL sources, preserving local
alias and private-owner exclusions rather than erasing them behind a bulk import.
Native-ext4 WSL sources shallow-list names for the same Rust regex, then validate,
manifest and copy only the selected roots. A sparse import retains root metadata
and compares the selected manifest before and after acquisition. Device and raw
traversal proofs remain required; unsupported projections go through the local
identity-preserving path rather than an unproven whole-tree import. Non-UTF-8
direct basenames cannot match the Unicode regex and are skipped consistently by
the local, WSL and SSH name-selection boundaries.

The registry is persistent control state rather than retained payload history.
Startup only inspects its own bounded record set; it never scans user trees for
matching names. Recovery requires the same configured scratch root. Deleting or
moving that root while a statement is pending destroys automatic discovery;
diagnostics retain the destination coordinator path for manual intervention.

Replacement staging is indexed too: an empty sibling workspace receives a
physical-identity Stage record and a synced ownership marker before any payload
is copied into it. The coordinator is similarly registered before its intent is
written. Once the complete Coordinator record is durable, temporary Stage
records can retire and publication can start. Recovery handles Coordinators
first, then cleans remaining unpromoted stages by exact identity and marker; a
durable cleanup-admission flag makes interrupted stage cleanup retryable.
An abrupt exit between empty-directory allocation and its first registration
can leave an empty control directory, but never copied payload or selected-target
effects. We do not delete similarly named unproven user directories to conceal
that boundary.

The registry index is not a process-global execution barrier. Its lock protects
only bounded record creation, listing, replacement and retirement. An active
owner retains its own record lock; startup skips such live records and claims
dead records individually. Filesystem parent locks and recovery run after
releasing the index lock, so an idle REPL, open stream or naturally running child
does not monopolize the shared scratch root. Before deleting an unpromoted stage,
recovery rechecks that parent's durable intents under the parent lock; this
settles a coordinator that could have appeared after the index snapshot.

## Failure and decision rules

Before the durable decision, cancellation or IO failure compensates only this
statement. After an admitted decision, cleanup cannot revoke installed new data.
A failed decision durability flush remains an execution error with retained
recovery state, not an unqualified success or a fictitious rollback. Normal
post-decision cleanup failures are committed-with-cleanup warnings.

The detailed protocol and platform bounds are maintained in
[durable-publication.md](durable-publication.md). The crash matrix and actual
acceptance evidence are maintained in
[storage-crash-validation.md](storage-crash-validation.md); authored tests are not
evidence of execution until the corresponding gate is recorded there.

## Runtime overwrite specialization

Within one admitted basic block, a maximal adjacent exact-target overwrite run
can execute only its surviving final file assignment. Recognition is pure and
does not authorize deletion. At runtime the ordinary owner checks every erased
source's raw traversal, current regular/readable/writable class and non-aliasing
with the destination; it also retains the destination's identity, type,
permissions and parent proof. The final source is acquired into an owned snapshot
and all guards are checked again after acquisition and private staging. Invalid
guards deoptimize to the original pending run, never replay an already completed
prefix.

A historical identity or complete permission-mode match on an erased input is
unnecessary: the final owned file overwrites all observable destination bytes and
portable metadata. A fresh readable, writable, non-aliased regular replacement is
sufficient. In contrast, erased raw `step/../file` obligations cannot be dropped
just because the final canonical address is unchanged. The optimizer does not
promise equivalence for every counterfactual IO failure, access time update or
uncooperative external observation of overwritten intermediate states.

This local proof needs no second whole-program preparation or replay certificate:
the same owner captures the surviving value, validates guards and publishes the
statement. The compiler still retains the original instructions and origins for
deoptimization and diagnostics.

## References and design choices

- [SQLite atomic commit](https://www.sqlite.org/atomiccommit.html): order recovery
  information before destructive writes, distinguish the decision from cleanup,
  and synchronize directory metadata as well as payload bytes. We reuse these
  invariants, not SQLite's user transaction or page-store abstraction.
- [Rust File locking](https://doc.rust-lang.org/std/fs/struct.File.html#method.lock):
  use OS-backed locks with owner lifetime rather than PID files that become stale
  after process crashes.
- [All File Systems Are Not Created Equal](https://www.usenix.org/system/files/conference/osdi14/osdi14-paper-pillai.pdf):
  filesystem persistence behavior matters; a successful namespace operation is
  not by itself a portable power-failure guarantee.

Versioned bounded manifests use the existing mature `serde`/`serde_json` stack.
Platform publication and locking mechanisms are preferred over inventing a
generic transaction manager or custom stale-lock recovery protocol.
