# Contextual transfer batches and native channel ownership

## Execution contract

`TransferEngine.CopyBatchAsync` accepts an independent frontier selected by the
contextual backend. Each `TransferBatchItem` carries its source, destination and
prepared root operation kind. It returns one `TransferBatchOutcome` per item,
including concrete operational exceptions. An item failure does not discard
completed members or prevent later independent members from running. Cancellation
still stops the owned batch. The transport does not construct dependency graphs,
validate snapshots or decide that a plan is stale.

Plans also carry `TransferDestinationMode`: Exact rejects an existing real
directory at the final leaf, Merge installs a directory into that final leaf
while retaining other members, and Replace replaces that final root. Preparation
has already resolved container insertion to its concrete destination, so the
transport must not append another source basename or infer the mode from that
concrete spelling. Direct engine callers can still use the ordinary `/` and
`/.` source spellings; prepared operations retain their explicit mode.

The VM owns the global jobs and payload permits. It partitions an independent
local frontier into worker chunks; each chunk uses one `Task.Run` and one rented
buffer, not a task and buffer per file. File and literal publication retains an
owned same-directory stage, atomic replacement and at most one file fsync.
Literal UTF-8 conversion uses the same buffer incrementally, retaining complete
Unicode scalars even with a configured buffer narrower than one scalar. Windows
native file copies do not rent the Unix copy buffer unless the batch needs it
for literal encoding.

Prepared File/Directory/Link operations select their root execution path without
changing kind after another filesystem observation. Mode/time capture and
recursive acquisition of actual data are still execution work. Existing
same-file and ancestor-overlap behavior remains part of the copy operation,
including finite input capture when the output overlaps a directory source.
There are no rollback registries or background cleanup scans.

Expanded directory graphs use two nonrecursive primitives. `EnsureDirectory`
creates or replaces only its final root; a Path source is checked as a directory,
while a Null source denotes the root of an explicitly nonempty replacement
cohort. `ApplyDirectoryMetadata` copies only directory mode and nanosecond mtime
after the selected children complete. Local execution uses native metadata and
mkdir; remote execution uses metadata-only commands on the retained lease, not
another tar tree or recursive copy. The VM blocks a metadata finalizer whose
children failed, leaving its outcome `Executed=false` rather than producing a
second I/O error or sealing an incomplete target's permissions before retry.

## One selected native session

`TransferEngine.OpenSession` creates a runtime-only
`TransferEngine.RemoteTransferSession`. Its lifetime belongs to the selected
backend epoch and its finite execution work. `UseSession` is an async-local
scope for existing native metadata calls: `RunCommandAsync` routes them onto
the same lease, and the later upload/download/same-endpoint-copy batch uses that
lease's process. No lease, handle, credentials or mutable transport state is
serialized in `.mexe`. Export-only preparation can close its metadata lease;
replaying the artifact naturally obtains an execution lease.

The session retains the platform native `ssh` or `wsl.exe` client rather than
adding another SSH library, authentication cache, ControlMaster startup process
or global pool. Aliases, agent selection, host-key behavior and prompts continue
to use the native client's existing configuration. An older selection operation
with its own active query channel retains priority over the broader epoch lease.
Only an actually selected endpoint opens a client. Contextual metadata discovery
inside such a selection borrows that query channel through
`ReadSelectedDirectoryMetadataAsync`; it does not create an idle batch lease.
Both query and batch paths use the same directory program and row parser, with
owned-directory identity pruning enabled only when its retained descriptor is
actually exported.

The remote coordinator retains an open directory descriptor for its own scratch
identity. Leased discovery prunes that identity with `find -samefile`, including
when the selected root is the owned directory itself. It does not hide directory
names matching `.msync-*`: an unrelated user directory with that spelling remains
visible. Native directory iterators use the same inherited descriptor when they
run on a lease, and explicit root links retain the normal `-H` behavior.

`ReadDirectoryMetadataAsync` obtains type and raw-name records with one native
`find` and one canonical directory root. A selected regex Range therefore warms
the backend's type and namespace-entry facts without a query or process per
member. `ReadMetadataBatchAsync` streams arbitrary selected paths in one command,
without putting the entire list in an exec argv. Its namespace-only form skips
unused kind acquisition and reuses a repeated parent's canonical spelling.
Final link entries remain link entries; only their parent is canonicalized.
Explicit directory acquisition follows its selected root link, while recursive
discovery does not follow descendant links.

`IsActive` describes the transport resource, not plan validity. A native command
which reports an ordinary failure leaves a healthy channel reusable. Framing,
transport and cancellation failures abort that channel; the next explicit work
admission can retire it and acquire a new one. The failed operation is not
automatically retried or replanned.

One endpoint group executes three transfer directions through one exec channel:

* Local Path or literal to a remote destination.
* Remote Path to a local destination.
* Remote Path to another destination on the same endpoint.

Mixed direction frontiers are grouped before acquiring the session command gate.
Cross-endpoint scalar frontiers borrow one lease per endpoint. Their source
download frames are forwarded directly into the target upload frames with one
pooled payload buffer. The target decodes the private wire names before atomic
installation; there is no intermediate payload file or complete-file buffer.
Both command gates use the same connection-kind/address order, so simultaneous
opposite-direction relays cannot acquire an A/B versus B/A lock cycle. This
serializes only work which shares those channel resources, not every endpoint.
Generic deferred Range operations retain their existing operation implementation;
the independent scalar frontier is the batching unit, not a new implicit
source-language loop.

## Framing and backpressure

The remote command loop supports metadata queries and finite transfer batches.
Path and script arguments are encoded as UTF-8 octal lines, so spaces, quotes,
backslashes and literal newlines do not become shell syntax. Responses carry
operation indices, status and exact byte lengths; query stdout and diagnostic
bytes are not split on their contents.

Uploads pipeline item headers, tar payloads and producer-status trailers. GNU
tar uses one-record blocks, so each payload ends at exactly its two zero records
without consuming the next command. The source trailer prevents an incomplete
local directory acquisition from being silently published as a successful
upload. Every payload extracts into an owned stage in its destination's parent;
there is no shared temporary payload spool or cross-filesystem rename.

An untouched [.NET TarWriter](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Formats.Tar/src/System/Formats/Tar/TarWriter.cs)
emits no final records. A source failure before the first entry therefore writes
one private failed-source entry before disposing the writer. Its failure trailer
still forbids publication. This keeps the next item out of the failed archive
without adding a metadata entry or padding cost to successful uploads.
An exception while tar is writing an entry is different: its declared payload
may be incomplete, so no marker is appended into that data. The channel is
aborted, already reported prefix results are retained, and remaining members
report failure. This distinction is made at actual `WriteEntryAsync` failures,
without pre-reading files, spooling payloads or adding normal-path callbacks.

Downloads similarly frame one tar payload and its producer completion per item.
The .NET tar reader consumes the first end record; the batch reader consumes the
second before reading the next status. If installing an item fails locally,
the reader drains that item through its owned tar parser before proceeding.
The source and target therefore do not lose framing after an ordinary failure.
GNU tar receives `--no-unquote`, including ordinary download and selection paths:
its default argument unquoting would otherwise reinterpret a literal POSIX
backslash followed by `r`, `n` or `t` even when the shell argument was quoted.

The ordinary download journey also exposes a .NET 10 PAX parser defect: it uses
the first LF instead of the length field to delimit a PAX value. A legal name
combining a newline and non-ASCII characters therefore fails when GNU tar emits
a `path` attribute. Remote-produced archives use GNU tar's existing transforms
to encode `%` as `%25`, then LF as `%0A`, in entry and link names. Local readers
restore LF first and `%` second; relay extraction uses the inverse transform.
This ordering preserves a user's literal `%0A`. Local upload archives remain
raw. No user filesystem name is changed, metadata precision is retained and
there is no second archive parser, payload spool or extra filesystem pass.
Metadata and bytes share a retained session, rather than one authenticated
helper per field or file. Producer and response-consumer tasks run concurrently;
there is no per-file network round trip or cyclic pipe backpressure.

The framing follows the actual [.NET TarReader implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Formats.Tar/src/System/Formats/Tar/TarHeader.Read.cs)
and the existing GNU tar/atomic publication implementation used by the transport.
The shell protocol's command/query and all three data directions are exercised
by a cheap local pipe probe before the real OpenSSH journey; that probe is not
evidence of authentication or native connection reuse.

## Focused verification

`tests/Msync.Transfer.Tests/BatchChecks.cs` covers actual local and selected
remote copies, literal encoding across narrow buffers, an operation whose source
kind changed, directory/container placement, dangling links, Unicode/spaced/
quoted/long and POSIX newline/backslash names, and per-item failures followed by
successful independent work. The remote journey keeps one lease across metadata,
uploads, downloads, same-endpoint copies and another metadata request.

Run the existing typed real OpenSSH owner with `--batch-only` and a coherently
built test DLL. Its fixture disables external connection sharing and checks
actual native client launches, server authentications and exec sessions. A
passing batch journey requires exactly one of each. Product code does not depend
on Python or that fixture. The pre-existing direct engine journeys separately
cover ordinary overlap, cancellation and transfer modes affected by the shared
archive installation helper.

The first complete affected-contract result uses immutable managed producer 4
(Runtime SHA256
`587c19ba80656badc1c46361202084235f2ecf34e8f8b5e688548cc2d2d8cdbe`).
The test project references those exact product DLLs without rebuilding them.
On Linux, the real native OpenSSH batch journey passes 159 checks with exactly
one client, one authentication and one exec session. The pre-existing direct
engine journey also passes its 28 expected channel uses, including source/target
overlap, actual cancellation, selector metadata callbacks, ordinary failure and
retry, and the strengthened combined newline/Unicode/backslash/literal-percent
filename. The fixture explicitly disables external connection sharing. Reports
are retained under `.temp/transfer-ssh/s-7hg_p_6g/result.json` and
`.temp/transfer-ssh/s-f5fqs396/result.json`. These are functional/channel-topology
observations of a managed product with a real native SSH client, not Native AOT
or throughput measurements and not proof of every compiler/VM plan route.

The two-endpoint follow-up uses immutable producer 5, Runtime SHA256
`327aa67b6de8efe36a4003fd4712a46ab60b30d51cbb0d3d34e8dbdc36d6e239`.
Its new relay journey passes 45 checks with exactly two native clients,
authentications and exec sessions across metadata, 34 scalar files, a missing
source and obstructed destination, directory/link payloads, simultaneous
opposite-direction disjoint relays, and later metadata queries on both leases.
The two aliases address the same owned loopback server with separate source and
target trees; this proves channel ownership and gate ordering, not WAN throughput
or distinct-server performance. The full report is retained at
`.temp/transfer-ssh/s-usxz1x2m/result.json`.

The selected-filter integration follow-up uses immutable producer 7, Runtime
SHA256 `ae45a510ae98abbec726c506cb20ba15e90f7419ebf3e5d4689764bb8ee922ab`.
An actual source function returning a remote lazy directory iterator, filtered
by source callbacks invoking native file metadata and then transferred, produces
the two expected files with exactly one native client, authentication and exec
session. Lazy directory constructors do not admit an idle backend lease;
metadata callbacks borrow the selected operation's channel. The strict report
is retained at `.temp/transfer-batch/filter-source/s-jxoy2yzl/result.json`.
