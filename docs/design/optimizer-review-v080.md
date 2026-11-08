# Independent staged optimizer review (0.8)

## Review scope

The November 9, 2026 review examined the mutable-location optimizer working tree
before its coherent Core/Compiler/Runtime integration commit. The assessment
focused on observable equivalence and compilation cost, not pass-name coverage.
The [GCC mapping](gcc-optimization-map-v080.md) owns the pass coverage audit and
[optimizer graph note](optimizer-graph-v080.md) owns implementation decisions.

The interpreter binds relocatable modules automatically. Differential probes
must set `OptimizationLevel: 0` on the actual control module, or call
`Optimizer.Optimize(module, 0)`, rather than assume that an unoptimized-looking
instruction array is an unoptimized execution oracle.

## Constant identity must preserve signed zero

A valid Number-returning module consisting of `Constant(0), Negate, Return`
returned negative zero at O0 and positive zero at O1/O2/O3. Both the returned
number's formatted text (`-0` versus `0`) and its use as a process argument are
observable. The optimizer's constant table used `NumberValue` record equality,
which initially inherited `double.Equals`; that equality identifies the two zero
encodings. Constant propagation joins and value numbering have the same
requirement as constant insertion: do not identify bit-distinct IEEE values
merely because the language's numeric comparison treats them as equal.

A direct discriminator is:

```csharp
Module program = new([new NumberValue(0)],
    [new("main", 0, 0,
        [new(OpCode.Constant, 0), new(OpCode.Negate), new(OpCode.Return)],
        TypeRef.Number)], OptimizationLevel: 0);
```

Compare `BitConverter.DoubleToInt64Bits` after actual execution at each level,
including serialized `.mso` and bound `.mexe` routes. The initial result was
`8000000000000000` at O0 and `0000000000000000` at optimized levels.

The final working-tree correction uses `ValueRepresentation` for bit-exact
constant identity while preserving `NumberValue` runtime equality. Rerunning
the discriminator retains `8000000000000000` at all four levels. This distinction
also preserves `ranges.contains`/`find` Value equality and `ranges.unique`
`HashSet<Value>` behavior: their existing numeric relation identifies the zero
encodings, as does the language's numeric comparison.

Predicate inference needs the same separation: successful `x == 0` establishes
numerical equality, not an exact zero encoding. `PredicateOptimization` now
retains that distinction for zero and NaN, rather than replacing the tested
value with the comparison constant. A fresh actual-source check of
`fn keep(x) { if x == 0 { x } else { 1 } }; keep(- 0)` returns negative zero at
O0/O1/O2/O3; `$std::ranges::contains([0], - 0)` returns true at all levels.
The space in `- 0` deliberately selects unary negation: the token `-0` is parsed
as a signed integer literal and therefore does not distinguish zero encodings.
Constant identity and language equality remain different relations; future
regression coverage must establish both independently.

## Inline expansion must interleave simplification

An acyclic direct-call DAG exposes a real artifact workload without requiring
unbounded source recursion:

```text
f0(v) = v + 1
fi(v) = f(i-1)(v) + f(i-1)(v)
main  = fd(2)
```

Construct each call with an explicit `MakeClosure` and matching monomorphic
`Call.B` target. This distinction matters: a source-level function capturing
another callable currently takes a different discovery path and does not
necessarily expose the same direct-call graph.

The initial O3 policy used an unlimited multi-use body threshold and ran the
inliner's own fixed point before returning to graph simplification. Thus an
exponential transient instruction tree was created even though the complete
entry result was constant. A bounded managed probe observed:

| DAG depth | O2 | Initial O3 | Initial O3 residual |
| --- | ---: | ---: | --- |
| 4 | 11 ms | 8 ms | 2 instructions, 31 locals |
| 8 | 8 ms | 603 ms | 2 instructions, 511 locals |
| 12 | 16 ms | More than 20 seconds; probe interrupted | Not obtained |

These are discriminating single-run observations in a shared development
workspace, not accepted production latency statistics.

The subsequent working-tree change performs one expansion wave per outer
cleanup iteration and applies a finite multi-use code-size heuristic at O3.
A rerun at depths 4/8/12/16 completed O3 in 7/43/60/65 ms respectively, with
2/58/90/122 instructions and 31/263/267/271 locals. This removes the demonstrated
exponential mechanism. Formal latency comparisons and parameter tuning belong
to the separate alternating native workload measurements, not these observations.

Keep an artifact-level DAG discriminator in optimizer performance checks.
Code-size heuristics must remain optimization choices, never source-language
validity limits. Interleave simplification so transient graph size, reaching
state size and retained local slots do not erase the benefit of a tiny final IR.

## Selected Scope construction failures

A final accepted-artifact discriminator revealed another concrete elimination
error: `True, False, MakeScope(A=2, Text="x\0x"), Pop, Null, Return` passes
`Artifact.Validate`, throws during selected construction at O0, and was removed
by O1/O2/O3 to `Null, Return`. The duplicate-name case is not produced by the new
source frontend, but accepting an older binary then optimizing away its selected
failure changes the binary execution contract.

The correction belongs in operation totality proofs: ordinary unique-member
Scope construction is removable; duplicate-member construction can fail. Its
exceptional behavior must reach graph roots and interprocedural summaries.
Marking every Scope allocation effectful would discard the ordinary optimization
benefit; globally rejecting accepted objects would also change unselected-branch
behavior instead of preserving selected execution semantics.

A separate mutable-identity discriminator passed at O0/O1/O2/O3: construct a
Scope with `field`, capture its binding location in setter/getter closures,
rebind it to a Scope with only `other`, and discard the delayed getter's result.
All variants retain the selected `Scope has no member 'field'` error, including
optimized paths which inline both closures through `GetCapture` and fresh
`InitializeLocal` locations. This check did not establish a defect in the current
`HasDeclaredMember`/mutable-origin exclusion mechanism.
