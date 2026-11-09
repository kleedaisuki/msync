# Structured semantic flow journeys

These programs exercise the production source binder, semantic fixed point,
finalized HIR, bytecode lowerer, artifact reader and interpreter. Each selected
program runs at O0 and O2. This complements the lattice properties in
`tests/HirFlow`; the latter alone does not establish language behavior.

The cases distinguish first-iteration values from later iterations: Boolean
updates, nullable Scope initialization, different structural rows, mutable
callable dispatch, and field writes through aliases and nested Scopes. Semantic
construction happens once after the bound-HIR graph reaches its fixed point.

```sh
dotnet run --project tests/HirFlowIntegration/Msync.HirFlowIntegration.Tests.csproj \
  -c Release --artifacts-path .temp/hir-flow-integration
```

For a changed contract, `-- --case 'source substring'` selects matching programs
without changing the default suite. For example, `--case 'let state = scope'`
selects the three direct, aliased and nested mutable-field journeys.

Type-valued loop variables carry finite canonical denotation sets, independently
of their runtime `Type` kind. Dynamic `is` targets remain evaluated expressions;
only a statically unique target supplies an ordinary value-type refinement.
Unresolved pure projection is intersected with the binding's fixed storage
constraint, rather than turning an established Number into an apparent nullable
value. Uninitialized standard-module cells are unassigned facts, not complete
Scope schemas.

Logical binding cells and object fields have different invalidation domains.
A known field write changes its field cell, not every private local that happens
to reference a Scope. Unknown calls conservatively invalidate mutable captured
cells and memory regions. Neither a branch nor a type query clones a live
compiler graph, emits speculative instructions, or rebinds syntax.

Indexed-resource joins are separate from ordinary binding-type joins. Element
reads join possible values; writes must satisfy every surviving carrier
constraint. The focused cases use a runtime-unknown pure Boolean to exercise
both branches, including through `decltype`, and reject a write accepted by
only one possible carrier. Branch-local overlays are gathered before updating
the destination, so an untouched branch cannot accidentally read an already
merged parent value.

Repeated allocation can describe an unbounded nested structural row even though
the declared storage contract is finite (`value = scope { item: value }`). The
projector recognizes this recurrence at the same bound construction site. Only
the recursive field loses nested shape depth; its runtime kind, null admission,
outer row and unrelated fields remain. This is structural widening, not an
iteration-count cutoff. Ordinary nullable initialization and finite shape
alternatives retain their member information.
