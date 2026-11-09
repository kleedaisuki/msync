# Typed indexed storage and alias facts

This focused program uses the production compiler, artifact codec, contextual
binder and interpreter. Run it with:

```sh
dotnet run --project tests/IndexedStorage -c Release
```

The cases distinguish independent buffer elements, repeated reads of the same
object, external aliases and nested objects. They also cover callable elements
and field replacement, immutable snapshot membership, actual Path/String store
conversion, first-write type inference and fixed-schema rejection. Nullable
member refinements survive an unrelated control-flow branch but are invalidated
by a potentially aliasing write, including a write through a function. Refining
one dynamically selected element never refines another selected element.

A concept checked through a dynamically selected aggregate retains its callable
contract when the underlying field is subsequently assigned through an original
object alias. Ordinary callable value-flow links are not treated as memory alias
links. Cases execute source, serialized relocatable bytecode and serialized
executable bytecode at O0/O2/O3; rejects are checked at the same levels.

The compiler ownership and proof model is recorded in
[the indexed-storage design note](../../docs/design/indexed-storage-v080.md).

The final focused managed run passed 191 assertions with no build warnings.
It includes a present-Null ADT stored in a Buffer: frozen associated-type proofs
survive dynamic access, while incompatible required-payload writes remain errors.

Runtime carrier checks additionally consume actual production NativeRegistry and
source/object/executable results, rather than comparing only inferred types.
They cover first-write insertion/push, retained Range aliases across actual REPL
submissions, before/after snapshots, fixed carriers after clear, ordinary
Range equality and hashed-key stability. The `ItemType` getter remains a field
load; the immutable-origin protocol is confined to equality and hashing.

The owned-Range Buffer representation passed the same 191 focused assertions
against the frozen GCC/indexed-memory candidate assemblies. This incremental
run rebuilt only the test entrypoint; it did not rebuild the product or repeat
the unrelated ordered-container property matrices. Introspection observes the
Buffer value directly rather than an implementation-only `items` Scope field.
