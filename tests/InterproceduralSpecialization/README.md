# Direct-call value specialization

`dotnet run --project tests/InterproceduralSpecialization` exercises the isolated
module pass and the actual level-2/3 product pipeline through executable runtime
outcomes, actual file bytes, and both binary stages. Every baseline explicitly
uses optimization level zero; interpreter binding must not accidentally
optimize the oracle.

The format-6 Release suite passes 190 contracts.

The pass is intended for levels 2 and 3 after graph cleanup. In particular,
effectful functions cannot be replaced by ordinary instruction inlining because
their return owns pending jobs. A known control argument can nevertheless select
a smaller callable body while retaining that invocation boundary.

The call-site analysis proves the actual callee is a nonnative closure, follows
its current lexical value and ordinary function-dispatch mapping, and then uses
the explicit specialization target. `Call.B` alone is not such a proof:
ordinary calls can dispatch a native value, while a selected direct source
specialization on a native value fails without calling the unrelated source
body. Captured cells and mutable Scope members invalidate their value facts;
values already evaluated on the operand stack retain ordinary value semantics.

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
input layout, an unused but selected failing cast with its original source span,
capture layout, parameter mutation, native dispatch precedence, mutable member
identity, escaping range callbacks, signed zero and both IEEE NaN branch
selection and payload retention, and many distinct control values under repeated
optimization. The integrated residual assertions also require removal of the
unescaped original callable body and its unselected branch constant. A local
`SpecializeClosure` changes only the consumed callee value, retaining actual
capture locations and leaving escaping bindings' callable identity unchanged.

## CFG callable discovery and inlining

Run the focused solver, rewrite, and binary-route tests independently:

```sh
dotnet run -c Release --project tests/InterproceduralSpecialization -- --call-target-facts
```

The CFG solver meets an exact source callable identity and capture count on
each incoming edge. Distinct closure objects with that same code identity can
share a target proof; inlining extracts the actual selected capture locations.
Differing target IDs, raw capture aliases, and open session calls are not made
direct. Iterator exhaustion and yield have separate stack transfers. Loop
backedges can invalidate an initially exact target before a result is exposed.

The optimizer owns a `CallTargetGraph` per module revision. Effect and memory
summaries, aggregate specialization and inlining reuse discovery when they
read the same revision. Inlining checks cheap policy gates before computing
eligibility, at most once per callee in a wave. SCC membership uses one
shared iterative graph algorithm; it is not inferred from backedges on an
active DFS path. Format-6 fixtures declare value versus location captures and
the reader rejects a direct specialization with an incompatible capture
prefix, or a raw location passed as an ordinary argument. Valid call tests
separately pass a value read from a Cell, rebind through a captured location,
and hold an already evaluated callee across an argument's alias mutation.

The lattice and worklist follow the separation in
[LLVM CalledValuePropagation](https://llvm.org/docs/doxygen/CalledValuePropagation_8cpp_source.html).
The version-scoped result likewise separates discovery, effects and policy
decisions rather than treating a call hint as proof, as in
[GCC IPA summary propagation](https://gcc.gnu.org/onlinedocs/gccint/Regular-IPA-passes.html).
This pass does not import LLVM/GCC IR, merge callable identities, or infer a
closed capture graph for every function-valued Scope member.

### Profiled scalar call loop

`cfg-call-loop.msync` selects the same lambda code with one of two different
capture Cells at runtime. It checks both binary64 recurrences through source,
portable and bound execution. The old O2 bytecode retained a call inside the
loop; the changed O2 bytecode has none. For 262,144 iterations with EOF after
the count, the same Native AOT runner's opt-in SQLite profiles report source
frames **262,146 → 1** (including entry), dispatched bytecodes
**4,980,775 → 5,242,922**, and operand-stack Number boxes **524,288 → 524,288**.
Capture projection remains inside the loop; the cost reduction comes from
replacing the frame boundary, not from executing fewer opcode dispatches.

Separately, eight fresh-process AB/BA pairs per selected capture, with telemetry
off and result bits checked, used the same stock Native AOT runner for the two
pre-bound artifacts. EOF selected capture: median 445 → 399 ms, 5/8 pairs
faster. Second input selected capture: 342 → 286 ms, 7/8 faster. These are
descriptive timings on a shared Linux host. An earlier inline-only variant
with repeated raw-argument normalization and an extra closure temporary
showed a mixed time result despite removing all calls. Exact raw observations
are under `.temp/ipa-cfg-isolated/`.
