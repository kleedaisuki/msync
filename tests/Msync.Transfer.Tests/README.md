# C# transfer journeys

Run the direct local engine journeys:

```sh
dotnet run --project tests/Msync.Transfer.Tests/Msync.Transfer.Tests.csproj \
  --artifacts-path .temp/build-transfer-tests -m:1 -nr:false
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

## Contextual transfer batches

```sh
python3 tests/Msync.Transfer.Tests/ssh_journeys.py --batch-only
```

The batch route keeps one selected native session through metadata queries,
uploads, downloads and same-endpoint copies. It verifies per-item failure and
continuation, directory/link and unusual-name behavior, exact output bytes and
one actual client/authentication/exec session with external sharing disabled.
The local cases also exercise shared-buffer UTF-8 encoding and prepared-kind
execution. Pass `--test-dll /absolute/path/to/Msync.Transfer.Tests.dll` to reuse
an already built coherent producer. See
[batch ownership and framing](../../docs/design/transport-batches.md).

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
