# Safety mechanisms: user value and cost audit

## Current C# implementation

The [2026-11-11 C# audit](safety-cost-audit-v086.md) supersedes the old Rust
inventory as an assessment of current code. It includes process-gated CLI
reproductions, filesystem syscall observations, session optimization gates,
and the remaining distinctions between useful boundaries and broad fallback
policies. The historical entries below are not claims about current C# defects.

## Scope and decision rule (2026-11-01)

A native Windows v0.6.5 user reported 35,399.5 ms for one 1,795-byte upload.
The SSH investigation exposed repeated authenticated clients, redundant private
staging and filesystem-wide publication flushes. The user expanded the task to
evaluate defensive design across the complete project, not merely SSH.

The evaluation starts from a single-user synchronization/program-composition
tool: the user intentionally chooses scripts, native processes and endpoints.
msync is not a sandbox or a multi-tenant execution service. A hypothetical
malicious actor is not sufficient justification for restricting an ordinary
workflow. Existing public behavior and metadata/publication contracts still
matter; removing an implementation restriction is different from silently
removing a guarantee users already rely on.

Each mechanism must identify its concrete actor or failure, the user data or
capability at stake, the ordinary-workload cost, and a simpler alternative.
Evidence can be a supplied user journey, an existing regression, a small actual
reproduction or a direct code-path argument. Absence of a measurement is not
permission to invent a performance number. In particular, classify separately:

1. Core semantics: literal paths, correct bytes/metadata, deliberate overwrite
   behavior, immutable inputs and ownership of cleanup.
2. Common operational failures: interruption, disconnection, full disks,
   concurrent live statements and changing filesystem state.
3. Compatibility/format constraints needed to interpret supplied programs.
4. Adversarial security restrictions: these require an explicit attack model.
5. Resource policy: an arbitrary cap is not automatically appropriate for real
   directory sizes, long-running commands or normal desktop configurations.

Disposition vocabulary: **remove**, **simplify**, **replace**, or **keep**.
“Keep” must explain a concrete benefit, not invoke safety as its own objective.
“Replace” must preserve the useful contract while reducing its cost or refusal
surface. The first request asked for evaluation. The subsequent request explicitly
supersedes the transactional/recovery contract and authorizes removing its cost;
the normal-copy/retry model below is now authoritative.

## Coverage

| Domain | Evaluated entries | Detailed record |
| --- | ---: | --- |
| Capture, publication, recovery, local path and metadata policy | 23 | [Storage](safety-cost-audit-storage.md) |
| SSH/WSL, remote helpers, authentication and process capture | 41 | [Transport](safety-cost-audit-transport.md) |
| Process/stream lifetime, IO, planning and effect admission | 24 | [Runtime](safety-cost-audit-runtime.md) |
| Parser/compiler, HIR, objects, IR and relocation | 30 | [Language and artifacts](safety-cost-audit-language.md) |
| CLI/configuration, REPL/editor/LSP, telemetry and packaging | 41 | [Tooling](safety-cost-audit-tooling.md) |

The audit covers first-party production implementation and its public delivery
surface. Third-party source is examined where a specific integration depends on
its behavior, not treated as a separate upstream security-audit project.
The 159 entries include cross-domain views of related mechanisms; this is not a
claim of 159 independent defects. Each entry states its own disposition.

## Integrated conclusions and implementation order

The dominant problem is **proof and ownership amplification**: a path is passed
between layers instead of carrying its already-established meaning, so the next
layer reopens, validates, snapshots, registers and persists it again. A second
pattern turns a missing internal representation into a refusal of an ordinary
OS capability. A third lets an optional observer govern the work it observes.
These problems are not solved by deleting all checks or raising every limit.

| Priority / related entries | Finding and actual user cost | Decision |
| --- | --- | --- |
| 1 — transport T26/T29, storage ST04/ST05 | Repeated owners/barriers: a tiny SSH upload needs eight authenticated sessions without reuse; one six-byte local-copy lifecycle issued 39 fsync calls, including unchanged registry-gate flushes. The latter is a syscall count, not 39 proven physical device flushes or a measured latency multiplier. | Remove redundant work, consolidate one statement owner and move existing-stage persistence to transitions that actually change durable state. SSH subset implemented below; local protocol consolidation is not yet implemented. |
| 1 — tooling C01/C02 | Config is validated before CLI overrides, and inactive SQLite settings can reject `--no-telemetry`/JSONL runs. | Parse, overlay and validate the active effective configuration once. Keep actual numeric/API constraints, not irrelevant inactive-policy gates. |
| 1 — tooling T01/T02 | A completed copy prints done and has correct destination bytes, then returns failure solely because an optional exporter reached its cap; an invalid observer path can prevent all work. | Separate transfer outcome from observation health, retain rich actionable observer diagnostics, and define compatibility of exit/status reporting explicitly. Never silently claim a complete trace. |
| 1 — language L01 | 128 sibling calls pass, 129 sibling calls fail as excessive nesting although actual delimiter depth is bounded separately. | Remove the width quota; retain actual stack/representation obligations. This is an ordinary acceptance bug. |
| 1 — storage ST03 | An empty recovery directory owned by the same user becomes unusable at 0750, and even declarations fail, despite no added group write authority. | Remove readability-as-corruption admission policy under this working model. Keep private creation defaults and exact identity/live-owner facts. |
| 2 — storage ST01/ST02, transport T01/T06, runtime R01 | Selected linked workspaces, nonredirecting Windows reparse points and supported case-sensitive directories are blanket-refused. | Replace missing link/tag/case representations. Preserve literal/native identity and cleanup ownership; do not just remove checks while the data model still aliases distinct objects. |
| 2 — transport T15/T16/T17, language L04/L05/L14/L15/L24 | Long names, large listings/import sets and generated finite objects cross unrelated hard-coded ceilings; some diagnostics silently lose their tail. | Stream/spool variable-size data, use actual representation/platform constraints, and apply an intentional resource policy where necessary. Do not ask users to split a valid tree to accommodate an internal Output vector. |
| 2 — storage ST06, transport T20/T25, runtime R05/R06 | Capture/materialize/copy and repeated WSL manifests reproduce work and disk use, but do not produce an atomic multi-file source snapshot. | Carry one captured owner and typed provenance through consumers, move/adopt when unique, clone for real fan-out, and use genuine snapshot capabilities only when their stronger contract is desired. |
| 2 — storage ST09/ST10, transport T27/T28, runtime R03/R04 | Whole-parent locks, full-container clones, scalar-control global drains and busy-capacity errors expand conflict or waiting beyond selected effects. | Narrow the conflict/wait scope; queue runtime-owned pending work rather than requiring resubmission. Preserve actual overlapping-update and rendezvous liveness obligations. |
| 2 — runtime R02 | Two FIFO writers and one reader are rejected by a one-to-one proof model, not by POSIX. | Represent generation-scoped participant groups and scheduling/EOF semantics; deleting only the count check would leave the scheduler incomplete. |
| 2 — tooling E01/E05 | Pasted TAB text on Windows and explicitly selected symlinked source formatting are rejected by internal rendering/update choices. | Support the normal workflow with correct rendering and resolved-target atomic writes, preserving the selected link. |
| Keep, simplify implementation — storage ST12–ST16, language L19–L22/L26–L29, runtime R15/R19–R23, tooling P01/P04/E09–E11 | Exclusive creation, no foreign cleanup, hardlink metadata preservation, complete decisions, valid executable references, native I/O lifetime and stable released bytes prevent concrete user-data or execution failures. | Keep these useful contracts and express them once through owners/types. They are not an excuse for every additional scan, refusal or persistence barrier. |

The first implementation tranche should remove the demonstrated flat-call and
unchanged-gate mistakes and unify effective configuration. Observer outcome
separation needs an explicit CLI compatibility decision. The next tranche should
consolidate ownership/persistence and native transport lifetime; link/case/FIFO
support is a representation change, not a pile of permission exceptions.

## Superseding implementation decision

The user rejected the remaining journal/backup/syncfs and repeated-client model,
not merely its implementation constants. On 2026-11-01 the contract changed to
ordinary file staging/replacement and retry. The older KEEP recommendations
above that depended on recoverable all-or-nothing statements are historical.

Implemented work removes persistent recovery admission, journal/backup/barrier
protocols, implicit client preauthentication, duplicate file acquisition and
whole-container clones. Containers now merge/overwrite selected entries so that
retry is useful. Native links/case behavior, ordinary FIFO N:M participants and
queued independent flows replace blanket rejection. Config is overlaid before
active validation; optional diagnostics no longer govern transfer status; input,
object, import, query and LSP byte/width policy caps are removed. Compiler-owned
objects/graphs carry existing facts instead of replaying full certificates.

Actual parser/type/reference validity and native buffer/descriptor lifetime are
not deleted: they define how the requested program executes, not a policy about
hostile users. True recursive native-stack constraints remain where the data
representation is still recursive; flat scope composition is balanced rather
than rejected at an arbitrary width.

See [the current transfer contract](statement-atomicity.md) and
[normal-user validation](normal-user-validation.md). The earlier SSH measurements
below identify an intermediate image, not the final rewritten transfer path.

## SSH changes implemented in this working tree

The implementation work keeps OpenSSH and statement-owned recovery. It removes
the generic `/tmp` staging owner from journaled uploads, normalizes private
uploaded payloads in place instead of copying them again, disarms successful
rollback only after durable COMMITTED, and groups a copied ordinary-file
before-image with the initial intent barrier. Phase/process timing is explicit
for both synchronous transport and asynchronous publication clients.

These changes do not provide native Windows connection multiplexing and do not
yet replace remote filesystem-wide syncfs with targeted fsync. Native Windows
timing and the precise allocation of the user's 35 seconds remain separate
acceptance questions; Linux loopback results must not be represented as answers
to them.

Actual-helper counts changed from **5 to 4 syncfs calls** for a missing target
and **6 to 4** for an existing ordinary file. The generic unowned-destination
copy path is unchanged. Direct journal staging removes two authenticated remote
sessions and the remote payload copy; a separate in-place metadata/count helper
remains. This is not a claim that the complete path now uses one connection.

Focused evidence includes 12 authenticated CLI SSH journeys (including exact
fractional timestamps and traced owned/native-client topologies), ordinary-file
backup crash cuts, readonly/partial-GC recovery, native versus portable-mode
helpers, strict local static checks and a Windows-target compile check. Native
Windows runtime/performance has not been run on this Linux host. Detailed image
identities, paired observations and commands are in
[the SSH performance record](ssh-performance-v065.md#native-windows-follow-up-delivery-2026-11-01).

## Final normal-path result

The completed rewrite is validated on the exact image recorded in
[normal-user acceptance](normal-user-validation.md#final-integrated-release-image):
local ordinary copies use one fsync/zero syncfs and same-parent rename; finite
SSH uploads, downloads, same-server arrays and bare regex selections use one
native exec, not a separate authentication per helper/item. Container writes
merge/overwrite selected entries and retry normally, without cloning unrelated
old destination data. Final image tests cover 2,200 selected files, active FIFO
cancellation, exact metadata and no late orphan payload.

The normal compiler graph no longer materializes all pairwise independence
certificates, and overwritten-file specialization no longer re-proves public
paths three times after acquiring the surviving value. GUI/diagnostic admission
is not allowed to turn optional recording/display failure into file-operation
failure. Actual type/format/I/O lifetimes remain part of executing the user's
program, not a hostile-input sandbox.
