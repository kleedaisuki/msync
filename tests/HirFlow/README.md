# Semantic control-flow lattice

This focused executable exercises the new HIR semantic-flow layer without the
old compiler, syntax tree, bytecode emitter, optimizer or interpreter. It links
the actual flow source and the Core/HIR projects, so a successful run cannot be
produced by an alternate bytecode execution path.

The current checks cover immediate versus deferred read domains, stable private
locations, externally writable captures, field-alias region invalidation,
unreachable edges, Boolean facts, must-assignment joins and loop convergence.
Deterministic generated triples verify join idempotence, commutativity and
associativity. Structured HIR examples build an `If` inside a `Loop` and a `For`
whose source cell is reassigned: the iterator keeps the originally evaluated
sequence rather than rereading the changed cell for its element type.

Structural checks also cover nullable wrappers around alternative Scope rows,
finite represented-Type domains, and fixed-storage intersections. Null belongs
to the wrapper even when each structural alternative is itself non-null. A
storage intersection removes incompatible alternatives, rather than changing
only the outer runtime-kind mask while retaining contradictory payloads.

```sh
dotnet run --project tests/HirFlow/Msync.HirFlow.Tests.csproj -c Release \
  --artifacts-path .temp/hir-flow-build
```

The graph is an analysis graph of stable logical blocks and locations. Its
primitive transfer operations are not executable VM operations; there are no
stack slots, branch byte offsets or calls back into syntax binding. A worklist
revisits only blocks whose incoming facts change. Once the fixed point is
available, one linear transfer replay records node input facts for typed-body
construction. That replay does not rebind or republish a function body.

The flow graph and edge maps are temporary. Finalized HIR retains the selected
types, captures, structured control flow and effects required by lowering, not
the analysis workspace. Structural type/shape projection is supplied by the
semantic constraint layer; this scalar lattice test is not a substitute for
the complete compiler and editor user journeys.
