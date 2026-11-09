# Finite compile-time evaluation

`dotnet run --project tests/CompileTimeEvaluation` checks the exact sequential
evaluator independently and through artifact round trips. Baselines explicitly
use optimization level zero: `Interpreter.ExecuteAsync` otherwise binds and
optimizes relocatable modules before executing them.
`--integrated` additionally requires the production O2/O3 pipeline to eliminate
the same whole known computations, rather than merely exercising the pass API.

The pass evaluates zero-argument, uncaptured functions containing eager finite
range loops. It certifies all backward control edges before interpretation:
each targets a canonical private iterator header, the iterator's only storage
definition is its preheader, and outside edges cannot enter its body. Nested
finite loops may reinitialize inner iterators when the outer iterator advances.
There is no step budget or input quota. A large known loop is evaluated without
expanding its bytecode, unlike the separate code-size-driven unrolling pass.
The distinction between effect freedom and safe speculation follows the
[MLIR side-effect rationale](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/):
absence of an external write alone is not a termination proof.

The evaluator has an intentionally explicit executable vocabulary. It never
calls the runtime registry, authenticates an endpoint, probes files, enumerates
an asynchronous range, launches a process, performs a transfer, or executes a
barrier. Runtime cells and heap Scope values are not compile-time values here.
An unsupported operation or selected scalar error retains the original body;
it does not convert a runtime diagnostic into a compiler diagnostic.

Arithmetic follows the original IEEE-double operation order. No reduction is
reassociated, including loops above the exact-integer range. Literal interning
uses representation equality, preserving signed zero and NaN payloads without
changing language numeric comparison. Unbound local Path operations remain
runtime-dependent; bound executable context or explicit POSIX remote provenance
allows the same pure path operations used by the ordinary optimizer.

The focused checks include a 200-item sum with two residual instructions,
nested loops, selected branches and early return, independent bit-exact random
accumulations, retained I/O/error/unbounded work, and `.mso`/`.mexe` codecs.
The integrated entry currently passes 136 local contracts, including the actual
production O2/O3 pipeline and bound executable path reduction.
