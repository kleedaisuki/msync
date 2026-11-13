# C# transfer journeys

Run the direct native local journeys:

```sh
dotnet build tests/Msync.Transfer.Tests -c Release \
  --artifacts-path artifacts/transfer-tests -m:1
dotnet artifacts/transfer-tests/bin/Msync.Transfer.Tests/release/Msync.Transfer.Tests.dll
```

Run the same implementation through real native OpenSSH, with connection sharing
explicitly disabled by the retained baseline fixture:

```sh
python3 tests/Msync.Transfer.Tests/ssh_journeys.py
```

The C# executable checks ordinary bytes and executable permissions, literal
replacement preserving target permissions, directory links and dangling links,
self-copy, descendant-copy input capture, ancestor tree replacement, and actual
mid-copy cancellation preserving the old target and removing the owned stage. With
`MSYNC_TRANSFER_REMOTE` selected, the remote journeys cover file/literal/tree
upload, tree download, same-endpoint copy, finite cohort upload/download,
trailing-newline/Unicode/backslash/quote filenames, incompatible parent failure,
and a successful ordinary retry, plus upload/self/ancestor tree replacement and
finite descendant copy. The suite also checks direct and recursive remote selectors,
no-match/no-write behavior, negative-lookahead regexes, single-channel metadata
predicates in deferred filter/reverse/take/transform chains, and endpoint-changing
path transforms. `take(0)` must neither resolve nor enumerate its source. The
current suite uses exactly 28 native SSH channels (including one channel per
endpoint for the cross-endpoint relay); all filesystem-predicate queries remain
on their already admitted selector channel. The Python owner checks actual client launches,
authentications, and server exec-session counts, and records output under `.temp`.
The root language acceptance suite separately exercises source compilation and
artifact execution; this focused executable isolates transfer semantics from
frontend syntax.

`ssh_journeys.py` reuses the typed fixture in
`tests/support/ssh_fixture.py` so transport and language journeys use the same host keys,
native alias configuration, process counting, and owned daemon lifetime. Product
code has no dependency on that fixture or Python. The current link/mode journeys
run on Unix; native Windows installation, filesystem behavior and WSL transport
are separate platform acceptance work.

Run the focused node-kind journey with `--node-kind-only`. It checks the Windows
name-surrogate tag contract on every host and ordinary recursive copies and
Unix links using real files. On Windows/NTFS it additionally creates a WOF file
with `compact.exe /c /exe:lzx`, verifies its reparse attribute, copies its bytes
directly and through an overlap snapshot, and checks junction status, traversal
and removal. A real provider-backed cloud directory remains separate platform
acceptance; a synthetic tag check is not a substitute for that provider journey.

## Prepared operations and finite cohorts

```sh
python3 tests/Msync.Transfer.Tests/ssh_journeys.py --batch-only
```

The prepared route executes ordinary `NativePlannedTransfer` operations through
`NativeHostIO.ExecutePlannedAsync`. It preserves per-item failure/continuation,
directory actions, Merge/Replace semantics, timestamps, narrow-buffer Unicode,
unusual names and exact file bytes. `NativeTransfers` is only the test's driver
for independent operations, not a product batch implementation.

Separate finite-cohort processes transfer 36 paths (files, a directory and a
dangling link) in one actual `NativeRange`. Upload, download and same-endpoint
copy each require exactly one native SSH client/authentication/exec session.
External connection sharing is disabled; all paths, link targets and binary
payloads are independently checked. Pass
`--test-dll /absolute/path/to/Msync.Transfer.Tests.dll` to reuse a coherent build.

`--relay-batch-only` retains the independent source/target failure, directory,
symbolic-link and opposite-direction relay oracles. Its separate finite relay
cohort requires exactly two clients/authentications/exec sessions: one per
configured endpoint, not one pair per member.

The retired `OpenSession`/`UseSession` tests asserted one explicit transport lease
across unrelated metadata and transfer statements. That implementation-specific
lease and its framing/owned-fd probes are not recreated. Channel-count acceptance
now follows the user contract: one finite transfer owns metadata and bytes on one
channel per endpoint. Missing-entry, alias, raw-spelling, source-kind and failure
diagnostics remain checked through native filesystem and transfer operations.

## Compiled CLI/OpenSSH integration

```sh
python3 tests/Msync.Transfer.Tests/cli_ssh_journeys.py /absolute/path/to/msync
```

This smaller end-to-end proof runs a deferred selector function and a deferred
recursive directory iterator, each followed by an ordinary typed native metadata
predicate and `ranges.take(2)`. Both programs run as source, `.mso`, and `.mexe`.
Each of the six executions must use exactly one actual SSH client,
authentication and exec session; compile and binding must use none. The fixture
changes source bytes between stages, runs the relocatable object from a new cwd,
and runs the bound executable from another cwd, checking both transferred bytes
and the respective context semantics. The command accepts managed apphosts and
published Native AOT executables without rebuilding or changing them.
