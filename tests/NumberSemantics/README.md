# Number and discrete sequence contracts

```text
dotnet run --project tests/NumberSemantics
```

This is a focused language/runtime journey: expressions compile at O0/O2/O3 and
execute directly, after `.mso` serialization, and after contextual `.mexe`
lowering/serialization. Expected binary64 values are checked bitwise except
NaN, including preservation of negative zero.

The cases cover decimal/base/scientific literals, adjacent operators, numeric
precedence and power, IEEE comparisons, scalar conversions, classification and
constants, and lazy counts beyond Int32. Generated finite sequences compare
`take`/`drop`/`stride` against independent LINQ models. Invalid count checks call
the native operation directly, rather than depending on a parser failure to
stand in for runtime-domain validation. Large lazy inputs are never exhausted.

The final managed snapshot 4 passed 601 contracts. Its test-only build used
`-p:BuildProjectReferences=false --no-restore` against the shared product build;
both the build and execution completed without warnings or errors. An actual
CLI transfer of `(-value) as String`, with `value = 2`, wrote `-2`, not the
formerly misclassified Path spelling `-value`.
