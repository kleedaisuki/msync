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
self-copy, descendant-copy input capture, and ancestor tree replacement. With
`MSYNC_TRANSFER_REMOTE` selected, fifteen remote operations cover file/literal/tree
upload, tree download, same-endpoint copy, finite cohort upload/download,
trailing-newline/Unicode/backslash/quote filenames, incompatible parent failure,
and a successful ordinary retry, plus upload/self/ancestor tree replacement and
finite descendant copy. The Python owner checks actual client launches,
authentications, and server exec-session counts, and records output under `.temp`.
The root language acceptance suite separately exercises source compilation and
artifact execution; this focused executable isolates transfer semantics from
frontend syntax.

`ssh_journeys.py` reuses the existing typed fixture in
`legacy/benches/ssh_v065.py` so both implementations use the same host keys,
native alias configuration, process counting, and owned daemon lifetime. Product
code has no dependency on that fixture or Python. The current link/mode journeys
run on Unix; native Windows installation, filesystem behavior and WSL transport
are separate platform acceptance work.
