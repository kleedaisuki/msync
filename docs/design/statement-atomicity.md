# Ordinary staging, replacement and retry

## Current user contract (2026-11-01)

The user explicitly replaced the former recoverable-atomic-statement contract.
This is a normal single-user file synchronization tool. A failed copy can be
retried; successful copies must not pay for a persistent transaction/recovery
protocol. The historical protocol is retained as history in
[durable publication](durable-publication.md), not as current requirements.

| Operation | Current behavior |
| --- | --- |
| Ordinary file | Write a uniquely owned sibling temporary file, apply selected metadata, close/sync that file at most once, then use native atomic replacement. No before-image or parent-directory flush protocol. |
| Directory container | Merge selected entries, replace same-name ordinary files, retain unrelated children. Repeating the transfer updates the same entries normally. |
| Explicit tree replacement (`/.`) | Replace the selected tree. Directory replacement is not an OS-wide atomic transaction and may be incomplete if interrupted. |
| Finite range/cohort | One selected operation with deterministic source order, not persistent all-or-nothing publication. Completed members remain when a later member fails. Retry the command. |
| Remote finite transfer | One native SSH exec per endpoint carries discovery, metadata, bytes and completion. Rust tar coding on the client and GNU tar/cp/mv on the server; no client-side tar executable is needed on Windows. |
| Empty script/declaration | No storage initialization, registry scan, remote connection or recovery prerequisite. |
| Failure or disconnect | Return the actual operation/OS error. Some selected entries may already be new; retry normally. No commit-receipt query or manual recovery ritual. |

The remote helper currently closes the staged file and replaces it without an
explicit file fsync; the local ordinary-file path uses one file fsync. There is
no filesystem-wide syncfs, multi-directory persistence fence or universal
power-loss promise. The live client requires a successful completed archive and
helper result before reporting success; failure is not converted into success.

A forced termination can leave an owned temporary file/directory. A later
execution does not interpret old paths as cleanup authority or make new work
wait for them. Existing legacy `.msync-recovery-v1` and remote registry entries
are left alone, including malformed or group-readable state. Neither their
presence nor a disconnected old endpoint blocks a new local copy.

## Ownership and normal filesystem semantics

The live owner cleans its own stage. Unique creation and actual file-descriptor
lifetimes remain ordinary resource management, not a sandbox. Cleanup never
scans similarly named user paths or chmods regular files that may have external
hard-link aliases. A parent namespace timestamp changes normally; it is not
restored to a fabricated old value after a scratch operation.

Selected ancestor symlinks resolve according to the native filesystem, including
`alias/../file`. Recursive link entries are preserved as links, including dangling
links and cycles, instead of rejecting the entire tree or recursively following
them. Windows case-sensitive directories are not blanket-refused. Positive
same-object proofs use actual identity or exact canonical names; a possible
case-insensitive alias may add ordering but cannot erase a distinct `a`/`A` write.
The kernel decides actual permissions, not an owner-write-bit precheck.

A retained native source descriptor survives this tool's atomic pathname
replacement, so ordinary released file flows do not need a full source snapshot
plus a private hardlink merely to retain their own selected input. This is not
snapshot isolation against another program changing that inode in place.
Complex logical composition can still retain a captured version where the
program actually reuses it; one-consumer copies and unrelated destination data
must not be cloned simply to defend against hypothetical interference.

## Implementation ownership

- `execution::ordinary` provides direct local file/literal and selected tree
  merge paths. Snapshot composition is reserved for actual overlapping/fan-out
  semantics, not mandatory acquisition of every copy.
- `io::StagedFile` owns a same-parent stage and native replacement.
- `runtime::ssh_channel` owns one actual transport child, archive I/O and helper
  completion. A worker-side local error closes/joins that child instead of
  waiting forever for a remote producer that can no longer finish.
- `execution::endpoint` dispatches remote copies without ALLOCATE/PUBLISH/SETTLE
  clients, journals, before-images or independent recovery records.
- `filesystem::durable` and `filesystem::recovery` contain only narrow source-API
  compatibility adapters. Runtime admission does not use them and they create
  no persistent state, locks or flushes.

`AGENTS.md` records the superseding engineering requirement. The
[normal-user validation record](normal-user-validation.md) maps old crash/
registry tests to actual copy, overwrite, retry, native path and one-channel SSH
journeys. The [cost audit](safety-cost-audit.md) retains why the old design was
removed. Published v0.6.5 artifacts and historical measurements are unchanged.
