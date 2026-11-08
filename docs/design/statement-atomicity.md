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

## C# v0.8 implementation

`Msync.Runtime.TransferEngine` implements the same retry contract without calling
the retained Rust binary. Ordinary local files use an owned same-parent stage,
stream through the configured buffer, apply metadata, flush once, and replace
with `File.Move(overwrite: true)`. Literal writes preserve an existing target's
permissions. Directory entries merge, symlinks retain their raw targets, and
completed entries are not rolled back. A proven same-node transfer is a no-op;
only actual tree overlap retains a temporary input copy before publication.
This avoids both self-destructive `tree -> tree/.` and recursively discovering a
new destination when a tree is copied into one of its descendants. Snapshot
scratch belongs to the live operation and does not become a recovery registry.

The native transport uses one selected `ssh` or `wsl.exe` process. Uploads use
[`System.Formats.Tar`](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar?view=net-10.0)
PAX entries rather than requiring a client tar executable. GNU tar and small POSIX
shell helpers carry metadata and bytes over the same channel; the helpers install
ordinary files by rename and retain unrelated directory children. The producer's
exit status and an upload completion marker must both succeed. No compile/check,
artifact binding, or unselected branch starts these helpers. Native OpenSSH owns
host aliases, agent/key handling, host verification and prompts; the existing
`MSYNC_SSH_CONFIG` selection is passed through as `ssh -F`.

Path data is always passed as literal argv, not interpolated into shell source.
Shell command substitutions use a non-newline sentinel when preserving path
values: ordinary Unix filenames ending in newlines are not silently truncated.
Archive entries are consumed sequentially before advancing `TarReader`, following
its documented streaming lifetime. Native process execution reuses the same
transport argument builder, including port/config handling and remote home
expansion, instead of introducing a second interpretation of connection values.

`tests/Msync.Transfer.Tests` contains focused engine journeys;
`ssh_journeys.py` reuses the real loopback OpenSSH fixture kept with the Rust
baseline. The accepted C# run completed fifteen remote operations with
exactly fifteen ssh clients, fifteen public-key authentications, fifteen server exec
sessions and zero scp clients, with ControlMaster disabled. It checked ordinary
upload, literal overwrite, symlink/dangling-link trees, download, same-endpoint
copy, finite cohorts in both directions, a trailing-newline/Unicode/quote filename,
a genuine incompatible-parent failure followed by retry, and actual upload/self/ancestor
tree replacement plus finite descendant-tree copy. Overlapping remote tree copies
retain their input beside the source tree, outside the selected source, rather
than recursively capturing their newly created output. This is protocol and
Linux filesystem evidence, not a Windows/WSL or WAN performance measurement.


### Selected range discovery and native queries

A runtime `PathSelection` descriptor retains the source directory, exact .NET
regex, recursive traversal flag, and composed range adaptors; it does not contain
source-language syntax. A deferred source keeps a demand-time resolver, so its
function is evaluated once when the transfer is selected. `take(0)` removes the
demand rather than opening the source endpoint and discarding its answer.

One duplex native session emits NUL-separated relative path names. The client
sorts names and evaluates the actual .NET regex and typed callback chain, then
sends literal selected paths as octal-encoded protocol lines. The same process
produces the archive or performs same-endpoint installation. Recursive directory
iteration uses the same mechanism, preserving descendant paths until ordinary
cohort basename collision preflight. No-match selection does not create a target
or authenticate an unused destination endpoint.

A callback's selected remote filesystem queries use a scoped, endpoint-matching
`AsyncLocal` transport owner while the discovery session awaits final selection.
The helper executes the same registered native shell operation and returns its
status plus byte-count-framed output; the typed callback still executes in the
bytecode VM. Native filesystem predicates therefore do not introduce separately
authenticated helpers or a source interpreter. Other endpoint operations retain
their own actual selected transport ownership. Transformations that produce
paths outside the original endpoint finish discovery and dispatch their real
resulting values normally instead of treating endpoint provenance as a rejection
policy.

The expanded encrypted OpenSSH suite passed with **28 clients, 28 public-key
authentications, 28 exec sessions and zero scp clients**. This includes recursive
selection, a deferred metadata filter followed by reverse/take/transform,
negative-lookahead regexes, no-match and `take(0)` non-admission, a same-endpoint
path transformation to another directory, a transformation to a captured local
path, and collision detection before target creation. Multiple filesystem
predicate calls remain inside the one selected source session. The C# local
journey also cancels a real large-file copy after observing its owned stage,
checks unchanged prior target bytes, and checks cleanup of that live stage.
