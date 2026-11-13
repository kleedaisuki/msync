# Endpoint path projection

Run the focused contract executable:

```sh
dotnet run --project tests/PathProjection/Msync.PathProjection.Tests.csproj \
  -c Release --artifacts-path artifacts/path-projection -p:RestoreLockedMode=true
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

It is an actual format-1 object with source name `-`, not a modified current
header. The current decoder rejects it with the original format and explicit
recompilation guidance. Every newly written object uses the current
`NativeArtifact.FormatVersion` and retains optional local spelling through allocated namespace fields and captured environments. Transient fixtures stay below root `.temp`.

The accepted-value editor journey uses the real VM and persistent compiler:

```sh
dotnet run --project tests/PathProjection/Msync.PathProjection.Tests.csproj -- --editor-session
```

It changes the process cwd and home after accepting Path values, Scope fields
and closures. Both old/new directories contain discriminating entries. The
editor resolves completion facts from actual initialized values, including a
closure returned from a generic factory, while preserving the original captured
projection. Subsequent real generic calls prove editor probes do not mutate live
specialization identities; an earlier analysis retains its frozen signatures.
The callable portion additionally runs O0/O2 with and without runtime feedback
on acceptance: repeated incompatible editor replacements report E0213 at the
replacement function, a failed real compile leaves the accepted closure intact,
and a compatible real replacement changes the result through that closure.
Consumed top-level scalar calls and independent generic specializations remain
free to change their result type; they do not inherit a retained-body protocol.

`--process-members` runs actual native children with exit status 0 and 7 and
checks compiled `stdout`, `stderr`, `exit_code` and Bool `success` properties
against the canonical standard-library result. It uses `/bin/sh` on Unix and
`ComSpec` on Windows; no simulated Process value substitutes for the child.

The default journey also executes native format-1 artifacts whose lexical result is
consumed by a String comparison. It covers both implicit conversion and
explicit cast, endpoint-specific backslash/trailing-separator behavior,
unbound Windows projection, and supplementary-plane scalar ordering. Logical
instruction sites and the lexical operation's actual native-call counter must remain
available even when neither intermediate value is allocated.

The direct owned-result variant uses the same endpoint grammar and verifies
the complete result Path (not just its display), including captured cwd and
home, unchanged remote endpoint, raw/native spelling differences and absent
duplicate projection. Direct String inputs acquire the active binding
context. Both variants require the same original instruction counts.
