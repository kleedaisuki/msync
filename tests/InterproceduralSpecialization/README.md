# Direct-call value specialization

`dotnet run --project tests/InterproceduralSpecialization` exercises the isolated
module pass and the actual level-2/3 product pipeline through executable runtime
outcomes, actual file bytes, and both binary stages. Every baseline explicitly
uses optimization level zero; interpreter binding must not accidentally
optimize the oracle.

The pass is intended for levels 2 and 3 after graph cleanup. In particular,
effectful functions cannot be replaced by ordinary instruction inlining because
their return owns pending jobs. A known control argument can nevertheless select
a smaller callable body while retaining that invocation boundary.

The call-site analysis proves the actual callee is a nonnative closure, follows
its current lexical value and ordinary function-dispatch mapping, and then uses
the explicit specialization target. `Call.B` alone is not such a proof: a native
callee retains native dispatch even when that field names another function.
Captured cells and mutable Scope members invalidate their value facts; values
already evaluated on the operand stack retain ordinary value semantics.

Only parameters with no writes, raw storage references, or captures can receive
literal substitution. Their concrete storage type must match the literal type;
an old `Any`-typed body can otherwise move a selected dynamic type error into
artifact validation. A control-dependence scan selects useful constant inputs.
Wholly unused parameters and substituted inputs disappear from the cloned
signature; caller-side pops and temporary values preserve the original
left-to-right argument evaluation, including writes and failures. Capture slots
retain their ABI prefix. Original functions remain available to escaping
closures and callbacks rather than changing their public arity.

Clone keys use exact constant representations, including signed-zero bits.
Repeated equal control tuples reuse a clone. Aggregate code growth is charged
against one original body at level 2 and two bodies at level 3; generated names
carry accounting provenance across repeated optimization calls. Names never
prove callable identity or permit semantic reuse. This is an optimization
profitability policy, not a source-language limit.

The tests cover a transfer-producing argument that is removed from the callee's
input layout, a selected argument failure, capture layout, parameter mutation,
native dispatch precedence, mutable member identity, escaping range callbacks,
signed zero/NaN payloads, and many distinct control values under repeated
optimization. The integrated residual assertions also require removal of the
unescaped original callable body and its unselected branch constant. A local
`SpecializeClosure` changes only the consumed callee value, retaining actual
capture locations and leaving escaping bindings' callable identity unchanged.
