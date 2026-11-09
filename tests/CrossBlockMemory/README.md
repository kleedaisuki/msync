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

## Physical instances and precise call effects

Escaped instances remain real Scope values. For eligible fields, invocation-local
shadow slots carry the same value across blocks. The must-valid field set is
intersected at CFG joins; an overlapping write kills a version, and an explicit
field store refreshes both the physical field and its shadow location. Constructor
staging preserves argument evaluation and raw-cell fields stay physical.

The four-argument optimizer entry consumes one module-wide `MemoryEffects`
analysis. Known parameter and capture field paths invalidate only overlapping
fields, while captured binding rebinding is separate from mutation of the old
object. Readonly helpers and filesystem observations do not overwrite Scope
fields. Unknown source callbacks invalidate escaped fields when invoked or
enumerated; creating a lazy adapter is not that invocation. Native and closure
dispatch use the actual reachable callable, never an unproved direct-call hint.

Return-alias summaries are may-alias facts: the actual function return type's
Null/scalar alternatives remain in the abstract value. Missing-field and raw
captured-cell discriminators check that neither a possible error nor a shared
location is replaced with an unconditional cached scalar.

Physical stores are retained; deleting a physical write across a potentially
failing call would change the externally observable failure prefix. Private
stores become ordinary local definitions and the existing graph DSE removes
overwritten versions with the normal observable-effect rules.
