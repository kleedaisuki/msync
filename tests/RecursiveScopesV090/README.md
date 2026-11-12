# Recursive schemas and summary contracts

`dotnet run --project tests/RecursiveScopesV090 -c Release` checks recursive
schema identity and compiler constraints. `--shapes` exercises source and
source-free HIR through O0/O2 native artifacts; pass a journey name to select it.
These are production-pipeline checks, not the mathematical model below.

```console
python3 tests/RecursiveScopesV090/static_observations.py PATH_TO_NATIVE_AOT_CLI
python3 tests/RecursiveScopesV090/summary_model.py
```

The first command checks 18 real source/object/detached-bound routes. The same
runtime Null has different static `decltype` observations under precise versus
broader input facts; the broader program also keeps its static observation when
the runtime branch is non-null. This prevents a recursive-summary optimization
from changing compile-time observations merely because the widened body remains
well typed. Bound runs remove source/object inputs and use another directory.

The second command is a small executable mathematical witness: 8,748 append/frame
cases, alias/capture counterexamples and a finite footprint fixed point. It is
**not** a language interpreter, proof of arbitrary source footprints, BTree
acceptance or evidence that the compiler implements the proposed summary model.
See [the ownership/summary proposal](../../docs/design/semantic-summary-ownership-v090.md).

Full acceptance keeps the original 745-mutation BTree source, its 180-second
deadline and independent tree/output oracles. A smaller witness never replaces it.
