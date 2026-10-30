# Static IR, late runtime facts, and statement-local publication

> Historical v0.4.1 work-in-progress design. Its ordinary publication and evidence
> analysis informed v0.5, but its managed transaction engine and whole-module
> binding architecture are superseded by [barrier execution](barrier-execution.md).

## Status and motivation

The `0.4.1` implementation now separates static CLI compilation, statement-local
ordinary publication and managed execution. Final cloud/delivery acceptance is
pending and must be recorded separately; this note describes inspected code and contracts, not an
unperformed release gate. Workflow measurements belong in
[performance-v4.md](performance-v4.md).

The user's representative workload expands one macro twice into four ordinary
assignments: two `config.toml` copies and two `agents/.` tree replacements, one
pair from the local filesystem and one from WSL. About 28 files / 126 KB should
not require retained multi-version transaction history or eagerly initializing
every execution provider. The reported four checkpoints are not four explicit
DSL commits. In the accepted `0.4.0` implementation each ordinary transfer enters
an implicit read-committed Manager transaction, and `Engine::committed` counts
its finish as a checkpoint. Changing that counter alone would not remove its
capture, version bookkeeping, materialization or publication costs.

The repository already has a typed intermediate representation. Replacing it
with another unrelated IR is unnecessary:

| Owner | Implemented `0.4.1` responsibility |
| --- | --- |
| `language` and `compiler/structured.rs` | Parse blocks, capture lexical macro environments and lower typed values without textual substitution. |
| `pipeline::Compilation` / `hir::Program` | Retain expanded instructions, IDs, source origins and a conservative effect graph. `hir::rewrite` also recognizes adjacent-overwrite candidates without authorizing their execution. |
| `structured::State::submit_inner` | Returns static typed IR/graph for `compile`, `plan` and dry-run before endpoint inspection or execution runtime initialization. |
| `preparation::PreparedProgram` | Retains a private pre-normalization semantic witness as well as the public inspected/re-written preview. Verification checks the certificate, not current host truth. |
| `execution::RuntimeSession` | Selects the actual execution owner, retains physical workers and pending suffixes, and owns fresh facts, event gates and publication. |
| `execution::ordinary` / `ordinary_runtime` | Interpret eligible ordinary statements with per-statement assets and publication receipts, without a Manager or Store. `ordinary_fold` guards and certifies complete adjacent overwrite runs at dispatch. |
| `execution::Domain` / `Engine` | Continue to service explicit/natively managed contexts and retained history. Ordinary-only submissions do not eagerly enroll unrelated cwd resources. |
| `transaction::filesystem::Publication` | Already implements private replacement, before-image retention, compensation and explicit finalization. It does not inherently require MVCC or named transaction history. |

Even `preparation::prepare(..., false)` is not a pure static compiler:
`inspect::Observations::resolve` still resolves physical PATH/local values,
inspects local classes and ancestors, and may normalize existing filesystem
paths. Disabling remote probes is not equivalent to removing environmental IO.

## Chosen decomposition

```text
source + explicit lexical context
             |
             v
parse -> typed lexical expansion -> semantic IR + source map
             |
             v
typed composition + conservative effect/dependency graph
             |
             +---------- check / compile / plan return here
             |
             v
interpreter selects ordinary or managed owner
             |
             v
observe only needed endpoints -> guard / retain owned input
             |
             v
specialize affected pending graph fragment
       guard true /                 \ guard false
       specialized execution        original pending IR
             \                       /
              v                     v
       admitted physical IO -> publication -> completed ledger
```

The interpreter refines graph facts for the resource view it actually owns.
Physical specializations have guards and a semantic fallback. This borrows the
JIT/deoptimization discipline; it does not emit native machine code or require
a heavyweight JIT framework. No filesystem observation becomes a source-language
fact merely because it once succeeded. In particular, the CLI does not execute
operation-erasing source-forward/DSE rewrites on unleased host observations.

### Static semantic layer

Keep typed `Input`, `Output`, process and transaction identities. Compile macro
calls structurally and retain definition/argument provenance. Normalize typed
path composition using lexical rules, not `canonicalize` or path existence.
Source-file/cwd context and explicitly captured environment values are inputs;
they are not permission to search PATH, open a file or start WSL during compile.

Safe static transformations include constant/path-expression folding, resolving
lexical bindings, typed macro inlining, eliminating unused pure values, and
reducing redundant dependency edges. A filesystem operand remains an unknown
physical kind unless its kind is inherent in its semantic value. A path can
name a directory or stream; it cannot be treated as an ordinary scalar file just
because it ends in `.toml`.

An overwrite/source-forwarding transformation that needs ordinary-file identity,
absence, permission or alias information is an optional runtime specialization.
It must not erase the original operations from the semantic fallback. Streams,
namespace creation receipts and directory predicates are effects, not dead
temporary values. Fixed-point canonicalization must converge under a finite
rewrite/iteration bound; expensive global or environmental work does not belong
in the canonicalization match loop. These are admissibility rules, not a claim
that a new general canonicalizer or hardware JIT was added in this follow-up.

### Runtime fact ownership

The ownership split can be summarized as:

```text
SemanticPlan { original_ir, static_graph, origins }
RuntimeFacts { selected_epoch, classes, identities, parent_frontiers, input_leases }
ExecutableFragment { pending_original_ids, guards, specialized_ir, graph }
```

Use existing `EndpointKey`, `PlanningContext`, retained input proofs and event
gates where they already express these concepts. Do not build a second unrelated
proof language. The original IR and source map outlive a specialized fragment.
Runtime facts are scoped to an actual resource view/lease and invalidation point,
not cached indefinitely by pathname or mtime.

1. Static commands request no endpoint facts. Ordinary dispatch guards each
   statement; managed dispatch observes its required resource view, then refines
   event scheduling without constructing and discarding an optimized preview.
2. Batch currently needed WSL/SSH metadata by the actual endpoint, without
   starting helpers for unused backends or opening stream data during planning.
3. Bind observed identities to the descriptor/private source or managed epoch
   actually consumed. A later open must revalidate relevant identity/type facts.
4. A publication, native import or foreign-host refresh invalidates overlapping
   facts and dependent specializations. Disjoint facts need not all be thrown
   away. Unknown effects conservatively invalidate their possible write domain.
5. On failed guard, discard only unstarted specialization and resume its pending
   original IDs. Retain completed publications, consuming IO and source-owned
   diagnostics. Never restart a complete script after a socket read or copy has
   already become externally visible.

Metadata freshness alone does not solve hostile concurrent replacement. Keep
the existing explicit concurrency/TOCTOU boundary unless a stronger descriptor
or platform lock really enforces it. Conversely, do not pretend that a replayable
certificate authenticates the current filesystem: it authenticates a derivation
*conditional on its premises*.

### Actual prepared-program compatibility

The public `prepare*` APIs and their conditional rewrites remain available.
`Witness::semantic` privately retains the HIR **before** PATH/physical path
resolution and normalization. This matters for `step/../source`: a normalized
path can lose the requirement that `step` is still a directory at execution.

`RuntimeSession::{execute_prepared,submit_prepared}` first verify the certificate,
then interpret that semantic witness rather than execute its old rewritten
`program`. Thus an earlier source eliminated by an old overwrite proof is not
silently skipped if it has disappeared since preparation. A formerly missing
target that is now an existing directory is rejected under current semantics.
Managed dispatch uses `preparation::observe_runtime` to obtain resolved IR and
current observations without paying for the full preview optimizer, rewrite
certificate construction and replay that it will not use. Actual source/view
pins and the phase ledger remain the positive proofs for further graph refinement.

Preserving original pending operations when no owned fact authorizes their
erasure is the baseline. It does not prohibit runtime optimization once the
needed evidence really exists.

### Implemented guarded adjacent-overwrite specialization

`hir::rewrite::{AdjacentOverwrite,overwrite_run}` performs an inexpensive static
shape check for a maximal run of adjacent exact assignments to the same raw
destination. It retains every original instruction. Ports, selectors, tree and
container modes do not become overwrite candidates. Different destination
assignments in the user's four-copy macro therefore incur no optimizer or
additional helper work merely to discover that they cannot fold.

At dispatch, `execution::ordinary_fold` captures guards for **every** discarded
raw source operand and the target/parent, then invokes the existing optimizer
only on the candidate run. Its certificate must eliminate all but the original
last instruction using `DeadWrite`/`CoalesceCreation`; source-forwarding and
redundant-write certificates are not accepted by this adapter. The survivor
must still name the original last transfer and ID. This bounded recognition is
separate from an unrestricted repeated whole-program optimizer.

The runtime then acquires the surviving file value as an owned immutable
Snapshot. It rechecks the guards after acquisition and again after private
journal construction, immediately before publication. Guards retain the raw
`Location`/operand role and re-resolve its traversal, so an erased `step/..`
component cannot silently stop being a directory while the normalized leaf
still has the same inode. The certificate's type, alias, permission, absence and
parent requirements must hold for the whole discarded prefix. Historical source
identity is stronger than necessary for some discarded operands; the precision
audit below distinguishes it from an indispensable semantic condition.
On invalid or unavailable evidence, the specialization discards its uncommitted
work and resumes the original run, not the completed script prefix.

The final guard is consumed once at the publication handoff. Prepared
Publication receipts then retain their own target-identity checks; obsolete
speculative handles are released rather than keeping a Windows before-image
delete-pending during journal finalization. This is a lifetime handoff, not a
claim that an identity handle freezes arbitrary file bytes or an unmanaged
external writer. Source inspection and targeted tests are evidence for this
implementation; final cross-platform gate results remain a separate acceptance
record.

## What evidence permits an optimization?

The model is not "unknown means serialize everything forever". It is
"state the exact proposition, its resource scope and its lifetime, then use it
for precisely the transformation it justifies". Different evidence kinds are
not one linear hierarchy:

| Evidence | Proposition established | Does not establish |
| --- | --- | --- |
| Typed symbolic HIR / raw `Location` | Lexical value, endpoint spelling, mode, program/port identity, logical order and candidate rewrite shape | Existence, current kind, physical alias separation or OS permissions |
| Instant `stat`/path observation at time t | A result for that lookup at t, subject to the API's semantics | Future validity, a repeatable-read value or absence of other aliases/writers |
| Retained descriptor / `same_file::Handle` | Identity/lifetime of the opened object and the access admitted by that open; comparing a pathname again can detect replacement | Immutable bytes, a complete directory membership snapshot or exclusion of writes through another handle |
| Owned immutable Snapshot / actual `VersionReadProof` lease | The private value consumed by this operation remains that selected version while its owner retains it | That the original pathname still names it, that this is the correct logical read epoch, or that the acquisition itself was free of external effects |
| Runtime guard | Its explicit predicates hold at its check/handoff, and failure has an original pending continuation | A universal lock over all named paths or protection from hostile uncooperative mutation between separate syscalls |
| Verified OS resource-view certificate | An enforced **upper bound** on a process tree's possible accesses in the declared managed resource universe while that view lease lives | A minimal actual read/write set, immutability of a writable private native view, or undo of unmodeled external services |
| Actual access/event log so far | A **lower bound** on effects that have happened by that point | That the process will never touch another path, that all descendants have sealed, or that a read-only looking prefix is a read-only computation |
| Sealed/imported native delta plus retained view identity | Completed managed writes/delta from the actual joined cohort | A complete predicate/read-set history merely from looking at changed files |

For the pinned `same-file` dependency, `Handle::from_path` opens with `read(true)`
on both Unix and Windows (the latter through `winapi-util::from_path_any`). That
is useful read-open evidence, not merely a remembered inode number. Its source
must still be consulted before assigning a stronger proposition to some other
platform descriptor API. In particular, a metadata-only `O_PATH` FIFO lease does
not establish byte-read admission or make a FIFO an immutable file.

Let `E_A` be the complete actual managed effect set of A and `L_A(t)` its observed
prefix. A verified enforced footprint gives `E_A ⊆ U_A`; logging only gives
`L_A(t) ⊆ E_A`. An empty observed intersection with B does **not** imply an empty
future intersection. Conversely, if a genuine upper bound `U_A` is disjoint from
B's relevant read/write/namespace effects, waiting for A to exit is unnecessary.
These statements concern the managed resource universe; they do not erase
network requests, terminal IO or other external-service effects from the full
program contract.

### Resource identity has several roles, not one universal conflict key

An inode/file ID describes an object relationship. A dependency also needs the
role, operation and selected logical version of that relationship:

| Resource role | Meaning / example | Relevant effect distinction |
| --- | --- | --- |
| `PATH_BINDING` | A parent/name resolves to an object or absence in an endpoint/view epoch | Rename publication replaces this binding; resolving it later can observe a different object. |
| `OBJECT_DATA` | Bytes of a live object or a particular owned immutable version | In-place writes affect readers of that live object through every hardlink. A correct immutable-version reader is not reading that mutable pathname again. |
| `DIR_ENTRY_SET` | Directory membership, name-to-object mappings and enumeration/absence/collision predicates | Insertion/removal changes membership; a selector may observe a predicate, not every descendant's bytes. |
| `DIR_METADATA` | Exact directory-node permissions and supported timestamps | Child creation can change parent mtime or depend on permissions without rewriting unrelated child data. |
| `CHANNEL_INSTANCE` | The actually opened FIFO/socket/pipe/port connection and its consuming lifetime | Replacing an endpoint pathname does not retroactively replace an already opened channel. Its bytes, peer closure and non-replayable consume events remain owned. |

These are a model vocabulary, not a claim that five new public Rust variants
have already been introduced. Current `FileResource`, `SnapshotFile`,
`DirectoryMetadata`, namespace proofs and `StreamIdentity` implement parts of
the distinction; future refinements must preserve rather than flatten it.

`transaction::filesystem::Publication` uses rename-based installation. Publishing
new data at path `a` therefore does not perform an in-place write to the old
object. If `a` and `b` were hardlinks, the old binding `b` still refers to its old
object; an already pinned reader of its selected old immutable version likewise
does not become a reader of the new `a`. An undifferentiated "same object ID
means all operations conflict" rule would lose this useful independence.
Conversely, a later unpinned read of `a` must resolve the new binding at its
required logical epoch. Pinning an old value early does not by itself authorize
substituting that value for an after-publication read.

The existing same-file/hardlink copy refusal remains an API policy and source/
destination safety check. This role analysis does **not** silently remove it or
permit a new self-copy. It identifies where an established valid captured read
and a distinct path-binding replacement need not be serialized merely because
their historical object identities overlap. Retained origins, directory
predicates and metadata effects still contribute their own dependencies.

### Finite directory manifests refine different sides differently

An owned source-directory Snapshot proves a finite enumerated **source version**:
its selected files, directory nodes and membership can refine read footprints.
It is stronger than a single `stat` saying "directory", but it is not evidence
that the live source pathname will never change or that an earlier capture is
the correct future read view.

The destination mode then determines a different effect domain:

* `ReplaceTree` removes old descendants as well as installs new ones. Its
  logical write/removal footprint includes **old and new** reachable bindings,
  not only names present in the source manifest. An observer of an old child
  that disappears is a real dependency even when that child has no source
  counterpart. Narrowing this effect requires a valid destination version/
  membership witness or a subtree obligation; source enumeration alone cannot
  supply it. Retaining the directory root's inode alone does not freeze its
  children either.
* Container merge need only validate and install the selected insertion paths,
  their necessary parents, absence/collision predicates and supported namespace
  metadata receipts. Untouched destination siblings are not inherently data
  writes. With the proper per-entry proofs, future merge publication could avoid
  capturing an unrelated target subtree, including an untouched socket/FIFO.
  That does not require or imply a snapshot/undo model for the channel's bytes.

The current ordinary `Container` path still captures the existing target
Snapshot to assemble a merged version; it has **not** implemented that finer
selected-insertion publication. The current `ReplaceTree` path keeps its
whole-target safety/publication rules. The useful next design is finite
read/write/predicate evidence and per-entry receipts, not dropping destination
validation because the source manifest happens to be small. Unselected live
channels may eventually remain untouched; no `SocketSnapshot` undo capability
is claimed today.

### Observables and failure model

The ordinary-file optimizer has an observation-free final-state contract, not
universal physical-trace equivalence. It can remove overwritten regular-file
intermediates within its certified run. It must preserve the surviving bytes,
supported permissions/mtime provenance, logical namespace result and explicit
checkpoint/rollback behavior. A data observer, directory predicate, consuming
stream, externally effectful request or incompatible namespace observation ends
that freedom. Physical external observation of every intermediate rename is not
made equivalent by a managed final-state proof.

The new fold guards additionally preserve the modeled original obligations:
missing/type errors, raw traversal validity, same-file/alias exclusions,
read-open admission, readonly/permission propagation and destination/parent
conditions. A missing discarded source or a readonly intermediate cannot be
ignored merely because the final bytes would match. An uncertainty deoptimizes
to original operations so their located diagnostics and failure behavior run
normally. Metadata with no writer/version lease is not a general substitute
for a successful original read.

This does not claim preservation of every counterfactual hardware EIO, disk-full
timing or failure during a payload read that the final-state model permits the
optimizer to eliminate. It does not promise exact atime, universal ACL/ownership,
inode topology, crash durability or adversarial concurrent-host equivalence.
Such guarantees would need additional mechanisms and a different observable
contract, not more confident wording about a `stat`. Guarded rewrites are
licensed by the explicit modeled observations; excluded OS details are not an
excuse to drop a currently modeled permission or namespace dependency.

### Acquisition, staging, publication and replay are separate effects

| Phase | Safe work / obligation |
| --- | --- |
| Pure semantic analysis | No endpoint effect; repeat freely while preserving source identity and diagnostics. |
| Finite local value acquisition | May be staged privately with a selected version/read-set witness. Mutable source reads need later validation if moved before their logical predecessor. Atime is outside this portable contract. |
| HTTP/SSH requests, socket/FIFO consumption, program launch or port IO | Have their own events; an owned resulting Snapshot does not retroactively make acquisition replay-free. Preserve request/consume order and exactly-once ownership. |
| Invocation-private materialization | Can be discarded before publication if private backing cannot be observed through a participating native view. Resource admission and retained worker ownership still apply. |
| Parent creation / sibling journal preparation | May change logical-parent membership/mtime. It is not automatically a pure private phase just because payload bytes have not been installed. |
| Publication | Changes logical data/namespace and must satisfy event, identity/version, metadata and source-order validation. Compensation can restore managed state, not erase arbitrary external observations. |
| Retry / deoptimization | May replay only uncommitted, replay-safe pending work. Keep completed effects and request/consume receipts; do not restart a script or arbitrary program. |

`SnapshotStore::prepare` currently creates a `.msync-transaction*` sibling in
the logical target parent, and `Ordinary::prepare` can first create required
parents. Therefore neither is automatically invisible speculative staging.
The existing sequential implementation accounts for these effects using parent
receipts. A future parallel stage must delay them until the namespace gate or
explicitly mediate/hide their effects.

## Audit of remaining conservative cuts

"Semantic-required" means the represented dependency itself is required.
"Missing evidence" means the implementation cannot yet prove a narrower case.
"Evidence underused" means existing evidence could support more work with a
small additional ownership/integration rule. These classifications apply to the
particular cut, not to a whole module being either correct or incorrect.

| Current cut and code | Classification | Evidence that can relax it safely |
| --- | --- | --- |
| `execution/live.rs::submit_inner` requires `engine.is_none()` for ordinary execution, even when a retained named history is quiescent and unrelated | Evidence underused / implementation boundary, not a semantic ban | Per-resource overlap against retained origin/read/predicate domains; for overlap, an explicit external-publication version/fence or the managed owner. Quiescence alone does not permit erasing foreign history. |
| `process/view_linux.rs::resource_effects` and `process/view_windows.rs::effects` certify read/write Tree access to every selected managed root; `execution/program.rs` prepares the complete supplied native view | Valid upper bound, but missing finer enforced effect evidence | Read-only grants, smaller verified writable domains, provider-checked capabilities, or later sealed actual read/write/predicate evidence. A frozen baseline Snapshot does not make the child's writable view read-only. Do not narrow grants merely from argv/cwd or access logs so far. |
| `hir/effects.rs::transfer` classifies unknown-kind sources conservatively as Tree/Consume; `preparation/inspect.rs` and `execution/domain.rs::deferred_local` defer producer-invalidated traversal | Semantic requirement until kind/view is selected; missing evidence for a finer current case | Actual epoch-qualified type/namespace witnesses or a pinned immutable file proof. Re-observe at the producer's actual import gate; do not normalize `..` across an ancestor that may change kind. Private replay-safe work can still be staged before this gate under later validation. |
| `hir/effects.rs::{DirectoryMetadata,NamespaceEntry,NamespaceChange}` distinguishes exact directory metadata from data subtrees; `transaction/manager/enrollment_active.rs` checks metadata imports separately | Already relaxed using evidence; the retained exact-node/namespace dependency is semantic-required | Preserve this distinction when adding APIs. A new ancestor's metadata does not overwrite its descendants. Further independence requires actual namespace-epoch/parent identity and complete active-owner inventory, not dropping directory predicates. |
| `hir/effects.rs::FileResource::comparable` treats native/WSL and unresolved backends as possible aliases | Missing physical evidence, not an inherent cross-backend conflict | Provider/mount/device/identity evidence tied to actual opened or captured resources. `execution/wsl_source.rs` already uses positive native-ext4 single-device source and NTFS/ReFS target guards to select a useful bulk path. Different textual backend labels alone are insufficient. |
| `execution/ordinary_runtime.rs::ordinary_submission` runs ordinary statements in source order, while managed `frontier.rs` already overlaps admitted independent work | Evidence underused / implementation choice | Separate replay-safe private acquisition from namespace preparation/publication, retain logical read/version/predicate witnesses, and validate at the existing publication frontier. This needs bounded jobs/memory admission and exact future-cancellation ownership, not simply spawning all statements. |
| `ordinary_fold.rs` requires complete current discarded-operand admissibility before erasure | Semantic-required for that transformation under the current observable contract | Supply the particular absent witness or an alternate lower-cost transformation. An invalid stale certificate cannot be made true by logging that the final bytes happen to match. |
| The initially inspected `ordinary_fold::Stamp::valid` also required every discarded source's historical inode and complete permission bits to remain identical | Evidence underused / overstrong predicate for the certified sole-final-file case | Validate the current raw operand as a regular, read-openable, non-readonly intermediate, disjoint from the current target. A final file-valued Snapshot replaces its permissions/mtime as well as bytes; stable discarded identity and irrelevant full-mode equality are not inherently required. Keep target/parent and creation-namespace guards. |

The goal is to move work earlier where evidence permits it, not to remove the
publication obligations that make its result valid. A broad footprint can be
overconservative and a guard can simultaneously be insufficient for a different
claim; both need to be stated at the right scope.

### Concrete precision improvement: validate a predicate, not an old source stamp

Consider `first -> target; last -> target` while the final value is being
acquired. Atomic rotation replaces `first` with a different regular readable
file, and its writable mode changes from 0644 to 0600. Insisting on the original
inode or full mode makes this candidate deoptimize even though neither the
discarded bytes nor that intermediate mode is observable after the final
file-valued assignment.

The justification is specific to the certificate, not a blanket relaxation:

* The sole survivor is explicitly the original final **file-valued** transfer,
  already pinned as an immutable Snapshot. In `assignment.rs::Exact`, this last
  value replaces the whole file metadata, unlike a literal that inherits target
  permissions. Only `DeadWrite`/`CoalesceCreation` may have removed predecessors.
* Every discarded raw source still resolves through valid non-link traversal,
  is currently regular and read-openable, yields a non-readonly intermediate,
  and does not alias the current target. Here "non-readonly" describes the
  permissions propagated to the intermediate destination, not a requirement to
  write to the original source itself.
* Interior literals retain the preceding destination permissions, but do not
  make those exact permission bits a final observable when the sole surviving
  file later overwrites them. Their intermediate overwrite must remain admitted.
* Target/parent identity, mode/kind, absence and namespace-creation predicates
  remain guarded. `CoalesceCreation` retains the creation contribution; rotating
  a discarded source is not permission to erase that parent receipt.

Thus fresh admissibility can license regular-to-regular source rotation without
requiring historical identity. Missing/directory/readonly sources, a new hardlink
to the target, failed raw traversal or final-literal metadata inheritance are
counterexamples to broader relaxations and must deoptimize.

The source implementation now distinguishes `ordinary_fold::SourceStamp` from
the strict target/parent `Stamp`. Source admissibility reruns the retained raw
operand, requires its expected resolution, rejects nonregular/readonly entries
before a read-open, and derives current type, propagated permissions and alias
identity from that same readable file handle. Historical source handles remain
owned until the final guard-to-publication handoff, but historical source inode
and full-mode equality no longer reject an otherwise admissible rotation.
The certificate explicitly requires the unique surviving original last transfer
to be `Input::File`; this rule cannot silently expand to literal metadata
inheritance if later optimizer rules change.

`ordinary_fold::tests::a_readable_writable_source_rotation_keeps_the_final_file_specialization`
atomically rotates the discarded file after final-source acquisition (0644 to
0600 on Unix), verifies one publication, final Snapshot bytes/mtime/permissions,
the preserved replacement source and empty journals/storage.
`source_rotation_to_a_target_alias_or_readonly_file_still_deoptimizes` checks both
essential negative predicates. Existing missing-source, non-link raw traversal
and invalidated interior-source tests remain required; target and parent guards
are unchanged. These are implementation and executable controls, not a claim
that an unobserved gate passed. This refinement does not relax the retained
final Snapshot or permit erasing observable intermediate consumers.

### Hypothesis: stage finite B while opaque A runs

This is an implementation proposal, **not currently delivered functionality**:

```text
A: launch under enforced view U_A -------- seal / import / logical event
B:       acquire/build private candidate ---------- validate ---------- publish
                                      invalid? -> discard -> original pending B
```

If `U_A` is already disjoint, existing dependency proof can admit B normally.
The more interesting case is potential overlap but replay-safe finite B:

1. Retain B's original instruction/continuation, source bindings and logical
   event position. Acquire/build only in invocation-private backing that A
   cannot observe; do not create B's logical parents or sibling journals yet.
2. Retain the actual source Snapshot/read version plus namespace, absence and
   relevant metadata predicates used to choose its value/target. A source fd
   alone does not prove that bytes selected before A equal the value B should
   read after A. Literal B may avoid a data read-set, but not target predicates.
3. Bound staging by the same jobs/buffer budget and retain its physical worker
   across cancellation. Speculative failure is not an early user-visible error:
   A may legitimately produce the source or fail first in logical order.
4. At A's required seal/import or other publication gate, validate B against
   the selected logical version history and namespace state. Metadata equality
   alone is insufficient when A could have changed bytes without a distinct
   tracked version or could still modify the source through its view.
   The gate is the complete logical dependency cut, not merely child exit: an
   explicit later rollback/origin dependency may still constrain publication.
5. Valid B can prepare its final sibling journal and publish. Invalid B discards
   its candidate and executes only original pending B in the correct view;
   already completed A or earlier B-prefix effects are not replayed.

There is no analogous license to repeat a GET, socket/FIFO read, arbitrary native
program or external-service call merely because its observed access prefix had
no conflict. Such operations can still overlap if their actual enforced
resource/port semantics permit it, but replay requires a separate contract or
retained exactly-once result/event, not the finite-file retry rule.

This proposal resembles optimistic concurrency validation, not changing the
meaning of isolation levels. [Adya's version/dependency formulation](https://dsf.berkeley.edu/cs286/papers/adya-phd1999.pdf)
separates visibility from ordering constraints; [PostgreSQL's SSI contract](https://www.postgresql.org/docs/18/transaction-iso.html#XACT-SERIALIZABLE)
supplies a production example of permitting work while validating dependencies
rather than holding one global execution lock. Neither justifies copying SQL
retry behavior onto non-replayable external IO. Moreover, "some serial order"
is insufficient when msync's IR has already required a particular source-order
edge or named-undo boundary. Validation must use that intended logical view,
not merely find any convenient serial history after the fact.

## Ordinary assignment is not a retained transaction

There are two distinct publication requirements:

| Owner | Necessary state | Unnecessary ordinary-copy state |
| --- | --- | --- |
| Ordinary assignment | Its selected source, target mode, private stage, target identity/before-image until settlement, cancellation and publication receipt | Named origin, repeatable-read snapshot, MVCC transaction record or full unrelated directory capture |
| Explicit transaction / native managed program cohort | Isolation state, reads/predicates, immutable versions, checkpoints, retained origin, native-view/process ownership | None of these may be weakened merely to improve the ordinary case |

Use one semantic frontend and explicit physical ownership contracts, with an
ordinary statement owner or a managed transaction owner. An internal operation
identity is not a request for MVCC. Do not change the language into one implicit
whole-script transaction, and do not abandon statement failure atomicity by
routing new directory replacement semantics through the old per-leaf copier.

The ordinary owner reuses the existing publication/recovery machinery below the Manager:

```text
resolve actual source/target
  -> validate exact/container/replacement relation and selected-tree conflicts
  -> build one private replacement for this assignment
  -> revalidate target / admit publication
  -> publish or compensate
  -> finalize ordinary before-image immediately
```

For an ordinary exact file/tree replacement, the owner retains the old destination via a
rename journal, rather than first cloning its complete bytes into MVCC and later
rebuilding a parent envelope. Creating missing parents requires a small owned
namespace receipt so failure can remove only newly created directories and
restore required metadata, not arbitrary preexisting siblings.

The implementation uses `SnapshotStore::capture` followed by
`SnapshotStore::prepare`: it still copies the selected source into an immutable
artifact and then a publishable stage. It does not claim one physical payload
copy. A possible later direct source-to-owned-stage path could remove this
remaining materialization, but must preserve the same metadata,
exclusive creation, cancellation, relation and compensation checks. Do not fake
this by constructing an immutable `Artifact` that points at mutable user files.

Containers/selectors need the same distinction. Validate all selected conflicts
before effects; retain a statement-local undo set for installed children and
created directories, rather than weakening a directory assignment into silent
partial success. Tree replacement must not merge leftover old children. Sources
containing the invocation scratch tree must keep the identity-checked exclusion
policy; unrelated preexisting `.temp` content is still user data. Shared
`assignment.rs` and `transfer::select_snapshot` keep replacement/metadata,
selection and source/destination relation rules common to both owners.

On every completed ordinary statement, `Ordinary::close` releases its source
artifacts and invocation before dispatching its sibling. Successful statements
do not accumulate an undo history until REPL exit. Failures retain only the
resources actually required to diagnose/recover an unsettled journal.

The ordinary worker executes statements in logical order. It is one joined
blocking worker, not a second unbounded scheduler, and does not promise that
`--jobs N` will produce N simultaneous ordinary copies. Its physical ownership
remains in `RuntimeSession::ordinary_task` after a waiting future is dropped;
cancel/stop/shutdown joins that real worker before considering cleanup complete.

### Interaction with retained managed state

Manager initialization can be lazy, but its presence is not simply a Boolean
performance flag. Two interactions require an actual policy:

* A direct ordinary write after named transaction A is a foreign publication.
  `rollback A` must not erase it unnoticed. Either record a foreign-publication
  version/fence for overlapping managed keys, or route only that overlapping
  operation through the existing Manager. Disjoint ordinary work need not pay
  for the entire retained domain.
* A read that depends on unpublished native output must consume its selected
  managed view, not the current host pathname. Existing event gates, input
  pins and cohort ownership remain authoritative.

The implemented selection is intentionally explicit: a wholly ordinary
file/literal submission enters this path only while no Engine/retained history
or background work exists. Mixed explicit/native contexts and later submissions
with managed history keep the existing owner and its foreign-publication fences.
This is not a claim that every top-level statement now bypasses MVCC regardless
of resource/view interactions. Ordinary publication itself reports no checkpoint;
named checkpoints keep their actual meaning.

### WSL source specialization and suffix fallback

`execution::wsl_source` specializes finite WSL acquisition only when its guards
establish a supported ordinary source/tree on one native ext4 device and an actual NTFS/ReFS
Windows destination. Two guest commands preflight source identity/type/manifest
and acquire it in bulk into a fresh invocation-owned path, checking the receipt
and names again afterward. There is no per-leaf publication into logical user
destinations in that guest copy. Windows names, aliases, source relations and
the selected destination mode still require their normal validation.

FIFO/physical endpoints and unsupported mount/provider cases return an explicit
fallback instead of pretending to be immutable filesystem data. The ordinary
runner drains only its completed prefix from the retained IR and hands the
unexecuted suffix to the managed/flow interpreter. Prior local copies and
consuming effects are not replayed. This is a real adapter deoptimization, not
a retry from source line one.

Killing the Windows WSL launcher does not establish that its guest `cp` stopped.
This finite helper drains normally; a pending cancellation then rejects further
capture/publication while its invocation is still owned. The retained ordinary
JoinHandle makes stop/shutdown wait for the same physical worker. That may delay
cancellation of a blocked/uninterruptible call, but never falsely reports joined
guest IO or removes a stage still being written.

## Observability and acceptance

The optional library `execution::progress::Progress` is a latest-event slot
(`Arc`/`Mutex`/`Condvar`), not an IO thread. Actual execution updates inspection,
acquisition, staging, publication and wait phases with owned endpoint/instruction
context. The CLI starts a reporter only for terminal stderr and not `--trace -`.
It waits 200 ms, refreshes at most every 250 ms, shows genuine phase/path/elapsed
time without invented percentages, and joins/clears before the next prompt.
Pipes emit no transient observer noise; formatter and trace payloads stay intact.
Trace spans preserve the statement-journal versus managed-checkpoint policy.

The four-assignment ordinary macro reports four assignments and zero checkpoints,
not four renamed implicit transactions. The CLI completion line omits the
checkpoint/rollback fields when both are zero and includes elapsed milliseconds.
This accompanies removal of underlying Manager/Store initialization on that path,
not only a label change.

Focused executable controls (the final delivery record owns actual pass counts):

1. Compile the exact two-call macro with WSL absent or a sentinel helper: no
   helper launch, metadata request, scratch tree, Tokio runtime or Manager.
2. Run it against owned 28-file local/WSL fixtures, both missing and existing
   destinations, with byte/metadata/removed-child checks and complete CLI timing.
   Include a large unrelated cwd and count backend launches/captured bytes.
3. Compile first; replace/remove file endpoints before execution. Runtime chooses
   the current valid adapter or gives a located error without stale specialization.
4. Force a guard failure before a candidate optimized fragment starts. Original
   pending operations execute exactly once; already completed socket reads,
   checkpoints and file publications are never replayed.
5. Inject cancellation/failure after backup and after install. Old exact/tree
   destination state and parent namespace are restored; no private stage leaks.
6. Repeat named four-isolation/checkpoint/origin tests. Also put a direct foreign
   ordinary write between A's checkpoint and later `rollback A` and require the
   existing foreign-history policy, not silent erasure.
7. Run ordinary assignments alongside a held background stream/native owner:
   disjoint work remains admitted, conflicting work retains correct gates, and
   actual buffer/job limits apply to the selected executor.

The user's directory/WSL workload is a release acceptance workload, not a
theoretical edge case. A fast 64-KiB sparse-file microbenchmark does not establish
that directory publication, remote startup or `compile` latency is acceptable.

The current regression sources are `tests/cli_structured_v4.rs` (pure compile,
stable IR and REPL binding boundaries), `tests/ordinary_execution_v4.rs`
(four-assignment macro, assignment metadata/atomicity and managed compatibility),
`tests/runtime_guards_v4.rs` (stale missing/type/path/erasure facts),
`execution/ordinary_tests.rs` (deterministic publication cancellation/compensation
and per-statement retirement) and `execution/ordinary_runtime_tests.rs` (dropped
waiter retains worker ownership). Real terminal waiting/progress is exercised
in `tests/repl_terminal.rs`. These are targeted workflows, not a reason to run
unrelated complete suites after each edit.

### REPL lexical commit boundary

A frontend error discards the candidate lexical environment. Once static
compilation succeeds, its declarations commit before endpoint IO begins.
A later IO/backend failure does not undo a binding or macro definition, just as
resource rollback does not rewind lexical scope. The two-input regression in
`cli_structured_v4` separately checks both cases; a later input can use a binding
declared by a statically valid submission whose copy failed. Plain compilation
therefore has one coherent success boundary rather than depending on whether
the host happened to contain its source file.

## Primary references and applicable lessons

* [MLIR side effects and speculation](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
  separates data-flow from mutable-resource effects and conditional speculation.
  Apply that separation to endpoint IO and namespace effects rather than calling
  every assignment pure because its textual source is unchanged. Its hierarchy
  is not a substitute for msync's path/alias analysis.
* [MLIR canonicalization](https://mlir.llvm.org/docs/Canonicalization/) puts
  inexpensive convergent rewrites before later analyses; correctness cannot
  depend on a canonicalizer having run. That matches retaining original semantic
  IR as a correct fallback when no environmental optimization is available.
* [V8's production deoptimization design](https://chromium.googlesource.com/v8/v8/+/main/docs/runtime/deoptimization.md)
  and [its WebAssembly deoptimization account](https://v8.dev/blog/wasm-speculative-optimizations)
  require state reconstruction and continuation after assumptions fail. For IO,
  the analogous state is pending operation IDs plus the completed-effect ledger;
  falling back to the beginning would duplicate effects.
* [Flueckiger et al., POPL 2018, Correctness of Speculative Optimizations with Dynamic Deoptimization](https://www.lix.polytechnique.fr/Labo/Gabriel.Scherer/research/sourir/sourir.pdf)
  makes assumptions and deoptimization targets explicit in IR. The useful
  transfer is an explicit conditional specialization with a semantic fallback,
  not an assertion that a filesystem stat makes later IO immutable.
* [Barriere et al., POPL 2021, Formally Verified Speculation and Deoptimization in a JIT Compiler](https://people.irisa.fr/Aurele.Barriere/papers/corejit.pdf)
  studies explicit assumptions/synchronization points with proved optimizations.
  msync should make its smaller proof obligation executable: guarded fragments
  must preserve pending-state correspondence and never replay completed IO.

No reference makes full MVCC mandatory for an ordinary copy. The design choice
here is to keep transactional history only where the DSL actually needs it,
while sharing the lower-level stage/publication machinery and exact diagnostics.
