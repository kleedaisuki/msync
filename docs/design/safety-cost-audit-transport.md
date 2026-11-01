> Baseline inventory plus superseding implementation context: the user later
> rejected the recovery/transaction contract itself, not merely excessive helper
> orchestration. Old KEEP recommendations tied to that protocol no longer apply.
> The current implementation uses one native SSH exec per finite endpoint,
> ordinary staging/replace and retry, without registries/backups/syncfs. See
> [the current contract](statement-atomicity.md) and [validation](normal-user-validation.md).

# Safety-cost audit: transport, remote publication, WSL and authentication

## Scope and decision context

2026-11-01 follow-up to the native Windows report: one 1,795-byte upload took
35,399.5 ms despite materially faster manual SSH/copy. This note inventories
transport policies rather than treating every refusal as a security requirement.
It inspects `src/runtime.rs`, `src/runtime/{inspection,namespace,ssh,windows_auth}.rs`,
`src/execution/{endpoint,remote,wsl_source}.rs`, and `src/auth.rs`.
References use symbol names because the concurrent SSH repair changes line numbers.

The operating model is a single user authoring scripts and choosing endpoints.
Scripts and remote commands are not sandboxed. A hostile remote server is **not**
a general justification for rejecting the user's own ordinary paths. The useful
failure models are concrete: copying the wrong file, overwriting an existing
container member, mutating an external hard-link alias, losing a before-image
after process/network interruption, writing outside an owned import due to name
conversion, or leaving a writer alive after its owner returns. Native SSH's
existing host identity and credential behavior is a separate established contract.
A remote server that controls its shell already controls remote publication;
extra shell path predicates do not make that server trustworthy.

`KEEP` means a demonstrated semantic/ownership requirement, not an endorsement of
its current repeated implementation. `REPLACE` preserves useful behavior through
a better representation/owner. `SIMPLIFY` removes redundant or excessively broad
work. `REMOVE` identifies a policy with no benefit in this operating model.
P1 is direct ordinary-workload damage or material amplification; P2 is narrower
functionality/maintenance debt. This is a source audit, not a claim that an
unexecuted Windows scenario was reproduced on this Linux host.

## Highest-return decisions

1. **P1 REPLACE blanket Windows reparse refusal with tag/capability semantics.**
   `runtime::metadata` rejects every `FILE_ATTRIBUTE_REPARSE_POINT`. Reparse data
   is also used by storage filters and placeholders, not only symlinks/junctions.
   This can reject the user's cloud-managed files and paths even when native
   reads can hydrate/read them. Identify name-surrogate redirection separately;
   let supported data-filter files use ordinary reads. Existing symlink policy
   can remain until explicit link semantics are implemented.
2. **P1 REPLACE uppercase name identity with target-volume/directory semantics.**
   `validate_remote_names` applies Rust Unicode uppercase to complete relative
   paths. Windows has per-directory case sensitivity. `straße` and `STRASSE`
   also demonstrate that Unicode expansion is not a sound NT filename identity
   model. The implementation itself documents conservative false refusals in
   `docs/design/runtime.md`. Use target-native exclusive creation in owned
   staging to detect actual collisions before public publication, retaining the
   useful non-clobber guarantee. Source-only directory classification cannot
   determine destination identity rules.
3. **P1 SIMPLIFY repeated tree proofs around an already owned payload.**
   Source capture, `validate_source_tree`, remote upload finalization and
   `PUBLISH` all traverse/validate related trees. Carry explicit finite-tree and
   permission provenance from the owning capture. Keep boundary checks when a
   generic API receives an unowned source; do not re-prove trusted internal
   state with another network command or ancestor walk per leaf.
4. **P1 REPLACE filesystem-wide durability coupling, not durability itself.**
   `sync -f` flushes the filesystem, so unrelated remote writers can dominate a
   tiny upload. `PUBLISH`, recovery and intent retirement use several barriers.
   The concurrent repair already merges backup durability into initial intent
   durability and disarms the successful rollback trap. A later remote helper
   with file/directory fsync can preserve ordered durable state without waiting
   for unrelated filesystem traffic. Do not delete the remaining ordered
   barriers without replacing their crash/restart guarantee.
5. **P1 REPLACE WSL all-tree quiescence demand with useful acquisition.**
   WSL preflight walks types/devices, writes a complete metadata manifest and
   hashes it; capture walks it before and after copying and returns it again.
   A single unrelated selected-tree metadata change rejects the whole acquired
   copy and tells the user to stop source activity. This is not an atomic
   filesystem snapshot: a writer can still change bytes while preserving the
   observed metadata. Use one owned acquisition and focused per-file/version
   retry or an actual snapshot primitive if snapshot coherence is required.
   Preserve the stronger declared snapshot contract explicitly rather than
   silently downgrading it. Unselected siblings remain out of the payload.
6. **P1 SIMPLIFY remote transport lifecycle on Windows.** Many independent
   native clients repeat authentication/connection setup. UI/cancellation
   machinery does not create connection reuse. The concurrent private-journal
   SCP repair removes redundant generic staging; a retained native SSH exec
   channel is the longer-term solution. Do not turn user configuration advice
   into the product fix. See the existing SSH performance audit.

## Policy inventory

### A. Ordinary path and data semantics

| ID / disposition | Location | Concrete model and current normal-user cost | Simpler implementation / action |
| --- | --- | --- | --- |
| T01 P1 REPLACE | `runtime::metadata`; `inspection::Inspection::check_parents` | Every symlink and every Windows reparse point, even ancestors, is refused. A source link can otherwise dereference outside the intended payload; target links can redirect writes. But chosen symlinked workspaces/home paths are common, and data-bearing reparse tags are not name redirects. No attacker is necessary for either wrong-target loss or false refusal. | Classify link/reparse kind; allow ordinary data-filter reads. Define source-link and target-link behavior explicitly, resolving chosen ancestor links once to identity. Do not equate all reparse data with redirection. Internal cleanup remains stricter. |
| T02 P2 KEEP, SIMPLIFY implementation | `preflight_cancellable`, `trees_overlap`, `ancestor_has_identity`; `REMOTE_COPY::ancestor_identity`; `endpoint::ALLOCATE::check_origin` | Same object, hard links and ancestor/descendant copies can mutate source or recurse into their own result; Windows verbatim/case/8.3 and WSL aliases are real. Reopening every ancestor for every leaf and rechecking after immutable capture multiplies metadata work. | Establish statement-owned canonical root identities once and reuse them; retain actual source/target overlap semantics. This is data-loss avoidance, not sandboxing. |
| T03 P2 KEEP | `validate_destination`, `copy_selected`, `endpoint::inspect`, `PUBLISH` mode branches | Existing directory without `/`, selector without container, incompatible file/directory replacement, and container collision are rejected. These are the language's exact/container/replace modes, including preserving existing members. | Keep modes typed and handle collision once on the private replacement view. Removing refusals changes established DSL behavior, not just safety code. |
| T04 P2 KEEP / REPLACE for new link support | `validate_source_entry`, `REMOTE_VALIDATE`, `REMOTE_FINALIZE_UPLOAD`, `PUBLISH` | Recursive finite capture rejects symlinks, FIFOs, devices and sockets. Blind recursive copy can block forever or read unintended data. Whole-tree symlink rejection also makes ordinary code repositories with links unsynchronizable. | Keep finite-vs-stream dispatch. For full tree sync, add a symbolic-link entry type and preserve links rather than repeatedly rejecting them; do not silently follow or silently skip. |
| T05 P2 SIMPLIFY | `runtime::local_path_at` / `expand_home` | PATH must be one component; HTTP/PATH destinations readonly; absent/empty ENV and `~user` unsupported. Mostly namespace capabilities, not attacks. `~user` is a feature gap and zero-length ENV may reasonably mean cwd, but current behavior is documented. | Keep capabilities; improve resolution where useful without describing arbitrary namespace restrictions as security. Prefer explicit full path over expanding executable PATH scope. |
| T06 P1 REPLACE | `validate_staging_basename`, `validate_remote_names` | Windows separators, ADS `:`, devices and aliases can cause wrong path/object or silent collisions. Current invalid-character/trailing-dot/space/control and Unicode-uppercase policy rejects names even when a native supported namespace/case-sensitive directory can represent them. | Keep actual representation guarantees for chosen native transport. Use native staging creation/collision receipts for actual destination namespace. Do not automatically rename source files. |
| T07 P2 REPLACE | `SourceReceipt::parse`, `remote_copy_output`, `receive_selected`, `Spec::names` | Whole receipts require UTF-8 even on Unix, so valid byte filenames cannot download/upload through these paths. Selector enumeration silently drops non-UTF-8 names with `filter_map`, which can misrepresent a broad selector as complete. | Keep raw byte names in Unix internal paths and encode protocol fields separately; define regex-on-byte or explicit unmatchable-name diagnostics. Preserve unselected payload exclusion, but do not silently lose selected files. |
| T08 P1 REPLACE | `endpoint::inspect`, allocation `.lines()`, `Spec::names`, `recover_journal` line `read` | CR/LF targets and backslash basenames are refused although POSIX permits them. Here the obstacle is line-based receipt/intent representation and unnecessarily portable basename restrictions, not a user threat. | Use NUL/length-delimited allocation and intent records; reject only NUL in POSIX argv and actual target separators. Never shell-interpolate unquoted values to remove the refusal. |
| T09 P2 KEEP, REPLACE unsupported transport | `scp`, `escape_sftp_pattern` | Win32 SCP rewrites backslashes and applies globs to arguments; prior real fixture copied a decoy file. Literal glob encoding is useful. Native Windows rejects remote backslashes instead of copying a different file. | Keep literal-selection fix; a framed stream/SFTP API can support these names. Do not disable literal matching or silently transform actual names. |
| T10 P2 KEEP | `source_relative`, receipt duplicate/root checks; `SourceReceipt::apply_times` | Receipt-relative paths are applied beneath local capture and affect metadata. Absolute/parent paths or duplicate root can touch wrong objects after malformed helper output, even due bugs. | Parse once into a relative-path type. Basic framing and containment are cheap ordinary protocol correctness; no hostile-server story is needed. Avoid rescanning all ancestors once capture identity is owned. |
| T11 P2 SIMPLIFY | `source_time`, `touch_time`, `UploadTimes` | Timestamp precision/range validation preserves exact nanoseconds/pre-epoch times. Extra helper per 8 KiB metadata batch costs native Windows reconnects. Repeated permission/no-link checks under owned staging add traversal. | Keep exact times; send metadata with bytes/finalization on the same native channel. Check host representability once; where destination cannot represent precision, report its actual capability rather than silently round. |
| T12 P2 REMOVE destination inference for proven uploads; REPLACE generic source heuristic | `RemoteSource`, `SourcePermissions`; `REMOTE_COPY::posix_mode`; upload finalizer | An unlisted POSIX filesystem can lose meaningful modes under a magic-number whitelist. Conversely, real WSL/DrvFS sources can expose synthetic 0777, so a generic Unix observation is not proof of POSIX provenance. Destination staging filesystem cannot recover source provenance. | The concurrent repair preserves already-materialized native Unix upload metadata without querying destination FS; Windows uploads remain portable. Generic REMOTE_COPY's source-FS heuristic remains unchanged. Replace it only with actual source capability/provenance evidence, not unconditional Unix-mode copying or a larger unexplained allowlist. |
| T13 P2 KEEP, capability-based | `PUBLISH` read-only exact-target check, literal `chmod --reference`, `RECOVERY::{admit_directory,restore_mode,remove_owned_tree}` | Root/testing privilege could otherwise bypass ordinary readonly intent. Cleanup of readonly private trees must not permanently mutate before-image modes or external hard-link aliases. Some chmod failures presently block cleanup. | Keep source/target permission semantics, restore modes, and never chmod hard-linked regular user files during cleanup. Limit chmod authority to owned directories or identity-proven installed roots. |
| T14 P2 SIMPLIFY | `http_command_builder` origin check | Same-origin check rejects `base.join()` results while curl `--location` accepts cross-origin HTTP redirects anyway. It is not an SSRF sandbox when the author chooses endpoint URLs and script commands. | Treat URL qualification as namespace semantics only. Permit explicitly authored absolute HTTP URLs if the DSL supports them; retain http/https protocol capability for this backend. Do not advertise current check as protection against malicious HTTP endpoints. |

### B. Bounds, protocol, inspection and acquisition

| ID / disposition | Location | Concrete model and current normal-user cost | Simpler implementation / action |
| --- | --- | --- | --- |
| T15 P1 REPLACE | `inspection::inspect_remote_batch` 8 KiB budget | Batching limits argv growth, but one legitimate long path is rejected rather than transported. Fixed command bytes include helper code; target OS can support paths larger than the leftover budget. | Keep bounded batches for ordinary paths, move data to framed stdin for oversize path/request. No need to push a representable path error back to user. |
| T16 P1 REPLACE | `run_process`, `drain_bounded` 8 MiB stdout + 64 KiB stderr | Metadata for a large but ordinary tree exceeds 8 MiB and explicitly asks user to split selection. Full stderr is drained but diagnostic tail is discarded; important final error may be beyond first 64 KiB. Preventing unbounded RAM is useful, not a justification to reject tree size. | Stream-parse records or spool under owning invocation; retain bounded head+tail diagnostics with total bytes. Keep draining to avoid backpressure deadlock. |
| T17 P1 REPLACE | `endpoint::drain` 1 MiB per control stream | Continues draining but silently retains only prefix; caller cannot distinguish complete from truncated receipt/diagnostic. A benign long diagnostic loses its final cause. Control receipts themselves are tiny. | Parse fixed receipts with framing; separately retain bounded head+tail stderr and truncation count. Overflow of unexpected stdout should be recorded explicitly rather than plausible truncated success. |
| T18 P2 KEEP, SIMPLIFY | `parse_remote_inspections`, source/copy/WSL receipts | Count/tag/version/arity/class parsing catches helper/protocol mismatches, not just attacks. Current field vectors and repeated UTF-8 conversions add allocation but are not the large cost. | Typed streaming parsers at protocol boundary, not repeated validation through callers. |
| T19 P2 SIMPLIFY | preparation `Inspection`, `remote::class`, runtime copy preflight | Live reclassification distinguishes finite files from streams; source may change between preparation and execution. Multiple full fresh observations of an owned immutable capture do not have the same benefit. | Retain final live admission where dispatch changes; reuse statement-owned observations after capture. No cross-statement cache of filesystem truth. |
| T20 P1 REPLACE | `wsl_source::{PREFLIGHT,CAPTURE,SELECT_COMMON,SELECT_CAPTURE}` | Multiple find passes, device-set sort, symlink/special scans, full manifest, SHA256, repeated cmp and complete receipt transport. Any source metadata change rejects whole acquisition and says stop activity. It neither locks source nor creates a true atomic snapshot. | One ordinary owned acquisition with per-file/version validation and useful retry; use OS snapshot if stronger coherent multi-file snapshot is a product promise. Remove fingerprinting solely to defend user-owned `.manifest` from the same user; retain framing/count correspondence needed to detect implementation error. |
| T21 P2 KEEP narrow proof, REPLACE broad optimization gate | `namespace::wsl_native_file`, `native_windows_filesystem`, WSL `ef53` / device checks | These are proofs of cross-domain non-aliasing, not permission to run on the filesystem. DrvFS/network projection can name the same Windows object and defeat snapshot alias rules. But FS-magic allowlists limit optimized paths on overlay/btrfs/external filesystems and unknown evidence is collapsed into fallback. | Keep sound “unknown is not distinct” reasoning. Replace filesystem spelling with actual projected identity/capability where practical; log why optimization fell back. Do not simply assume every WSL path is disjoint from Windows. |
| T22 P1 REPLACE | `wsl_source::command`, `import_finite_source` fresh uncancellable token for WSL acquisition | Killing Windows `wsl.exe` does not prove guest copy exited; immediate owner cleanup races a surviving writer. Current remedy makes Ctrl-C wait for entire remote copy, even a slow/mounted/hung source. | Keep guest-writer joining, but add an invocation-owned guest process cancellation/acknowledgement protocol; quarantine only that import until guest exits. Never make full uncancellable copying the permanent user experience. |
| T23 P2 KEEP | WSL selector shallow listing / selected acquisition | Unselected symlink/special/private siblings must not be traversed or copied. This avoids both ordinary errors and accidental unwanted data movement. | Keep lazy selection and one selected cohort; remove all-tree checks outside selected payload. |
| T24 P2 SIMPLIFY | `Scratch::new_in` 100 PID/counter candidates; drop ignoring errors | Artificial 100-collision ceiling can reject stale but otherwise writable scratch. Cleanup failures disappear, hurting diagnosis. | Use established unique private temp allocation under owner root and report cleanup issue with retained path. No global scans or deleting similarly named user paths. |

### C. Publication, recovery, identity and isolation

| ID / disposition | Location | Concrete model and current normal-user cost | Simpler implementation / action |
| --- | --- | --- | --- |
| T25 P1 SIMPLIFY | `remote::Material::{capture,populate}`, `materialize`, `stage_ssh_source` | Captures immutable source into version store, materializes another tree, walks validation/timestamps, then uploads. Full local bytes/metadata can be copied multiple times before network starts. Capture ownership is useful; every subsequent duplicate is not. | Upload directly from an owned immutable capture representation or materialize once, carrying metadata/provenance/finite-tree proof. Preserve source versions, not redundant local data movement. |
| T26 P1 REMOVE redundant staging | historical generic `runtime::send` inside `Spec::run_inner` | Journal already provides private owned upload destination; extra `/tmp` root, SCP, remote copy and cleanup added 3 clients and one remote full copy. No additional authority/rollback guarantee. | Direct SCP into owned journal payload and in-place finalize. Concurrent repair implements this specialization; generic unowned-destination send is not automatically removed. |
| T27 P1 REPLACE | `endpoint::{ALLOCATE,PUBLISH,SETTLE,RECOVER}` `flock -x 7` on containing directory | Coordinates overlapping roots and journal cleanup across same-user processes, but serializes **different sibling targets**; PUBLISH holds lock across destination cloning, merge and fsync. SETTLE also acquires directory lock before its journal timeout. | Narrow ownership locks to actual conflicting publication roots/ancestor relationships. Reuse scheduler conflict knowledge within process; retain cross-process protection for real overlapping updates. Never treat merely sharing a parent as a user conflict. |
| T28 P1 REPLACE | `PUBLISH` destination container `cp -a --reflink=auto` | Inserting a tiny file clones/copies complete preexisting root, checks full payload/destination, and merges recursively. On non-reflink storage cost is proportional to unrelated destination bytes; before-image semantics are real, not hostile-input defense. | Endpoint version helper or publication representation tracking touched children can avoid complete root copy while preserving one-statement rollback. Assess statement visibility/isolation contract explicitly; hardlink old writable files is not an equivalent private clone. |
| T29 P1 REPLACE | all remote `sync -f`, `retire_intent`, rollback path | Ordered durable payload, before-image, intent, installed state and COMMITTED survive crash/restart. Filesystem-wide barriers couple tiny operations to unrelated writes and repeats waste time. | Keep order and barrier errors; consolidate same-stage barriers and use file/directory fsync via helper when possible. Concurrent repair removes the known successful EXIT rollback flush and redundant backup barrier. |
| T30 P2 KEEP | `Pending` durable record / file lock; `remote::recover` skips `WouldBlock` | Distinguishes abandoned coordinator from another still-live user process; without lock, later invocation can delete active transfer payload. Writable JSON registry, version parsing, owner marker and no-redirect checks prevent incidental collision/stale/corrupt state from authorizing deletion. | Keep one local record/lock per publication; consolidate redundant marker metadata reads at owning registry. Do not replace with database/daemon/global history. |
| T31 P2 SIMPLIFY incidental schema rules; KEEP cleanup authority | pending record checks `/.msync-v4.` substring and absolute path, `owned_registry`, remote intent same-parent/identity checks | Prefix spelling is not identity or authority against malicious same-user edits. Cheap structure catches broken records; final journal ownership/root identity prevents clobbering a genuinely changed target after interruption. | Treat record as versioned internal capability with explicit journal/root ownership. Keep identity refusal on changed user root; don't extend filename rules into arbitrary filesystem allowlists. |
| T32 P2 KEEP | `recover_journal` before/after device:inode checks, regular-file unlink-only cleanup, mode identities | Another user action/process can replace destination while recovery is pending. Blind rollback would delete that new content; cleanup chmod can affect external hardlink aliases. | Keep exact owned-version/identity accounting. On true conflict, preserve both versions and show actionable paths, not an unsupported claim that arbitrary same-user mutation is prevented. |
| T33 P1 REPLACE timeout UX, KEEP commit ambiguity resolution | `Spec::settle` fresh token + 35 s; SETTLE/RECOVER `flock -w 30`; `run` detached settlement worker | Lost SSH acknowledgement does not establish commit/abort; committed wins later client failure. Current fixed 30/35-second waits can label a merely slow active commit unknown and return “inspect manually”; repeated native auth adds latency. | Keep independent receipt/join semantics, native owned process reap and durable pending record. Use observed progress/lock state and retain operation continuation/recovery rather than a magic wall-clock refusal; explain current phase and allow user-driven cancellation. 35-second resemblance alone does not prove user's successful `done` was this timeout. |
| T34 P1 SIMPLIFY recovery workflow, KEEP current external invariant | `remote::recover` stops on first unreachable abandoned endpoint | One disconnected old endpoint blocks later executable submissions, including unrelated local-only work. This is a real liveness cost, not a hostile-input boundary. Project AGENTS explicitly currently requires every executable submission to settle interrupted state before success. | Do not violate current invariant in a local patch. Product-level improvement would surface/settle pending records automatically with precise endpoint/conflict ownership; any independent-work admission requires an explicit contract evolution, not quietly skipping recovery. Existing no-state/live-owner fast path must remain. |
| T35 P2 KEEP root replacement limit, SIMPLIFY container scope | `ALLOCATE` rejects `/`; `PUBLISH` scans existing tree, allows symlinks only container mode | Replacing filesystem root is incompatible with sibling journal publication; rejecting unrelated unsupported descendants of a whole-tree replacement can be needlessly broad, while merge retains them. | Reserve impossible root replacement as capability limit; define whether root container insertion can use child-level owner. For replacing a directory, old special entries need not be treated as source payload; preserving before-image/cleanup may support them without opening devices. |

### D. Authentication, cancellation and process ownership

| ID / disposition | Location | Concrete model and current normal-user cost | Simpler implementation / action |
| --- | --- | --- | --- |
| T36 P2 KEEP | `SshContext` owned versus Existing session, `stop`, `BatchMode=yes` only after lease | Do not kill user masters; losing authenticated lease must not open a password prompt from background editor work. These are real UX/lifetime contracts. Warm probes and private-master setup add clients; Windows has no equivalent lease. | Retain configured native SSH behavior and scoped ownership; reduce control probes using retained child/socket liveness as warranted, and implement Windows reuse rather than repeated prompt UI. |
| T37 P2 SIMPLIFY fallback | `private_directory`, 95-byte socket cap, `ensure_optional`, `requires_foreground` | Unix socket path length/writable cwd `.temp` can force foreground completion despite remote transfer itself working. Retained identity avoids copying its own socket and deleting replaced user directories. | Keep identity-bound exclusion/cleanup and fallback-to-working foreground, not hard failure. Reuse configured application-owned short control storage when available without moving project artifacts to arbitrary locations; do not make long cwd an error for copying. |
| T38 P1 SIMPLIFY / REPLACE | `auth::Submission::{structured,start_with_cancel}`, `reuses_transport`, `watch` | Scans possible backend references to allocate a cancellation runtime/watcher even for unselected SSH or some generic loops. Actual authentication is correctly lazy; eagerly contacting referenced endpoints would be wrong. Each submission can create a thread and Tokio runtime merely to observe Ctrl-C. | Centralize existing signal/cancellation ownership; establish endpoint/session only on admitted actual work. Avoid plan-wide foreground joins. Keep cancellation token scope, task/thread propagation and user labels. |
| T39 P2 KEEP, SIMPLIFY where OS offers direct handle | `windows_auth::{ProcessJob,spawn,spawn_async,primary_thread}` | Suspended start + job enrollment avoids child helpers escaping before enrollment; without it askpass/proxy may retain output pipes or survive Ctrl-C. Toolhelp snapshots all threads to locate newly spawned primary thread and job setup can fail in restricted host jobs. | Keep owned process-tree termination, not a “sandbox.” Prefer process API exposing primary thread handle or documented nested-job compatible launch; show actual enrollment failure. Do not return success with unowned live writers as a shortcut. |
| T40 P2 KEEP, simplify allocation | `CancellationEvent`, `graphical_command`, askpass integration | Event lets native Cancel terminate submission instead of retrying empty password. Context task/alias and honoring external askpass are useful. PID/counter event collision refusal defends accidental naming collision, not a realistic malicious same-user attacker model. Event watcher/thread is per graphical submission. | Keep native Cancel and helper context, use platform-unique allocation/in-process event multiplexing if overhead matters; never cache user secrets just to avoid reconnects. |
| T41 P2 KEEP | `ssh_client`, host alias passing, explicit config, native host keys/agent, no secret telemetry | Working OpenSSH Host aliases/ProxyJump/agents/hardware keys/native prompts are external behavior. Replacing SSH or auto-switching client family risks breaking them. Bare ssh/scp lookup can choose different families from manual SSH. | Diagnose/retain selected client identity and explicit configuration; any new library/transport must match real workload compatibility. No invented sandbox or separate credential policy is needed. |

## Cross-cutting interpretation

The most costly defenses are not the constant-time record parser. They are
turning representation constraints into user refusals, re-proving owned state by
walking it again, treating a whole directory/volume as a conflict domain, and
using “stop changing your files / split selection / inspect manually” instead of
a runtime-owned continuation. The intended simpler model is:

```text
selected user source -> one owned captured version + typed metadata provenance
                    -> one native transport/channel into owned private payload
                    -> one publication owner with precise conflict identities
                    -> ordered durable intent/commit + automatic receipt recovery
```

Validation belongs at representation/ownership transitions. A private payload's
existing proof should travel with its owner, not be reconstructed from its path,
filesystem magic or repeated network probes. This is not permission to erase
source/target distinction, silently discard files or claim snapshot isolation
that a best-effort copy cannot supply.

## Engineering and research references

- [Microsoft reparse points](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points)
  documents filter-defined data and mounted-folder/link uses; the attribute bit
  alone does not mean a symlink. Supports T01's tag/capability replacement.
- [Microsoft WSL case sensitivity](https://learn.microsoft.com/en-us/windows/wsl/case-sensitivity)
  describes per-directory case sensitivity and inheritance behavior. Supports
  target-native collision behavior, not global Unicode uppercase rejection.
- Basu et al., [Unsafe at Any Copy: Name Collisions from Mixing Case Sensitivities](https://arxiv.org/abs/2211.16735)
  studies how inconsistent filesystem/tool name resolution causes wrong-object
  outcomes. Its hostile-repository threat model is not assumed here; the useful
  mechanism is that name identity must agree with the destination, not a generic
  casefold approximation. Native staging probes prevent accidental collisions
  too; the paper is not a reason to reject all mixed-case user files.
- [GNU cp](https://www.gnu.org/software/coreutils/manual/html_node/cp-invocation.html)
  distinguishes preserving links from following them. A link entry model is a
  feasible alternative to refusing all symlink-containing code trees.
- [GNU sync](https://www.gnu.org/software/coreutils/manual/html_node/sync-invocation.html)
  defines `-f` as filesystem-wide syncfs; [fsync(2)](https://man7.org/linux/man-pages/man2/fsync.2.html)
  distinguishes per-file durability and directory-entry durability. Supports
  reducing flush scope without deleting crash recovery guarantees.
- Existing project records: `docs/design/ssh-performance-v065-audit.md`,
  `docs/design/ssh-performance-v065.md`, `docs/design/runtime.md`,
  `docs/design/statement-atomicity.md`, `docs/design/remote-publication-v5.md`.

## Discriminating next journeys

Do not run a full regression matrix for a documentation audit. Implementation
acceptance should follow the changed contract:

1. One-file Windows upload, then repeat in same REPL; inspect client/phase counts,
   bytes, actual target modes/mtime and cold recovery. Compare same native client
   as manual SSH, not WSL substitution.
2. Existing large container on non-reflink filesystem, insert one tiny file;
   measure unrelated bytes copied, directory-lock hold time and flush time.
   Concurrent disjoint sibling publication must not wait for unrelated copying.
3. Supported cloud-managed regular source, explicit symlinked workspace ancestor,
   case-sensitive destination with `a`/`A`, and Unicode case expansion pair.
   Establish actual native API capability before relaxing specific refusals.
4. Busy selected WSL tree: harmless directory metadata changes versus actual
   changed source file; quantify whole-tree scans and retry work. Destination
   stays unchanged on unresolved capture, but user need not freeze unrelated work.
5. Oversize metadata/listing and long representable paths: bounded memory with
   streaming/spooling, complete intended file set and retained diagnostic tail.
6. Real process/network interruption during staging/publication; next ordinary
   submission reconciles existing pending state and protects a user-replaced
   destination. Long copy cancellation joins the real writer without waiting for
   an entire uncancellable tree copy as the only option.
