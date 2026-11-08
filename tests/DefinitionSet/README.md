# Reaching-definition value sets

Run the focused internal representation oracle:

```sh
dotnet run --project tests/DefinitionSet -c Release
```

The project compiles the product's internal `DefinitionSet.cs` directly rather
than opening a public API just for testing. A seeded `HashSet<int>` oracle checks
union, membership, signed token boundaries, commutativity, associativity,
idempotence and immutable operand snapshots. Empty/singleton and unchanged
multi-definition unions must allocate no managed heap after warmup; there is no
wall-clock threshold or CI publication gate.

The representation is a readonly value containing an empty/singleton token or a
private sorted array for genuine control-flow joins. Arrays are never mutated
or exposed. A union returns an existing operand if it already contains the
other, otherwise a fresh exact sorted array. Consequently block-entry state and
node-input snapshots copy values, not a heap set per stack operand/local slot.

End-to-end dataflow semantics are checked separately through the production
optimizer by `tests/OptimizationGraph`, including stack phi consumption,
short-circuit expressions, captured mutable aliases and field storage.
