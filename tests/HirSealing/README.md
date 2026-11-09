# Semantic HIR sealing contracts

`HirSealer` removes semantic-construction remnants before a module crosses the
analysis boundary. This is arena reachability, not dead-code optimization:
all finalized functions and all bound generic templates remain roots, even when
not called. Value captures and match arm bodies add edges beyond `Node.Children`.

Source, type, location, function, template, layout, signature, concept and import
identities remain stable. Nodes, child edges, literals and executable payload
arenas are compacted in ascending original identity order. `Seal` avoids the
optional old-to-new node map; `Compact` returns it for consumers holding node
identities. Discarded nodes map to `HirNodeId.None`. Sealing an already compact
module returns its original instance without rebuilding immutable arenas.

The fixture checks unused generic definitions, capture ownership, match patterns,
every executable payload arena, literal remapping, stable semantic identities,
invalid live references and exact bytecode equivalence after pure lowering. It
also checks that sealing twice leaves identical artifact bytes. Abandoned binding
nodes are intentionally not validated: final artifact validation checks the
retained semantic representation, not the compiler's discarded query workspace.

Run from the repository root:

```text
dotnet run --project tests/HirSealing/Msync.HirSealing.Tests.csproj \
  -c Release --artifacts-path .temp/hir-artifacts/sealing-build
```

An optional current-schema `.mhir` argument additionally compares source-free
lowering before and after sealing and reports retained node counts. Files from
earlier unpublished schema snapshots must be regenerated rather than treated as
an older released format.
