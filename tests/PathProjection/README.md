# Endpoint path projection

Run the focused contract executable:

```sh
dotnet run --project tests/PathProjection/Msync.PathProjection.Tests.csproj \
  --artifacts-path .temp/build-path-projection -m:1 -nr:false
```

It checks Windows join grammar explicitly even on Linux, then exercises the real
host filesystem and VM/artifact APIs. The core distinction is that symbolic Raw
spelling survives a binding until remote qualification, while local native
spelling drives Path-to-String, regex and physical I/O. Literal POSIX backslashes
are data, not a signal to rewrite an entire pathname.

`Fixtures/path-v1.mso` was compiled from standard input by the frozen original
0.8 Native AOT executable (SHA-256
`ca5c2eb2aebe1397f4ec3a745a6e76dcfcf598723ba1046523f183012263f38b`), using:

```text
let file = ./legacy-input; file
```

It is an actual format-1 object with source name `-`, not a version-2 artifact
whose header was edited. The executable reads it with the current versioned
decoder; every newly written object uses format 2 and retains optional local
spelling through nested values. Transient fixtures stay below root `.temp`.
