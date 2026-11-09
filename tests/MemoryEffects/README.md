# Sparse interprocedural VM memory effects

Run from the repository root:

```sh
dotnet run --project tests/MemoryEffects
```

The probe checks parameter/capture field footprints, nested aliases, raw captured
cell rebinding, closure retention through returned function aliases, recursive
and loop access-path convergence, private containment cycles, native callback
execution, and deferred creation versus enumeration. It additionally compiles,
serializes, binds and executes an actual branch/call/field file-transfer journey
at O0/O2/O3, verifying returned Scope shape and binary file bytes.

## Summary contract

`MemoryEffects.Analyze(Module)` returns one `FunctionMemoryEffects` per function.
The analysis describes source-VM memory, not filesystem effects or task ownership.
`EffectAnalysis` remains authoritative for failures, divergence and scheduling.

Each `MemoryAccess` has a Capture/Parameter root, a logical field path, and an
optional descendant wildcard. Paths refer to object identities reachable on
**function entry**. A caller resolves those paths against its pre-call aliases
before applying writes. An empty Capture write changes that captured **cell**;
it does not mutate the object previously stored in it. A field write can also
reach a shared raw cell embedded in a Scope field; both aliases remain visible.
Parameters are values, so rebinding a parameter's private location is not a
write to the caller's binding.

- `Reads` / `Writes` are sparse may-access sets. They do not claim every path
  must execute and must not alone justify deleting a preceding caller store.
- `Escapes` identifies retained input identities, including captures of a
  returned/stored closure. Returned argument aliases are tracked separately in
  `ReturnAliases`; simply returning an argument does not itself retain it.
- `UnknownReads` / `UnknownWrites` describe opaque VM access. A native filesystem
  read is not an opaque Scope mutation merely because it performs I/O.
- `Enumerates` identifies values whose actual consumption can execute deferred
  source callbacks. It is conditional: a caller proving Path/String or an eager,
  callback-free range need not clobber VM memory. An unknown Range still can.
- `HasDeferredInvocation` records a retained/constructed deferred capability,
  not an immediate write. `ReturnsCallbackFreeRange` additionally distinguishes
  filesystem path selection and callback-free native ranges.

Actual native callback terminals apply known callback summaries. Pure adapter
creation retains aliases but does not execute callbacks. Function values use
the actual known closure/native identity; `Call.B` by itself does not prove a
nonnative callee. Unknown indirect calls keep opaque effects.

## Convergence and cost

Only block-entry states are copied. Function changes queue actual callers.
Recursive SCC edges that grow a rooted access path widen it to a subtree;
loop backedges similarly widen growing pointer paths. This is a finite semantic
domain, not a field-depth or iteration quota. Containment traversal follows
allocation identities and raw cells, never repeated syntactic field suffixes.

Empty/singleton set comparisons avoid hashing; larger summaries compare through
hash sets rather than quadratic list membership. Literal and closed-scalar
helpers need no CFG state. Ordinary modules without a field-memory optimization
client do not request this analysis from the optimizer driver.

The design follows the separation between alias/ModRef queries and memory
versions in [LLVM Alias Analysis](https://llvm.org/docs/AliasAnalysis.html) and
[MemorySSA](https://llvm.org/docs/MemorySSA.html), without importing C undefined
behavior, byte-address alias assumptions, or a general compiler framework.
