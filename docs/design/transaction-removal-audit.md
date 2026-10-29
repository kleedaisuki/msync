# Transaction removal audit for the barrier execution model

## Scope and status

This is an independent migration checklist for the user-requested breaking
0.5 model, inspected on 2026-10-29 while parallel teams are editing the shared
worktree. It is not a completion claim. No Cargo checks were run by this audit.
The original request removes transaction syntax and implementation, not merely
its default use. Statement-local crash recovery is a separate storage concern.

The user confirmed that graceful cancellation must stop admitting work while
allowing already-started external programs to exit naturally and draining their
outputs. Arbitrary effects after msync SIGKILL or machine power loss are not
promised to be reversible.

## Removal boundaries and owners

| Surface | Required action | Responsible team |
| --- | --- | --- |
| `src/language/{mod,parser,writer}.rs`, `src/compiler/structured.rs`, `src/hir.rs`, `src/hir/graph.rs`, `src/pipeline.rs`, `src/relocatable.rs` | Remove transaction AST/HIR, isolation modes, named origin imports/exports, commit/rollback dispatch. Emit explicit directional barriers and macro-exit joins. | `barrier_frontend` |
| `src/hir/effects/namespace.rs` | Old Manager merge epoch and `transaction::ResourceKey` proof cannot remain as an unused compatibility facade. Remove or replace with a real current-block filesystem mechanism. | `barrier_frontend`, `block_specialization` |
| `src/execution/{mod,live,domain,frontier,events,materialize,transfer,physical,program,error}.rs` | Remove Manager/Store ownership, transaction diagnostics and counters, cohort epochs and hidden program view requirements. Interpreter observes only the current basic block. | `runtime_rebuild` |
| `src/execution/native.rs`, `src/io/windows.rs` | Retain actual path/alias safety, but remove Manager-specific policy claims and stale diagnostic wording. | `runtime_rebuild`, `statement_storage` |
| `src/process/view_linux.rs`, `src/process/view_windows.rs`, `src/process/windows_view/` | Delete obsolete bubblewrap/Sandboxie resource-view implementation, not just its module declaration. Preserve ordinary process-tree ownership without implicit child termination. | `runtime_rebuild` |
| `src/transaction/{store,manager}.rs`, `src/transaction/manager/`, `src/transaction/mod.rs` | Delete MVCC and user transaction implementation. Migrate reusable filesystem primitives to `src/filesystem.rs` / `src/filesystem/`, then delete the temporary old filesystem re-export. | `statement_storage`, `runtime_rebuild` |
| `src/lib.rs` | Export canonical filesystem/context modules; remove transaction/linker exports after callers migrate. | `statement_storage`, `artifact_context` |
| `src/preparation/`, `src/plan/` | Remove transaction/view-epoch assumptions. Preserve fresh block-local kind, identity, traversal, effect and replayability proofs. | `block_specialization` |
| `src/object/`, `src/context.rs` | Expanded, versioned typed IR; no transaction schema or stale filesystem facts, no source reparse. Pure lexical context substitution is not an eager filesystem linker. | `artifact_context` |
| `src/{main,structured,repl,presentation,doctor}.rs` | No transaction help, counters, rollback commands or optional sandbox dependency. Source/token diagnostics must identify compilation versus evaluation/execution accurately. | `cli_release_surface` |
| `.github/workflows/ci.yml`, `.github/workflows/windows-backend.yml`, `scripts/ci-install-sandboxie.ps1`, selector scripts | Remove deleted process-view targets, privileged backend provisioning and backend-only workflow/script. Preserve actual native child execution on ordinary Windows/Linux runners. | `barrier_validation` |
| `Cargo.toml`, `benches/store_v4.rs` | Delete exclusively MVCC benchmark and manifest target. Retain startup, planner, flow and macro latency measurements. | root |

## Tests: remove obsolete contracts, retain user journeys

### Exclusively obsolete

- `transaction_manager_v4.rs`, `transaction_store_v4.rs`: Manager/Store and isolation semantics.
- `review_metadata_v4.rs`: every test exercises named origin undo.
- `process_view_linux_v4.rs`, `process_view_windows_v4.rs`: native transaction views.
- `named_execution_v4.rs`: almost entirely named histories; migrate its ordinary
  REPL read-after-write test rather than losing that journey.

### Mixed suites requiring selective migration

| Suite | Preserve | Remove or replace |
| --- | --- | --- |
| `transaction_filesystem_v4.rs` | Snapshot identity, publication, compensation, native cleanup and metadata behavior through the new filesystem module. | Only obsolete module imports/names; do not delete the valuable storage tests wholesale. |
| `execution_v4.rs` | Typed macro tree copy; exact UTF-8/NUL literals; selector preflight/collisions; container merge; exact-directory refusal; alias/hardlink protection; cancellation; real HTTP/WSL; readonly/case behavior. | Named rollback/checkpoints, immutable transaction versions and whole-script native-capability preflight assumptions. |
| `execution_workspace_v4.rs` | No unrelated snapshots; active-flow sibling work; private scratch identity exclusion; user scratch preservation; cwd-as-source; FIFO/socket siblings. | Named origin undo and native private resource-view semantics. |
| `lowering_v4.rs` | Hygienic macro capture, typed program/namespace/port arguments, source-file/pwd context, path modes, exact argv and NUL rejection. | Transaction lowering, isolation enums, commit/rollback lexical-region assertions. Update process node indices for emitted barriers. |
| `hir_plan_v4.rs` | File/tree hazards; HTTP observable ordering; unknown-kind obligations; FIFO identities, rendezvous and waves; raw-path alias handling; graph properties. | Manager namespace epochs, native resource-view certificates, shared transaction epochs and origin-rollback schedules. New smaller graph tests must retain meaningful FIFO/HTTP regressions. |
| `pipeline_v4.rs` | Pure compilation, source offsets, atomic candidate bindings and cross-submission macro capture. | Replace the invalid transaction example with a genuinely invalid 0.5 port/type example. Delete resource-view premise certificate test. |
| `preparation_v4.rs` | Fresh aliases, raw traversal, readonly/kind evidence, guarded rewrite failure and finite resource proofs. | Checkpoints, native private import epochs, and assumptions that future blocks have already been interpreted. |
| `program_execution_v4.rs` | Empty submission creates no workspace; resource limits; large stderr/stdin; cross-REPL ports; native creation/deletion; connected children; scalar data. | Named histories, view export/import, private epochs, rollback cancellation and dependency on provisioned sandbox backends. |
| `macro_native_v4.rs` | Typed whole-program macro formal, captured destination and exact stdin/stdout bytes. | Isolation-mode loop and undo. Explicit async is needed when later statements feed child stdin. |
| `physical_execution_v4.rs` | TCP/FIFO/Unix socket directions, EOF only ends one flow, independent admission, FIFO aliases/waves, native stdin/exit integration. | Named transaction capability refusal. Native process journeys must no longer require a sandbox provision. |
| `endpoint_execution_v4.rs` | WSL upload/download/recursive replacement/conflict; readonly cleanup; HTTP destination refusal. | Named transaction branch only; preserve literal and transfer conflict cases. |
| `runtime_guards_v4.rs` | Raw traversal survives stale preview; cancellation does not poison next REPL input; stale target/source kind deoptimization. | Named history fence case and checkpoint counters. |
| `review_foundation_v4.rs` | Foreign shell edits remain visible; metadata-sensitive dead writes; external hardlink permissions not changed by cleanup. | Named rollback interaction only. |
| `submission_v4.rs` | Multiline pending/cancel/EOF/editor behavior and exact source offsets. | Replace transaction compound headers with macro headers. A quoted `transaction A:` remains an ordinary valid literal. |
| `telemetry.rs` | Real IO paths/stages/bytes, causal diagnostics, cancellation and cleanup observability. | Final two Manager-based cases must move to statement publication, not retain `TransactionId` for telemetry. |
| CLI / artifact / doctor suites | Pure compile, source/object runtime, safe formatting, colored help, token diagnostics and no optional dependency bootstrap. | Provider-required errors, named origins, old version/schema and checkpoint assertions. Coordinate with their owning teams. |

The legacy `tests/runtime.rs` suite does not use transaction types: preserve its
real copying, recursive merge, selectors, HTTP/WSL, stream dispatch, cancellation
and Windows long-path regressions. Occurrences of “transaction” referring only
to an unpublished sibling stage do not justify deleting the test.

## Performance and documentation

`benches/latency_v4.rs` mostly measures useful fresh-process, REPL and flow paths.
Only its named A/B literal workload needs conversion to disjoint ordinary
statements. `benches/planner_v4.rs` has no transaction types and should be adapted,
not discarded. Re-run a bounded real macro workload after correctness stabilizes;
older 0.4.1 measurements do not measure the rebuilt 0.5 binary.

Historical notes currently present themselves as current product contracts:
`architecture-v4.md`, `transactions-v4.md`, `transaction-dirty-publication-v4.md`,
`planning.md`, `runtime.md`, `validation.md` and the previous late-bound execution
note. In particular `runtime.md` says whole-directory expansion is not atomic,
which contradicts the new requested finite-statement guarantee. Preserve dated
historical evidence only under explicit historical status, and point the live
README/language/design path to the new block and statement-recovery contract.
Do not mechanically delete every word “commit”: storage publication still has
an internal durable decision point even though the DSL has no transactions.

## Exit evidence required

- No active AST/HIR/public API contains TransactionId/Isolation or DSL commit/rollback instructions.
- No reachable or merely orphaned MVCC/resource-view implementation remains.
- No CI job installs backend drivers or tries to execute deleted native-view tests.
- All-target discovery includes only compilable, relevant tests/benches.
- New runtime is tested with actual native programs and independently verified block order.
- Native cancellation drains already-started children; streams retain per-flow EOF behavior.
- Crash injection proves finite statement recovery at real publication boundaries.
- Source/object compilation is pure; contextualization does not inspect future filesystem state.

The checklist must be re-scanned after teams freeze source; transient findings
above are assignments, not claims that another team has finished resolving them.
