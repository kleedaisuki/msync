# Executable edges and predicates

Run `dotnet run --project tests/PredicateOptimization` from the repository root.
The executable compares the real interpreter before/after the predicate pass,
serializes relocatable objects, binds executable artifacts, and changes actual
filesystem state to select both arms of otherwise unknown predicates. Cases
cover dominating Bool/null/type tests, infeasible predecessors, assignments,
captured writes invalidating earlier facts, and loop-carried values.
Representation-sensitive checks compare exact Number bits, preserve both signed
zeros through a feasible-predecessor join and numerical equality, and retain a
NaN payload through an inequality-true edge while NaN equality selects its false
edge. Text equality must not replace a Path
with a String or discard its endpoint/context provenance.

The test invokes the internal pass directly so its own evidence does not depend
on dead-code elimination or inlining. No external effect is evaluated by the pass.

`dotnet run --project tests/PredicateOptimization -c Release -- --performance`
reports nine-sample median component time and thread allocation for 100, 500 and
2,000 straight-line bindings and reads, after three warmups. It measures this
pass, not CLI startup or I/O throughput, and imposes no timing quality gate.

## Analysis contract

Only executable branch edges contribute to the constant/type meet. Edge facts
refer to stable value definitions rather than names; writes replace the payload
version. Bool/null/type/equality assumptions refine the selected successor, and
joins discard incompatible facts. Branch rewriting consumes the original Bool;
folding a computed result consumes its original operands, leaving effectful
producers and selected diagnostics in place for the graph cleanup pass.

A separate stack-taint worklist discovers raw cell aliases through capture
loads, local initialization, copies and CFG joins. Stores through any alias,
modifying calls and deferred callbacks invalidate the affected storage facts.
Ordinary filesystem reads and scalar transfers cannot mutate language cells;
the optimizer neither executes them nor discards them as compile-time facts.
