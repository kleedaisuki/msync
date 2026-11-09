# Source-independent Typed HIR lowering

This executable builds semantic HIR directly, writes and reads `.mhir`, then
lowers the loaded immutable graph to existing bytecode. Its diagnostic source
text is deliberately not a valid msync program. No parser, binder, source replay
or inference callback participates in execution.

Nine programs run at O0/O2/O3 through relocatable bytecode, serialized `.mso`
and serialized context-bound `.mexe`. They cover arithmetic, shared captures,
callee/argument order, branches, matches, value loops, iterator break, Scope
field mutation, named-self reassignment and generic identity/dispatch. Three
additional checks exercise two persistent HIR generations and the explicit
location/export layout. The accepted local run passed **84 contracts** with
zero compiler warnings.

```text
dotnet build tests/HirLowering/Msync.HirLowering.Tests.csproj -c Release \
  --artifacts-path .temp/hir-lowering-tests -m:1
dotnet .temp/hir-lowering-tests/bin/Msync.HirLowering.Tests/release/Msync.HirLowering.Tests.dll
```

Layout location indexing is linear in the declaration count, not a full scan
per function. Value-type slot keys avoid a heap allocation on each lookup.
Normal compilation can pass `RetainLayout: false` to discard the immutable
layout receipt; persistent callers retain explicit forward and reverse maps.
