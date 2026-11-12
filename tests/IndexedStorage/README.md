# Typed indexed storage and alias facts

This focused program uses the production compiler, artifact codec, contextual
binder and interpreter. Run it with:

```sh
dotnet run --project tests/IndexedStorage -c Release
```

The cases distinguish independent vector elements, repeated reads of the same
object, external aliases and nested owners. They also cover callable elements
and field replacement, immutable snapshot membership, actual Path/String store
conversion and fixed-schema rejection. A typed-empty slot arena must recover
the live vector owned by an inserted row rather than the type witness's
hypothetical field. Nullable member refinements survive an unrelated branch
but are invalidated by a potentially aliasing write, including a write through
a function. Refining one selected element never refines another one.

A concept checked through a dynamically selected aggregate retains its callable
contract when the underlying field is subsequently assigned through an original
object alias. Ordinary callable value-flow links are not treated as memory alias
links. Cases execute source, serialized relocatable bytecode and serialized
executable bytecode at O0/O2/O3; rejects are checked at the same levels.

The compiler ownership and proof model is recorded in
[the indexed-storage design note](../../docs/design/indexed-storage-v080.md).

Runtime carrier checks additionally consume actual production NativeRegistry
and source/object/executable results, rather than comparing only inferred
types. They cover insertion/removal, retained storage aliases across actual
REPL submissions, independent Range snapshots, moving membership out of the
same vector, ordinary Range equality and stable mutable-owner identity. A
vector declares its element carrier at construction and does not become a
Range. An ordinary Range and an untyped empty Range cannot serve as mutable
storage or silently change a constructed vector's carrier.
