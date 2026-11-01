# Runtime safety cost/value audit

## Decision context and scope

The 2026-11-01 investigation began with a native Windows v0.6.5 upload of 1,795
bytes taking 35,399.5 ms, then expanded at the user's request to the project's
defensive design. This note covers process lifetime, byte flows, native I/O,
admission, planner hazards and runtime control. Storage/publication, object
formats and tooling have separate domain audits.

The working model is a single-user synchronization tool executing user-selected
paths, endpoints and programs with that user's rights. It is not a sandbox for
hostile child programs or a multi-tenant authority. A malformed frame, an ordinary
disconnect, an interrupted copy, aliases and a concurrently replaced FIFO are
concrete correctness/liveness cases; they do not require inventing an attacker.
Conversely, saying "cannot prove safe" does not justify rejecting a normal OS
feature when the implementation can represent it.

Recommendations below are evaluations, not claims that the proposed changes have
already shipped. Source references use symbols because the concurrent SSH repair
changes line numbers. No production code was changed by this audit.

## Highest-value changes

1. **Replace the Windows case-insensitive-directory requirement.** A supported
   case-sensitive NTFS/WSL directory is not an invalid user input. Teach resource
   identity about its actual case policy instead of refusing all work beneath it.
2. **Replace one-to-one FIFO proof pairing with participant groups.** The actual
   CLI rejects a normal two-writers/one-reader FIFO topology before opening it.
   Preserve co-admission/liveness, but remove the artificial equality condition.
3. **Narrow control barriers by typed effects.** Every runtime control currently
   drains every finite worker, including non-folded scalar-only controls. An
   observation barrier should depend on what is observed, not the `Control` enum.
4. **Queue busy independent background work.** Interactive flow admission reports
   capacity occupancy as an error and asks the user to `/wait` or tune settings.
   Busy and impossible are already distinct states; the UI can retain pending
   work without blocking its reader.
5. **Make spooling/capture proportional to the required lifetime.** Immediate
   draining and stable asynchronous source bytes are valuable, but unconditional
   disk-backed process outputs and file captures are not free or intrinsic
   security requirements.

## Mechanism-by-mechanism evaluation

Priority denotes delivery value, not hypothetical exploit severity. `KEEP` means
the contract has a concrete benefit; it does not prohibit a simpler mechanism.

| ID / priority | Mechanism and source | Concrete benefit or failure model | Normal-user cost / restriction | Disposition and simpler direction |
| --- | --- | --- | --- | --- |
| R01 / P1 | `execution::domain::normalize` → `path_policy::validate_case_policy` → `io::windows::require_case_insensitive_ancestors` | Prevents the current ordinal-insensitive publication/resource model from merging names an OS directory treats as distinct. | Opens and queries every existing directory ancestor. Any case-sensitive ancestor, unsupported `FileCaseSensitiveInfo` query, or query access failure rejects the whole operation, including an unambiguous single-file copy. | **REPLACE.** Carry actual per-directory case policy and native identities in runtime resource keys. Unsupported metadata should disable a rewrite or retain a conservative dependency, not automatically forbid native file I/O. Keep collision detection for an actual destination that cannot represent both names. |
| R02 / P1 | `execution::fifo::FifoState::inspect`, equal writer/reader count check and ordered `zip` | Ensures each rendezvous proof has a counterpart, so the scheduler does not admit one blocking FIFO operation and wait for it before admitting its peer. | Rejects N:M physical FIFO topologies even though POSIX permits them. The rejection is reproduced below. Ordered pairing is the proof representation, not an OS requirement. | **REPLACE.** A generation-scoped FIFO participant group can carry N writers and M readers. Reserve its actual co-start demand and preserve external dependency gates; do not merely delete the count check from the existing paired graph. |
| R03 / P1 | `execution::live::interpret_control_block` drains acquisition and all finite workers before dispatch | A later `exists`, deletion, selector or effectful condition must observe earlier writes rather than speculative state. | All non-folded controls incur a global finite-work barrier, including slot-only bool/string updates and branch decisions with no filesystem observation. Constant-folded controls are already absent and do not pay this cost. | **SIMPLIFY.** Derive an observation/effect summary from typed control expressions. Pure slot operations need no filesystem join; reads join overlapping writes; unknown native effects retain the real barrier. |
| R04 / P1 | `execution::live::flow_lease`, `Availability::Busy` in interactive mode | Avoids waiting on a process-connected flow whose input might only be supplied by a later user command. | A merely busy independent background-flow lane returns "background flow capacity is occupied" and asks the user to drain or retune it. This makes an internal queueing decision a user error. | **REPLACE.** Keep pending independent admission as runtime-owned work, leave the prompt responsive, and wake on capacity. Do not implicitly await connected external programs. `Impossible` demand remains a distinct capacity/design issue. |
| R05 / P2 | `process::Process::spawn_prepared`, unconditional stdout/stderr `Spool`s and `drain_inner` | Draining both pipes from launch prevents a child blocking on an unread output pipe when only its exit code is later requested; retained spools support later REPL consumers. | Every byte goes through scratch disk even for an immediately attached consuming flow or an output that will never be observed. Scratch availability becomes a prerequisite for launching programs; disk use is unlimited by default. `flush()` per chunk is a Tokio cross-handle visibility boundary, **not** an fsync. | **SIMPLIFY / REPLACE selectively.** Preserve independent drains. Express output-retention intent in the ownership model: retain when later access is possible, use a bounded direct path for an already-established sole consumer, and expose a deliberate discard mode rather than silently discarding output. Do not add an arbitrary default output cap. |
| R06 / P2 | `execution::flow::capture_source` / `capture_file`, `SnapshotStore::capture_in`, private basename hard link | A released file-to-stream assignment must keep its selected input bytes even if a later command overwrites or deletes the source. An open descriptor alone does not isolate in-place writes. | Captures a complete native regular input before background admission, potentially copying its bytes to scratch before the first network byte; a normal file-to-TCP transfer can therefore require extra disk space and I/O. Platform cloning may reduce this cost, so it must be measured by filesystem, not asserted universally. | **SIMPLIFY with lifetime evidence.** Preserve snapshot semantics for released mutable sources. Prefer proven reflink/snapshot capabilities; retain an open source directly only where the admitted lifetime excludes an overwrite. Do not trade away byte stability merely to remove a copy. |
| R07 / P2 | `streams::REMOTE_PYTHON::safe_path`, rejects symlinks in every ancestor | Prevents an endpoint from being redirected between lexical inspection and open; avoids publishing through a different tree than the scheduler describes. | Rejects ordinary user-owned symlinked directories and symlinked FIFOs/sockets as well as genuinely unexpected replacements. No hostile endpoint or privilege boundary is established by the user-selected SSH connection. | **REPLACE.** Give selected link-following behavior an explicit endpoint meaning, resolve to a retained native target/parent where supported, and compare actual identity. Keep no-follow semantics for runtime-owned staging/cleanup. A blanket ancestor-link ban is not the only correct representation. |
| R08 / P2 | `streams::FifoIdentity`, metadata-only `O_PATH` generation handle, require pin in `FifoState::observe` | A plan for FIFO generation A must not accidentally connect to replacement generation B. Holding the inode prevents recycled inode numbers from satisfying a stale `(dev, ino)` proof. | One retained descriptor per observed FIFO; all observed local FIFOs require a pin before the code even determines whether an internal co-start group exists. Unix platforms outside Linux/Android/FreeBSD currently produce no lease and are rejected by this path. | **KEEP for actual shared-generation proofs; SIMPLIFY scope.** Pin the channels whose multi-operation co-admission relies on a shared generation. A standalone externally rendezvousing endpoint can bind at actual open without pretending a cross-operation generation proof exists. Add a platform adapter rather than calling supported FIFO usage unsafe. |
| R09 / P2 | `execution::live::reserve_rendezvous` and `scheduler::Admission::feasible` | Starting only a subset of a mutually dependent pipe cohort under a hard slot limit can deadlock before its peers get slots. External predecessors must finish before a cohort reserves their needed capacity. | The whole computed FIFO co-start group must fit stream and buffer limits; the user must tune capacity if it does not. A group larger than the actual minimal blocking cycle may be unnecessarily refused. | **KEEP the liveness obligation; SIMPLIFY group granularity.** Reserve the minimal rendezvous component justified by the endpoint protocol, not arbitrary graph connectivity. Test whether endpoint-open and active-payload slots can be separate, without hiding real buffers from accounting. |
| R10 / P2 | `execution::scheduler::Admission`, six buffer units per program, two per flow, one per finite copy | Enforces a stated aggregate **internal payload buffer** budget and independent program/copy/stream lanes, avoiding the old child-waits-for-copy lane deadlock. | A six-unit program reservation is conservative and static; it can refuse additional processes although only two output drain buffers are active. It is not an RSS or child-memory bound. | **KEEP separate lanes and honest accounting; SIMPLIFY actual reservations.** Lease buffers at the owners that allocate them and transfer the lease on ownership changes. Keep checked feasibility before a genuinely indivisible co-start operation. |
| R11 / P2 | `plan::Resource::overlaps`, Windows projections, identity hints and unresolved ENV/PATH endpoint `*` | Aliased native/WSL/hard-linked paths can otherwise reorder a write/read or remove a required assignment. A collision in a hash hint adds dependencies rather than deleting work. | Unknown endpoints and alias relations can serialize unrelated operations or block copy forwarding/dead-store elimination; native path normalization can repeat metadata work. | **KEEP correctness direction; SIMPLIFY uncertainty.** Resolve actual selected endpoint facts once at admission, reuse those observations within their valid block, and retire uncertainty rather than turning it into permanent global overlap. Never use an overlap hint as positive identity for deleting writes. |
| R12 / P2 | `preparation::resolve_instruction`, `prepare_block`, one-block-only observation and structural certificates | A program launch/control may create or remove inputs; old metadata cannot authorize a later effect after an arbitrary external action. A changed branch/object cannot reuse an unrelated preparation certificate. | Reclassification and proof construction recur at selected execution boundaries; coarse invalidation throws away facts unrelated to the effect. | **KEEP boundary ownership; SIMPLIFY invalidation.** Version observations by affected resource/effect, retain immutable typed plans, and avoid duplicate probes within one admission. Do not cache filesystem truth across an operation that can change it. |
| R13 / P2 | `streams::finish` / `RemoteProcess::wait_commit`, 30-second commit-ack deadline and acknowledgement flag | Network loss after remote rename is not proof of rollback. An already received COMMITTED acknowledgement remains authoritative even if SSH later exits unsuccessfully or a pipe remains open. | Without an acknowledgement, the user gets an unknown-outcome error and must inspect the destination. The fixed deadline is a liveness budget, not a normal 30-second sleep; a slow valid commit can exceed it. | **KEEP "no invented rollback"; REPLACE manual reconciliation.** Reuse a queryable publication receipt/owner for file outputs, so a reconnect resolves the actual outcome. A deadline alone does not complete the user journey. Do not add a second transaction service. |
| R14 / P2 | `process::Process` never kills a child on handle drop; runtime graceful cancellation drains to natural exit | User-launched programs may be performing non-idempotent external work. Interpreter cancellation must not silently become SIGKILL or imply their effects were rolled back. | A child that never exits, or a descendant keeping an output pipe open, can keep graceful shutdown waiting indefinitely. Silent waiting is poor UX even when preserving the contract is intentional. | **KEEP the explicit semantic distinction; SIMPLIFY UX.** Show which child/descendant/pipe retains shutdown, retain ownership without blocking the REPL unnecessarily, and expose explicit force-stop. Do not silently change normal cancellation into destruction. |
| R15 / P2 | `process::tree::{configure,Tree}` native groups/jobs for explicit force-stop | Force-stopping only the leader leaves descendants holding pipes and resources; native group/job termination provides ordinary process-tree lifecycle handling. | Per-program OS group/job; Windows optional job attachment may be unavailable. Unix descendants can intentionally leave the group. | **KEEP.** This code explicitly does not claim a hostile-child sandbox. Optional job failure must not kill a child that has already started external effects; improve diagnostics for weaker force-stop coverage rather than pretending security containment. |
| R16 / P2 | `execution::control_fs::delete` refuses directories/special nodes and PATH deletion | Prevents an ordinary file-delete operation from silently becoming recursive tree removal; PATH is a search namespace, not a writable directory object. The final symlink itself can be unlinked. | There is no recursive directory-delete behavior through this operation; deleting a socket/FIFO is also refused although native unlink can do it. | **KEEP no implicit recursive deletion; REPLACE accidental gaps if desired.** Give directory removal and physical-node unlink explicit typed operations/semantics. These are product capabilities, not reasons to invent a security boundary. |
| R17 / P2 | `execution::flow` and preparation reject directory-to-byte-stream assignments, selector-on-physical-stream, unnamed-port-to-container | A directory is not a byte stream without an archive/framing format, and a port has no truthful basename to choose for container output. | Users cannot use these forms without explicitly producing bytes or a destination name. | **KEEP semantic distinction.** Add an explicit archive/serialization adaptor if useful; do not silently guess tar format or filename. The failure is a missing representation, not an adversary. |
| R18 / P3 | `execution::program::Program::take_reader` / `take_stdin`, one consuming stream reader/writer | Two consumers of one program pipe would compete for bytes rather than each receive a copy; a second producer changes framing/order. Scalars such as exit code are intentionally replayable. | Reusing stdout requires explicit tee/materialization rather than a second implicit read. | **KEEP, express through ownership/types.** Use an explicit broadcast/tee node with its own buffering/backpressure if requested. Do not add repetitive dynamic consumer checks where construction already proves uniqueness. |
| R19 / P3 | `ResourceLimits::validate_keyed`, positive buffers and actual semaphore/allocation maximum | Zero-sized progress buffers cannot transfer bytes; Tokio semaphore construction has a real maximum and allocations must fit `isize`. | Rejects impossible numeric configurations before starting work; does not cap ordinary file sizes. | **KEEP actual platform constraints.** These checks are arithmetic/progress validity, not safety policy. Avoid unrelated lower business limits. |
| R20 / P3 | `io::linux` bounded kernel chunks and same-handle fallback after unsupported `copy_file_range`/`sendfile`; `io::windows` CopyFileEx/TransmitFile completion ownership | Prevents truncating a same-inode source; reports the accepted prefix and resumes without reconnect/reread. An in-flight kernel operation can still access borrowed buffers/OVERLAPPED records after cancellation is requested. | Bounded worker operations and descriptor clones; cancellation may wait for a kernel completion rather than return immediately. | **KEEP.** These are concrete data-correctness and memory-lifetime obligations. Unsupported acceleration already falls back instead of rejecting the user. Do not replace them with arbitrary waits or release live kernel memory early. |
| R21 / P3 | `io::windows::wait_operation` aborts on failure of its private completion-event invariant | A failed wait does not establish that an overlapped I/O has stopped accessing its record. Unwinding would release live kernel memory. | The process aborts if a supposedly owned, live private ALL_ACCESS event cannot be waited on after cancellation. This is an exceptional internal invariant violation, not a normal unsupported-filesystem path. | **KEEP narrowly**, or replace with an ownership design that can safely retain the live record until completion. Returning an ordinary error from the current stack-owned representation is not a correct simplification. |
| R22 / P3 | `streams::REMOTE_PYTHON::Frames`, maximum DATA length equals allocated buffer; explicit zero-length FINISH | A disconnect mid-frame must not be mistaken for successful EOF/publication; frame length must fit the actual preallocated storage. | Rejects a corrupt/incompatible helper frame instead of allocating its advertised length; payload can be arbitrarily long across multiple frames. | **KEEP.** This is protocol completeness and actual buffer capacity, not a file-size cap. Negotiate protocol changes rather than weakening framing. |
| R23 / P3 | `process::OutputBudget`, optional combined stdout/stderr disk quota, continued drain-to-sink after failure | An explicitly configured quota prevents exhausting its requested storage budget; continuing to drain prevents a quota error from deadlocking the child. | Output beyond an exceeded quota is discarded while the outcome carries a spool error. No quota is imposed by default. | **KEEP explicit quota**, with progress/error visibility as soon as the spool fails. Do not silently report success or introduce a default small quota unsupported by actual output distributions. |
| R24 / P3 | `streams` FIFO writer rendezvous 10-ms retry and remote `poll(100)` close checking | Nonblocking FIFO writer open returns ENXIO until a reader exists; periodic retry preserves cancellation where no native open-readiness notification is supplied. Poll returns early on readiness. | Adds bounded retry latency and idle wakeups, but not a fixed interval after every transferred buffer or a 35-second sleep. | **SIMPLIFY where a platform event exists; otherwise KEEP cancellable fallback.** This is a specific rendezvous implementation, not a general justification for timer polling everywhere. |

## Reproduced normal-user rejection: FIFO fan-in

Using the frozen v0.6.5 delivery image SHA-256
`f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`,
a private fixture under repository `.temp` contained ordinary files `a`, `b`
and a FIFO `channel`:

```msync
a -> channel
b -> channel
channel -> out
```

Invocation was the actual CLI, not an isolated validation function:

```sh
timeout 10s /home/moesegfault/msync/.temp/ssh-windows-latency/msync-v065-before --no-config run case.msync
```

It returned status 1 immediately:

```text
execution error: a FIFO rendezvous has unequal writer and reader counts
help: Use one writer and one reader per FIFO wave, or separate independent flows onto distinct FIFOs.
```

The sources remained five and six bytes and no output was produced. Evidence was
retained at `.temp/runtime-safety-audit.uLuFmC/fanin`. This is a demonstrated
representation restriction, not evidence that removing the check alone makes
the present scheduler support fan-in. The replacement must specify normal FIFO
byte ordering/interleaving (POSIX atomicity applies only to suitably sized
individual writes), endpoint lifetime and the co-start group.

## Windows case-policy decision

Microsoft documents case-sensitive directories as a supported Windows/WSL
interoperability feature, including native Windows access. The current code
does not discover an actual collision before refusing: one flagged ancestor is
enough. Unsupported filesystem case queries fail the same early path.

The useful abstraction is a native namespace/resource identity with a case rule
belonging to the relevant directory, not a global claim that Windows paths are
always insensitive. The planner already separates exact keys from conservative
comparison/hazard hints, so uncertain case policy can retain extra dependencies
without necessarily forbidding the underlying file operation. Publication's
own name maps must be repaired as part of this change; simply deleting the
policy call would leave the existing case-folded model incorrect.

This host is Linux. The source-level rejection and Microsoft's documented normal
workload are established; a native Windows before/after copy with both `a` and
`A`, plus a single-file no-collision control and an unsupported-query filesystem,
is the required acceptance journey. No native Windows execution is inferred
from reading or cross-compiling the branch.

## Focused next experiments

- FIFO: one-writer/one-reader compatibility; two-writers/one-reader and the
  converse; capacity one slot below the actual group; peer replacement; an
  external predecessor that needs the group's slots. Prove eventual bytes/EOF
  and prompt progress, not only that validation accepts the graph.
- Control: hold an independent finite worker behind a gate; evaluate a runtime
  slot-only control before releasing it; separately read a conflicting path and
  prove that read waits. This discriminates scoped ordering from global drain.
- Spool/capture: compare scalar-only, attached live consumer and delayed REPL
  consumer on actual file systems; record scratch bytes, startup-to-first-byte,
  total I/O and EOF behavior. Retain the later-overwrite source-byte journey.
- Busy admission: occupy capacity with a gate-controlled flow, submit an
  independent pending transfer, confirm the prompt returns, then release the
  first peer and verify the pending work starts without resubmission.
- Remote commit loss: disconnect after installation but before acknowledgement;
  a reconnect should resolve the recorded outcome instead of requiring a manual
  destination inspection or blind retry.

## Production and research connections

- [Microsoft: case sensitivity](https://learn.microsoft.com/en-us/windows/wsl/case-sensitivity)
  establishes that mixed-case directories are normal supported namespaces, not
  inherently invalid input.
- [Linux FIFO semantics](https://man7.org/linux/man-pages/man7/fifo.7.html) and
  [pipe capacity/atomicity](https://man7.org/linux/man-pages/man7/pipe.7.html)
  explain both the genuine rendezvous obligation and why a one-to-one source
  count is not a POSIX requirement.
- [Microsoft: CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)
  distinguishes cancellation request from actual completion, which is why the
  retained native I/O lifetime checks have a concrete benefit.
- [MLIR side effects and speculation](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
  offers an existing model for deciding whether typed pure work needs an effect
  barrier, rather than treating all control nodes as unknown external work.
- [Unsafe at Any Copy: Name Collisions from Mixing Case Sensitivities](https://arxiv.org/abs/2211.16735)
  identifies concrete wrong-file/collision mechanisms when case policies differ.
  It motivates accurate namespace modeling, not refusing an entire case-sensitive
  directory. No attack claim from that research is adopted as this tool's threat
  model by default.

Related existing knowledge: [barrier runtime](barrier-runtime.md),
[structured runtime](runtime-v6.md), [streams](streams.md),
[planner proof obligations](planner-proof.md), and
[runtime performance](performance-runtime-v6.md).

## Native path implementation after the normal-user contract change

The runtime `domain::normalize` no longer queries/rejects a directory's case
policy, rejects symlink ancestors, or lexically erases `..` before resolving the
selected filesystem namespace. It canonicalizes the nearest existing prefix and
appends missing components. A selected linked workspace therefore works as its
native target; `alias/../file` uses the alias target's parent, not a lexical
parent guessed before link resolution. Native lookup errors remain ordinary I/O
errors, rather than being translated into a security refusal.

Pure HIR path identities now preserve Windows directory/file component spelling.
Drive prefixes and UNC server/share prefixes retain their platform root aliases,
but `a` and `A`, or `straße` and `STRASSE`, cannot establish identical resources
or satisfy an immutable-version proof merely through case folding. A separate
hazard-only comparison orders possible native aliases. Uncased Unicode
components, including ordinary Chinese/Japanese workspaces and emoji, retain
complete comparison keys. Only case-bearing non-ASCII components fall back to
the known parent prefix, not Rust's expanding Unicode uppercase map or a blanket
whole-endpoint dependency. The physical planner already has exact canonical keys;
frontier retirement now uses those exact keys instead of its hazard comparison.

The legacy planner's channel topology also accepts multiple readers/writers in
one co-start group, matching the runtime participant-group model. It no longer
invents one-to-one pairings or independent waves for all accesses to one channel
within the same block. Genuine file dependencies and closed feedback are still
reported as execution/liveness constraints, not security policy.

Focused coverage is in `tests/native_paths_v065.rs`, the HIR Windows-path and
candidate-index properties, and the changed legacy stream-plan cases in
`tests/plan.rs`. The linked-workspace cases exercise the real CLI. Native Windows
case-sensitive directory execution remains a platform acceptance task; portable
path-model tests do not claim that OS execution occurred on a Linux host.

The path adapter uses the standard library's existing native resolution rather
than another case/security probe. [Rust's canonicalize contract](https://doc.rust-lang.org/std/fs/fn.canonicalize.html)
resolves links through Unix realpath or Windows handle-based final-path APIs.
[Microsoft's case-sensitivity documentation](https://learn.microsoft.com/en-us/windows/wsl/case-sensitivity)
explicitly supports distinct case-only names and warns against applications
rewriting their spellings. These motivate exact identity keys plus separate
hazard comparisons, not refusal of the directory.

On Unix, a backslash inside an ordinary native filename remains a character,
not a portable separator. The logical effect adapter recognizes explicit
Windows drive/UNC spellings without rewriting ordinary POSIX paths; this keeps
legal newline/backslash/Unicode filenames distinct from directory paths.

Focused implementation acceptance passed seven logical Windows/native-path
cases, two runtime-domain cases, the candidate-index inclusion property with
512 generated examples plus its sibling/read-sharing control, and four actual
CLI journeys in `tests/native_paths_v065.rs`. The CLI journeys verify linked
workspace reads/replacement, native `alias/..` parent selection, preservation of
two distinct writes that lexical parent cancellation would merge, and literal
POSIX backslash names not being forwarded from a directory-path decoy.
The legacy planner's unchanged cases passed; its old expected rejection of an
independent two-writer/two-reader channel was replaced with the intended single
participant-group outcome while retaining the genuine same-file dependency
control. This updates a superseded policy assertion, not a failed I/O result.
