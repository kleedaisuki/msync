# Cross-CFG Scope storage

`dotnet run --project tests/CrossBlockMemory` compares actual O0 execution with
the field transformation, O2/O3 graph cleanup, serialized `.mso` and bound `.mexe`.
Both real filesystem predicate edges are selected. The byte oracle also copies
branch-selected UTF-8 field contents through a loop-carried destination.

The first transformation promotes private Scope instances allocated outside a
cyclic CFG component. Each field has an invocation-local scalar slot; writes on
each predecessor update that same location, providing executable phi semantics
without changing the bytecode format. Existing graph demand then removes dead
field values/stores. Allocation identity, not names or equal contents, owns the
slots. Nested object containment propagates escapes transitively.

An allocation executed inside a cycle can create several simultaneously live
instances. Those sites retain physical objects. Captured/raw field cells,
escaping instances, ambiguous receivers and selected missing/duplicate-member
errors remain physical as well; this is an optimization applicability decision,
never a source-language rejection or an iteration quota.

The design follows the def/use/join separation described by
[LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) and the distinct alias and
clobber queries described by [GCC alias analysis](https://gcc.gnu.org/onlinedocs/gccint/Alias-analysis.html).
It does not claim that a fixed field schema implies a statically bounded runtime
instance count.
