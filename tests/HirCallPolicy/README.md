# Call evaluation policy journeys

These journeys exercise the public semantic frontend and real interpreter. They
include a persistent REPL session and serialize/read the finalized HIR before
lowering source fixtures, so the deferred-call contract cannot depend on a live
binder or an earlier compiler callback.

Covered contracts:

- A source function returning a lazy Range evaluates the whole call, including
  its argument reads, on demand. Later REPL writes are observed.
- Native eager snapshots propagate through source wrappers and remain eager.
- Local aliases and type-refined branches preserve the source return scheme.
- Mutable scalar arguments do not retain obsolete literal proofs in a thunk.
- A writable Range/String identity cannot promise a Range to an enumerator.
- Pure unknown return projections cannot erase actual specialized callable types.
- Standard source/native metadata can determine laziness before module execution.

The return-shape projection reads already bound semantic nodes. It has no
executable-output capability, does not call generic instantiation, and does not
apply a function's effects. Its cache retains canonical type/callable signatures,
not caller object graphs. Actual argument and callee expressions are analyzed
once into either an ordinary call or a whole-call deferred function.

Run with `dotnet run --project tests/HirCallPolicy -c Release`. A frozen assembly
set can be supplied with `-p:FrozenAssemblies=/absolute/path` for incremental
validation without rebuilding the product.
