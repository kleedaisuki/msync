# Native activation-frame allocation acceptance

Run from the repository:

```text
dotnet run --project tests/NativeAllocationV090
dotnet run --project tests/NativeAllocationV090 -c Release -- --measure
```

The default journey compares actual `Compiler.CompileNative` O0/O2 source and
detached artifact execution against independent numeric oracles. It covers mixed
aliases, finite layout projections, repeated loop allocation, nested returned
captures, all builtin fields, and a real `/bin/sh` Process field on Unix. Typed
composition properties cover all eleven native kinds, empty/Null freshness,
and unproved dynamic/cyclic placement hints. A gated source transfer checks
pending field futures through a deferred Range against actual published file
bytes. Owned-memory Range versus copied snapshot controls distinguish escaping
backing storage from private source storage. Profiles assert actual selected
frame allocations, not only optimization plans, and preserve logical payload
byte/element accounting across heap/frame controls. Index and snapshot bounds
checks cover original Scope and register-frame boundaries with original source
diagnostics, including detached artifacts.

The opt-in measurement compares identical optimized IR with Frame plans enabled
and demoted to LogicalHeap. It measures process-local managed bytes, not wall
time. Each route is warmed for 32 executions; five alternating trials run 200
executions each, checking the result every time. Image construction and disposal
are outside the byte-count interval. See the actual-coallocation section in
`docs/design/native-model-v090.md` for the implementation contract and receipt.
